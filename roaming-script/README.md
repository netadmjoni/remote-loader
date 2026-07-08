# wgbdiag

`wgbdiag` is an Expect-based diagnostic tool for Cisco C9167 WGB roaming troubleshooting.

It continuously connects to a WGB-capable Cisco device over SSH, enters enable mode, polls `show wgb dot11 associations`, prints compact live terminal output, and writes detailed diagnostic data to daily log and CSV files.

The script is production-tested. Version `v1.1.1` keeps the existing runtime behavior unchanged.

## Requirements

- Linux or WSL
- Expect 5.45
- OpenSSH client
- Network reachability to the target device
- A login user/password and enable password

## Usage

```sh
chmod +x wgbdiag.exp
./wgbdiag.exp <host> <user> "<loginpw>" "<enablepw>" [options]
```

Basic example:

```sh
./wgbdiag.exp 10.194.240.11 admin "LOGIN" "ENABLE"
```

Use a one-second poll interval and reconnect after 60 seconds on failure:

```sh
./wgbdiag.exp 10.194.240.11 admin "LOGIN" "ENABLE" --interval 1 --reconnect 60
```

Write default daily logs under a custom folder:

```sh
./wgbdiag.exp 10.194.240.11 admin "LOGIN" "ENABLE" --log-dir ./truck-test
```

Print compact roam details in the terminal:

```sh
./wgbdiag.exp 10.194.240.11 admin "LOGIN" "ENABLE" --verbose-roam --roam-notify
```

Run terminal-only without log or CSV files:

```sh
./wgbdiag.exp 10.194.240.11 admin "LOGIN" "ENABLE" --no-log
```

Show raw SSH/CLI output while troubleshooting login or enable handling:

```sh
./wgbdiag.exp 10.194.240.11 admin "LOGIN" "ENABLE" --debug
```

Print the script version only:

```sh
./wgbdiag.exp --version
```

## Options

| Option | Description | Default |
| --- | --- | --- |
| `--interval <seconds>` | Poll interval for `show wgb dot11 associations`. | `1` |
| `--reconnect <seconds>` | Delay before retrying after connection, login, enable, timeout, or closed-session failures. | `60` |
| `--log-dir <folder>` | Folder used for automatic daily log and CSV files. | `./logs` |
| `--log <file>` | Specific human-readable log file path. | Daily file in `--log-dir` |
| `--csv <file>` | Specific CSV file path. | Daily file in `--log-dir` |
| `--no-log` | Disable all file logging. Terminal output only. | Off |
| `--roam-only` | Only print roam, reconnect, and info events to the terminal. Also enables verbose roam notification behavior internally. | Off |
| `--roam-notify` | Print roam notification lines to the terminal. | Off |
| `--no-roam-notify` | Do not print extra roam notification lines. | Default behavior |
| `--verbose-roam` | Print compact `FROM -> TO` roam lines in the terminal when roam notifications are enabled. | Off |
| `--warn-same-channel` | Print a terminal warning if the WGB roams between APs on the same channel. | Off |
| `--debug` | Show raw SSH/CLI output for troubleshooting login and enable behavior. | Off |
| `--version` | Print `wgbdiag v1.1.1` and exit with status code 0. | n/a |
| `--help` | Show built-in help. | n/a |

## Default Logging

Unless `--no-log` is used, the script creates the log directory automatically and writes daily files:

```text
./logs/wgb-YYYYMMDD.log
./logs/wgb-YYYYMMDD.csv
```

The human-readable log mirrors terminal-oriented status lines. The CSV contains full diagnostic rows for later analysis, TAC cases, and roam timeline reconstruction.

See [docs/CSV_FORMAT.md](docs/CSV_FORMAT.md) for CSV columns and event types.

## Notes

- Passwords are supplied as command-line arguments in the current script. Handle shell history and process visibility according to your operational environment.
- The script intentionally keeps terminal output compact for live operators.
- Reconnect handling is part of the normal operating model.
- Daily log rotation is the default when automatic logging is enabled.
