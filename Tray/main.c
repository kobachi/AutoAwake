#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <tlhelp32.h>
#include <strsafe.h>
#include <wchar.h>
#include "resource.h"
#include "settings.h"
#include "update_suppression.h"

#pragma comment(lib, "pdh.lib")

#define ID_TRAY 1
#define ID_EXIT 2
#define ID_SETTINGS_TIMER 1
#define ID_CPU_TIMER 2
#define ID_APPLICATION_TIMER 3
#define SETTINGS_RELOAD_DELAY_MS 500
#define APPLICATION_INTERVAL_MS 5000
#define WM_TRAY (WM_APP + 1)

static const wchar_t window_class[] = L"AwakeTrayWindow";
static const wchar_t instance_name[] = L"Local\\Kobachi.AutoAwake.Tray.Singleton";
static UINT taskbar_created;
static HICON closed_icon;
static HICON open_icon;
static PDH_HQUERY cpu_query;
static PDH_HCOUNTER cpu_counter;
static BOOL active;
static BOOL have_sample;
static double last_cpu_usage;
static BOOL application_no_sleep;
static BOOL application_keep_display;
static BOOL application_keep_unlocked;
static HANDLE display_request;
static AwakeSettings settings;
static UpdateSuppression update_state;
static wchar_t strings[IDS_SETTINGS_UPDATE_ERROR - IDS_TITLE + 1][256];

/* Loads the localized application strings.
 * instance: Module containing the string resources.
 */
static BOOL load_strings(HINSTANCE instance)
{
	UINT id;
	for (id = IDS_TITLE; id <= IDS_SETTINGS_UPDATE_ERROR; ++id) {
		int length = LoadStringW(instance, id, strings[id - IDS_TITLE], 256);
		if (length == 0 || length == 255) {
			/* A required localized string is missing or may be truncated. */
			MessageBoxW(NULL, L"Could not load application resources.", L"AutoAwake", MB_OK | MB_ICONERROR);
			return FALSE;
		}
	}
	return TRUE;
}

/* Retrieves a previously loaded resource string.
 * id: Resource identifier in the application string range.
 */
static const wchar_t *text(UINT id)
{
	return strings[id - IDS_TITLE];
}

/* Displays the error corresponding to a settings load result.
 * window: Owner of the error dialog, or NULL.
 * result: Settings error to report.
 */
static void show_settings_error(HWND window, SettingsResult result)
{
	UINT error_id = IDS_SETTINGS_IO_ERROR;
	if (result == SETTINGS_INVALID_ENCODING) {
		error_id = IDS_SETTINGS_ENCODING_ERROR;
	} else if (result == SETTINGS_INVALID_VALUE) {
		error_id = IDS_SETTINGS_VALUE_ERROR;
	} else if (result == SETTINGS_INVALID_INTERVAL) {
		error_id = IDS_SETTINGS_INTERVAL_ERROR;
	} else if (result == SETTINGS_INVALID_CPU_THRESHOLD) {
		error_id = IDS_SETTINGS_CPU_THRESHOLD_ERROR;
	} else if (result == SETTINGS_INVALID_CPU_ACTION) {
		error_id = IDS_SETTINGS_CPU_ACTION_ERROR;
	} else if (result == SETTINGS_INVALID_APPLICATION_PATH) {
		error_id = IDS_SETTINGS_APPLICATION_PATH_ERROR;
	} else if (result == SETTINGS_INVALID_APPLICATION_ACTION) {
		error_id = IDS_SETTINGS_APPLICATION_ACTION_ERROR;
	} else if (result == SETTINGS_INVALID_SUPPRESS_WINDOWS_UPDATE) {
		error_id = IDS_SETTINGS_UPDATE_ERROR;
	} else if (result == SETTINGS_INVALID_APPLICATION_VISIBLE_WINDOW) {
		error_id = IDS_SETTINGS_APPLICATION_VISIBLE_WINDOW_ERROR;
	}
	MessageBoxW(window, text(error_id), text(IDS_TITLE), MB_OK | MB_ICONERROR);
}

/* Stops CPU sampling and discards the last sample.
 * window: Window that owns the CPU timer.
 */
static void stop_cpu_monitor(HWND window)
{
	KillTimer(window, ID_CPU_TIMER);
	if (cpu_query) {
		PdhCloseQuery(cpu_query);
		cpu_query = NULL;
		cpu_counter = NULL;
	}
	have_sample = FALSE;
}

