# AutoAwake settings

Clicking the tray icon shows a menu with a baseline sleep prevention choice (Auto, Prevent sleep, Prevent display off, Prevent screen saver and lock) and Exit. The baseline is the minimum mode: the strongest of it and any matching condition (NoSleep < KeepDisplay < KeepUnlocked) is applied, and it is active even with no matching condition. Auto keeps the condition-only behavior below. It applies only while running, is not saved, and resets to Auto at each launch.

Double-click the tray icon to launch `AutoAwakeSettings.exe` from the same directory as `AutoAwakeCore.exe`. Launching `AutoAwakeCore.exe` again also opens Settings instead of starting another tray instance. Only one Settings window can run at a time; a subsequent Settings launch exits without showing anything. An error is shown if Settings cannot be launched.

AutoAwake creates `%APPDATA%\AutoAwake\settings.ini` on first launch. The file is an INI file encoded as UTF-16 LE with a byte order mark (BOM). Edit it with an editor that preserves this encoding; AutoAwake reloads changes while running.

```ini
[General]
Interval=30
CpuUsageEnabled=1
CpuUsageThreshold=80
CpuUsageAction=NoSleep
SuppressWindowsUpdate=1
```

- `Interval`: sampling interval in whole seconds from `1` to `86400`; default `30`.
- `CpuUsageEnabled`: enable (`1`) or disable (`0`) the CPU-based feature; default `1`.
- `CpuUsageThreshold`: CPU usage threshold in whole percent from `0` to `100`; default `80`.
- `CpuUsageAction`: action to apply while the CPU condition matches; default `NoSleep`. Values are case-insensitive.
- `SuppressWindowsUpdate`: request the Windows Update registry changes described below (`1` or `0`); default `1`.
- `HotkeyAuto`, `HotkeyNoSleep`, `HotkeyKeepDisplay`, `HotkeyKeepUnlocked`: optional global shortcut keys that select the matching baseline from the tray menu. The format is modifiers and one key joined by `+`, for example `Ctrl+Alt+F9`; case and spaces around `+` are ignored. At least one modifier (`Ctrl`, `Alt`, `Shift`, `Win`) is required. Keys are `A`-`Z`, `0`-`9`, `F1`-`F24`, `Space`, `Enter`, `Tab`, `Esc`, `Up`, `Down`, `Left`, `Right`, `Home`, `End`, `PageUp`, `PageDown`, `Insert`, `Delete` and `Num0`-`Num9`. Omitted or empty values disable the shortcut key (default). Invalid or duplicated values are settings errors. If Windows cannot register a shortcut key (for example, another application uses it), that shortcut key is skipped and a notification is shown once; the others keep working.

## Actions

The table describes idle behavior: ○ means allowed and ✗ means prevented. Allowed means AutoAwake does not request suppression; Windows settings and policies still apply.

| Action | System sleep | Display idle-off | Screen saver | Idle automatic lock |
|---|:---:|:---:|:---:|:---:|
| `NoSleep` | ✗ | ○ | ○ | ○ |
| `KeepDisplay` | ✗ | ✗ | ○ | ○ |
| `KeepUnlocked` | ✗ | ✗ | ✗ | ✗ |

`KeepUnlocked` requests suppression of idle screen savers and idle automatic locking. Policy and power state may affect the request; it does not prevent explicit sleep or locking.

## Windows Update suppression

While any sleep-prevention condition is active, `SuppressWindowsUpdate=1` sets the machine-wide DWORD `HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU\NoAutoRebootWithLoggedOnUsers` to `1`, and sets `IsContinuousInnovationOptedIn` and `IsExpedited` under `HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings` to `0`. These settings require permission to write HKLM, normally an elevated administrator. On failure AutoAwake continues sleep prevention without displaying an error; Windows Update suppression may not take effect. Set `SuppressWindowsUpdate=0` to disable this feature.

This is not a guarantee against updates or restarts. Microsoft documents `NoAutoRebootWithLoggedOnUsers` as applicable only when Configure Automatic Updates is set to option 4, and scheduled user restarts may still occur. Windows version, policy and other software may affect all these settings.

