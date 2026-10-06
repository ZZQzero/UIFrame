#include <fstream>
#ifndef _WIN32
#include <unistd.h>
#include <sys/stat.h>
#include <errno.h>
static bool fail_directory_sync = false;
static int snapshot_fsync(int fd) {
    struct stat info{};
    if (fail_directory_sync && fstat(fd, &info) == 0 && S_ISDIR(info.st_mode)) {
        fail_directory_sync = false; errno = EIO; return -1;
    }
    return fsync(fd);
}
#define fsync snapshot_fsync
#endif
#include "../src/core.cpp"
#ifndef _WIN32
#undef fsync
#endif
#include <iostream>
namespace {
std::vector<uint8_t> request(const char *sql) {
    Writer w(4096); w.number(1, 4); w.string(sql, std::strlen(sql));
    w.number(uint64_t(-1), 8); w.number(0, 4); return std::move(w.bytes);
}
uint64_t send(uint64_t client, uint64_t db, int kind, const std::vector<uint8_t> &body = {}) {
    uint64_t id;
    require(ufsqlite_submit(client, db, kind, body.data(), body.size(), kind == UF_CLOSE ? 0 : 10, kind == UF_CLOSE ? 0 : 4096, 5000, &id) == 0,
            UF_STATE, "submit failed"); return id;
}
uf_completion receive(uint64_t client, uint64_t expected) {
    uf_completion result{}; uint32_t count = 0;
    for (int i = 0; i < 100 && !count; ++i)
        require(ufsqlite_wait(client, &result, 1, 100, &count) == 0, UF_STATE, "wait failed");
    require(count == 1 && result.operation == expected, UF_STATE, "wrong completion order");
    return result;
}
void finish(uint64_t client, uint64_t expected) {
    auto result = receive(client, expected);
    require(result.error == 0, result.error, result.message);
    require(ufsqlite_release_result(client, expected) == 0, UF_STATE, "result release failed");
}
uint64_t open_store(uint64_t client, const std::string &path, int mode) {
    Writer w(4096); w.number(mode, 4); w.string(path.data(), path.size());
    w.number(0, 8); w.number(128 * MiB, 8);
    auto result = receive(client, send(client, 0, UF_OPEN, w.bytes));
    require(result.error == 0, result.error, result.message);
    require(ufsqlite_release_result(client, result.operation) == 0, UF_STATE, "open release failed");
    return result.database;
}
void snapshot_boundaries(uint64_t client, uint64_t db, const std::filesystem::path &root) {
    finish(client, send(client, db, UF_EXECUTE, request("CREATE TABLE snapshot_data(value BLOB)")));
    for (int i=0;i<4;++i) finish(client, send(client, db, UF_EXECUTE, request("INSERT INTO snapshot_data VALUES(zeroblob(500000))")));
    uint64_t serial = 100000;
    auto make = [&](const std::string &path) {
        auto job = std::make_unique<Job>(); job->id = ++serial; job->deadline = Clock::now()+std::chrono::seconds(60);
        { std::lock_guard<std::mutex> lock(engine().mutex); job->database = engine().databases.at(db); }
        Writer w(4096); w.string(path.data(), path.size()); w.number(32*MiB,8); w.number(32*MiB,8);
        job->input = std::move(w.bytes); return job;
    };
    auto fail = [&](Job &job, int expected) {
        int code = 0;
        try { while (!snapshot_step(job)) {} } catch (const Failure &error) { code = error.code; }
        require(code == expected, UF_STATE, "snapshot returned unexpected status");
        require(close_snapshot(job) == SQLITE_OK, UF_STATE, "snapshot cleanup failed");
    };
    for (const char *suffix : {"-wal", "-shm", "-journal"}) {
        auto path = (root / (std::string("sidecar")+suffix+".sqlite")).u8string();
        auto side = path+suffix;
        { std::ofstream stream(side); stream << "foreign"; }
        auto job = make(path); fail(*job, UF_STATE);
        require(!std::filesystem::exists(path), UF_STATE, "sidecar conflict published a snapshot");
        std::ifstream stream(side); std::string value; stream >> value;
        require(value == "foreign", UF_STATE, "foreign sidecar changed");
        stream.close(); std::filesystem::remove(side);
        job = make(path); require(!snapshot_step(*job), UF_STATE, "fixture must span steps");
        { std::ofstream output(side); output << "late"; }
        fail(*job, UF_STATE);
        require(!std::filesystem::exists(path) && std::filesystem::exists(side), UF_STATE, "late sidecar was overwritten");
        std::filesystem::remove(side);
    }
    auto path = (root / "owned-snapshot.sqlite").u8string();
    auto job = make(path); require(!snapshot_step(*job), UF_STATE, "fixture must span steps");
    auto competitor = make(path); fail(*competitor, UF_STATE);
    Writer open(4096); open.number(0,4); open.string(path.data(),path.size()); open.number(0,8); open.number(32*MiB,8);
    auto blocked = receive(client,send(client,0,UF_OPEN,open.bytes));
    require(blocked.error == UF_STATE, UF_STATE, "CreateNew entered snapshot destination");
    ufsqlite_release_result(client, blocked.operation);
    job->cancel = true; fail(*job, UF_CANCELED);
    require(!std::filesystem::exists(path), UF_STATE, "canceled snapshot published");
    job = make(path); while (!snapshot_step(*job)) {}
    require(job->completion.committed == 1, UF_STATE, "successful snapshot not marked published");
    auto copy = open_store(client,path,2);
    finish(client,send(client,copy,UF_QUERY,request("SELECT length(value) FROM snapshot_data")));
    finish(client,send(client,copy,UF_CLOSE));
#ifndef _WIN32
    path = (root / "sync-failure.sqlite").u8string(); job = make(path);
    fail_directory_sync = true; fail(*job,UF_IO);
    require(!fail_directory_sync && job->completion.committed == 1 && std::filesystem::exists(path), UF_STATE,
            "post-publication fsync failure lost its publication fact");
    copy = open_store(client,path,2);
    finish(client,send(client,copy,UF_QUERY,request("SELECT length(value) FROM snapshot_data")));
    finish(client,send(client,copy,UF_CLOSE));
#endif
}
}
int main() {
    try {
        auto root = std::filesystem::temp_directory_path() /
            ("uiframe-lifecycle-" + std::to_string(Clock::now().time_since_epoch().count()));
        std::filesystem::create_directory(root);
        uint64_t client, other;
        require(ufsqlite_client_create(UFSQLITE_ABI, &client) == 0, UF_STATE, "create");
        require(ufsqlite_client_create(UFSQLITE_ABI, &other) == 0, UF_STATE, "other create");
        auto path = (root / "test.sqlite").u8string();
        auto db = open_store(client, path, 0);
        finish(client, send(client, db, UF_EXECUTE, request("CREATE TABLE state(value INTEGER)")));
        finish(client, send(client, db, UF_EXECUTE, request("INSERT INTO state VALUES(0)")));
        auto a = request("UPDATE state SET value=1"), b = request("UPDATE state SET value=2");
        uint64_t first, second;
        auto &e = engine();
        // A controlled occupied writer removes timing assumptions from this test.
        { std::lock_guard<std::mutex> lock(e.mutex); e.databases.at(db)->write_busy = true; }
        require(ufsqlite_reserve(client, db, UF_EXECUTE, a.size(), 1, 128, 5000, &first) == 0, UF_STATE, "reserve A");
        require(ufsqlite_reserve(client, db, UF_EXECUTE, b.size(), 1, 128, 5000, &second) == 0, UF_STATE, "reserve B");
        require(ufsqlite_commit_request(client, second, b.data(), b.size()) == 0, UF_STATE, "commit B");
        require(ufsqlite_commit_request(client, first, a.data(), a.size()) == 0, UF_STATE, "commit A");
        { std::lock_guard<std::mutex> lock(e.mutex); e.databases.at(db)->write_busy = false; }
        e.work.notify_all();
        finish(client, second); finish(client, first);
        auto query = receive(client, send(client, db, UF_QUERY, request("SELECT value FROM state")));
        require(query.error == 0, UF_STATE, "read order");
        Reader value{query.data + query.data_size - 8, 8};
        require(value.number(8) == 1, UF_STATE, "writes did not follow acceptance order");
        require(ufsqlite_release_result(client, query.operation) == 0, UF_STATE, "query release");
        snapshot_boundaries(client, db, root);
        uint64_t reserved_close, duplicate_close;
        require(ufsqlite_reserve(client, db, UF_CLOSE, 0, 0, 0, 5000, &reserved_close) == 0, UF_STATE, "reserve close");
        require(ufsqlite_reserve(client, db, UF_CLOSE, 0, 0, 0, 5000, &duplicate_close) == UF_STATE, UF_STATE, "duplicate close reservation admitted");
        require(ufsqlite_reserve(client, db, UF_CLOSE, 1, 0, 0, 5000, &duplicate_close) == UF_ARGUMENT, UF_STATE, "close allocated unbounded payload");
        finish(client, send(client, db, UF_QUERY, request("SELECT 1")));
        require(ufsqlite_discard_request(client, reserved_close) == 0, UF_STATE, "discard close reservation");
        // Unaccepted reservations cannot block close, or commit after close is accepted.
        require(ufsqlite_reserve(client, db, UF_EXECUTE, a.size(), 1, 128, 5000, &first) == 0, UF_STATE, "reserve late");
        auto closing = send(client, db, UF_CLOSE);
        require(ufsqlite_commit_request(client, first, a.data(), a.size()) == UF_STATE, UF_STATE, "late commit accepted");
        require(ufsqlite_discard_request(client, first) == 0, UF_STATE, "late discard");
        finish(client, closing);
        db = open_store(client, path, 1);
        auto held = receive(client, send(client, db, UF_QUERY, request("SELECT value FROM state")));
        auto slow = send(client, db, UF_QUERY, request("WITH RECURSIVE r(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM r WHERE x<100000000) SELECT sum(x) FROM r"));
        require(ufsqlite_reserve(client, db, UF_EXECUTE, a.size(), 1, 128, 5000, &first) == 0, UF_STATE, "unused reservation");
        require(ufsqlite_client_stop(client) == 0, UF_STATE, "stop failed");
        Reader retained{held.data + held.data_size - 8, 8};
        require(retained.number(8) == 1, UF_STATE, "stop invalidated delivered result");
        require(ufsqlite_discard_result(client, slow) == 0, UF_STATE, "undelivered result leaked");
        require(ufsqlite_release_result(client, held.operation) == 0, UF_STATE, "delivered result leaked");
        require(ufsqlite_client_release(client) == 0, UF_STATE, "client leaked");
        auto reopened = open_store(other, path, 1);
        finish(other, send(other, reopened, UF_QUERY, request("SELECT value FROM state")));
        finish(other, send(other, reopened, UF_CLOSE));
        require(ufsqlite_client_release(other) == 0, UF_STATE, "independent client leaked");
        uf_diagnostics stats{}; stats.size = sizeof(stats); stats.abi = UFSQLITE_ABI;
        require(ufsqlite_get_diagnostics(&stats) == 0 && stats.operations == 0 && stats.databases == 0 && stats.reserved_bytes == 0,
                UF_STATE, "lifecycle retained resources");
        std::filesystem::remove_all(root);
        std::cout << "Acceptance order and fault lifetime checks passed.\n";
        return 0;
    } catch(const std::exception &error) { std::cerr << error.what() << "\n"; return 1; }
}
