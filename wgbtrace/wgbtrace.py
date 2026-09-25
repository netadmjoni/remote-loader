#!/usr/bin/env python3
"""Conservative offline parser for Cisco WGB event logs."""

import argparse
import json
import re
import sys
from dataclasses import asdict, dataclass, replace
from datetime import datetime, timedelta
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple


VERSION = "0.1.0"

CISCO_LINE_RE = re.compile(
    r"\[\*?(?P<timestamp>\d{2}/\d{2}/\d{4}\s+\d{2}:\d{2}:\d{2}(?:\.\d+)?)\]"
    r"\s*(?P<module>[A-Za-z0-9_.-]+):(?P<level>\d+)\s*(?P<message>.*)$"
)
ISO_LINE_RE = re.compile(
    r"^\s*(?P<timestamp>\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?)"
    r"\s+(?:(?P<module>[A-Za-z0-9_.-]+):(?P<level>\d+)\s+)?(?P<message>.*)$"
)
MODULE_LINE_RE = re.compile(
    r"^\s*(?P<module>[A-Za-z0-9_.-]+):(?P<level>\d+)\s+(?P<message>.*)$"
)
RADIO_RE = re.compile(r"(?:^|\s)R(?P<radio>\d+)(?=\s|$)", re.IGNORECASE)
MAC_RE = re.compile(r"\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b")
STATE_RE = re.compile(
    r"\bState\s+(?P<from>[A-Z][A-Z0-9_-]*)\s+to\s+(?P<to>[A-Z][A-Z0-9_-]*)\b",
    re.IGNORECASE,
)
INTERESTING_RE = re.compile(
    r"\b(fail(?:ure|ed)?|reason|disconnect(?:ed)?|delete|timeout|deauth|mismatch|abort(?:ed)?|auth|assoc|error)\b",
    re.IGNORECASE,
)


@dataclass(frozen=True)
class TraceEvent:
    line_number: int
    timestamp: Optional[datetime]
    timestamp_text: Optional[str]
    timestamp_inherited: bool
    module: Optional[str]
    level: Optional[int]
    radio: Optional[int]
    event_type: str
    fields: Dict[str, object]
    message: str
    raw: str

    def as_json(self) -> str:
        data = asdict(self)
        data["timestamp"] = self.timestamp_text
        return json.dumps(data, ensure_ascii=True, separators=(",", ":"))


def _parse_timestamp(value: str) -> Tuple[datetime, str]:
    match = re.fullmatch(
        r"(?P<a>\d{2,4})[-/](?P<b>\d{2})[-/](?P<c>\d{2,4})[ T]"
        r"(?P<hour>\d{2}):(?P<minute>\d{2}):(?P<second>\d{2})(?:\.(?P<fraction>\d+))?",
        value,
    )
    if not match:
        raise ValueError("unsupported timestamp")

    a = match.group("a")
    b = match.group("b")
    c = match.group("c")
    if len(a) == 4:
        year, month, day = int(a), int(b), int(c)
    elif len(c) == 4:
        month, day, year = int(a), int(b), int(c)
    else:
        raise ValueError("timestamp must include a four-digit year")

    fraction = match.group("fraction") or ""
    microseconds = int((fraction[:6]).ljust(6, "0")) if fraction else 0
    parsed = datetime(
        year,
        month,
        day,
        int(match.group("hour")),
        int(match.group("minute")),
        int(match.group("second")),
        microseconds,
    )
    canonical = parsed.strftime("%Y-%m-%d %H:%M:%S")
    if fraction:
        canonical += "." + fraction
    return parsed, canonical


def _extract_status(message: str) -> Optional[int]:
    match = re.search(
        r"\bstatus(?:\s+(?:code\s*)?)?\s*(?:[:=]|is)?\s*\(?(-?\d+)\)?",
        message,
        re.IGNORECASE,
    )
    return int(match.group(1)) if match else None


def _extract_reason(message: str) -> Optional[str]:
    match = re.search(r"\breason\s*\(([^)]+)\)", message, re.IGNORECASE)
    if not match:
        match = re.search(r"\breason\s*(?:[:=]|is)\s*([^,;]+)", message, re.IGNORECASE)
    return match.group(1).strip() if match else None


