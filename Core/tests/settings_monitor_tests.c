#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shellapi.h>
#include <strsafe.h>
#include <stdio.h>
#include <wchar.h>
#include "..\update_suppression.h"

static BOOL update_mock_enabled;
static int update_mock_calls;
static BOOL update_mock_failure;
static BOOL update_mock_rollback_failure;
static BOOL update_init_failure, update_recover_failure;
static int update_recover_calls, update_diagnostics;
static BOOL test_update_set(UpdateSuppression *state, BOOL enabled) {
	(void)state; ++update_mock_calls; if (update_mock_failure) { update_mock_failure = FALSE; state->rollback_failed = update_mock_rollback_failure; return FALSE; } update_mock_enabled = enabled; return TRUE;
}
static BOOL test_update_init(UpdateSuppression *state, HKEY root, const wchar_t *a, const wchar_t *b, const wchar_t *name) {
	(void)state; (void)root; (void)a; (void)b; (void)name; return !update_init_failure;
}
static BOOL test_update_recover(UpdateSuppression *state) { (void)state; ++update_recover_calls; return !update_recover_failure; }
static void WINAPI test_debug_output(LPCWSTR message) { (void)message; ++update_diagnostics; }
static BOOL test_update_close(UpdateSuppression *state) { return test_update_set(state, FALSE); }

static EXECUTION_STATE last_flags;
static int monitor_errors;
static const wchar_t *module_path = L"C:\\Program Files\\AutoAwake\\AutoAwakeCore.exe";
static BOOL module_path_failure;
static BOOL launch_failure;
static int launch_count;
static wchar_t launched_path[32768];
static BOOL mutex_failure;
static int power_creates, power_sets, power_clears;
static BOOL power_create_failure, power_set_failure, power_clear_failure;

static HANDLE WINAPI test_power_create(PREASON_CONTEXT reason)
{
	++power_creates;
	if (power_create_failure || reason->Version != POWER_REQUEST_CONTEXT_VERSION ||
		reason->Flags != POWER_REQUEST_CONTEXT_SIMPLE_STRING) return INVALID_HANDLE_VALUE;
	return CreateEventW(NULL, FALSE, FALSE, NULL);
}

static BOOL WINAPI test_power_set(HANDLE request, POWER_REQUEST_TYPE type)
{
	++power_sets;
	return request != NULL && type == PowerRequestDisplayRequired && !power_set_failure;
}

static BOOL WINAPI test_power_clear(HANDLE request, POWER_REQUEST_TYPE type)
{
	++power_clears;
	return request != NULL && type == PowerRequestDisplayRequired && !power_clear_failure;
}

static HANDLE WINAPI test_create_mutex(LPSECURITY_ATTRIBUTES attributes, BOOL owner, LPCWSTR name)
{
	HANDLE handle;
	(void)attributes;
	(void)owner;
	(void)name;
	if (mutex_failure) {
		SetLastError(ERROR_ACCESS_DENIED);
		return NULL;
	}
	handle = CreateEventW(NULL, FALSE, FALSE, NULL);
	if (handle) SetLastError(ERROR_ALREADY_EXISTS);
	return handle;
}

static int WINAPI test_load_string(HINSTANCE instance, UINT id, LPWSTR buffer, int capacity)
{
	(void)instance;
	(void)id;
	return SUCCEEDED(StringCchCopyW(buffer, capacity, L"test")) ? 4 : 0;
}

static DWORD WINAPI test_module_filename(HMODULE module, LPWSTR path, DWORD size)
{
	(void)module;
	if (module_path_failure) return 0;
	if (FAILED(StringCchCopyW(path, size, module_path))) return size;
	return (DWORD)wcslen(path);
}

static HINSTANCE WINAPI test_shell_execute(HWND window, LPCWSTR operation, LPCWSTR file,
	LPCWSTR parameters, LPCWSTR directory, INT show)
{
	(void)window;
	(void)operation;
	(void)parameters;
	(void)directory;
	(void)show;
	++launch_count;
	if (FAILED(StringCchCopyW(launched_path, ARRAYSIZE(launched_path), file))) return (HINSTANCE)(INT_PTR)SE_ERR_OOM;
	return (HINSTANCE)(INT_PTR)(launch_failure ? SE_ERR_FNF : 33);
}

/* Records a simulated sleep prevention request.
 * flags: Execution-state flags requested by the monitor.
 */
