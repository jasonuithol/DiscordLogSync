# DiscordLogSync — Valheim Dedicated Server Mod

Pipes Valheim dedicated server logs to a Discord webhook in near-real-time,
with local buffering so no lines are lost on unexpected process death.

---

## How It Works

```
Server output (BepInEx pipeline, Console.Out, or raw stdout — see Source config)
      │
      ▼
BepInEx/DiscordLogBuffer.txt   ← every line flushed to disk immediately
      │
      ▼  (background thread, every N seconds)
Discord Webhook POST
      │
      ├─ success → remove sent lines from front of buffer (FIFO)
      └─ failure → leave buffer intact, retry next tick

On startup:
  buffer file exists from previous run? → send as ⚠️ crash-recovery message first
On clean shutdown:
  flush remaining buffer as 🛑 shutdown message
```

---

## Log Sources

Configure `Source` in the config file to choose what gets captured:

| Source | What it captures | Risk |
|--------|-----------------|------|
| `BepInEx` *(default)* | Everything flowing through BepInEx's log pipeline | Safe |
| `Console` | Managed `Console.Out` writes (superset of BepInEx in some cases) | Low |
| `RawStdout` | **All** stdout at the OS fd level — including world saves, ZDO counts, PlayFab lines that bypass BepInEx entirely | **Experimental — read warning below** |

### ⚠️ RawStdout Warning

`RawStdout` uses Linux `pipe()` + `dup2()` to replace fd 1 with a kernel pipe.
A background relay thread forwards every byte to the original stdout and extracts
lines for Discord. **If the relay thread crashes or deadlocks, the pipe buffer
(~64 KB) will fill up and block all stdout writes in the entire process.**

- Linux only — falls back to `BepInEx` source automatically on Windows or on any syscall failure
- Only enable this if you specifically need to capture native stdout output (e.g. world save events)
- Designed as a diagnostic tool, not a permanent production setting

---

## Install

1. Copy `DiscordLogSync.dll` into `BepInEx/plugins/`
2. Launch the server once to generate the config file
3. Open `BepInEx/config/com.byawn.DiscordLogSync.cfg`
4. Set your webhook URL and preferred source:

```ini
[Discord]
Source = BepInEx
WebhookUrl = https://discord.com/api/webhooks/YOUR_ID/YOUR_TOKEN
SendIntervalSeconds = 3
MaxMessageChars = 1800
```

5. Restart the server

---

## Config Reference

| Key | Default | Description |
|-----|---------|-------------|
| `Source` | `BepInEx` | Log source: `BepInEx`, `Console`, or `RawStdout` (see above) |
| `WebhookUrl` | *(empty)* | **Required.** Discord webhook URL. Plugin does nothing if unset. |
| `SendIntervalSeconds` | `3` | Flush interval in seconds. Minimum 2 (Discord rate limit). |
| `MaxMessageChars` | `1800` | Max characters per Discord message. Hard Discord limit is 2000. |

---

## Buffer File

`BepInEx/DiscordLogBuffer.txt`

- Every line is flushed to disk immediately — a hard kill loses at most the current line
- Treated as a FIFO queue: oldest lines sent first, newest preserved at the back
- If the file exists on startup, its contents are sent as a crash-recovery message before normal logging begins

---

## Build

Requires .NET SDK with `netstandard2.1` support and a Valheim dedicated server + BepInEx installation.
The `.csproj` assumes the default Linux Steam path:

```
~/.steam/steam/steamapps/common/Valheim dedicated server
```

```bash
./build.sh        # dotnet build -c Release
./deploy_server.sh # copy DLL + config to local server
./package.sh      # zip ThunderstoreAssets + DLL for upload
```
