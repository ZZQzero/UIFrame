#pragma once
#include "ufsqlite_client.hpp"
#include <algorithm>
#include <filesystem>

namespace ufbackup {
using namespace ufsqlite;
constexpr uint32_t MaxBytes=1024*1024;
constexpr int64_t UnixEpochTicks=621355968000000000LL;
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
    std::string metadata(size_t i,size_t limit) const {
        auto s=text(i,limit);require(!s.empty() && std::all_of(s.begin(),s.end(),[](unsigned char c){return c>=32;}),"Invalid protocol metadata");return s;
    }
    int64_t number(size_t i,int64_t low=0,int64_t high=INT64_MAX) const {
        auto n=value(i,1).integer; require(n>=low && n<=high,"Invalid numeric argument"); return n;
    }
    std::string id(size_t i) const { auto s=text(i,64); require(!s.empty() && std::all_of(s.begin(),s.end(),[](char c){return (c>='a' && c<='f') || (c>='0' && c<='9');}),"Invalid identity"); return s; }
};
// A task has one control owner; receipt sharing never transfers that ownership.
inline std::string admission(const std::string &task) {
    return "("+task+".scope_id IS NULL OR EXISTS(SELECT 1 FROM scopes s WHERE s.id="+task+".scope_id AND s.requires_confirmation=0 AND s.admission_epoch="+task+".scope_epoch))";
}
void durable_payload(const std::filesystem::path &payload,uint64_t bytes);
void append_confirmation(std::vector<Command> &commands,const std::string &id,int64_t generation,
    const std::string &backup,const Value &hash,int64_t size,int64_t confirmed,int64_t now);
Bytes protocol_command(Client &db,const std::string &path,
    const std::string &account,unsigned command,const Args &args,unsigned capacity);
}
