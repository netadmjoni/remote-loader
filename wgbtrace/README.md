# wgbtrace

`wgbtrace` is a conservative offline parser for event logs captured from Cisco
C9167 WGB devices. It does not connect to or change the WGB.

Version: `0.1.0`

The first version recognizes a deliberately small event set and keeps every
original input line. Unknown lines containing potentially important words are
shown as `UNCLASSIFIED_INTERESTING`; no Cisco reason-code meaning is guessed.

## Requirements

- Python 3.9 or later
- No third-party packages

## Input

Capture the complete output from the read-only command:

```text
show wgb event all
```

A useful file name is:

```text
WGB-NAME-wgb-events-YYYYMMDD-HHMMSS.txt
```

The parser supports Ciscos documented event format:

```text
[*MM/DD/YYYY HH:MM:SS.fraction] MODULE:LEVEL R1 event text
```

An optional syslog prefix before the bracket is accepted. The timestamp inside
the WGB event is used for the timeline.

## Usage

Readable timeline of recognized and interesting events:

```powershell
python .\wgbtrace.py C:\Logs\WGB01-wgb-events-20260623-110800.txt
```

Parsed events and original lines within five seconds on either side of a time:

```powershell
python .\wgbtrace.py C:\Logs\WGB01.txt --around "2026-06-23 11:07:57" --window 5
```

Machine-readable output containing every input line, including ordinary raw
lines:

```powershell
python .\wgbtrace.py C:\Logs\WGB01.txt --jsonl > C:\Logs\WGB01.jsonl
```

Show ordinary `RAW` lines in the text timeline:

```powershell
python .\wgbtrace.py C:\Logs\WGB01.txt --all-lines
```

Version:

```powershell
python .\wgbtrace.py --version
```

## Event types in v0.1

- `SCAN_START`
- `SCAN_DONE`
- `BEST_AP`
- `AUTH_START`
- `AUTH_SUCCESS`
- `AUTH_FAILURE`
- `ASSOC_START`
- `ASSOC_SUCCESS`
- `ASSOC_FAILURE`
- `EAPOL_START`
- `EAPOL_COMPLETE`
- `CONNECTED`
- `ROAM_TRIGGER`
- `ROAM_ABORT`
- `DISCONNECTED`
- `DELETE_CLIENT`
- `STATE_CHANGE`
- `UNCLASSIFIED_INTERESTING`
- `RAW`

`status=0` is treated as success for authentication and association responses.
Other numeric status values are retained and classified as failure, but are not
given a textual meaning.

## Raw-data guarantee

Every parsed record contains:

- original line number
- exact original line in `raw`
- parsed WGB timestamp when available
- module and numeric level when available
- radio when available
- conservative event type and extracted fields

Text timeline output hides ordinary `RAW` records by default to remain readable.
`--jsonl`, `--all-lines`, and the raw section produced by `--around` expose them.

## Tests

```powershell
python -m unittest discover -s tests -v
```

The test data is synthetic. Parser patterns should be expanded only after they
have been checked against real event-log captures.
