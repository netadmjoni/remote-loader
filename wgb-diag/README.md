# wgb-diag

## Build MSI installer

The installer build publishes the WPF app as a .NET 8 self-contained `win-x64` application and packages the complete publish output into a per-machine MSI.

```powershell
.\build-installer.ps1
```

Expected artifact:

```text
artifacts\installer\WgbDiagnostics-0.1.17-win-x64.msi
```

## Installation

Interactive installation:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.17-win-x64.msi
```

Silent installation with desktop shortcut:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.17-win-x64.msi /qn /norestart INSTALLDESKTOPSHORTCUT=1
```

Silent installation without desktop shortcut:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.17-win-x64.msi /qn /norestart INSTALLDESKTOPSHORTCUT=0
```

Silent uninstallation using the MSI:

```powershell
msiexec /x artifacts\installer\WgbDiagnostics-0.1.17-win-x64.msi /qn /norestart
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

If a newer probe has already succeeded before an older probe times out, WGB Diagnostics records a `PACKET_LOSS` event with `applied_to_state=false`. This increments lost-probe statistics and is visible in All pings and Events only, but it does not create `LOSS_START`, `ALERT`, `RECOVER`, an outage duration, or an active interruption.

## Manual GUI regression test

Realtime graph interaction:

1. Press Start and confirm that both ping monitoring and WGB polling begin.
2. Confirm the graph window starts at 10 minutes.
3. Use the 1, 5, 10, 30, and 60 minute presets and confirm both RTT and RSSI X-axes change together.
4. Zoom with the mouse wheel and pan by dragging either graph; confirm both graphs keep the same time window.
5. Pause the graph, let monitoring continue, then resume and confirm autoscroll/manual view state behaves predictably.
6. Click Reset zoom and confirm the view returns to the current time window with a sensible Y-scale.
7. Move the mouse repeatedly over both graphs and confirm no labels, color blocks, selections, or duplicate markers accumulate.
8. Cause loss/recover events and confirm ICMP event view shows LAST_OK, LOSS_START, ALERT, RECOVER, and ERROR only.
9. Switch to raw ping view and confirm individual probes are visible.
10. Trigger or load WGB roam output and confirm roam markers appear on both graphs and in the roaming timeline.
11. Confirm the compact WGB panel shows Parent AP, BSSID, channel, radio ID, RSSI, rates, association status, last poll, data age, and last roam.
12. Save SSH and enable passwords with the save checkboxes, reload settings, then use Forget buttons and confirm no cleartext appears in `%LocalAppData%\WgbDiagnostics\appsettings.json` or session logs.
13. Confirm the Dashboard remains readable at 1366x768.
