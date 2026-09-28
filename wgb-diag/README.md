# wgb-diag

## Build MSI installer

The installer build publishes the WPF app as a .NET 8 self-contained `win-x64` application and packages the complete publish output into a per-machine MSI.

```powershell
.\build-installer.ps1
```

Expected artifact:

```text
artifacts\installer\WgbDiagnostics-0.1.26-win-x64.msi
```

## Installation

Interactive installation:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.26-win-x64.msi
```

Silent installation with desktop shortcut:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.26-win-x64.msi /qn /norestart INSTALLDESKTOPSHORTCUT=1
```

Silent installation without desktop shortcut:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.26-win-x64.msi /qn /norestart INSTALLDESKTOPSHORTCUT=0
```

Silent uninstallation using the MSI:

```powershell
msiexec /x artifacts\installer\WgbDiagnostics-0.1.26-win-x64.msi /qn /norestart
```

## Installed locations

Program files:

```text
C:\Program Files\WgbDiagnostics\
```

Per-user configuration and writable data:

```text
%LocalAppData%\WgbDiagnostics\appsettings.json
%LocalAppData%\WgbDiagnostics\Logs\
```

The MSI does not include credentials and the application does not require administrator privileges for normal use. Installation is per-machine and may require elevation.

## ICMP timing semantics

Fresh installations leave the ICMP target empty. Configure a machine-side device such as `10.194.240.10` under Settings before starting monitoring. With no target configured, the Dashboard reports `ICMP NOT CONFIGURED` and Start is rejected before either ICMP monitoring or WGB polling begins. Existing installations using the obsolete development placeholder are migrated to the same unconfigured state; other configured targets are preserved.

Default ICMP settings match `ping-script/loss_monitor.sh` defaults:

```text
ICMP interval: 100 ms
ICMP timeout: 1000 ms
Loss alert threshold: 600 ms
```

`loss_monitor.sh` runs the platform `ping` process with `-i 0.1 -W 1 -O`. The script reads ping output serially and treats each `no answer yet` line as one lost probe. WGB Diagnostics schedules probes every configured interval and allows probes to overlap when timeout is longer than interval. With the defaults above, up to about ten 100 ms probes can be in flight before an older probe reaches its 1000 ms timeout.

If a newer probe has already succeeded before an older probe times out, WGB Diagnostics records a `PACKET_LOSS` event with `applied_to_state=false`. This increments lost-probe statistics and appears as operator-friendly packet loss in normal diagnostics; engineering/debug views retain the internal timeout classification. It does not create `LOSS_START`, `ALERT`, `RECOVER`, an outage duration, or an active interruption.

`ping-losses.csv` records each failed probe once, independently of whether it affected connectivity state. Adjacent sequence numbers can therefore be analyzed as a raw probe-loss burst without changing the meaning of `consecutive_loss` or `loss_window_ms` in `ping-events.csv`. See [docs/CSV_FORMAT.md](docs/CSV_FORMAT.md).

## Dashboard and diagnostics views

The compact Dashboard shows the current WGB Tx/Rx data rate alongside AP, RSSI, channel, radio, and latest event. Settings can enable a separate Tx/Rx data-rate graph; it uses the existing WGB samples and follows the same time window, autoscroll, zoom, and pan state as the RTT and RSSI graphs.

`Follow latest roam` selects each newly detected roam in Roam Details without changing graph zoom or its visible time window. The latest roam details remain available for the current session after its marker leaves the graph window. Previous roam, Next roam, or clicking a roam marker disables following so historical selection remains stable.

The locked Admin / Engineering settings include the Live diagnostics display buffer. The default is 10000 rows and accepted values are limited to 500-100000 rows.

Normal Events / Diagnostics shows only Live diagnostics with operator-relevant packet loss, interruption, recovery, roam, association, disconnect, reconnect, stale/healthy, and real failure events. Expected parser details such as IW9167 RSSI magnitude normalization are hidden there. `Enable engineering/debug views` exposes the ICMP, WGB, Roam, and Raw / Parser tabs together with raw ping details, parser messages, and SSH/command lifecycle events. Engineering/debug views and the data-rate graph are disabled by default.

## Manual GUI regression test

Realtime graph interaction:

1. Press Start and confirm that both ping monitoring and WGB polling begin.
2. Confirm the graph window starts at 5 minutes.
3. Use the 1, 2, 3, 5, and 10 minute presets and confirm all enabled graph X-axes change together. Confirm 30 minutes remains available under Tools / Advanced.
4. Zoom with the mouse wheel and pan by dragging a graph; confirm all enabled graphs keep the same time window.
5. Pause the graph, let monitoring continue, then resume and confirm autoscroll/manual view state behaves predictably.
6. Click Reset zoom and confirm the view returns to the current time window with a sensible Y-scale.
7. Move the mouse repeatedly over all enabled graphs and confirm no labels, color blocks, selections, or duplicate markers accumulate.
8. Cause loss/recover events and confirm the normal ICMP event view uses packet loss, interruption, and connectivity restored wording without sequence numbers or internal timeout reasons.
9. Enable engineering/debug views and confirm ICMP, WGB, Roam, Raw / Parser, individual probes, and internal reasons are visible. Disable it and confirm only Live diagnostics remains.
10. Trigger or load WGB roam output and confirm roam markers appear on the enabled graphs without successful poll lifecycle or expected RSSI-normalization messages in normal Live diagnostics.
11. Confirm the Dashboard shows current AP, RSSI, channel, radio ID, Tx/Rx rate, and latest event.
12. Enable the data-rate graph and confirm separate Tx and Rx lines follow the same time window, autoscroll, zoom, and pan as RTT and RSSI.
13. With Follow latest roam enabled, trigger a roam and confirm its details and marker become selected without changing graph zoom. Let the marker leave the graph window and confirm the details and Dashboard Last event remain. Use Previous or Next and confirm following turns off and the historical selection remains when another roam arrives.
14. Save SSH and enable passwords with the save checkboxes, reload settings, then use Forget buttons and confirm no cleartext appears in `%LocalAppData%\WgbDiagnostics\appsettings.json` or session logs.
15. Confirm the Dashboard remains readable at 1366x768.
