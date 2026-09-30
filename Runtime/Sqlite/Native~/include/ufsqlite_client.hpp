#ifndef UFSQLITE_CLIENT_HPP
#define UFSQLITE_CLIENT_HPP
#include "ufsqlite.h"
#include <cstdio>
#include <cstring>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

// Blocking native facade over the same bounded engine used by C#. Call from a
// platform worker, never the Unity/UI thread. One caller owns each Client.
namespace ufsqlite
{
using Bytes = std::vector<uint8_t>;
struct Error : std::runtime_error
{
    int code, sql, committed; unsigned phase;
    Error(int c, const char *message, int s=0, int commit=0, unsigned p=0)
        : std::runtime_error(message), code(c), sql(s), committed(commit), phase(p) {}
};
inline void require(bool value, const char *message, int code=UF_ARGUMENT)
{ if (!value) throw Error(code, message); }
inline void check(int code, const char *message)
{ if (code) throw Error(code, message); }
inline void number(Bytes &b, uint64_t value, unsigned size)
{ for (unsigned i=0;i<size;++i) b.push_back(uint8_t(value>>(8*i))); }
inline void string(Bytes &b, const std::string &value)
{ number(b,value.size(),4); b.insert(b.end(),value.begin(),value.end()); }
struct Reader
{
    const uint8_t *data; size_t left;
    uint64_t number(unsigned size)
    {
        require(left>=size,"Truncated wire value"); uint64_t result=0;
        for (unsigned i=0;i<size;++i) result|=uint64_t(data[i])<<(8*i);
        data+=size; left-=size; return result;
    }
    std::string string()
    {
        auto n=number(4); require(n<=left,"Truncated wire string");
        std::string value(reinterpret_cast<const char*>(data),size_t(n)); data+=n; left-=n; return value;
    }
};
struct Value
{
    uint8_t type=0; int64_t integer=0; std::string text;
    Value()=default;
    Value(int64_t n):type(1),integer(n) {}
    Value(const std::string &s):type(3),text(s) {}
    Value(const char *s):type(3),text(s) {}
    static Value blob(std::string s) { Value v(std::move(s)); v.type=4; return v; }
    void write(Bytes &b) const
    { number(b,type,1); if(type==1 || type==2) number(b,uint64_t(integer),8); else if(type==3 || type==4) string(b,text); }
    static Value read(Reader &r)
    {
        Value v; v.type=uint8_t(r.number(1)); require(v.type<=4,"Invalid wire tag");
        if(v.type==1 || v.type==2) v.integer=int64_t(r.number(8));
        else if(v.type==3 || v.type==4) v.text=r.string();
        return v;
    }
};
struct Command
{
    std::string sql; std::vector<Value> parameters; int64_t expected=-1;
    Command(std::string s, std::vector<Value> p={}, int64_t e=-1):sql(std::move(s)),parameters(std::move(p)),expected(e) {}
};
inline Bytes encode(const std::vector<Command> &commands)
{
    require(!commands.empty() && commands.size()<=200,"SQL batch must have 1-200 commands");
    Bytes b; number(b,commands.size(),4);
    for(auto &c:commands) { string(b,c.sql); number(b,uint64_t(c.expected),8); number(b,c.parameters.size(),4); for(auto &p:c.parameters) p.write(b); }
    require(b.size()<=1024*1024,"Native command exceeds 1 MiB"); return b;
}
struct Table { std::vector<std::string> columns; std::vector<std::vector<Value>> rows; int64_t affected; };
inline std::vector<Table> decode(const Bytes &bytes)
{
    if(bytes.empty()) return {};
    Reader r{bytes.data(),bytes.size()}; auto count=r.number(4); require(count<=200,"Invalid table count");
    std::vector<Table> tables;
    for(uint64_t i=0;i<count;++i) {
        Table t; auto columns=r.number(4), rows=r.number(4); t.affected=int64_t(r.number(8));
        require(columns<=1024 && rows<=200,"Invalid table dimensions");
        for(uint64_t c=0;c<columns;++c) t.columns.push_back(r.string());
        for(uint64_t row=0;row<rows;++row) { std::vector<Value> values; for(uint64_t c=0;c<columns;++c) values.push_back(Value::read(r)); t.rows.push_back(std::move(values)); }
        tables.push_back(std::move(t));
    }
    require(!r.left,"Trailing result bytes"); return tables;
}
class Client
{
    uint64_t client=0, database=0;
    bool stopped=false;
public:
    Client() { check(ufsqlite_client_create(UFSQLITE_ABI,&client),"Cannot create native SQLite client"); }
    Client(const Client&)=delete; Client &operator=(const Client&)=delete;
    ~Client() noexcept
    { try { close(); } catch(const std::exception &error) { std::fprintf(stderr,"UIFrame native SQLite cleanup: %s\n",error.what()); } }
    Bytes run(unsigned kind,const Bytes &payload={},unsigned rows=200,unsigned bytes=1024*1024)
    {
        require(client && !stopped,"Native SQLite client is closed",UF_STATE);
        Bytes result; result.reserve(bytes); // Allocate before any SQL can commit.
        uint64_t operation=0; check(ufsqlite_submit(client,database,kind,payload.data(),payload.size(),rows,bytes,10000,&operation),"Native SQL admission failed");
        uf_completion completion{}; uint32_t count=0;
        int wait=0;
        do { wait=ufsqlite_wait(client,&completion,1,1000,&count); } while(!wait && !count);
        if(wait) {
            stopped=true;
            int cleanup=ufsqlite_client_stop(client);
            if(cleanup) std::fprintf(stderr,"UIFrame SQLite stop failed: %d\n",cleanup);
            cleanup=ufsqlite_discard_result(client,operation);
            if(cleanup) std::fprintf(stderr,"UIFrame SQLite discard failed: %d\n",cleanup);
            throw Error(wait,"Native SQLite completion failed");
        }
        struct Release {
            uint64_t client, operation; bool owned=true;
            ~Release() { if(owned) { auto error=ufsqlite_release_result(client,operation); if(error) std::fprintf(stderr,"UIFrame SQLite result release failed: %d\n",error); } }
        } release{client,operation};
        if(completion.error) throw Error(completion.error,completion.message,completion.sqlite_code,completion.committed,completion.phase);
        if(kind==UF_OPEN) database=completion.database;
        if(kind==UF_CLOSE) database=0;
        if(completion.data_size) result.assign(completion.data,completion.data+completion.data_size);
        release.owned=false;
        auto released=ufsqlite_release_result(client,operation);
        if(released) throw Error(released,"Native SQL result release failed",0,completion.committed,completion.phase);
        return result;
    }
    void open(const std::string &path,bool create)
    {
        require(!database,"Native client already owns a database",UF_STATE);
        Bytes b; number(b,create?0:1,4); string(b,path); number(b,16*1024*1024,8); number(b,128*1024*1024,8);
        run(UF_OPEN,b);
    }
    Bytes batch(const std::vector<Command> &commands,unsigned bytes=1024*1024)
    { return run(UF_BATCH,encode(commands),200,bytes); }
    Bytes query(const Command &command,unsigned bytes=1024*1024)
    { return run(UF_QUERY,encode({command}),200,bytes); }
    void close()
    {
        if(!client) return;
        if(!stopped) {
            // Stop is independent of result delivery and closes even a faulted DB.
            stopped=true; check(ufsqlite_client_stop(client),"Native SQLite stop failed"); database=0;
        }
        check(ufsqlite_client_release(client),"Native SQLite client release failed"); client=0;
    }
};
}
#endif