static void initialize_update_suppression(void)
{
	if (!update_init(&update_state, HKEY_LOCAL_MACHINE,
		L"SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate\\AU",
		L"SOFTWARE\\Microsoft\\WindowsUpdate\\UX\\Settings",
		L"Global\\Kobachi.AutoAwake.UpdateSuppression")) {
		OutputDebugStringW(L"AutoAwake: Windows Update suppression initialization failed.\n");
	} else if (!update_recover(&update_state)) {
		OutputDebugStringW(L"AutoAwake: Windows Update settings recovery failed; it will be retried.\n");
	}
}

/* Reports a fatal monitoring error and shuts down the application.
 * window: Tray window and owner of the error dialog.
 * error_id: Resource identifier of the error message.
 */
static void fail_monitor(HWND window, UINT error_id)
{
	stop_cpu_monitor(window);
	if (!update_set(&update_state, FALSE)) OutputDebugStringW(L"AutoAwake: Windows Update settings restoration failed.\n");
	if (display_request) {
		CloseHandle(display_request);
		display_request = NULL;
	}
	SetThreadExecutionState(ES_CONTINUOUS);
	active = FALSE;
	MessageBoxW(window, text(error_id), text(IDS_TITLE), MB_OK | MB_ICONERROR);
	DestroyWindow(window);
	PostQuitMessage(1);
}

/* Applies a sleep prevention request and updates the tray icon.
 * window: Tray window used for icon updates and error reporting.
 * enabled: Whether to keep the system awake.
 * action: Prevention mode to apply when enabled.
 */
static BOOL set_prevention(HWND window, BOOL enabled, CpuUsageAction action)
{
	EXECUTION_STATE flags = ES_CONTINUOUS;
	NOTIFYICONDATAW icon = { 0 };
	if (enabled) {
		flags |= ES_SYSTEM_REQUIRED;
		if (action == CPU_USAGE_KEEP_DISPLAY_AWAKE) {
			flags |= ES_DISPLAY_REQUIRED;
		}
	}
	if (!SetThreadExecutionState(flags)) {
		/* Windows rejected the sleep prevention request. */
		fail_monitor(window, IDS_POWER_REQUEST_ERROR);
		return FALSE;
	}
	if (enabled && action == CPU_USAGE_KEEP_UNLOCKED && !display_request) {
		REASON_CONTEXT reason = { 0 };
		reason.Version = POWER_REQUEST_CONTEXT_VERSION;
		reason.Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING;
		reason.Reason.SimpleReasonString = strings[IDS_POWER_REASON - IDS_TITLE];
		display_request = PowerCreateRequest(&reason);
		if (display_request == INVALID_HANDLE_VALUE) {
			display_request = NULL;
			fail_monitor(window, IDS_POWER_REQUEST_ERROR);
			return FALSE;
		}
		if (!PowerSetRequest(display_request, PowerRequestDisplayRequired)) {
			fail_monitor(window, IDS_POWER_REQUEST_ERROR);
			return FALSE;
		}
	} else if ((!enabled || action != CPU_USAGE_KEEP_UNLOCKED) && display_request) {
		BOOL cleared = PowerClearRequest(display_request, PowerRequestDisplayRequired);
		BOOL closed = CloseHandle(display_request);
		display_request = NULL;
		if (!cleared || !closed) {
			fail_monitor(window, IDS_POWER_REQUEST_ERROR);
			return FALSE;
		}
	}
	if (!update_set(&update_state, enabled && settings.suppress_windows_update)) {
		OutputDebugStringW(update_state.rollback_failed ?
			L"AutoAwake: Windows Update settings rollback failed; it will be retried.\n" :
			L"AutoAwake: Windows Update settings could not be changed or restored.\n");
	}
	if (active == enabled) {
		/* The active state is unchanged, so the tray icon needs no update. */
		return TRUE;
	}
	active = enabled;
	icon.cbSize = sizeof(icon);
	icon.hWnd = window;
	icon.uID = ID_TRAY;
	icon.uFlags = NIF_ICON;
	icon.hIcon = active ? open_icon : closed_icon;
	if (!Shell_NotifyIconW(NIM_MODIFY, &icon)) {
		/* The notification area rejected the icon update. */
		fail_monitor(window, IDS_TRAY_UPDATE_ERROR);
		return FALSE;
	}
	return TRUE;
}

