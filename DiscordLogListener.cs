using BepInEx;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DiscordLogSync
{
    /// <summary>
    /// Plugs into BepInEx's log pipeline.
    /// Every log line is immediately written + flushed to a local buffer file.
    /// A background timer periodically reads the OLDEST lines from the buffer and
    /// POSTs them to Discord. On success, only the sent lines are removed — new lines
    /// keep accumulating at the end. If the buffer is larger than one message, it will
    /// catch up over subsequent ticks. Nothing is ever dropped.
    /// On startup, any leftover buffer (from a previous crash) is sent first.
    /// On clean shutdown, a final flush is attempted.
    /// </summary>
    public class DiscordLogListener : ILogListener, IDisposable
    {
        private readonly string _serverName;
        private readonly string _webhookUrl;

        // ── Paths ──────────────────────────────────────────────────────────────
        private static readonly string BufferPath =
            Path.Combine(Paths.BepInExRootPath, "DiscordLogBuffer.txt");

        // ── State ──────────────────────────────────────────────────────────────
        private readonly object     _fileLock = new object();
        private StreamWriter        _writer;
        private readonly HttpClient _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly Timer      _sendTimer;
        private volatile bool       _disposed;

        // Prevent overlapping send attempts
        private int _sending = 0;

        // ── Constructor ────────────────────────────────────────────────────────
        public DiscordLogListener(string serverName, string webhookUrl)
        {
            _serverName = serverName;
            _webhookUrl = webhookUrl;

            // ① Send any leftover buffer from a previous crash before normal logging begins.
            //    Opens/closes the file directly (no _writer yet).
            RecoverAndSendLeftoverBuffer();

            // ② Open buffer in Append mode — preserves any lines not yet sent from recovery.
            _writer = OpenWriter(FileMode.Append);

            // ③ Start the background send timer.
            int intervalMs = Math.Max(2, Plugin.SendIntervalSeconds.Value) * 1000;
            _sendTimer = new Timer(OnTimerTick, null, intervalMs, intervalMs);
        }

        // ── ILogListener ───────────────────────────────────────────────────────

        public void LogEvent(object sender, LogEventArgs e)
        {
            string line = (e.Data?.ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(line)) return;
            WriteToBuffer($"[{e.Source.SourceName}] {e.Level} {line}");
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Write a pre-formatted line to the buffer. Blank lines are silently dropped.</summary>
        public void WriteToBuffer(string line)
        {
            if (_disposed) return;

            string trimmed = (line ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return;

            lock (_fileLock)
            {
                try
                {
                    // AutoFlush = true, so this hits disk immediately.
                    // A hard kill after this write loses at most the current line.
                    _writer?.WriteLine(trimmed);
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

            // Skip if a previous send is still in-flight
            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

            try   { TrySendBuffer($"📋 [{_serverName}] {DateTime.Now:yyyy-MM-dd HH:mm:ss}"); }
            finally { Interlocked.Exchange(ref _sending, 0); }
        }

        // ── Send logic ─────────────────────────────────────────────────────────

        /// <summary>
        /// Reads the OLDEST lines from the buffer that fit within the Discord message
        /// limit, POSTs them, and on success rewrites the buffer without those lines.
        /// New lines accumulate at the end undisturbed. If there is more in the buffer
        /// than fits in one message, the next tick will send the next chunk.
        /// </summary>
        private void TrySendBuffer(string title)
        {
            // Flush writer so we see latest content (brief lock)
            lock (_fileLock)
            {
                try { _writer?.Flush(); }
                catch { return; }
            }

            // Read current buffer outside the lock
            string allContent;
            try
            {
                using var fs     = new FileStream(BufferPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                allContent       = reader.ReadToEnd();
            }
            catch { return; }

            if (string.IsNullOrWhiteSpace(allContent)) return;

            // Build the oldest-first chunk that fits within the Discord limit.
            // Blank lines are skipped for display but still counted for trimming.
            int maxChars = Math.Clamp(Plugin.MaxMessageChars.Value, 100, 1900);
            string[] allLines = allContent.Split('\n');

            var toDisplay     = new List<string>();
            int displayChars  = 0;
            int linesConsumed = 0;  // how many lines (including blanks) to remove on success

            foreach (string rawLine in allLines)
            {
                string line = rawLine.TrimEnd('\r');

                if (string.IsNullOrWhiteSpace(line))
                {
                    linesConsumed++;
                    continue;
                }

                // Stop if adding this line would exceed the limit
                if (displayChars + line.Length + 1 > maxChars) break;

                toDisplay.Add(line);
                displayChars += line.Length + 1;
                linesConsumed++;
            }

            if (toDisplay.Count == 0) return;

            string body = string.Join("\n", toDisplay);

            // POST
            bool sent = false;
            try
            {
                PostToDiscord(title, body).GetAwaiter().GetResult();
                sent = true;
            }
            catch
            {
                // Leave buffer intact; retry next tick
            }

            if (!sent) return;

            // Remove exactly the lines we sent from the top of the buffer.
            // Hold the lock for the rewrite so LogEvent writes block briefly (< 1ms)
            // rather than writing to a file mid-rewrite.
            lock (_fileLock)
            {
                try
                {
                    _writer?.Dispose();
                    _writer = null;

                    // Re-read: new lines may have been appended while we were sending
                    string currentContent;
                    using (var fs     = new FileStream(BufferPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(fs, Encoding.UTF8))
                        currentContent = reader.ReadToEnd();

                    // Drop the first linesConsumed lines and rewrite
                    string[] currentLines = currentContent.Split('\n');
                    string   remaining    = string.Join("\n", currentLines.Skip(linesConsumed));

                    File.WriteAllText(BufferPath, remaining, Encoding.UTF8);

                    // Reopen in append mode to continue accumulating
                    _writer = OpenWriter(FileMode.Append);
                }
                catch
                {
                    // Best effort — try to at least reopen the writer
                    try { _writer = OpenWriter(FileMode.Append); } catch { }
                }
            }
        }

        /// <summary>
        /// Called once at startup before _writer is opened.
        /// Sends the first chunk of any leftover buffer as a crash-recovery message,
        /// trims those lines, and leaves the remainder for the normal timer to handle.
        /// </summary>
        private void RecoverAndSendLeftoverBuffer()
        {
            if (!File.Exists(BufferPath)) return;

            string allContent;
            try { allContent = File.ReadAllText(BufferPath, Encoding.UTF8); }
            catch { return; }

            if (string.IsNullOrWhiteSpace(allContent))
            {
                TryDeleteBuffer();
                return;
            }

            int maxChars = Math.Clamp(Plugin.MaxMessageChars.Value, 100, 1900);
            string[] allLines = allContent.Split('\n');

            var toDisplay     = new List<string>();
            int displayChars  = 0;
            int linesConsumed = 0;

            foreach (string rawLine in allLines)
            {
                string line = rawLine.TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line)) { linesConsumed++; continue; }
                if (displayChars + line.Length + 1 > maxChars) break;
                toDisplay.Add(line);
                displayChars += line.Length + 1;
                linesConsumed++;
            }

            if (toDisplay.Count == 0) { TryDeleteBuffer(); return; }

            string body = string.Join("\n", toDisplay);
            bool sent = false;
            try
            {
                PostToDiscord($"⚠️ [{_serverName}] Recovered — previous session ended unexpectedly — {DateTime.Now:yyyy-MM-dd HH:mm:ss}", body)
                    .GetAwaiter().GetResult();
                sent = true;
            }
            catch { }

            if (!sent) return; // leave buffer intact; will retry on next startup

            // Trim sent lines, leave remainder for normal timer
            string remaining = string.Join("\n", allLines.Skip(linesConsumed));
            if (string.IsNullOrWhiteSpace(remaining))
                TryDeleteBuffer();
            else
                File.WriteAllText(BufferPath, remaining, Encoding.UTF8);
        }

        // ── Discord HTTP ───────────────────────────────────────────────────────

        private System.Threading.Tasks.Task PostToDiscord(string title, string body)
        {
            // Strip consecutive blank lines
            string cleanBody = Regex.Replace(body, @"\n\s*\n", "\n");
            string message   = $"**{title}**\n{cleanBody.TrimEnd()}";
            string json      = "{\"content\":" + JsonString(message) + "}";
            return PostJson(_webhookUrl, json);
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

            // Final flush on clean shutdown — sends whatever is in the buffer right now.
            // Any remainder beyond one message stays in the buffer for recovery on next start.
            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
            {
                try   { TrySendBuffer($"🛑 [{_serverName}] Server Shutdown — {DateTime.Now:yyyy-MM-dd HH:mm:ss}"); }
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
