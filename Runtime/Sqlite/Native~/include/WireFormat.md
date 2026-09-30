# UIFrame SQLite ABI 3

`ufsqlite.h` is the public C boundary. ABI 3 currently targets little-endian 64-bit Android, iOS, macOS and Windows. Structure sizes use natural 8-byte alignment: `uf_completion` 320 bytes, `uf_diagnostics` 176 bytes. Validate `ufsqlite_abi()` before creating a client. There is no previous-format parser or migration path.

All payload integers are little endian. A string is uint32 byte length followed by UTF-8 bytes, without a terminator. Handles are monotonically allocated uint64 values; never cast pointers across this boundary. Function return values describe admission / API errors. Accepted operations end in exactly one `uf_completion`, which must be released with `ufsqlite_release_result`, including failures and empty results.

## Requests

| Kind | Payload |
| --- | --- |
| OPEN (1) | uint32 mode (0 create, 1 existing read/write, 2 existing read-only), string absolute path, uint64 min free disk bytes, uint64 max WAL bytes (>0) |
| EXECUTE (2), QUERY (3), BATCH (4) | uint32 command count, followed by commands below |
| CLOSE (5) | empty |
| SNAPSHOT (6) | string new destination, uint64 temporary-file budget, uint64 source WAL budget |
| CHECKPOINT (7) | uint32 mode (0 passive, 1 truncate) |
| STORAGE (8) | empty |

Each SQL command contains string SQL, int64 expected affected rows (-1 means no condition), uint32 parameter count, then tagged values. EXECUTE / QUERY have exactly one command; BATCH has 1–200. Bind every parameter. A value is byte tag followed by: 0=null (no data), 1=int64, 2=IEEE754 double bits, 3=string, 4=uint32 size + blob bytes. Non-finite managed floating values are rejected before admission.

Reserve budget with `ufsqlite_reserve` before creating managed encoding buffers. Every reservation must be committed with matching length or discarded. Reservation does not mean SQL was accepted; `commit_request` does. `submit` is the native convenience that performs both steps. Cancellation identifies a client and operation, never a connection. CLOSE ignores cancellation and has one reserved admission slot per database. It requires zero input length, row limit and byte limit; this prevents the release path from bypassing normal buffer quotas. Discarding its reservation returns that slot. Accepted writes follow commit_request order, not reservation order. An uncommitted reservation cannot block CLOSE; after CLOSE is accepted, such a reservation must be discarded and cannot be committed.

## Results

OPEN returns the new context in completion.database. CLOSE / SNAPSHOT return no data. SQL results contain uint32 table count followed by each table: uint32 columns, uint32 rows, int64 affected rows, one string name per column, then rows in row-major order with the same tagged-value encoding as parameters. Limits apply to the whole result, including metadata; result overflow rolls back an uncommitted write batch.

CHECKPOINT / STORAGE use the same table encoding with one row and six int64 columns: database_bytes, wal_bytes, available_bytes, log_pages, checkpointed_pages, blocked. STORAGE has zero checkpoint page counts. SQLite may return -1 page counts when no WAL is present. Passive work can be incomplete without a SQL error; blocked reports incomplete or busy checkpoint work. No maintenance attempt is replayed.

Completion.error is `uf_error`, sqlite_code is the extended SQLite error if available, committed is 0 (no confirmed commit), 1 (confirmed commit/publication), or -1 (commit outcome unknown). `reserved` carries a secondary cleanup SQLite error code, not application data. Phase identifies admission, queue, open, prepare, execute, commit, snapshot, maintenance or close. Secondary rollback/cleanup failures do not replace the primary phase. Message is a terminated UTF-8 diagnostic up to255 bytes. COMMIT success wins cancellation received afterwards. Raw output remains valid until its one release call, independently of database close.

`ufsqlite_wait` blocks on a condition variable and drains a bounded completion batch. It never invokes client callbacks. Each client owns its databases and completions; an independent native service uses its own client and remains alive when the C# client closes. A native service must close all its contexts and release all results before client_release. Native callers must provide readable / writable buffers of the declared length and must not race result release with buffer access.

## Fault shutdown and diagnostics

`ufsqlite_client_stop` is a one-shot, blocking lifetime operation independent of result delivery. It rejects new requests for that client, discards uncommitted reservations, cancels and finishes accepted work, then closes its databases. It does not replay operations or change previously committed outcomes. Results already delivered remain valid. `ufsqlite_discard_result` relinquishes a finished result whose delivery is no longer required, whether or not it was claimed. Stop does not release result buffers or unregister the client: release/discard each owned result, then call `ufsqlite_client_release`. Other clients keep running. Native I/O can remain uninterruptible; stop waits for real termination.

Diagnostics are global and read on demand. Queue/active counts are instantaneous; parameter bytes include the two admission copies, reserved result bytes count declared quotas, held result bytes count encoded finished results. Durations are monotonic nanoseconds: queue wait counts scheduling waits (each snapshot step separately), execution excludes queue wait and sums actual worker occupancy, maximum execution is the cumulative service time of one finished request. Commit duration includes attempted SQL commits; committed_transactions only counts confirmed SQL commits. Cache/error/cancellation/timeout totals are accrued on operation completion. No SQL parameters, credentials or file names are collected. WAL bytes and checkpoint progress are available through STORAGE/CHECKPOINT. These aggregates are not latency percentiles or a bound on total process memory.
