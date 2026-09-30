// Inject SQLite allocator failures in a separate executable; no test switch in the shipped core.
#include "../src/core.cpp"
#include <iostream>
namespace
{
    sqlite3_mem_methods original_memory{};
    std::atomic<int> remaining{-1}, hits{0};
    bool fail_allocation()
    {
        int value = remaining.load();
        if (value < 0)
            return false;
        if (value == 0)
        {
            ++hits;
            return true;
        }
        remaining.fetch_sub(1);
        return false;
    }
    void *allocate(int size)
    {
        return fail_allocation() ? nullptr : original_memory.xMalloc(size);
    }
    void *reallocate(void *pointer, int size)
    {
        return fail_allocation() ? nullptr : original_memory.xRealloc(pointer, size);
    }
    void expect(bool value, const char *message)
    {
        if (!value)
            throw std::runtime_error(message);
    }
    std::vector<uint8_t> command(const std::string &sql)
    {
        Writer w(4096);
        w.number(1, 4);
        w.string(sql.data(), sql.size());
        w.number(uint64_t(-1), 8);
        w.number(0, 4);
        return std::move(w.bytes);
    }
    uf_completion call(uint64_t client, uint64_t db, uint32_t kind, const std::vector<uint8_t> &payload = {})
    {
        uint64_t operation = 0;
        expect(ufsqlite_submit(client, db, kind, payload.data(), payload.size(), kind == UF_CLOSE ? 0 : 1, kind == UF_CLOSE ? 0 : 4096, 5000, &operation) ==
                   UF_OK,
               "admission failed");
        uf_completion result{};
        uint32_t count = 0;
        auto deadline = Clock::now() + std::chrono::seconds(10);
        while (!count && Clock::now() < deadline)
            expect(ufsqlite_wait(client, &result, 1, 100, &count) == UF_OK, "wait failed");
        expect(count == 1 && result.operation == operation, "completion missing");
        return result;
    }
    void release(uint64_t client, uf_completion &result)
    {
        expect(ufsqlite_release_result(client, result.operation) == UF_OK, "release failed");
    }
} // namespace
int main()
{
    try
    {
        expect(sqlite3_config(SQLITE_CONFIG_GETMALLOC, &original_memory) == SQLITE_OK,
               "allocator not available");
        auto methods = original_memory;
        methods.xMalloc = allocate;
        methods.xRealloc = reallocate;
        expect(sqlite3_config(SQLITE_CONFIG_MALLOC, &methods) == SQLITE_OK, "allocator registration failed");
        auto root = std::filesystem::temp_directory_path() /
                    ("uiframe-sqlite-memory-" + std::to_string(Clock::now().time_since_epoch().count()));
        std::filesystem::create_directory(root);
        auto path = (root / "memory.sqlite").u8string();
        uint64_t client = 0;
        expect(ufsqlite_client_create(UFSQLITE_ABI, &client) == UF_OK, "client failed");
        Writer open(4096);
        open.number(0, 4);
        open.string(path.data(), path.size());
        open.number(0, 8);
        open.number(128 * MiB, 8);
        auto result = call(client, 0, UF_OPEN, open.bytes);
        expect(!result.error, "open failed");
        auto db = result.database;
        release(client, result);
        result = call(client, db, UF_EXECUTE,
                      command("CREATE TABLE receipts(id INTEGER PRIMARY KEY,value TEXT NOT NULL)"));
        expect(!result.error, "schema failed");
        release(client, result);
        int failures = 0, successes = 0;
        for (int index = 0; index < 160; ++index)
        {
            auto payload = command("INSERT INTO receipts VALUES(" + std::to_string(index) +
                                   ",hex(randomblob(256))) RETURNING value");
            remaining = index;
            result = call(client, db, UF_BATCH, payload);
            remaining = -1;
            bool committed = result.error == 0;
            if (committed)
            {
                Reader reader{result.data, size_t(result.data_size)};
                expect(reader.number(4) == 1 && reader.number(4) == 1 && reader.number(4) == 1,
                       "invalid result dimensions");
                expect(reader.number(8) == 1, "wrong affected rows");
                reader.string();
                expect(reader.number(1) == 3 && reader.string().size() == 512 && reader.left == 0,
                       "allocation failure fabricated a value");
                ++successes;
            }
            else
            {
                expect(result.data_size == 0 && result.committed == 0,
                       "allocation failure published a partial result");
                expect((result.sqlite_code & 0xff) == SQLITE_NOMEM || result.error == UF_MEMORY,
                       "unexpected allocation error");
                ++failures;
            }
            release(client, result);
            result = call(client, db, UF_QUERY,
                          command("SELECT count(*) FROM receipts WHERE id=" + std::to_string(index)));
            expect(!result.error, "database unusable after local allocation failure");
            Reader reader{result.data, size_t(result.data_size)};
            reader.number(4);
            reader.number(4);
            reader.number(4);
            reader.number(8);
            reader.string();
            expect(reader.number(1) == 1 && reader.number(8) == uint64_t(committed),
                   "receipt disagrees with commit outcome");
            release(client, result);
        }
        expect(failures > 0 && successes > 0 && hits > 0,
               "allocation injection did not cover failure and adjacent success");
        result = call(client, db, UF_CLOSE);
        expect(!result.error, "close failed");
        release(client, result);
        expect(ufsqlite_client_release(client) == UF_OK, "client leaked ownership");
        std::filesystem::remove_all(root);
        std::cout << "SQLite allocation faults: " << failures << " failures, " << successes
                  << " adjacent successes; receipt and value checks passed.\n";
        return 0;
    }
    catch (const std::exception &error)
    {
        remaining = -1;
        std::cerr << error.what() << "\n";
        return 1;
    }
}