/* Combines active CPU and application conditions and applies their priority.
 * window: Tray window used for icon updates and error reporting.
 */
static BOOL update_prevention(HWND window)
{
	BOOL cpu_active = settings.cpu_usage_enabled && have_sample &&
		last_cpu_usage >= settings.cpu_usage_threshold;
	BOOL no_sleep = application_no_sleep ||
		(cpu_active && settings.cpu_usage_action == CPU_USAGE_KEEP_SYSTEM_AWAKE);
	BOOL keep_display = application_keep_display ||
		(cpu_active && settings.cpu_usage_action == CPU_USAGE_KEEP_DISPLAY_AWAKE);
	BOOL keep_unlocked = application_keep_unlocked ||
		(cpu_active && settings.cpu_usage_action == CPU_USAGE_KEEP_UNLOCKED);
	return set_prevention(window, no_sleep || keep_display || keep_unlocked,
		no_sleep ? CPU_USAGE_KEEP_SYSTEM_AWAKE :
		keep_display ? CPU_USAGE_KEEP_DISPLAY_AWAKE : CPU_USAGE_KEEP_UNLOCKED);
}

/* Tracks whether a process has a visible top-level window during enumeration. */
typedef struct VisibleWindowSearch {
	DWORD process_id;
	BOOL found;
} VisibleWindowSearch;

/* Records a visible top-level window belonging to the target process.
 * window: Window supplied by EnumWindows.
 * parameter: Pointer to the VisibleWindowSearch state.
 */
static BOOL CALLBACK find_visible_window(HWND window, LPARAM parameter)
{
	VisibleWindowSearch *search = (VisibleWindowSearch *)parameter;
	DWORD process_id = 0;
	if (IsWindowVisible(window) && GetWindowThreadProcessId(window, &process_id) &&
		process_id == search->process_id) {
		search->found = TRUE;
	}
	return TRUE;
}

/* Evaluates application rules against running processes and their windows.
 * window: Tray window used for icon updates and error reporting.
 */
static BOOL scan_applications(HWND window)
{
	HANDLE snapshot;
	PROCESSENTRY32W process = { 0 };
	BOOL no_sleep = FALSE, keep_display = FALSE, keep_unlocked = FALSE;
	DWORD error;

	if (!settings.application_count) {
		/* No application rules exist, so only CPU conditions can remain active. */
		application_no_sleep = FALSE;
		application_keep_display = FALSE;
		application_keep_unlocked = FALSE;
		return update_prevention(window);
	}
	snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
	if (snapshot == INVALID_HANDLE_VALUE) {
		/* The running process snapshot could not be created. */
		fail_monitor(window, IDS_APPLICATION_MONITOR_ERROR);
		return FALSE;
	}
	process.dwSize = sizeof(process);
	if (!Process32FirstW(snapshot, &process)) {
		/* Process enumeration could not be started. */
		CloseHandle(snapshot);
		fail_monitor(window, IDS_APPLICATION_MONITOR_ERROR);
		return FALSE;
	}
	do {
		size_t i;
		BOOL name_matches = FALSE;
		for (i = 0; i < settings.application_count; ++i) {
			const wchar_t *name = wcsrchr(settings.applications[i].path, L'\\');
			if (name && CompareStringOrdinal(name + 1, -1, process.szExeFile, -1, TRUE) == CSTR_EQUAL) {
				name_matches = TRUE;
				break;
			}
		}
		if (name_matches) {
			HANDLE handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process.th32ProcessID);
			if (!handle) {
				/* A matching process cannot be opened, unless it has already exited. */
				if (GetLastError() == ERROR_INVALID_PARAMETER) {
					continue;
				}
				CloseHandle(snapshot);
				fail_monitor(window, IDS_APPLICATION_MONITOR_ERROR);
				return FALSE;
			} else {
				wchar_t path[32768];
				DWORD length = ARRAYSIZE(path);
				BOOL windows_checked = FALSE;
				VisibleWindowSearch search = { process.th32ProcessID, FALSE };
				if (!QueryFullProcessImageNameW(handle, 0, path, &length)) {
					/* The process image cannot be read, unless the process has exited. */
					DWORD exit_code;
					BOOL exited = GetExitCodeProcess(handle, &exit_code) && exit_code != STILL_ACTIVE;
					CloseHandle(handle);
					if (exited) {
						continue;
					}
					CloseHandle(snapshot);
					fail_monitor(window, IDS_APPLICATION_MONITOR_ERROR);
					return FALSE;
				}
				CloseHandle(handle);
				for (i = 0; i < settings.application_count; ++i) {
					if (CompareStringOrdinal(path, -1, settings.applications[i].path, -1, TRUE) == CSTR_EQUAL) {
						if (settings.applications[i].visible_window_only) {
							if (!windows_checked) {
								if (!EnumWindows(find_visible_window, (LPARAM)&search)) {
									/* The visible-window condition cannot be evaluated. */
									CloseHandle(snapshot);
									fail_monitor(window, IDS_APPLICATION_MONITOR_ERROR);
									return FALSE;
								}
								windows_checked = TRUE;
							}
							if (!search.found) {
								continue;
							}
						}
						if (settings.applications[i].action == APPLICATION_KEEP_SYSTEM_AWAKE) {
							no_sleep = TRUE;
						} else if (settings.applications[i].action == APPLICATION_KEEP_DISPLAY_AWAKE) {
							keep_display = TRUE;
						} else {
							keep_unlocked = TRUE;
						}
					}
				}
			}
		}
	} while (Process32NextW(snapshot, &process));
	error = GetLastError();
	CloseHandle(snapshot);
	if (error != ERROR_NO_MORE_FILES) {
		/* Process enumeration ended with an error rather than reaching its end. */
		fail_monitor(window, IDS_APPLICATION_MONITOR_ERROR);
		return FALSE;
	}
	application_no_sleep = no_sleep;
	application_keep_display = keep_display;
	application_keep_unlocked = keep_unlocked;
	return update_prevention(window);
}