def _classify(message: str) -> Tuple[str, Dict[str, object]]:
    payload = re.sub(r"^\s*R\d+\s+", "", message, count=1, flags=re.IGNORECASE).strip()

    state = STATE_RE.search(payload)
    if state:
        from_state = state.group("from").upper()
        to_state = state.group("to").upper()
        event_type = {
            "SCAN_START": "SCAN_START",
            "SCAN_DONE": "SCAN_DONE",
            "AUTH": "AUTH_START",
            "AUTH_START": "AUTH_START",
            "EAPOL": "EAPOL_START",
            "CONNECTED": "CONNECTED",
        }.get(to_state, "STATE_CHANGE")
        return event_type, {"from_state": from_state, "to_state": to_state}

    if re.search(r"\bnew\s+best\s+AP\s+not\s+better\s+enough\b", payload, re.IGNORECASE):
        return "ROAM_ABORT", {"reason": "NEW_BEST_NOT_BETTER_ENOUGH"}

    if re.search(r"\bRSSI\s+Low\b.*\bRoaming\s+triggered\b", payload, re.IGNORECASE):
        fields: Dict[str, object] = {"reason": "RSSI_LOW"}
        rssi = re.search(r"\bRSSI\s+Low\s*\(\s*(-?\d+)", payload, re.IGNORECASE)
        if rssi:
            fields["rssi"] = int(rssi.group(1))
        return "ROAM_TRIGGER", fields

    if re.search(r"\bBest\s+AP\b", payload, re.IGNORECASE):
        fields = {}
        mac = MAC_RE.search(payload)
        channel = re.search(r"\b(?:CH|CHANNEL)\s*[:=]\s*(\d+)", payload, re.IGNORECASE)
        rssi = re.search(r"\bRSSI\s*[:=]\s*(-?\d+)", payload, re.IGNORECASE)
        if mac:
            fields["bssid"] = mac.group(0).upper()
        if channel:
            fields["channel"] = int(channel.group(1))
        if rssi:
            fields["rssi"] = int(rssi.group(1))
        return "BEST_AP", fields

    if re.search(r"\bScan\s+(?:Started|Start)\b", payload, re.IGNORECASE):
        return "SCAN_START", {}
    if re.search(r"\bScan\s+done\b.*\bStarting\s+Auth\b", payload, re.IGNORECASE):
        return "AUTH_START", {"scan": "done"}
    if re.search(r"\bScan\s+(?:done|complete(?:d)?)\b", payload, re.IGNORECASE):
        return "SCAN_DONE", {}

    if re.search(r"\bAuth(?:entication)?\s+(?:request|req)\s+sent\b", payload, re.IGNORECASE):
        fields = {}
        mac = MAC_RE.search(payload)
        if mac:
            fields["bssid"] = mac.group(0).upper()
        return "AUTH_START", fields
    if re.search(r"\bAuth(?:entication)?\s+(?:Resp|Response)\b", payload, re.IGNORECASE):
        status = _extract_status(payload)
        fields = {"status": status} if status is not None else {}
        if status == 0 or re.search(r"\bsuccess(?:ful)?\b", payload, re.IGNORECASE):
            return "AUTH_SUCCESS", fields
        if status is not None or re.search(r"\bfail(?:ure|ed)?\b", payload, re.IGNORECASE):
            return "AUTH_FAILURE", fields
        return "UNCLASSIFIED_INTERESTING", {"text": payload}

    if re.search(r"\bAssoc(?:iation)?\s+(?:request|req)\b", payload, re.IGNORECASE):
        return "ASSOC_START", {}
    if re.search(r"\bAssoc(?:iation)?\s+(?:Resp|Response)\b", payload, re.IGNORECASE):
        status = _extract_status(payload)
        fields = {"status": status} if status is not None else {}
        if status == 0 or re.search(r"\bsuccess(?:ful)?\b", payload, re.IGNORECASE):
            return "ASSOC_SUCCESS", fields
        if status is not None or re.search(r"\bfail(?:ure|ed)?\b", payload, re.IGNORECASE):
            return "ASSOC_FAILURE", fields
        return "UNCLASSIFIED_INTERESTING", {"text": payload}

    if re.search(r"\bEAPOL[ ._-]*M1\b", payload, re.IGNORECASE):
        return "EAPOL_START", {"message": 1}
    if re.search(r"\bEAPOL[ ._-]*M4\b", payload, re.IGNORECASE):
        return "EAPOL_COMPLETE", {"message": 4}

    if re.search(r"\bDisconnected\s+client\b", payload, re.IGNORECASE):
        fields = {}
        reason = _extract_reason(payload)
        mac = MAC_RE.search(payload)
        if reason:
            fields["reason"] = reason
        if mac:
            fields["mac"] = mac.group(0).upper()
        return "DISCONNECTED", fields

    if re.search(r"\bDelete\s+client\b", payload, re.IGNORECASE):
        fields = {}
        mac = MAC_RE.search(payload)
        if mac:
            fields["mac"] = mac.group(0).upper()
        return "DELETE_CLIENT", fields

    if re.search(r"\bauth(?:entication)?\b.*\bfail(?:ure|ed)?\b", payload, re.IGNORECASE):
        return "AUTH_FAILURE", {"text": payload}
    if re.search(r"\bassoc(?:iation)?\b.*\bfail(?:ure|ed)?\b", payload, re.IGNORECASE):
        return "ASSOC_FAILURE", {"text": payload}

    if INTERESTING_RE.search(payload):
        return "UNCLASSIFIED_INTERESTING", {"text": payload}
    return "RAW", {}


