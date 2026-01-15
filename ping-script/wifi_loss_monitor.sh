# -----------------------------------------------------------------------------
# Wi-Fi Roaming / Link-Loss Ping Monitor (WSL/Linux)
#
# What this script does
# - Sends ICMP echo requests ("ping") to a target at a fast interval (default 0.1s).
# - Designed for moving machines over Wi-Fi where brief outages matter.
# - Detects and highlights outages as "loss windows" and raises an ALERT once
#   consecutive loss reaches a threshold (default 600ms).
# - Prints clear, timestamped events to the terminal:
#     ✅ OK          = reply received (includes icmp_seq and RTT)
#     ❌ LOSS        = "no answer yet" for a given icmp_seq (requires ping -O)
#     ❌ UNREACHABLE = network/host unreachable messages (routing failure)
#     🚨 ALERT       = consecutive loss window >= threshold (e.g. 600ms)
#     🟦 RECOVER     = first reply after a loss streak
#     ⚠️ INFO        = any other ping output lines (banner, misc messages)
#
# Why 100ms interval?
# - With 0.1s probes, each missed probe is ~100ms of "no connectivity" signal.
# - A 600ms outage ≈ 6 consecutive missed probes.
#
# Output files (created in the current directory)
# - TXT log: human-readable, includes RAW ping output lines for correlation.
# - CSV log: machine-friendly, includes timestamps suitable for correlation
#   with syslog/roaming/handoff logs.
#
# CSV columns
#   timestamp            Human timestamp with milliseconds (local time)
#   event                OK | LOST | UNREACHABLE | ALERT | RECOVER | INFO
#   seq                  icmp_seq number when available
#   rtt_ms               RTT in ms for OK/RECOVER rows (when available)
#   consecutive_loss     current streak length (in probes)
#   loss_window_ms       consecutive_loss * interval_ms (approx outage duration)
#   message              short description (or raw line for UNREACHABLE/INFO)
#
# Usage
#   sudo ./wifi_loss_monitor.sh <target_ip> [interval_seconds] [threshold_ms]
#
# Examples
#   sudo ./wifi_loss_monitor.sh 10.194.240.11
#   sudo ./wifi_loss_monitor.sh 10.194.240.11 0.1 600
#   sudo ./wifi_loss_monitor.sh 1.1.1.1 0.1 600
#
# Notes / Requirements
# - Run in WSL/Linux with "iputils-ping" available.
# - Fast intervals (<200ms) often require root permissions:
#     sudo ./wifi_loss_monitor.sh ...
# - The "LOSS" detection relies on: ping -O
# - This is a best-effort approximation of traffic loss using ICMP. ICMP may be
#   treated differently than application traffic in some networks, so correlate
#   with your other logs when validating security requirements.
#
# Stop
# - Press Ctrl+C to stop. A summary is printed and appended to the TXT log.
# -----------------------------------------------------------------------------


#!/usr/bin/env bash
set -u -o pipefail

TARGET="${1:-10.194.240.11}"
INTERVAL_S="${2:-0.1}"
THRESHOLD_MS="${3:-600}"
TIMEOUT_S="1"

interval_ms() { awk -v i="$1" 'BEGIN { printf("%d", (i*1000)+0.5) }'; }
now_ts() { date '+%Y-%m-%d %H:%M:%S.%3N'; }

GREEN=$'\033[32m'
RED=$'\033[31m'
YELLOW=$'\033[33m'
CYAN=$'\033[36m'
RESET=$'\033[0m'

INTERVAL_MS="$(interval_ms "$INTERVAL_S")"

if (( INTERVAL_MS < 200 )) && (( EUID != 0 )); then
  echo "ERROR: interval ${INTERVAL_S}s (~${INTERVAL_MS}ms) usually requires root/capabilities."
  echo "Run: sudo $0 $TARGET $INTERVAL_S $THRESHOLD_MS"
  exit 2
fi

TAG="$(date +%Y%m%d_%H%M%S)"
SAFE_TARGET="${TARGET//./_}"
LOG_TXT="ping_${SAFE_TARGET}_${TAG}.log"
LOG_CSV="ping_${SAFE_TARGET}_${TAG}.csv"

echo "timestamp,event,seq,rtt_ms,consecutive_loss,loss_window_ms,message" > "$LOG_CSV"

total_ok=0
total_lost=0
consec_lost=0
max_consec_lost=0
alerting=0

PING_PID=""
PING_FD=""          # read FD for coproc
CLEANED_UP=0

cleanup() {
  # run once
  (( CLEANED_UP == 1 )) && return
  CLEANED_UP=1

  echo
  echo "Stopping..."

  # Close read FD to unblock read loop if it's still running
  if [[ -n "$PING_FD" ]]; then
    exec {PING_FD}<&- 2>/dev/null || true
  fi

  # Stop ping if still running
  if [[ -n "${PING_PID}" ]] && kill -0 "${PING_PID}" 2>/dev/null; then
    kill "${PING_PID}" 2>/dev/null || true
  fi

  local endts; endts="$(now_ts)"
  {
    echo "[$endts] --- stopped ---"
    echo "Summary:"
    echo "  target              : $TARGET"
    echo "  interval            : ${INTERVAL_S}s (~${INTERVAL_MS}ms)"
    echo "  threshold           : ${THRESHOLD_MS}ms"
    echo "  ok replies          : $total_ok"
    echo "  lost probes         : $total_lost"
    echo "  max consecutive loss: $max_consec_lost (≈ $((max_consec_lost * INTERVAL_MS))ms)"
    echo "Logs:"
    echo "  TXT: $LOG_TXT"
    echo "  CSV: $LOG_CSV"
  } | tee -a "$LOG_TXT" >/dev/null

  echo
  echo "Logs saved:"
  echo "  TXT: $LOG_TXT"
  echo "  CSV: $LOG_CSV"
}

