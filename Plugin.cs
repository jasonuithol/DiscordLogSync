using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace DiscordLogSync
{
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID    = "com.byawn.DiscordLogSync";
        public const string NAME    = "DiscordLogSync";
        public const string VERSION = "1.0.0";

        // Config entries - public so DiscordLogListener can read them
        public static ConfigEntry<string> WebhookUrl;
        public static ConfigEntry<int>    SendIntervalSeconds;
        public static ConfigEntry<int>    MaxMessageChars;

        private DiscordLogListener _listener;

        private void Awake()
        {
            WebhookUrl = Config.Bind(
                "Discord", "WebhookUrl", "",
                "Your Discord webhook URL. Required.");

            SendIntervalSeconds = Config.Bind(
                "Discord", "SendIntervalSeconds", 3,
                "How often (in seconds) to flush the buffer to Discord. Minimum 2 (Discord rate limit).");

            MaxMessageChars = Config.Bind(
                "Discord", "MaxMessageChars", 1800,
                "Max log characters per Discord message (hard limit is 2000). If buffer exceeds this, oldest lines are dropped.");

            if (string.IsNullOrWhiteSpace(WebhookUrl.Value))
            {
                Logger.LogWarning("[DiscordLogSync] No WebhookUrl configured — logging to Discord is disabled.");
                return;
            }

            _listener = new DiscordLogListener();
            BepInEx.Logging.Logger.Listeners.Add(_listener);

            Logger.LogInfo($"[DiscordLogSync] Started. Flushing every {System.Math.Max(2, SendIntervalSeconds.Value)}s.");
        }

        private void OnDestroy()
        {
            if (_listener == null) return;
            BepInEx.Logging.Logger.Listeners.Remove(_listener);
            _listener.Dispose();
        }
    }
}
