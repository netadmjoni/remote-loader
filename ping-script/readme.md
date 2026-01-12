
# Continuous Ping Logger for Windows (PowerShell)

This PowerShell script provides a continuous ping monitor and packet loss logger for Windows environments. It is designed for network diagnostics and long-running connectivity tests (e.g. field technician use, site monitoring, wireless link validation, etc.).

## 📌 Features

- Continuous `ping.exe` execution with 100ms interval
- Tracks **successful replies** and **timeouts**
- Automatically timestamps each ping result (millisecond resolution)
- Detects and logs **packet loss**
- Logs output to:
  - 📝 Plain-text `.txt` file (for humans)
  - 📊 `.csv` file (for analysis/graphing in Excel, Power BI, Python, etc.)
- Gracefully stops with `Ctrl+C`
- Minimal output, no duplication in console

---

## ⚙️ How It Works

- Launches `ping.exe -t` in the background.
- Parses each output line using a streaming reader.
- Each successful ping is tagged with:
  - Timestamp
  - Response time
  - Sequence number
- Missed replies (timeouts) are tracked using a manual sequence counter and logged with approximate delay.
- Outputs are flushed in real time to:
  - Console
  - A timestamped `.txt` log file
  - A `.csv` log file for structured analysis

---

## ✅ Output Example

[2026-01-12 09:23:41.207] ✅ Reply from 1.1.1.1: bytes=32 time=58ms TTL=51

[2026-01-12 09:23:42.222] ❌ Timeout seq=10 (~1997ms)

### 📂 Example files created:
- `PingLog_20260112_092300.txt`
- `PingLog_20260112_092300.csv`

---

## 🖥️ Usage

1. Open PowerShell
2. Run the script:
   ```powershell
   .\ping.ps1
Press Ctrl+C to stop the test and finalize the log files.

## 🔧 Customization

You can modify the following variables at the top of the script:
```powershell
$target   = "1.1.1.1"    # Target IP or hostname
$interval = 100          # Delay between pings in milliseconds
```
## 📊 CSV Format

The CSV file includes:
```powershell
Timestamp	Status	Sequence	Message	DelayMs
2026-01-12 09:23:41.207	OK	5	Reply from 1.1.1.1: bytes=32 ...	102
2026-01-12 09:23:42.222	TIMEOUT	6		1997
```
This is ideal for post-analysis in tools like Excel or data processing scripts.

## 📁 Requirements

- Windows PowerShell (tested on Windows 10/11)
- No external dependencies
- Uses native ping.exe

