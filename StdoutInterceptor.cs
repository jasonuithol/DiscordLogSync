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
    /// Intercepts stdout at the OS file descriptor level using Linux pipe() + dup2()
    /// syscalls. Unlike Console.SetOut(), this captures ALL writes to fd 1 regardless
    /// of whether they come from managed C# code or native/unmanaged code — including
    /// Valheim world save events, ZDO counts, and PlayFab registration lines that
    /// bypass BepInEx's log pipeline entirely.
    ///
    /// Mechanism:
    ///   1. dup(1)              — saves a copy of the original stdout fd
    ///   2. pipe()              — creates a kernel pipe (read end + write end)
    ///   3. dup2(writeEnd, 1)   — replaces fd 1 with the pipe write end; all code
    ///                            that writes to fd 1 now writes into the pipe
    ///   4. Background thread   — reads from the pipe read end, immediately forwards
    ///                            every byte to the saved original stdout (so the
    ///                            server's own log output is preserved), and
    ///                            extracts complete lines to feed into Discord
    ///   5. Dispose()           — restores fd 1 to original stdout, which closes the
    ///                            pipe write end and causes the relay thread to exit
    ///
    /// What can go wrong:
    ///   - If dup2() fails mid-init, fd 1 may be left pointing at a half-open pipe
    ///     with no reader, silently swallowing all server output until process exit.
    ///   - If the relay thread crashes or deadlocks, the pipe buffer (~64 KB) fills
    ///     up, blocking ALL writes to fd 1 in every thread in the process.
    ///   - On Windows or non-Linux platforms this will throw at construction time
    ///     and leave stdout untouched.
    ///   - Hosting environments that inspect fd 1 directly (rare) may see unexpected
    ///     pipe semantics instead of a file or terminal.
    ///
    /// Linux only. Throws InvalidOperationException on any syscall failure so the
    /// caller can log the error and abort cleanly without a broken stdout.
    /// </summary>
    public class StdoutInterceptor : IDisposable
    {
        private readonly DiscordLogListener _listener;
        private readonly int               _originalStdoutFd;
        private readonly int               _pipeReadFd;
        private readonly Thread            _relayThread;
        private volatile bool              _disposed;

        // ── Linux syscalls ─────────────────────────────────────────────────────
        [DllImport("libc", SetLastError = true)] static extern int pipe(int[] pipefd);
        [DllImport("libc", SetLastError = true)] static extern int dup(int oldfd);
        [DllImport("libc", SetLastError = true)] static extern int dup2(int oldfd, int newfd);
        [DllImport("libc", SetLastError = true)] static extern int close(int fd);
        [DllImport("libc", SetLastError = true)] static extern int read(int fd, byte[] buf, int count);
        [DllImport("libc", SetLastError = true)] static extern int write(int fd, byte[] buf, int count);

        // ── Constructor ────────────────────────────────────────────────────────
        public StdoutInterceptor(DiscordLogListener listener)
        {
            _listener = listener;

            // Step 1 — save original stdout
            _originalStdoutFd = dup(1);
            if (_originalStdoutFd < 0)
                throw new InvalidOperationException(
                    $"[DiscordLogSync] StdoutInterceptor: dup(1) failed (errno {Marshal.GetLastWin32Error()}). stdout unchanged.");

            // Step 2 — create pipe
            int[] fds = new int[2]; // fds[0]=read, fds[1]=write
            if (pipe(fds) < 0)
            {
                close(_originalStdoutFd);
                throw new InvalidOperationException(
                    $"[DiscordLogSync] StdoutInterceptor: pipe() failed (errno {Marshal.GetLastWin32Error()}). stdout unchanged.");
            }

            _pipeReadFd    = fds[0];
            int pipeWriteFd = fds[1];

            // Step 3 — replace fd 1 with pipe write end
            // From this point on, ALL writes to fd 1 go into the pipe.
            if (dup2(pipeWriteFd, 1) < 0)
            {
                close(_originalStdoutFd);
                close(fds[0]);
                close(fds[1]);
                throw new InvalidOperationException(
                    $"[DiscordLogSync] StdoutInterceptor: dup2() failed (errno {Marshal.GetLastWin32Error()}). stdout unchanged.");
            }
            close(pipeWriteFd); // fd 1 is now the sole reference to the write end

            // Step 4 — start relay thread before returning, so the pipe doesn't fill
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
                int n = read(_pipeReadFd, buf, buf.Length);
                if (n <= 0) break; // EOF — write end was closed by Dispose()

                // Forward every byte to original stdout immediately.
                // This preserves the server's log output on the host side.
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

            close(_pipeReadFd);
        }

        // ── IDisposable ────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Restoring fd 1 to original stdout atomically closes the pipe write end
            // (dup2 closes the target fd before duplicating). With no writer left,
            // the relay thread's next read() returns 0 (EOF) and exits cleanly.
            dup2(_originalStdoutFd, 1);
            close(_originalStdoutFd);

            _relayThread?.Join(2000);
        }
    }
}
