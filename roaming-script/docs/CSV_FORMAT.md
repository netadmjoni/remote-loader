# CSV Format

`wgbdiag.exp` writes CSV rows to the file selected by `--csv`, or to the automatic daily CSV file when default logging is enabled:

```text
./logs/wgb-YYYYMMDD.csv
```

All values are quoted. Embedded double quotes are escaped by doubling them.

## Header

```csv
timestamp,event,ap,mac,rssi,channel,rate,radio,state,auth,km,duration,from_ap,from_mac,from_rssi,from_channel,from_rate,from_radio,downtime_seconds
```

## Columns

| Column | Description |
| --- | --- |
| `timestamp` | Local timestamp when the CSV row is written, formatted as `YYYY-MM-DD HH:MM:SS`. |
| `event` | Event type emitted by the script. See event types below. |
| `ap` | Current parent AP name parsed from `Parent AP Name`. |
| `mac` | Current parent AP MAC parsed from `Parent AP MAC`. |
| `rssi` | Current RSSI parsed from `RSSI`. |
| `channel` | Current channel parsed from `Channel`. |
| `rate` | Current Tx/Rx data rate parsed from `Current Datarate (Tx/Rx)`. |
| `radio` | Current uplink radio ID parsed from `Uplink Radio ID`. |
| `state` | Current uplink state parsed from `Uplink State`. |
| `auth` | Authentication type parsed from `Auth Type`. |
| `km` | Key-management type parsed from `Key management Type`. |
| `duration` | Connected duration parsed from `Connected Duration`. |
| `from_ap` | Previous parent AP name for roam or reconnect-related context. |
| `from_mac` | Previous parent AP MAC for roam or reconnect-related context. |
| `from_rssi` | Previous RSSI for roam or reconnect-related context. |
| `from_channel` | Previous channel for roam or reconnect-related context. |
| `from_rate` | Previous Tx/Rx data rate for roam or reconnect-related context. |
| `from_radio` | Previous uplink radio ID for roam or reconnect-related context. |
| `downtime_seconds` | Number of seconds down for connection failure or recovery events. Empty when not applicable. |

## Event Types

| Event | Meaning |
| --- | --- |
| `CONNECTED` | Initial successful SSH/login/enable connection when no downtime was being tracked. |
| `CONNECT_FAIL` | SSH connection, login, or enable handling failed. `downtime_seconds` contains the current tracked downtime. |
| `CONNECTED_AFTER_DOWN` | Connection succeeded after a previous failure period. `downtime_seconds` contains the measured downtime. |
| `INITIAL_AP` | First successfully parsed associated parent AP after startup or before previous AP state exists. |
| `SAMPLE` | Regular parsed association sample with no detected roam. |
| `ROAM` | Parent AP name or MAC changed from the previously tracked AP. Current AP fields describe the new AP; `from_*` fields describe the previous AP. |
| `SAME_CHANNEL_ROAM` | A roam was detected and the previous and current channels are the same. This event is always written to CSV for same-channel roams, regardless of terminal warning settings. |
| `RADIO_CHANGE` | Uplink radio ID changed. On a roam, this may be emitted in addition to `ROAM`; without a roam, it is emitted alongside the regular sample path. |
| `COMMAND_TIMEOUT` | The polling command timed out. Previous AP context is written in the `from_*` fields when available. |
| `SSH_CLOSED` | The SSH session ended unexpectedly. Previous AP context is written in the `from_*` fields when available. |

## Data Source

Association fields come from parsing the output of:

```text
show wgb dot11 associations
```

The parser currently extracts values appearing after a colon on lines containing these labels:

- `Parent AP Name`
- `Parent AP MAC`
- `RSSI`
- `Channel`
- `Current Datarate (Tx/Rx)`
- `Uplink Radio ID`
- `Connected Duration`
- `Uplink State`
- `Auth Type`
- `Key management Type`