/* Starts the CPU counter and periodic sampling timer.
 * window: Window that owns the timer and error dialogs.
 */
static BOOL start_cpu_monitor(HWND window)
{
	if (PdhOpenQueryW(NULL, 0, &cpu_query) != ERROR_SUCCESS ||
		PdhAddEnglishCounterW(cpu_query, L"\\Processor Information(_Total)\\% Processor Time", 0, &cpu_counter) != ERROR_SUCCESS ||
		PdhCollectQueryData(cpu_query) != ERROR_SUCCESS) {
		/* The CPU query could not be initialized or primed. */
		fail_monitor(window, IDS_CPU_COUNTER_ERROR);
		return FALSE;
	}
	if (!SetTimer(window, ID_CPU_TIMER, settings.interval_seconds * 1000, NULL)) {
		/* Periodic CPU sampling could not be scheduled. */
		fail_monitor(window, IDS_CPU_TIMER_ERROR);
		return FALSE;
	}
	return TRUE;
}

/* Collects a CPU sample and updates the sleep prevention request.
 * window: Tray window used for icon updates and error reporting.
 */
static void sample_cpu(HWND window)
{
	PDH_FMT_COUNTERVALUE value;
	if (!cpu_query) {
		/* CPU monitoring is not running. */
		return;
	}
	if (PdhCollectQueryData(cpu_query) != ERROR_SUCCESS ||
		PdhGetFormattedCounterValue(cpu_counter, PDH_FMT_DOUBLE, NULL, &value) != ERROR_SUCCESS ||
		(value.CStatus != PDH_CSTATUS_VALID_DATA && value.CStatus != PDH_CSTATUS_NEW_DATA)) {
		/* The CPU counter did not produce a valid sample. */
		fail_monitor(window, IDS_CPU_COUNTER_ERROR);
		return;
	}
	last_cpu_usage = value.doubleValue;
	have_sample = TRUE;
	update_prevention(window);
}

/* Reloads settings and updates monitors and active conditions.
 * window: Tray window used for timers and error reporting.
 */
