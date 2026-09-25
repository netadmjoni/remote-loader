# Product brief: Operator connectivity monitor

## Product goal

The primary product is a simple Windows application for machine operators in an
underground mining environment.

It must answer one question immediately:

> Did the network connection stop, when did it stop, and for how long?

The application is an operational tool first. Detailed Cisco WGB diagnostics
remain available underneath for network engineering and Cisco TAC work, but
must not dominate the normal operator view.

## Users

The primary user is a machine operator. The operator is not expected to
understand Cisco WGBs, roaming states, RSSI theory, authentication, EAPOL, WLAN
debug logs, Git, or command-line tools.

The secondary user is a network engineer who needs detailed recorded data to
investigate an interruption after the operator reports its time.

## Operator experience

The intended workflow is:

1. Start the program.
2. See a clear green status when connectivity is normal.
3. See an interruption become visually obvious when it occurs.
4. Read when it happened and how long it lasted.

The default screen should prioritize:

- large, unambiguous connectivity state
- current latency
- packet loss over a recent window
- most recent interruption and its duration
- longest interruption in the current session
- rolling latency and connectivity timeline
- a short, readable event list

The normal screen must not be dominated by BSSIDs, authentication states,
EAPOL exchanges, scan states, or raw Cisco events. Those belong in an advanced
or engineering view.

## Connectivity monitoring

ICMP is the primary signal. The initial target is approximately 100 ms between
probes where the operating system and network permit it.

Connectivity state must be based on consecutive failures and recovery, not on
a single lost packet. Timing and thresholds must be configurable and validated
against real behavior.

The conceptual states are:

- `GREEN`: connectivity is operating normally
- `YELLOW`: a short interruption or degraded connectivity is in progress
- `RED`: an interruption has exceeded the configured operational threshold

Approximately 600 ms is historically important in this environment and should
be visually prominent during validation. It is a configurable operational
threshold, not a universal hard-coded definition of failure.

## Timeline and events

The rolling graph should show latency, missed probes, and interruption periods.
Interruption periods must be easy to identify without interpreting raw data.

The event list should include concise entries such as:

```text
14:21:03  Short interruption      210 ms
14:27:44  Roam                    AP-A -> AP-B
14:32:18  NETWORK INTERRUPTION    740 ms
14:32:19  Connection restored
```

Selecting an event should focus the graph around that event, initially using a
window such as five seconds before and after it. This same window can later show
correlated WGB data.

## Continuous recording

The application should continuously store enough local data for an operator to
report only an approximate event time while allowing engineering to inspect the
details later.

Recorded data should include, when available:

- timestamp
- ICMP result and round-trip time
- consecutive failures
- calculated interruption duration
- connectivity state
- WGB parent AP
- RSSI
- channel
- active radio
- roam event

Raw and detailed data should be retained without cluttering the operator view.
Future WGB event logs and AP/WLC diagnostics can be correlated on the same
timeline.

## Product layers

### 1. Operator layer

Simple connectivity status, current measurements, interruption history, and a
clickable timeline. This is the primary product experience.

### 2. Diagnostic layer

High-frequency ICMP data plus selected `wgbdiag` context such as current AP,
RSSI, channel, radio, and roam events.

### 3. Engineering and TAC layer

Detailed WGB event parsing, raw Cisco events, reason codes, and cross-source
correlation. `wgbtrace` belongs here.

All layers should use the same recorded timestamps where practical. The deeper
layers add context; they do not redefine the operator-facing connectivity
result.

## First useful release

The first operator-ready version should:

- probe connectivity at approximately 100 ms
- calculate interruptions accurately
- show `GREEN`, `YELLOW`, and `RED` state clearly
- show current round-trip time
- show recent and longest interruption duration
- draw a rolling latency and connectivity graph
- place visible interruption markers on the timeline
- maintain a concise event list
- store measurements and events locally

WGB AP, RSSI, channel, radio, and roam information should be integrated only
after this connectivity workflow is reliable and understandable to operators.

## Safety and scope

- Do not require production WGB configuration or firmware changes.
- Do not treat one lost probe as a major outage.
- Do not guess unknown Cisco reason-code meanings.
- Do not remove raw diagnostic data to simplify the UI.
- Do not expose engineering complexity on the default operator screen.
- Preserve existing log and data compatibility unless a change is explicitly
  approved.

Success means an operator can look at the application and confidently say:

> There was a network interruption at 14:32, and it lasted about 740 ms.

Engineering must then be able to inspect the recorded data around 14:32 without
requiring the operator to have collected Cisco debug output during the event.
