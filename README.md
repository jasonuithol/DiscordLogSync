# DiscordLogger — Valheim BepInEx Mod

Pipes all BepInEx log output to a Discord webhook in near-real-time,
with local buffering so no lines are lost on unexpected process death.

---

## How It Works

```
Every log line
      │
      ▼
[Timestamp + format]
      │
      ▼
BepInEx/DiscordLogBuffer.txt   ← flushed to disk immediately, every line
      │
      ▼ (every N seconds, background thread)
Discord Webhook POST
      │
      ├─ 200 OK  → truncate buffer, start fresh
      └─ failure → leave buffer, retry next tick

On next startup:
  buffer file exists? → send as "recovered from crash" → delete → begin fresh
```

---

## Build

**Requirements:** .NET SDK with `netstandard2.1` support (i.e. .NET Core 3.0+ or .NET 5+ SDK), `dotnet` CLI, Valheim dedicated server + BepInEx installed.

> Targets `netstandard2.1`, which runs on Unity's Mono 6.4+ runtime. Not compatible with plain .NET Framework 4.7.2.

The `.csproj` assumes the default Linux Steam dedicated server path:
```
~/.steam/steam/steamapps/common/Valheim dedicated server
```
Edit the `<ValheimDir>` property if your path differs.

```bash
dotnet build -c Release
```

Output: `bin/Release/netstandard2.1/DiscordLogger.dll`

---

## Install

1. Copy `DiscordLogger.dll` into `BepInEx/plugins/`
2. Launch the game once to generate the config file
3. Open `BepInEx/config/com.yourname.discordlogger.cfg`
4. Set your webhook URL:

```ini
[Discord]
WebhookUrl = https://discord.com/api/webhooks/YOUR_ID/YOUR_TOKEN
SendIntervalSeconds = 3
MaxEmbedChars = 3800
```

5. Restart

---

## Config Options

| Key                  | Default | Description                                              |
|----------------------|---------|----------------------------------------------------------|
| `WebhookUrl`         | (empty) | **Required.** Discord webhook URL.                       |
| `SendIntervalSeconds`| `3`     | How often to flush buffer to Discord. Minimum 2.         |
| `MaxEmbedChars`      | `3800`  | Characters per embed. Oldest lines dropped if exceeded.  |

---

## Buffer File

`BepInEx/DiscordLogBuffer.txt`

- Created fresh each run
- Every line flushed to disk immediately (no in-memory buffering)
- If the file exists on startup → previous run crashed → contents sent as recovery message
- Deleted/truncated after each successful Discord POST

---

## Discord Message Colors

| Situation             | Color  |
|-----------------------|--------|
| Contains `[Fatal]` or `[Error]` | 🔴 Red    |
| Contains `[Warning]`            | 🟠 Orange |
| Recovery (crash)                | 🟣 Purple |
| Normal                          | 🔵 Blue   |
