# AutoAwake settings

Double-click the tray icon to launch `Settings.exe` from the same directory as `autoawake.exe`. Launching `autoawake.exe` again also opens Settings instead of starting another tray instance. Only one Settings window can run at a time; a subsequent Settings launch exits without showing anything. An error is shown if Settings cannot be launched.

AutoAwake creates `%APPDATA%\AutoAwake\settings.ini` on first launch. The file is an INI file encoded as UTF-16 LE with a byte order mark (BOM). Edit it with an editor that preserves this encoding; AutoAwake reloads changes while running.

```ini
[General]
Interval=30
CpuUsageEnabled=1
CpuUsageThreshold=80
CpuUsageAction=NoSleep
```

- `Interval`: sampling interval in whole seconds from `1` to `86400`; default `30`.
- `CpuUsageEnabled`: enable (`1`) or disable (`0`) the CPU-based feature; default `1`.
- `CpuUsageThreshold`: CPU usage threshold in whole percent from `0` to `100`; default `80`.
- `CpuUsageAction`: `NoSleep` prevents system sleep while allowing the display to turn off; `KeepDisplay` prevents both system sleep and display idle-off. Default `NoSleep`; values are case-insensitive.

## Settings window

The Settings app records its window position and size in the `[Settings]` section. Values are screen coordinates and dimensions in pixels; the app restores the saved placement at startup and updates them when the window is moved or resized.

```ini
[Settings]
WindowX=100
WindowY=100
WindowWidth=800
WindowHeight=600
```

- `WindowX` and `WindowY`: screen position of the window's top-left corner; negative coordinates are valid for displays positioned to the left of or above the primary display.
- `WindowWidth` and `WindowHeight`: window dimensions in pixels; the minimum is 640 by 480. Smaller saved dimensions are raised to the minimum when restored.

## Application monitoring

Use one section per application, with a distinct, arbitrary section name other than `General` or `Settings` (section names are case-insensitive):

```ini
[Video editor]
Path=C:\Program Files\Example\editor.exe
Action=NoSleep

[Presentation]
Path=C:\Program Files\Example\presentation.exe
Action=KeepDisplay
VisibleWindowOnly=1
```

- `Path`: the absolute Windows path to the application's executable. Paths are compared case-insensitively with the process image path; use the actual image path rather than a shortcut, symbolic link, or alternate path spelling.
- `Action`: required; the sleep prevention mode while the application is running. `NoSleep` prevents system sleep; `KeepDisplay` prevents system sleep and display idle-off, as with `CpuUsageAction`. Values are case-insensitive.
- `VisibleWindowOnly`: `1` requires a matching process to have a visible top-level window; `0` matches the running process regardless of its windows. The default is `0`.

A window is visible when Windows reports it as visible (`IsWindowVisible`). A minimized window or one obscured by other windows can still count as visible. Only top-level windows on AutoAwake's current desktop are enumerated; a window belonging to a different process does not satisfy the condition.

AutoAwake checks running processes and window visibility at startup, after settings changes and system resume, and every five seconds while application sections exist. A process and, when required, its visible window must remain present until a check to be detected. If a process with a configured executable name cannot be inspected or window enumeration fails, AutoAwake reports the error and exits instead of silently ignoring it.

When multiple application or CPU conditions match, the priority is `KeepDisplay < NoSleep`: any matching `NoSleep` condition wins, so the display may turn off even if another condition requests `KeepDisplay`. When none match, the sleep prevention request is cleared. The tray icon reflects whether any condition is active.

## CPU monitoring

When enabled, AutoAwake samples the system-wide CPU performance counter every `Interval` seconds. The first result is available after one interval. Usage greater than or equal to `CpuUsageThreshold` activates the CPU condition; lower usage clears it. The tray icon is `open.ico` while any condition is active and `closed.ico` otherwise. `KeepDisplay` prevents idle display-off but does not turn on an already-off display. These requests prevent idle sleep, not explicit user-initiated sleep.

Settings changes are applied while running. Disabling CPU monitoring clears its request immediately; threshold and action changes apply to the last sample, while interval changes restart the timer. After system resume, a new sample is collected over one interval. Invalid encoding or values and file access failures are reported: at startup they prevent launch; during reload the last valid settings remain active. If the file is deleted while AutoAwake runs, it is not recreated until a later launch; restore it to reload settings. If directory monitoring, CPU counter collection, power requests, or a monitoring timer fails, AutoAwake reports the error and exits.

## Tests

From `Tray\tests` in a Visual Studio Developer Command Prompt:

```bat
cl /W4 /Fe:settings_monitor_tests.exe settings_monitor_tests.c ..\settings.c /link /subsystem:console shell32.lib user32.lib pdh.lib
settings_monitor_tests.exe
```

The tests use temporary settings files and a short-lived child process. Power and tray calls are stubbed.