static void reload_settings(HWND window)
{
	AwakeSettings updated;
	SettingsResult result = settings_reload(&updated);
	if (result != SETTINGS_OK) {
		/* The new settings are invalid or unavailable; retain the current settings. */
		show_settings_error(window, result);
		return;
	}
	{
		BOOL start_cpu = !settings.cpu_usage_enabled && updated.cpu_usage_enabled;
		if (settings.cpu_usage_enabled && !updated.cpu_usage_enabled) {
			stop_cpu_monitor(window);
		}
		if (settings.cpu_usage_enabled && updated.cpu_usage_enabled && settings.interval_seconds != updated.interval_seconds &&
			!SetTimer(window, ID_CPU_TIMER, updated.interval_seconds * 1000, NULL)) {
			/* The changed CPU sampling interval could not be scheduled. */
			settings_free(&updated);
			fail_monitor(window, IDS_CPU_TIMER_ERROR);
			return;
		}
		settings_free(&settings);
		settings = updated;
		if (start_cpu && !start_cpu_monitor(window)) {
			/* CPU monitoring was enabled but could not be started. */
			return;
		}
	}
	if (settings.application_count) {
		if (!SetTimer(window, ID_APPLICATION_TIMER, APPLICATION_INTERVAL_MS, NULL)) {
			/* The application monitoring timer could not be scheduled. */
			fail_monitor(window, IDS_APPLICATION_TIMER_ERROR);
			return;
		}
	} else {
		KillTimer(window, ID_APPLICATION_TIMER);
	}
	scan_applications(window);
}

/* Adds the current tray icon to the notification area.
 * window: Window that receives tray icon messages.
 */
static BOOL add_tray_icon(HWND window)
{
	NOTIFYICONDATAW icon = { 0 };
	icon.cbSize = sizeof(icon);
	icon.hWnd = window;
	icon.uID = ID_TRAY;
	icon.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
	icon.uCallbackMessage = WM_TRAY;
	icon.hIcon = active ? open_icon : closed_icon;
	lstrcpynW(icon.szTip, text(IDS_TITLE), ARRAYSIZE(icon.szTip));
	return Shell_NotifyIconW(NIM_ADD, &icon);
}

