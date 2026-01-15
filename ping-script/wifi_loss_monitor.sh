#!/usr/bin/env bash
set -u -o pipefail

# -----------------------------------------------------------------------------
# Wi-Fi Roaming / Link-Loss Ping Monitor (WSL/Linux)
#
# Named-arg usage (order doesn't matter):
#   sudo ./wifi_loss_monitor.sh --target <ip> --interval <sec> --threshold <ms> [--timeout <sec>] [--no-log] [--log-dir <dir>]
#
# Short options:
#   -t <ip>     target
#   -i <sec>    interval seconds (e.g. 0.1)
#   -T <ms>     threshold ms (e.g. 600)
#   -W <sec>    per-probe timeout seconds (ping -W)
#   -n          no-log (console only)
#   -d <dir>    log directory (default .)
#   -h          help
#
# Also allowed: one bare positional argument as target IP/hostname.
# -----------------------------------------------------------------------------

is_number() { [[ "$1" =~ ^[0-9]+([.][0-9]+)?$ ]]; }
is_int()    { [[ "$1" =~ ^[0-9]+$ ]]; }

TARGET=""
INTERVAL_S="0.1"
THRESHOLD_MS="600"
TIMEOUT_S="1"
NO_LOG=0
LOG_DIR="."

usage() {
  cat <<EOF
Usage:
  sudo $0 --target <ip> --interval <sec> --threshold <ms> [--timeout <sec>] [--no-log] [--log-dir <dir>]
  sudo $0 <ip> --interval <sec> --threshold <ms> [--timeout <sec>] [--no-log] [--log-dir <dir>]

Options:
  -t, --target <ip>         Target IP/hostname
  -i, --interval <sec>      Ping interval in seconds (e.g. 0.1)
  -T, --threshold <ms>      Alert threshold in milliseconds (e.g. 600)
  -W, --timeout <sec>       Per-probe timeout in seconds passed to ping (-W). (e.g. 1)
  -n, --no-log              Console output only (no files created)
  -d, --log-dir <dir>       Directory for logs (default: current dir)
  -h, --help                Show help

Examples:
  sudo $0 --target 1.1.1.1 --interval 0.1 --threshold 700 --timeout 1 --no-log
  sudo $0 10.194.240.11 --interval 0.1 --threshold 600 --timeout 1
EOF
}

# Parse args (flags in any order). Allow one bare positional target.
while (( $# > 0 )); do
  case "$1" in
    -t|--target)
      TARGET="${2:-}"
      shift 2
      ;;
    -i|--interval)
      INTERVAL_S="${2:-}"
      shift 2
      ;;
    -T|--threshold)
      THRESHOLD_MS="${2:-}"
      shift 2
      ;;
    -W|--timeout)
      TIMEOUT_S="${2:-}"
      shift 2
      ;;
    -n|--no-log)
      NO_LOG=1
      shift
      ;;
    -d|--log-dir)
      LOG_DIR="${2:-.}"
      shift 2
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    --)
      shift
      break
      ;;
    -*)
      echo "Unknown option: $1"
      usage
      exit 1
      ;;
    *)
      # bare target (only once)
      if [[ -z "$TARGET" ]]; then
        TARGET="$1"
        shift
      else
        echo "Unexpected extra argument: $1"
        usage
        exit 1
      fi
      ;;
  esac
done

TARGET="${TARGET:-10.194.240.11}"

# Validate
if ! is_number "$INTERVAL_S"; then
  echo "ERROR: --interval must be numeric seconds (e.g. 0.1). Got: $INTERVAL_S"
  exit 2
fi
if ! is_int "$THRESHOLD_MS"; then
  echo "ERROR: --threshold must be an integer ms (e.g. 600). Got: $THRESHOLD_MS"
  exit 2
fi
if ! is_number "$TIMEOUT_S"; then
  echo "ERROR: --timeout must be numeric seconds (e.g. 1). Got: $TIMEOUT_S"
  exit 2
fi

interval_ms() { awk -v i="$1" 'BEGIN { printf("%d", (i*1000)+0.5) }'; }
now_ts() { date '+%Y-%m-%d %H:%M:%S.%3N'; }

GREEN=$'\033[32m'
RED=$'\033[31m'
YELLOW=$'\033[33m'
CYAN=$'\033[36m'
RESET=$'\033[0m'

INTERVAL_MS="$(interval_ms "$INTERVAL_S")"

# Many Linux builds require root for <200ms ping interval
if (( INTERVAL_MS < 200 )) && (( EUID != 0 )); then
  echo "ERROR: interval ${INTERVAL_S}s (~${INTERVAL_MS}ms) usually requires root/capabilities."
  echo "Run with sudo."
  exit 2
fi

# Only define log paths if logging is enabled
LOG_TXT=""
LOG_CSV=""
if (( NO_LOG == 0 )); then
  TAG="$(date +%Y%m%d_%H%M%S)"
  SAFE_TARGET="${TARGET//./_}"
  LOG_TXT="${LOG_DIR%/}/ping_${SAFE_TARGET}_${TAG}.log"
  LOG_CSV="${LOG_DIR%/}/ping_${SAFE_TARGET}_${TAG}.csv"

  mkdir -p "$LOG_DIR"
  echo "timestamp,event,seq,rtt_ms,consecutive_loss,loss_window_ms,message" > "$LOG_CSV"
fi

