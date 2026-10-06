#ifndef UFBACKUP_H
#define UFBACKUP_H
#include <stdint.h>
#ifdef _WIN32
#ifdef UFBACKUP_BUILD
#define UFB_API __declspec(dllexport)
#else
#define UFB_API __declspec(dllimport)
#endif
#else
#define UFB_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif
#define UFB_ABI 4u
// The file effect failed and its failure was durably isolated. Other commands
// may continue; only an explicit cleanup retry may select this file again.
#define UFB_CLEANUP_FILE_FAILED 1001
enum ufb_command {
    UFB_INFO=1, UFB_PREPARE=2, UFB_SEAL=3,
    UFB_TASKS=6, UFB_TASK=7, UFB_SUMMARY=8, UFB_CLAIM=9, UFB_ACTION=14, UFB_PAUSE=15,
    UFB_RECEIPTS=16, UFB_RECEIPT=17, UFB_CLEANUP_PAGE=18, UFB_CLEANUP_RUN=19,
    UFB_CLEANUP_RETRY=21, UFB_ATTEMPTS=23,
    UFB_CHANGES=24, UFB_POLICY=25, UFB_SET_POLICY=26,
    UFB_SCOPE=30, UFB_BEGIN_SCAN=31, UFB_SCAN_PAGE=32, UFB_ACTIVATE_SCAN=33,
    UFB_DISCOVER=34, UFB_DISCOVERIES=35, UFB_DISPOSITION=36,
    UFB_OPERATION=40, UFB_SELECT_OPERATION=41, UFB_APPLY_OPERATION=42,
    UFB_OPERATION_STATUS=43, UFB_OPERATION_ITEMS=44,
    UFB_HISTORY_PAGE=50, UFB_PRUNE_TASK=51, UFB_PRUNE_OPERATION=52,
    UFB_STORAGE=53, UFB_CHECKPOINT=54, UFB_RECOVER_PREPARATIONS=55,
    UFB_FILE_SUMMARY=56, UFB_RECOVER_ATTEMPT=57, UFB_BIND_PREPARER=58,
    UFB_ATTEMPT=60, UFB_READY=62, UFB_OPERATIONS=66,
    UFB_PRUNE_CHANGES=67, UFB_PRUNE_SCANS=68, UFB_SUSPEND_SCOPE=69,
    UFB_RESET_SCOPE=70, UFB_RESET_SCOPE_PAGE=71, UFB_OPERATION_CLEANUP_PAGE=72, UFB_SCOPE_STATE=73, UFB_HANDOFF=74,
    UFB_CLEANUP_FAILURES=76, UFB_TRY_PREPARE=77, UFB_PREPARATION_ELIGIBILITY=78, UFB_STOP_PREPARATION=79, UFB_CONTROL_CREATE=80, UFB_CONTROL_SEAL=81, UFB_CONTROL_SUBMITTED=82,
    UFB_CONTROL_START=83, UFB_CONTROL_VALIDATE=84, UFB_CONTROL_APPLY=85,
    UFB_CONTROL_FAIL=86, UFB_CONTROL_RELEASE=87, UFB_CONTROL_RECOVER=88,
    UFB_CONTROLS=89, UFB_UPLOADS=90, UFB_UPLOAD_START=91,
    UFB_UPLOAD_END=92, UFB_UPLOAD_RELEASE=93, UFB_PROTOCOL_ACTIONS=94,
    UFB_SYSTEM_SCHEDULED=95, UFB_ACCEPT_ITEM=96, UFB_FAIL_ITEM=97,
    UFB_SUBMISSION=98, UFB_PROTOCOL_CONFIGURE=99, UFB_PROTOCOL_CLEANUP=100,
    UFB_PROTOCOL_RECONCILE=101, UFB_PROTOCOL_RELEASE=102,
    UFB_PROTOCOL_RESUME=103, UFB_CONTROL=104, UFB_PROTOCOL_WAKE=105,
    UFB_UPLOAD_REJECT=106, UFB_CONTROL_CLEANUP_FAILURES=107, UFB_CONTROL_CLEANUP_RETRY=108, UFB_CONFIRM_SCOPE=109
};
typedef struct ufb_status {
    uint32_t size, abi;
    int32_t error, sqlite_code, committed;
    uint32_t phase, length;
    char message[256];
} ufb_status;
UFB_API uint32_t ufbackup_abi(void);
// root/store_id/catalog.sqlite; create=1 creates a new catalog, create=0 only
// opens an existing one. Empty server/account are permitted ONLY on native
// existing-open; identity is then read from the store, never invented.
UFB_API int ufbackup_open(const char *root, const char *store_id, const char *server,
    const char *account, int create, uint64_t *handle, ufb_status *status);
UFB_API int ufbackup_close(uint64_t handle, ufb_status *status);
// Blocking, bounded command. Input is uint32 count + SQLite wire tagged values.
// Output is SQLite wire tables, including command results followed by final rows.
// Each handle has independent attachment ownership; native background attachments
// keep the shared repository alive after the C# facade detaches.
UFB_API int ufbackup_call(uint64_t handle, uint32_t command, const uint8_t *input,
    uint32_t length, uint8_t *output, uint32_t capacity, ufb_status *status);
#ifdef __cplusplus
}
#endif
#endif
