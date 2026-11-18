#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <shlobj.h>
#include <strsafe.h>
#include <string.h>
#include <wchar.h>
#include "settings.h"

#define APPLICATION_PATH_CAPACITY 32768

void settings_free(AwakeSettings *settings)
{
	size_t i;
	for (i = 0; i < settings->application_count; ++i) {
		free(settings->applications[i].path);
	}
	free(settings->applications);
	settings->applications = NULL;
	settings->application_count = 0;
}

/* Loads application rules from all non-General INI sections.
 * file: Path to the settings file.
 * section_capacity: Capacity in wide characters for the section-name list.
 * settings: Receives the allocated application rules; rules are freed on failure.
 */
static SettingsResult load_applications(const wchar_t *file, DWORD section_capacity, AwakeSettings *settings)
{
	wchar_t *sections = malloc((size_t)section_capacity * sizeof(wchar_t));
	wchar_t *path = malloc(APPLICATION_PATH_CAPACITY * sizeof(wchar_t));
	wchar_t *normalized = malloc(APPLICATION_PATH_CAPACITY * sizeof(wchar_t));
	wchar_t *section;
	SettingsResult result = SETTINGS_OK;
	DWORD length;

	if (!sections || !path || !normalized) {
		result = SETTINGS_IO_ERROR;
		goto done;
	}
	sections[0] = L'\0';
	length = GetPrivateProfileSectionNamesW(sections, section_capacity, file);
	if (length >= section_capacity - 2) {
		result = SETTINGS_IO_ERROR;
		goto done;
	}
	for (section = sections; *section; section += wcslen(section) + 1) {
		ApplicationSettings entry;
		ApplicationSettings *entries;
		wchar_t action[16];
		wchar_t visible_window[16];
		DWORD normalized_length;
		if (lstrcmpiW(section, L"General") == 0 || lstrcmpiW(section, L"Settings") == 0) {
			continue;
		}
		length = GetPrivateProfileStringW(section, L"Path", L"", path, APPLICATION_PATH_CAPACITY, file);
		if (!length || length >= APPLICATION_PATH_CAPACITY - 1 || path[length - 1] == L'\\' ||
			!((length >= 3 && ((path[0] >= L'A' && path[0] <= L'Z') ||
								(path[0] >= L'a' && path[0] <= L'z')) && path[1] == L':' && path[2] == L'\\') ||
			  (length >= 3 && path[0] == L'\\' && path[1] == L'\\'))) {
			result = SETTINGS_INVALID_APPLICATION_PATH;
			goto done;
		}
		normalized_length = GetFullPathNameW(path, APPLICATION_PATH_CAPACITY, normalized, NULL);
		if (!normalized_length || normalized_length >= APPLICATION_PATH_CAPACITY) {
			result = SETTINGS_INVALID_APPLICATION_PATH;
			goto done;
		}
		length = GetPrivateProfileStringW(section, L"Action", L"", action, ARRAYSIZE(action), file);
		if (!length || length >= ARRAYSIZE(action) - 1) {
			result = SETTINGS_INVALID_APPLICATION_ACTION;
			goto done;
		}
		if (lstrcmpiW(action, L"NoSleep") == 0) {
			entry.action = APPLICATION_KEEP_SYSTEM_AWAKE;
		} else if (lstrcmpiW(action, L"KeepDisplay") == 0) {
			entry.action = APPLICATION_KEEP_DISPLAY_AWAKE;
		} else if (lstrcmpiW(action, L"KeepUnlocked") == 0) {
			entry.action = APPLICATION_KEEP_UNLOCKED;
		} else {
			result = SETTINGS_INVALID_APPLICATION_ACTION;
			goto done;
		}
		GetPrivateProfileStringW(section, L"VisibleWindowOnly", L"#missing#", visible_window, ARRAYSIZE(visible_window), file);
		if (lstrcmpW(visible_window, L"#missing#") == 0 || lstrcmpW(visible_window, L"0") == 0) {
			entry.visible_window_only = FALSE;
		} else if (lstrcmpW(visible_window, L"1") == 0) {
			entry.visible_window_only = TRUE;
		} else {
			result = SETTINGS_INVALID_APPLICATION_VISIBLE_WINDOW;
			goto done;
		}
		entry.path = malloc(((size_t)normalized_length + 1) * sizeof(wchar_t));
		if (!entry.path) {
			result = SETTINGS_IO_ERROR;
			goto done;
		}
		memcpy(entry.path, normalized, ((size_t)normalized_length + 1) * sizeof(wchar_t));
		entries = realloc(settings->applications, (settings->application_count + 1) * sizeof(*entries));
		if (!entries) {
			free(entry.path);
			result = SETTINGS_IO_ERROR;
			goto done;
		}
		settings->applications = entries;
		settings->applications[settings->application_count++] = entry;
	}
done:
	free(sections);
	free(path);
	free(normalized);
	if (result != SETTINGS_OK) {
		settings_free(settings);
	}
	return result;
}