def parse_line(raw_line: str, line_number: int) -> TraceEvent:
    raw = raw_line.rstrip("\r\n")
    timestamp = None
    timestamp_text = None
    module = None
    level = None
    message = raw.strip()

    match = CISCO_LINE_RE.search(raw)
    if not match:
        match = ISO_LINE_RE.match(raw)

    if match:
        try:
            timestamp, timestamp_text = _parse_timestamp(match.group("timestamp"))
        except ValueError:
            timestamp = None
            timestamp_text = match.group("timestamp")
        module = match.groupdict().get("module")
        level_text = match.groupdict().get("level")
        level = int(level_text) if level_text is not None else None
        message = match.group("message").strip()
    else:
        module_match = MODULE_LINE_RE.match(raw)
        if module_match:
            module = module_match.group("module")
            level = int(module_match.group("level"))
            message = module_match.group("message").strip()

    radio_match = RADIO_RE.search(message)
    radio = int(radio_match.group("radio")) if radio_match else None
    event_type, fields = _classify(message)
    return TraceEvent(
        line_number=line_number,
        timestamp=timestamp,
        timestamp_text=timestamp_text,
        timestamp_inherited=False,
        module=module,
        level=level,
        radio=radio,
        event_type=event_type,
        fields=fields,
        message=message,
        raw=raw,
    )


def parse_lines(lines: Iterable[str]) -> List[TraceEvent]:
    events: List[TraceEvent] = []
    last_timestamp = None
    last_timestamp_text = None
    for line_number, raw_line in enumerate(lines, start=1):
        event = parse_line(raw_line, line_number)
        if event.timestamp is not None:
            last_timestamp = event.timestamp
            last_timestamp_text = event.timestamp_text
        elif last_timestamp is not None and event.raw.strip():
            event = replace(
                event,
                timestamp=last_timestamp,
                timestamp_text=last_timestamp_text,
                timestamp_inherited=True,
            )
        events.append(event)
    return events


def load_events(path: Path) -> List[TraceEvent]:
    with path.open("r", encoding="utf-8", errors="replace") as handle:
        return parse_lines(handle)


def select_around(events: Iterable[TraceEvent], center: datetime, window: float) -> List[TraceEvent]:
    radius = timedelta(seconds=window)
    return [
        event
        for event in events
        if event.timestamp is not None and abs(event.timestamp - center) <= radius
    ]


def _format_value(value: object) -> str:
    text = str(value)
    if not text or any(character.isspace() for character in text):
        return json.dumps(text, ensure_ascii=True)
    return text


def format_event(event: TraceEvent) -> str:
    timestamp = event.timestamp_text or "NO_TIMESTAMP"
    radio = " R{}".format(event.radio) if event.radio is not None else ""
    details = ["line={}".format(event.line_number)]
    details.extend("{}={}".format(key, _format_value(value)) for key, value in event.fields.items())
    if event.event_type == "RAW":
        details.append("text={}".format(json.dumps(event.raw, ensure_ascii=True)))
    return "{}{} {} {}".format(timestamp, radio, event.event_type, " ".join(details)).rstrip()


def _parse_cli_timestamp(value: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value.replace("T", " "))
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            "use an ISO timestamp such as 2026-06-23 11:07:57"
        ) from exc
    if parsed.tzinfo is not None:
        raise argparse.ArgumentTypeError("timezone offsets are not supported")
    return parsed


def _non_negative_float(value: str) -> float:
    try:
        parsed = float(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("must be a number") from exc
    if parsed < 0:
        raise argparse.ArgumentTypeError("must be zero or greater")
    return parsed


def build_argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Parse a captured Cisco WGB event log without changing the WGB."
    )
    parser.add_argument("logfile", type=Path, help="captured output from 'show wgb event all'")
    parser.add_argument(
        "--around",
        type=_parse_cli_timestamp,
        help="show parsed and raw lines around an ISO timestamp",
    )
    parser.add_argument(
        "--window",
        type=_non_negative_float,
        default=5.0,
        metavar="SECONDS",
        help="seconds before and after --around (default: 5)",
    )
    parser.add_argument(
        "--all-lines",
        action="store_true",
        help="include ordinary RAW lines in timeline output",
    )
    parser.add_argument(
        "--jsonl",
        action="store_true",
        help="write every selected line as JSON Lines, including RAW lines",
    )
    parser.add_argument("--version", action="version", version="wgbtrace {}".format(VERSION))
    return parser


def main(argv: Optional[List[str]] = None) -> int:
    parser = build_argument_parser()
    args = parser.parse_args(argv)
    try:
        events = load_events(args.logfile)
    except OSError as exc:
        print("wgbtrace: cannot read {}: {}".format(args.logfile, exc), file=sys.stderr)
        return 2

    selected = select_around(events, args.around, args.window) if args.around else events
    if args.jsonl:
        for event in selected:
            print(event.as_json())
        return 0

    visible = [event for event in selected if args.all_lines or event.event_type != "RAW"]
    if args.around:
        print("Parsed events:")
        if visible:
            for event in visible:
                print(format_event(event))
        else:
            print("(none)")
        print("\nRaw log:")
        if selected:
            for event in selected:
                print("{:06d} {}".format(event.line_number, event.raw))
        else:
            print("(none)")
        return 0

    for event in visible:
        print(format_event(event))
    return 0


if __name__ == "__main__":
    sys.exit(main())
