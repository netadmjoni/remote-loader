# Design

`wgbdiag` is built as an operator-first diagnostic script for live Cisco WGB roaming investigations.

## Behavior Stability

The script is already tested in production. The `v1.1.1` project structure documents and packages the existing script without refactoring or behavioral changes.

Future changes should separate documentation-only work from runtime changes, and runtime changes should be validated against real or representative WGB CLI output.

## Compact Terminal Output

Terminal output is intentionally compact because it is meant for live operators watching a session in real time.

The normal sample line keeps the important live fields visible:

```text
2026-07-08 12:55:09 AP=MHO1109STV-421-o2930 RSSI=47 CH=40 RATE="173/173 Mbps" R=2
```

The terminal view prioritizes quick recognition of:

- current parent AP
- RSSI
- channel
- current Tx/Rx data rate
- uplink radio ID
- reconnect and timeout status
- roam notifications when enabled

The terminal is not intended to carry every raw diagnostic field.

## Full CSV Diagnostic Output

CSV output is the durable troubleshooting record. It keeps the fields needed for later analysis, timeline review, and TAC/debug work.

CSV rows include current AP information, previous AP information for roam-related events, association state, authentication data, key-management data, connected duration, and downtime where applicable.

This split is deliberate:

- terminal output stays readable during live work
- CSV output keeps the fuller evidence trail for later analysis

## Robust Reconnect Logic

The script treats failed SSH connection, login failure, enable failure, command timeout, and closed SSH sessions as expected operational failure modes.

Reconnect behavior is controlled by `--reconnect` and defaults to 60 seconds. The script tracks downtime with `down_since` and writes connection failure and recovery events to CSV where possible.

Reconnect behavior should remain conservative and robust. Any future change in this area should be tested carefully because the tool is meant to keep running during unstable wireless or management-plane conditions.

## Daily Log Rotation By Default

Automatic logging is enabled by default. Unless `--no-log` is used, the script creates `./logs` and writes date-stamped files:

```text
./logs/wgb-YYYYMMDD.log
./logs/wgb-YYYYMMDD.csv
```

The filename is resolved each time output is written, so a long-running process naturally rolls into the next day's files.

Operators can override paths with `--log-dir`, `--log`, or `--csv`, or disable file output with `--no-log`.

## Compatibility

The script is intended for Linux/WSL environments with Expect 5.45 and OpenSSH.

Avoid introducing dependencies or shell features that would reduce compatibility with that environment unless a future release explicitly changes the supported platform.
