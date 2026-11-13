#ifndef AWAKE_SETTINGS_H
#define AWAKE_SETTINGS_H

#include <windows.h>

typedef enum CpuUsageAction {
	CPU_USAGE_KEEP_SYSTEM_AWAKE,
	CPU_USAGE_KEEP_DISPLAY_AWAKE
} CpuUsageAction;

typedef enum ApplicationAction {
	APPLICATION_KEEP_SYSTEM_AWAKE,
	APPLICATION_KEEP_DISPLAY_AWAKE
} ApplicationAction;

/* Defines the matching conditions and sleep prevention mode for one application. */
typedef struct ApplicationSettings {
	wchar_t *path;
	ApplicationAction action;
	BOOL visible_window_only;
} ApplicationSettings;

/* Holds the CPU monitoring configuration and application monitoring rules. */
typedef struct AwakeSettings {
	DWORD interval_seconds;
	BOOL cpu_usage_enabled;
	DWORD cpu_usage_threshold;
	CpuUsageAction cpu_usage_action;
	ApplicationSettings *applications;
	size_t application_count;
} AwakeSettings;

typedef enum SettingsResult {
	SETTINGS_OK,
	SETTINGS_IO_ERROR,
	SETTINGS_INVALID_ENCODING,
	SETTINGS_INVALID_VALUE,
	SETTINGS_INVALID_INTERVAL,
	SETTINGS_INVALID_CPU_THRESHOLD,
	SETTINGS_INVALID_CPU_ACTION,
	SETTINGS_INVALID_APPLICATION_PATH,
	SETTINGS_INVALID_APPLICATION_ACTION,
	SETTINGS_INVALID_APPLICATION_VISIBLE_WINDOW
} SettingsResult;

/* Loads settings from the default location, creating them if missing.
 * settings: Receives the loaded settings on success; release them with settings_free.
 */
SettingsResult settings_load(AwakeSettings *settings);

/* Reloads the existing settings file without creating it.
 * settings: Receives the loaded settings on success; release them with settings_free.
 */
SettingsResult settings_reload(AwakeSettings *settings);

/* Loads a settings file, creating it with defaults if missing.
 * path: Absolute path to the settings file.
 * settings: Receives the loaded settings on success; release them with settings_free.
 */
SettingsResult settings_load_file(const wchar_t *path, AwakeSettings *settings);

/* Releases the application rules owned by a settings object.
 * settings: Settings whose application rules are to be released.
 */
void settings_free(AwakeSettings *settings);

/* Computes the default settings directory.
 * path: Buffer that receives the directory path.
 * capacity: Buffer size in wide characters, including the terminator.
 */
BOOL settings_directory(wchar_t *path, size_t capacity);

#endif
