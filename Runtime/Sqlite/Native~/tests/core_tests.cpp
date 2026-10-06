#include "ufsqlite.h"
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <stdexcept>
#include <thread>
#include <unordered_map>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#endif
using Bytes = std::vector<uint8_t>;
void expect(bool ok, const char *message)
{
    if (!ok)
        throw std::runtime_error(message);
}
void number(Bytes &b, uint64_t v, unsigned n)
{
    for (unsigned i = 0; i < n; ++i)
        b.push_back(uint8_t(v >> (8 * i)));
}
void text(Bytes &b, const std::string &s)
{
    number(b, s.size(), 4);
    b.insert(b.end(), s.begin(), s.end());
}
Bytes command(const std::string &sql, int64_t expected = -1)
{
    Bytes b;
    number(b, 1, 4);
    text(b, sql);
    number(b, uint64_t(expected), 8);
    number(b, 0, 4);
    return b;
}
uint64_t submit(uint64_t c, uint64_t d, uint32_t kind, const Bytes &b = {}, uint32_t rows = 200,
                uint32_t bytes = 1024 * 1024, uint32_t deadline = 5000)
{
    uint64_t id = 0;
    expect(ufsqlite_submit(c, d, kind, b.data(), b.size(), rows, bytes, deadline, &id) == 0, "submit failed");
    return id;
}
struct Client
{
    uint64_t c = 0;
    std::unordered_map<uint64_t, uf_completion> received;
    Client()
    {
        expect(ufsqlite_client_create(UFSQLITE_ABI, &c) == 0, "client failed");
    }
    uf_completion wait(uint64_t id)
    {
        auto deadline = std::chrono::steady_clock::now() + std::chrono::seconds(15);
        while (!received.count(id))
        {
            expect(std::chrono::steady_clock::now() < deadline, "wait timed out");
            uf_completion out[16];
            uint32_t count = 0;
            expect(ufsqlite_wait(c, out, 16, 100, &count) == 0, "wait failed");
            for (uint32_t i = 0; i < count; ++i)
                received.emplace(out[i].operation, out[i]);
        }
        auto out = received.at(id);
        received.erase(id);
        return out;
    }
    uf_completion done(uint64_t id, int error = 0)
    {
        auto out = wait(id);
        if (out.error != error)
        {
            std::cerr << "Operation " << id << " Actual: " << out.error << " " << out.message << " expected "
                      << error << "\n";
            throw std::runtime_error("Wrong completion");
        }
        expect(ufsqlite_release_result(c, id) == 0, "release failed");
        out.data = nullptr;
        return out;
    }
    uint64_t open(const std::string &path, int mode = 0)
    {
        Bytes b;
        number(b, mode, 4);
        text(b, path);
        number(b, 32 * 1024 * 1024, 8);
        number(b, 128 * 1024 * 1024, 8);
        return done(submit(c, 0, UF_OPEN, b)).database;
    }
    void close(uint64_t d)
    {
        done(submit(c, d, UF_CLOSE, {}, 0, 0));
    }
};
int main()
{
    try
    {
        const auto root = std::filesystem::temp_directory_path() /
                          std::filesystem::u8path(u8"uiframe-照片 😀-" +
                           std::to_string(std::chrono::steady_clock::now().time_since_epoch().count()));
        std::filesystem::create_directory(root);
        Client c;
        auto path = (root / "test.sqlite").u8string();
        auto d = c.open(path);
        auto empty = root / "empty.sqlite";
        std::ofstream(empty).close();
        Bytes empty_open;
        number(empty_open, 1, 4);
        text(empty_open, empty.u8string());
        number(empty_open, 0, 8);
        number(empty_open, 128 * 1024 * 1024, 8);
        c.done(submit(c.c, 0, UF_OPEN, empty_open), UF_STATE);
        expect(std::filesystem::file_size(empty) == 0, "empty existing database was modified");
        // Represents a native application service attaching alongside a managed client.
        // Both use one linked core and share its file-identity registry.
        Client application;
        Bytes duplicate;
        number(duplicate, 1, 4);
        text(duplicate, path);
        number(duplicate, 0, 8);
        number(duplicate, 128 * 1024 * 1024, 8);
        application.done(submit(application.c, 0, UF_OPEN, duplicate), UF_STATE);
        auto independent = application.open((root / "independent.sqlite").u8string());
        application.done(
            submit(application.c, independent, UF_EXECUTE, command("CREATE TABLE owned(id INTEGER)")));
        c.done(submit(c.c, d, UF_EXECUTE,
                      command("CREATE TABLE saves(id INTEGER PRIMARY KEY,value TEXT NOT NULL)")));
        c.done(submit(c.c, d, UF_EXECUTE, command("INSERT INTO saves VALUES(1,'original')", 1)));
        c.done(submit(c.c, d, UF_EXECUTE, command("UPDATE saves SET value='wrong'", 2)), UF_CONDITION);
        auto q = c.wait(submit(c.c, d, UF_QUERY, command("SELECT value FROM saves")));
        expect(q.error == 0 &&
                   std::string((const char *)q.data, q.data_size).find("original") != std::string::npos,
               "condition did not rollback");
        expect(ufsqlite_release_result(c.c, q.operation) == 0, "release query");
        c.done(submit(c.c, d, UF_QUERY, command("DELETE FROM saves")), UF_ARGUMENT);
        c.done(submit(c.c, d, UF_EXECUTE, command("BEGIN")), UF_SQL);
        c.done(submit(c.c, d, UF_EXECUTE, command("PRAGMA synchronous=OFF")), UF_SQL);
        c.done(submit(c.c, d, UF_EXECUTE, command("DELETE FROM saves; INSERT INTO saves VALUES(2,'bad')")),
               UF_ARGUMENT);
        c.done(submit(c.c, d, UF_BATCH,
                      command("INSERT INTO saves VALUES(2,'huge') RETURNING randomblob(5000)"), 200, 128),
               UF_RESULT_LIMIT);
        c.done(submit(c.c, d, UF_EXECUTE, command("INSERT INTO saves VALUES(2,'ok')", 1)));
        auto cancel = submit(c.c, d, UF_QUERY,
                             command("WITH RECURSIVE r(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM r WHERE "
                                     "x<100000000) SELECT sum(x) FROM r"));
        expect(ufsqlite_cancel(c.c, cancel) == 0, "cancel failed");
        c.done(cancel, UF_CANCELED);
        c.done(submit(c.c, d, UF_QUERY, command("SELECT value FROM saves")));
        c.done(submit(c.c, d, UF_QUERY,
                      command("WITH RECURSIVE r(x) AS (VALUES(0) UNION ALL SELECT x+1 FROM r WHERE "
                              "x<100000000) SELECT sum(x) FROM r"),
                      200, 1024, 1),
               UF_TIMEOUT);
        auto committed = submit(c.c, d, UF_EXECUTE, command("INSERT INTO saves VALUES(3,'committed')", 1));
        auto result = c.wait(committed);
        expect(result.error == 0 && result.committed == 1, "commit receipt");
        ufsqlite_cancel(c.c, committed);
        ufsqlite_release_result(c.c, committed);
        std::vector<uint64_t> held;
        for (int i = 0; i < 4; ++i)
            held.push_back(submit(c.c, d, UF_QUERY, command("SELECT value FROM saves")));
        uint64_t rejected;
        auto payload = command("SELECT 1");
        expect(ufsqlite_submit(c.c, d, UF_QUERY, payload.data(), payload.size(), 200, 1024 * 1024, 5000,
                               &rejected) == UF_CAPACITY,
               "result admission bound");
        for (auto id : held)
            c.done(id);
        c.done(submit(c.c, d, UF_EXECUTE, command("CREATE TABLE images(id INTEGER PRIMARY KEY,bytes BLOB)")));
        for (int n = 0; n < 12; ++n)
            c.done(
                submit(c.c, d, UF_EXECUTE, command("INSERT INTO images(bytes) VALUES(randomblob(500000))")));
        Bytes snapshot;
        auto snapshot_path = (root / "snapshot.sqlite").u8string();
        text(snapshot, snapshot_path);
        number(snapshot, 32 * 1024 * 1024, 8);
        number(snapshot, 32 * 1024 * 1024, 8);
        auto copying = submit(c.c, d, UF_SNAPSHOT, snapshot, 0, 0);
        auto reading = submit(c.c, d, UF_QUERY, command("SELECT count(*) FROM images"));
        c.done(reading);
        c.done(copying);
        auto backup = c.open(snapshot_path, 2);
        c.done(submit(c.c, backup, UF_QUERY, command("SELECT id,length(bytes) FROM images ORDER BY id")));
        c.close(backup);
        c.done(submit(c.c, d, UF_SNAPSHOT, snapshot, 0, 0), UF_STATE);
        Bytes small;
        text(small, (root / "too-small.sqlite").u8string());
        number(small, 1, 8);
        number(small, 32 * 1024 * 1024, 8);
        c.done(submit(c.c, d, UF_SNAPSHOT, small, 0, 0), UF_CAPACITY);
        expect(!std::filesystem::exists(root / "too-small.sqlite"), "failed snapshot published");
        c.done(submit(c.c, d, UF_BATCH, command("ALTER TABLE saves ADD COLUMN extra INTEGER CHECK(extra>=0)")));
        c.done(submit(c.c, d, UF_EXECUTE, command("UPDATE saves SET extra=-1")), UF_SQL);
        c.done(submit(c.c, d, UF_QUERY, command("PRAGMA quick_check")));
        auto first = submit(c.c, d, UF_QUERY, command("SELECT 1"));
        auto closing = submit(c.c, d, UF_CLOSE, {}, 0, 0);
        c.done(first);
        c.done(closing);
#ifdef _WIN32
        expect(SetFileAttributesW(std::filesystem::u8path(path).c_str(), FILE_ATTRIBUTE_READONLY) != 0,
               "cannot mark database readonly");
#endif
        d = c.open(path, 2);
        c.done(submit(c.c, d, UF_EXECUTE, command("DELETE FROM saves")), UF_STATE);
        c.done(submit(c.c, d, UF_QUERY, command("SELECT value FROM saves WHERE id=3")));
        c.close(d);
#ifdef _WIN32
        expect(SetFileAttributesW(std::filesystem::u8path(path).c_str(), FILE_ATTRIBUTE_NORMAL) != 0,
               "cannot restore database attributes");
#endif
        Bytes reopen;
        number(reopen, 0, 4);
        text(reopen, path);
        number(reopen, 0, 8);
        number(reopen, 128 * 1024 * 1024, 8);
        c.done(submit(c.c, 0, UF_OPEN, reopen), UF_STATE);
        Bytes absent;
        number(absent, 1, 4);
        text(absent, (root / "absent.sqlite").u8string());
        number(absent, 0, 8);
        number(absent, 128 * 1024 * 1024, 8);
        c.done(submit(c.c, 0, UF_OPEN, absent), UF_IO);
        expect(!std::filesystem::exists(root / "absent.sqlite"), "missing database recreated");
        uf_diagnostics stats{};
        stats.size = sizeof(stats);
        stats.abi = UFSQLITE_ABI;
        expect(ufsqlite_get_diagnostics(&stats) == 0 && stats.operations == 0 && stats.databases == 1 &&
                   stats.reserved_bytes == 0,
               "leaked core ownership");
        expect(ufsqlite_client_release(c.c) == 0, "client retained ownership");
        application.done(
            submit(application.c, independent, UF_EXECUTE, command("INSERT INTO owned VALUES(1)", 1)));
        application.close(independent);
        expect(ufsqlite_client_release(application.c) == 0, "native client ownership leaked");
        expect(ufsqlite_get_diagnostics(&stats) == 0 && stats.databases == 0 && stats.operations == 0 &&
                   stats.reserved_bytes == 0,
               "independent service leaked after managed client detached");
        // Last release must stop the workers; the next lifetime must start cleanly.
        for (int i = 0; i < 8; ++i)
        {
            Client next;
            auto reopened = next.open(path, 2);
            next.done(submit(next.c, reopened, UF_QUERY, command("SELECT value FROM saves")));
            next.close(reopened);
            expect(ufsqlite_client_release(next.c) == 0, "restarted client leaked ownership");
        }
        std::filesystem::remove_all(root);
        std::cout << "Native contract checks passed (transactions, limits, cancellation, readonly, close, "
                     "reopen).\n";
        return 0;
    }
    catch (const std::exception &e)
    {
        std::cerr << e.what() << "\n";
        return 1;
    }
}