log_txt() { (( NO_LOG == 1 )) && return 0; echo "$1" >> "$LOG_TXT"; }
log_csv() { (( NO_LOG == 1 )) && return 0; echo "$1" >> "$LOG_CSV"; }

total_ok=0
total_lost=0
consec_lost=0
max_consec_lost=0
alerting=0

PING_PID=""
PING_FD=""
CLEANED_UP=0

cleanup() {
  (( CLEANED_UP == 1 )) && return
  CLEANED_UP=1

  echo
  echo "Stopping..."

  if [[ -n "$PING_FD" ]]; then
    exec {PING_FD}<&- 2>/dev/null || true
  fi
  if [[ -n "${PING_PID}" ]] && kill -0 "${PING_PID}" 2>/dev/null; then
    kill "${PING_PID}" 2>/dev/null || true
  fi

  local endts; endts="$(now_ts)"

  if (( NO_LOG == 0 )); then
    {
      echo "[$endts] --- stopped ---"
      echo "Summary:"
      echo "  target              : $TARGET"
      echo "  interval            : ${INTERVAL_S}s (~${INTERVAL_MS}ms)"
      echo "  threshold           : ${THRESHOLD_MS}ms"
      echo "  timeout             : ${TIMEOUT_S}s"
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
  else
    echo "[$endts] --- stopped ---"
    echo "Summary:"
    echo "  target              : $TARGET"
    echo "  interval            : ${INTERVAL_S}s (~${INTERVAL_MS}ms)"
    echo "  threshold           : ${THRESHOLD_MS}ms"
    echo "  timeout             : ${TIMEOUT_S}s"
    echo "  ok replies          : $total_ok"
    echo "  lost probes         : $total_lost"
    echo "  max consecutive loss: $max_consec_lost (≈ $((max_consec_lost * INTERVAL_MS))ms)"
  fi
}

trap cleanup INT TERM

echo
echo "Monitoring ICMP to ${TARGET}"
echo "  interval : ${INTERVAL_S}s (~${INTERVAL_MS}ms)   threshold : ${THRESHOLD_MS}ms   timeout : ${TIMEOUT_S}s"
if (( NO_LOG == 0 )); then
  echo "  TXT: $LOG_TXT"
  echo "  CSV: $LOG_CSV"
else
  echo "  logging : disabled (--no-log)"
fi
echo "Press Ctrl+C to stop."
echo

log_txt "[$(now_ts)] --- started ---"

# ping -W expects seconds (iputils). Keep it as passed.
coproc PINGPROC { stdbuf -oL ping -n -O -i "$INTERVAL_S" -W "$TIMEOUT_S" "$TARGET" 2>&1; }
PING_PID="$PINGPROC_PID"
PING_FD="${PINGPROC[0]}"

while IFS= read -r line <&"$PING_FD"; do
  ts="$(now_ts)"
  log_txt "[$ts] RAW: $line"

  if [[ "$line" == *"no answer yet"* ]] && [[ "$line" =~ icmp_seq[=[:space:]]*([0-9]+) ]]; then
    seq="${BASH_REMATCH[1]}"
    ((total_lost++))
    ((consec_lost++))
    ((consec_lost > max_consec_lost)) && max_consec_lost="$consec_lost"
    loss_window_ms=$((consec_lost * INTERVAL_MS))

    msg="LOSS icmp_seq=$seq (consecutive=$consec_lost, window≈${loss_window_ms}ms)"
    printf "[%s] %s❌ %s%s\n" "$ts" "$RED" "$msg" "$RESET"
    log_csv "$ts,LOST,$seq,,${consec_lost},${loss_window_ms},\"$msg\""

    if (( loss_window_ms >= THRESHOLD_MS )) && (( alerting == 0 )); then
      alerting=1
      amsg="ALERT: loss window >= ${THRESHOLD_MS}ms (now≈${loss_window_ms}ms)"
      printf "[%s] %s🚨 %s%s\n" "$ts" "$YELLOW" "$amsg" "$RESET"
      log_csv "$ts,ALERT,$seq,,${consec_lost},${loss_window_ms},\"$amsg\""
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
    log_csv "$ts,UNREACHABLE,${seq},,${consec_lost},${loss_window_ms},\"$esc\""

    if (( loss_window_ms >= THRESHOLD_MS )) && (( alerting == 0 )); then
      alerting=1
      amsg="ALERT: loss window >= ${THRESHOLD_MS}ms (now≈${loss_window_ms}ms)"
      printf "[%s] %s🚨 %s%s\n" "$ts" "$YELLOW" "$amsg" "$RESET"
      log_csv "$ts,ALERT,${seq},,${consec_lost},${loss_window_ms},\"$amsg\""
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
      log_csv "$ts,RECOVER,$seq,$rtt,${consec_lost},${loss_window_ms},\"$rmsg\""
      consec_lost=0
      alerting=0
    fi

    ((total_ok++))
    msg="OK icmp_seq=$seq rtt=${rtt}ms"
    printf "[%s] %s✅ %s%s\n" "$ts" "$GREEN" "$msg" "$RESET"
    log_csv "$ts,OK,$seq,$rtt,0,0,\"$msg\""
    continue
  fi

  printf "[%s] %s⚠️  %s%s\n" "$ts" "$YELLOW" "$line" "$RESET"
  esc="${line//\"/\"\"}"
  log_csv "$ts,INFO,,,${consec_lost},$((consec_lost * INTERVAL_MS)),\"$esc\""
done

cleanup

