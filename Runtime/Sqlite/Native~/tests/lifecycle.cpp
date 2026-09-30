#include "../src/core.cpp"
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
