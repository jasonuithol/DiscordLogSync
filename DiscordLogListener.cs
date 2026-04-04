using BepInEx;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace DiscordLogSync
{
    /// <summary>
    /// One instance per source. Owns a buffer file, a send timer, and a webhook URL.
    /// Lines are fed in via WriteToBuffer(). Every write is flushed to disk immediately.
    /// A background timer sends the oldest buffered lines to Discord, trimming exactly
    /// those bytes on success. Nothing is ever dropped.
    /// </summary>
    public class DiscordLogListener : IDisposable
    {
        // ── State ──────────────────────────────────────────────────────────────
        private readonly string     _bufferPath;
        private readonly string     _webhookUrl;
        private readonly string     _serverName;
        private readonly string     _sourceName;
        private readonly object     _fileLock  = new object();
        private StreamWriter        _writer;
        private readonly HttpClient _http      = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        private readonly Timer      _sendTimer;
        private volatile bool       _disposed;
        private int                 _sending   = 0;

        // ── Constructor ────────────────────────────────────────────────────────
        public DiscordLogListener(string sourceName, string serverName, string webhookUrl)
        {
            _sourceName = sourceName;
            _serverName = serverName;
            _webhookUrl = webhookUrl;
            _bufferPath = Path.Combine(Paths.BepInExRootPath, $"DiscordLogBuffer_{sourceName}.txt");

            RecoverAndSendLeftoverBuffer();
            _writer = OpenWriter(FileMode.Append);

            int intervalMs = Math.Max(2, Plugin.SendIntervalSeconds.Value) * 1000;
            _sendTimer = new Timer(OnTimerTick, null, intervalMs, intervalMs);
        }

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>Write a line to the buffer. Blank lines are dropped here and nowhere else.</summary>
        public void WriteToBuffer(string line)
        {
            if (_disposed) return;

            string trimmed = (line ?? "").Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) return;

            lock (_fileLock)
            {
                try { _writer?.WriteLine(trimmed); }
                catch { }
            }
        }

        // ── Timer callback ─────────────────────────────────────────────────────

        private void OnTimerTick(object _)
        {
            if (_disposed) return;
            if (Interlocked.CompareExchange(ref _sending, 1, 0) != 0) return;

            try   { TrySendBuffer($"📋 [{_sourceName}][{_serverName}] {DateTime.Now:yyyy-MM-dd HH:mm:ss}"); }
            finally { Interlocked.Exchange(ref _sending, 0); }
        }

        // ── Send logic ─────────────────────────────────────────────────────────

        private void TrySendBuffer(string title)
        {
            lock (_fileLock)
            {
                try { _writer?.Flush(); }
                catch { return; }
            }

            string allContent;
            try
            {
                using var fs     = new FileStream(_bufferPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                allContent       = reader.ReadToEnd();
            }
            catch { return; }

            if (string.IsNullOrWhiteSpace(allContent)) return;

            int maxChars     = Math.Clamp(Plugin.MaxMessageChars.Value, 100, 1900);
            var toDisplay    = new List<string>();
            int displayChars = 0;
            int charPos      = 0;
            int searchFrom   = 0;

            while (searchFrom <= allContent.Length)
            {
                int  newline  = allContent.IndexOf('\n', searchFrom);
                bool lastLine = newline == -1;
                int  lineEnd  = lastLine ? allContent.Length : newline;
                int  nextPos  = lastLine ? allContent.Length : newline + 1;

                string line = allContent.Substring(searchFrom, lineEnd - searchFrom).TrimEnd('\r');

                if (displayChars + line.Length + 1 > maxChars) break;

                toDisplay.Add(line);
                displayChars += line.Length + 1;
                charPos       = nextPos;
                searchFrom    = nextPos;
                if (lastLine) break;
            }

            if (toDisplay.Count == 0) return;

            int    consumedBytes = Encoding.UTF8.GetByteCount(allContent.Substring(0, charPos));
            string body          = string.Join("\n", toDisplay);

            bool sent = false;
            try
            {
                PostToDiscord(title, body).GetAwaiter().GetResult();
                sent = true;
            }
            catch { }

            if (!sent) return;

            lock (_fileLock)
            {
                try
                {
                    _writer?.Dispose();
                    _writer = null;

                    byte[] current = File.ReadAllBytes(_bufferPath);
                    File.WriteAllBytes(_bufferPath,
                        current.Length <= consumedBytes
                            ? Array.Empty<byte>()
                            : current.Skip(consumedBytes).ToArray());

                    _writer = OpenWriter(FileMode.Append);
                }
                catch
                {
                    try { _writer = OpenWriter(FileMode.Append); } catch { }
                }
            }
        }

        private void RecoverAndSendLeftoverBuffer()
        {
            if (!File.Exists(_bufferPath)) return;

            string allContent;
            try { allContent = File.ReadAllText(_bufferPath, Encoding.UTF8); }
            catch { return; }

            if (string.IsNullOrWhiteSpace(allContent)) { TryDeleteBuffer(); return; }

            int maxChars     = Math.Clamp(Plugin.MaxMessageChars.Value, 100, 1900);
            var toDisplay    = new List<string>();
            int displayChars = 0;
            int charPos      = 0;
            int searchFrom   = 0;

            while (searchFrom <= allContent.Length)
            {
                int  newline  = allContent.IndexOf('\n', searchFrom);
                bool lastLine = newline == -1;
                int  lineEnd  = lastLine ? allContent.Length : newline;
                int  nextPos  = lastLine ? allContent.Length : newline + 1;

                string line = allContent.Substring(searchFrom, lineEnd - searchFrom).TrimEnd('\r');

                if (displayChars + line.Length + 1 > maxChars) break;

                toDisplay.Add(line);
                displayChars += line.Length + 1;
                charPos       = nextPos;
                searchFrom    = nextPos;
                if (lastLine) break;
            }

            if (toDisplay.Count == 0) { TryDeleteBuffer(); return; }

            int    consumedBytes = Encoding.UTF8.GetByteCount(allContent.Substring(0, charPos));
            string body          = string.Join("\n", toDisplay);

            bool sent = false;
            try
            {
                PostToDiscord(
                    $"⚠️ [{_sourceName}][{_serverName}] Recovered — previous session ended unexpectedly — {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    body).GetAwaiter().GetResult();
                sent = true;
            }
            catch { }

            if (!sent) return;

            byte[] raw = File.ReadAllBytes(_bufferPath);
            if (raw.Length <= consumedBytes)
                TryDeleteBuffer();
            else
                File.WriteAllBytes(_bufferPath, raw.Skip(consumedBytes).ToArray());
        }

        // ── Discord HTTP ───────────────────────────────────────────────────────

        private System.Threading.Tasks.Task PostToDiscord(string title, string body)
        {
            string message = $"**{title}**\n{body.TrimEnd()}";
            string json    = "{\"content\":" + JsonString(message) + "}";
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

        private StreamWriter OpenWriter(FileMode mode)
        {
            var fs = new FileStream(_bufferPath, mode, FileAccess.Write, FileShare.ReadWrite);
            return new StreamWriter(fs, Encoding.UTF8) { AutoFlush = true };
        }

        private void TryDeleteBuffer()
        {
            try { File.Delete(_bufferPath); } catch { }
        }

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
                    case '\r':                    break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                        else          sb.Append(c);
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

            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
            {
                try   { TrySendBuffer($"🛑 [{_sourceName}][{_serverName}] Server Shutdown — {DateTime.Now:yyyy-MM-dd HH:mm:ss}"); }
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
