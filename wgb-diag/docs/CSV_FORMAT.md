# WGB Diagnostics CSV and log format

## Timestamps

Application-generated CSV and raw-log timestamps use the Windows computer's local timezone. They are written as ISO 8601 with sub-second precision and an explicit UTC offset:

```text
2026-09-28T10:23:27.1940000+02:00
```

The offset makes the local operator time unambiguous for later engineering or TAC correlation. Event ordering still uses the original `DateTimeOffset` values; converting the representation to local time does not reorder events.

Daily file rotation and session-folder timestamps also follow the computer's local calendar day.

## ICMP event state

`ping-events.csv` retains its existing schema:

```csv
timestamp,event,sequence,rtt_ms,consecutive_loss,loss_window_ms,applied_to_state,ignored_reason,connection_state,message
```

`consecutive_loss` is the number of consecutive failed probes accepted by the connectivity state machine since its latest accepted success. `loss_window_ms` is the state-machine estimate `consecutive_loss * configured ping interval`; it is not calculated from timeout detection timestamps.

An out-of-order timeout after a newer success is logged as `PACKET_LOSS` with `applied_to_state=false`, `consecutive_loss=0`, and `loss_window_ms=0`. These zeroes mean that the raw loss did not alter outage state.

## Raw probe losses

`ping-losses.csv` is additive and records each failed probe once:

```csv
probe_started_at,detected_at,sequence,outcome,elapsed_ms,completion_order,applied_to_state,state_consecutive_loss,state_loss_window_ms,ignored_reason,connection_state,message
```

| Column | Meaning |
| --- | --- |
| `probe_started_at` | Approximate original probe send time, reconstructed from the monotonic elapsed values. |
| `detected_at` | Time the probe completed or was declared timed out. This may be much later than the actual start of loss. |
| `sequence` | Probe sequence number. Adjacent failed sequence numbers within one uninterrupted monitoring run form a raw loss burst. |
| `outcome` | `LOSS` for a timeout/loss or `ERROR` for a probe execution error. |
| `elapsed_ms` | Monotonic milliseconds from probe start until completion or timeout detection. |
| `completion_order` | Order in which overlapping probes completed. |
| `applied_to_state` | Whether this result was allowed to change connectivity/outage state. |
| `state_consecutive_loss` | State-machine loss count; separate from offline raw burst grouping. |
| `state_loss_window_ms` | State-machine estimated loss window. |
| `ignored_reason` | Reason an out-of-order result did not affect state. |
| `connection_state` | Connectivity state after processing the result. |
| `message` | Scrubbed diagnostic detail. |

Sort by `sequence` when analyzing raw loss bursts. Do not calculate precise outage duration from `detected_at`, because the default 1000 ms timeout allows several newer 100 ms probes to finish before an older lost probe is declared timed out.

## Other files

- `events.csv`: combined meaningful ICMP and WGB events.
- `wgb-samples.csv`: parsed WGB association samples.
- `wgb-events.csv`: structured WGB connection, polling, and roam events.
- `roam-events.csv`: AP/channel/radio roam transitions.
- `raw-ping.log` and `raw-wgb.log`: optional engineering logs when Raw logging is enabled.
- `session-summary.json`: local-offset session start and stop timestamps plus event counters.
