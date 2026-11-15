#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdio.h>
#include <wchar.h>
#include <string.h>
#include "..\update_suppression.h"

static const wchar_t *fail_name;
static const wchar_t *second_fail_name;
static const wchar_t *fail_delete_name;
static LONG WINAPI test_set_value(HKEY key, LPCWSTR name, DWORD reserved, DWORD type, const BYTE *data, DWORD size)
{
	if (fail_name && wcscmp(name, fail_name) == 0) {
		fail_name = second_fail_name;
		second_fail_name = NULL;
		return ERROR_ACCESS_DENIED;
	}
	return RegSetValueExW(key, name, reserved, type, data, size);
}
static LONG WINAPI test_delete_value(HKEY key, LPCWSTR name)
{
	if (fail_delete_name && wcscmp(name, fail_delete_name) == 0) {
		fail_delete_name = NULL;
		return ERROR_ACCESS_DENIED;
	}
	return RegDeleteValueW(key, name);
}
#define RegDeleteValueW test_delete_value
#define RegSetValueExW test_set_value
#include "..\update_suppression.c"
#undef RegSetValueExW
#undef RegDeleteValueW

#define CHECK(x) do { if (!(x)) { fprintf(stderr, "FAIL line %d: %s\n", __LINE__, #x); return 1; } } while (0)
static wchar_t base[128], policy[160], ux[160], lock_name[128];
static const wchar_t *reboot = L"NoAutoRebootWithLoggedOnUsers";
static const wchar_t *innovation = L"IsContinuousInnovationOptedIn";
static const wchar_t *expedited = L"IsExpedited";

static BOOL query(HKEY key, const wchar_t *name, DWORD expected_type, const void *expected, DWORD expected_size)
{
	BYTE data[256]; DWORD type = 0, size = sizeof(data);
	return RegQueryValueExW(key, name, NULL, &type, data, &size) == ERROR_SUCCESS &&
		type == expected_type && size == expected_size && memcmp(data, expected, size) == 0;
}
static BOOL missing(HKEY key, const wchar_t *name)
{
	return RegQueryValueExW(key, name, NULL, NULL, NULL, NULL) == ERROR_FILE_NOT_FOUND;
}
static DWORD WINAPI hold_lock(void *parameter)
{
	HANDLE *events = parameter;
	HANDLE mutex = CreateMutexW(NULL, FALSE, lock_name);
	if (!mutex || WaitForSingleObject(mutex, INFINITE) != WAIT_OBJECT_0) return 1;
	SetEvent(events[0]);
	WaitForSingleObject(events[1], INFINITE);
	ReleaseMutex(mutex); CloseHandle(mutex);
	return 0;
}

