#!/usr/bin/env bash
set -u -o pipefail

# -----------------------------------------------------------------------------
# Link-Loss Ping Monitor
#
# Terminal:
#   Visar varje lyckad ping, varje förlorad ping, alert och återhämtning.
#
# Normal loggning:
#   Loggar endast relevanta händelser:
#     - LAST_OK     Sista fungerande ping före avbrott
#     - LOSS_START  Första förlorade ping
#     - ALERT       När tröskeln uppnås
#     - RECOVER     Första fungerande ping efter avbrott
#
# Med --raw-log:
#   Loggar dessutom varje rå pingrad samt varje OK/LOST-händelse.
#
# Loggfiler roteras automatiskt vid dygnsskifte:
#   ping_<target>_YYYYMMDD.log
#   ping_<target>_YYYYMMDD.csv
# -----------------------------------------------------------------------------

is_number() {
  [[ "$1" =~ ^[0-9]+([.][0-9]+)?$ ]]
}

is_int() {
  [[ "$1" =~ ^[0-9]+$ ]]
}

TARGET=""
INTERVAL_S="0.1"
THRESHOLD_MS="600"
TIMEOUT_S="1"
NO_LOG=0
RAW_LOG=0
LOG_DIR="logs"

usage() {
  cat <<EOF
Usage:
  sudo $0 --target <ip> [options]
  sudo $0 <ip> [options]

Options:
  -t, --target <ip>         Target IP or hostname
  -i, --interval <sec>      Ping interval, default: 0.1
  -T, --threshold <ms>      Alert threshold, default: 600
  -W, --timeout <sec>       Per-probe timeout, default: 1
  -r, --raw-log             Log every ping response and raw ping line
  -n, --no-log              Console only; create no log files
  -d, --log-dir <dir>       Log directory, default: logs
  -h, --help                Show this help

Examples:
  sudo $0 10.194.240.10
  sudo $0 10.194.240.10 --interval 0.1 --threshold 600
  sudo $0 10.194.240.10 --raw-log
EOF
}

# -----------------------------------------------------------------------------
# Parse arguments
# -----------------------------------------------------------------------------

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
    -r|--raw-log)
      RAW_LOG=1
      shift
      ;;
    -n|--no-log)
      NO_LOG=1
      shift
      ;;
    -d|--log-dir)
      LOG_DIR="${2:-}"
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

TARGET="${TARGET:-10.194.240.10}"

# -----------------------------------------------------------------------------
# Validate arguments
# -----------------------------------------------------------------------------

if ! is_number "$INTERVAL_S"; then
  echo "ERROR: --interval must be numeric. Got: $INTERVAL_S"
  exit 2
fi

if ! is_int "$THRESHOLD_MS"; then
  echo "ERROR: --threshold must be an integer. Got: $THRESHOLD_MS"
  exit 2
fi

if ! is_number "$TIMEOUT_S"; then
  echo "ERROR: --timeout must be numeric. Got: $TIMEOUT_S"
  exit 2
fi

if [[ -z "$LOG_DIR" ]]; then
  echo "ERROR: --log-dir cannot be empty."
  exit 2
fi

interval_ms() {
  awk -v i="$1" 'BEGIN { printf("%d", (i * 1000) + 0.5) }'
}

now_ts() {
  date '+%Y-%m-%d %H:%M:%S.%3N'
}

current_log_date() {
  date '+%Y%m%d'
}

csv_escape() {
  local value="${1//\"/\"\"}"
  printf '"%s"' "$value"
}

GREEN=$'\033[32m'
RED=$'\033[31m'
YELLOW=$'\033[33m'
CYAN=$'\033[36m'
RESET=$'\033[0m'

INTERVAL_MS="$(interval_ms "$INTERVAL_S")"

# Many Linux builds require root for ping intervals below 200 ms.
if (( INTERVAL_MS < 200 )) && (( EUID != 0 )); then
  echo "ERROR: interval ${INTERVAL_S}s normally requires root privileges."
  echo "Run the script with sudo."
  exit 2
fi

# -----------------------------------------------------------------------------
# Log handling and daily rotation
# -----------------------------------------------------------------------------

LOG_DATE=""
LOG_TXT=""
LOG_CSV=""
SAFE_TARGET="${TARGET//[^a-zA-Z0-9_-]/_}"

