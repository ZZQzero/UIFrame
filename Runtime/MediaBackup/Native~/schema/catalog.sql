PRAGMA application_id=1430667843;
PRAGMA user_version=5;
CREATE TABLE store_settings (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    store_id TEXT NOT NULL UNIQUE,
    server TEXT NOT NULL,
    account TEXT NOT NULL,
    paused INTEGER NOT NULL CHECK(paused IN(0,1)),
    retained_after_seq INTEGER NOT NULL DEFAULT 0 CHECK(retained_after_seq>=0),
    automatic_policy BLOB,
    source_namespace TEXT NOT NULL DEFAULT '',
    development_http INTEGER NOT NULL DEFAULT 0 CHECK(development_http IN(0,1)),
    transfer_mode INTEGER NOT NULL DEFAULT 0 CHECK(transfer_mode IN(0,1)),
    last_any_control_kind INTEGER NOT NULL DEFAULT 2 CHECK(last_any_control_kind BETWEEN 0 AND 2),
    last_wifi_control_kind INTEGER NOT NULL DEFAULT 2 CHECK(last_wifi_control_kind BETWEEN 0 AND 2)
) STRICT;
CREATE TABLE preparations (
    id TEXT PRIMARY KEY,
    owner TEXT NOT NULL,
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 2),
    stop_requested INTEGER NOT NULL DEFAULT 0 CHECK(stop_requested IN(0,1)),
    created_utc INTEGER NOT NULL
) STRICT;
CREATE TABLE preparation_items (
    preparation_id TEXT NOT NULL REFERENCES preparations(id),
    ordinal INTEGER NOT NULL,
    id TEXT NOT NULL,
    PRIMARY KEY(preparation_id,ordinal)
) WITHOUT ROWID, STRICT;
CREATE INDEX preparations_active ON preparations(created_utc,id) WHERE phase=0;
CREATE TABLE scopes (
    id TEXT PRIMARY KEY,
    source_id TEXT NOT NULL,
    library_id TEXT NOT NULL,
    index_generation INTEGER NOT NULL CHECK(index_generation>=0),
    scope_revision INTEGER NOT NULL CHECK(scope_revision>=0),
    permission_generation INTEGER NOT NULL CHECK(permission_generation>=0),
    requires_confirmation INTEGER NOT NULL DEFAULT 0 CHECK(requires_confirmation IN(0,1)),
    admission_epoch INTEGER NOT NULL DEFAULT 1 CHECK(admission_epoch>0),
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
    scope_id TEXT REFERENCES scopes(id),
    scope_epoch INTEGER NOT NULL DEFAULT 0,
    preparation_accepted INTEGER NOT NULL DEFAULT 0 CHECK(preparation_accepted IN(0,1)),
    preparation_started INTEGER NOT NULL DEFAULT 0 CHECK(preparation_started IN(0,1)),
    source_id TEXT NOT NULL,
    content_version TEXT NOT NULL,
    state INTEGER NOT NULL CHECK(state IN(0,1,2,3,4,6,7,8,9)),
    desired_action INTEGER NOT NULL DEFAULT 0 CHECK(desired_action BETWEEN 0 AND 2),
    file_id TEXT REFERENCES file_records(id),
    backup_id TEXT REFERENCES backup_receipts(backup_id),
    current_generation INTEGER NOT NULL DEFAULT 0 CHECK(current_generation>=0),
    created_utc INTEGER NOT NULL,
    updated_utc INTEGER NOT NULL,
    error TEXT
) STRICT;
CREATE TABLE task_metadata (
    task_id TEXT PRIMARY KEY REFERENCES tasks(id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    mime TEXT NOT NULL,
    confirmed_bytes INTEGER NOT NULL DEFAULT 0 CHECK(confirmed_bytes>=0)
) STRICT;
CREATE INDEX tasks_scope ON tasks(scope_id,state,sequence);
CREATE INDEX tasks_state_page ON tasks(state,sequence);
CREATE INDEX tasks_batch ON tasks(batch_id,sequence);
CREATE INDEX tasks_source ON tasks(source_id,content_version);
CREATE INDEX tasks_file ON tasks(file_id);
CREATE INDEX tasks_history ON tasks(state,updated_utc,sequence);
CREATE TABLE task_attempts (
    task_id TEXT NOT NULL REFERENCES tasks(id),
    generation INTEGER NOT NULL CHECK(generation>0),
    system_task_id TEXT,
    submission_state INTEGER NOT NULL CHECK(submission_state IN(0,1)),
    execution_state INTEGER NOT NULL CHECK(execution_state IN(0,1,2,4)),
    server_outcome INTEGER NOT NULL CHECK(server_outcome BETWEEN 0 AND 2),
    payload_released INTEGER NOT NULL CHECK(payload_released IN(0,1)),
    credential_released INTEGER NOT NULL CHECK(credential_released IN(0,1)),
    executor INTEGER NOT NULL DEFAULT 0 CHECK(executor BETWEEN 0 AND 2),
    credential_reference TEXT,
    wifi_only INTEGER NOT NULL DEFAULT 1 CHECK(wifi_only IN(0,1)),
    protocol_phase INTEGER NOT NULL DEFAULT 0 CHECK(protocol_phase BETWEEN 0 AND 3),
    upload_reference TEXT,
    upload_expires_utc INTEGER NOT NULL DEFAULT 0,
    upload_released INTEGER NOT NULL DEFAULT 1 CHECK(upload_released IN(0,1)),
    control_id TEXT,
    next_check_utc INTEGER NOT NULL DEFAULT 0,
    confirm_deadline_utc INTEGER NOT NULL DEFAULT 0,
    confirm_checks INTEGER NOT NULL DEFAULT 0,
    system_scheduled INTEGER NOT NULL DEFAULT 0 CHECK(system_scheduled IN(0,1)),
    error TEXT,
    PRIMARY KEY(task_id,generation)
) WITHOUT ROWID, STRICT;
CREATE UNIQUE INDEX attempts_unreleased ON task_attempts(task_id) WHERE payload_released=0 OR credential_released=0;
CREATE INDEX attempts_protocol_work ON task_attempts(executor,protocol_phase,next_check_utc,task_id)
 WHERE credential_released=0 AND control_id IS NULL;
CREATE TABLE control_requests (
    id TEXT PRIMARY KEY,
    kind INTEGER NOT NULL CHECK(kind BETWEEN 0 AND 2),
    executor INTEGER NOT NULL CHECK(executor BETWEEN 0 AND 2),
    state INTEGER NOT NULL CHECK(state BETWEEN 0 AND 5),
    relative_path TEXT NOT NULL UNIQUE,
    body TEXT NOT NULL CHECK(length(CAST(body AS BLOB))<=262144),
    byte_count INTEGER NOT NULL CHECK(byte_count BETWEEN 1 AND 262144),
    not_before_utc INTEGER NOT NULL,
    created_utc INTEGER NOT NULL,
    system_task_id TEXT,
    released INTEGER NOT NULL DEFAULT 0 CHECK(released IN(0,1)),
    cleanup_state INTEGER NOT NULL DEFAULT 0 CHECK(cleanup_state BETWEEN 0 AND 3),
    error TEXT,
    cleanup_error TEXT
) STRICT;
CREATE INDEX controls_active ON control_requests(executor,not_before_utc,id) WHERE released=0;
CREATE INDEX controls_cleanup ON control_requests(cleanup_state,id) WHERE released=1 AND cleanup_state<>2;
CREATE INDEX controls_budget ON control_requests(byte_count) WHERE cleanup_state<>2;
CREATE INDEX controls_history ON control_requests(created_utc,id) WHERE released=1 AND cleanup_state=2;
CREATE TABLE control_items (
    request_id TEXT NOT NULL REFERENCES control_requests(id),
    task_id TEXT NOT NULL,
    generation INTEGER NOT NULL,
    applied INTEGER NOT NULL DEFAULT 0 CHECK(applied IN(0,1)),
    PRIMARY KEY(request_id,task_id,generation),
    FOREIGN KEY(task_id,generation) REFERENCES task_attempts(task_id,generation)
) WITHOUT ROWID, STRICT;
CREATE INDEX control_items_task ON control_items(task_id,generation,request_id);
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
    outcome INTEGER NOT NULL CHECK(outcome IN(0,1,2,3,5,6)),
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
    state INTEGER PRIMARY KEY CHECK(state IN(0,1,2,3,4,6,7,8,9)),
    count INTEGER NOT NULL CHECK(count>=0)
) STRICT;
INSERT INTO task_counts VALUES(0,0),(1,0),(2,0),(3,0),(4,0),(6,0),(7,0),(8,0),(9,0);
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
