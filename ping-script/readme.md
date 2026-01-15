# Wi-Fi Roaming / Link-Loss Ping Monitor (WSL/Linux)

This script is a fast-interval ping monitor intended for diagnosing brief connectivity drops (e.g., roaming events) when pinging a moving machine over Wi-Fi. It emits **clear, timestamped terminal output** and writes **TXT + CSV logs** with **human timestamps (ms precision)** for correlation with other logs (syslog, roaming/handoff logs, etc.).

---

## What it does

- Sends ICMP echo requests to a target at a fast interval (default **0.1s / 100ms**).
- Detects and highlights:
  - **Packet loss / no reply** (via `ping -O` “no answer yet” lines)
  - **Unreachable conditions** (e.g., `Destination Net Unreachable`)
- Tracks **consecutive loss** and computes a **loss window**:
  - `loss_window_ms ≈ consecutive_loss * interval_ms`
- Raises an **ALERT** once the loss window reaches a configured threshold (default **600ms**).
- Prints timestamped events to the terminal and logs everything to disk.

---

## Why 100ms probes?

If you send a probe every 100ms, then each missed probe is roughly **100ms** of missed connectivity signal.

Example:  
- **600ms maximum outage** requirement  
- ≈ **6 consecutive missed probes** at 100ms interval

This makes it easy to see when you exceed your allowed “traffic loss” budget.

---

## Terminal output legend

- ✅ **OK** — Reply received (includes `icmp_seq` and RTT)
- ❌ **LOSS** — “no answer yet” for an `icmp_seq` (requires `ping -O`)
- ❌ **UNREACHABLE** — Routing failure (`Destination ... Unreachable`, etc.)
- 🚨 **ALERT** — Loss window ≥ threshold (e.g., 600ms)
- 🟦 **RECOVER** — First reply after a loss streak
- ⚠️ **INFO** — Other ping output (banner / misc), still logged for reference

---

## Files created

The script writes two timestamped files in the current directory:

- **TXT log**: human-readable, includes `RAW:` ping output lines (great for correlation)
- **CSV log**: structured events for analysis

Example filenames:

- `ping_10_194_240_11_20260115_220423.log`
- `ping_10_194_240_11_20260115_220423.csv`

---

## CSV format

Columns:

| Column | Meaning |
|---|---|
| `timestamp` | Human timestamp with milliseconds (local time) |
| `event` | `OK`, `LOST`, `UNREACHABLE`, `ALERT`, `RECOVER`, `INFO` |
| `seq` | ICMP sequence number when available |
| `rtt_ms` | RTT in ms for `OK/RECOVER` rows (when available) |
| `consecutive_loss` | Current loss streak length (in probes) |
| `loss_window_ms` | `consecutive_loss * interval_ms` (approx outage duration) |
| `message` | Short description or raw ping line |

---

## Usage

> **Tip:** On many systems, intervals <200ms require `sudo`.

```bash
sudo ./wifi_loss_monitor.sh <target_ip> [interval_seconds] [threshold_ms]
```

## Examples
```bash
sudo ./wifi_loss_monitor.sh 10.194.240.11
sudo ./wifi_loss_monitor.sh 10.194.240.11 0.1 600
sudo ./wifi_loss_monitor.sh 1.1.1.1 0.1 600
```

## Requirements / Notes

- Intended for WSL/Linux with iputils-ping installed.
- Fast intervals (like 0.1s) often require elevated permissions:
- run with sudo, or configure capabilities for ping.
- LOSS detection relies on ping -O, which prints “no answer yet” lines.
- ICMP is a useful proxy for connectivity, but some networks treat ICMP differently from application traffic. Always correlate with your other logs when validating strict requirements.
