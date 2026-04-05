using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DiscordLogSync
{
    /// <summary>
    /// EXPERIMENTAL — HIGH RISK. Read the config comments before enabling.
    ///
    /// Intercepts stdout at the CRT/OS file descriptor level so that ALL writes
    /// to fd 1 are captured — including Valheim world save events, ZDO counts,
    /// and PlayFab registration lines that bypass BepInEx's log pipeline entirely.
    ///
    /// Mechanism (same on both platforms, different syscalls):
    ///   Linux  — pipe() + dup2() via libc
    ///   Windows — _pipe() + _dup2() via ucrtbase.dll (Universal CRT)
    ///
    ///   1. Save original stdout fd via dup/dup2
    ///   2. Create a kernel pipe (read end + write end)
    ///   3. dup2 write end → fd 1; all writes now flow into the pipe
    ///   4. Background relay thread reads from pipe, forwards every byte to the
    ///      saved original stdout (preserves host log output), and extracts
    ///      complete lines to feed into Discord
    ///   5. Dispose() restores fd 1, which signals EOF to the relay thread
    ///
    /// What can go wrong:
    ///   - If dup2 fails mid-init, fd 1 may point at a half-open pipe with no
    ///     reader, silently swallowing all server output until process exit.
    ///   - If the relay thread crashes or deadlocks, the pipe buffer fills up
    ///     (~64 KB on Linux, configurable on Windows) and blocks ALL writes to
    ///     fd 1 in the entire process.
    ///   - Hosting environments that inspect fd 1 directly may see unexpected
    ///     pipe semantics instead of a file or terminal.
    ///
    /// Throws InvalidOperationException on any syscall failure so the caller can
    /// log the error and fall back to BepInEx source with stdout intact.
    /// </summary>
    public class StdoutInterceptor : IDisposable
    {
        private readonly DiscordLogListener _listener;
        private readonly bool               _isWindows;
        private readonly int                _originalStdoutFd;
        private readonly int                _pipeReadFd;
        private readonly Thread             _relayThread;
        private volatile bool               _disposed;

        // ── Linux syscalls (libc) ──────────────────────────────────────────────
        [DllImport("libc", SetLastError = true)] static extern int pipe(int[] pipefd);
        [DllImport("libc", SetLastError = true)] static extern int dup(int oldfd);
        [DllImport("libc", SetLastError = true)] static extern int dup2(int oldfd, int newfd);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] static extern int read(int fd, byte[] buf, int count);
        [DllImport("libc", SetLastError = true)] static extern int write(int fd, byte[] buf, int count);

        // ── Windows CRT (ucrtbase.dll) ─────────────────────────────────────────
        // _dup/_dup2/_pipe operate on the Universal CRT fd table, which is shared
        // by all DLLs in the process that link against ucrtbase.dll — including
        // Unity's native engine code. _dup2 also calls SetStdHandle() internally
        // so Win32 and CRT views of stdout stay in sync.
        private const int O_BINARY = 0x8000;
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] static extern int _dup(int fd);
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] static extern int _dup2(int src, int dst);
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] static extern int _pipe(int[] pfds, uint psize, int textmode);
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] static extern int _close(int fd);
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] static extern int _read(int fd, byte[] buf, uint count);
        [DllImport("ucrtbase.dll", CallingConvention = CallingConvention.Cdecl)] static extern int _write(int fd, byte[] buf, uint count);

        // ── Constructor ────────────────────────────────────────────────────────
        public StdoutInterceptor(DiscordLogListener listener)
        {
            _listener  = listener;
            _isWindows = Environment.OSVersion.Platform != PlatformID.Unix;

            int[] fds = new int[2]; // fds[0]=read end, fds[1]=write end

            if (_isWindows)
            {
                _originalStdoutFd = _dup(1);
                if (_originalStdoutFd < 0)
                    throw new InvalidOperationException(
                        "[DiscordLogSync] StdoutInterceptor: _dup(1) failed. stdout unchanged.");

                if (_pipe(fds, 65536, O_BINARY) < 0)
                {
                    _close(_originalStdoutFd);
                    throw new InvalidOperationException(
                        "[DiscordLogSync] StdoutInterceptor: _pipe() failed. stdout unchanged.");
                }

                if (_dup2(fds[1], 1) < 0)
                {
                    _close(_originalStdoutFd);
                    _close(fds[0]);
                    _close(fds[1]);
                    throw new InvalidOperationException(
                        "[DiscordLogSync] StdoutInterceptor: _dup2() failed. stdout unchanged.");
                }
                _close(fds[1]); // fd 1 is now the sole reference to the write end
            }
            else
            {
                _originalStdoutFd = dup(1);
                if (_originalStdoutFd < 0)
                    throw new InvalidOperationException(
                        $"[DiscordLogSync] StdoutInterceptor: dup(1) failed (errno {Marshal.GetLastWin32Error()}). stdout unchanged.");

                if (pipe(fds) < 0)
                {
                    close(_originalStdoutFd);
                    throw new InvalidOperationException(
                        $"[DiscordLogSync] StdoutInterceptor: pipe() failed (errno {Marshal.GetLastWin32Error()}). stdout unchanged.");
                }

                if (dup2(fds[1], 1) < 0)
                {
                    close(_originalStdoutFd);
                    close(fds[0]);
                    close(fds[1]);
                    throw new InvalidOperationException(
                        $"[DiscordLogSync] StdoutInterceptor: dup2() failed (errno {Marshal.GetLastWin32Error()}). stdout unchanged.");
                }
                close(fds[1]); // fd 1 is now the sole reference to the write end
            }

            _pipeReadFd = fds[0];

            // Start relay before returning so the pipe can't fill before we read it
            _relayThread = new Thread(RelayLoop)
            {
                IsBackground = true,
                Name         = "DiscordLogSync-StdoutRelay"
            };
            _relayThread.Start();
        }

        // ── Relay loop ─────────────────────────────────────────────────────────

        private void RelayLoop()
        {
            var buf       = new byte[4096];
            var lineBytes = new List<byte>(256);

            while (true)
            {
                int n = _isWindows
                    ? _read(_pipeReadFd, buf, (uint)buf.Length)
                    :  read(_pipeReadFd, buf,       buf.Length);

                if (n <= 0) break; // EOF — write end was closed by Dispose()

                // Forward every byte to original stdout immediately.
                // This preserves the server's log output on the host side.
                if (_isWindows)
                    _write(_originalStdoutFd, buf, (uint)n);
                else
                    write(_originalStdoutFd, buf, n);

                // Extract complete lines and feed to Discord buffer.
                for (int i = 0; i < n; i++)
                {
                    if (buf[i] == 0x0A) // LF — end of line
                    {
                        // Strip trailing CR if present (CRLF line endings)
                        if (lineBytes.Count > 0 && lineBytes[lineBytes.Count - 1] == 0x0D)
                            lineBytes.RemoveAt(lineBytes.Count - 1);

                        _listener.WriteToBuffer(Encoding.UTF8.GetString(lineBytes.ToArray()));
                        lineBytes.Clear();
                    }
                    else
                    {
                        lineBytes.Add(buf[i]);
                    }
                }
            }

            // Flush any partial line buffered at the moment the pipe closed
            if (lineBytes.Count > 0)
                _listener.WriteToBuffer(Encoding.UTF8.GetString(lineBytes.ToArray()));

            if (_isWindows) _close(_pipeReadFd);
            else             close(_pipeReadFd);
        }

        // ── IDisposable ────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Restoring fd 1 to original stdout atomically closes the pipe write
            // end (dup2 closes the target fd before duplicating). With no writer
            // left, the relay thread's next read returns 0 (EOF) and exits cleanly.
            if (_isWindows)
            {
                _dup2(_originalStdoutFd, 1);
                _close(_originalStdoutFd);
            }
            else
            {
                dup2(_originalStdoutFd, 1);
                close(_originalStdoutFd);
            }

            _relayThread?.Join(2000);
        }
    }
}
