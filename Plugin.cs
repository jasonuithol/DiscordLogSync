using BepInEx;
using BepInEx.Configuration;
using System;
using System.IO;

namespace DiscordLogSync
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID    = "com.byawn.DiscordLogSync";
        public const string NAME    = "DiscordLogSync";
        public const string VERSION = "1.0.1";

        // Shared
        public static ConfigEntry<int> SendIntervalSeconds;
        public static ConfigEntry<int> MaxMessageChars;

        // BepInEx log source
        public static ConfigEntry<bool>   BepInExEnabled;
        public static ConfigEntry<string> BepInExWebhookUrl;

        // Console source
        public static ConfigEntry<bool>   ConsoleEnabled;
        public static ConfigEntry<string> ConsoleWebhookUrl;

        private DiscordLogListener  _bepinexListener;
        private LogTailer           _logTailer;
        private DiscordLogListener  _consoleListener;
        private ConsoleInterceptor  _interceptor;
        private TextWriter          _originalOut;

        private void Awake()
        {
            SendIntervalSeconds = Config.Bind("General", "SendIntervalSeconds", 3,
                "How often (seconds) to flush each buffer to Discord. Minimum 2.");

            MaxMessageChars = Config.Bind("General", "MaxMessageChars", 1800,
                "Max characters per Discord message (hard limit 2000).");

            BepInExEnabled = Config.Bind("Source.BepInEx", "Enabled", true,
                "Tail BepInEx/LogOutput.log and send to Discord.");

            BepInExWebhookUrl = Config.Bind("Source.BepInEx", "WebhookUrl", "",
                "Discord webhook URL for BepInEx log output.");

            ConsoleEnabled = Config.Bind("Source.Console", "Enabled", true,
                "Intercept console (stdout) output and send to Discord.");

            ConsoleWebhookUrl = Config.Bind("Source.Console", "WebhookUrl", "",
                "Discord webhook URL for console (stdout) output.");

            string serverName = "Valheim";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-name") { serverName = args[i + 1]; break; }

            if (BepInExEnabled.Value && !string.IsNullOrWhiteSpace(BepInExWebhookUrl.Value))
            {
                string logPath = Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
                _bepinexListener = new DiscordLogListener("BepInEx", serverName, BepInExWebhookUrl.Value);
                _logTailer       = new LogTailer(logPath, _bepinexListener);
                Logger.LogInfo($"[DiscordLogSync] BepInEx source active → {logPath}");
            }
            else
                Logger.LogWarning("[DiscordLogSync] BepInEx source disabled or no webhook configured.");

            if (ConsoleEnabled.Value && !string.IsNullOrWhiteSpace(ConsoleWebhookUrl.Value))
            {
                _consoleListener = new DiscordLogListener("Console", serverName, ConsoleWebhookUrl.Value);
                _originalOut     = System.Console.Out;
                _interceptor     = new ConsoleInterceptor(System.Console.Out, _consoleListener);
                System.Console.SetOut(_interceptor);
                Logger.LogInfo("[DiscordLogSync] Console source active.");
            }
            else
                Logger.LogWarning("[DiscordLogSync] Console source disabled or no webhook configured.");
        }

        private void OnDestroy()
        {
            _logTailer?.Dispose();
            _bepinexListener?.Dispose();

            if (_originalOut != null)
                System.Console.SetOut(_originalOut);
            _interceptor?.Dispose();
            _consoleListener?.Dispose();
        }
    }
}
