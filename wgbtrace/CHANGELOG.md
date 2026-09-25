# Changelog

## v0.1.0 - 2026-09-25

- Add offline parsing of Cisco WGB timestamps, modules, numeric levels, and radios.
- Add conservative recognition for initial roaming and connection event classes.
- Surface unknown lines with important keywords as `UNCLASSIFIED_INTERESTING`.
- Preserve every original line in JSON Lines output.
- Add `--around` with a configurable time window and corresponding raw-log output.