set_log_paths() {
  LOG_DATE="$(current_log_date)"
  LOG_TXT="${LOG_DIR%/}/ping_${SAFE_TARGET}_${LOG_DATE}.log"
  LOG_CSV="${LOG_DIR%/}/ping_${SAFE_TARGET}_${LOG_DATE}.csv"

  mkdir -p "$LOG_DIR"

  if [[ ! -s "$LOG_CSV" ]]; then
    printf '%s\n' \
      "timestamp,event,seq,rtt_ms,consecutive_loss,loss_window_ms,message" \
      > "$LOG_CSV"
  fi
}

rotate_logs_if_needed() {
  (( NO_LOG == 1 )) && return 0

  local new_date
  local rotate_ts
  local old_date
  local old_txt
  local old_csv

  new_date="$(current_log_date)"

  if [[ "$new_date" == "$LOG_DATE" ]]; then
    return 0
  fi

  rotate_ts="$(now_ts)"
  old_date="$LOG_DATE"
  old_txt="$LOG_TXT"
  old_csv="$LOG_CSV"

  if [[ -n "$old_txt" ]]; then
    printf '[%s] LOG_ROTATE: Closing daily log %s\n' \
      "$rotate_ts" "$old_date" >> "$old_txt"
  fi

  if [[ -n "$old_csv" ]]; then
    printf '%s,ROTATE,,,,,%s\n' \
      "$rotate_ts" \
      "$(csv_escape "Closing daily log ${old_date}")" >> "$old_csv"
  fi

  set_log_paths
  rotate_ts="$(now_ts)"

  printf '[%s] LOG_ROTATE: Starting daily log %s\n' \
    "$rotate_ts" "$LOG_DATE" >> "$LOG_TXT"

  printf '%s,ROTATE,,,,,%s\n' \
    "$rotate_ts" \
    "$(csv_escape "Starting daily log ${LOG_DATE}")" >> "$LOG_CSV"

  printf '[%s] %sDaily log rotation%s\n' \
    "$rotate_ts" "$CYAN" "$RESET"

  printf '  TXT: %s\n' "$LOG_TXT"
  printf '  CSV: %s\n' "$LOG_CSV"
}

log_txt() {
  (( NO_LOG == 1 )) && return 0

  rotate_logs_if_needed
  printf '%s\n' "$1" >> "$LOG_TXT"
}

log_csv() {
  (( NO_LOG == 1 )) && return 0

  rotate_logs_if_needed
  printf '%s\n' "$1" >> "$LOG_CSV"
}

log_event() {
  local timestamp="$1"
  local event="$2"
  local seq="${3:-}"
  local rtt="${4:-}"
  local consecutive="${5:-}"
  local window="${6:-}"
  local message="${7:-}"

  log_txt "[$timestamp] $event: $message"

  log_csv \
    "$timestamp,$event,$seq,$rtt,$consecutive,$window,$(csv_escape "$message")"
}

if (( NO_LOG == 0 )); then
  set_log_paths
fi

# -----------------------------------------------------------------------------
# Runtime state
# -----------------------------------------------------------------------------

total_ok=0
total_lost=0
consec_lost=0
max_consec_lost=0
alerting=0
in_loss=0

last_ok_ts=""
last_ok_seq=""
last_ok_rtt=""

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

  if [[ -n "$PING_PID" ]] && kill -0 "$PING_PID" 2>/dev/null; then
    kill "$PING_PID" 2>/dev/null || true
  fi

  local endts
  local summary

  endts="$(now_ts)"

  summary="target=$TARGET, ok=$total_ok, lost=$total_lost, max_consecutive=$max_consec_lost, max_window≈$((max_consec_lost * INTERVAL_MS))ms"

  if (( NO_LOG == 0 )); then
    rotate_logs_if_needed
    log_event "$endts" "STOPPED" "" "" "" "" "$summary"
  fi

  echo "[$endts] --- stopped ---"
  echo "Summary:"
  echo "  target              : $TARGET"
  echo "  interval            : ${INTERVAL_S}s (~${INTERVAL_MS}ms)"
  echo "  threshold           : ${THRESHOLD_MS}ms"
  echo "  timeout             : ${TIMEOUT_S}s"
  echo "  ok replies          : $total_ok"
  echo "  lost probes         : $total_lost"
  echo "  max consecutive loss: $max_consec_lost"
  echo "  max loss window     : ≈$((max_consec_lost * INTERVAL_MS))ms"

  if (( NO_LOG == 0 )); then
    echo
    echo "Logs saved:"
    echo "  TXT: $LOG_TXT"
    echo "  CSV: $LOG_CSV"
  fi
}

