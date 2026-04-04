using System;
using System.IO;
using System.Text;
using System.Threading;

namespace DiscordLogSync
{
    /// <summary>
    /// Tails a log file on disk (e.g. BepInEx/LogOutput.log), feeding new lines
    /// into a DiscordLogListener as they appear. Uses a polling timer rather than
    /// FileSystemWatcher for reliability across platforms.
    /// </summary>
    public class LogTailer : IDisposable
    {
        private readonly string              _filePath;
        private readonly DiscordLogListener  _listener;
        private readonly Timer               _pollTimer;
        private          long                _position = 0;
        private volatile bool                _disposed;

        public LogTailer(string filePath, DiscordLogListener listener)
        {
            _filePath = filePath;
            _listener = listener;

            // Start reading from the end of the file — we don't want to
            // re-send everything that's already been logged before this session.
            if (File.Exists(_filePath))
            {
                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _position = fs.Length;
            }

            int intervalMs = Math.Max(2, Plugin.SendIntervalSeconds.Value) * 1000;
            _pollTimer = new Timer(OnPollTick, null, intervalMs, intervalMs);
        }

        private void OnPollTick(object _)
        {
            if (_disposed) return;

            try
            {
                if (!File.Exists(_filePath)) return;

                using var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

                // Handle log rotation / truncation
                if (fs.Length < _position)
                    _position = 0;

                if (fs.Length == _position) return;

                fs.Seek(_position, SeekOrigin.Begin);

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: true);
                string line;
                while ((line = reader.ReadLine()) != null)
                    _listener.WriteToBuffer(line);

                _position = fs.Position;
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _pollTimer?.Dispose();
        }
    }
}
