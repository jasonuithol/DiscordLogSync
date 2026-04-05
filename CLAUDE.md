# DiscordLogSync — Claude Code Game Plan

## Project Overview

A BepInEx mod for the Valheim dedicated server that mirrors logs to a Discord webhook in near-real-time.

- Language: C# / netstandard2.1
- Framework: BepInEx 5.x on Unity/Mono (Valheim dedicated server, Linux)
- Namespace: `DiscordLogSync`
- Plugin GUID: `com.byawn.DiscordLogSync`

## Repo Structure

```
DiscordLogSync.csproj
Plugin.cs
DiscordLogListener.cs
build.sh
deploy_server.sh
package.sh
ThunderstoreAssets/
  icon.png
  manifest.json
  README.md
  CHANGELOG.md
```

## Two Branches to Merge

### `master` — Working, ILogListener-based

`Plugin.cs` — standard BepInEx plugin entry point, config binding, registers `DiscordLogListener` as a `BepInEx.Logging.Logger.Listeners` entry.

`DiscordLogListener.cs` — implements `ILogListener, IDisposable`. Key behaviours:
- `LogEvent()` receives every log event from BepInEx's pipeline, strips blank lines, writes to buffer file immediately (AutoFlush=true)
- Background timer sends oldest buffered lines to Discord via webhook POST
- FIFO queue: sends oldest lines first, trims exactly those bytes from the buffer file on success (byte-offset based, NOT line-count based — this is important)
- On startup: if buffer file exists from previous run, sends it as a crash recovery message first
- On clean shutdown: final flush sent as shutdown message
- All Discord messages use `content` field (not embeds) for full-width display

**Known limitation:** Only captures events that flow through BepInEx's log pipeline. Valheim writes some important events (world saves, ZDO counts, PlayFab registration) directly to stdout, bypassing BepInEx entirely — these are NOT captured.

### `streaminterceptor` — Experimental, buggy, Console.SetOut-based

Attempted to intercept `Console.Out` via a `ConsoleInterceptor : TextWriter` class using `Console.SetOut()`. This caused an infinite recursion because BepInEx's `LoggedTextWriter` internally re-references `Console.Out`, creating a loop:

```
LoggedTextWriter.WriteLine → ConsoleInterceptor.WriteLine → LoggedTextWriter.WriteLine → ...
```

A `[ThreadStatic] bool _inWrite` re-entrancy guard was added but the approach remains unreliable. This branch is NOT production-ready.

## The Merge Goal

Unify both branches into a single mod with a **configurable `Source` enum**:

```ini
[Discord]
Source = BepInEx   # or: Console
WebhookUrl = https://...
SendIntervalSeconds = 3
MaxMessageChars = 1800
```

### Source = BepInEx
Use `master`'s `ILogListener` approach exactly as-is. No changes to this path.

### Source = Console
Use `Console.SetOut()` intercept with a fixed `ConsoleInterceptor`. The recursion fix must be rock solid. See implementation notes below.

## ConsoleInterceptor — Required Fix

The core issue: `Console.SetOut(_interceptor)` puts us in the chain, but BepInEx's `LoggedTextWriter` holds a reference to whatever `Console.Out` was at the time BepInEx initialised — NOT a live reference. So `SetOut` may not even be in the call path at all.

**Approach to investigate/try in order:**

1. **Re-entrancy guard (simplest):** `[ThreadStatic] static bool _inWrite` on `WriteLine` only. Forward to `_original` first, then if `_inWrite` is false, set it, call `WriteToBuffer`, clear it. This breaks the loop if `_original.WriteLine` calls back into us.

2. **OpenStandardOutput (bypass wrapper):** Use `new StreamWriter(Console.OpenStandardOutput())` as `_original` instead of `Console.Out`. This bypasses the BepInEx `LoggedTextWriter` wrapper entirely for forwarding, so there's nothing to loop back through.

3. **Reflection into LoggedTextWriter:** Get `Console.Out` (which is BepInEx's `LoggedTextWriter`), find its inner writer field via reflection, replace it with our interceptor, store the original inner writer for forwarding and restoration. This inserts us directly into BepInEx's chain rather than wrapping it.

The `master` branch buffer/send engine (`DiscordLogListener`) does NOT change — `ConsoleInterceptor` just calls `_listener.WriteToBuffer(line)` exactly as `ILogListener.LogEvent()` does.

## DiscordLogListener — Must Preserve These Behaviours

These are non-negotiable from `master`:

1. **Byte-offset trimming** — when removing sent lines from the buffer, calculate `consumedBytes = Encoding.UTF8.GetByteCount(allContent.Substring(0, charPos))`, then `File.ReadAllBytes` and `Skip(consumedBytes)`. Do NOT use line counting (`Split('\n').Skip(N)`) — this caused missing log lines due to race conditions with new lines arriving during HTTP send.

2. **Blank line filtering at write time only** — `WriteToBuffer` / `LogEvent` strips blank lines. `TrySendBuffer` and `RecoverAndSendLeftoverBuffer` do NOT filter blank lines — they process the buffer as raw bytes/chars.

3. **FIFO order** — oldest lines sent first, newest preserved at the back.

4. **Crash recovery** — buffer file persists across restarts. If it exists on startup, send its contents as a recovery message before normal logging begins.

5. **Titles include server name and timestamp:**
   - Normal: `📋 [My server] 2026-03-28 14:32:01`
   - Recovery: `⚠️ [My server] Recovered — previous session ended unexpectedly — 2026-03-28 14:32:01`
   - Shutdown: `🛑 [My server] Server Shutdown — 2026-03-28 14:32:01`

6. **Server name** parsed from CLI args: `Environment.GetCommandLineArgs()`, look for `-name` flag, fallback to `"Valheim"`.

7. **Discord message format:** `content` field (NOT embeds), plain text, `**{title}**\n{body}`. No code fences.

## Build System

```bash
./build.sh          # dotnet build -c Release
./deploy_server.sh  # copies DLL to BepInEx/plugins on local server
./package.sh        # zips ThunderstoreAssets + DLL for Thunderstore upload
```

Target: `netstandard2.1` on Mono/Unity. Valheim path: `~/.steam/steam/steamapps/common/Valheim dedicated server`

## What Claude Code Should Do

1. Check out `master` as the base
2. Read both `Plugin.cs` and `DiscordLogListener.cs` from master carefully
3. Add `Source` enum config (`BepInEx` | `Console`) to `Plugin.cs`
4. Add `ConsoleInterceptor.cs` with the recursion fix (try approaches in order above, verify it actually works)
5. Wire up `Plugin.Awake()` to instantiate the correct strategy based on config
6. Ensure `DiscordLogListener` is strategy-agnostic — it just receives lines via `WriteToBuffer()` (rename `LogEvent` path to use this too, or keep both entry points)
7. Build with `./build.sh` and confirm zero errors/warnings
8. Do NOT touch `TrySendBuffer` byte-offset logic, the FIFO queue, or the crash recovery flow
9. Do NOT add multiple simultaneous sources — one source, one webhook, one buffer file

## Testing

After building, deploy to the local server with `./deploy_server.sh` and start the server. Confirm in Discord:
- Log messages appear within a few seconds of appearing in terminal
- World save lines (`World save writing starting`, `Saved N ZDOs`, etc.) appear when `Source = Console`
- Recovery message appears if server is killed hard and restarted
- Shutdown message appears on clean stop
