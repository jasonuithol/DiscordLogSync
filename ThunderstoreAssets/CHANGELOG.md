## 1.0.1

- Fix issue where world saves were being skipped.

## 1.0.0

- Initial release
- Pipes all BepInEx and Unity logs to a Discord webhook in near-real-time
- Local buffer file flushed on every line — nothing lost on unexpected shutdown
- FIFO queue — oldest logs sent first, catches up automatically after bursts
- Crash recovery — leftover buffer from previous session sent on next startup flagged as recovered
- Configurable send interval and max message size