static BOOL launch_settings(HWND window)
{
	wchar_t path[32768];
	wchar_t *filename;
	DWORD length = GetModuleFileNameW(NULL, path, ARRAYSIZE(path));

	if (length == 0 || length >= ARRAYSIZE(path) ||
		!(filename = wcsrchr(path, L'\\')) ||
		FAILED(StringCchCopyW(filename + 1, ARRAYSIZE(path) - (filename + 1 - path), L"Settings.exe")) ||
		(INT_PTR)ShellExecuteW(window, L"open", path, NULL, NULL, SW_SHOWNORMAL) <= 32) {
		MessageBoxW(window, text(IDS_SETTINGS_LAUNCH_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		return FALSE;
	}
	return TRUE;
}

/* Displays the tray icon context menu.
 * window: Tray window that owns the menu.
 */
static void show_menu(HWND window)
{
	POINT position;
	HMENU menu = CreatePopupMenu();
	UINT command;

	if (!menu) {
		/* The tray context menu could not be created. */
		MessageBoxW(window, text(IDS_MENU_CREATE_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		return;
	}
	if (!AppendMenuW(menu, MF_STRING, ID_EXIT, text(IDS_EXIT)) || !GetCursorPos(&position)) {
		/* The exit command or menu position could not be obtained. */
		DestroyMenu(menu);
		MessageBoxW(window, text(IDS_MENU_DISPLAY_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		return;
	}

	SetForegroundWindow(window);
	command = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, position.x, position.y, 0, window, NULL);
	DestroyMenu(menu);
	PostMessageW(window, WM_NULL, 0, 0);
	if (command == ID_EXIT) {
		DestroyWindow(window);
	}
}

/* Handles tray, timer, power and lifecycle messages.
 * window: Window receiving the message.
 * message: Windows message identifier.
 * wparam: Message-specific first parameter.
 * lparam: Message-specific second parameter.
 */
static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam)
{
	if (message == taskbar_created && taskbar_created != 0) {
		/* Explorer recreated the taskbar, so restore the tray icon. */
		if (!add_tray_icon(window)) {
			MessageBoxW(window, text(IDS_TRAY_RESTORE_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		}
		return 0;
	}

	switch (message) {
	case WM_POWERBROADCAST:
		if (wparam == PBT_APMRESUMEAUTOMATIC) {
			if (display_request) {
				BOOL closed = CloseHandle(display_request);
				display_request = NULL;
				if (!closed) {
					fail_monitor(window, IDS_POWER_REQUEST_ERROR);
					return TRUE;
				}
			}
			if (settings.cpu_usage_enabled && cpu_query) {
				stop_cpu_monitor(window);
				if (!start_cpu_monitor(window)) {
					/* CPU monitoring could not be restarted after resume. */
					return TRUE;
				}
			}
			scan_applications(window);
		}
		return TRUE;
	case WM_TIMER:
		if (wparam == ID_SETTINGS_TIMER) {
			KillTimer(window, ID_SETTINGS_TIMER);
			reload_settings(window);
		} else if (wparam == ID_CPU_TIMER) {
			sample_cpu(window);
		} else if (wparam == ID_APPLICATION_TIMER) {
			scan_applications(window);
		}
		return 0;
	case WM_TRAY:
		if (wparam == ID_TRAY && lparam == WM_RBUTTONUP) {
			show_menu(window);
		} else if (wparam == ID_TRAY && lparam == WM_LBUTTONDBLCLK) {
			launch_settings(window);
		}
		return 0;
	case WM_ENDSESSION:
		if (wparam && !update_set(&update_state, FALSE))
			OutputDebugStringW(L"AutoAwake: Windows Update settings restoration failed during session shutdown.\n");
		return 0;
	case WM_DESTROY:
	{
		NOTIFYICONDATAW icon = { 0 };
		stop_cpu_monitor(window);
		if (!update_close(&update_state)) OutputDebugStringW(L"AutoAwake: Windows Update settings restoration failed during shutdown.\n");
		KillTimer(window, ID_APPLICATION_TIMER);
		settings_free(&settings);
		if (display_request) {
			BOOL cleared = PowerClearRequest(display_request, PowerRequestDisplayRequired);
			BOOL closed = CloseHandle(display_request);
			display_request = NULL;
			if (!cleared || !closed) MessageBoxW(NULL, text(IDS_POWER_REQUEST_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		}
		if (active && !SetThreadExecutionState(ES_CONTINUOUS)) {
			MessageBoxW(NULL, text(IDS_POWER_REQUEST_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		}
		active = FALSE;
		icon.cbSize = sizeof(icon);
		icon.hWnd = window;
		icon.uID = ID_TRAY;
		Shell_NotifyIconW(NIM_DELETE, &icon);
		PostQuitMessage(0);
		return 0;
	}
	default:
		return DefWindowProcW(window, message, wparam, lparam);
	}
}

/* Initializes the tray application and runs its message and settings-watch loop.
 * instance: Handle to the application module.
 * previous: Legacy previous-instance handle, unused.
 * command_line: Command-line arguments, unused.
 * show_command: Requested initial window display state, unused.
 */
int WINAPI WinMain(HINSTANCE instance, HINSTANCE previous, LPSTR command_line, int show_command)
{
	WNDCLASSW window_class_info = { 0 };
	HWND window;
	MSG message;
	DWORD wait_result;
	int exit_code = 0;
	SettingsResult settings_result;
	wchar_t directory[MAX_PATH];
	HANDLE watch;
	HANDLE instance_mutex;
	DWORD instance_error;

	(void)previous;
	(void)command_line;
	(void)show_command;

	if (!load_strings(instance)) {
		/* Required application resources could not be loaded. */
		return 1;
	}
	instance_mutex = CreateMutexW(NULL, FALSE, instance_name);
	instance_error = GetLastError();
	if (!instance_mutex) {
		MessageBoxW(NULL, text(IDS_INSTANCE_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		return 1;
	}
	if (instance_error == ERROR_ALREADY_EXISTS) {
		BOOL launched = launch_settings(NULL);
		CloseHandle(instance_mutex);
		return launched ? 0 : 1;
	}
	initialize_update_suppression();
	settings_result = settings_load(&settings);
	if (settings_result != SETTINGS_OK) {
		/* Initial settings could not be loaded or validated. */
		show_settings_error(NULL, settings_result);
		return 1;
	}
	taskbar_created = RegisterWindowMessageW(L"TaskbarCreated");
	if (!taskbar_created) {
		/* The taskbar recreation message could not be registered. */
		MessageBoxW(NULL, text(IDS_TASKBAR_MESSAGE_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		settings_free(&settings);
		return 1;
	}

	closed_icon = (HICON)LoadImageW(instance, MAKEINTRESOURCEW(IDI_CLOSED), IMAGE_ICON,
		GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0);
	open_icon = (HICON)LoadImageW(instance, MAKEINTRESOURCEW(IDI_OPEN), IMAGE_ICON,
		GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), 0);
	if (!closed_icon || !open_icon) {
		/* One or both required tray icons could not be loaded. */
		MessageBoxW(NULL, text(IDS_ICON_LOAD_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		if (closed_icon) DestroyIcon(closed_icon);
		if (open_icon) DestroyIcon(open_icon);
		settings_free(&settings);
		return 1;
	}

	window_class_info.lpfnWndProc = window_proc;
	window_class_info.hInstance = instance;
	window_class_info.lpszClassName = window_class;
	if (!RegisterClassW(&window_class_info)) {
		/* The tray window class could not be registered. */
		MessageBoxW(NULL, text(IDS_WINDOW_CLASS_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		settings_free(&settings);
		return 1;
	}

	window = CreateWindowExW(0, window_class, text(IDS_TITLE), WS_OVERLAPPED, 0, 0, 0, 0,
		NULL, NULL, instance, NULL);
	if (!window) {
		/* The tray window could not be created. */
		MessageBoxW(NULL, text(IDS_WINDOW_CREATE_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		settings_free(&settings);
		return 1;
	}
	if (!add_tray_icon(window)) {
		/* The initial tray icon could not be added. */
		MessageBoxW(NULL, text(IDS_TRAY_ADD_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		DestroyWindow(window);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		return 1;
	}

	if (!settings_directory(directory, ARRAYSIZE(directory))) {
		/* The settings directory to watch could not be resolved. */
		MessageBoxW(window, text(IDS_SETTINGS_WATCH_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		DestroyWindow(window);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		return 1;
	}
	watch = FindFirstChangeNotificationW(directory, FALSE, FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE);
	if (watch == INVALID_HANDLE_VALUE) {
		/* The settings directory change watch could not be started. */
		MessageBoxW(window, text(IDS_SETTINGS_WATCH_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
		DestroyWindow(window);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		return 1;
	}

	if (settings.cpu_usage_enabled && !start_cpu_monitor(window)) {
		/* CPU monitoring is enabled but failed to start. */
		FindCloseChangeNotification(watch);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		return 1;
	}
	if (settings.application_count && !SetTimer(window, ID_APPLICATION_TIMER, APPLICATION_INTERVAL_MS, NULL)) {
		/* Application rules exist but their monitoring timer could not start. */
		fail_monitor(window, IDS_APPLICATION_TIMER_ERROR);
		FindCloseChangeNotification(watch);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		return 1;
	}
	if (!scan_applications(window)) {
		/* The initial application scan or prevention update failed. */
		FindCloseChangeNotification(watch);
		DestroyIcon(closed_icon);
		DestroyIcon(open_icon);
		return 1;
	}
	/* Cover changes between the initial load and starting the watch. */
	reload_settings(window);
	for (;;) {
		wait_result = MsgWaitForMultipleObjects(1, &watch, FALSE, INFINITE, QS_ALLINPUT);
		if (wait_result == WAIT_OBJECT_0) {
			if (!FindNextChangeNotification(watch) || !SetTimer(window, ID_SETTINGS_TIMER, SETTINGS_RELOAD_DELAY_MS, NULL)) {
				MessageBoxW(window, text(IDS_SETTINGS_WATCH_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
				exit_code = 1;
				break;
			}
		} else if (wait_result == WAIT_OBJECT_0 + 1) {
			while (PeekMessageW(&message, NULL, 0, 0, PM_REMOVE)) {
				if (message.message == WM_QUIT) {
					exit_code = (int)message.wParam;
					goto done;
				}
				TranslateMessage(&message);
				DispatchMessageW(&message);
			}
		} else {
			MessageBoxW(window, text(IDS_SETTINGS_WATCH_ERROR), text(IDS_TITLE), MB_OK | MB_ICONERROR);
			exit_code = 1;
			break;
		}
	}
	DestroyWindow(window);
done:
	FindCloseChangeNotification(watch);
	DestroyIcon(closed_icon);
	DestroyIcon(open_icon);
	CloseHandle(instance_mutex);
	return exit_code;
}