/* Parses a bounded unsigned decimal value.
 * value: Wide-character digit sequence.
 * length: Number of characters in the sequence.
 * maximum: Largest permitted value.
 * number: Receives the parsed value on success.
 */
static BOOL parse_number(const wchar_t *value, DWORD length, DWORD maximum, DWORD *number)
{
	DWORD i, result = 0;
	if (!length || length >= 15) {
		/* The number is empty or too long to parse. */
		return FALSE;
	}
	for (i = 0; i < length; ++i) {
		DWORD digit;
		if (value[i] < L'0' || value[i] > L'9') {
			/* The input contains a non-decimal character. */
			return FALSE;
		}
		digit = value[i] - L'0';
		if (result > (maximum - digit) / 10) {
			/* Adding this digit would exceed the allowed maximum. */
			return FALSE;
		}
		result = result * 10 + digit;
	}
	*number = result;
	return TRUE;
}

/* Converts an upper-case key name to a virtual-key code.
 * name: Key name such as "A", "F9", "NUM5" or "PAGEUP".
 * key: Receives the virtual-key code on success.
 */
static BOOL parse_hotkey_key(const wchar_t *name, UINT *key)
{
	static const struct { const wchar_t *name; UINT key; } names[] = {
		{ L"SPACE", VK_SPACE }, { L"ENTER", VK_RETURN }, { L"TAB", VK_TAB }, { L"ESC", VK_ESCAPE },
		{ L"UP", VK_UP }, { L"DOWN", VK_DOWN }, { L"LEFT", VK_LEFT }, { L"RIGHT", VK_RIGHT },
		{ L"HOME", VK_HOME }, { L"END", VK_END }, { L"PAGEUP", VK_PRIOR }, { L"PAGEDOWN", VK_NEXT },
		{ L"INSERT", VK_INSERT }, { L"DELETE", VK_DELETE }
	};
	size_t i;
	DWORD number;
	size_t length = wcslen(name);
	if (length == 1 && ((name[0] >= L'A' && name[0] <= L'Z') || (name[0] >= L'0' && name[0] <= L'9'))) {
		*key = name[0];
		return TRUE;
	}
	if (name[0] == L'F' && length >= 2 && parse_number(name + 1, (DWORD)length - 1, 24, &number) && number >= 1 &&
		name[1] != L'0') {
		*key = VK_F1 + number - 1;
		return TRUE;
	}
	if (length == 4 && wcsncmp(name, L"NUM", 3) == 0 && name[3] >= L'0' && name[3] <= L'9') {
		*key = VK_NUMPAD0 + (name[3] - L'0');
		return TRUE;
	}
	for (i = 0; i < ARRAYSIZE(names); ++i) {
		if (wcscmp(name, names[i].name) == 0) {
			*key = names[i].key;
			return TRUE;
		}
	}
	return FALSE;
}

BOOL settings_parse_hotkey(const wchar_t *value, Hotkey *hotkey)
{
	wchar_t buffer[64];
	wchar_t *token;
	wchar_t *context = NULL;
	Hotkey parsed = { 0, 0 };
	BOOL have_key = FALSE;
	size_t length = wcslen(value);
	size_t i, count = 0;

	if (length >= ARRAYSIZE(buffer) || value[0] == L'+' || (length && value[length - 1] == L'+')) {
		return FALSE;
	}
	for (i = 0; i < length; ++i) {
		if (value[i] == L'+') ++count;
	}
	if (!count) return FALSE;
	for (i = 0; i <= length; ++i) {
		buffer[i] = (value[i] >= L'a' && value[i] <= L'z') ? (wchar_t)(value[i] - L'a' + L'A') : value[i];
	}
	for (token = wcstok_s(buffer, L"+", &context); token; token = wcstok_s(NULL, L"+", &context)) {
		UINT modifier = 0;
		size_t start = 0, end = wcslen(token);
		while (token[start] == L' ') ++start;
		while (end > start && token[end - 1] == L' ') --end;
		token[end] = L'\0';
		token += start;
		if (have_key) return FALSE;
		if (wcscmp(token, L"CTRL") == 0) modifier = MOD_CONTROL;
		else if (wcscmp(token, L"ALT") == 0) modifier = MOD_ALT;
		else if (wcscmp(token, L"SHIFT") == 0) modifier = MOD_SHIFT;
		else if (wcscmp(token, L"WIN") == 0) modifier = MOD_WIN;
		if (modifier) {
			if (parsed.modifiers & modifier) return FALSE;
			parsed.modifiers |= modifier;
		} else if (parse_hotkey_key(token, &parsed.virtual_key)) {
			have_key = TRUE;
		} else {
			return FALSE;
		}
	}
	if (!have_key || !parsed.modifiers) return FALSE;
	*hotkey = parsed;
	return TRUE;
}