trap cleanup INT TERM

# -----------------------------------------------------------------------------
# Startup information
# -----------------------------------------------------------------------------

echo
echo "Monitoring ICMP to $TARGET"
echo "  interval : ${INTERVAL_S}s (~${INTERVAL_MS}ms)"
echo "  threshold: ${THRESHOLD_MS}ms"
echo "  timeout  : ${TIMEOUT_S}s"

if (( NO_LOG == 1 )); then
  echo "  logging  : disabled"
elif (( RAW_LOG == 1 )); then
  echo "  logging  : raw/full"
  echo "  TXT      : $LOG_TXT"
  echo "  CSV      : $LOG_CSV"
  echo "  rotation : daily"
else
  echo "  logging  : event only"
  echo "  TXT      : $LOG_TXT"
  echo "  CSV      : $LOG_CSV"
  echo "  rotation : daily"
fi

echo "Press Ctrl+C to stop."
echo

start_ts="$(now_ts)"

if (( RAW_LOG == 1 )); then
  mode_text="raw/full logging"
else
  mode_text="event-only logging"
fi

log_event \
  "$start_ts" \
  "STARTED" \
  "" "" "" "" \
  "Monitoring $TARGET using ${mode_text}"

# -----------------------------------------------------------------------------
# Start ping process
# -----------------------------------------------------------------------------

coproc PINGPROC {
  stdbuf -oL ping \
    -n \
    -O \
    -i "$INTERVAL_S" \
    -W "$TIMEOUT_S" \
    "$TARGET" 2>&1
}

PING_PID="$PINGPROC_PID"
PING_FD="${PINGPROC[0]}"

# -----------------------------------------------------------------------------
# Process ping output
# -----------------------------------------------------------------------------

