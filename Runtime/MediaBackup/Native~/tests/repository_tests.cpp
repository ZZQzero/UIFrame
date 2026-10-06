#include "ufbackup.h"
#include "ufsqlite_client.hpp"
#include <filesystem>
#include <fstream>
#include <iostream>
#include <chrono>
#include <thread>
#include <algorithm>
using namespace ufsqlite;
void expect(bool ok,const char *message) { if(!ok) throw std::runtime_error(message); }
struct Store {
    uint64_t handle=0;
    std::string root,id;
    explicit Store(std::string path,std::string identity,bool create):root(std::move(path)),id(std::move(identity)) {
        ufb_status status{}; status.size=sizeof(status); status.abi=UFB_ABI;
        auto code=ufbackup_open(root.c_str(),id.c_str(),"https://test.invalid","test",create?1:0,&handle,&status);
        if(code) throw std::runtime_error(std::string("Open failed: ")+status.message);
    }
    ~Store(){ if(handle) { try { close(); } catch(const std::exception &e){std::cerr<<e.what()<<'\n';} } }
    void close(){ufb_status s{};s.size=sizeof(s);s.abi=UFB_ABI; auto h=handle;handle=0;expect(ufbackup_close(h,&s)==0,"Close failed");}
    std::vector<Table> call(unsigned command,std::vector<Value> args={},int expected=0,unsigned capacity=1024*1024) {
        Bytes input; number(input,args.size(),4); for(auto &v:args) v.write(input);
        Bytes output(capacity); ufb_status status{};status.size=sizeof(status);status.abi=UFB_ABI;
        auto error=ufbackup_call(handle,command,input.data(),uint32_t(input.size()),output.data(),capacity,&status);
        if(error!=expected) throw std::runtime_error("Command "+std::to_string(command)+" expected "+std::to_string(expected)+" got "+std::to_string(error)+": "+status.message);
        if(error) return {};
        output.resize(status.length);return decode(output);
    }