static EXECUTION_STATE WINAPI test_execution_state(EXECUTION_STATE flags)
{
	last_flags = flags;
	return flags;
}

/* Simulates a successful tray icon operation.
 * message: Requested notification-area operation, unused.
 * data: Tray icon data, unused.
 */
static BOOL WINAPI test_notify_icon(DWORD message, PNOTIFYICONDATAW data)
{
	(void)message;
	(void)data;
	return TRUE;
}

/* Counts simulated error dialogs without displaying them.
 * window: Dialog owner, unused.
 * message: Dialog text, unused.
 * title: Dialog title, unused.
 * type: Dialog style flags, unused.
 */
static int WINAPI test_message_box(HWND window, LPCWSTR message, LPCWSTR title, UINT type)
{
	(void)window;
	(void)message;
	(void)title;
	(void)type;
	++monitor_errors;
	return IDOK;
}

#define update_init test_update_init
#define update_recover test_update_recover
#define update_set test_update_set
#define update_close test_update_close
#define OutputDebugStringW test_debug_output
#define SetThreadExecutionState test_execution_state
#define PowerCreateRequest test_power_create
#define PowerSetRequest test_power_set
#define PowerClearRequest test_power_clear
#define Shell_NotifyIconW test_notify_icon
#define MessageBoxW test_message_box
#define GetModuleFileNameW test_module_filename
#define ShellExecuteW test_shell_execute
#define CreateMutexW test_create_mutex
#define LoadStringW test_load_string
#include "..\main.c"
#undef update_init
#undef update_recover
#undef update_set
#undef update_close
#undef CreateMutexW
#undef LoadStringW
#undef SetThreadExecutionState
#undef PowerCreateRequest
#undef PowerSetRequest
#undef PowerClearRequest
#undef Shell_NotifyIconW
#undef MessageBoxW
#undef GetModuleFileNameW
#undef ShellExecuteW

#define CHECK(condition) do { if (!(condition)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #condition); return 1; } } while (0)

/* Writes a UTF-16 test settings file.
 * path: Destination file path.
 * contents: Null-terminated contents, including any desired BOM.
 */
static BOOL write_settings(const wchar_t *path, const wchar_t *contents)
{
	HANDLE file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
	DWORD written;
	DWORD bytes = (DWORD)(wcslen(contents) * sizeof(wchar_t));
	BOOL ok;
	/* The test settings file could not be created or opened. */
	if (file == INVALID_HANDLE_VALUE) return FALSE;
	ok = WriteFile(file, contents, bytes, &written, NULL) && written == bytes;
	return CloseHandle(file) && ok;
}

/* Runs settings and monitoring tests, or acts as the short-lived child process.
 * argc: Argument count; more than one selects child mode.
 * argv: Argument values, unused.
 */