# IMPORTANT: don't trap EXIT (avoids double cleanup)
trap cleanup INT TERM

echo
echo "Monitoring ICMP to ${TARGET}"
echo "  interval : ${INTERVAL_S}s (~${INTERVAL_MS}ms)   threshold : ${THRESHOLD_MS}ms"
echo "  TXT: $LOG_TXT"
echo "  CSV: $LOG_CSV"
echo "Press Ctrl+C to stop."
echo

echo "[$(now_ts)] --- started ---" | tee -a "$LOG_TXT" >/dev/null

coproc PINGPROC { stdbuf -oL ping -n -O -i "$INTERVAL_S" -W "$TIMEOUT_S" "$TARGET" 2>&1; }
PING_PID="$PINGPROC_PID"

# Save the FD into a normal variable so we can safely close it
PING_FD="${PINGPROC[0]}"

# Read ping output in the main shell (no subshell counter issues)
while IFS= read -r line <&"$PING_FD"; do
  ts="$(now_ts)"
  echo "[$ts] RAW: $line" >> "$LOG_TXT"

  if [[ "$line" == *"no answer yet"* ]] && [[ "$line" =~ icmp_seq[=[:space:]]*([0-9]+) ]]; then
    seq="${BASH_REMATCH[1]}"

    ((total_lost++))
    ((consec_lost++))
    ((consec_lost > max_consec_lost)) && max_consec_lost="$consec_lost"
    loss_window_ms=$((consec_lost * INTERVAL_MS))

    msg="LOSS icmp_seq=$seq (consecutive=$consec_lost, window≈${loss_window_ms}ms)"
    printf "[%s] %s❌ %s%s\n" "$ts" "$RED" "$msg" "$RESET"
    echo "$ts,LOST,$seq,,${consec_lost},${loss_window_ms},\"$msg\"" >> "$LOG_CSV"

    if (( loss_window_ms >= THRESHOLD_MS )) && (( alerting == 0 )); then
      alerting=1
      amsg="ALERT: loss window >= ${THRESHOLD_MS}ms (now≈${loss_window_ms}ms)"
      printf "[%s] %s🚨 %s%s\n" "$ts" "$YELLOW" "$amsg" "$RESET"
      echo "$ts,ALERT,$seq,,${consec_lost},${loss_window_ms},\"$amsg\"" >> "$LOG_CSV"
    fi
    continue
  fi

  if [[ "$line" == *"Destination Net Unreachable"* ]] || \
     [[ "$line" == *"Destination Host Unreachable"* ]] || \
     [[ "$line" == *"Network is unreachable"* ]] || \
     [[ "$line" == *"Host Unreachable"* ]]; then

    seq=""
    if [[ "$line" =~ icmp_seq[=[:space:]]*([0-9]+) ]]; then
      seq="${BASH_REMATCH[1]}"
    fi

    ((total_lost++))
    ((consec_lost++))
    ((consec_lost > max_consec_lost)) && max_consec_lost="$consec_lost"
    loss_window_ms=$((consec_lost * INTERVAL_MS))

    msg="UNREACHABLE${seq:+ icmp_seq=$seq} (consecutive=$consec_lost, window≈${loss_window_ms}ms)"
    printf "[%s] %s❌ %s%s\n" "$ts" "$RED" "$msg" "$RESET"

    esc="${line//\"/\"\"}"
    echo "$ts,UNREACHABLE,${seq},,${consec_lost},${loss_window_ms},\"$esc\"" >> "$LOG_CSV"

    if (( loss_window_ms >= THRESHOLD_MS )) && (( alerting == 0 )); then
      alerting=1
      amsg="ALERT: loss window >= ${THRESHOLD_MS}ms (now≈${loss_window_ms}ms)"
      printf "[%s] %s🚨 %s%s\n" "$ts" "$YELLOW" "$amsg" "$RESET"
      echo "$ts,ALERT,${seq},,${consec_lost},${loss_window_ms},\"$amsg\"" >> "$LOG_CSV"
    fi
    continue
  fi

  if [[ "$line" == *"bytes from"* ]] && [[ "$line" =~ icmp_seq[=[:space:]]*([0-9]+) ]]; then
    seq="${BASH_REMATCH[1]}"

    rtt=""
    if [[ "$line" =~ time=([0-9.]+)[[:space:]]*ms ]]; then
      rtt="${BASH_REMATCH[1]}"
    fi

    if (( consec_lost > 0 )); then
      loss_window_ms=$((consec_lost * INTERVAL_MS))
      rmsg="RECOVER: reply after ${consec_lost} lost (≈${loss_window_ms}ms)"
      printf "[%s] %s🟦 %s%s\n" "$ts" "$CYAN" "$rmsg" "$RESET"
      echo "$ts,RECOVER,$seq,$rtt,${consec_lost},${loss_window_ms},\"$rmsg\"" >> "$LOG_CSV"
      consec_lost=0
      alerting=0
    fi

    ((total_ok++))
    msg="OK icmp_seq=$seq rtt=${rtt}ms"
    printf "[%s] %s✅ %s%s\n" "$ts" "$GREEN" "$msg" "$RESET"
    echo "$ts,OK,$seq,$rtt,0,0,\"$msg\"" >> "$LOG_CSV"
    continue
  fi

  printf "[%s] %s⚠️  %s%s\n" "$ts" "$YELLOW" "$line" "$RESET"
  esc="${line//\"/\"\"}"
  echo "$ts,INFO,,,${consec_lost},$((consec_lost * INTERVAL_MS)),\"$esc\"" >> "$LOG_CSV"
done

# If ping exits naturally, run cleanup once (so you still get a summary)
cleanup

