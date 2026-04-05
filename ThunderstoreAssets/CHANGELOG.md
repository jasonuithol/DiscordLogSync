## 1.0.2

- Add configurable `Source` setting: `BepInEx` (default), `Console`, or `RawStdout`
- `BepInEx` — original ILogListener approach, unchanged
- `Console` — intercepts managed Console.Out via Console.SetOut()
- `RawStdout` — experimental, Linux only; intercepts stdout at the OS file descriptor
  level using pipe()+dup2(), capturing world saves, ZDO counts, and all native stdout
  writes that bypass BepInEx entirely. Falls back to BepInEx source on init failure.
- Fix recovery message title to include server name and timestamp
- Fix shutdown message to use consistent em-dash formatting

## 1.0.1

- Add server name, timestamp to the message title
- Don't double up timestamps in the message content
- Show log event source and level.

## 1.0.0

- Initial release
- Pipes all BepInEx and Unity logs to a Discord webhook in near-real-time
- Local buffer file flushed on every line — nothing lost on unexpected shutdown
- FIFO queue — oldest logs sent first, catches up automatically after bursts
- Crash recovery — leftover buffer from previous session sent on next startup flagged as recovered
- Configurable send interval and max message size


