# wgb-diag

## Build MSI installer

The installer build publishes the WPF app as a .NET 8 self-contained `win-x64` application and packages the complete publish output into a per-machine MSI.

```powershell
.\build-installer.ps1
```

Expected artifact:

```text
artifacts\installer\WgbDiagnostics-0.1.2-win-x64.msi
```

## Installation

Interactive installation:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.2-win-x64.msi
```

Silent installation with desktop shortcut:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.2-win-x64.msi /qn /norestart INSTALLDESKTOPSHORTCUT=1
```

Silent installation without desktop shortcut:

```powershell
msiexec /i artifacts\installer\WgbDiagnostics-0.1.2-win-x64.msi /qn /norestart INSTALLDESKTOPSHORTCUT=0
```

Silent uninstallation using the MSI:

```powershell
msiexec /x artifacts\installer\WgbDiagnostics-0.1.2-win-x64.msi /qn /norestart
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

## Manual GUI regression test

Realtime graph interaction:

1. Run ping monitoring for at least 10 minutes.
2. Cause several loss/recover events.
3. Move the mouse repeatedly over both RTT and RSSI graphs.
4. Pause and resume the graph while monitoring continues.
5. Click Reset zoom.
6. Confirm no labels, color blocks, selections, or duplicate markers accumulate on either graph.
7. Confirm the Dashboard remains readable at 1366x768.
