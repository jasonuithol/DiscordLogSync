using BepInEx;
using BepInEx.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DiscordLogSync
{
    /// <summary>
    /// Plugs into BepInEx's log pipeline.
    /// Every log line is immediately written + flushed to a local buffer file.
    /// A background timer periodically reads the buffer and POSTs it to Discord.
    /// On startup, any leftover buffer (from a previous crash) is sent first.
    /// On clean shutdown, a final flush is attempted.
    /// </summary>
    public class DiscordLogListener : ILogListener, IDisposable
    {
        // ── Paths ──────────────────────────────────────────────────────────────
        private static readonly string BufferPath =
            Path.Combine(Paths.BepInExRootPath, "DiscordLogBuffer.txt");

        // ── State ──────────────────────────────────────────────────────────────
        private readonly object    _fileLock  = new object();
        private StreamWriter       _writer;
        private readonly HttpClient _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly Timer     _sendTimer;
        private volatile bool      _disposed;

        // Prevent overlapping send attempts
        private int _sending = 0;

        // ── Constructor ────────────────────────────────────────────────────────
        public DiscordLogListener()
        {
            // ① If there's a buffer left over from a crash, send it before anything else
            RecoverAndSendLeftoverBuffer();

            // ② Open (or create) the buffer file for append, flushed on every write
            _writer = OpenWriter(FileMode.Create);

            // ③ Start the background send timer
            int intervalMs = Math.Max(2, Plugin.SendIntervalSeconds.Value) * 1000;
            _sendTimer = new Timer(OnTimerTick, null, intervalMs, intervalMs);
        }

        // ── ILogListener ───────────────────────────────────────────────────────

        public void LogEvent(object sender, LogEventArgs e)
        {
            if (_disposed) return;

            // Format: [2026-03-27 14:32:01.456] [Info] [PluginName] Message text
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line      = $"[{timestamp}] [{e.Level,-7}] [{e.Source?.SourceName ?? "?"}] {e.Data}";

            lock (_fileLock)
            {
                try
                {
                    // AutoFlush is true, so this write hits disk immediately.
                    // A hard kill after this point loses at most the current line.
                    _writer?.WriteLine(line);
                }
                catch
                {
                    // Never let a logging error propagate into the game.
                }
            }
        }

        // ── Timer callback ─────────────────────────────────────────────────────

        private void OnTimerTick(object _)
        {
            if (_disposed) return;

            // If a previous send is still in-flight, skip this tick
            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

            try   { TrySendBuffer("📋 Valheim Log"); }
            finally { Interlocked.Exchange(ref _sending, 0); }
        }

        // ── Send logic ─────────────────────────────────────────────────────────

        /// <summary>
        /// Read the buffer file, POST it to Discord, then truncate on success.
        /// On any failure the buffer is left intact for the next tick.
        /// </summary>
        private void TrySendBuffer(string title)
        {
            // Step 1: flush and snapshot the buffer content (brief lock)
            string content;
            lock (_fileLock)
            {
                try { _writer?.Flush(); }
                catch { return; }
            }

            // Step 2: read outside the lock — writer uses FileShare.Read
            try
            {
                using var fs     = new FileStream(BufferPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                content          = reader.ReadToEnd();
            }
            catch { return; }

            if (string.IsNullOrWhiteSpace(content)) return;

            // Step 3: POST to Discord
            bool sent = false;
            try
            {
                PostToDiscord(title, content, isRecovery: false).GetAwaiter().GetResult();
                sent = true;
            }
            catch
            {
                // Leave buffer intact; retry next tick
            }

            // Step 4: on success, truncate the buffer
            if (sent)
            {
                lock (_fileLock)
                {
                    try
                    {
                        _writer?.Dispose();
                        _writer = OpenWriter(FileMode.Create);
                    }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Called once on startup. If a buffer file already exists, it means
        /// the previous run was killed without a clean shutdown.
        /// </summary>
        private void RecoverAndSendLeftoverBuffer()
        {
            if (!File.Exists(BufferPath)) return;

            string content;
            try { content = File.ReadAllText(BufferPath, Encoding.UTF8); }
            catch { return; }

            if (string.IsNullOrWhiteSpace(content))
            {
                TryDeleteBuffer();
                return;
            }

            // Send synchronously — we want this done before normal logging starts
            try
            {
                PostToDiscord("⚠️ Recovered — previous session ended unexpectedly", content, isRecovery: true)
                    .GetAwaiter().GetResult();
            }
            catch { /* if this fails we still continue; recovered lines stay in buffer */ }

            TryDeleteBuffer();
        }

        // ── Discord HTTP ───────────────────────────────────────────────────────

        private System.Threading.Tasks.Task PostToDiscord(string title, string content, bool isRecovery)
        {
            // Reserve 20 chars for the header line + code fence markers (``` ... ```)
            int maxChars = Math.Clamp(Plugin.MaxMessageChars.Value, 100, 1900);

            // If the buffer is larger than the limit, keep the NEWEST lines
            // (oldest are already on Discord from the previous send)
            string body = content;

            // remove empty lines.
            string body = System.Text.RegularExpressions.Regex.Replace(content, @"\n\s*\n", "\n");

            if (body.Length > maxChars)
                body = "(oldest lines omitted)\n" + body.Substring(body.Length - maxChars);

            // Content field = full width in Discord. Code block = monospace, preserves spacing.
            // Header line provides the title/context that embeds used to give us.
//            string message = $"**{title}**\n```\n{body.TrimEnd()}\n```";
            string message = $"**{title}**\n{body.TrimEnd()}";

            string json = "{\"content\":" + JsonString(message) + "}";

            return PostJson(Plugin.WebhookUrl.Value, json);
        }

        private async System.Threading.Tasks.Task PostJson(string url, string json)
        {
            var payload  = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, payload).ConfigureAwait(false);

            if ((int)response.StatusCode == 429)
                throw new Exception("Discord rate-limited (429)");

            response.EnsureSuccessStatusCode();
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static StreamWriter OpenWriter(FileMode mode)
        {
            var fs = new FileStream(BufferPath, mode, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
        }

        private static void TryDeleteBuffer()
        {
            try { File.Delete(BufferPath); } catch { }
        }

        private static bool ContainsCaseInsensitive(string haystack, string needle) =>
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Serialize a C# string as a JSON string literal (with quotes).</summary>
        private static string JsonString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r':                    break;  // strip CR
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:x4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        // ── IDisposable ────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _sendTimer?.Dispose();

            // Final flush on clean shutdown
            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
            {
                try   { TrySendBuffer("🛑 Server Shutdown"); }
                catch { }
            }

            lock (_fileLock)
            {
                _writer?.Dispose();
                _writer = null;
            }

            _http?.Dispose();
        }
    }
}