while IFS= read -r line <&"$PING_FD"; do
  ts="$(now_ts)"

  if (( RAW_LOG == 1 )); then
    log_event \
      "$ts" \
      "RAW" \
      "" \
      "" \
      "$consec_lost" \
      "$((consec_lost * INTERVAL_MS))" \
      "$line"
  fi

  # ---------------------------------------------------------------------------
  # Lost ping reported by ping -O
  # ---------------------------------------------------------------------------

  if [[ "$line" == *"no answer yet"* ]] &&
     [[ "$line" =~ icmp_seq[=[:space:]]*([0-9]+) ]]; then

    seq="${BASH_REMATCH[1]}"

    ((total_lost++))
    ((consec_lost++))

    if (( consec_lost > max_consec_lost )); then
      max_consec_lost="$consec_lost"
    fi

    loss_window_ms=$((consec_lost * INTERVAL_MS))

    # Terminal output is always shown.
    terminal_msg="LOSS icmp_seq=$seq (consecutive=$consec_lost, window≈${loss_window_ms}ms)"

    printf '[%s] %s❌ %s%s\n' \
      "$ts" "$RED" "$terminal_msg" "$RESET"

    if (( in_loss == 0 )); then
      in_loss=1

      if [[ -n "$last_ok_ts" ]]; then
        last_msg="Last reply before loss: icmp_seq=$last_ok_seq rtt=${last_ok_rtt}ms"

        log_event \
          "$last_ok_ts" \
          "LAST_OK" \
          "$last_ok_seq" \
          "$last_ok_rtt" \
          "0" \
          "0" \
          "$last_msg"
      fi

      loss_msg="Loss started at icmp_seq=$seq"

      log_event \
        "$ts" \
        "LOSS_START" \
        "$seq" \
        "" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$loss_msg"

    elif (( RAW_LOG == 1 )); then
      loss_msg="Lost ping icmp_seq=$seq"

      log_event \
        "$ts" \
        "LOST" \
        "$seq" \
        "" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$loss_msg"
    fi

    if (( loss_window_ms >= THRESHOLD_MS )) && (( alerting == 0 )); then
      alerting=1

      alert_msg="Loss window reached approximately ${loss_window_ms}ms"

      printf '[%s] %s🚨 ALERT: loss window >= %sms (now≈%sms)%s\n' \
        "$ts" \
        "$YELLOW" \
        "$THRESHOLD_MS" \
        "$loss_window_ms" \
        "$RESET"

      log_event \
        "$ts" \
        "ALERT" \
        "$seq" \
        "" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$alert_msg"
    fi

    continue
  fi

  # ---------------------------------------------------------------------------
  # Unreachable responses
  # ---------------------------------------------------------------------------

  if [[ "$line" == *"Destination Net Unreachable"* ]] ||
     [[ "$line" == *"Destination Host Unreachable"* ]] ||
     [[ "$line" == *"Network is unreachable"* ]] ||
     [[ "$line" == *"Host Unreachable"* ]]; then

    seq=""

    if [[ "$line" =~ icmp_seq[=[:space:]]*([0-9]+) ]]; then
      seq="${BASH_REMATCH[1]}"
    fi

    ((total_lost++))
    ((consec_lost++))

    if (( consec_lost > max_consec_lost )); then
      max_consec_lost="$consec_lost"
    fi

    loss_window_ms=$((consec_lost * INTERVAL_MS))

    terminal_msg="UNREACHABLE${seq:+ icmp_seq=$seq} (consecutive=$consec_lost, window≈${loss_window_ms}ms)"

    printf '[%s] %s❌ %s%s\n' \
      "$ts" "$RED" "$terminal_msg" "$RESET"

    if (( in_loss == 0 )); then
      in_loss=1

      if [[ -n "$last_ok_ts" ]]; then
        last_msg="Last reply before loss: icmp_seq=$last_ok_seq rtt=${last_ok_rtt}ms"

        log_event \
          "$last_ok_ts" \
          "LAST_OK" \
          "$last_ok_seq" \
          "$last_ok_rtt" \
          "0" \
          "0" \
          "$last_msg"
      fi

      log_event \
        "$ts" \
        "LOSS_START" \
        "$seq" \
        "" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$line"

    elif (( RAW_LOG == 1 )); then
      log_event \
        "$ts" \
        "UNREACHABLE" \
        "$seq" \
        "" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$line"
    fi

    if (( loss_window_ms >= THRESHOLD_MS )) && (( alerting == 0 )); then
      alerting=1

      alert_msg="Loss window reached approximately ${loss_window_ms}ms"

      printf '[%s] %s🚨 ALERT: loss window >= %sms (now≈%sms)%s\n' \
        "$ts" \
        "$YELLOW" \
        "$THRESHOLD_MS" \
        "$loss_window_ms" \
        "$RESET"

      log_event \
        "$ts" \
        "ALERT" \
        "$seq" \
        "" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$alert_msg"
    fi

    continue
  fi

  # ---------------------------------------------------------------------------
  # Successful ping
  # ---------------------------------------------------------------------------

  if [[ "$line" == *"bytes from"* ]] &&
     [[ "$line" =~ icmp_seq[=[:space:]]*([0-9]+) ]]; then

    seq="${BASH_REMATCH[1]}"
    rtt=""

    if [[ "$line" =~ time[=\<]([0-9.]+)[[:space:]]*ms ]]; then
      rtt="${BASH_REMATCH[1]}"
    fi

    ((total_ok++))

    # Terminal output is always shown, regardless of log mode.
    ok_msg="OK icmp_seq=$seq rtt=${rtt}ms"

    printf '[%s] %s✅ %s%s\n' \
      "$ts" "$GREEN" "$ok_msg" "$RESET"

    if (( in_loss == 1 )); then
      loss_window_ms=$((consec_lost * INTERVAL_MS))

      recover_msg="First reply after ${consec_lost} lost ping(s), outage≈${loss_window_ms}ms, rtt=${rtt}ms"

      printf '[%s] %s🟦 RECOVER: reply after %s lost (≈%sms)%s\n' \
        "$ts" \
        "$CYAN" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$RESET"

      log_event \
        "$ts" \
        "RECOVER" \
        "$seq" \
        "$rtt" \
        "$consec_lost" \
        "$loss_window_ms" \
        "$recover_msg"

      consec_lost=0
      alerting=0
      in_loss=0

    elif (( RAW_LOG == 1 )); then
      log_event \
        "$ts" \
        "OK" \
        "$seq" \
        "$rtt" \
        "0" \
        "0" \
        "$ok_msg"
    fi

    last_ok_ts="$ts"
    last_ok_seq="$seq"
    last_ok_rtt="$rtt"

    continue
  fi

  # ---------------------------------------------------------------------------
  # Unexpected ping output
  # ---------------------------------------------------------------------------

  printf '[%s] %s⚠️  %s%s\n' \
    "$ts" "$YELLOW" "$line" "$RESET"

  log_event \
    "$ts" \
    "INFO" \
    "" \
    "" \
    "$consec_lost" \
    "$((consec_lost * INTERVAL_MS))" \
    "$line"
done

cleanup
