#ifndef AUTOAWAKE_UPDATE_SUPPRESSION_H
#define AUTOAWAKE_UPDATE_SUPPRESSION_H

#include <windows.h>

/* The root and paths are injectable so tests never access machine policy. */
typedef struct UpdateSuppression {
	HKEY root;
	const wchar_t *paths[2];
	HANDLE mutex;
	BOOL owned;
	BOOL applied;
	BOOL rollback_failed;
} UpdateSuppression;

BOOL update_init(UpdateSuppression *state, HKEY root, const wchar_t *policy_path,
	const wchar_t *settings_path, const wchar_t *mutex_name);
BOOL update_recover(UpdateSuppression *state);
BOOL update_set(UpdateSuppression *state, BOOL enabled);
BOOL update_close(UpdateSuppression *state);

#endif
