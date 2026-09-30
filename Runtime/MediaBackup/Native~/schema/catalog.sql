PRAGMA application_id=1430667843;
PRAGMA user_version=1;
CREATE TABLE store_settings (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    store_id TEXT NOT NULL UNIQUE,
    server TEXT NOT NULL,
    account TEXT NOT NULL,
    paused INTEGER NOT NULL CHECK(paused IN(0,1)),
    retained_after_seq INTEGER NOT NULL DEFAULT 0 CHECK(retained_after_seq>=0)
) STRICT;
CREATE TABLE scopes (
    id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL,
    library_id TEXT NOT NULL,
    index_generation INTEGER NOT NULL CHECK(index_generation>=0),
    scope_revision INTEGER NOT NULL CHECK(scope_revision>=0),
    permission_generation INTEGER NOT NULL CHECK(permission_generation>=0),
    consumed_seq INTEGER NOT NULL DEFAULT 0 CHECK(consumed_seq>=0),
    include_existing INTEGER NOT NULL CHECK(include_existing IN(0,1)),
    enabled INTEGER NOT NULL DEFAULT 0 CHECK(enabled IN(0,1)),
    active_baseline_id TEXT
) STRICT;
CREATE TABLE scan_runs (
    id TEXT PRIMARY KEY,
    scope_id TEXT NOT NULL REFERENCES scopes(id),
    permission_generation INTEGER NOT NULL,
    scope_revision INTEGER NOT NULL,
    log_start INTEGER NOT NULL,
    selection_cursor TEXT,
    completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN(0,1))
) STRICT;
CREATE TABLE scan_items (
    run_id TEXT NOT NULL REFERENCES scan_runs(id),
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    PRIMARY KEY(run_id,source_id,content_version)
) WITHOUT ROWID, STRICT;
CREATE TABLE discoveries (
    scope_id TEXT NOT NULL REFERENCES scopes(id),
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    disposition INTEGER NOT NULL CHECK(disposition BETWEEN 0 AND 5),
    task_id TEXT,
    error TEXT,
    PRIMARY KEY(scope_id,source_id,content_version)
) WITHOUT ROWID, STRICT;
CREATE INDEX discoveries_pending ON discoveries(scope_id,disposition,source_id,content_version);
CREATE TABLE backup_receipts (
    backup_id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    sha256 BLOB NOT NULL CHECK(length(sha256)=32),
    byte_count INTEGER NOT NULL CHECK(byte_count>=0),
    name TEXT NOT NULL,
    mime TEXT NOT NULL,
    confirmed_utc INTEGER NOT NULL,
    UNIQUE(source_id,content_version)
) STRICT;
CREATE INDEX receipts_page ON backup_receipts(confirmed_utc,backup_id);
CREATE TABLE file_records (
    id TEXT PRIMARY KEY,
    relative_path TEXT NOT NULL UNIQUE,
    byte_count INTEGER NOT NULL CHECK(byte_count>=0),
    sha256 BLOB CHECK(sha256 IS NULL OR length(sha256)=32),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 4),
    preparation_id TEXT NOT NULL,
    cleanup_error TEXT,
    updated_utc INTEGER NOT NULL
) STRICT;
CREATE INDEX files_cleanup ON file_records(state,updated_utc,id);
CREATE TABLE tasks (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    id TEXT NOT NULL UNIQUE,
    batch_id TEXT NOT NULL,
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 9),
    desired_action INTEGER NOT NULL DEFAULT 0 CHECK(desired_action BETWEEN 0 AND 3),
    file_id TEXT REFERENCES file_records(id),
    backup_id TEXT REFERENCES backup_receipts(backup_id),
    current_generation INTEGER NOT NULL DEFAULT 0 CHECK(current_generation>=0),
    next_attempt_utc INTEGER NOT NULL DEFAULT 0,
    created_utc INTEGER NOT NULL,
    updated_utc INTEGER NOT NULL,
    error TEXT
) STRICT;
CREATE INDEX tasks_schedule ON tasks(state,next_attempt_utc,sequence);
CREATE INDEX tasks_state_page ON tasks(state,sequence);
CREATE INDEX tasks_source ON tasks(source_id,content_version);
CREATE INDEX tasks_history ON tasks(state,updated_utc,sequence);
CREATE TABLE task_attempts (
    task_id TEXT NOT NULL REFERENCES tasks(id),
    generation INTEGER NOT NULL CHECK(generation>0),
    system_task_id TEXT,
    session_id TEXT,
    idempotency_key TEXT NOT NULL UNIQUE,
    submission_state INTEGER NOT NULL CHECK(submission_state BETWEEN 0 AND 2),
    execution_state INTEGER NOT NULL CHECK(execution_state BETWEEN 0 AND 4),
    server_outcome INTEGER NOT NULL CHECK(server_outcome BETWEEN 0 AND 2),
    payload_released INTEGER NOT NULL CHECK(payload_released IN(0,1)),
    credential_released INTEGER NOT NULL CHECK(credential_released IN(0,1)),
    error TEXT,
    PRIMARY KEY(task_id,generation)
) WITHOUT ROWID, STRICT;
CREATE UNIQUE INDEX attempts_unreleased ON task_attempts(task_id) WHERE payload_released=0 OR credential_released=0;
CREATE TABLE operations (
    id TEXT PRIMARY KEY,
    parameter_fingerprint BLOB NOT NULL CHECK(length(parameter_fingerprint)=32),
    action INTEGER NOT NULL CHECK(action BETWEEN 0 AND 4),
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 3),
    criteria BLOB,
    upper_sequence INTEGER NOT NULL CHECK(upper_sequence>=0),
    selection_cursor INTEGER NOT NULL DEFAULT 0 CHECK(selection_cursor>=0),
    selected_count INTEGER NOT NULL DEFAULT 0 CHECK(selected_count>=0),
    applied_count INTEGER NOT NULL DEFAULT 0 CHECK(applied_count>=0),
    failed_count INTEGER NOT NULL DEFAULT 0 CHECK(failed_count>=0),
    details_expired INTEGER NOT NULL DEFAULT 0 CHECK(details_expired IN(0,1)),
    created_utc INTEGER NOT NULL,
    updated_utc INTEGER NOT NULL,
    error TEXT
) STRICT;
CREATE INDEX operations_work ON operations(phase,created_utc,id);
CREATE TABLE operation_items (
    operation_id TEXT NOT NULL REFERENCES operations(id),
    task_id TEXT NOT NULL,
    outcome INTEGER NOT NULL CHECK(outcome BETWEEN 0 AND 5),
    error TEXT,
    PRIMARY KEY(operation_id,task_id)
) WITHOUT ROWID, STRICT;
CREATE TABLE change_log (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    object_id TEXT NOT NULL,
    kind INTEGER NOT NULL,
    generation INTEGER NOT NULL,
    created_utc INTEGER NOT NULL
) STRICT;
