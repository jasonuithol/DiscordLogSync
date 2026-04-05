using System.IO;
using System.Text;

namespace DiscordLogSync
{
    /// <summary>
    /// Wraps the existing Console.Out and intercepts every line written to it.
    /// A [ThreadStatic] re-entrancy guard on WriteLine prevents infinite loops when
    /// BepInEx's LoggedTextWriter calls back into Console.Out during our own forward.
    /// </summary>
    public class ConsoleInterceptor : TextWriter
    {
        private readonly TextWriter         _original;
        private readonly DiscordLogListener _listener;
        private readonly StringBuilder      _partial = new StringBuilder();
        private readonly object             _lock    = new object();

        // Per-thread flag — breaks the LoggedTextWriter → us → LoggedTextWriter loop
        [System.ThreadStatic]
        private static bool _inWrite;

        public ConsoleInterceptor(TextWriter original, DiscordLogListener listener)
        {
            _original = original;
            _listener = listener;
        }

        public override Encoding Encoding => _original.Encoding;

        // ── Core overrides ─────────────────────────────────────────────────────

        public override void Write(char value)
        {
            _original.Write(value);

            lock (_lock)
            {
                if (value == '\n')
                    FlushPartial();
                else if (value != '\r')
                    _partial.Append(value);
            }
        }

        public override void Write(string value)
        {
            if (value == null) return;
            _original.Write(value);

            lock (_lock)
            {
                foreach (char c in value)
                {
                    if (c == '\n')
                        FlushPartial();
                    else if (c != '\r')
                        _partial.Append(c);
                }
            }
        }

        public override void WriteLine(string value)
        {
            _original.WriteLine(value);

            if (_inWrite) return;
            _inWrite = true;
            try
            {
                lock (_lock)
                {
                    _partial.Append(value ?? "");
                    FlushPartial();
                }
            }
            finally { _inWrite = false; }
        }

        public override void WriteLine()
        {
            _original.WriteLine();

            if (_inWrite) return;
            _inWrite = true;
            try   { lock (_lock) { FlushPartial(); } }
            finally { _inWrite = false; }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private void FlushPartial()
        {
            // WriteToBuffer handles blank line filtering
            _listener.WriteToBuffer(_partial.ToString());
            _partial.Clear();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                lock (_lock)
                {
                    if (_partial.Length > 0) FlushPartial();
                }
            base.Dispose(disposing);
        }
    }
}