/* Loads and validates the four baseline hotkeys from the General section.
 * path: Path to the settings file.
 * hotkeys: Receives the hotkeys on success.
 */
static SettingsResult load_hotkeys(const wchar_t *path, Hotkey hotkeys[HOTKEY_COUNT])
{
	static const wchar_t *const names[HOTKEY_COUNT] = {
		L"HotkeyAuto", L"HotkeyNoSleep", L"HotkeyKeepDisplay", L"HotkeyKeepUnlocked"
	};
	Hotkey parsed[HOTKEY_COUNT] = { 0 };
	int i, j;
	for (i = 0; i < HOTKEY_COUNT; ++i) {
		wchar_t value[64];
		DWORD length = GetPrivateProfileStringW(L"General", names[i], L"", value, ARRAYSIZE(value), path);
		if (!length) continue;
		if (length >= ARRAYSIZE(value) - 1 || !settings_parse_hotkey(value, &parsed[i])) {
			return SETTINGS_INVALID_HOTKEY;
		}
		for (j = 0; j < i; ++j) {
			if (parsed[j].virtual_key == parsed[i].virtual_key && parsed[j].modifiers == parsed[i].modifiers) {
				/* The same combination is assigned to two baselines. */
				return SETTINGS_INVALID_HOTKEY;
			}
		}
	}
	memcpy(hotkeys, parsed, sizeof(parsed));
	return SETTINGS_OK;
}

/* Loads and validates a settings file, optionally creating it with defaults.
 * path: Path to the settings file.
 * settings: Receives the loaded settings only on success.
 * create: Whether to create the file if it is missing.
 */
static SettingsResult load_file(const wchar_t *path, AwakeSettings *settings, BOOL create)
{
	static const wchar_t initial_contents[] = L"\xFEFF[General]\r\nInterval=30\r\nCpuUsageEnabled=1\r\nCpuUsageThreshold=80\r\nCpuUsageAction=NoSleep\r\nSuppressWindowsUpdate=1\r\n";
	HANDLE file;
	DWORD transferred;
	DWORD value_length;
	DWORD interval_length;
	DWORD threshold_length;
	DWORD action_length;
	LARGE_INTEGER size;
	wchar_t bom;
	wchar_t value[16];
	wchar_t interval_value[16];
	wchar_t threshold_value[16];
	wchar_t action_value[16];
	wchar_t suppress_value[16];
	AwakeSettings loaded = { 30, TRUE, 80, CPU_USAGE_KEEP_SYSTEM_AWAKE, NULL, 0, TRUE };
	SettingsResult result;

	if (create) {
		file = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, NULL);
		if (file != INVALID_HANDLE_VALUE) {
			BOOL written = WriteFile(file, initial_contents, sizeof(initial_contents) - sizeof(wchar_t), &transferred, NULL);
			BOOL closed = CloseHandle(file);
			if (!written || transferred != sizeof(initial_contents) - sizeof(wchar_t) || !closed) {
				/* The new default file could not be written and closed completely. */
				DeleteFileW(path);
				return SETTINGS_IO_ERROR;
			}
		} else if (GetLastError() != ERROR_FILE_EXISTS) {
			/* File creation failed for a reason other than an existing file. */
			return SETTINGS_IO_ERROR;
		}
	}

	file = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	if (file == INVALID_HANDLE_VALUE) {
		/* The settings file could not be opened for reading. */
		return SETTINGS_IO_ERROR;
	}
	if (!GetFileSizeEx(file, &size) || !ReadFile(file, &bom, sizeof(bom), &transferred, NULL)) {
		/* The file size or byte-order mark could not be read. */
		CloseHandle(file);
		return SETTINGS_IO_ERROR;
	}
	if (transferred != sizeof(bom) || bom != 0xFEFF || size.QuadPart % sizeof(wchar_t) != 0) {
		/* The file lacks a UTF-16 LE BOM or has an incomplete code unit. */
		CloseHandle(file);
		return SETTINGS_INVALID_ENCODING;
	}
	value_length = GetPrivateProfileStringW(L"General", L"CpuUsageEnabled", L"1", value, ARRAYSIZE(value), path);
	interval_length = GetPrivateProfileStringW(L"General", L"Interval", L"30", interval_value, ARRAYSIZE(interval_value), path);
	threshold_length = GetPrivateProfileStringW(L"General", L"CpuUsageThreshold", L"80", threshold_value, ARRAYSIZE(threshold_value), path);
	action_length = GetPrivateProfileStringW(L"General", L"CpuUsageAction", L"NoSleep", action_value, ARRAYSIZE(action_value), path);
	GetPrivateProfileStringW(L"General", L"SuppressWindowsUpdate", L"1", suppress_value, ARRAYSIZE(suppress_value), path);
	if (!CloseHandle(file)) {
		/* The settings file handle could not be closed. */
		return SETTINGS_IO_ERROR;
	}
	if (lstrcmpW(suppress_value, L"1") == 0) loaded.suppress_windows_update = TRUE;
	else if (lstrcmpW(suppress_value, L"0") == 0) loaded.suppress_windows_update = FALSE;
	else return SETTINGS_INVALID_SUPPRESS_WINDOWS_UPDATE;
	if (!value_length) {
		/* The CPU enable value is empty. */
		return SETTINGS_INVALID_VALUE;
	}
	if (lstrcmpW(value, L"1") == 0) {
		loaded.cpu_usage_enabled = TRUE;
	} else if (lstrcmpW(value, L"0") == 0) {
		loaded.cpu_usage_enabled = FALSE;
	} else {
		/* The CPU enable value is neither 0 nor 1. */
		return SETTINGS_INVALID_VALUE;
	}
	if (!parse_number(interval_value, interval_length, 86400, &loaded.interval_seconds) || !loaded.interval_seconds) {
		/* The interval is not a whole number between 1 and 86400. */
		return SETTINGS_INVALID_INTERVAL;
	}
	if (!parse_number(threshold_value, threshold_length, 100, &loaded.cpu_usage_threshold)) {
		/* The threshold is not a whole percentage from 0 to 100. */
		return SETTINGS_INVALID_CPU_THRESHOLD;
	}
	if (!action_length || action_length == ARRAYSIZE(action_value) - 1) {
		/* The CPU action is empty or may have been truncated. */
		return SETTINGS_INVALID_CPU_ACTION;
	}
	if (lstrcmpiW(action_value, L"NoSleep") == 0) {
		loaded.cpu_usage_action = CPU_USAGE_KEEP_SYSTEM_AWAKE;
	} else if (lstrcmpiW(action_value, L"KeepDisplay") == 0) {
		loaded.cpu_usage_action = CPU_USAGE_KEEP_DISPLAY_AWAKE;
	} else if (lstrcmpiW(action_value, L"KeepUnlocked") == 0) {
		loaded.cpu_usage_action = CPU_USAGE_KEEP_UNLOCKED;
	} else {
		/* The CPU action does not name a supported mode. */
		return SETTINGS_INVALID_CPU_ACTION;
	}
	result = load_hotkeys(path, loaded.hotkeys);
	if (result != SETTINGS_OK) {
		/* A hotkey value is invalid. */
		return result;
	}
	if (size.QuadPart / sizeof(wchar_t) > 0x7ffffffd) {
		/* The file is too large for the section-name buffer capacity. */
		return SETTINGS_IO_ERROR;
	}
	result = load_applications(path, (DWORD)(size.QuadPart / sizeof(wchar_t)) + 2, &loaded);
	if (result != SETTINGS_OK) {
		/* At least one application rule could not be loaded. */
		return result;
	}
	*settings = loaded;
	return SETTINGS_OK;
}

