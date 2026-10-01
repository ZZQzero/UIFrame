PRAGMA application_id=1430667843;
PRAGMA user_version=1;
CREATE TABLE store_settings (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    store_id TEXT NOT NULL UNIQUE,
    server TEXT NOT NULL,
    account TEXT NOT NULL,
    paused INTEGER NOT NULL CHECK(paused IN(0,1)),
    retained_after_seq INTEGER NOT NULL DEFAULT 0 CHECK(retained_after_seq>=0),
    automatic_policy BLOB,
    native_wifi_only INTEGER NOT NULL DEFAULT 1 CHECK(native_wifi_only IN(0,1))
) STRICT;
CREATE TABLE preparations (
    id TEXT PRIMARY KEY,
    owner TEXT NOT NULL,
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 3),
    expected_count INTEGER NOT NULL DEFAULT 0 CHECK(expected_count BETWEEN 0 AND 32),
    created_utc INTEGER NOT NULL,
    error TEXT
) STRICT;
CREATE TABLE preparation_items (
    preparation_id TEXT NOT NULL REFERENCES preparations(id),
    ordinal INTEGER NOT NULL,
    id TEXT NOT NULL,
    PRIMARY KEY(preparation_id,ordinal)
) WITHOUT ROWID, STRICT;
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
    resetting INTEGER NOT NULL DEFAULT 0 CHECK(resetting IN(0,1)),
    active_baseline_id TEXT,
    pending_scan_id TEXT,
    initialized INTEGER NOT NULL DEFAULT 0 CHECK(initialized IN(0,1))
) STRICT;
CREATE TABLE scan_runs (
    id TEXT PRIMARY KEY,
    scope_id TEXT NOT NULL REFERENCES scopes(id),
    permission_generation INTEGER NOT NULL,
    scope_revision INTEGER NOT NULL,
    log_start INTEGER NOT NULL,
    selection_cursor TEXT,
    selection_version TEXT,
    baseline INTEGER NOT NULL DEFAULT 0 CHECK(baseline IN(0,1)),
    completed INTEGER NOT NULL DEFAULT 0 CHECK(completed BETWEEN 0 AND 2)
) STRICT;
CREATE TABLE scan_items (
    run_id TEXT NOT NULL REFERENCES scan_runs(id),
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    baseline_member INTEGER NOT NULL DEFAULT 0 CHECK(baseline_member IN(0,1)),
    PRIMARY KEY(run_id,source_id,content_version)
) WITHOUT ROWID, STRICT;
CREATE TABLE discoveries (
    scope_id TEXT NOT NULL REFERENCES scopes(id),
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    disposition INTEGER NOT NULL CHECK(disposition BETWEEN 0 AND 6),
    task_id TEXT,
    name TEXT NOT NULL DEFAULT '',
    mime TEXT NOT NULL DEFAULT '',
    provider_id TEXT NOT NULL DEFAULT '',
    byte_count INTEGER NOT NULL DEFAULT 0,
    error TEXT,
    PRIMARY KEY(scope_id,source_id,content_version)
) WITHOUT ROWID, STRICT;
CREATE INDEX discoveries_pending ON discoveries(scope_id,disposition,source_id,content_version);
CREATE INDEX discoveries_task ON discoveries(task_id,scope_id);
CREATE INDEX discoveries_source ON discoveries(source_id,content_version);
CREATE TABLE backup_receipts (
    backup_id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    sha256 BLOB NOT NULL CHECK(length(sha256)=32),
    byte_count INTEGER NOT NULL CHECK(byte_count>=0),
    name TEXT NOT NULL,
    mime TEXT NOT NULL,
    confirmed_utc INTEGER NOT NULL
) STRICT;
CREATE INDEX receipts_page ON backup_receipts(confirmed_utc,backup_id);
CREATE TABLE receipt_sources (
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    backup_id TEXT NOT NULL REFERENCES backup_receipts(backup_id),
    PRIMARY KEY(source_id,content_version)
) WITHOUT ROWID, STRICT;
CREATE TABLE file_records (
    id TEXT PRIMARY KEY,
    relative_path TEXT NOT NULL UNIQUE,
    byte_count INTEGER NOT NULL CHECK(byte_count>=0),
    sha256 BLOB CHECK(sha256 IS NULL OR length(sha256)=32),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 5),
    preparation_id TEXT NOT NULL,
    cleanup_error TEXT,
    updated_utc INTEGER NOT NULL
) STRICT;
CREATE INDEX files_preparation ON file_records(preparation_id,state);
CREATE INDEX files_cleanup ON file_records(state,id);
CREATE TABLE file_counts (
    state INTEGER PRIMARY KEY CHECK(state BETWEEN 0 AND 5),
    count INTEGER NOT NULL CHECK(count>=0),
    bytes INTEGER NOT NULL CHECK(bytes>=0)
) STRICT;
INSERT INTO file_counts VALUES(0,0,0),(1,0,0),(2,0,0),(3,0,0),(4,0,0),(5,0,0);
CREATE TRIGGER files_insert AFTER INSERT ON file_records BEGIN
    UPDATE file_counts SET count=count+1,bytes=bytes+NEW.byte_count WHERE state=NEW.state;
