#include "ufbackup.h"
#include "repository_support.hpp"
#include <cctype>
#include <exception>
#include <fstream>
#include <map>
#include <set>

namespace ufbackup {
namespace {
constexpr int64_t Second=10000000LL, Day=86400*Second;
struct Row {
    std::map<std::string,Value> fields;
    Row(const Table &table,const std::vector<Value> &values) {
        for(size_t i=0;i<table.columns.size();++i)fields.emplace(table.columns[i],values[i]);
    }
    const Value &at(const std::string &key)const {auto it=fields.find(key);require(it!=fields.end(),"Missing repository column",UF_STATE);return it->second;}
    std::string text(const std::string &key)const{return at(key).text;}
    int64_t number(const std::string &key)const{return at(key).integer;}
};
std::vector<Row> select(Client &db,const std::string &sql,std::vector<Value> args={}) {
    auto tables=decode(db.query({sql,std::move(args)}));std::vector<Row> rows;
    for(auto &value:tables.back().rows)rows.emplace_back(tables.back(),value);
    return rows;
}
Row one(Client &db,const std::string &sql,std::vector<Value> args={}) {
    auto rows=select(db,sql,std::move(args));require(rows.size()==1,"Expected one persisted protocol fact",UF_STATE);return rows[0];
}
int64_t utc(int64_t milliseconds) {
    require(milliseconds>=0 && milliseconds<=(INT64_MAX-UnixEpochTicks)/10000,"Unsupported server timestamp");
    return UnixEpochTicks+milliseconds*10000;
}
struct Json {
    struct Field {std::string type;Value value;};
    Client &db;
    std::map<std::string,Field> fields;
    Json(Client &client,const std::string &body):db(client) {
        require(one(db,"SELECT json_valid(?) AS valid",{body}).number("valid")==1,"Invalid protocol JSON");
        require(one(db,"SELECT json_type(?)='object' AS valid",{body}).number("valid")==1,"Expected protocol object");
        auto rows=select(db,"SELECT key,type,value FROM json_each(?) LIMIT 40",{body});
        require(rows.size()<40,"Too many protocol fields");
        for(auto &row:rows)require(fields.emplace(row.text("key"),Field{row.text("type"),row.at("value")}).second,"Duplicate JSON property");
    }
    const Field &field(const std::string &key,const char *type)const {
        auto found=fields.find(key);require(found!=fields.end() && found->second.type==type,"Missing or mistyped protocol field");return found->second;
    }
    std::string text(const std::string &key,size_t maximum=4096,bool empty=false)const {
        auto value=field(key,"text").value.text;
        require((empty || !value.empty()) && value.size()<=maximum && value.find('\0')==std::string::npos,"Invalid protocol string");return value;
    }
    int64_t number(const std::string &key,int64_t low=0,int64_t high=INT64_MAX)const {
        auto &value=field(key,"integer").value;require(value.type==1 && value.integer>=low && value.integer<=high,"Invalid protocol integer");return value.integer;
    }
    std::string object(const std::string &key)const{return field(key,"object").value.text;}
    std::vector<Row> array(const std::string &key,size_t maximum)const {
        auto value=field(key,"array").value.text;
        require(one(db,"SELECT json_array_length(?) AS n",{value}).number("n")<=int64_t(maximum),"Protocol array exceeds limit");
        return select(db,"SELECT type,value FROM json_each(?)",{value});
    }
    void exact(std::initializer_list<const char*> keys)const {
        require(fields.size()==keys.size(),"Unexpected protocol property");
        for(auto key:keys)require(fields.count(key),"Missing protocol property");
    }
};
bool identifier(const std::string &text) {
    return !text.empty() && text.size()<=128 && std::all_of(text.begin(),text.end(),[](unsigned char c){return std::isalnum(c)||c=='.'||c=='_'||c==':'||c=='-';});
}
void validate_url(const std::string &url,bool development) {
    require(url.size()<=8192 && url.find_first_of("\r\n\t #")==std::string::npos,"Invalid upload URL");
    bool https=url.rfind("https://",0)==0,http=url.rfind("http://",0)==0;
    require(https || (http && development),"Upload requires HTTPS");
    size_t begin=https?8u:7u;auto end=url.find_first_of("/?",begin);auto host=url.substr(begin,end-begin);
    require(!host.empty() && host.find('@')==std::string::npos,"Upload URL contains user information or has no host");
    if(http) {
        auto colon=host.find(':');auto plain=host.substr(0,colon);
        bool local=plain=="localhost" || host=="[::1]" || host.rfind("[::1]:",0)==0;
        unsigned octets[4]={};size_t pos=0;bool ipv4=true;
        for(int i=0;i<4;++i) {
            size_t first=pos;
            while(pos<plain.size() && plain[pos]>='0' && plain[pos]<='9') {
                octets[i]=octets[i]*10+unsigned(plain[pos++]-'0');
                if(pos-first>3 || octets[i]>255){ipv4=false;break;}
            }
            if(!ipv4 || pos==first || (pos-first>1 && plain[first]=='0')){ipv4=false;break;}
            if(i<3){if(pos==plain.size() || plain[pos++]!='.'){ipv4=false;break;}}
        }
        ipv4=ipv4 && pos==plain.size();
        local=local || (ipv4 && (octets[0]==127 || octets[0]==10 || (octets[0]==192 && octets[1]==168) || (octets[0]==172 && octets[1]>=16 && octets[1]<=31)));
        require(local,"Development HTTP requires a local upload address");
    }
}
const std::string Work=
    "SELECT a.*,t.state AS task_state,t.desired_action,t.file_id,t.sequence,t.source_id,t.content_version,t.scope_id,t.scope_epoch,"
    "f.relative_path,f.byte_count,lower(hex(f.sha256)) AS sha256,m.name,m.mime "
    "FROM task_attempts a INDEXED BY attempts_protocol_work CROSS JOIN tasks t ON t.id=a.task_id AND t.current_generation=a.generation "
    "CROSS JOIN file_records f ON f.id=t.file_id CROSS JOIN task_metadata m ON m.task_id=t.id ";
const std::string Control=
    "SELECT c.*,CASE WHEN c.kind=1 THEN (SELECT coalesce(min(nullif(a.confirm_deadline_utc,0)),0) FROM control_items i JOIN task_attempts a ON a.task_id=i.task_id AND a.generation=i.generation WHERE i.request_id=c.id) ELSE 0 END AS deadline_utc,"
    "(SELECT max(a.wifi_only) FROM control_items i JOIN task_attempts a ON a.task_id=i.task_id AND a.generation=i.generation WHERE i.request_id=c.id) AS wifi_only,"
    "(SELECT a.credential_reference FROM control_items i JOIN task_attempts a ON a.task_id=i.task_id AND a.generation=i.generation WHERE i.request_id=c.id ORDER BY i.task_id LIMIT 1) AS credential_reference "
    "FROM control_requests c ";
struct Result {
    Row task;
    std::string status,descriptor,backup,error;
    int64_t next=0,expires=0,confirmed=0;
    bool applied=false;
};
std::vector<Result> validate_response(Client &db,const std::string &request,const std::string &body,const std::string &account) {
    require(body.size()<=512*1024,"Control response exceeds 512 KiB");
    Json envelope(db,body);envelope.exact({"protocolVersion","requestId","account","serverTime","limits","items"});
    require(envelope.number("protocolVersion")==2 && envelope.text("requestId",128)==request && envelope.text("account",1024)==account,"Control response identity mismatch");
    auto server_time=utc(envelope.number("serverTime"));
    Json limits(db,envelope.object("limits"));limits.exact({"maxItems","maxRequestBytes","maxResponseBytes","maxDescriptorBytes","maxFileBytes"});
    require(limits.number("maxItems")==32 && limits.number("maxRequestBytes")==262144 && limits.number("maxResponseBytes")==524288 && limits.number("maxDescriptorBytes")==8192 && limits.number("maxFileBytes")==536870912,"Unsupported protocol limits");
    auto control=one(db,"SELECT kind,state FROM control_requests WHERE id=?",{request});
    require(control.number("state")>=2 && control.number("state")<=4,"Control response has no submitted request",UF_STATE);
    auto settings=one(db,"SELECT source_namespace,development_http FROM store_settings");
    auto expected=select(db,"SELECT i.task_id,i.generation,i.applied,t.current_generation,t.source_id,t.content_version,f.sha256,f.byte_count,a.confirm_deadline_utc,a.confirm_checks "
        "FROM control_items i JOIN tasks t ON t.id=i.task_id JOIN file_records f ON f.id=t.file_id JOIN task_attempts a ON a.task_id=i.task_id AND a.generation=i.generation WHERE i.request_id=? ORDER BY i.task_id",{request});
    auto incoming=envelope.array("items",32);require(!expected.empty() && incoming.size()==expected.size(),"Control response membership mismatch");
    std::set<std::pair<std::string,int64_t>> seen;std::vector<Result> results;
    for(auto &entry:incoming) {
        require(entry.text("type")=="object","Expected control result object");Json item(db,entry.text("value"));
        auto id=item.text("clientTaskId",128);auto generation=item.number("attemptGeneration",1,2147483647);
        require(seen.emplace(id,generation).second,"Duplicate result identity");
        auto found=std::find_if(expected.begin(),expected.end(),[&](const Row &row){return row.text("task_id")==id && row.number("generation")==generation;});
        require(found!=expected.end(),"Unexpected control result identity");
        require(found->number("current_generation")==generation,"Obsolete control result generation",UF_STATE);
        Result result{*found,item.text("status",32),"","",""};result.applied=found->number("applied")!=0;
        auto validate_identity=[&](const Json &value) {
            require(value.text("clientTaskId",128)==id && value.number("attemptGeneration",1,2147483647)==generation,"Nested result identity mismatch");
            auto digest=value.text("sha256",64);auto expected_digest=one(db,"SELECT lower(hex(?)) AS sha",{found->at("sha256")}).text("sha");
            require(digest==expected_digest && value.number("byteCount")==found->number("byte_count"),"Result content identity mismatch");
        };
        if(result.status=="Confirmed") {
            item.exact({"clientTaskId","attemptGeneration","status","receipt"});Json receipt(db,item.object("receipt"));
            receipt.exact({"clientTaskId","attemptGeneration","backupId","sourceNamespace","sourceId","sourceVersion","sha256","byteCount","confirmedAt"});validate_identity(receipt);
            require(receipt.text("sourceNamespace",1024)==settings.text("source_namespace") && receipt.text("sourceId",1024)==found->text("source_id") && receipt.text("sourceVersion",1024)==found->text("content_version"),"Receipt source version mismatch");
            result.backup=receipt.text("backupId",128);require(identifier(result.backup),"Invalid backup identity");result.confirmed=utc(receipt.number("confirmedAt"));
            require(result.confirmed<=server_time,"Receipt is later than server time");
        } else if(result.status=="UploadRequired") {
            require(control.number("kind")!=2,"Cancel cannot authorize upload");item.exact({"clientTaskId","attemptGeneration","status","upload"});result.descriptor=item.object("upload");
            require(result.descriptor.size()<=8192,"Upload descriptor exceeds 8 KiB");Json upload(db,result.descriptor);
            upload.exact({"clientTaskId","attemptGeneration","uploadId","sha256","byteCount","url","headers","expiresAt","successStatusCodes"});validate_identity(upload);
            require(identifier(upload.text("uploadId",128)),"Invalid upload identity");validate_url(upload.text("url",8192),settings.number("development_http")!=0);
            result.expires=utc(upload.number("expiresAt"));
            auto headers=upload.array("headers",32);std::set<std::string> names;
            for(auto &entry:headers) {
                require(entry.text("type")=="object","Invalid upload header");Json header(db,entry.text("value"));header.exact({"name","value"});
                auto name=header.text("name",128),value=header.text("value",8192,true);
                require(std::all_of(name.begin(),name.end(),[](unsigned char c){return std::isalnum(c) || std::string("!#$%&'*+-.^_`|~").find(char(c))!=std::string::npos;}),"Invalid upload header name");
                require(std::all_of(value.begin(),value.end(),[](unsigned char c){return c>=32 && c!=127;}),"Invalid upload header value");
                std::transform(name.begin(),name.end(),name.begin(),[](unsigned char c){return char(std::tolower(c));});
                require(names.insert(name).second && name!="host" && name!="content-length" && name!="transfer-encoding" && name!="connection" && name!="proxy-authorization" && name!="expect","Forbidden or repeated upload header");
            }
            auto codes=upload.array("successStatusCodes",3);std::set<int64_t> unique;
            require(!codes.empty(),"Missing upload success codes");
            for(auto &code:codes)require(code.text("type")=="integer" && (code.number("value")==200 || code.number("value")==201 || code.number("value")==204) && unique.insert(code.number("value")).second,"Invalid upload success code");
        } else if(result.status=="Pending" || result.status=="Verifying") {
            require(control.number("kind")!=2,"Cancel requires an authoritative result");item.exact({"clientTaskId","attemptGeneration","status","nextCheckAt"});
            result.next=utc(item.number("nextCheckAt"));require(result.next>server_time,"Pending requires a future nextCheckAt");
        } else if(result.status=="Rejected") {
            item.exact({"clientTaskId","attemptGeneration","status","error"});Json error(db,item.object("error"));error.exact({"code","message"});
            result.error=error.text("code",128)+": "+error.text("message",3966);
        } else if(result.status=="Canceled" || result.status=="Absent") {
            item.exact({"clientTaskId","attemptGeneration","status"});
            require(result.status!="Absent" || control.number("kind")==1,"Only Query may return Absent");
        } else require(false,"Unknown backup result status");
        results.push_back(std::move(result));
    }
    return results;
}
void finish_error(std::vector<Command> &commands,const std::string &id,int64_t generation,const std::string &error,int64_t now,bool known=false) {
    commands.emplace_back("UPDATE task_attempts SET execution_state=4,protocol_phase=3,server_outcome=?,error=? WHERE task_id=? AND generation=? AND server_outcome<>1",std::vector<Value>{int64_t(known?2:0),error,id,generation});
    commands.emplace_back("UPDATE tasks SET state=?,error=?,updated_utc=? WHERE id=? AND current_generation=? AND state<>3",std::vector<Value>{int64_t(known?7:6),error,now,id,generation});
}
}

Bytes protocol_command(Client &db,const std::string &path,const std::string &store,const std::string &account,unsigned command,const Args &a,unsigned capacity) {
    (void)store;std::vector<Command> commands;auto directory=std::filesystem::u8path(path).parent_path();
    switch(command) {
    case UFB_CONFIRM_SCOPE:
        a.count(1);commands.emplace_back("UPDATE scopes SET requires_confirmation=0 WHERE id=?",std::vector<Value>{a.id(0)},1);break;
    case UFB_CONTROL:
        a.count(1);return db.query({Control+"WHERE c.id=?",{a.id(0)}},capacity);
    case UFB_PROTOCOL_WAKE:
        a.count(1);return db.query({"SELECT coalesce(min(ready),0) AS next_utc FROM (SELECT CASE WHEN a.protocol_phase=0 THEN t.updated_utc+2500000 ELSE min(a.next_check_utc,a.confirm_deadline_utc) END AS ready FROM task_attempts a INDEXED BY attempts_protocol_work CROSS JOIN tasks t ON t.id=a.task_id AND t.current_generation=a.generation WHERE a.credential_released=0 AND a.control_id IS NULL AND a.executor=? AND a.protocol_phase IN(0,2) AND t.desired_action=0 AND t.state IN(1,2) UNION ALL SELECT a.confirm_deadline_utc FROM control_requests c INDEXED BY controls_active CROSS JOIN control_items i ON i.request_id=c.id CROSS JOIN task_attempts a ON a.task_id=i.task_id AND a.generation=i.generation WHERE c.released=0 AND c.executor=? AND c.kind=1 AND c.state<=3 AND a.confirm_deadline_utc>0)",{a.number(0,0,2),a.number(0,0,2)}},capacity);
    case UFB_PROTOCOL_CONFIGURE:
        a.count(2);commands.emplace_back("UPDATE store_settings SET development_http=?,transfer_mode=? WHERE singleton=1",std::vector<Value>{a.number(0,0,1),a.number(1,0,1)},1);break;
    case UFB_ACCEPT_ITEM: {
        a.count(3);auto id=a.id(0),owner=a.id(1);auto now=a.number(2);
        commands.emplace_back("UPDATE tasks SET id=id WHERE id=? AND state=9 AND EXISTS(SELECT 1 FROM file_records f JOIN preparations p ON p.id=f.preparation_id WHERE f.id=file_id AND f.state=1 AND p.owner=? AND p.phase=0)",std::vector<Value>{id,owner},1);
        commands.emplace_back("UPDATE tasks SET state=0,preparation_accepted=1,updated_utc=? WHERE id=? AND state=9 AND EXISTS(SELECT 1 FROM file_records f JOIN preparations p ON p.id=f.preparation_id WHERE f.id=file_id AND f.state=1 AND p.phase=0 AND p.stop_requested=0 AND p.owner=?) AND desired_action=0 AND (SELECT paused FROM store_settings)=0 AND "+admission("tasks"),std::vector<Value>{now,id,owner});
        commands.emplace_back("UPDATE preparations SET phase=1 WHERE id=(SELECT batch_id FROM tasks WHERE id=?) AND NOT EXISTS(SELECT 1 FROM tasks WHERE batch_id=preparations.id AND state=9)",std::vector<Value>{id});
        commands.emplace_back("UPDATE discoveries SET disposition=2,task_id=? WHERE (source_id,content_version)=(SELECT source_id,content_version FROM tasks WHERE id=?) AND scope_id=(SELECT scope_id FROM tasks WHERE id=?) AND EXISTS(SELECT 1 FROM tasks WHERE id=? AND state=0) AND disposition IN(0,2)",std::vector<Value>{id,id,id,id});
        commands.emplace_back("SELECT state=0 AS accepted FROM tasks WHERE id=?",std::vector<Value>{id});break;
    }
    case UFB_FAIL_ITEM: {
        a.count(4);auto id=a.id(0),owner=a.id(1),error=a.text(2);auto now=a.number(3);
        commands.emplace_back("UPDATE tasks SET state=7,error=?,updated_utc=? WHERE id=? AND state=9 AND EXISTS(SELECT 1 FROM preparations p WHERE p.id=batch_id AND p.phase=0 AND p.owner=?)",std::vector<Value>{error,now,id,owner},1);
        commands.emplace_back("UPDATE file_records SET state=2,updated_utc=? WHERE id=(SELECT file_id FROM tasks WHERE id=?)",std::vector<Value>{now,id},1);
        // A closed scope is acknowledged by explicit confirmation, not a
        // per-source retry. Retain the candidate without adopting the old task.
        commands.emplace_back("UPDATE discoveries SET disposition=0,task_id=NULL,error=NULL WHERE task_id=? AND disposition=2 AND EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.scope_id=discoveries.scope_id AND NOT "+admission("t")+")",std::vector<Value>{id});
        commands.emplace_back("UPDATE discoveries SET disposition=4,error=? WHERE task_id=? AND scope_id=(SELECT scope_id FROM tasks WHERE id=?) AND disposition=2",std::vector<Value>{error,id,id});
        commands.emplace_back("UPDATE preparations SET phase=1 WHERE id=(SELECT batch_id FROM tasks WHERE id=?) AND NOT EXISTS(SELECT 1 FROM tasks WHERE batch_id=preparations.id AND state=9)",std::vector<Value>{id});break;
    }
    case UFB_SUBMISSION:
        a.count(1);return db.query({"SELECT i.ordinal,i.id,t.id IS NULL AS details_expired,t.state,t.error,t.current_generation,t.preparation_started,coalesce(a.submission_state,0) AS native_accepted,coalesce(a.system_scheduled,0) AS system_scheduled,coalesce(a.protocol_phase,0) AS protocol_phase FROM preparation_items i LEFT JOIN tasks t ON t.id=i.id LEFT JOIN task_attempts a ON a.task_id=t.id AND a.generation=t.current_generation WHERE i.preparation_id=? ORDER BY ordinal LIMIT 32",{a.id(0)}},capacity);
    case UFB_SYSTEM_SCHEDULED:
        a.count(1);commands.emplace_back("UPDATE task_attempts SET system_scheduled=1 WHERE executor=? AND submission_state>=1 AND credential_released=0 AND protocol_phase<3",std::vector<Value>{a.number(0,0,2)});break;
    case UFB_CONTROL_CREATE: {
        a.count(5);auto id=a.id(0);auto executor=a.number(1,0,2),now=a.number(2),future=a.number(3,0,1),flush=a.number(4,0,1);
        auto existing=select(db,Control+"WHERE c.id=?",{id});if(!existing.empty())return db.query({Control+"WHERE c.id=?",{id}},capacity);
        auto settings=one(db,"SELECT paused,last_control_kind,source_namespace FROM store_settings");
        // A future Query consumes a queued-system slot, not today's control slot.
        auto due=one(db,"SELECT count(*) AS n FROM control_requests INDEXED BY controls_active WHERE released=0 AND executor=? AND not_before_utc<=? AND state<=3",{executor,now}).number("n");
        if(due)return db.query({Control+"WHERE 0"},capacity);
        std::vector<Row> selected;int64_t kind=-1,not_before=now;
        std::vector<int> order{2};if(!settings.number("paused")){order.push_back(settings.number("last_control_kind")==1?0:1);order.push_back(settings.number("last_control_kind")==1?1:0);}
        for(auto candidate:order) {
            auto predicate=candidate==2?"t.desired_action=2 AND a.protocol_phase<3 AND a.execution_state<>1 AND a.upload_released=1":candidate==0?"t.desired_action=0 AND a.protocol_phase=0 AND t.state=1":"t.desired_action=0 AND a.protocol_phase=2 AND t.state=2 AND a.next_check_utc<=? AND a.confirm_deadline_utc>?";
            std::vector<Value> values{executor};if(candidate==1){values.emplace_back(now);values.emplace_back(now);}
            selected=select(db,Work+"WHERE a.credential_released=0 AND a.control_id IS NULL AND a.executor=? AND a.submission_state>=1 AND "+predicate+(candidate!=0?std::string():" AND "+admission("t"))+" ORDER BY a.next_check_utc,a.task_id LIMIT 32",values);
            if(!selected.empty()){kind=candidate;break;}
        }
        if(selected.empty() && future && !settings.number("paused")) {
            auto queued=one(db,"SELECT count(*) AS n FROM control_requests INDEXED BY controls_active WHERE released=0 AND executor=? AND not_before_utc>?",{executor,now}).number("n");
            if(!queued) {
                selected=select(db,Work+"WHERE a.credential_released=0 AND a.control_id IS NULL AND a.executor=? AND a.submission_state>=1 AND a.protocol_phase=2 AND t.state=2 AND t.desired_action=0 AND a.next_check_utc<a.confirm_deadline_utc AND a.confirm_deadline_utc>? ORDER BY a.next_check_utc,a.task_id LIMIT 32",{executor,now});
                if(!selected.empty()){kind=1;not_before=selected[0].number("next_check_utc");selected.erase(std::remove_if(selected.begin(),selected.end(),[&](const Row &row){return row.number("next_check_utc")>not_before;}),selected.end());}
            }
        }
        if(selected.empty())return db.query({Control+"WHERE 0"},capacity);
        // A sealed request has a fixed membership. Keep a single control owner
        // per request so revoking one scope cannot pause independent scopes.
        const auto scope=selected[0].text("scope_id");const auto epoch=selected[0].number("scope_epoch");
        selected.erase(std::remove_if(selected.begin(),selected.end(),[&](const Row &r){return r.text("scope_id")!=scope || r.number("scope_epoch")!=epoch;}),selected.end());
        if(!flush && kind==0 && selected.size()<32) {
            auto first=one(db,"SELECT min(t.updated_utc) AS earliest FROM tasks t JOIN task_attempts a ON a.task_id=t.id AND a.generation=t.current_generation WHERE a.credential_released=0 AND a.control_id IS NULL AND a.executor=? AND a.protocol_phase=0 AND t.state=1 AND t.desired_action=0",{executor}).number("earliest");
            if(first+Second/4>now)return db.query({Control+"WHERE 0"},capacity);
        }
        std::string items="[";size_t count=0;
        for(auto &row:selected) {
            std::string item;
            if(kind==0)item=one(db,"SELECT json_object('clientTaskId',?,'attemptGeneration',?,'sourceNamespace',?,'sourceId',?,'sourceVersion',?,'sha256',?,'byteCount',?,'name',?,'mimeType',?) AS body",{row.text("task_id"),row.number("generation"),settings.text("source_namespace"),row.text("source_id"),row.text("content_version"),row.text("sha256"),row.number("byte_count"),row.text("name"),row.text("mime")}).text("body");
            else item=one(db,"SELECT json_object('clientTaskId',?,'attemptGeneration',?) AS body",{row.text("task_id"),row.number("generation")}).text("body");
            require(item.size()+256<=256*1024,"One control item exceeds the request budget");
            if(items.size()+item.size()+256>256*1024)break;
            if(count++)items+=",";
            items+=item;
        }
        items+="]";auto body=one(db,"SELECT json_object('protocolVersion',2,'requestId',?,'items',json(?)) AS body",{id,items}).text("body");
        require(body.size()<=262144,"Control request exceeds 256 KiB");
        auto occupied=one(db,"SELECT coalesce(sum(byte_count),0) AS bytes FROM control_requests WHERE cleanup_state<>2").number("bytes");
        require(occupied+int64_t(body.size())<=8*1024*1024,"Control file budget exceeded",UF_CAPACITY);
        auto available=decode(db.run(UF_STORAGE)).back();Row storage(available,available.rows[0]);
        require(storage.number("available_bytes")>=int64_t(body.size())+16*1024*1024,"Insufficient control file disk space",UF_IO);
        commands.emplace_back("INSERT INTO control_requests(id,kind,executor,state,relative_path,body,byte_count,not_before_utc,created_utc) VALUES(?,?,?,0,?,?,?,?,?)",std::vector<Value>{id,kind,executor,"controls/"+id+".json",body,int64_t(body.size()),not_before,now},1);
        for(size_t i=0;i<count;++i) {
            auto &row=selected[i];commands.emplace_back("INSERT INTO control_items(request_id,task_id,generation) VALUES(?,?,?)",std::vector<Value>{id,row.text("task_id"),row.number("generation")},1);
            commands.emplace_back("UPDATE task_attempts SET control_id=? WHERE task_id=? AND generation=? AND control_id IS NULL",std::vector<Value>{id,row.text("task_id"),row.number("generation")},1);
        }
        commands.emplace_back("UPDATE store_settings SET last_control_kind=? WHERE singleton=1",std::vector<Value>{kind},1);
        commands.emplace_back(Control+"WHERE c.id=?",std::vector<Value>{id});break;
    }
    case UFB_CONTROL_SEAL: {
        a.count(1);auto id=a.id(0);auto row=one(db,"SELECT * FROM control_requests WHERE id=? AND state IN(0,1) AND released=0",{id});
        auto folder=directory/"controls";std::filesystem::create_directories(folder);auto file=folder/(id+".json");
        require(!std::filesystem::is_symlink(file),"Control file is a symbolic link",UF_IO);
        if(row.number("state")==0) {std::ofstream stream(file,std::ios::binary|std::ios::trunc);stream.exceptions(std::ios::badbit|std::ios::failbit);stream.write(row.text("body").data(),std::streamsize(row.text("body").size()));stream.close();durable_payload(file,uint64_t(row.number("byte_count")));}
        commands.emplace_back("UPDATE control_requests SET state=1 WHERE id=? AND state IN(0,1) AND released=0",std::vector<Value>{id},1);commands.emplace_back(Control+"WHERE c.id=?",std::vector<Value>{id});break;
    }
    case UFB_CONTROL_SUBMITTED: case UFB_CONTROL_START: {
        bool submit=command==UFB_CONTROL_SUBMITTED;a.count(submit?2:1);auto id=a.id(0);
        auto allowed="(kind=2 OR ((SELECT paused FROM store_settings)=0 AND NOT EXISTS(SELECT 1 FROM control_items i JOIN tasks t ON t.id=i.task_id WHERE i.request_id=control_requests.id AND (t.desired_action<>0 OR (control_requests.kind=0 AND NOT "+admission("t")+")))))";
        if(submit)commands.emplace_back("UPDATE control_requests SET state=2,system_task_id=? WHERE id=? AND released=0 AND (state=1 OR (state=2 AND system_task_id=?)) AND "+allowed,std::vector<Value>{a.text(1,256),id,a.text(1,256)});
        else commands.emplace_back("UPDATE control_requests SET state=3 WHERE id=? AND state=2 AND released=0 AND "+allowed,std::vector<Value>{id});
        commands.emplace_back("SELECT changes() AS admitted");break;
    }
    case UFB_CONTROL_VALIDATE: {
        a.count(2);auto request=a.id(0);auto results=validate_response(db,request,a.text(1,524288),account);
        std::string sql="SELECT '' AS task_id,0 AS generation,'' AS descriptor WHERE 0";std::vector<Value> values;
        for(auto &result:results)if(!result.descriptor.empty() && !result.applied) {sql+=" UNION ALL SELECT ?,?,?";values.emplace_back(result.task.text("task_id"));values.emplace_back(result.task.number("generation"));values.emplace_back(result.descriptor);}
        return db.query({sql,values},capacity);
    }
    case UFB_CONTROL_APPLY: {
        auto request=a.id(0),body=a.text(1,524288);auto now=a.number(2),n=a.number(3,0,32);a.count(size_t(4+2*n));
        auto results=validate_response(db,request,body,account);std::map<std::string,std::string> references;
        for(int64_t i=0;i<n;++i){auto id=a.id(size_t(4+i*2)),reference=a.text(size_t(5+i*2),16384);require(!reference.empty() && references.emplace(id,reference).second,"Invalid descriptor reference");}
        size_t needed=0;for(auto &result:results)if(!result.applied && !result.descriptor.empty()){require(references.count(result.task.text("task_id")),"Missing protected descriptor reference");++needed;}
        require(references.size()==needed,"Unexpected protected descriptor reference");
        // Validate the entire batch first. Each independent item then commits its
        // receipt and applied marker in one short, crash-recoverable transaction.
        for(auto &result:results) {
            if(result.applied)continue;
            auto id=result.task.text("task_id");auto generation=result.task.number("generation");commands.clear();
            auto deadline=result.task.number("confirm_deadline_utc");
            commands.emplace_back("UPDATE control_items SET applied=1 WHERE request_id=? AND task_id=? AND generation=? AND applied=0 AND EXISTS(SELECT 1 FROM tasks WHERE id=? AND current_generation=?)",std::vector<Value>{request,id,generation,id,generation},1);
            if(result.status=="Confirmed")append_confirmation(commands,id,generation,result.backup,result.task.at("sha256"),result.task.number("byte_count"),result.confirmed,now);
            else if((result.status=="UploadRequired" || result.status=="Pending" || result.status=="Verifying") && deadline && now>=deadline)
                finish_error(commands,id,generation,"Confirmation deadline reached",now);
            else if(result.status=="UploadRequired") {
                commands.emplace_back("UPDATE task_attempts SET protocol_phase=1,execution_state=0,upload_reference=?,upload_expires_utc=?,server_outcome=0,system_task_id=NULL WHERE task_id=? AND generation=? AND upload_released=1",std::vector<Value>{references.at(id),result.expires,id,generation},1);
                commands.emplace_back("UPDATE tasks SET state=1,error=NULL,updated_utc=? WHERE id=? AND current_generation=?",std::vector<Value>{now,id,generation},1);
            } else if(result.status=="Pending" || result.status=="Verifying") {
                if(!deadline)deadline=now+Day;
                if(result.task.number("confirm_checks")+1>=256)finish_error(commands,id,generation,"Confirmation check budget reached",now);
                else if(result.next>=deadline)finish_error(commands,id,generation,"Next confirmation time exceeds the confirmation deadline",now);
                else {
                    commands.emplace_back("UPDATE task_attempts SET protocol_phase=2,execution_state=0,next_check_utc=?,confirm_checks=confirm_checks+1,confirm_deadline_utc=CASE WHEN confirm_deadline_utc=0 THEN ? ELSE confirm_deadline_utc END WHERE task_id=? AND generation=?",std::vector<Value>{result.next,now+Day,id,generation},1);
                    commands.emplace_back("UPDATE tasks SET state=2,updated_utc=? WHERE id=? AND current_generation=?",std::vector<Value>{now,id,generation},1);
                }
            } else if(result.status=="Canceled") {
                commands.emplace_back("UPDATE task_attempts SET protocol_phase=3,execution_state=4,server_outcome=2 WHERE task_id=? AND generation=?",std::vector<Value>{id,generation},1);
                commands.emplace_back("UPDATE tasks SET state=8,desired_action=2,error=NULL,updated_utc=? WHERE id=? AND current_generation=? AND state<>3",std::vector<Value>{now,id,generation},1);
                commands.emplace_back("UPDATE file_records SET state=2,updated_utc=? WHERE id=? AND state=1",std::vector<Value>{now,result.task.text("task_id")});
            } else finish_error(commands,id,generation,result.status=="Absent"?"Server reports Absent; an earlier in-flight request may still commit":result.error,now,result.status=="Rejected");
            // Keep control ownership until its actual OS execution has released.
            db.batch(commands);commands.clear();
        }
        commands.emplace_back("UPDATE control_requests SET state=4 WHERE id=? AND state IN(2,3,4)",std::vector<Value>{request},1);break;
    }
    case UFB_CONTROL_FAIL: case UFB_CONTROL_RECOVER: {
        a.count(4);auto request=a.id(0),error=a.text(1);auto now=a.number(2),pause=a.number(3,0,1);
        auto control=one(db,"SELECT kind,state>=2 AS submitted FROM control_requests WHERE id=?",{request});auto submitted=control.number("submitted");
        auto rows=select(db,"SELECT task_id,generation FROM control_items WHERE request_id=? AND applied=0",{request});
        for(auto &row:rows) {
            if(pause) {
                commands.emplace_back("UPDATE task_attempts SET execution_state=4,protocol_phase=CASE WHEN ?=0 THEN protocol_phase ELSE 2 END,next_check_utc=max(next_check_utc,?),confirm_deadline_utc=CASE WHEN confirm_deadline_utc=0 THEN ? ELSE confirm_deadline_utc END WHERE task_id=? AND generation=?",std::vector<Value>{submitted,now,now+Day,row.text("task_id"),row.number("generation")},1);
                commands.emplace_back("UPDATE tasks SET state=4,desired_action=CASE WHEN "+admission("tasks")+" THEN desired_action ELSE 1 END,error=NULL,updated_utc=? WHERE id=? AND current_generation=? AND state<>3",std::vector<Value>{now,row.text("task_id"),row.number("generation")},1);
            } else finish_error(commands,row.text("task_id"),row.number("generation"),error,now);
            // A saved cancellation intent may follow an interrupted Plan/Query.
            // A failed Cancel itself is terminal until explicit reconciliation.
            if(control.number("kind")!=2) {
                commands.emplace_back("UPDATE task_attempts SET protocol_phase=2,execution_state=0 WHERE task_id=? AND generation=? AND EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.desired_action=2)",std::vector<Value>{row.text("task_id"),row.number("generation")});
                commands.emplace_back("UPDATE tasks SET state=2 WHERE id=? AND current_generation=? AND desired_action=2",std::vector<Value>{row.text("task_id"),row.number("generation")});
            }
        }
        commands.emplace_back("UPDATE control_requests SET state=5,error=? WHERE id=? AND state<=3",std::vector<Value>{error,request});break;
    }
    case UFB_CONTROL_RELEASE: {
        a.count(1);auto request=a.id(0);
        commands.emplace_back("UPDATE control_requests SET released=1 WHERE id=? AND state IN(4,5)",std::vector<Value>{request},1);
        commands.emplace_back("UPDATE task_attempts SET control_id=NULL WHERE control_id=?",std::vector<Value>{request});break;
    }
    case UFB_CONTROLS: {
        a.count(2);auto page=Control;
        // Executors stream the durable body file. Listing it would materialize
        // up to 32 * 256 KiB and exceed the ABI's 1 MiB response budget.
        page.replace(page.find("c.*"),3,"c.id,c.kind,c.executor,c.state,c.relative_path,c.byte_count,c.not_before_utc,c.created_utc,c.system_task_id,c.released,c.cleanup_state,c.error,c.cleanup_error");
        return db.query({page+"WHERE c.released=0 AND c.executor=? AND c.id>? ORDER BY c.id LIMIT 32",{a.number(0,0,2),a.text(1,64)}},capacity);
    }
    case UFB_UPLOADS:
        a.count(2);return db.query({Work+"WHERE a.credential_released=0 AND a.control_id IS NULL AND a.executor=? AND a.protocol_phase=1 AND t.state=1 AND a.task_id>? AND t.desired_action=0 AND (SELECT paused FROM store_settings)=0 AND "+admission("t")+" ORDER BY a.task_id LIMIT 32",{a.number(0,0,2),a.text(1,64)}},capacity);
    case UFB_UPLOAD_START: {
        a.count(4);auto id=a.id(0);auto generation=a.number(1,1),now=a.number(3);auto system=a.text(2,256);
        commands.emplace_back("UPDATE task_attempts SET execution_state=1,upload_released=0,system_task_id=?,system_scheduled=1,payload_released=0 WHERE task_id=? AND generation=? AND protocol_phase=1 AND execution_state=0 AND upload_released=1 AND control_id IS NULL AND upload_expires_utc>? AND EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.current_generation=generation AND t.state=1 AND t.desired_action=0 AND "+admission("t")+") AND (SELECT paused FROM store_settings)=0",std::vector<Value>{system,id,generation,now});
        commands.emplace_back("SELECT changes() AS started");break;
    }
    case UFB_UPLOAD_REJECT: {
        // No OS task/stream was started. Isolate this item's local failure;
        // its existing remote attempt still requires authoritative reconciliation.
        a.count(4);auto id=a.id(0);auto generation=a.number(1,1),now=a.number(3);
        auto rows=select(db,"SELECT task_id FROM task_attempts WHERE task_id=? AND generation=? AND protocol_phase=1 AND execution_state=0 AND upload_released=1 AND control_id IS NULL",{id,generation});
        if(rows.empty())return {};
        finish_error(commands,id,generation,a.text(2),now);break;
    }
    case UFB_UPLOAD_END: {
        a.count(5);auto id=a.id(0);auto generation=a.number(1,1),success=a.number(2,0,2),now=a.number(4);auto error=a.text(3);
        commands.emplace_back("UPDATE task_attempts SET task_id=task_id WHERE task_id=? AND generation=? AND protocol_phase=1 AND execution_state=1 AND upload_released=0",std::vector<Value>{id,generation},1);
        auto cancel=one(db,"SELECT desired_action=2 AS cancel FROM tasks WHERE id=?",{id}).number("cancel");
        if(success || cancel) {
            commands.emplace_back("UPDATE task_attempts SET protocol_phase=2,execution_state=0,next_check_utc=?,confirm_deadline_utc=?,confirm_checks=0 WHERE task_id=? AND generation=?",std::vector<Value>{now+Second,now+Day,id,generation},1);
            commands.emplace_back("UPDATE tasks SET state=?,updated_utc=? WHERE id=? AND current_generation=?",std::vector<Value>{int64_t(success==2 && !cancel?4:2),now,id,generation},1);
        } else finish_error(commands,id,generation,error,now);
        break;
    }
    case UFB_UPLOAD_RELEASE:
        // Once an unused expired description is physically released, expose its
        // next Plan in the same commit. Event-driven adapters need no extra wake.
        a.count(3);commands.emplace_back("UPDATE task_attempts SET upload_released=1,payload_released=1,upload_reference=NULL,protocol_phase=CASE WHEN protocol_phase=1 AND upload_expires_utc<=? THEN 0 ELSE protocol_phase END,system_task_id=NULL WHERE task_id=? AND generation=? AND execution_state<>1 AND (protocol_phase<>1 OR upload_expires_utc<=? OR EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.state=4))",std::vector<Value>{a.number(2),a.id(0),a.number(1,1),a.number(2)},1);break;
    case UFB_PROTOCOL_RECONCILE: {
        a.count(4);auto id=a.id(0),credential=a.text(3,16384);auto now=a.number(1),cancel=a.number(2,0,1);require(!credential.empty(),"Reconciliation requires protected credentials");
        commands.emplace_back("UPDATE tasks SET state=2,error=NULL,desired_action=CASE WHEN ?=1 OR desired_action=2 THEN 2 ELSE 0 END,updated_utc=? WHERE id=? AND state=6 AND EXISTS(SELECT 1 FROM task_attempts a WHERE a.task_id=tasks.id AND a.generation=current_generation AND a.credential_released=1 AND a.upload_released=1 AND a.upload_reference IS NULL AND a.control_id IS NULL)",std::vector<Value>{cancel,now,id},1);
        commands.emplace_back("UPDATE task_attempts SET protocol_phase=2,execution_state=0,next_check_utc=?,confirm_deadline_utc=?,confirm_checks=0,credential_reference=?,credential_released=0 WHERE task_id=? AND generation=(SELECT current_generation FROM tasks WHERE id=?)",std::vector<Value>{now,now+Day,credential,id,id},1);break;
    }
    case UFB_PROTOCOL_RELEASE:
        a.count(2);commands.emplace_back("UPDATE task_attempts SET credential_released=1,payload_released=1,credential_reference=NULL WHERE task_id=? AND generation=? AND (protocol_phase=3 OR EXISTS(SELECT 1 FROM tasks t WHERE t.id=task_id AND t.state=4)) AND upload_released=1 AND upload_reference IS NULL AND control_id IS NULL",std::vector<Value>{a.id(0),a.number(1,1)},1);break;
    case UFB_PROTOCOL_RESUME: {
        a.count(3);auto id=a.id(0),credential=a.text(1,16384);auto now=a.number(2);require(!credential.empty(),"Resume requires protected credentials");
        commands.emplace_back("UPDATE tasks SET scope_epoch=coalesce((SELECT admission_epoch FROM scopes WHERE id=scope_id),0),state=CASE (SELECT protocol_phase FROM task_attempts WHERE task_id=tasks.id AND generation=current_generation) WHEN 2 THEN 2 ELSE 1 END,desired_action=0,error=NULL,updated_utc=? WHERE id=? AND state IN(0,4) AND (scope_id IS NULL OR EXISTS(SELECT 1 FROM scopes WHERE id=scope_id AND enabled=1 AND requires_confirmation=0)) AND current_generation>0 AND EXISTS(SELECT 1 FROM task_attempts a WHERE a.task_id=tasks.id AND a.generation=current_generation AND a.protocol_phase<3 AND a.credential_released=1 AND a.upload_released=1 AND a.control_id IS NULL)",std::vector<Value>{now,id},1);
        commands.emplace_back("UPDATE task_attempts SET execution_state=0,protocol_phase=CASE WHEN protocol_phase=1 AND upload_reference IS NULL THEN 0 ELSE protocol_phase END,credential_reference=?,credential_released=0,system_scheduled=0 WHERE task_id=? AND generation=(SELECT current_generation FROM tasks WHERE id=?)",std::vector<Value>{credential,id,id},1);break;
    }
    case UFB_PROTOCOL_ACTIONS: {
        a.count(2);auto executor=a.number(0,0,2),now=a.number(1);
        auto rows=select(db,Work+"WHERE a.credential_released=0 AND a.control_id IS NULL AND a.executor=? AND a.upload_released=1 AND a.execution_state<>1 AND a.protocol_phase<3 ORDER BY a.task_id LIMIT 64",{executor});
        auto paused=one(db,"SELECT paused FROM store_settings").number("paused");
        for(auto &row:rows) {
            auto id=row.text("task_id");auto generation=row.number("generation");
            if(row.number("desired_action")==2)continue;
            bool revoked=row.number("protocol_phase")!=2 && !one(db,"SELECT "+admission("t")+" AS allowed FROM tasks t WHERE id=?",{id}).number("allowed");
            if(paused || row.number("desired_action")==1 || revoked) {
                commands.emplace_back("UPDATE tasks SET state=4,desired_action=CASE WHEN ? THEN 1 ELSE desired_action END,error=NULL,updated_utc=? WHERE id=? AND current_generation=?",std::vector<Value>{int64_t(revoked),now,id,generation},1);
                commands.emplace_back("UPDATE task_attempts SET execution_state=4 WHERE task_id=? AND generation=?",std::vector<Value>{id,generation},1);
            } else if(row.number("protocol_phase")==2 && now>=row.number("confirm_deadline_utc"))finish_error(commands,id,generation,"Confirmation deadline reached",now);
        }
        if(commands.empty())return {};
        break;
    }
    case UFB_PROTOCOL_CLEANUP: case UFB_CONTROL_CLEANUP_RETRY: {
        a.count(2);bool retry=command==UFB_CONTROL_CLEANUP_RETRY;
        auto now=a.number(retry?1:0),limit=retry?int64_t(1):a.number(1,1,64);
        if(retry)db.batch({{"UPDATE control_requests SET cleanup_state=0,cleanup_error=NULL WHERE id=? AND released=1 AND cleanup_state=3",{a.id(0)},1}});
        auto rows=select(db,"SELECT id,relative_path FROM control_requests WHERE released=1 AND cleanup_state IN(0,1) "+std::string(retry?"AND id=? ":"")+"ORDER BY id LIMIT ?",retry?std::vector<Value>{a.id(0),limit}:std::vector<Value>{limit});
        int64_t cleaned=0,failed=0;
        for(auto &row:rows) {
            auto id=row.text("id");require(row.text("relative_path")=="controls/"+id+".json","Invalid owned control path",UF_STATE);
            db.batch({{"UPDATE control_requests SET cleanup_state=1 WHERE id=? AND released=1 AND cleanup_state IN(0,1)",{id},1}});
            try {auto file=directory/row.text("relative_path");require(!std::filesystem::is_symlink(file),"Control file is a symbolic link",UF_IO);std::filesystem::remove(file);}
            catch(const std::exception &primary) {
                auto original=std::current_exception();
                try {db.batch({{"UPDATE control_requests SET cleanup_state=3,cleanup_error=? WHERE id=?",{std::string(primary.what()),id},1}});}
                catch(const std::exception &secondary){std::fprintf(stderr,"Control cleanup recording failed: %s\n",secondary.what());std::rethrow_exception(original);}
                if(retry)throw Error(UFB_CLEANUP_FILE_FAILED,primary.what());
                ++failed;continue;
            }
            db.batch({{"UPDATE control_requests SET cleanup_state=2,body='' WHERE id=?",{id},1}});++cleaned;
        }
        // File ownership is over. Retain small diagnostic rows for 30 days,
        // and prune through the sparse history index in bounded transactions.
        for(auto &row:select(db,"SELECT id FROM control_requests WHERE released=1 AND cleanup_state=2 AND created_utc<? ORDER BY created_utc,id LIMIT ?",{std::max(int64_t(0),now-30*Day),limit}))
            db.batch({{"DELETE FROM control_items WHERE request_id=?",{row.text("id")}},
                {"DELETE FROM control_requests WHERE id=? AND released=1 AND cleanup_state=2",{row.text("id")},1}});
        return db.query({"SELECT ? AS cleaned,? AS failed",{cleaned,failed}},capacity);
    }
    case UFB_CONTROL_CLEANUP_FAILURES:
        a.count(2);return db.query({"SELECT id,byte_count,cleanup_error,created_utc FROM control_requests WHERE released=1 AND cleanup_state=3 AND id>? ORDER BY id LIMIT ?",{a.text(0,64),a.number(1,1,200)}},capacity);
    default:throw Error(UF_ARGUMENT,"Unknown backup protocol command");
    }
    return db.batch(commands,capacity);
}
}