Original values are saved alongside each value with an `.AutoAwakeBackup` suffix, preserving type and data. A separate `.AutoAwakeState` marker tracks the transaction, including originally absent values, which have no backup. AutoAwake attempts to restore and remove its records when prevention ends, the setting is disabled, or the app or Windows shuts down. It also attempts recovery at startup even when suppression is disabled or settings cannot be loaded. After a crash or failed restoration, rerun with sufficient privileges to recover; damaged records cannot be restored safely. A backup found without a state marker is treated as an orphan: its type and data are restored to the original value, even if the target is absent, and the backup is deleted. This applies at startup and when suppression ends regardless of the setting. Do not use the `.AutoAwakeBackup` suffix for unrelated values. Failed cleanup is retried during shutdown; remaining records are retried on a later startup. Failures are written to debugger output rather than displayed. Do not edit the managed values or records while suppression is active; external changes can be overwritten by restoration. The keys themselves may remain if they were created for suppression.

A machine-wide mutex prevents different sessions from simultaneously modifying these values. A startup skips recovery while a live instance owns the mutex; another session cannot suppress updates while the mutex is held. Access to an existing mutex can also be denied across users or privilege levels. These failures do not stop sleep prevention. Forced termination or power loss can delay restoration until the next successful startup.

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
- `Action`: required; the sleep prevention mode while the application is running, as described in Actions. Values are case-insensitive.
- `VisibleWindowOnly`: `1` requires a matching process to have a visible top-level window; `0` matches the running process regardless of its windows. The default is `0`.

A window is visible when Windows reports it as visible (`IsWindowVisible`). A minimized window or one obscured by other windows can still count as visible. Only top-level windows on AutoAwake's current desktop are enumerated; a window belonging to a different process does not satisfy the condition.

AutoAwake checks running processes and window visibility at startup, after settings changes and system resume, and every five seconds while application sections exist. A process and, when required, its visible window must remain present until a check to be detected. If a process with a configured executable name cannot be inspected or window enumeration fails, AutoAwake reports the error and exits instead of silently ignoring it.

When multiple application or CPU conditions match, the priority is `KeepUnlocked < KeepDisplay < NoSleep`: the less restrictive matching action wins. For example, `NoSleep` allows display idle-off even when another condition requests `KeepDisplay` or `KeepUnlocked`. When none match, the sleep prevention request is cleared. The tray icon reflects whether any condition is active.

## CPU monitoring

When enabled, AutoAwake samples the system-wide CPU performance counter every `Interval` seconds. The first result is available after one interval. Usage greater than or equal to `CpuUsageThreshold` activates the CPU condition; lower usage clears it. The tray icon is `open.ico` while any condition is active and `closed.ico` otherwise. `KeepDisplay` prevents idle display-off but does not turn on an already-off display. These requests prevent idle sleep, not explicit user-initiated sleep.

Settings changes are applied while running. Disabling CPU monitoring clears its request immediately; threshold and action changes apply to the last sample, while interval changes restart the timer. After system resume, a new sample is collected over one interval. Invalid encoding or values and file access failures are reported: at startup they prevent launch; during reload the last valid settings remain active. If the file is deleted while AutoAwake runs, it is not recreated until a later launch; restore it to reload settings. If directory monitoring, CPU counter collection, power requests, or a monitoring timer fails, AutoAwake reports the error and exits.

## Tests

From `Core\tests` in a Visual Studio Developer Command Prompt:

```bat
cl /W4 /Fe:settings_monitor_tests.exe settings_monitor_tests.c ..\settings.c /link /subsystem:console shell32.lib user32.lib pdh.lib
settings_monitor_tests.exe
cl /W4 /Fe:update_suppression_tests.exe update_suppression_tests.c /link /subsystem:console advapi32.lib
update_suppression_tests.exe
```

The tests create disposable settings files in the current directory and use a short-lived child process. Power, tray and update calls are stubbed in the monitor tests. Registry tests use uniquely named keys under HKCU only; they never modify the live HKLM Windows Update keys.