END;
CREATE TRIGGER files_update AFTER UPDATE ON file_records BEGIN
    UPDATE file_counts SET count=count-1,bytes=bytes-OLD.byte_count WHERE state=OLD.state;
    UPDATE file_counts SET count=count+1,bytes=bytes+NEW.byte_count WHERE state=NEW.state;
END;
CREATE TRIGGER files_delete AFTER DELETE ON file_records BEGIN
    UPDATE file_counts SET count=count-1,bytes=bytes-OLD.byte_count WHERE state=OLD.state;
END;
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
CREATE TABLE task_metadata (
    task_id TEXT PRIMARY KEY REFERENCES tasks(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    mime TEXT NOT NULL,
    idempotency_key TEXT NOT NULL,
    confirmed_bytes INTEGER NOT NULL DEFAULT 0 CHECK(confirmed_bytes>=0),
    retries INTEGER NOT NULL DEFAULT 0 CHECK(retries>=0)
) STRICT;
CREATE INDEX tasks_schedule ON tasks(state,next_attempt_utc,sequence);
CREATE INDEX tasks_state_page ON tasks(state,sequence);
CREATE INDEX tasks_batch ON tasks(batch_id,sequence);
CREATE INDEX tasks_source ON tasks(source_id,content_version);
CREATE INDEX tasks_file ON tasks(file_id);
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
    executor INTEGER NOT NULL DEFAULT 0 CHECK(executor BETWEEN 0 AND 2),
    credential_reference TEXT,
    wifi_only INTEGER NOT NULL DEFAULT 1 CHECK(wifi_only IN(0,1)),
    error TEXT,
    PRIMARY KEY(task_id,generation)
) WITHOUT ROWID, STRICT;
CREATE UNIQUE INDEX attempts_unreleased ON task_attempts(task_id) WHERE payload_released=0 OR credential_released=0;
CREATE TABLE operations (
    id TEXT PRIMARY KEY,
    parameter_fingerprint BLOB NOT NULL CHECK(length(parameter_fingerprint)=32),
    action INTEGER NOT NULL CHECK(action BETWEEN 0 AND 5),
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
CREATE INDEX operations_cleanup ON operations(updated_utc,id) WHERE phase IN(2,3) AND details_expired=0;
CREATE TABLE operation_items (
    operation_id TEXT NOT NULL REFERENCES operations(id),
    task_id TEXT NOT NULL,
    outcome INTEGER NOT NULL CHECK(outcome BETWEEN 0 AND 6),
    error TEXT,
    PRIMARY KEY(operation_id,task_id)
) WITHOUT ROWID, STRICT;
CREATE INDEX operation_pending ON operation_items(operation_id,outcome,task_id);
CREATE TABLE operation_targets (
    operation_id TEXT NOT NULL REFERENCES operations(id),
    ordinal INTEGER NOT NULL,
    task_id TEXT NOT NULL,
    PRIMARY KEY(operation_id,ordinal),
    UNIQUE(operation_id,task_id)
) WITHOUT ROWID, STRICT;
CREATE TABLE change_log (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    object_id TEXT NOT NULL,
    kind INTEGER NOT NULL,
    generation INTEGER NOT NULL,
    created_utc INTEGER NOT NULL
) STRICT;
CREATE TABLE task_counts (
    state INTEGER PRIMARY KEY CHECK(state BETWEEN 0 AND 9),
    count INTEGER NOT NULL CHECK(count>=0)
) STRICT;
INSERT INTO task_counts VALUES(0,0),(1,0),(2,0),(3,0),(4,0),(5,0),(6,0),(7,0),(8,0),(9,0);
CREATE TRIGGER tasks_insert AFTER INSERT ON tasks BEGIN
    UPDATE task_counts SET count=count+1 WHERE state=NEW.state;
    INSERT INTO change_log(object_id,kind,generation,created_utc) VALUES(NEW.id,0,NEW.current_generation,NEW.updated_utc);
END;
CREATE TRIGGER tasks_update AFTER UPDATE ON tasks BEGIN
    UPDATE task_counts SET count=count-1 WHERE state=OLD.state;
    UPDATE task_counts SET count=count+1 WHERE state=NEW.state;
    INSERT INTO change_log(object_id,kind,generation,created_utc) VALUES(NEW.id,1,NEW.current_generation,NEW.updated_utc);
END;
CREATE TRIGGER tasks_delete AFTER DELETE ON tasks BEGIN
    UPDATE task_counts SET count=count-1 WHERE state=OLD.state;
    INSERT INTO change_log(object_id,kind,generation,created_utc) VALUES(OLD.id,2,OLD.current_generation,OLD.updated_utc);
END;