int main(int argc, char **argv)
{
	wchar_t directory[MAX_PATH], ini[MAX_PATH], child_path[MAX_PATH], executable[32768], contents[65536];
	AwakeSettings loaded = { 0 };
	AwakeSettings invalid = { 0 };
	STARTUPINFOW startup = { 0 };
	PROCESS_INFORMATION child = { 0 };
	wchar_t command[] = L"child.exe child";
	HANDLE file;
	HWND top_window, child_window;
	DWORD length, bytes;

	(void)argv;
	if (argc > 1) {
		/* An argument selects the short-lived child process used by the monitor test. */
		Sleep(3000);
		return 0;
	}
	window_proc(NULL, WM_TRAY, ID_TRAY, WM_LBUTTONUP);
	window_proc(NULL, WM_TRAY, ID_TRAY + 1, WM_LBUTTONDBLCLK);
	CHECK(launch_count == 0 && monitor_errors == 0);
	window_proc(NULL, WM_TRAY, ID_TRAY, WM_LBUTTONDBLCLK);
	CHECK(launch_count == 1 && monitor_errors == 0);
	CHECK(wcscmp(launched_path, L"C:\\Program Files\\AutoAwake\\AutoAwakeSettings.exe") == 0);
	launch_failure = TRUE;
	window_proc(NULL, WM_TRAY, ID_TRAY, WM_LBUTTONDBLCLK);
	CHECK(launch_count == 2 && monitor_errors == 1);
	launch_failure = FALSE;
	module_path_failure = TRUE;
	window_proc(NULL, WM_TRAY, ID_TRAY, WM_LBUTTONDBLCLK);
	CHECK(launch_count == 2 && monitor_errors == 2);
	module_path_failure = FALSE;
	module_path = L"AutoAwakeCore.exe";
	window_proc(NULL, WM_TRAY, ID_TRAY, WM_LBUTTONDBLCLK);
	CHECK(launch_count == 2 && monitor_errors == 3);
	monitor_errors = 0;
	module_path = L"C:\\Program Files\\AutoAwake\\AutoAwakeCore.exe";
	CHECK(WinMain(NULL, NULL, NULL, 0) == 0);
	CHECK(launch_count == 3 && monitor_errors == 0);
	CHECK(wcscmp(launched_path, L"C:\\Program Files\\AutoAwake\\AutoAwakeSettings.exe") == 0);
	launch_failure = TRUE;
	CHECK(WinMain(NULL, NULL, NULL, 0) == 1);
	CHECK(launch_count == 4 && monitor_errors == 1);
	launch_failure = FALSE;
	mutex_failure = TRUE;
	CHECK(WinMain(NULL, NULL, NULL, 0) == 1);
	CHECK(launch_count == 4 && monitor_errors == 2);
	mutex_failure = FALSE;
	monitor_errors = 0;
	CHECK(GetCurrentDirectoryW(ARRAYSIZE(directory), directory) != 0);
	CHECK(GetTempFileNameW(directory, L"awk", 0, ini) != 0);
	CHECK(DeleteFileW(ini));
	CHECK(settings_load_file(ini, &loaded) == SETTINGS_OK);
	CHECK(loaded.interval_seconds == 30 && loaded.cpu_usage_enabled && loaded.cpu_usage_threshold == 80);
	CHECK(loaded.cpu_usage_action == CPU_USAGE_KEEP_SYSTEM_AWAKE && loaded.application_count == 0 && loaded.suppress_windows_update);
	settings_free(&loaded);
	file = CreateFileW(ini, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	CHECK(file != INVALID_HANDLE_VALUE);
	CHECK(ReadFile(file, contents, sizeof(contents) - sizeof(wchar_t), &bytes, NULL));
	CHECK(CloseHandle(file));
	CHECK(bytes % sizeof(wchar_t) == 0);
	contents[bytes / sizeof(wchar_t)] = L'\0';
	CHECK(wcscmp(contents, L"\xFEFF[General]\r\nInterval=30\r\nCpuUsageEnabled=1\r\nCpuUsageThreshold=80\r\nCpuUsageAction=NoSleep\r\nSuppressWindowsUpdate=1\r\n") == 0);
	length = GetModuleFileNameW(NULL, executable, ARRAYSIZE(executable));
	CHECK(length > 0 && length < ARRAYSIZE(executable));
	CHECK(SUCCEEDED(StringCchPrintfW(contents, ARRAYSIZE(contents),
		L"\xFEFF[General]\r\nCpuUsageAction=keepunlocked\r\n[App]\r\nPath=%s\r\nAction=KEEPUNLOCKED\r\n", executable)));
	CHECK(write_settings(ini, contents));
	CHECK(settings_load_file(ini, &loaded) == SETTINGS_OK);
	CHECK(loaded.cpu_usage_action == CPU_USAGE_KEEP_UNLOCKED && loaded.application_count == 1);
	CHECK(loaded.applications[0].action == APPLICATION_KEEP_UNLOCKED);
	settings_free(&loaded);
	CHECK(SUCCEEDED(StringCchPrintfW(contents, ARRAYSIZE(contents),
		L"\xFEFF[General]\r\nCpuUsageEnabled=0\r\n[First]\r\nPath=%s\r\nAction=keepdisplay\r\nVisibleWindowOnly=1\r\n[Second]\r\nPath=%s\r\nAction=NoSleep\r\n", executable, executable)));
	CHECK(write_settings(ini, contents));
	CHECK(settings_load_file(ini, &loaded) == SETTINGS_OK);
	CHECK(!loaded.cpu_usage_enabled && loaded.application_count == 2);
	CHECK(loaded.applications[0].action == APPLICATION_KEEP_DISPLAY_AWAKE && loaded.applications[0].visible_window_only);
	CHECK(loaded.applications[1].action == APPLICATION_KEEP_SYSTEM_AWAKE && !loaded.applications[1].visible_window_only);
	CHECK(CompareStringOrdinal(loaded.applications[0].path, -1, executable, -1, TRUE) == CSTR_EQUAL);

	settings = loaded;
	top_window = CreateWindowExW(0, L"STATIC", L"Awake test", WS_OVERLAPPEDWINDOW,
		0, 0, 100, 100, NULL, NULL, GetModuleHandleW(NULL), NULL);
	CHECK(top_window != NULL);
	child_window = CreateWindowExW(0, L"STATIC", L"child", WS_CHILD | WS_VISIBLE,
		0, 0, 10, 10, top_window, NULL, GetModuleHandleW(NULL), NULL);
	CHECK(child_window != NULL);
	CHECK(scan_applications(NULL));
	CHECK(application_no_sleep && !application_keep_display && update_mock_enabled);
	settings.suppress_windows_update = FALSE;
	CHECK(update_prevention(NULL) && !update_mock_enabled);
	settings.suppress_windows_update = TRUE;
	CHECK(update_prevention(NULL) && update_mock_enabled);
	ShowWindow(top_window, SW_SHOWNOACTIVATE);
	CHECK(IsWindowVisible(top_window));
	CHECK(scan_applications(NULL));
	CHECK(application_no_sleep && application_keep_display);
	CHECK((last_flags & (ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)) == ES_SYSTEM_REQUIRED);
	ShowWindow(top_window, SW_MINIMIZE);
	CHECK(IsWindowVisible(top_window));
	CHECK(scan_applications(NULL) && application_keep_display);
	ShowWindow(top_window, SW_HIDE);
	CHECK(scan_applications(NULL) && !application_keep_display && application_no_sleep);
	settings.applications[1].visible_window_only = TRUE;
	CHECK(scan_applications(NULL) && !active);
	ShowWindow(top_window, SW_SHOWNOACTIVATE);
	settings.applications[1].action = APPLICATION_KEEP_DISPLAY_AWAKE;
	CHECK(scan_applications(NULL));
	CHECK(!application_no_sleep && application_keep_display);
	CHECK((last_flags & (ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)) == (ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED));
	settings.applications[1].action = APPLICATION_KEEP_UNLOCKED;
	CHECK(scan_applications(NULL) && application_keep_unlocked && application_keep_display && !display_request);
	settings.applications[0].action = APPLICATION_KEEP_UNLOCKED;
	CHECK(scan_applications(NULL) && application_keep_unlocked && !application_keep_display && display_request);
	settings.applications[1].action = APPLICATION_KEEP_SYSTEM_AWAKE;
	CHECK(scan_applications(NULL) && application_no_sleep && !display_request);
	settings.applications[0].action = APPLICATION_KEEP_DISPLAY_AWAKE;
	settings.applications[1].action = APPLICATION_KEEP_DISPLAY_AWAKE;
	CHECK(scan_applications(NULL) && !application_keep_unlocked && application_keep_display);
	power_creates = power_sets = power_clears = 0;
	settings.cpu_usage_enabled = TRUE;
	settings.cpu_usage_threshold = 80;
	settings.cpu_usage_action = CPU_USAGE_KEEP_SYSTEM_AWAKE;
	have_sample = TRUE;
	last_cpu_usage = 80;
	CHECK(update_prevention(NULL));
	CHECK((last_flags & (ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)) == ES_SYSTEM_REQUIRED);
	last_cpu_usage = 79;
	CHECK(update_prevention(NULL));
	CHECK((last_flags & ES_DISPLAY_REQUIRED) != 0);
	CHECK(DestroyWindow(top_window));
	CHECK(scan_applications(NULL) && !active);
	settings_free(&settings);
	CHECK(scan_applications(NULL));
	CHECK((last_flags & (ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)) == 0);
	CHECK(!active && monitor_errors == 0);

	settings.cpu_usage_enabled = TRUE;
	settings.cpu_usage_threshold = 80;
	settings.cpu_usage_action = CPU_USAGE_KEEP_UNLOCKED;
	have_sample = TRUE;
	last_cpu_usage = 80;
	CHECK(update_prevention(NULL));
	CHECK(active && display_request && power_creates == 1 && power_sets == 1);
	CHECK((last_flags & ES_SYSTEM_REQUIRED) != 0);
	CHECK(update_prevention(NULL) && power_creates == 1 && power_sets == 1);
	application_keep_display = TRUE;
	CHECK(update_prevention(NULL));
	CHECK(!display_request && power_clears == 1 && (last_flags & ES_DISPLAY_REQUIRED));
	application_keep_display = FALSE;
	CHECK(update_prevention(NULL) && display_request && power_creates == 2);
	application_no_sleep = TRUE;
	CHECK(update_prevention(NULL) && !display_request && power_clears == 2);
	CHECK((last_flags & (ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED)) == ES_SYSTEM_REQUIRED);
	application_no_sleep = FALSE;
	CHECK(update_prevention(NULL) && display_request && power_creates == 3);
	window_proc(NULL, WM_POWERBROADCAST, PBT_APMRESUMEAUTOMATIC, 0);
	CHECK(display_request && power_creates == 4 && power_clears == 2);
	last_cpu_usage = 79;
	CHECK(update_prevention(NULL) && !active && !display_request && power_clears == 3);
	power_create_failure = TRUE;
	last_cpu_usage = 80;
	CHECK(!update_prevention(NULL) && !active && !display_request && monitor_errors == 1);
	CHECK(last_flags == ES_CONTINUOUS);
	power_create_failure = FALSE;
	power_set_failure = TRUE;
	have_sample = TRUE;
	CHECK(!update_prevention(NULL) && !active && !display_request && monitor_errors == 2);
	CHECK(last_flags == ES_CONTINUOUS);
	power_set_failure = FALSE;
	have_sample = TRUE;
	CHECK(update_prevention(NULL) && display_request);
	power_clear_failure = TRUE;
	last_cpu_usage = 79;
	CHECK(!update_prevention(NULL) && !active && !display_request && monitor_errors == 3);
	power_clear_failure = FALSE;
	monitor_errors = 0;
	settings.cpu_usage_enabled = FALSE;
	have_sample = FALSE;

	CHECK(GetTempFileNameW(directory, L"awp", 0, child_path) != 0);
	CHECK(CopyFileW(executable, child_path, FALSE));
	CHECK(SUCCEEDED(StringCchPrintfW(contents, ARRAYSIZE(contents),
		L"\xFEFF[General]\r\nCpuUsageEnabled=0\r\n[Child]\r\nPath=%s\r\nAction=KeepDisplay\r\nVisibleWindowOnly=1\r\n", child_path)));
	CHECK(write_settings(ini, contents));
	CHECK(settings_load_file(ini, &settings) == SETTINGS_OK);
	CHECK(scan_applications(NULL) && !active);
	top_window = CreateWindowExW(0, L"STATIC", L"Awake test", WS_OVERLAPPEDWINDOW | WS_VISIBLE,
		0, 0, 100, 100, NULL, NULL, GetModuleHandleW(NULL), NULL);
	CHECK(top_window != NULL && IsWindowVisible(top_window));
	startup.cb = sizeof(startup);
	CHECK(CreateProcessW(child_path, command, NULL, NULL, FALSE, CREATE_NO_WINDOW, NULL, NULL, &startup, &child));
	CHECK(scan_applications(NULL) && !active);
	settings.applications[0].visible_window_only = FALSE;
	CHECK(scan_applications(NULL) && active && application_keep_display);
	CHECK(DestroyWindow(top_window));
	CHECK(WaitForSingleObject(child.hProcess, 10000) == WAIT_OBJECT_0);
	CloseHandle(child.hThread);
	CloseHandle(child.hProcess);
	CHECK(scan_applications(NULL) && !active);
	CHECK(write_settings(ini, L"\xFEFF[General]\r\n[Bad]\r\nPath=relative.exe\r\nAction=NoSleep\r\n"));
	CHECK(settings_load_file(ini, &settings) == SETTINGS_INVALID_APPLICATION_PATH);
	CHECK(settings.application_count == 1 && CompareStringOrdinal(settings.applications[0].path, -1, child_path, -1, TRUE) == CSTR_EQUAL);
	settings_free(&settings);
	CHECK(DeleteFileW(child_path));

	CHECK(write_settings(ini, L"\xFEFF[General]\r\n[Bad]\r\nPath=relative.exe\r\nAction=NoSleep\r\n"));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_INVALID_APPLICATION_PATH);
	CHECK(invalid.applications == NULL && invalid.application_count == 0);
	CHECK(SUCCEEDED(StringCchPrintfW(contents, ARRAYSIZE(contents), L"\xFEFF[General]\r\n[Bad]\r\nPath=%s\r\nAction=unknown\r\n", executable)));
	CHECK(write_settings(ini, contents));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_INVALID_APPLICATION_ACTION);
	CHECK(SUCCEEDED(StringCchPrintfW(contents, ARRAYSIZE(contents), L"\xFEFF[General]\r\n[Bad]\r\nPath=%s\r\nAction=NoSleep\r\nVisibleWindowOnly=2\r\n", executable)));
	CHECK(write_settings(ini, contents));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_INVALID_APPLICATION_VISIBLE_WINDOW);
	CHECK(SUCCEEDED(StringCchPrintfW(contents, ARRAYSIZE(contents), L"\xFEFF[General]\r\n[Bad]\r\nPath=%s\r\nAction=NoSleep\r\nVisibleWindowOnly=\r\n", executable)));
	CHECK(write_settings(ini, contents));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_INVALID_APPLICATION_VISIBLE_WINDOW);
	CHECK(SUCCEEDED(StringCchPrintfW(contents, ARRAYSIZE(contents), L"\xFEFF[General]\r\n[App]\r\nPath=%s\r\nAction=NoSleep\r\nVisibleWindowOnly=0\r\n", executable)));
	CHECK(write_settings(ini, contents));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_OK);
	CHECK(invalid.application_count == 1 && !invalid.applications[0].visible_window_only);
	settings_free(&invalid);
	CHECK(write_settings(ini, L"\xFEFF[General]\r\nCpuUsageEnabled=0\r\n[Settings]\r\nWindowX=-100\r\nWindowY=50\r\nWindowWidth=800\r\nWindowHeight=600\r\n"));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_OK);
	CHECK(invalid.application_count == 0 && !invalid.cpu_usage_enabled);
	settings_free(&invalid);
	CHECK(write_settings(ini, L"\xFEFF[General]\r\nEnabled=0\r\n"));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_OK);
	CHECK(invalid.cpu_usage_enabled);
	settings_free(&invalid);
	CHECK(settings_directory(contents, ARRAYSIZE(contents)));
	CHECK(wcslen(contents) >= wcslen(L"\\AutoAwake"));
	CHECK(wcscmp(contents + wcslen(contents) - wcslen(L"\\AutoAwake"), L"\\AutoAwake") == 0);
	CHECK(write_settings(ini, L"\xFEFF[General]\r\nSuppressWindowsUpdate=0\r\n"));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_OK && !invalid.suppress_windows_update);
	settings_free(&invalid);
	CHECK(write_settings(ini, L"\xFEFF[General]\r\nSuppressWindowsUpdate=2\r\n"));
	CHECK(settings_load_file(ini, &invalid) == SETTINGS_INVALID_SUPPRESS_WINDOWS_UPDATE);
	update_init_failure = TRUE;
	initialize_update_suppression();
	CHECK(update_diagnostics == 1 && update_recover_calls == 0 && monitor_errors == 0);
	update_init_failure = FALSE;
	update_recover_failure = TRUE;
	initialize_update_suppression();
	CHECK(update_diagnostics == 2 && update_recover_calls == 1 && monitor_errors == 0);
	update_recover_failure = FALSE;
	update_mock_enabled = TRUE;
	update_mock_failure = TRUE;
	window_proc(NULL, WM_ENDSESSION, TRUE, 0);
	CHECK(update_mock_enabled && monitor_errors == 0 && update_diagnostics == 3);
	window_proc(NULL, WM_ENDSESSION, TRUE, 0);
	CHECK(!update_mock_enabled);
	update_mock_failure = TRUE;
	window_proc(NULL, WM_DESTROY, 0, 0);
	CHECK(monitor_errors == 0 && update_diagnostics == 4);
	settings.suppress_windows_update = TRUE;
	update_mock_failure = TRUE;
	update_mock_rollback_failure = TRUE;
	CHECK(set_prevention(NULL, TRUE, CPU_USAGE_KEEP_SYSTEM_AWAKE));
	CHECK(active && (last_flags & ES_SYSTEM_REQUIRED) && monitor_errors == 0 && update_diagnostics == 5);
	CHECK(set_prevention(NULL, TRUE, CPU_USAGE_KEEP_SYSTEM_AWAKE) && update_mock_enabled);
	update_mock_failure = TRUE;
	CHECK(set_prevention(NULL, FALSE, CPU_USAGE_KEEP_SYSTEM_AWAKE));
	CHECK(!active && last_flags == ES_CONTINUOUS && monitor_errors == 0 && update_diagnostics == 6);
	CHECK(set_prevention(NULL, FALSE, CPU_USAGE_KEEP_SYSTEM_AWAKE) && !update_mock_enabled);
	CHECK(DeleteFileW(ini));
	puts("settings and monitor tests passed");
	return 0;
}
