# Shared BackupRepository contract

ABI 1 (`ufb_status`: 284 bytes), SQLite core ABI 3. `ufbackup_call` is blocking and must run on a platform worker; it never calls user code. The library links the existing engine and exports no SQLite engine symbols. SQL and state transitions exist only in `repository.cpp`.

A store is `<private root>/<SHA256(normalized server + newline + account)>/catalog.sqlite`. Android uses `getNoBackupFilesDir()/UIFrameBackup`; iOS uses Application Support/UIFrameBackup, excluded from iCloud backup. Native existing-open reads persisted server/account without Unity. C# holds the exclusive preparation attachment; platform attachments independently retain the catalog for background work.

Input is a little-endian uint32 count followed by tagged values from the SQLite wire format. Output uses its table format. All strings are UTF-8. Maximum 1 MiB per command/result, 200 returned rows, 16 admitted commands process-wide, 64 attachments. C# bounds buffer admission before allocating the output page. These are request limits, not total process-memory guarantees.

| Operation | Atomic result / ownership |
| --- | --- |
| Prepare (2), ReservePayload (65), Seal (3), Accept (4) | Preparation ID is known before submission; 1–32 items. Copy reservation is durable before file creation. Seal checks file presence and size. The copier hashes and flushes bytes; Accept atomically publishes the complete batch. |
| Preparation (64) | Returns durable header and original task IDs independently of task-history retention. Unknown acceptance is reconciled with the same ID; callers must not invent a replacement ID. |
| Claim (9), Submitted (10), Start (22) | Claim assigns a new attempt generation. Submitted binds system identity and protected credential reference. Start returns no row for a pause/cancel race, rejects duplicate running starts. |
| Finish (12) | Outcome 1 confirms the receipt, source/version relation and cleanup intent in one transaction; 2 is a known failure before uncertain server commit; 3 is interrupted/unknown; 4 proves no server commit or no network start. Confirmation wins previous cancellation intent. |
| Release (13) | Confirms actual payload/credential release. Cleanup requires all attempts released. A failed completion write must not report backup success. |
| Restart (63) | Only after positively observing the previous executor ended; restarts unfinished execution, never a recorded terminal failure. |
| RecoverAttempt (57) | Positively absent executor; releases ownership while retaining unknown server outcome as NeedsAttention. |
| Action (14), Pause (15) | Per-task intent and global queue pause are independent. Unknown server outcomes cannot be discarded as canceled. |
| Operation / Select / Apply (40–42) | Durable ID + fingerprint + creation upper bound. Selection pages ≤128; application pages ≤32. WaitingForRelease is nonterminal. Repeated IDs do not execute again. |
| Cleanup (18/19/21) | Selection is advisory; execution rechecks ownership. Owns both the payload and its export directory. Failed cleanup is persisted and needs explicit retry; incomplete unfailed intents resume. |
| Scope suspend / reset (69–71), ScopeState (73) | Permission changes pause accepted scope tasks in pages of32. Reset disables scanning until all disposition pages are removed; confirmed receipts and accepted tasks survive. |
| OperationCleanupPage (72) | Uses the eligible-operation index; expired headers cannot starve later eligible details. |
| Prune (50–52/67/68) | Receipts, source dispositions and minimal operation identity survive history deletion. Notification truncation invalidates old cursors. |

Command arguments are defined in the dispatch cases and are exercised by `tests/repository_tests.cpp`; C#, Java and Objective-C++ encode those typed arguments without duplicating SQL. Repository errors carry SQLite error, phase and commit uncertainty. Unknown database commit faults the attachment context until explicit shutdown/reopen. File/HTTP failures do not imply database corruption.

`UFB_FINISH` keeps source-version links separate from backup IDs: distinct content versions can legitimately receive the same idempotent server backup ID. HTTP credentials are never stored in plaintext. Tokens are encrypted by AndroidKeyStore or stored by Keychain; the shared repository stores only the protected reference.

Platform adapters reconcile orphaned running executions as NeedsAttention; they do not automatically restart an executor whose server outcome is unknown. Recorded platform/repository failures require explicit recovery. iOS record callbacks always use attempt_generation, including late callbacks.
