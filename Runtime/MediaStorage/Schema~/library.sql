PRAGMA application_id=1430667852;
PRAGMA user_version=2;
CREATE TABLE library_settings (
    singleton INTEGER PRIMARY KEY CHECK(singleton=1),
    library_id TEXT NOT NULL UNIQUE,
    index_generation INTEGER NOT NULL CHECK(index_generation>0),
    retained_after_seq INTEGER NOT NULL DEFAULT 0 CHECK(retained_after_seq>=0)
) STRICT;
CREATE TABLE library_scopes (
    id TEXT PRIMARY KEY,
    provider TEXT NOT NULL,
    source_identity TEXT NOT NULL,
    revision INTEGER NOT NULL CHECK(revision>0),
    permission_generation INTEGER NOT NULL CHECK(permission_generation>=0),
    access_state INTEGER NOT NULL DEFAULT 0,
    access_fingerprint TEXT,
    completed_scan_id TEXT,
    platform_cursor TEXT,
    requires_reconcile INTEGER NOT NULL CHECK(requires_reconcile IN(0,1))
) STRICT;
CREATE TABLE assets (
    source_id TEXT PRIMARY KEY,
    content_version TEXT,
    name TEXT NOT NULL,
    mime TEXT NOT NULL,
    width INTEGER NOT NULL CHECK(width>=0),
    height INTEGER NOT NULL CHECK(height>=0),
    byte_count INTEGER CHECK(byte_count IS NULL OR byte_count>=0),
    asset_revision INTEGER NOT NULL DEFAULT 1 CHECK(asset_revision>0)
) STRICT;
CREATE TABLE scope_assets (
    scope_id TEXT NOT NULL REFERENCES library_scopes(id),
    source_id TEXT NOT NULL REFERENCES assets(source_id),
    seen_scan_id TEXT,
    updated_seq INTEGER NOT NULL,
    content_version TEXT,
    asset_revision INTEGER NOT NULL DEFAULT 0 CHECK(asset_revision>=0),
    present INTEGER NOT NULL CHECK(present IN(0,1)),
    PRIMARY KEY(scope_id,source_id)
) WITHOUT ROWID, STRICT;
CREATE INDEX assets_page ON scope_assets(scope_id,present,source_id);
CREATE INDEX assets_scan ON scope_assets(scope_id,seen_scan_id,source_id);
CREATE TABLE scan_runs (
    id TEXT PRIMARY KEY,
    scope_id TEXT NOT NULL REFERENCES library_scopes(id),
    permission_generation INTEGER NOT NULL,
    scope_revision INTEGER NOT NULL,
    log_start INTEGER NOT NULL,
    phase INTEGER NOT NULL CHECK(phase BETWEEN 0 AND 3),
    platform_upper_bound TEXT,
    missing_cursor TEXT,
    error TEXT
) STRICT;
CREATE INDEX scans_scope_phase ON scan_runs(scope_id,phase);
CREATE INDEX scans_retention ON scan_runs(phase,id);
CREATE TABLE change_log (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    scope_id TEXT NOT NULL,
    source_id TEXT NOT NULL,
    content_version TEXT,
    kind INTEGER NOT NULL,
    scope_revision INTEGER NOT NULL,
    permission_generation INTEGER NOT NULL
) STRICT;
CREATE INDEX changes_scope ON change_log(scope_id,sequence);