    std::string confirmed(const std::string &task,const std::string &request,const std::string &backup,
        const std::string &source,int64_t bytes,const std::string &hash,int64_t now) {
        call(UFB_CONTROL_CREATE,{request,int64_t(0),now,int64_t(0),int64_t(1)});
        call(UFB_CONTROL_SEAL,{request});call(UFB_CONTROL_SUBMITTED,{request,"system:"+request});call(UFB_CONTROL_START,{request});
        auto info=call(UFB_INFO).back();auto at=std::find(info.columns.begin(),info.columns.end(),"source_namespace")-info.columns.begin();
        auto space=info.rows[0][at].text;
        auto body=std::string("{\"protocolVersion\":2,\"requestId\":\"")+request+"\",\"account\":\"test\",\"serverTime\":0,"
            "\"limits\":{\"maxItems\":32,\"maxRequestBytes\":262144,\"maxResponseBytes\":524288,\"maxDescriptorBytes\":8192,\"maxFileBytes\":536870912},"
            "\"items\":[{\"clientTaskId\":\""+task+"\",\"attemptGeneration\":1,\"status\":\"Confirmed\",\"receipt\":{"
            "\"clientTaskId\":\""+task+"\",\"attemptGeneration\":1,\"backupId\":\""+backup+"\",\"sourceNamespace\":\""+space+"\","
            "\"sourceId\":\""+source+"\",\"sourceVersion\":\"v1\",\"sha256\":\""+hash+"\",\"byteCount\":"+std::to_string(bytes)+",\"confirmedAt\":0}}]}";
        call(UFB_CONTROL_APPLY,{request,body,now,int64_t(0)});return body;
    }
};
int main() {
    try {
        auto root=std::filesystem::temp_directory_path()/("ufbackup-contract-"+std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
        auto identity=std::string(64,'a'); auto hash=std::string(64,'b');
        {
            Store store(root.u8string(),identity,true);
            store.call(UFB_BIND_PREPARER,{"bb"});
            store.call(UFB_PREPARE,{"aa","bb",int64_t(1),int64_t(1),"cc","file:sample","v1","sample.jpg","image/jpeg",int64_t(16)});
            store.call(UFB_ACCEPT_ITEM,{"cc","bb",int64_t(2)},UF_CONDITION);
            std::ofstream(root/identity/"payloads/cc.payload")<<"0123456789abcdef";
            store.call(UFB_SEAL,{"cc","bb",int64_t(16),hash,"image/jpeg",int64_t(1024),int64_t(2)});
            std::ofstream(root/identity/"payloads/cc.payload")<<"0123456789abcdef";
            store.call(UFB_ACCEPT_ITEM,{"cc","bb",int64_t(3)});
            auto page=store.call(UFB_TASKS,{int64_t(0),int64_t(100),int64_t(-1),int64_t(10)}).back();
            expect(page.rows.size()==1 && page.rows[0][5].integer==0,"Prepared batch not accepted");
            store.call(UFB_PAUSE,{int64_t(1)});
            expect(store.call(UFB_CLAIM,{"cc",int64_t(0),int64_t(0),int64_t(4)}).back().rows.empty(),"Ineligible task was claimed");
            store.call(UFB_PAUSE,{int64_t(0)});
            store.call(UFB_CLAIM,{"cc",int64_t(0),int64_t(0),int64_t(4)});
            expect(store.call(UFB_CLAIM,{"cc",int64_t(0),int64_t(0),int64_t(4)}).back().rows.empty(),"Ineligible task was claimed");
            store.call(UFB_HANDOFF,{"cc",int64_t(1),"protected"});
            store.call(UFB_ACTION,{"cc",int64_t(2),int64_t(5)});
            auto response=store.confirmed("cc","abcd",hash,"file:sample",16,hash,6);
            expect(store.call(UFB_TASK,{"cc"}).back().rows[0][5].integer==3,"Confirmed completion must win cancellation");
            expect(store.call(UFB_CLEANUP_PAGE,{"",int64_t(10)}).back().rows.empty(),"Live payload was eligible for cleanup");
            store.call(UFB_CONTROL_APPLY,{"abcd",response,int64_t(6),int64_t(0)});
            store.call(UFB_PROTOCOL_RELEASE,{"cc",int64_t(1)},UF_CONDITION);
            store.call(UFB_CONTROL_RELEASE,{"abcd"});store.call(UFB_PROTOCOL_RELEASE,{"cc",int64_t(1)});
            store.call(UFB_PROTOCOL_CLEANUP,{int64_t(6),int64_t(32)});
            expect(store.call(UFB_CLEANUP_PAGE,{"",int64_t(10)}).back().rows.size()==1,"Released payload must be reclaimable");
            Store background(root.u8string(),identity,false);
            store.close();
            expect(background.call(UFB_RECEIPT,{hash}).back().rows.size()==1,"Background attachment lost repository");
            background.call(UFB_UPLOAD_END,{"cc",int64_t(2),int64_t(1),"",int64_t(6)},UF_CONDITION);
            background.call(UFB_ACTION,{"cc",int64_t(2),int64_t(7)},UF_CONDITION);
            auto cleaned=background.call(UFB_CLEANUP_RUN,{"cc",int64_t(6),int64_t(7)}).back().rows;
            expect(cleaned[0][1].integer==16,"Cleanup must report actual released file bytes");
            expect(!std::filesystem::exists(root/identity/"payloads/cc.payload"),"Owned payload was not deleted");
            expect(background.call(UFB_HISTORY_PAGE,{int64_t(0),int64_t(0),int64_t(0),int64_t(10)}).back().rows.size()==1,"Keep-zero history policy retained one extra task");
            background.call(UFB_PRUNE_TASK,{"cc",int64_t(6)});
            expect(background.call(UFB_TASK,{"cc"}).back().rows.empty(),"Eligible history was not removed");
            expect(background.call(UFB_RECEIPT,{hash}).back().rows.size()==1,"History cleanup erased receipt");
            background.call(UFB_BIND_PREPARER,{"ee"});
            background.call(UFB_PREPARE,{"11","ee",int64_t(10),int64_t(1),"22","file:new","v1","new.jpg","image/jpeg",int64_t(1)});
            std::ofstream(root/identity/"payloads/22.payload")<<"x";
            background.call(UFB_SEAL,{"22","ee",int64_t(1),hash,"image/jpeg",int64_t(1024),int64_t(11)});
            background.call(UFB_ACCEPT_ITEM,{"22","ee",int64_t(12)});
            auto fingerprint=Value::blob(std::string(32,'a'));
            background.call(UFB_OPERATION,{"33",int64_t(1),int64_t(0),int64_t(13),int64_t(0),fingerprint});
            background.call(UFB_OPERATION,{"33",int64_t(2),int64_t(0),int64_t(13),int64_t(0),fingerprint},UF_CONDITION);
            background.call(UFB_PREPARE,{"44","ee",int64_t(14),int64_t(1),"55","file:later","v1","later.jpg","image/jpeg",int64_t(1)});
            std::ofstream(root/identity/"payloads/55.payload")<<"x";
            background.call(UFB_SEAL,{"55","ee",int64_t(1),hash,"image/jpeg",int64_t(1024),int64_t(15)});
            background.call(UFB_ACCEPT_ITEM,{"55","ee",int64_t(16)});
            background.call(UFB_SELECT_OPERATION,{"33",int64_t(17),int64_t(128)});
            background.call(UFB_APPLY_OPERATION,{"33",int64_t(18),int64_t(32)});
            expect(background.call(UFB_TASK,{"22"}).back().rows[0][5].integer==4,"Operation target was not paused");
            expect(background.call(UFB_TASK,{"55"}).back().rows[0][5].integer==0,"Operation included a post-submission task");
            background.call(UFB_OPERATION,{"33",int64_t(1),int64_t(0),int64_t(19),int64_t(0),fingerprint});
            expect(background.call(UFB_OPERATION_ITEMS,{"33","",int64_t(200)}).back().rows.size()==1,"Duplicate operation replayed targets");
            background.call(UFB_PRUNE_OPERATION,{"33",int64_t(30),int64_t(200)});
            auto operation=background.call(UFB_OPERATION_STATUS,{"33"}).back();
            expect(operation.rows.size()==1 && operation.rows[0][10].integer==1,"Expired operation header was lost");
            background.call(UFB_SCOPE,{"66","directory:photos","77",int64_t(1),int64_t(1),int64_t(1),int64_t(0)});
            background.call(UFB_BEGIN_SCAN,{"66","88","77",int64_t(1),int64_t(1),int64_t(1),int64_t(10)});
            background.call(UFB_SCAN_PAGE,{"66","88",int64_t(1),"file:old","v1","old.jpg","image/jpeg","old",int64_t(1)});
            background.call(UFB_ACTIVATE_SCAN,{"66","88","77",int64_t(1),int64_t(1),int64_t(1),int64_t(10),int64_t(10),int64_t(11)},UF_CONDITION);
            background.call(UFB_DISCOVER,{"66","77",int64_t(1),int64_t(1),int64_t(1),int64_t(10),int64_t(11),int64_t(0),int64_t(1),int64_t(11),int64_t(0),"file:newest","v1","newest.jpg","image/jpeg","newest",int64_t(1)});
            background.call(UFB_ACTIVATE_SCAN,{"66","88","77",int64_t(1),int64_t(1),int64_t(1),int64_t(10),int64_t(11),int64_t(0)});
            auto discoveries=background.call(UFB_DISCOVERIES,{"66","","",int64_t(0),int64_t(200)}).back();
            expect(discoveries.rows.size()==1 && discoveries.rows[0][0].text=="file:newest","Baseline swallowed a change discovered during enumeration");
            background.call(UFB_DISCOVER,{"66","99",int64_t(1),int64_t(1),int64_t(1),int64_t(11),int64_t(12),int64_t(0),int64_t(0)},UF_CONDITION);
            // Pause before control submission releases credentials without deleting the file.
            background.call(UFB_CLAIM,{"55",int64_t(0),int64_t(0),int64_t(40)});
            background.call(UFB_HANDOFF,{"55",int64_t(1),"protected"});
            background.call(UFB_PAUSE,{int64_t(1)});
            background.call(UFB_PROTOCOL_ACTIONS,{int64_t(0),int64_t(41)});
            background.call(UFB_PROTOCOL_RELEASE,{"55",int64_t(1)});
            background.call(UFB_PAUSE,{int64_t(0)});
            expect(background.call(UFB_TASK,{"55"}).back().rows[0][5].integer==4,"Paused task lost its state");
            // Preparation bytes are reserved before copying; partial exported files
            // belong to the same intent and are reclaimed after preparation failure.
            background.call(UFB_PREPARE,{"a1","ee",int64_t(45),int64_t(1),"a2","file:partial","v1","partial.jpg","image/jpeg",int64_t(0)});
            background.call(UFB_RESERVE_PAYLOAD,{"a2","ee",int64_t(100),int64_t(1024),int64_t(46)});
            background.call(UFB_RESERVE_PAYLOAD,{"a2","ee",int64_t(1024),int64_t(1024),int64_t(47)},UF_CONDITION);
            std::filesystem::create_directory(root/identity/"payloads/a2.payload.source");
            std::ofstream(root/identity/"payloads/a2.payload.source/image.jpg")<<"123";
            background.call(UFB_FAIL_ITEM,{"a2","ee","interrupted",int64_t(48)});
            expect(background.call(UFB_TASK,{"a2"}).back().rows[0][5].integer==7,"Preparation failure lost its observable task result");
            expect(background.call(UFB_CLEANUP_RUN,{"a2",int64_t(48),int64_t(49)}).back().rows[0][1].integer==3,"Partial export cleanup byte count");
            expect(!std::filesystem::exists(root/identity/"payloads/a2.payload.source"),"Partial export directory leaked");
            auto fileSummary=background.call(UFB_FILE_SUMMARY).back();
            for(auto &row:fileSummary.rows)
                if(row[0].integer==3)expect(row[1].integer==1,"Failed task lost its cleaned-file record");
            auto preparation=background.call(UFB_SUBMISSION,{"aa"}).back();
            expect(preparation.rows[0][1].text=="cc" && preparation.rows[0][2].integer==1,"Accepted preparation identity lost after task pruning");
            // Permission suspension and interrupted reset use bounded durable pages.
            background.call(UFB_ACTION,{"55",int64_t(0),int64_t(50)});
            background.call(UFB_DISCOVER,{"66","77",int64_t(1),int64_t(1),int64_t(1),int64_t(11),int64_t(12),int64_t(0),int64_t(1),int64_t(12),int64_t(0),"file:later","v1","later.jpg","image/jpeg","later",int64_t(1)});
            expect(background.call(UFB_SUSPEND_SCOPE,{"66",int64_t(0),int64_t(51)}).back().rows[0][0].integer==1,"Scope did not suspend its accepted task");
            expect(background.call(UFB_TASK,{"55"}).back().rows[0][5].integer==4,"Permission suspension did not persist pause");
            background.call(UFB_RESET_SCOPE,{"66"});
            background.call(UFB_SCOPE,{"66","directory:photos","77",int64_t(1),int64_t(1),int64_t(1),int64_t(0)},UF_CONDITION);
            expect(background.call(UFB_RESET_SCOPE_PAGE,{"66"}).back().rows[0][0].integer==0,"Reset did not finish");
            expect(background.call(UFB_DISCOVERIES,{"66","","",int64_t(0),int64_t(200)}).back().rows.empty(),"Reset left pending source dispositions");
            expect(background.call(UFB_RECEIPT,{hash}).back().rows.size()==1,"Reset erased confirmed backup");
            background.call(UFB_SCOPE,{"66","directory:photos","77",int64_t(1),int64_t(1),int64_t(1),int64_t(0)});
            expect(background.call(UFB_PRUNE_SCANS,{int64_t(1)}).back().rows[0][0].integer<=1,"Metadata maintenance exceeded its page budget");
            // Expired operation details do not prevent a later eligible operation
            // from being selected, nor replay an old operation ID.
            background.call(UFB_OPERATION,{"33",int64_t(1),int64_t(0),int64_t(52),int64_t(0),fingerprint});
            background.call(UFB_OPERATION,{"34",int64_t(1),int64_t(0),int64_t(53),int64_t(0),fingerprint});
            background.call(UFB_SELECT_OPERATION,{"34",int64_t(54),int64_t(128)});
            background.call(UFB_APPLY_OPERATION,{"34",int64_t(55),int64_t(32)});
            auto cleanupOps=background.call(UFB_OPERATION_CLEANUP_PAGE,{int64_t(60),int64_t(32)}).back();
            expect(cleanupOps.rows.size()==1 && cleanupOps.rows[0][0].text=="34","Operation cleanup starved behind expired headers");
            // Rebinding the facade never closes another background attachment.
            Store other(root.u8string(),identity,false);
            other.call(UFB_BIND_PREPARER,{"ff"},UF_STATE);
        }
        { Store reopened(root.u8string(),identity,false); expect(reopened.call(UFB_RECEIPTS,{int64_t(0),"",int64_t(10)}).back().rows.size()==1,"Receipt did not survive reopen"); }
        {
            Store store(root.u8string(),std::string(64,'f'),true);store.call(UFB_BIND_PREPARER,{"aa"});
            store.call(UFB_PREPARE,{"bb","aa",int64_t(1),int64_t(2),"a1","file:bad","v1","bad.jpg","image/jpeg",int64_t(4),"a2","file:good","v1","good.jpg","image/jpeg",int64_t(4)});
            auto folder=root/store.id/"payloads";
            std::filesystem::create_directory(folder/"a1.payload");std::ofstream(folder/"a1.payload"/"unexpected")<<"data";
            std::ofstream(folder/"a2.payload")<<"data";
            for(auto task:{"a1","a2"})store.call(UFB_FAIL_ITEM,{task,"aa","fixture cleanup",int64_t(2)});
            auto files=store.call(UFB_CLEANUP_PAGE,{"",int64_t(10)}).back();expect(files.rows.size()==2,"Missing cleanup candidates");
            store.call(UFB_CLEANUP_RUN,{"a1",files.rows[0][4],int64_t(3)},UFB_CLEANUP_FILE_FAILED);
            auto failed=store.call(UFB_CLEANUP_FAILURES,{"",int64_t(1)}).back();
            expect(failed.rows.size()==1 && failed.rows[0][0].text=="a1" && !failed.rows[0][2].text.empty() && failed.rows[0][4].text=="a1","Preparation cleanup failure lost its task identity");
            expect(store.call(UFB_CLEANUP_FAILURES,{"a1",int64_t(1)}).back().rows.empty(),"Cleanup failure cursor repeated a file");
            auto remaining=store.call(UFB_CLEANUP_PAGE,{"",int64_t(10)}).back();
            expect(remaining.rows.size()==1 && remaining.rows[0][0].text=="a2","Failed file was not isolated");
            store.call(UFB_CLEANUP_RUN,{"a2",remaining.rows[0][4],int64_t(4)});
            expect(!std::filesystem::exists(folder/"a2.payload"),"Independent cleanup was blocked");
            store.call(UFB_INFO);store.call(UFB_PAUSE,{int64_t(1)});store.call(UFB_PAUSE,{int64_t(0)});
            std::filesystem::remove_all(folder/"a1.payload");
            auto retry=store.call(UFB_CLEANUP_RETRY,{"a1",int64_t(5)}).back();
            expect(retry.rows.size()==1 && retry.rows[0][0].text=="a1","Retry lost its file identity");
            store.call(UFB_CLEANUP_RUN,{"a1",retry.rows[0][1],int64_t(6)});
        }
        for(int executor=0;executor<=2;++executor) {
            Store store(root.u8string(),std::string(64,char('b'+executor)),true);
            store.call(UFB_BIND_PREPARER,{"aa"});
            store.call(UFB_PREPARE,{"bb","aa",int64_t(1),int64_t(2),
                "cc","file:unknown","v1","unknown.jpg","image/jpeg",int64_t(4),
                "dd","file:independent","v1","independent.jpg","image/jpeg",int64_t(4)});
            for(auto task:{"cc","dd"}) {
                store.call(UFB_SEAL,{task,"aa",int64_t(4),hash,"image/jpeg",int64_t(1024),int64_t(2)},UF_IO);
                std::ofstream(root/store.id/"payloads"/(std::string(task)+".payload"))<<"data";
                store.call(UFB_SEAL,{task,"aa",int64_t(5),hash,"image/jpeg",int64_t(1024),int64_t(2)},UF_IO);
                store.call(UFB_SEAL,{task,"aa",int64_t(4),hash,"image/jpeg",int64_t(1024),int64_t(2)});
                store.call(UFB_ACCEPT_ITEM,{task,"aa",int64_t(3)});
            }
            store.call(UFB_CLAIM,{"cc",int64_t(executor),int64_t(0),int64_t(4)});
            expect(store.call(UFB_SCHEDULABLE,{int64_t(0),int64_t(executor),int64_t(10)}).back().rows.empty(),"Unhanded attempt reached scheduler");
            store.call(UFB_HANDOFF,{"cc",int64_t(1),"protected"});
            expect(store.call(UFB_ATTEMPTS,{int64_t(0),int64_t(executor),int64_t(10)}).back().rows.size()==1,"Active attempt inventory lost handoff");
            store.call(UFB_CONTROL_CREATE,{"ce",int64_t(executor),int64_t(4),int64_t(0),int64_t(1)});
            store.call(UFB_CONTROL_SEAL,{"ce"});store.call(UFB_CONTROL_SUBMITTED,{"ce","system"});
            store.call(UFB_CONTROL_RECOVER,{"ce","response lost",int64_t(5),int64_t(0)});
            store.call(UFB_PROTOCOL_RELEASE,{"cc",int64_t(1)},UF_CONDITION);
            store.call(UFB_CONTROL_RELEASE,{"ce"});store.call(UFB_PROTOCOL_RELEASE,{"cc",int64_t(1)});
            store.call(UFB_ACTION,{"cc",int64_t(3),int64_t(6)},UF_CONDITION);
            expect(store.call(UFB_TASK,{"cc"}).back().rows[0][5].integer==6,"Unknown result allowed direct retry");
            store.call(UFB_PROTOCOL_RECONCILE,{"cc",int64_t(7),int64_t(0),"renewed"});
            expect(store.call(UFB_CLAIM,{"dd",int64_t(executor),int64_t(0),int64_t(7)}).back().rows.size()==1,"Reconciliation blocked independent work");
            store.call(UFB_HANDOFF,{"dd",int64_t(1),"protected"});
            store.call(UFB_PAUSE,{int64_t(1)});store.call(UFB_PROTOCOL_ACTIONS,{int64_t(executor),int64_t(8)});
            for(auto task:{"cc","dd"})store.call(UFB_PROTOCOL_RELEASE,{task,int64_t(1)});
            expect(store.call(UFB_ATTEMPTS,{int64_t(0),int64_t(executor),int64_t(10)}).back().rows.empty(),"Released ownership retained in active inventory");
        }
        uf_diagnostics diagnostics{}; diagnostics.size=sizeof(diagnostics); diagnostics.abi=UFSQLITE_ABI;
        expect(ufsqlite_get_diagnostics(&diagnostics)==0,"Diagnostics unavailable");
        expect(!diagnostics.databases && !diagnostics.operations && !diagnostics.reserved_bytes,"Native resources leaked");
        std::filesystem::remove_all(root);
        std::cout<<"Backup repository contract passed\n"; return 0;
    } catch(const std::exception &error) { std::cerr<<error.what()<<'\n'; return 1; }
}