int main(void)
{
	UpdateSuppression s;
	HKEY p, u, base_key;
	DWORD disposition;
	DWORD zero = 0, one = 1, original = 77, marker = READY_PRESENT;
	const wchar_t original_text[] = L"original";
	HANDLE events[2], thread;
	DWORD exit_code;
	CHECK(swprintf_s(base, ARRAYSIZE(base), L"Software\\AutoAwakeTests-%lu-%llu", GetCurrentProcessId(), (unsigned long long)GetTickCount64()) > 0);
	CHECK(swprintf_s(policy, ARRAYSIZE(policy), L"%s\\UpdatePolicy", base) > 0);
	CHECK(swprintf_s(ux, ARRAYSIZE(ux), L"%s\\UpdateUX", base) > 0);
	CHECK(swprintf_s(lock_name, ARRAYSIZE(lock_name), L"Local\\AutoAwake.UpdateSuppression.Test-%lu", GetCurrentProcessId()) > 0);
	CHECK(RegCreateKeyExW(HKEY_CURRENT_USER, base, 0, NULL, 0, KEY_ALL_ACCESS, NULL, &base_key, &disposition) == ERROR_SUCCESS);
	CHECK(disposition == REG_CREATED_NEW_KEY);
	RegCloseKey(base_key);
	CHECK(RegCreateKeyExW(HKEY_CURRENT_USER, policy, 0, NULL, 0, KEY_ALL_ACCESS, NULL, &p, NULL) == ERROR_SUCCESS);
	CHECK(RegCreateKeyExW(HKEY_CURRENT_USER, ux, 0, NULL, 0, KEY_ALL_ACCESS, NULL, &u, NULL) == ERROR_SUCCESS);
	CHECK(RegSetValueExW(p, reboot, 0, REG_SZ, (const BYTE *)original_text, sizeof(original_text)) == ERROR_SUCCESS);
	CHECK(RegSetValueExW(u, expedited, 0, REG_BINARY, (const BYTE *)&original, sizeof(original)) == ERROR_SUCCESS);
	CHECK(update_init(&s, HKEY_CURRENT_USER, policy, ux, lock_name));
	CHECK(update_recover(&s));
	CHECK(update_set(&s, TRUE));
	CHECK(query(p, reboot, REG_DWORD, &one, sizeof(one)));
	CHECK(query(u, innovation, REG_DWORD, &zero, sizeof(zero)));
	CHECK(query(u, expedited, REG_DWORD, &zero, sizeof(zero)));
	CHECK(query(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup", REG_SZ, original_text, sizeof(original_text)));
	CHECK(missing(u, L"IsContinuousInnovationOptedIn.AutoAwakeBackup"));
	CHECK(query(u, L"IsExpedited.AutoAwakeBackup", REG_BINARY, &original, sizeof(original)));
	CHECK(update_set(&s, TRUE) && update_set(&s, FALSE));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)) && missing(u, innovation));
	CHECK(query(u, expedited, REG_BINARY, &original, sizeof(original)));
	CHECK(missing(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeState"));
	/* Simulate process death after applying: keep the registry and release the lock. */
	CHECK(update_set(&s, TRUE));
	CHECK(ReleaseMutex(s.mutex)); s.owned = FALSE; s.applied = FALSE;
	CHECK(update_recover(&s));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)) && missing(u, innovation));
	/* Interrupted preparation has not changed the original, even if a partial backup exists. */
	marker = PREPARING;
	CHECK(RegSetValueExW(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeState", 0, REG_DWORD, (BYTE *)&marker, sizeof(marker)) == ERROR_SUCCESS);
	CHECK(RegSetValueExW(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup", 0, REG_DWORD, (BYTE *)&zero, sizeof(zero)) == ERROR_SUCCESS);
	CHECK(update_recover(&s));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)));
	CHECK(missing(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup"));
	/* A failed restoration remains recoverable on the next startup. */
	CHECK(update_set(&s, TRUE));
	fail_name = reboot;
	CHECK(!update_set(&s, FALSE));
	CHECK(query(p, reboot, REG_DWORD, &one, sizeof(one)));
	CHECK(update_recover(&s));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)) && missing(u, innovation));
	/* A failed write must roll back prior values and leave the original intact. */
	fail_name = innovation;
	CHECK(!update_set(&s, TRUE));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)) && missing(u, innovation));
	CHECK(query(u, expedited, REG_BINARY, &original, sizeof(original)));
	/* Startup recovery restores an orphan over an existing target, preserving type and bytes. */
	CHECK(RegSetValueExW(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup", 0, REG_BINARY, (BYTE *)&original, sizeof(original)) == ERROR_SUCCESS);
	CHECK(update_recover(&s));
	CHECK(query(p, reboot, REG_BINARY, &original, sizeof(original)));
	CHECK(missing(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup"));
	/* Disabling suppression restores an orphan even when the target is missing. */
	CHECK(RegSetValueExW(u, L"IsContinuousInnovationOptedIn.AutoAwakeBackup", 0, REG_SZ, (const BYTE *)original_text, sizeof(original_text)) == ERROR_SUCCESS);
	CHECK(missing(u, innovation));
	CHECK(update_set(&s, FALSE));
	CHECK(query(u, innovation, REG_SZ, original_text, sizeof(original_text)));
	CHECK(missing(u, L"IsContinuousInnovationOptedIn.AutoAwakeBackup"));
	CHECK(RegDeleteValueW(u, innovation) == ERROR_SUCCESS);
	/* A failed orphan restore must leave its backup intact for another attempt. */
	CHECK(RegSetValueExW(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup", 0, REG_SZ, (const BYTE *)original_text, sizeof(original_text)) == ERROR_SUCCESS);
	fail_name = reboot;
	CHECK(!update_recover(&s) && s.owned);
	CHECK(query(p, reboot, REG_BINARY, &original, sizeof(original)));
	CHECK(query(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup", REG_SZ, original_text, sizeof(original_text)));
	CHECK(update_set(&s, FALSE) && !s.owned);
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)));
	CHECK(missing(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup"));
	/* Failed orphan cleanup is reported and leaves the backup for retry. */
	CHECK(RegSetValueExW(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup", 0, REG_SZ, (const BYTE *)original_text, sizeof(original_text)) == ERROR_SUCCESS);
	fail_delete_name = L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup";
	CHECK(!update_set(&s, FALSE) && s.owned);
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)));
	CHECK(query(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup", REG_SZ, original_text, sizeof(original_text)));
	CHECK(update_set(&s, FALSE) && !s.owned);
	CHECK(missing(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeBackup"));
	/* A failed application and failed rollback are distinguished and retried. */
	fail_name = innovation; second_fail_name = reboot;
	CHECK(!update_set(&s, TRUE) && s.rollback_failed && s.owned);
	CHECK(query(p, reboot, REG_DWORD, &one, sizeof(one)));
	CHECK(update_close(&s) && !s.owned);
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)) && missing(u, innovation));
	CHECK(update_init(&s, HKEY_CURRENT_USER, policy, ux, lock_name));
	/* Missing backup in a ready record must never cause the target to be deleted. */
	marker = READY_PRESENT;
	CHECK(RegSetValueExW(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeState", 0, REG_DWORD, (BYTE *)&marker, sizeof(marker)) == ERROR_SUCCESS);
	CHECK(!update_recover(&s));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)));
	CHECK(RegDeleteValueW(p, L"NoAutoRebootWithLoggedOnUsers.AutoAwakeState") == ERROR_SUCCESS);
	/* A different thread owning the machine-wide lock must not be disturbed. */
	CHECK(update_set(&s, TRUE));
	CHECK(ReleaseMutex(s.mutex)); s.owned = FALSE; s.applied = FALSE;
	events[0] = CreateEventW(NULL, TRUE, FALSE, NULL); events[1] = CreateEventW(NULL, TRUE, FALSE, NULL);
	CHECK(events[0] && events[1]);
	thread = CreateThread(NULL, 0, hold_lock, events, 0, NULL); CHECK(thread);
	CHECK(WaitForSingleObject(events[0], 10000) == WAIT_OBJECT_0);
	CHECK(update_recover(&s)); CHECK(!update_set(&s, TRUE));
	CHECK(query(p, reboot, REG_DWORD, &one, sizeof(one)));
	SetEvent(events[1]); CHECK(WaitForSingleObject(thread, 10000) == WAIT_OBJECT_0);
	CHECK(GetExitCodeThread(thread, &exit_code) && exit_code == 0);
	CloseHandle(thread); CloseHandle(events[0]); CloseHandle(events[1]);
	CHECK(update_recover(&s));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)) && missing(u, innovation));
	CHECK(update_set(&s, TRUE)); CHECK(update_close(&s));
	CHECK(query(p, reboot, REG_SZ, original_text, sizeof(original_text)) && missing(u, innovation));
	RegCloseKey(p); RegCloseKey(u);
	CHECK(RegDeleteTreeW(HKEY_CURRENT_USER, base) == ERROR_SUCCESS);
	puts("update suppression tests passed");
	return 0;
}
