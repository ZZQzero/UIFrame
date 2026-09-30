#include "ufbackup.h"
#include "ufsqlite_client.hpp"
#include "catalog_schema.hpp"
#include <algorithm>
#include <atomic>
#include <filesystem>
#include <exception>
#include <map>
#include <memory>
#include <mutex>

namespace {
using namespace ufsqlite;
constexpr uint32_t MaxBytes=1024*1024;
std::mutex registry_gate, open_gate;
std::atomic<unsigned> calls{0};
uint64_t next_handle=1;
struct Args {
    std::vector<Value> values;
    Args(const uint8_t *input,uint32_t length) {
        require(length>=4 && length<=MaxBytes && input,"Invalid command payload");
        Reader r{input,length}; auto count=r.number(4); require(count<=2048,"Too many command values");
        values.reserve(size_t(count));
        for(uint64_t i=0;i<count;++i) values.push_back(Value::read(r));
        require(!r.left,"Trailing command data");
    }
    void count(size_t n) const { require(values.size()==n,"Wrong command argument count"); }
    const Value &value(size_t i,uint8_t type) const { require(i<values.size() && values[i].type==type,"Invalid command argument type"); return values[i]; }
    std::string text(size_t i,size_t limit=4096) const {
        const auto &v=value(i,3).text; require(v.size()<=limit && v.find('\0')==std::string::npos,"Invalid text argument"); return v;
    }
    int64_t number(size_t i,int64_t low=0,int64_t high=INT64_MAX) const {
        auto n=value(i,1).integer; require(n>=low && n<=high,"Invalid numeric argument"); return n;
    }
    std::string id(size_t i) const { auto s=text(i,64); require(!s.empty() && std::all_of(s.begin(),s.end(),[](char c){return (c>='a' && c<='f') || (c>='0' && c<='9');}),"Invalid identity"); return s; }
};
std::string require_id(const char *s) {
    require(s,"Missing identity"); std::string id(s);
    require(id.size()==64 && std::all_of(id.begin(),id.end(),[](char c){return (c>='a' && c<='f') || (c>='0' && c<='9');}),"Store ID must be 64 lowercase hexadecimal characters"); return id;
}
std::string sha(const Args &a,size_t index) {
    auto hex=a.text(index,64); require(hex.size()==64,"SHA-256 must have 64 hexadecimal characters");
    std::string bytes; bytes.reserve(32);
    for(size_t i=0;i<64;i+=2) {
        auto digit=[](char c)->unsigned { require((c>='0'&&c<='9') || (c>='a'&&c<='f'),"Invalid SHA-256"); return c<='9'?unsigned(c-'0'):unsigned(c-'a'+10); };
        bytes.push_back(char((digit(hex[i])<<4)|digit(hex[i+1])));
    }
    return bytes;
}
const std::string TaskColumns=
    "SELECT t.sequence,t.id,t.batch_id,t.source_id,t.content_version,t.state,t.desired_action,t.current_generation,"
    "t.created_utc,t.updated_utc,t.error,t.backup_id,m.name,m.mime,m.idempotency_key,m.confirmed_bytes,m.retries,"
    "f.byte_count,lower(hex(f.sha256)) AS sha256,f.relative_path,f.state AS file_state,f.cleanup_error,"
    "a.executor,a.submission_state,a.execution_state,a.payload_released,a.credential_released,a.system_task_id,"
    "a.session_id,a.credential_reference,a.wifi_only,t.next_attempt_utc,a.generation AS attempt_generation "
    "FROM tasks t JOIN task_metadata m ON m.task_id=t.id JOIN file_records f ON f.id=t.file_id "
    "LEFT JOIN task_attempts a ON a.task_id=t.id AND a.generation=t.current_generation ";
const std::string ReceiptColumns="SELECT backup_id,source_id,content_version,lower(hex(sha256)) AS sha256,byte_count,name,mime,confirmed_utc FROM backup_receipts ";
const std::string Unreleased="EXISTS(SELECT 1 FROM task_attempts a WHERE a.task_id=tasks.id AND (a.payload_released=0 OR a.credential_released=0))";
const std::string CleanupEligible="NOT EXISTS(SELECT 1 FROM tasks t JOIN task_attempts a ON a.task_id=t.id WHERE t.file_id=file_records.id AND (a.payload_released=0 OR a.credential_released=0))";

struct Repository {
    std::mutex gate;
    Client db;
    std::string path,id,server,account;
    unsigned attachments=0;
    uint64_t preparer_handle=0;
    std::string preparer;
    bool closed=false, faulted=false;
    Repository(const std::string &p,const std::string &identity,const std::string &s,const std::string &a,bool create):path(p),id(identity),server(s),account(a) {
        db.open(path,create);
        if(create) {
            std::vector<Command> commands;
            for(auto sql:catalog_schema) commands.emplace_back(sql);
            commands.emplace_back("INSERT INTO store_settings(singleton,store_id,server,account,paused) VALUES(1,?,?,?,0)",std::vector<Value>{id,server,account},1);
            db.batch(commands);
        }
        auto header=decode(db.batch({{"PRAGMA application_id"},{"PRAGMA user_version"}}));
        require(header[0].rows[0][0].integer==1430667843 && header[1].rows[0][0].integer==1,"Unexpected backup catalog schema",UF_STATE);
        auto settings=decode(db.query({"SELECT store_id,server,account FROM store_settings WHERE singleton=1"})).back().rows;
        require(settings.size()==1 && settings[0][0].text==id,"Backup store identity mismatch",UF_STATE);
        require((server.empty() || server==settings[0][1].text) && (account.empty() || account==settings[0][2].text),"Backup server/account mismatch",UF_STATE);
        server=settings[0][1].text; account=settings[0][2].text;
    }
    Command task(const std::string &id) { return {TaskColumns+"WHERE t.id=?",{id}}; }
    Bytes execute(unsigned command,const Args &a,unsigned capacity) {
        require(!closed && !faulted,"Backup repository is closed or faulted",UF_STATE);
        std::vector<Command> commands;
        bool read=false;
        switch(command) {
        case UFB_INFO: a.count(0); read=true; commands.emplace_back("SELECT store_id,server,account,paused,native_wifi_only,retained_after_seq,(SELECT coalesce(max(sequence),0) FROM tasks) AS upper_sequence FROM store_settings WHERE singleton=1"); break;
        case UFB_PREPARE: {
            auto batch=a.id(0), owner=a.id(1); auto now=a.number(2), n=a.number(3,1,32); a.count(size_t(4+n*6));
            commands.emplace_back("INSERT INTO preparations(id,owner,phase,created_utc,expected_count) VALUES(?,?,0,?,?)",std::vector<Value>{batch,owner,now,n},1);
            for(int64_t i=0;i<n;++i) {
                size_t at=size_t(4+i*6); auto item=a.id(at), source=a.text(at+1),version=a.text(at+2),name=a.text(at+3,1024),mime=a.text(at+4,128); auto reserve=a.number(at+5);
                require(!source.empty() && !version.empty(),"Source identity and content version are required");
                commands.emplace_back("INSERT INTO file_records(id,relative_path,byte_count,state,preparation_id,updated_utc) VALUES(?,?,?,0,?,?)",std::vector<Value>{item,"payloads/"+item+".payload",reserve,batch,now},1);
                commands.emplace_back("INSERT INTO tasks(id,batch_id,source_id,content_version,state,file_id,created_utc,updated_utc) VALUES(?,?,?,?,9,?,?,?)",std::vector<Value>{item,batch,source,version,item,now,now},1);
                commands.emplace_back("INSERT INTO task_metadata(task_id,name,mime,idempotency_key) VALUES(?,?,?,'')",std::vector<Value>{item,name,mime},1);
                commands.emplace_back("INSERT INTO preparation_items(preparation_id,ordinal,id) VALUES(?,?,?)",std::vector<Value>{batch,i,item},1);
            }
            break;
        }
        case UFB_SEAL: {
            a.count(8); auto id=a.id(0),owner=a.id(1),key=a.id(4),mime=a.text(5,128); auto bytes=a.number(2),budget=a.number(6,1),now=a.number(7); auto hash=Value::blob(sha(a,3));
            auto payload=std::filesystem::u8path(path).parent_path()/"payloads"/(id+".payload");
            require(std::filesystem::is_regular_file(payload) && !std::filesystem::is_symlink(payload) && std::filesystem::file_size(payload)==uint64_t(bytes),"Prepared payload is missing or size changed",UF_IO);
            commands.emplace_back("UPDATE file_records SET byte_count=?,sha256=?,state=1,updated_utc=? WHERE id=? AND state=0 AND EXISTS(SELECT 1 FROM preparations p WHERE p.id=preparation_id AND p.phase=0 AND p.owner=?) AND ? <= ? - (SELECT coalesce(sum(bytes),0) FROM file_counts WHERE state<>3) + byte_count",std::vector<Value>{bytes,hash,now,id,owner,bytes,budget},1);
            commands.emplace_back("UPDATE task_metadata SET idempotency_key=?,mime=? WHERE task_id=?",std::vector<Value>{key,mime,id},1); break;
        }
        case UFB_ACCEPT: {
            a.count(3); auto batch=a.id(0),owner=a.id(1); auto now=a.number(2);
            commands.emplace_back("UPDATE preparations SET phase=1 WHERE id=? AND owner=? AND phase=0 AND expected_count=(SELECT count(*) FROM file_records WHERE preparation_id=preparations.id AND state=1) AND NOT EXISTS(SELECT 1 FROM file_records WHERE preparation_id=? AND state<>1)",std::vector<Value>{batch,owner,batch},1);
            commands.emplace_back("UPDATE tasks SET state=0,updated_utc=? WHERE batch_id=? AND state=9",std::vector<Value>{now,batch});
            commands.emplace_back("UPDATE discoveries SET disposition=2,task_id=(SELECT id FROM tasks WHERE batch_id=? AND source_id=discoveries.source_id AND content_version=discoveries.content_version ORDER BY sequence LIMIT 1) WHERE disposition=0 AND EXISTS(SELECT 1 FROM tasks WHERE batch_id=? AND source_id=discoveries.source_id AND content_version=discoveries.content_version)",std::vector<Value>{batch,batch}); break;
        }
        case UFB_ABANDON: {
            a.count(4); auto batch=a.id(0),owner=a.id(1),error=a.text(3); auto now=a.number(2);
            commands.emplace_back("UPDATE preparations SET phase=2,error=? WHERE id=? AND owner=? AND phase=0",std::vector<Value>{error,batch,owner},1);
            commands.emplace_back("DELETE FROM tasks WHERE batch_id=? AND state=9",std::vector<Value>{batch});
            commands.emplace_back("UPDATE file_records SET state=2,updated_utc=? WHERE preparation_id=? AND state IN(0,1)",std::vector<Value>{now,batch}); break;
        }
        case UFB_TASKS: {
            a.count(4); auto after=a.number(0),upper=a.number(1),state=a.number(2,-1,9),limit=a.number(3,1,200); read=true;
            commands.emplace_back(TaskColumns+"WHERE t.sequence>? AND t.sequence<=? "+(state<0?"":"AND t.state=? ")+"ORDER BY t.sequence LIMIT ?",state<0?std::vector<Value>{after,upper,limit}:std::vector<Value>{after,upper,state,limit}); break;
        }
        case UFB_TASK: a.count(1); read=true; commands.push_back(task(a.id(0))); break;
        case UFB_SUMMARY: a.count(0); read=true; commands.emplace_back("SELECT state,count,(SELECT paused FROM store_settings WHERE singleton=1) AS paused FROM task_counts ORDER BY state"); break;
        case UFB_CLAIM: {
            a.count(5); auto id=a.id(0); auto executor=a.number(1,0,2),wifi=a.number(2,0,1),now=a.number(3); auto session=a.id(4);
            commands.emplace_back("UPDATE tasks SET current_generation=current_generation+1,state=1,error=NULL,updated_utc=? WHERE id=? AND state IN(0,5) AND next_attempt_utc<=? AND desired_action=0 AND NOT "+Unreleased+" AND (SELECT paused FROM store_settings WHERE singleton=1)=0",std::vector<Value>{now,id,now},1);
            commands.emplace_back("INSERT INTO task_attempts(task_id,generation,session_id,idempotency_key,submission_state,execution_state,server_outcome,payload_released,credential_released,executor,wifi_only) SELECT t.id,t.current_generation,?,t.id||':'||t.current_generation,0,0,0,0,0,?,? FROM tasks t JOIN task_metadata m ON m.task_id=t.id WHERE t.id=?",std::vector<Value>{session,executor,wifi,id},1);
            commands.push_back(task(id)); break;
        }
        case UFB_SUBMITTED: {
            a.count(4); auto id=a.id(0); auto generation=a.number(1,1); auto system=a.text(2,256),credential=a.text(3,16384);
            commands.emplace_back("UPDATE task_attempts SET submission_state=1,system_task_id=?,credential_reference=? WHERE task_id=? AND generation=? AND execution_state IN(0,1) AND payload_released=0 AND (submission_state=0 OR (system_task_id=? AND credential_reference=?))",std::vector<Value>{system,credential,id,generation,system,credential},1); break;
        }
        case UFB_START: {
            a.count(2); auto id=a.id(0); auto generation=a.number(1,1);
            commands.emplace_back("UPDATE task_attempts SET task_id=task_id WHERE task_id=? AND generation=? AND execution_state=0 AND payload_released=0 AND EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.current_generation=generation AND t.state=1)",std::vector<Value>{id,generation},1);
            commands.emplace_back("UPDATE task_attempts SET execution_state=1 WHERE task_id=? AND generation=? AND EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.desired_action=0) AND (SELECT paused FROM store_settings WHERE singleton=1)=0",std::vector<Value>{id,generation});
            commands.emplace_back(TaskColumns+"WHERE t.id=? AND changes()=1",std::vector<Value>{id}); break;
        }
        case UFB_PROGRESS: {
            a.count(5); auto id=a.id(0); auto generation=a.number(1,1),bytes=a.number(2),verifying=a.number(3,0,1),now=a.number(4);
            commands.emplace_back("UPDATE task_metadata SET confirmed_bytes=? WHERE task_id=? AND EXISTS(SELECT 1 FROM tasks t JOIN file_records f ON f.id=t.file_id WHERE t.id=task_id AND t.current_generation=? AND t.state IN(1,2) AND ?<=f.byte_count)",std::vector<Value>{bytes,id,generation,bytes},1);
            commands.emplace_back("UPDATE tasks SET state=?,updated_utc=? WHERE id=? AND current_generation=?",std::vector<Value>{int64_t(verifying?2:1),now,id,generation},1); break;
        }
        case UFB_FINISH: {
            // outcome: 1 confirmed, 2 definitive failure, 3 interrupted/unknown.
            a.count(8); auto id=a.id(0); auto generation=a.number(1,1),outcome=a.number(2,1,4); auto backup=a.text(3,256),error=a.text(4); auto now=a.number(5),size=a.number(6); auto hash=Value::blob(sha(a,7));
            if(outcome==1) {
                require(!backup.empty(),"Confirmed backup ID is required");
                commands.emplace_back("UPDATE task_attempts SET execution_state=2,server_outcome=1,error=NULL WHERE task_id=? AND generation=? AND execution_state IN(0,1,2,4) AND EXISTS(SELECT 1 FROM tasks t JOIN file_records f ON f.id=t.file_id WHERE t.id=task_id AND t.current_generation=generation AND f.sha256=? AND f.byte_count=?)",std::vector<Value>{id,generation,hash,size},1);
                commands.emplace_back("INSERT INTO backup_receipts(backup_id,source_id,content_version,sha256,byte_count,name,mime,confirmed_utc) SELECT ?,t.source_id,t.content_version,f.sha256,f.byte_count,m.name,m.mime,? FROM tasks t JOIN file_records f ON f.id=t.file_id JOIN task_metadata m ON m.task_id=t.id WHERE t.id=? ON CONFLICT(backup_id) DO NOTHING",std::vector<Value>{backup,now,id});
                commands.emplace_back("UPDATE backup_receipts SET backup_id=backup_id WHERE backup_id=? AND sha256=? AND byte_count=? AND source_id=(SELECT source_id FROM tasks WHERE id=?)",std::vector<Value>{backup,hash,size,id},1);
                commands.emplace_back("INSERT INTO receipt_sources(source_id,content_version,backup_id) SELECT source_id,content_version,? FROM tasks WHERE id=? ON CONFLICT(source_id,content_version) DO NOTHING",std::vector<Value>{backup,id});
                commands.emplace_back("UPDATE receipt_sources SET backup_id=backup_id WHERE (source_id,content_version)=(SELECT source_id,content_version FROM tasks WHERE id=?) AND backup_id=?",std::vector<Value>{id,backup},1);
                commands.emplace_back("UPDATE tasks SET state=3,desired_action=0,backup_id=?,error=NULL,updated_utc=? WHERE id=? AND current_generation=?",std::vector<Value>{backup,now,id,generation},1);
                commands.emplace_back("UPDATE task_metadata SET confirmed_bytes=? WHERE task_id=?",std::vector<Value>{size,id},1);
                commands.emplace_back("UPDATE file_records SET state=CASE WHEN state=3 THEN 3 ELSE 2 END,updated_utc=? WHERE id=(SELECT file_id FROM tasks WHERE id=?)",std::vector<Value>{now,id},1);
                commands.emplace_back("UPDATE discoveries SET disposition=3,error=NULL WHERE (source_id,content_version)=(SELECT source_id,content_version FROM tasks WHERE id=?)",std::vector<Value>{id});
            } else {
                commands.emplace_back("UPDATE task_attempts SET execution_state=?,server_outcome=?,error=? WHERE task_id=? AND generation=? AND execution_state IN(0,1,4)",std::vector<Value>{int64_t(outcome==2?3:4),int64_t(outcome==3?0:2),error,id,generation},1);
                // Unknown server result remains NeedsAttention even when cancellation was requested.
                commands.emplace_back("UPDATE tasks SET state=CASE WHEN ?=4 THEN CASE desired_action WHEN 2 THEN 8 WHEN 1 THEN 4 ELSE 0 END ELSE ? END,error=?,updated_utc=? WHERE id=? AND current_generation=? AND state<>3",std::vector<Value>{outcome,int64_t(outcome==2?7:6),error,now,id,generation},1);
                commands.emplace_back("UPDATE file_records SET state=2,updated_utc=? WHERE id=(SELECT file_id FROM tasks WHERE id=? AND state=8) AND state=1",std::vector<Value>{now,id});
                commands.emplace_back("UPDATE discoveries SET disposition=5 WHERE task_id=? AND EXISTS(SELECT 1 FROM tasks WHERE id=? AND state=8)",std::vector<Value>{id,id});
            }
            break;
        }
        case UFB_RELEASE: {
            a.count(4); auto id=a.id(0); auto generation=a.number(1,1),payload=a.number(2,0,1),credential=a.number(3,0,1);
            commands.emplace_back("UPDATE task_attempts SET payload_released=max(payload_released,?),credential_released=max(credential_released,?),credential_reference=CASE WHEN ?=1 THEN NULL ELSE credential_reference END WHERE task_id=? AND generation=? AND execution_state IN(2,3,4)",std::vector<Value>{payload,credential,credential,id,generation},1); break;
        }
        case UFB_ACTION: {
            a.count(3); auto id=a.id(0); auto action=a.number(1,0,3),now=a.number(2);
            append_action(commands,id,action,now,true); commands.push_back(task(id)); break;
        }
        case UFB_PAUSE: a.count(1); commands.emplace_back("UPDATE store_settings SET paused=? WHERE singleton=1",std::vector<Value>{a.number(0,0,1)},1); break;
        case UFB_RECEIPTS: a.count(3); read=true; commands.emplace_back(ReceiptColumns+"WHERE (confirmed_utc,backup_id)>(?,?) ORDER BY confirmed_utc,backup_id LIMIT ?",std::vector<Value>{a.number(0),a.text(1,256),a.number(2,1,200)}); break;
        case UFB_RECEIPT: a.count(1); read=true; commands.emplace_back(ReceiptColumns+"WHERE backup_id=?",std::vector<Value>{a.text(0,256)}); break;
        case UFB_CLEANUP_PAGE: a.count(2); read=true; commands.emplace_back("SELECT id,relative_path,byte_count,state,updated_utc FROM file_records WHERE state IN(2,5) AND id>? AND "+CleanupEligible+" ORDER BY id LIMIT ?",std::vector<Value>{a.text(0,64),a.number(1,1,200)}); break;
        case UFB_CLEANUP_RUN: {
            a.count(3); auto id=a.id(0); auto version=a.number(1),now=a.number(2);
            auto selected=decode(db.batch({{"UPDATE file_records SET state=5 WHERE id=? AND state IN(2,5) AND updated_utc=? AND "+CleanupEligible+" RETURNING relative_path",{id,version}}})).back().rows;
            if(selected.empty()) return db.query({"SELECT 0 AS deleted,0 AS freed_bytes"},capacity);
            // Only this repository owns file effects; adapters never unlink payloads.
            auto relative=selected[0][0].text;
            require(relative=="payloads/"+id+".payload","Unexpected owned payload path",UF_STATE);
            auto payload=std::filesystem::u8path(path).parent_path()/relative;
            try {
                int64_t bytes=0;
                if(std::filesystem::exists(payload)) {
                    require(!std::filesystem::is_symlink(payload),"Owned payload is a symbolic link",UF_IO);
                    bytes=int64_t(std::filesystem::file_size(payload)); std::filesystem::remove(payload);
                }
                auto exported=payload; exported+=".source";
                if(std::filesystem::exists(exported)) {
                    require(!std::filesystem::is_symlink(exported),"Owned export directory is a symbolic link",UF_IO);
                    for(auto &entry:std::filesystem::directory_iterator(exported)) {
                        require(entry.is_regular_file() && !entry.is_symlink(),"Unexpected export directory entry",UF_IO);
                        bytes+=int64_t(entry.file_size());std::filesystem::remove(entry.path());
                    }
                    std::filesystem::remove(exported);
                }
                return db.batch({{"UPDATE file_records SET state=3,cleanup_error=NULL,updated_utc=? WHERE id=? AND state=5",{now,id},1},{"SELECT 1 AS deleted,? AS freed_bytes",{bytes}}},capacity);
            } catch(...) {
                auto primary=std::current_exception();
                try { std::rethrow_exception(primary); }
                catch(const Error &e) { if(e.committed<0) { faulted=true; throw; } }
                catch(...) {}
                try { std::rethrow_exception(primary); }
                catch(const std::exception &e) {
                    try { db.batch({{"UPDATE file_records SET state=4,cleanup_error=?,updated_utc=? WHERE id=? AND state=5",{std::string(e.what()),now,id},1}}); }
                    catch(const std::exception &cleanup) { std::fprintf(stderr,"Backup cleanup recording failed: %s\n",cleanup.what()); }
                }
                std::rethrow_exception(primary);
            }
        }
        case UFB_CLEANUP_RETRY: a.count(2); commands.emplace_back("UPDATE file_records SET state=2,cleanup_error=NULL,updated_utc=? WHERE id=? AND state=4",std::vector<Value>{a.number(1),a.id(0)},1); break;
        case UFB_ATTEMPTS: a.count(3); read=true; commands.emplace_back(TaskColumns+"WHERE t.sequence>? AND a.executor=? AND (a.payload_released=0 OR a.credential_released=0) ORDER BY t.sequence LIMIT ?",std::vector<Value>{a.number(0),a.number(1,0,2),a.number(2,1,200)}); break;
        case UFB_CHANGES: {
            a.count(2); auto after=a.number(0),limit=a.number(1,1,199);
            commands.emplace_back("SELECT store_id,retained_after_seq,? < retained_after_seq AS requires_refresh FROM store_settings WHERE singleton=1",std::vector<Value>{after});
            commands.emplace_back("SELECT sequence,object_id,kind,generation,created_utc FROM change_log WHERE sequence>? ORDER BY sequence LIMIT ?",std::vector<Value>{after,limit}); break;
        }
        case UFB_POLICY: a.count(0); read=true; commands.emplace_back("SELECT automatic_policy FROM store_settings WHERE singleton=1"); break;
        case UFB_SCOPE_STATE: a.count(1);read=true;commands.emplace_back("SELECT * FROM scopes WHERE id=?",std::vector<Value>{a.id(0)});break;
        case UFB_SET_POLICY: a.count(1); require(a.value(0,4).text.size()<=16384,"Policy exceeds 16 KiB"); commands.emplace_back("UPDATE store_settings SET automatic_policy=? WHERE singleton=1",std::vector<Value>{a.values[0]},1); break;
        case UFB_SCOPE: {
            a.count(7); auto scope=a.id(0),source=a.text(1),library=a.id(2); auto generation=a.number(3,1),revision=a.number(4,1),permission=a.number(5),include=a.number(6,0,1);
            commands.emplace_back("INSERT INTO scopes(id,source_id,library_id,index_generation,scope_revision,permission_generation,include_existing) VALUES(?,?,?,?,?,?,?) ON CONFLICT(id) DO NOTHING",std::vector<Value>{scope,source,library,generation,revision,permission,include});
            commands.emplace_back("UPDATE scopes SET enabled=CASE WHEN library_id=? AND index_generation=? AND scope_revision=? AND permission_generation=? THEN enabled ELSE 0 END,consumed_seq=CASE WHEN library_id=? AND index_generation=? THEN consumed_seq ELSE 0 END,library_id=?,index_generation=?,scope_revision=?,permission_generation=? WHERE id=? AND source_id=? AND include_existing=? AND resetting=0",std::vector<Value>{library,generation,revision,permission,library,generation,library,generation,revision,permission,scope,source,include},1);
            commands.emplace_back("SELECT * FROM scopes WHERE id=?",std::vector<Value>{scope}); break;
        }
        case UFB_BEGIN_SCAN: {
            a.count(7); auto scope=a.id(0),run=a.id(1),library=a.id(2); auto generation=a.number(3,1),revision=a.number(4,1),permission=a.number(5),start=a.number(6);
            commands.emplace_back("UPDATE scopes SET enabled=0,consumed_seq=?,pending_scan_id=? WHERE id=? AND library_id=? AND index_generation=? AND scope_revision=? AND permission_generation=?",std::vector<Value>{start,run,scope,library,generation,revision,permission},1);
            commands.emplace_back("UPDATE scan_runs SET completed=2 WHERE scope_id=? AND completed=0",std::vector<Value>{scope});
            commands.emplace_back("INSERT INTO scan_runs(id,scope_id,permission_generation,scope_revision,log_start,baseline) SELECT ?,id,permission_generation,scope_revision,?,CASE WHEN initialized=0 AND include_existing=0 THEN 1 ELSE 0 END FROM scopes WHERE id=?",std::vector<Value>{run,start,scope},1); break;
        }
        case UFB_SCAN_PAGE: {
            auto scope=a.id(0),run=a.id(1); auto n=a.number(2,1,32); a.count(size_t(3+n*6));
            commands.emplace_back("UPDATE scopes SET id=id WHERE id=? AND pending_scan_id=? AND EXISTS(SELECT 1 FROM scan_runs r WHERE r.id=pending_scan_id AND r.completed=0 AND r.scope_revision=scopes.scope_revision AND r.permission_generation=scopes.permission_generation)",std::vector<Value>{scope,run},1);
            for(int64_t i=0;i<n;++i) {
                auto at=size_t(3+i*6); auto source=a.text(at),version=a.text(at+1),name=a.text(at+2,1024),mime=a.text(at+3,128),provider=a.text(at+4); auto bytes=a.number(at+5);
                require(!source.empty() && !version.empty(),"Discovery requires source identity and content version");
                commands.emplace_back("INSERT INTO scan_items(run_id,source_id,content_version) VALUES(?,?,?) ON CONFLICT DO NOTHING",std::vector<Value>{run,source,version});
                append_discovery(commands,scope,source,version,name,mime,provider,bytes);
            }
            break;
        }
        case UFB_ACTIVATE_SCAN: {
            a.count(9); auto scope=a.id(0),run=a.id(1),library=a.id(2); auto generation=a.number(3,1),revision=a.number(4,1),permission=a.number(5),start=a.number(6),through=a.number(7),retained=a.number(8);
            require(retained<=start && through>=start,"Library change log was truncated; reconciliation required",UF_CONDITION);
            commands.emplace_back("UPDATE scan_runs SET completed=1 WHERE id=? AND scope_id=? AND completed=0 AND log_start=? AND scope_revision=? AND permission_generation=?",std::vector<Value>{run,scope,start,revision,permission},1);
            commands.emplace_back("UPDATE scopes SET active_baseline_id=CASE WHEN (SELECT baseline FROM scan_runs WHERE id=?)=1 THEN ? ELSE active_baseline_id END,enabled=1,initialized=1,pending_scan_id=NULL WHERE id=? AND pending_scan_id=? AND library_id=? AND index_generation=? AND scope_revision=? AND permission_generation=? AND consumed_seq=?",std::vector<Value>{run,run,scope,run,library,generation,revision,permission,through},1); break;
        }
        case UFB_DISCOVER: {
            auto scope=a.id(0),library=a.id(1); auto generation=a.number(2,1),revision=a.number(3,1),permission=a.number(4),from=a.number(5),through=a.number(6),retained=a.number(7),n=a.number(8,0,32); a.count(size_t(9+n*8));
            require(retained<=from && through>=from,"Library cursor no longer continuous",UF_CONDITION);
            commands.emplace_back("UPDATE scopes SET consumed_seq=? WHERE id=? AND library_id=? AND index_generation=? AND scope_revision=? AND permission_generation=? AND consumed_seq=?",std::vector<Value>{through,scope,library,generation,revision,permission,from},1);
            auto last=from;
            for(int64_t i=0;i<n;++i) {
                require(last<through,"Change sequence exceeds declared page boundary");
                auto at=size_t(9+i*8); auto sequence=a.number(at,last+1,through),kind=a.number(at+1,0,4); last=sequence;
                auto source=a.text(at+2),version=a.text(at+3),name=a.text(at+4,1024),mime=a.text(at+5,128),provider=a.text(at+6); auto bytes=a.number(at+7);
                if(kind<=2) append_discovery(commands,scope,source,version,name,mime,provider,bytes);
                else {
                    commands.emplace_back("UPDATE discoveries SET disposition=6 WHERE scope_id=? AND source_id=? AND disposition=0",std::vector<Value>{scope,source});
                    commands.emplace_back("UPDATE tasks SET desired_action=1,state=CASE WHEN state IN(0,5) THEN 4 ELSE state END WHERE id IN(SELECT task_id FROM discoveries WHERE scope_id=? AND source_id=?) AND state IN(0,1,2,5)",std::vector<Value>{scope,source});
                }
            }
            break;
        }
        case UFB_DISCOVERIES: {
            a.count(5); auto scope=a.id(0),source=a.text(1),version=a.text(2); auto disposition=a.number(3,0,6),limit=a.number(4,1,200); read=true;
            commands.emplace_back("SELECT source_id,content_version,name,mime,provider_id,byte_count,disposition,task_id,error FROM discoveries WHERE scope_id=? AND disposition=? AND (source_id,content_version)>(?,?) ORDER BY source_id,content_version LIMIT ?",std::vector<Value>{scope,disposition,source,version,limit}); break;
        }
        case UFB_DISPOSITION: {
            a.count(5); auto scope=a.id(0),source=a.text(1),version=a.text(2),error=a.text(4); auto disposition=a.number(3,0,5);
            require(disposition==0 || disposition==4 || disposition==5,"Only explicit retry, preparation failure or cancellation may set discovery disposition");
            commands.emplace_back("UPDATE discoveries SET disposition=?,error=? WHERE scope_id=? AND source_id=? AND content_version=? AND disposition IN(0,4,5)",std::vector<Value>{disposition,error,scope,source,version},1); break;
        }
        case UFB_OPERATION: {
            // Explicit targets are bounded per durable operation. A filter can
            // select an arbitrarily large history through successive pages.
            auto id=a.id(0); auto action=a.number(1,0,5),state=a.number(2,-1,9),now=a.number(3),n=a.number(4,0,128); a.count(size_t(6+n));
            auto fingerprint=a.value(5,4); require(fingerprint.text.size()==32,"Operation fingerprint must be SHA-256");
            Bytes criteria; number(criteria,uint64_t(state),8); number(criteria,n,4);
            std::vector<std::string> targets;
            for(int64_t i=0;i<n;++i) { auto target=a.id(size_t(6+i)); require(std::find(targets.begin(),targets.end(),target)==targets.end(),"Duplicate operation target"); targets.push_back(target); string(criteria,target); }
            auto parameters=Value::blob(std::string(criteria.begin(),criteria.end()));
            // Before detail expiry compare exact parameters too. Afterwards the
            // canonical parameter fingerprint is the durable idempotency key.
            commands.emplace_back("INSERT INTO operations(id,parameter_fingerprint,action,phase,criteria,upper_sequence,created_utc,updated_utc) VALUES(?,?,?,0,?,(SELECT coalesce(max(sequence),0) FROM tasks),?,?) ON CONFLICT(id) DO NOTHING",std::vector<Value>{id,fingerprint,action,parameters,now,now});
            commands.emplace_back("UPDATE operations SET id=id WHERE id=? AND action=? AND (criteria=? OR details_expired=1) AND parameter_fingerprint=?",std::vector<Value>{id,action,parameters,fingerprint},1);
            for(size_t i=0;i<targets.size();++i) commands.emplace_back("INSERT INTO operation_targets(operation_id,ordinal,task_id) SELECT ?,?,? WHERE EXISTS(SELECT 1 FROM operations WHERE id=? AND phase=0) ON CONFLICT(operation_id,ordinal) DO NOTHING",std::vector<Value>{id,int64_t(i+1),targets[i],id});
            commands.emplace_back("SELECT * FROM operations WHERE id=?",std::vector<Value>{id}); break;
        }
        case UFB_SELECT_OPERATION: {
            a.count(3); auto id=a.id(0); auto now=a.number(1),limit=a.number(2,1,128);
            auto rows=decode(db.query({"SELECT phase,criteria,upper_sequence,selection_cursor FROM operations WHERE id=?",{id}})).back().rows;
            require(rows.size()==1,"Operation not found",UF_STATE); auto &op=rows[0];
            if(op[0].integer!=0) return db.query({"SELECT * FROM operations WHERE id=?",{id}},capacity);
            auto &encoded=op[1].text; Reader criteria{reinterpret_cast<const uint8_t*>(encoded.data()),encoded.size()};
            auto state=int64_t(criteria.number(8)); auto explicit_count=criteria.number(4); auto upper=op[2].integer,cursor=op[3].integer;
            std::vector<std::vector<Value>> selected;
            if(explicit_count) selected=decode(db.query({"SELECT ordinal,task_id FROM operation_targets WHERE operation_id=? AND ordinal>? ORDER BY ordinal LIMIT ?",{id,cursor,limit}})).back().rows;
            else selected=decode(db.query({"SELECT sequence,id FROM tasks WHERE sequence>? AND sequence<=? "+std::string(state<0?"":"AND state=? ")+"ORDER BY sequence LIMIT ?",state<0?std::vector<Value>{cursor,upper,limit}:std::vector<Value>{cursor,upper,state,limit}})).back().rows;
            for(auto &row:selected) { commands.emplace_back("INSERT INTO operation_items(operation_id,task_id,outcome) VALUES(?,?,0)",std::vector<Value>{id,row[1]},1); cursor=row[0].integer; }
            commands.emplace_back("UPDATE operations SET selection_cursor=?,selected_count=selected_count+?,phase=?,updated_utc=? WHERE id=? AND phase=0",std::vector<Value>{cursor,int64_t(selected.size()),int64_t(selected.size()<size_t(limit)?1:0),now,id},1);
            commands.emplace_back("SELECT * FROM operations WHERE id=?",std::vector<Value>{id}); break;
        }
        case UFB_APPLY_OPERATION: {
            a.count(3);auto id=a.id(0);auto now=a.number(1),limit=a.number(2,1,32);
            auto op=decode(db.query({"SELECT action,phase FROM operations WHERE id=?",{id}})).back().rows;
            require(op.size()==1,"Operation not found",UF_STATE);
            if(op[0][1].integer!=1)return db.query({"SELECT * FROM operations WHERE id=?",{id}},capacity);
            auto action=op[0][0].integer;
            auto selected=decode(db.query({"SELECT i.task_id,t.state,EXISTS(SELECT 1 FROM task_attempts a WHERE a.task_id=t.id AND (a.payload_released=0 OR a.credential_released=0)),f.state,i.outcome,t.desired_action FROM operation_items i LEFT JOIN tasks t ON t.id=i.task_id LEFT JOIN file_records f ON f.id=t.file_id WHERE i.operation_id=? AND i.outcome IN(0,5) ORDER BY i.outcome,i.task_id LIMIT ?",{id,limit}})).back().rows;
            int64_t applied=0;
            for(auto &row:selected) {
                auto task_id=row[0].text;auto state=row[1].integer;bool exists=row[1].type!=0,free=row[2].integer==0;
                bool waiting=row[4].integer==5;
                int64_t outcome=2;std::string reason="State or ownership changed before execution";
                if(!exists) {outcome=action==5?6:3;reason="Task history no longer exists";}
                else if(waiting) {
                    if(!free) {outcome=5;reason="Waiting for executor release";}
                    else if(action==1 && (state==4 || (state==0 && row[5].integer==1))) {outcome=1;reason.clear();}
                    else if(action==2 && state==8) {outcome=1;reason.clear();}
                    else if(state==3) {outcome=6;reason="Backup already confirmed";}
                    else {outcome=2;reason="Executor stopped; inspect task result before further action";}
                } else if((action==0 && state==0) || (action==1 && state==4 && free) || (action==2 && (state==3 || state==8) && free)) {
                    outcome=6;reason=state==3?"Backup already confirmed":"Requested state already satisfied";
                } else {
                    bool eligible=action==0?(state==4 && free):action==1?(state==0 || state==1 || state==2 || state==4 || state==5):action==2?(state==0 || state==1 || state==2 || state==4 || state==5 || state==7 || state==8):action==3?((state==5 || state==6 || state==7) && free):action==4?row[3].integer==4:((state==3 || state==8) && free && row[3].integer==3);
                    if(eligible) {
                        if(action==4)commands.emplace_back("UPDATE file_records SET state=2,cleanup_error=NULL,updated_utc=? WHERE id=(SELECT file_id FROM tasks WHERE id=?) AND state=4",std::vector<Value>{now,task_id},1);
                        else if(action==5) {
                            commands.emplace_back("DELETE FROM task_attempts WHERE task_id=?",std::vector<Value>{task_id});
                            commands.emplace_back("DELETE FROM tasks WHERE id=?",std::vector<Value>{task_id},1);
                            commands.emplace_back("DELETE FROM file_records WHERE id=? AND state=3",std::vector<Value>{task_id},1);
                        } else append_action(commands,task_id,action,now,true);
                        outcome=(action==1 || action==2) && !free?5:1;reason=outcome==5?"Waiting for executor release":"";
                    }
                }
                if(outcome==1 || outcome==6)++applied;
                commands.emplace_back("UPDATE operation_items SET outcome=?,error=? WHERE operation_id=? AND task_id=? AND outcome IN(0,5)",std::vector<Value>{outcome,reason.empty()?Value():Value(reason),id,task_id},1);
            }
            commands.emplace_back("UPDATE operations SET applied_count=applied_count+?,phase=CASE WHEN EXISTS(SELECT 1 FROM operation_items WHERE operation_id=? AND outcome IN(0,5)) THEN 1 ELSE 2 END,updated_utc=? WHERE id=? AND phase=1",std::vector<Value>{applied,id,now,id},1);
            commands.emplace_back("SELECT * FROM operations WHERE id=?",std::vector<Value>{id});break;
        }
        case UFB_OPERATION_STATUS: a.count(1); read=true; commands.emplace_back("SELECT * FROM operations WHERE id=?",std::vector<Value>{a.id(0)}); break;
        case UFB_OPERATION_ITEMS: a.count(3); read=true; commands.emplace_back("SELECT task_id,outcome,error FROM operation_items WHERE operation_id=? AND task_id>? ORDER BY task_id LIMIT ?",std::vector<Value>{a.id(0),a.text(1,64),a.number(2,1,200)}); break;
        case UFB_HISTORY_PAGE: {
            a.count(4); auto after=a.number(0),before=a.number(1),keep=a.number(2,0,1000000),limit=a.number(3,1,200); read=true;
            commands.emplace_back("SELECT sequence,id,updated_utc FROM tasks WHERE sequence>? AND state IN(3,8) AND (updated_utc<? OR sequence<=coalesce((SELECT sequence FROM tasks WHERE state IN(3,8) ORDER BY sequence DESC LIMIT 1 OFFSET ?),0)) AND NOT "+Unreleased+" AND EXISTS(SELECT 1 FROM file_records f WHERE f.id=file_id AND f.state=3) ORDER BY sequence LIMIT ?",std::vector<Value>{after,before,keep,limit}); break;
        }
        case UFB_PRUNE_TASK: {
            a.count(2); auto id=a.id(0); auto version=a.number(1);
            // Selection is advisory. Every dependency is checked again in this
            // transaction; independent receipts and operation results survive.
            commands.emplace_back("DELETE FROM task_attempts WHERE task_id=? AND payload_released=1 AND credential_released=1 AND EXISTS(SELECT 1 FROM tasks t JOIN file_records f ON f.id=t.file_id WHERE t.id=task_id AND t.updated_utc=? AND t.state IN(3,8) AND f.state=3) ",std::vector<Value>{id,version});
            commands.emplace_back("DELETE FROM tasks WHERE id=? AND updated_utc=? AND state IN(3,8) AND NOT EXISTS(SELECT 1 FROM task_attempts WHERE task_id=tasks.id) AND EXISTS(SELECT 1 FROM file_records f WHERE f.id=file_id AND f.state=3)  RETURNING id",std::vector<Value>{id,version});
            commands.emplace_back("DELETE FROM file_records WHERE id=? AND state=3 AND NOT EXISTS(SELECT 1 FROM tasks WHERE file_id=file_records.id)",std::vector<Value>{id}); break;
        }
        case UFB_PRUNE_OPERATION: {
            a.count(3); auto id=a.id(0); auto before=a.number(1),limit=a.number(2,1,200);
            commands.emplace_back("DELETE FROM operation_items WHERE (operation_id,task_id) IN(SELECT i.operation_id,i.task_id FROM operation_items i JOIN operations o ON o.id=i.operation_id WHERE o.id=? AND o.phase IN(2,3) AND o.updated_utc<? ORDER BY i.task_id LIMIT ?)",std::vector<Value>{id,before,limit});
            commands.emplace_back("SELECT changes() AS removed");
            commands.emplace_back("DELETE FROM operation_targets WHERE operation_id=? AND EXISTS(SELECT 1 FROM operations WHERE id=? AND phase IN(2,3) AND updated_utc<?)",std::vector<Value>{id,id,before});
            commands.emplace_back("UPDATE operations SET details_expired=1,criteria=NULL WHERE id=? AND phase IN(2,3) AND updated_utc<? AND NOT EXISTS(SELECT 1 FROM operation_items WHERE operation_id=operations.id)",std::vector<Value>{id,before}); break;
        }
        case UFB_FILE_SUMMARY: a.count(0); read=true; commands.emplace_back("SELECT state,count,bytes FROM file_counts ORDER BY state"); break;
        case UFB_RECOVER_PREPARATIONS: {
            // Explicit application recovery supplies the new preparation owner. It
            // is legal only after the former facade has relinquished ownership.
            a.count(3); auto owner=a.id(0); auto now=a.number(1),limit=a.number(2,1,32);
            auto batches=decode(db.query({"SELECT id,owner FROM preparations WHERE phase=0 AND owner<>? ORDER BY created_utc,id LIMIT ?",{owner,limit}})).back().rows;
            if(batches.empty()) return db.query({"SELECT 0 AS recovered"},capacity);
            for(auto &b:batches) {
                commands.emplace_back("UPDATE preparations SET phase=2,error='Preparation interrupted before durable acceptance' WHERE id=? AND phase=0",std::vector<Value>{b[0]},1);
                commands.emplace_back("DELETE FROM tasks WHERE batch_id=? AND state=9",std::vector<Value>{b[0]});
                commands.emplace_back("UPDATE file_records SET state=2,updated_utc=? WHERE preparation_id=? AND state IN(0,1)",std::vector<Value>{now,b[0]});
            }
            commands.emplace_back("SELECT ? AS recovered",std::vector<Value>{int64_t(batches.size())}); break;
        }
        case UFB_RECOVER_ATTEMPT: {
            // The executor has positively observed that no system/managed task
            // owns these resources. Preserve unknown server outcome for lookup.
            a.count(3); auto id=a.id(0); auto generation=a.number(1,1),now=a.number(2);
            commands.emplace_back("UPDATE task_attempts SET execution_state=CASE WHEN execution_state IN(0,1) THEN 4 ELSE execution_state END,payload_released=1,credential_released=1,credential_reference=NULL WHERE task_id=? AND generation=?",std::vector<Value>{id,generation},1);
            commands.emplace_back("UPDATE tasks SET state=6,error='Executor ended; server result requires reconciliation',updated_utc=? WHERE id=? AND current_generation=? AND state IN(1,2)",std::vector<Value>{now,id,generation}); break;
        }
        case UFB_SCHEDULE_RETRY: {
            a.count(4); auto id=a.id(0); auto limit=a.number(1,0,5),now=a.number(2),server_delay=a.number(3,0,864000000000LL);
            auto rows=decode(db.query({"SELECT retries FROM task_metadata WHERE task_id=?",{id}})).back().rows;
            require(rows.size()==1,"Task not found",UF_STATE); auto retries=rows[0][0].integer;
            if(retries>=limit) return db.query(task(id),capacity);
            constexpr int64_t delays[]={50000000,300000000,1200000000,6000000000,18000000000};
            auto delay=std::max(delays[retries],server_delay); require(now<=INT64_MAX-delay,"Retry time exceeds supported range");
            commands.emplace_back("UPDATE tasks SET state=5,next_attempt_utc=?,updated_utc=? WHERE id=? AND state IN(6,7) AND desired_action=0 AND NOT "+Unreleased,std::vector<Value>{now+delay,now,id},1);
            commands.emplace_back("UPDATE task_metadata SET retries=retries+1 WHERE task_id=?",std::vector<Value>{id},1); commands.push_back(task(id)); break;
        }
        case UFB_ATTEMPT: {
            a.count(2); auto sql=TaskColumns; auto at=sql.find("a.generation=t.current_generation");
            sql.replace(at,std::strlen("a.generation=t.current_generation"),"a.generation=?"); read=true;
            commands.emplace_back(sql+"WHERE t.id=? AND a.generation IS NOT NULL",std::vector<Value>{a.number(1,1),a.id(0)}); break;
        }
        case UFB_READY: a.count(4); read=true; commands.emplace_back(TaskColumns+"WHERE t.sequence>? AND t.sequence<=? AND t.state IN(0,5) AND t.next_attempt_utc<=? AND t.desired_action=0 ORDER BY t.sequence LIMIT ?",std::vector<Value>{a.number(0),a.number(1),a.number(2),a.number(3,1,200)}); break;
        case UFB_RESTART: {
            // The executor must positively observe its previous execution ended.
            // A recorded failed/unknown terminal attempt cannot be restarted.
            a.count(2); auto id=a.id(0); auto generation=a.number(1,1);
            commands.emplace_back("UPDATE task_attempts SET execution_state=0,submission_state=0,system_task_id=NULL WHERE task_id=? AND generation=? AND execution_state=1 AND payload_released=0 AND server_outcome=0 AND EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.current_generation=generation AND t.state IN(1,2))",std::vector<Value>{id,generation},1);
            commands.emplace_back("UPDATE tasks SET state=1 WHERE id=? AND current_generation=? AND state IN(1,2)",std::vector<Value>{id,generation},1); break;
        }
        case UFB_PREPARATION: {
            a.count(1); auto id=a.id(0);
            commands.emplace_back("SELECT id,phase,expected_count,created_utc,error FROM preparations WHERE id=?",std::vector<Value>{id});
            commands.emplace_back("SELECT id FROM preparation_items WHERE preparation_id=? ORDER BY ordinal LIMIT 32",std::vector<Value>{id});break;
        }
        case UFB_RESERVE_PAYLOAD: {
            a.count(5); auto id=a.id(0),owner=a.id(1);auto reserved=a.number(2),budget=a.number(3,1),now=a.number(4);
            commands.emplace_back("UPDATE file_records SET byte_count=?,updated_utc=? WHERE id=? AND state=0 AND EXISTS(SELECT 1 FROM preparations p WHERE p.id=preparation_id AND p.phase=0 AND p.owner=?) AND ?<=?-(SELECT coalesce(sum(bytes),0) FROM file_counts WHERE state<>3)+byte_count",std::vector<Value>{reserved,now,id,owner,reserved,budget},1);break;
        }
        case UFB_OPERATIONS: {
            a.count(3);auto phase=a.number(1,-1,3);read=true;
            commands.emplace_back("SELECT * FROM operations WHERE id>? "+std::string(phase<0?"":"AND phase=? ")+"ORDER BY id LIMIT ?",phase<0?std::vector<Value>{a.text(0,64),a.number(2,1,200)}:std::vector<Value>{a.text(0,64),phase,a.number(2,1,200)});break;
        }
        case UFB_OPERATION_CLEANUP_PAGE: {
            a.count(2);read=true;
            commands.emplace_back("SELECT id FROM operations WHERE phase IN(2,3) AND details_expired=0 AND updated_utc<? ORDER BY updated_utc,id LIMIT ?",std::vector<Value>{a.number(0),a.number(1,1,200)});break;
        }
        case UFB_PRUNE_CHANGES: {
            a.count(2);auto through=a.number(0),limit=a.number(1,1,200);
            commands.emplace_back("UPDATE store_settings SET retained_after_seq=max(retained_after_seq,coalesce((SELECT max(sequence) FROM (SELECT sequence FROM change_log WHERE sequence<=? ORDER BY sequence LIMIT ?)),retained_after_seq)) WHERE singleton=1",std::vector<Value>{through,limit});
            commands.emplace_back("DELETE FROM change_log WHERE sequence IN(SELECT sequence FROM change_log WHERE sequence<=(SELECT retained_after_seq FROM store_settings WHERE singleton=1) ORDER BY sequence LIMIT ?)",std::vector<Value>{limit});break;
        }
        case UFB_PRUNE_SCANS: {
            a.count(1);auto remaining=a.number(0,1,200),removed=int64_t(0);
            const char *pages[]={
                "DELETE FROM scan_items WHERE (run_id,source_id,content_version) IN(SELECT i.run_id,i.source_id,i.content_version FROM scan_items i JOIN scan_runs r ON r.id=i.run_id WHERE r.id NOT IN(SELECT active_baseline_id FROM scopes WHERE active_baseline_id IS NOT NULL UNION ALL SELECT pending_scan_id FROM scopes WHERE pending_scan_id IS NOT NULL) ORDER BY i.run_id,i.source_id,i.content_version LIMIT ?)",
                "DELETE FROM scan_runs WHERE id IN(SELECT r.id FROM scan_runs r WHERE NOT EXISTS(SELECT 1 FROM scan_items WHERE run_id=r.id) AND r.id NOT IN(SELECT active_baseline_id FROM scopes WHERE active_baseline_id IS NOT NULL UNION ALL SELECT pending_scan_id FROM scopes WHERE pending_scan_id IS NOT NULL) LIMIT ?)",
                "DELETE FROM file_records WHERE id IN(SELECT id FROM file_records WHERE state=3 AND NOT EXISTS(SELECT 1 FROM tasks WHERE file_id=file_records.id) LIMIT ?)"};
            for(auto sql:pages) {
                if(!remaining)break;
                auto result=decode(db.batch({{sql,{remaining}},{"SELECT changes() AS removed"}})).back();
                auto count=result.rows[0][0].integer;remaining-=count;removed+=count;
            }
            return db.query({"SELECT ? AS removed",{removed}},capacity);
        }
        case UFB_SUSPEND_SCOPE: {
            a.count(3);auto scope=a.id(0);auto after=a.number(1),now=a.number(2);
            auto selected=decode(db.query({"SELECT sequence,id FROM tasks WHERE sequence>? AND desired_action=0 AND state IN(0,1,2,5) AND EXISTS(SELECT 1 FROM discoveries d WHERE d.scope_id=? AND d.task_id=tasks.id) ORDER BY sequence LIMIT 32",{after,scope}})).back().rows;
            commands.emplace_back("UPDATE scopes SET enabled=0 WHERE id=?",std::vector<Value>{scope});
            for(auto &row:selected)append_action(commands,row[1].text,1,now,true);
            commands.emplace_back("SELECT ? AS count,? AS cursor",std::vector<Value>{int64_t(selected.size()),selected.empty()?after:selected.back()[0].integer});break;
        }
        case UFB_RESET_SCOPE: {
            a.count(1);commands.emplace_back("UPDATE scopes SET enabled=0,initialized=0,resetting=1,active_baseline_id=NULL,pending_scan_id=NULL,consumed_seq=0 WHERE id=?",std::vector<Value>{a.id(0)});break;
        }
        case UFB_RESET_SCOPE_PAGE: {
            a.count(1);auto scope=a.id(0);
            commands.emplace_back("DELETE FROM discoveries WHERE (scope_id,source_id,content_version) IN(SELECT scope_id,source_id,content_version FROM discoveries WHERE scope_id=? AND EXISTS(SELECT 1 FROM scopes WHERE id=? AND resetting=1) LIMIT 200)",std::vector<Value>{scope,scope});
            commands.emplace_back("UPDATE scopes SET resetting=0 WHERE id=? AND NOT EXISTS(SELECT 1 FROM discoveries WHERE scope_id=?)",std::vector<Value>{scope,scope});
            commands.emplace_back("SELECT coalesce((SELECT resetting FROM scopes WHERE id=?),0) AS pending",std::vector<Value>{scope});break;
        }
        case UFB_STORAGE: a.count(0); return db.run(UF_STORAGE,{},200,capacity);
        case UFB_CHECKPOINT: { a.count(0); Bytes mode; number(mode,0,4); return db.run(UF_CHECKPOINT,mode,200,capacity); }
        default: throw Error(UF_ARGUMENT,"Unknown backup repository command");
        }
        try { return read?db.query(commands.front(),capacity):db.batch(commands,capacity); }
        catch(const Error &error) {
            if((command==UFB_SELECT_OPERATION || command==UFB_APPLY_OPERATION) && error.committed==0) {
                try { db.batch({{"UPDATE operations SET phase=3,error=? WHERE id=? AND phase IN(0,1)",{std::string(error.what()),a.id(0)}}}); }
                catch(const std::exception &cleanup) { std::fprintf(stderr,"Backup operation failure recording failed: %s\n",cleanup.what()); }
            }
            throw;
        }
    }
    static void append_action(std::vector<Command> &commands,const std::string &id,int64_t action,int64_t now,bool strict) {
        // 0 resume single pause; 1 pause; 2 cancel; 3 explicit retry. Global pause
        // stays independent. Unknown outcomes must be reconciled before cancel.
        if(action==0 || action==3) {
            commands.emplace_back(std::string("UPDATE tasks SET state=0,desired_action=0,error=NULL,next_attempt_utc=0,updated_utc=? WHERE id=? AND ")+(action==0?"state=4":"state IN(5,6,7)")+" AND NOT "+Unreleased,std::vector<Value>{now,id},strict?1:-1);
            if(action==3) commands.emplace_back("UPDATE task_metadata SET retries=0 WHERE task_id=?",std::vector<Value>{id});
        } else if(action==1) {
            commands.emplace_back("UPDATE tasks SET desired_action=1,state=CASE WHEN state IN(0,5) THEN 4 ELSE state END,updated_utc=? WHERE id=? AND state IN(0,1,2,4,5)",std::vector<Value>{now,id},strict?1:-1);
        } else {
            commands.emplace_back("UPDATE tasks SET desired_action=2,state=CASE WHEN state IN(0,4,5,7) AND NOT "+Unreleased+" THEN 8 ELSE state END,updated_utc=? WHERE id=? AND state IN(0,1,2,4,5,7,8)",std::vector<Value>{now,id},strict?1:-1);
            commands.emplace_back("UPDATE file_records SET state=2,updated_utc=? WHERE id=(SELECT file_id FROM tasks WHERE id=? AND state=8) AND state=1",std::vector<Value>{now,id});
            commands.emplace_back("UPDATE discoveries SET disposition=5 WHERE task_id=? AND EXISTS(SELECT 1 FROM tasks WHERE id=? AND state=8)",std::vector<Value>{id,id});
        }
    }
    static void append_discovery(std::vector<Command> &commands,const std::string &scope,const std::string &source,const std::string &version,const std::string &name,const std::string &mime,const std::string &provider,int64_t bytes) {
        require(!source.empty() && !version.empty(),"Discovery requires source identity and content version");
        commands.emplace_back(
            "INSERT INTO discoveries(scope_id,source_id,content_version,disposition,task_id,name,mime,provider_id,byte_count) "
            "SELECT ?,?,?,CASE WHEN EXISTS(SELECT 1 FROM receipt_sources WHERE source_id=? AND content_version=?) THEN 3 "
            "WHEN EXISTS(SELECT 1 FROM tasks WHERE source_id=? AND content_version=? AND state<>9) THEN 2 ELSE 0 END,"
            "(SELECT id FROM tasks WHERE source_id=? AND content_version=? AND state<>9 ORDER BY sequence LIMIT 1),?,?,?,? "
            "WHERE NOT EXISTS(SELECT 1 FROM scan_items WHERE run_id IN(SELECT active_baseline_id FROM scopes WHERE id=? "
            "UNION ALL SELECT pending_scan_id FROM scopes WHERE id=? AND EXISTS(SELECT 1 FROM scan_runs WHERE id=pending_scan_id AND baseline=1)) "
            "AND source_id=? AND content_version=?) "
            "ON CONFLICT(scope_id,source_id,content_version) DO UPDATE SET name=excluded.name,mime=excluded.mime,"
            "provider_id=excluded.provider_id,byte_count=excluded.byte_count,"
            "disposition=CASE WHEN discoveries.disposition=6 THEN excluded.disposition ELSE discoveries.disposition END",
            std::vector<Value>{scope,source,version,source,version,source,version,source,version,name,mime,provider,bytes,scope,scope,source,version});
    }
};
struct Attachment { std::shared_ptr<Repository> repository; bool closed=false; };
std::map<uint64_t,std::shared_ptr<Attachment>> handles;
std::map<std::string,std::weak_ptr<Repository>> repositories;
std::shared_ptr<Attachment> get(uint64_t handle) {
    std::lock_guard<std::mutex> lock(registry_gate); auto i=handles.find(handle);
    require(i!=handles.end(),"Invalid backup repository handle",UF_HANDLE); return i->second;
}
void init(ufb_status *status) {
    require(status && status->size==sizeof(ufb_status) && status->abi==UFB_ABI,"Invalid backup status ABI");
    std::memset(status,0,sizeof(*status)); status->size=sizeof(*status); status->abi=UFB_ABI;
}
template<class F> int boundary(ufb_status *status,F action) noexcept {
    if(!status || status->size!=sizeof(ufb_status) || status->abi!=UFB_ABI) return UF_ARGUMENT;
    try { init(status); action(); return UF_OK; }
    catch(const Error &e) { status->error=e.code; status->sqlite_code=e.sql; status->committed=e.committed; status->phase=e.phase; std::strncpy(status->message,e.what(),255); }
    catch(const std::bad_alloc &) { status->error=UF_MEMORY; std::strcpy(status->message,"Backup repository allocation failed"); }
    catch(const std::exception &e) { status->error=UF_IO; std::strncpy(status->message,e.what(),255); }
    catch(...) { status->error=UF_STATE; std::strcpy(status->message,"Unknown native backup failure"); }
    return status->error;
}
}
extern "C" {
uint32_t ufbackup_abi(void) { return UFB_ABI; }
int ufbackup_open(const char *root,const char *store_id,const char *server,const char *account,int create,uint64_t *handle,ufb_status *status) {
    return boundary(status,[&] {
        require(root && server && account && handle && (create==0 || create==1),"Invalid repository open arguments");
        auto id=require_id(store_id); auto base=std::filesystem::u8path(root);
        require(base.is_absolute(),"Absolute repository root required");
        require(!create || (*server && *account),"New catalog requires server and account");
        std::lock_guard<std::mutex> opening(open_gate);
        auto folder=base/id;
        if(create) std::filesystem::create_directories(folder/"payloads");
        require(std::filesystem::is_directory(folder),"Existing repository directory not found",UF_IO);
        auto path=(std::filesystem::canonical(folder)/"catalog.sqlite").u8string();
        std::shared_ptr<Repository> repository;
        { std::lock_guard<std::mutex> lock(registry_gate); auto i=repositories.find(path); if(i!=repositories.end()) repository=i->second.lock(); require(handles.size()<64,"Backup attachment capacity reached",UF_CAPACITY); }
        if(repository) {
            std::lock_guard<std::mutex> lock(repository->gate);
            require(!create && !repository->closed && !repository->faulted,"Repository lifetime conflicts with open",UF_STATE);
            require(repository->id==id && (!*server || repository->server==server) && (!*account || repository->account==account),"Repository identity mismatch",UF_STATE);
            auto attachment=std::make_shared<Attachment>(); attachment->repository=repository;
            std::lock_guard<std::mutex> registry(registry_gate); auto h=next_handle++; handles.emplace(h,attachment); ++repository->attachments; *handle=h;
        } else {
            repository=std::make_shared<Repository>(path,id,server,account,create!=0);
            auto attachment=std::make_shared<Attachment>(); attachment->repository=repository;
            std::lock_guard<std::mutex> registry(registry_gate);
            auto h=next_handle++; handles.emplace(h,attachment); repositories[path]=repository; repository->attachments=1; *handle=h;
        }
    });
}
int ufbackup_close(uint64_t handle,ufb_status *status) {
    return boundary(status,[&] {
        auto attachment=get(handle); auto repository=attachment->repository;
        std::lock_guard<std::mutex> lock(repository->gate);
        require(!attachment->closed,"Backup attachment is already closed",UF_HANDLE);
        attachment->closed=true;
        if(repository->preparer_handle==handle) { repository->preparer_handle=0; repository->preparer.clear(); }
        { std::lock_guard<std::mutex> registry(registry_gate); handles.erase(handle); }
        if(--repository->attachments==0) {
            repository->closed=true;
            { std::lock_guard<std::mutex> registry(registry_gate); repositories.erase(repository->path); }
            repository->db.close();
        }
    });
}
int ufbackup_call(uint64_t handle,uint32_t command,const uint8_t *input,uint32_t length,uint8_t *output,uint32_t capacity,ufb_status *status) {
    return boundary(status,[&] {
        require(output && capacity>=20 && capacity<=MaxBytes,"Invalid result buffer");
        struct Admission { bool held=false; ~Admission(){if(held)--calls;} } admission;
        auto count=++calls; admission.held=true; require(count<=16,"Backup command capacity reached",UF_CAPACITY);
        Args arguments(input,length); auto attachment=get(handle); auto repository=attachment->repository;
        std::lock_guard<std::mutex> lock(repository->gate); require(!attachment->closed,"Backup attachment is closed",UF_HANDLE);
        if(command==UFB_BIND_PREPARER) {
            arguments.count(1); auto owner=arguments.id(0);
            require(!repository->preparer_handle || (repository->preparer_handle==handle && repository->preparer==owner),"Another facade owns file preparation",UF_STATE);
            repository->preparer=owner; repository->preparer_handle=handle; return;
        }
        if(command==UFB_PREPARE || command==UFB_SEAL || command==UFB_ACCEPT || command==UFB_ABANDON || command==UFB_RECOVER_PREPARATIONS || command==UFB_RESERVE_PAYLOAD) {
            require(repository->preparer_handle==handle,"Bind an exclusive preparation owner first",UF_STATE);
            auto owner=arguments.id(command==UFB_RECOVER_PREPARATIONS?0:1);
            require(repository->preparer==owner,"Preparation owner mismatch",UF_STATE);
        }
        try {
            auto result=repository->execute(command,arguments,capacity);
            if(!result.empty()) std::memcpy(output,result.data(),result.size());
            status->length=uint32_t(result.size());
        } catch(const Error &error) { if(error.committed<0 || error.code==UF_FAULTED) repository->faulted=true; throw; }
    });
}
}
