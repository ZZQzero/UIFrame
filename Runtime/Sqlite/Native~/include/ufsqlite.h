#ifndef UFSQLITE_H
#define UFSQLITE_H
#include <stdint.h>
#if defined(_WIN32)
#if defined(UFSQLITE_BUILD)
#define UFSQLITE_API __declspec(dllexport)
#else
#define UFSQLITE_API __declspec(dllimport)
#endif
#else
#define UFSQLITE_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C"
{
#endif
#define UFSQLITE_ABI 3u
    /* All structures use natural 8-byte alignment. Handles are monotonic, never reused.
       Callers must not release a completion while reading its buffer. No C++ exception
       or managed callback crosses this ABI. Payloads use little endian WireFormat.md. */
    enum uf_kind
    {
        UF_OPEN = 1,
        UF_EXECUTE = 2,
        UF_QUERY = 3,
        UF_BATCH = 4,
        UF_CLOSE = 5,
        UF_SNAPSHOT = 6,
        UF_CHECKPOINT = 7,
        UF_STORAGE = 8
    };
    enum uf_error
    {
        UF_OK = 0,
        UF_ARGUMENT = 1,
        UF_HANDLE = 2,
        UF_CAPACITY = 3,
        UF_SQL = 4,
        UF_CANCELED = 5,
        UF_TIMEOUT = 6,
        UF_IO = 7,
        UF_STATE = 8,
        UF_MEMORY = 9,
        UF_CONDITION = 10,
        UF_RESULT_LIMIT = 11,
        UF_FAULTED = 12
    };
    enum uf_phase
    {
        UF_PHASE_ADMISSION = 0, UF_PHASE_QUEUE = 1, UF_PHASE_OPEN = 2,
        UF_PHASE_PREPARE = 3, UF_PHASE_EXECUTE = 4, UF_PHASE_COMMIT = 5,
        UF_PHASE_SNAPSHOT = 6, UF_PHASE_MAINTENANCE = 7, UF_PHASE_CLOSE = 8
    };
    typedef struct uf_completion
    {
        uint32_t size, abi;
        uint64_t operation, database;
        int32_t error, sqlite_code, committed, reserved;
        uint32_t phase;
        const uint8_t *data;
        uint64_t data_size;
        char message[256];
    } uf_completion;
    typedef struct uf_diagnostics
    {
        uint32_t size, abi;
        uint64_t databases, operations, reserved_bytes, completed, sqlite_bytes;
        uint64_t queued, active, parameter_bytes, result_reserved_bytes, held_result_bytes;
        uint64_t queue_wait_ns, max_queue_wait_ns, execution_ns, max_execution_ns;
        uint64_t commit_ns, commits, cache_hits, cache_misses, errors, canceled, timeouts;
    } uf_diagnostics;
    UFSQLITE_API uint32_t ufsqlite_abi(void);
    UFSQLITE_API const char *ufsqlite_source_id(void);
    UFSQLITE_API const char *ufsqlite_build_id(void);
    UFSQLITE_API int ufsqlite_client_create(uint32_t abi, uint64_t *client);
    UFSQLITE_API int ufsqlite_client_release(uint64_t client);
    /* Stop this client exactly once: reject admission, discard reservations,
       cancel and finish accepted work, and close owned databases. Blocking; never
       call on a database worker. Results remain valid until explicitly released.
       Other clients and their databases are unaffected. */
    UFSQLITE_API int ufsqlite_client_stop(uint64_t client);
    /* Open: db=0. Result contains the newly opened handle. Other operations require
       an opened handle owned by client. Admission copies payload before returning.
       CLOSE has reserved admission capacity and cannot be canceled. */
    UFSQLITE_API int ufsqlite_submit(uint64_t client, uint64_t database, uint32_t kind,
                                     const uint8_t *payload, uint64_t length, uint32_t max_rows,
                                     uint32_t max_bytes, uint32_t timeout_ms, uint64_t *operation);
    /* Two-phase admission allows managed code to reserve memory BEFORE encoding.
       Every reservation must be committed or discarded. It is not an accepted SQL
       operation until commit_request. The existing submit combines both steps. */
    UFSQLITE_API int ufsqlite_reserve(uint64_t client, uint64_t database, uint32_t kind, uint64_t length,
                                      uint32_t max_rows, uint32_t max_bytes, uint32_t timeout_ms,
                                      uint64_t *operation);
    UFSQLITE_API int ufsqlite_commit_request(uint64_t client, uint64_t operation, const uint8_t *payload,
                                             uint64_t length);
    UFSQLITE_API int ufsqlite_discard_request(uint64_t client, uint64_t operation);
    UFSQLITE_API int ufsqlite_cancel(uint64_t client, uint64_t operation);
    /* Blocks on a condition variable, drains at most capacity results; timeout=0 polls.
       A result is delivered exactly once, but remains owned until release_result. */
    UFSQLITE_API int ufsqlite_wait(uint64_t client, uf_completion *output, uint32_t capacity,
                                   uint32_t timeout_ms, uint32_t *count);
    UFSQLITE_API int ufsqlite_release_result(uint64_t client, uint64_t operation);
    /* Relinquish a finished result, including one not received by wait. The caller
       must own its delivery/consumption and must not subsequently read its buffer. */
    UFSQLITE_API int ufsqlite_discard_result(uint64_t client, uint64_t operation);
    UFSQLITE_API int ufsqlite_get_diagnostics(uf_diagnostics *output);
#ifdef __cplusplus
}
#endif
#endif
