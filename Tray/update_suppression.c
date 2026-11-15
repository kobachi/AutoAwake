#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdlib.h>
#include <strsafe.h>
#include "update_suppression.h"

#pragma comment(lib, "advapi32.lib")

/* The marker is flushed before every destructive step. PREPARING never
 * modifies the original; CLEANUP means the original has been restored. */
enum { PREPARING = 1, READY_PRESENT, CLEANUP_PRESENT, READY_ABSENT, CLEANUP_ABSENT };
typedef struct UpdateValue { int path; const wchar_t *name; DWORD value; } UpdateValue;
static const UpdateValue values[] = {
	{ 0, L"NoAutoRebootWithLoggedOnUsers", 1 },
	{ 1, L"IsContinuousInnovationOptedIn", 0 },
	{ 1, L"IsExpedited", 0 }
};

static BOOL names(const UpdateValue *v, wchar_t *backup, wchar_t *marker)
{
	return SUCCEEDED(StringCchPrintfW(backup, 128, L"%s.AutoAwakeBackup", v->name)) &&
		SUCCEEDED(StringCchPrintfW(marker, 128, L"%s.AutoAwakeState", v->name));
}

static LONG read_value(HKEY key, const wchar_t *name, DWORD *type, BYTE **data, DWORD *size)
{
	LONG result;
	DWORD capacity;
	*data = NULL;
	result = RegQueryValueExW(key, name, NULL, type, NULL, size);
	if (result != ERROR_SUCCESS) return result;
	capacity = *size;
	*data = malloc(capacity ? capacity : 1);
	if (!*data) return ERROR_OUTOFMEMORY;
	result = RegQueryValueExW(key, name, NULL, type, *data, size);
	if (result != ERROR_SUCCESS) { free(*data); *data = NULL; }
	return result;
}

static BOOL write_marker(HKEY key, const wchar_t *name, DWORD state)
{
	return RegSetValueExW(key, name, 0, REG_DWORD, (const BYTE *)&state, sizeof(state)) == ERROR_SUCCESS &&
		RegFlushKey(key) == ERROR_SUCCESS;
}

static BOOL delete_value(HKEY key, const wchar_t *name)
{
	LONG result = RegDeleteValueW(key, name);
	return (result == ERROR_SUCCESS || result == ERROR_FILE_NOT_FOUND) && RegFlushKey(key) == ERROR_SUCCESS;
}

static BOOL restore_one(UpdateSuppression *s, const UpdateValue *v)
{
	HKEY key;
	wchar_t backup[128], marker[128];
	DWORD state, type, size = sizeof(state);
	BYTE *data = NULL;
	LONG result;
	BOOL ok = FALSE;
	if (!names(v, backup, marker)) return FALSE;
	result = RegOpenKeyExW(s->root, s->paths[v->path], 0, KEY_QUERY_VALUE | KEY_WOW64_64KEY, &key);
	if (result == ERROR_FILE_NOT_FOUND) return TRUE;
	if (result != ERROR_SUCCESS) return FALSE;
	result = RegQueryValueExW(key, marker, NULL, &type, (BYTE *)&state, &size);
	if (result == ERROR_FILE_NOT_FOUND) {
		result = RegQueryValueExW(key, backup, NULL, NULL, NULL, NULL);
		RegCloseKey(key);
		if (result == ERROR_FILE_NOT_FOUND) return TRUE;
		if (result != ERROR_SUCCESS) return FALSE;
		state = 0;
	} else {
		RegCloseKey(key);
		if (result != ERROR_SUCCESS || type != REG_DWORD || size != sizeof(state) || state < PREPARING || state > CLEANUP_ABSENT) return FALSE;
	}
	if (RegOpenKeyExW(s->root, s->paths[v->path], 0, KEY_QUERY_VALUE | KEY_SET_VALUE | KEY_WOW64_64KEY, &key) != ERROR_SUCCESS) return FALSE;
	if (state == READY_PRESENT || state == 0) {
		result = read_value(key, backup, &type, &data, &size);
		if (result != ERROR_SUCCESS) goto done;
		result = RegSetValueExW(key, v->name, 0, type, data, size);
		free(data);
		if (result != ERROR_SUCCESS || RegFlushKey(key) != ERROR_SUCCESS) goto done;
		if (state == 0) {
			/* With no marker, retain the backup until restoration is durable. */
			ok = delete_value(key, backup);
			goto done;
		}
		if (!write_marker(key, marker, CLEANUP_PRESENT)) goto done;
		state = CLEANUP_PRESENT;
	} else if (state == READY_ABSENT) {
		if (!delete_value(key, v->name) || !write_marker(key, marker, CLEANUP_ABSENT)) goto done;
		state = CLEANUP_ABSENT;
	}
	if ((state == PREPARING || state == CLEANUP_PRESENT) && !delete_value(key, backup)) goto done;
	if (!delete_value(key, marker)) goto done;
	ok = TRUE;
done:
	RegCloseKey(key);
	return ok;
}

