// Standalone fault-injection executable. It compiles the same core and pinned engine;
// no fault controls or additional engine are shipped in the runtime library.
#include "../src/core.cpp"
#include <iostream>
namespace
{
    struct FaultFile
    {
        sqlite3_file base;
        sqlite3_file *original;
        bool wal;
    };
    std::atomic<int> injected{0}, hits{0};
    sqlite3_vfs *original_vfs;
    FaultFile *file(sqlite3_file *p)
    {
        return reinterpret_cast<FaultFile *>(p);
    }
    int xClose(sqlite3_file *p)
    {
        return file(p)->original->pMethods->xClose(file(p)->original);
    }
    int xRead(sqlite3_file *p, void *b, int n, sqlite3_int64 o)
    {
        return file(p)->original->pMethods->xRead(file(p)->original, b, n, o);
    }
    int xWrite(sqlite3_file *p, const void *b, int n, sqlite3_int64 o)
    {
        int expected = 1;
        if (file(p)->wal && injected.compare_exchange_strong(expected, 0))
        {
            ++hits;
            return SQLITE_FULL;
        }
        return file(p)->original->pMethods->xWrite(file(p)->original, b, n, o);
    }
    int xTruncate(sqlite3_file *p, sqlite3_int64 n)
    {
        return file(p)->original->pMethods->xTruncate(file(p)->original, n);
    }
    int xSync(sqlite3_file *p, int f)
    {
        int expected = 2;
        if (file(p)->wal && injected.compare_exchange_strong(expected, 0))
        {
            ++hits;
            return SQLITE_IOERR_FSYNC;
        }
        return file(p)->original->pMethods->xSync(file(p)->original, f);
    }
    int xFileSize(sqlite3_file *p, sqlite3_int64 *n)
    {
        return file(p)->original->pMethods->xFileSize(file(p)->original, n);
    }
    int xLock(sqlite3_file *p, int n)
    {
        return file(p)->original->pMethods->xLock(file(p)->original, n);
    }
    int xUnlock(sqlite3_file *p, int n)
    {
        return file(p)->original->pMethods->xUnlock(file(p)->original, n);
    }
    int xCheckReservedLock(sqlite3_file *p, int *n)
    {
        return file(p)->original->pMethods->xCheckReservedLock(file(p)->original, n);
    }
    int xFileControl(sqlite3_file *p, int n, void *a)
    {
        return file(p)->original->pMethods->xFileControl(file(p)->original, n, a);
    }
    int xSectorSize(sqlite3_file *p)
    {
        return file(p)->original->pMethods->xSectorSize(file(p)->original);
    }
    int xDeviceCharacteristics(sqlite3_file *p)
    {
        return file(p)->original->pMethods->xDeviceCharacteristics(file(p)->original);
    }
    int xShmMap(sqlite3_file *p, int a, int b, int d, void volatile **v)
    {
        return file(p)->original->pMethods->xShmMap(file(p)->original, a, b, d, v);
    }
    int xShmLock(sqlite3_file *p, int a, int b, int d)
    {
        return file(p)->original->pMethods->xShmLock(file(p)->original, a, b, d);
    }
    void xShmBarrier(sqlite3_file *p)
    {
        file(p)->original->pMethods->xShmBarrier(file(p)->original);
    }
    int xShmUnmap(sqlite3_file *p, int a)
    {
        return file(p)->original->pMethods->xShmUnmap(file(p)->original, a);
    }
    int xFetch(sqlite3_file *p, sqlite3_int64 a, int b, void **v)
    {
        auto m = file(p)->original->pMethods;
        if (m->iVersion < 3 || !m->xFetch)
        {
            *v = nullptr;
            return SQLITE_OK;
        }
        return m->xFetch(file(p)->original, a, b, v);
    }
    int xUnfetch(sqlite3_file *p, sqlite3_int64 a, void *v)
    {
        auto m = file(p)->original->pMethods;
        return m->iVersion < 3 || !m->xUnfetch ? SQLITE_OK : m->xUnfetch(file(p)->original, a, v);
    }
    const sqlite3_io_methods methods = {3,
                                        xClose,
                                        xRead,
                                        xWrite,
                                        xTruncate,
                                        xSync,
                                        xFileSize,
                                        xLock,
                                        xUnlock,
                                        xCheckReservedLock,
                                        xFileControl,
                                        xSectorSize,
                                        xDeviceCharacteristics,
                                        xShmMap,
                                        xShmLock,
                                        xShmBarrier,
                                        xShmUnmap,
                                        xFetch,
                                        xUnfetch};
    int xOpen(sqlite3_vfs *, const char *name, sqlite3_file *p, int flags, int *actual)
    {
        auto f = file(p);
        f->original = reinterpret_cast<sqlite3_file *>(reinterpret_cast<char *>(p) + sizeof(FaultFile));
        f->wal = (flags & SQLITE_OPEN_WAL) != 0;
        int rc = original_vfs->xOpen(original_vfs, name, f->original, flags, actual);
        if (f->original->pMethods)
            p->pMethods = &methods;
        return rc;
    }
    void check(bool condition, const char *message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }
    std::vector<uint8_t> sql(const char *text)
    {
        Writer w(4096);
        w.number(1, 4);
        w.string(text, std::strlen(text));
        w.number(uint64_t(-1), 8);
        w.number(0, 4);
        return std::move(w.bytes);
    }
    struct Result
    {
        uf_completion value;
        std::vector<uint8_t> bytes;
    };
    Result call(uint64_t client, uint64_t db, int kind, const std::vector<uint8_t> &payload = {})
    {
        uint64_t id;
        check(ufsqlite_submit(client, db, kind, payload.data(), payload.size(), kind == UF_CLOSE ? 0 : 200, kind == UF_CLOSE ? 0 : 4096, 5000, &id) == 0,
              "submit failed");
        Result r{};
        uint32_t count = 0;
        for (int n = 0; n < 100 && !count; n++)
            check(ufsqlite_wait(client, &r.value, 1, 100, &count) == 0, "wait failed");
        check(count == 1, "No terminal result");
        if (r.value.data_size)
            r.bytes.assign(r.value.data, r.value.data + r.value.data_size);
        check(ufsqlite_release_result(client, id) == 0, "release failed");
        return r;
    }
    uint64_t open(uint64_t client, const std::string &path, int mode)
    {
        Writer w(4096);
        w.number(mode, 4);
        w.string(path.c_str(), path.size());
        w.number(32 * 1024 * 1024, 8);
        w.number(128 * 1024 * 1024, 8);
        auto r = call(client, 0, UF_OPEN, w.bytes);
        check(r.value.error == 0, r.value.message);
        return r.value.database;
    }
} // namespace
int main()
{
    try
    {
        original_vfs = sqlite3_vfs_find(nullptr);
        sqlite3_vfs wrapped = *original_vfs;
        wrapped.zName = "uiframe-test-faults";
        wrapped.szOsFile = original_vfs->szOsFile + sizeof(FaultFile);
        wrapped.xOpen = xOpen;
        check(sqlite3_vfs_register(&wrapped, 1) == SQLITE_OK, "VFS registration failed");
        auto root = std::filesystem::temp_directory_path() /
                    ("uiframe-io-faults-" + std::to_string(Clock::now().time_since_epoch().count()));
        std::filesystem::create_directory(root);
        uint64_t client;
        check(ufsqlite_client_create(UFSQLITE_ABI, &client) == 0, "client failed");
        for (int fault : {1, 2})
        {
            auto path = (root / (std::to_string(fault) + ".sqlite")).u8string();
            auto db = open(client, path, 0);
            check(
                call(client, db, UF_EXECUTE, sql("CREATE TABLE receipts(id TEXT PRIMARY KEY)")).value.error ==
                    0,
                "schema failed");
            injected = fault;
            auto result = call(client, db, UF_EXECUTE, sql("INSERT INTO receipts VALUES('operation')"));
            check(result.value.error == UF_SQL, "I/O failure reported success");
            check(result.value.committed != 1, "I/O failure marked committed");
            check(injected == 0, "Fault did not reach WAL");
            check(call(client, db, UF_CLOSE).value.error == 0, "failed context cannot close");
            db = open(client, path, 1);
            auto q = call(client, db, UF_QUERY, sql("SELECT count(*) FROM receipts"));
            check(q.value.error == 0, "Cannot inspect recovered receipts");
            Reader last{q.bytes.data() + q.bytes.size() - 8, 8};
            auto count = last.number(8);
            check(count <= 1, "Duplicate receipt");
            if (fault == 1)
                check(count == 0, "Disk-full write unexpectedly persisted");
            check(call(client, db, UF_CLOSE).value.error == 0, "recovery close failed");
        }
        check(hits == 2, "Fault coverage missing");
        check(ufsqlite_client_release(client) == 0, "client leak");
        std::filesystem::remove_all(root);
        sqlite3_vfs_unregister(&wrapped);
        std::cout << "Disk-full and WAL fsync fault checks passed.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << "\n";
        return 1;
    }
}
