using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using System;

namespace DiscordLogSync
{
    public enum LogSource
    {
        /// <summary>
        /// Captures everything that flows through BepInEx's log pipeline.
        /// Reliable and safe. Misses lines written directly to stdout by the
        /// game (world saves, ZDO counts, PlayFab registration).
        /// </summary>
        BepInEx,

        /// <summary>
        /// Intercepts managed Console.Out via Console.SetOut(). Captures
        /// slightly more than BepInEx mode but still misses native stdout
        /// writes. Not recommended — use BepInEx or RawStdout instead.
        /// </summary>
        Console,

        /// <summary>
        /// EXPERIMENTAL — HIGH RISK. See config comment below.
        /// Intercepts stdout at the OS file descriptor level. Captures
        /// everything including world saves and native stdout writes.
        /// Linux only.
        /// </summary>
        RawStdout
    }

    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID    = "com.byawn.DiscordLogSync";
        public const string NAME    = "DiscordLogSync";
        public const string VERSION = "1.0.2";

        // Config entries - public so DiscordLogListener can read them
        public static ConfigEntry<LogSource> Source;
        public static ConfigEntry<string>    WebhookUrl;
        public static ConfigEntry<int>       SendIntervalSeconds;
        public static ConfigEntry<int>       MaxMessageChars;

        private DiscordLogListener   _listener;
        private ConsoleInterceptor   _consoleInterceptor;
        private StdoutInterceptor    _stdoutInterceptor;
        private System.IO.TextWriter _originalOut;

        private void Awake()
        {
            Source = Config.Bind(
                "Discord", "Source", LogSource.BepInEx,
                "Log source. Choose one:\n" +
                "  BepInEx   — Safe. Captures BepInEx log pipeline. Misses world saves / ZDO counts.\n" +
                "  Console   — Captures managed Console.Out. Still misses native stdout writes.\n" +
                "  RawStdout — !! EXPERIMENTAL / HIGH RISK !! Linux only. Uses pipe()+dup2() to\n" +
                "              intercept stdout at the OS fd level. Captures EVERYTHING including\n" +
                "              world saves and ZDO counts. If initialisation fails or the relay\n" +
                "              thread dies, ALL server stdout will be silently swallowed until the\n" +
                "              process exits. Only enable this if you specifically need to capture\n" +
                "              evidence of world-save interruptions and understand the risk.");

            WebhookUrl = Config.Bind(
                "Discord", "WebhookUrl", "",
                "Your Discord webhook URL. Required.");

            SendIntervalSeconds = Config.Bind(
                "Discord", "SendIntervalSeconds", 3,
                "How often (in seconds) to flush the buffer to Discord. Minimum 2 (Discord rate limit).");

            MaxMessageChars = Config.Bind(
                "Discord", "MaxMessageChars", 1800,
                "Max log characters per Discord message (hard limit is 2000).");

            if (string.IsNullOrWhiteSpace(WebhookUrl.Value))
            {
                Logger.LogWarning("[DiscordLogSync] No WebhookUrl configured — logging to Discord is disabled.");
                return;
            }

            string serverName = "Valheim";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-name") { serverName = args[i + 1]; break; }

            _listener = new DiscordLogListener(serverName, WebhookUrl.Value);

            switch (Source.Value)
            {
                case LogSource.BepInEx:
                    BepInEx.Logging.Logger.Listeners.Add(_listener);
                    Logger.LogInfo($"[DiscordLogSync] Started (BepInEx source). Flushing every {Math.Max(2, SendIntervalSeconds.Value)}s.");
                    break;

                case LogSource.Console:
                    _originalOut        = System.Console.Out;
                    _consoleInterceptor = new ConsoleInterceptor(System.Console.Out, _listener);
                    System.Console.SetOut(_consoleInterceptor);
                    Logger.LogInfo($"[DiscordLogSync] Started (Console source). Flushing every {Math.Max(2, SendIntervalSeconds.Value)}s.");
                    break;

                case LogSource.RawStdout:
                    if (Environment.OSVersion.Platform != PlatformID.Unix)
                    {
                        Logger.LogError("[DiscordLogSync] RawStdout source requires Linux. Falling back to BepInEx source.");
                        BepInEx.Logging.Logger.Listeners.Add(_listener);
                        break;
                    }
                    Logger.LogWarning("[DiscordLogSync] RawStdout source: intercepting stdout at fd level via pipe()+dup2(). " +
                                      "If this breaks, ALL server stdout will be lost. See config for details.");
                    try
                    {
                        _stdoutInterceptor = new StdoutInterceptor(_listener);
                        Logger.LogInfo($"[DiscordLogSync] Started (RawStdout source). Flushing every {Math.Max(2, SendIntervalSeconds.Value)}s.");
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"[DiscordLogSync] RawStdout source failed to initialise: {ex.Message}");
                        Logger.LogError("[DiscordLogSync] stdout is intact — falling back to BepInEx source.");
                        BepInEx.Logging.Logger.Listeners.Add(_listener);
                    }
                    break;
            }
        }

        private void OnDestroy()
        {
            if (_listener == null) return;

            switch (Source.Value)
            {
                case LogSource.BepInEx:
                    BepInEx.Logging.Logger.Listeners.Remove(_listener);
                    break;

                case LogSource.Console:
                    if (_originalOut != null)
                        System.Console.SetOut(_originalOut);
                    _consoleInterceptor?.Dispose();
                    break;

                case LogSource.RawStdout:
                    // Dispose restores fd 1 before the listener flushes,
                    // so the shutdown Discord message still goes out via the
                    // restored original stdout path.
                    _stdoutInterceptor?.Dispose();
                    // If we fell back to BepInEx source, remove the listener
                    if (_stdoutInterceptor == null)
                        BepInEx.Logging.Logger.Listeners.Remove(_listener);
                    break;
            }

            _listener.Dispose();
        }
    }
}
