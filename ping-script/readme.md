# Wi-Fi Roaming / Link-Loss Ping Monitor (WSL/Linux)

A fast-interval ICMP monitor for diagnosing brief connectivity drops (e.g., Wi-Fi roaming / handoff events) when pinging a moving machine. It prints **clear, timestamped events** to the terminal and (optionally) writes **TXT + CSV logs** with **human timestamps including milliseconds** for correlation with other logs (syslog, roam events, controller logs, etc.).

---

## Features

- **Fast ping cadence** (default `0.1s` / 100ms) to detect short outages.
- Detects and highlights:
  - **LOSS** via `ping -O` (“no answer yet for icmp_seq=…”)
  - **UNREACHABLE** routing errors (“Destination Net/Host Unreachable”, “Network is unreachable”, etc.)
- Tracks **consecutive loss** and approximates **loss window duration**:
  - `loss_window_ms ≈ consecutive_loss * interval_ms`
- Emits an **ALERT** when loss window reaches a threshold (default `600ms`).
- Emits **RECOVER** when replies resume after a loss streak.
- Can run **console-only** with `--no-log` (no files created).

---

## Requirements

- WSL/Linux with `bash` and `iputils-ping` available.
- For intervals faster than ~200ms, `ping` typically requires elevated permissions:
  - run with `sudo`, or configure capabilities for `ping` on your distro.

---

## Installation

```bash
chmod +x wifi_loss_monitor.sh
```

## Usage (named options, any order)

```bash
sudo ./wifi_loss_monitor.sh --target <ip_or_host> --interval <seconds> --threshold <ms> [--timeout <seconds>] [--no-log] [--log-dir <dir>]
```

## Short options

```bash
-t target
-i interval (seconds)
-T threshold (milliseconds)
-W timeout (seconds) passed to ping -W
-n no-log (console only)
-d log directory
-h help
```


## Examples

Typical roaming test (100ms probes, 600ms limit)
```bash
sudo ./wifi_loss_monitor.sh --target 10.194.240.11 --interval 0.1 --threshold 600
```
- Console-only (no files created)
```bash
sudo ./wifi_loss_monitor.sh -t 10.194.240.11 -i 0.1 -T 600 -n
```
- Custom threshold (700ms)
```bash
sudo ./wifi_loss_monitor.sh -t 1.1.1.1 -i 0.1 -T 700
```
- Adjust per-probe timeout (ping -W)
```bash
sudo ./wifi_loss_monitor.sh -t 10.194.240.11 -i 0.1 -T 600 -W 1
```
- Save logs to a specific directory
```bash
sudo ./wifi_loss_monitor.sh -t 10.194.240.11 -i 0.1 -T 600 -d /tmp
```

## Terminal Output Legend

- ✅ OK — Reply received (icmp_seq, RTT)
- ❌ LOSS — “no answer yet” for a given icmp_seq (requires ping -O)
- ❌ UNREACHABLE — Routing failure reported by an intermediate device
- 🚨 ALERT — loss_window_ms >= threshold_ms
- 🟦 RECOVER — First reply after a loss streak
- ⚠️ INFO — Other ping output lines (banner / misc diagnostics)


## Log Files (when logging is enabled)

- Two files are created in the current directory (or --log-dir):

- - TXT log (ping_<target>_<timestamp>.log)

Includes RAW: lines from ping output for troubleshooting and correlation.

CSV log (ping_<target>_<timestamp>.csv)

Structured event records suitable for analysis and correlation.

Example filenames:

ping_10_194_240_11_20260115_220423.log

ping_10_194_240_11_20260115_220423.csv