static BOOL restore_all(UpdateSuppression *s)
{
	size_t i;
	BOOL ok = TRUE;
	for (i = 0; i < ARRAYSIZE(values); ++i) if (!restore_one(s, &values[i])) ok = FALSE;
	return ok;
}

static BOOL apply_one(UpdateSuppression *s, const UpdateValue *v)
{
	HKEY key;
	wchar_t backup[128], marker[128];
	DWORD type, size, state;
	BYTE *data = NULL;
	LONG result;
	BOOL ok = FALSE;
	if (!names(v, backup, marker)) return FALSE;
	if (RegCreateKeyExW(s->root, s->paths[v->path], 0, NULL, 0,
		KEY_QUERY_VALUE | KEY_SET_VALUE | KEY_WOW64_64KEY, NULL, &key, NULL) != ERROR_SUCCESS) return FALSE;
	result = RegQueryValueExW(key, marker, NULL, NULL, NULL, NULL);
	if (result != ERROR_FILE_NOT_FOUND) goto done;
	result = RegQueryValueExW(key, backup, NULL, NULL, NULL, NULL);
	if (result != ERROR_FILE_NOT_FOUND) goto done;
	result = read_value(key, v->name, &type, &data, &size);
	if (result == ERROR_SUCCESS) {
		if (!write_marker(key, marker, PREPARING)) goto done;
		if (RegSetValueExW(key, backup, 0, type, data, size) != ERROR_SUCCESS || RegFlushKey(key) != ERROR_SUCCESS) goto done;
		state = READY_PRESENT;
	} else if (result == ERROR_FILE_NOT_FOUND) {
		state = READY_ABSENT;
	} else goto done;
	if (!write_marker(key, marker, state)) goto done;
	if (RegSetValueExW(key, v->name, 0, REG_DWORD, (const BYTE *)&v->value, sizeof(v->value)) != ERROR_SUCCESS ||
		RegFlushKey(key) != ERROR_SUCCESS) goto done;
	ok = TRUE;
done:
	free(data);
	RegCloseKey(key);
	return ok;
}

BOOL update_init(UpdateSuppression *s, HKEY root, const wchar_t *policy_path,
	const wchar_t *settings_path, const wchar_t *mutex_name)
{
	ZeroMemory(s, sizeof(*s));
	s->root = root;
	s->paths[0] = policy_path;
	s->paths[1] = settings_path;
	s->mutex = CreateMutexW(NULL, FALSE, mutex_name);
	return s->mutex != NULL;
}

static DWORD acquire(UpdateSuppression *s)
{
	DWORD result = WaitForSingleObject(s->mutex, 0);
	if (result == WAIT_OBJECT_0 || result == WAIT_ABANDONED) s->owned = TRUE;
	return result;
}

static BOOL release(UpdateSuppression *s)
{
	BOOL ok = ReleaseMutex(s->mutex);
	if (ok) s->owned = FALSE;
	return ok;
}

BOOL update_recover(UpdateSuppression *s)
{
	DWORD result = s->owned ? WAIT_OBJECT_0 : acquire(s);
	BOOL ok;
	/* A live owner is responsible for recovery; do not disturb its values. */
	if (result == WAIT_TIMEOUT) return TRUE;
	if (!s->owned) return FALSE;
	ok = restore_all(s);
	if (ok && !release(s)) ok = FALSE;
	return ok;
}

BOOL update_set(UpdateSuppression *s, BOOL enabled)
{
	size_t i;
	DWORD result;
	if (!s->mutex) return !enabled;
	if (!enabled) {
		if (!s->owned) {
			result = acquire(s);
			if (result == WAIT_TIMEOUT) return TRUE;
			if (!s->owned) return FALSE;
		}
		s->applied = FALSE;
		if (!restore_all(s)) return FALSE;
		return release(s);
	}
	if (s->applied) return TRUE;
	s->rollback_failed = FALSE;
	if (!s->owned && acquire(s) != WAIT_OBJECT_0 && !s->owned) return FALSE;
	if (!restore_all(s)) { s->rollback_failed = TRUE; return FALSE; }
	for (i = 0; i < ARRAYSIZE(values); ++i) {
		if (!apply_one(s, &values[i])) {
			if (!restore_all(s)) {
				s->rollback_failed = TRUE;
				/* Keep the mutex for the shutdown retry. */
			} else if (!release(s)) {
				s->rollback_failed = TRUE;
			}
			return FALSE;
		}
	}
	s->applied = TRUE;
	return TRUE;
}

BOOL update_close(UpdateSuppression *s)
{
	BOOL ok = update_set(s, FALSE);
	if (s->owned && !release(s)) ok = FALSE;
	if (s->mutex && !CloseHandle(s->mutex)) ok = FALSE;
	s->mutex = NULL;
	return ok;
}
