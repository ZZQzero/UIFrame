PRAGMA user_version=2;
CREATE TABLE settings(key TEXT PRIMARY KEY,value BLOB NOT NULL);
CREATE TABLE requests(account TEXT NOT NULL,id TEXT NOT NULL,operation TEXT NOT NULL,fingerprint TEXT NOT NULL,
 PRIMARY KEY(account,id)) WITHOUT ROWID;
CREATE TABLE tasks(account TEXT NOT NULL,id TEXT NOT NULL,metadata TEXT NOT NULL,fingerprint TEXT NOT NULL,
 PRIMARY KEY(account,id)) WITHOUT ROWID;
CREATE TABLE sources(account TEXT NOT NULL,namespace TEXT NOT NULL,id TEXT NOT NULL,version TEXT NOT NULL,
 sha256 TEXT NOT NULL,size INTEGER NOT NULL,backup_id TEXT,
 PRIMARY KEY(account,namespace,id,version)) WITHOUT ROWID;
CREATE TABLE attempts(account TEXT NOT NULL,task TEXT NOT NULL,generation INTEGER NOT NULL,
 upload_id TEXT NOT NULL UNIQUE,state TEXT NOT NULL CHECK(state IN ('upload','verifying','confirmed','canceled','rejected')),
 error_code TEXT,error_message TEXT,created_at INTEGER NOT NULL,expires_at INTEGER NOT NULL,
 PRIMARY KEY(account,task,generation)) WITHOUT ROWID;
CREATE INDEX attempts_expiry ON attempts(expires_at) WHERE state IN ('upload','verifying');
CREATE TABLE contents(account TEXT NOT NULL,sha256 TEXT NOT NULL,size INTEGER NOT NULL,path TEXT NOT NULL UNIQUE,
 verified INTEGER NOT NULL DEFAULT 0,created_at INTEGER NOT NULL,reference_count INTEGER NOT NULL DEFAULT 0 CHECK(reference_count>=0),
 cleanup_state INTEGER NOT NULL DEFAULT 0 CHECK(cleanup_state BETWEEN 0 AND 3),cleanup_error TEXT,
 PRIMARY KEY(account,sha256,size)) WITHOUT ROWID;
CREATE TABLE backups(sequence INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT NOT NULL UNIQUE,account TEXT NOT NULL,
 namespace TEXT NOT NULL,source TEXT NOT NULL,version TEXT NOT NULL,sha256 TEXT NOT NULL,size INTEGER NOT NULL,
 name TEXT NOT NULL,mime TEXT NOT NULL,confirmed_at INTEGER NOT NULL,
 UNIQUE(account,namespace,source,version));
CREATE INDEX backups_page ON backups(account,sequence);
CREATE INDEX contents_unreferenced ON contents(created_at,path) WHERE reference_count=0 AND cleanup_state=0;
CREATE INDEX contents_cleanup ON contents(cleanup_state,path) WHERE cleanup_state<>0;
CREATE TRIGGER backup_content_reference AFTER INSERT ON backups BEGIN
 UPDATE contents SET reference_count=reference_count+1 WHERE account=NEW.account AND sha256=NEW.sha256 AND size=NEW.size;
END;
CREATE TABLE incoming(id TEXT PRIMARY KEY,upload_id TEXT NOT NULL,path TEXT NOT NULL UNIQUE,
 state TEXT NOT NULL CHECK(state IN ('writing','ready','cleanup','cleanup_failed','verification_failed')),
 error TEXT,created_at INTEGER NOT NULL);
CREATE INDEX incoming_work ON incoming(state,created_at,id);
CREATE INDEX incoming_attempt ON incoming(upload_id,state);