SettingsResult settings_load_file(const wchar_t *path, AwakeSettings *settings)
{
	return load_file(path, settings, TRUE);
}

BOOL settings_directory(wchar_t *path, size_t capacity)
{
	wchar_t appdata[MAX_PATH];
	if (FAILED(SHGetFolderPathW(NULL, CSIDL_APPDATA | CSIDL_FLAG_CREATE, NULL, SHGFP_TYPE_CURRENT, appdata))) {
		/* The roaming application data directory is unavailable. */
		return FALSE;
	}
	return SUCCEEDED(StringCchPrintfW(path, capacity, L"%s\\AutoAwake", appdata));
}

/* Loads settings from the default directory.
 * settings: Receives the loaded settings only on success.
 * create: Whether to create the directory and file if missing.
 */
static SettingsResult load_settings(AwakeSettings *settings, BOOL create)
{
	wchar_t path[MAX_PATH];
	if (!settings_directory(path, ARRAYSIZE(path))) {
		/* The default settings directory could not be resolved. */
		return SETTINGS_IO_ERROR;
	}
	if (create && !CreateDirectoryW(path, NULL) && GetLastError() != ERROR_ALREADY_EXISTS) {
		/* The default settings directory could not be created. */
		return SETTINGS_IO_ERROR;
	}
	if (FAILED(StringCchCatW(path, ARRAYSIZE(path), L"\\settings.ini"))) {
		/* The default settings path does not fit in the buffer. */
		return SETTINGS_IO_ERROR;
	}
	return load_file(path, settings, create);
}

SettingsResult settings_load(AwakeSettings *settings)
{
	return load_settings(settings, TRUE);
}

SettingsResult settings_reload(AwakeSettings *settings)
{
	return load_settings(settings, FALSE);
}
