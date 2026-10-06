#include "sqlite3.h"
#include "ufsqlite.h"
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstring>
#include <list>
#include <filesystem>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>
#ifdef _WIN32
#include <windows.h>
#else
#include <fcntl.h>
#include <sys/file.h>
#include <sys/stat.h>
#include <unistd.h>
#endif
namespace
{
    using Clock = std::chrono::steady_clock;
    constexpr uint64_t MiB = 1024 * 1024, GlobalBudget = 16 * MiB, ParameterBudget = 4 * MiB,
                       ResultBudget = 4 * MiB;
    struct Failure : std::exception
    {
        int code, sql;
        const char *what() const noexcept override
        {
            return message;
        }
        char message[256]{};
        Failure(int c, const char *m, int s = 0) : code(c), sql(s)
        {
            std::strncpy(message, m, 255);
        }
    };
    void require(bool condition, int code, const char *message)
    {
        if (!condition)
            throw Failure(code, message);
    }
    struct Reader
    {
        const uint8_t *p;
        size_t left;
        uint64_t number(unsigned n)
        {
            require(left >= n, UF_ARGUMENT, "Truncated request");
            uint64_t v = 0;
            for (unsigned i = 0; i < n; ++i)
                v |= uint64_t(p[i]) << (8 * i);
            p += n;
            left -= n;
            return v;
        }
        std::string string()
        {
            auto n = number(4);
            require(n <= left, UF_ARGUMENT, "Invalid string length");
            std::string v((const char *)p, size_t(n));
            p += n;
            left -= n;
            return v;
        }
    };
    struct Writer
    {
        std::vector<uint8_t> bytes;
        size_t limit;
        explicit Writer(size_t n) : limit(n)
        {
            bytes.reserve(n);
        }
        void space(size_t n)
        {
            require(n <= limit && bytes.size() <= limit - n, UF_RESULT_LIMIT, "Result budget exceeded");
        }
        void number(uint64_t v, unsigned n)
        {
            space(n);
            for (unsigned i = 0; i < n; ++i)
                bytes.push_back(uint8_t(v >> (8 * i)));
        }
        void raw(const void *p, size_t n)
        {
            space(n);
            if (n)
            {
                auto b = (const uint8_t *)p;
                bytes.insert(bytes.end(), b, b + n);
            }
        }
        void string(const char *p, size_t n)
        {
            number(n, 4);
            raw(p, n);
        }
        void patch(size_t at, uint64_t v, unsigned n)
        {
            for (unsigned i = 0; i < n; ++i)
                bytes[at + i] = uint8_t(v >> (8 * i));
        }
    };
    struct Connection
    {
        sqlite3 *db = nullptr;
        bool internal = false;
        struct Cached
        {
            std::string sql;
            sqlite3_stmt *stmt = nullptr;
            uint64_t age = 0;
        };
        std::array<Cached, 64> cache{};
        uint64_t cache_clock = 0;
        void clear()
        {
            for (auto &c : cache)
            {
                sqlite3_finalize(c.stmt);
                c.stmt = nullptr;
                std::string().swap(c.sql);
            }
        }
        int close()
        {
            clear();
            if (!db)
                return SQLITE_OK;
            auto p = db;
            db = nullptr;
            return sqlite3_close_v2(p);
        }
        ~Connection()
        {
            close();
        }
    };
    // Path ownership exists before the main database does. Keep the lock inode
    // permanently so concurrent openers cannot lock different generations of it.
    struct PathOwner
    {
#ifdef _WIN32
        HANDLE handle = INVALID_HANDLE_VALUE;
#else
        int handle = -1;
#endif
        ~PathOwner() { close(); }
        void close()
        {
#ifdef _WIN32
            if (handle != INVALID_HANDLE_VALUE) { CloseHandle(handle); handle = INVALID_HANDLE_VALUE; }
#else
            if (handle >= 0) { ::close(handle); handle = -1; }
#endif
        }
        void acquire(const std::string &path)
        {
            auto lock_path = path + ".ufsqlite-owner";
#ifdef _WIN32
            handle = CreateFileW(std::filesystem::u8path(lock_path).c_str(), GENERIC_READ | GENERIC_WRITE,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
            require(handle != INVALID_HANDLE_VALUE, UF_IO, "Cannot open ownership lock");
            OVERLAPPED lock{};
            require(LockFileEx(handle, LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY, 0, 1, 0, &lock),
                    UF_STATE, "Database path already owned");
#else
            handle = ::open(lock_path.c_str(), O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW, 0600);
            require(handle >= 0, UF_IO, "Cannot open ownership lock");
            require(flock(handle, LOCK_EX | LOCK_NB) == 0, UF_STATE, "Database path already owned");
#endif
        }
    };
    void require_new_target(const std::string &path)
    {
        for (const auto &entry : {path, path + "-wal", path + "-shm", path + "-journal"})
            require(std::filesystem::symlink_status(std::filesystem::u8path(entry)).type() == std::filesystem::file_type::not_found,
                    UF_STATE, "New database target has an existing file or sidecar");
    }
    struct Database
    {
        uint64_t id, client;
        std::string path, identity;
        Connection write, read;
        PathOwner path_owner;
        std::atomic<bool> ready{false}, fault{false};
        bool closing = false, write_busy = false, read_busy = false, snapshot_busy = false, readonly = false;
        bool close_reserved = false;
        uint64_t parameters = 0, results = 0, count = 0;
        uint64_t min_free = 0, max_wal = 0;
#ifdef _WIN32
        HANDLE owner = INVALID_HANDLE_VALUE;
#else
        int owner = -1;
#endif
        ~Database()
        {
            unlock();
        }
        void unlock()
        {
            close_file();
            path_owner.close();
        }
        void close_file()
        {
#ifdef _WIN32
            if (owner != INVALID_HANDLE_VALUE)
            {
                CloseHandle(owner);
                owner = INVALID_HANDLE_VALUE;
            }
#else
            if (owner >= 0)
            {
                ::close(owner);
                owner = -1;
            }
#endif
        }
    };
    struct Snapshot
    {
        Connection source, target;
        PathOwner destination_owner;
        sqlite3_backup *backup = nullptr;
        std::string temporary, destination;
        uint64_t max_bytes = 0, max_wal = 0;
        bool published = false;
        ~Snapshot()
        {
            if (backup)
                sqlite3_backup_finish(backup);
            source.close();
            target.close();
            if (!temporary.empty())
            {
                std::error_code ec;
                std::filesystem::remove(std::filesystem::u8path(temporary), ec);
            }
        }
    };
    struct Job
    {
        uint64_t id, client;
        std::shared_ptr<Database> database;
        uint32_t kind, rows, result_limit;
        std::vector<uint8_t> input, output;
        uint64_t charge = 0;
        Clock::time_point deadline;
        Clock::time_point enqueued;
        uint64_t execution_ns = 0, commit_ns = 0, cache_hits = 0, cache_misses = 0;
        std::atomic<bool> cancel{false};
        bool submitted = false, done = false, claimed = false;
        uf_completion completion{};
        std::unique_ptr<Snapshot> snapshot;
        Job *completion_next = nullptr;
    };
    struct Client
    {
        uint64_t operations = 0, waiters = 0;
        bool stopping = false;
        Job *first = nullptr;
        Job *last = nullptr;
    };
    struct Engine
    {
        std::mutex lifecycle, mutex;
        std::condition_variable work, completion;
        std::unordered_map<uint64_t, Client> clients;
        std::unordered_map<uint64_t, std::shared_ptr<Job>> jobs;
        std::unordered_map<uint64_t, std::shared_ptr<Database>> databases;
        std::unordered_map<std::string, uint64_t> owners;
        std::list<std::shared_ptr<Job>> queue;
        uint64_t next = 1, reserved = 0, total_completed = 0;
        uint64_t active = 0, queue_wait_ns = 0, max_queue_wait_ns = 0;
        uint64_t execution_ns = 0, max_execution_ns = 0, commit_ns = 0, commits = 0;
        uint64_t cache_hits = 0, cache_misses = 0, errors = 0, canceled = 0, timeouts = 0;
        bool stopping = false, snapshot_slot = false;
        std::thread workers[2];
        Engine() = default;
        ~Engine();
        void start();
        void stop();
        void run(bool writer);
    };
    Engine &engine()
    {
        static Engine e;
        return e;
    }
    int progress(void *context)
    {
        auto j = (Job *)context;
        return j->cancel || Clock::now() >= j->deadline;
    }
    int busy(void *context, int count)
    {
        auto j = (Job *)context;
        if (progress(j) || count >= 10)
            return 0;
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
        return 1;
    }
    void check_cancel(Job &j)
    {
        if (j.cancel)
            throw Failure(UF_CANCELED, "Operation canceled");
        if (Clock::now() >= j.deadline)
            throw Failure(UF_TIMEOUT, "Operation deadline exceeded");
    }
    void sql_check(int rc, sqlite3 *db, Job *j = nullptr)
    {
        if (rc == SQLITE_OK || rc == SQLITE_DONE || rc == SQLITE_ROW)
            return;
        if (j && (rc == SQLITE_INTERRUPT || rc == SQLITE_BUSY))
        {
            check_cancel(*j);
        }
        throw Failure(UF_SQL, db ? sqlite3_errmsg(db) : "SQLite resource release failed",
                      db ? sqlite3_extended_errcode(db) : rc);
    }
    int authorize(void *p, int action, const char *a, const char *b, const char *, const char *)
    {
        if (((Connection *)p)->internal)
            return SQLITE_OK;
        if (action == SQLITE_TRANSACTION || action == SQLITE_SAVEPOINT || action == SQLITE_ATTACH ||
            action == SQLITE_DETACH)
            return SQLITE_DENY;
        // quick_check is read-only; SQLite also invokes it to validate ALTER TABLE.
        if (action == SQLITE_PRAGMA &&
            (!a || (std::strcmp(a, "application_id") && std::strcmp(a, "user_version") && std::strcmp(a, "quick_check"))))
            return SQLITE_DENY;
        if (action == SQLITE_FUNCTION && b &&
            (!std::strcmp(b, "load_extension") || !std::strcmp(b, "readfile") ||
             !std::strcmp(b, "writefile")))
            return SQLITE_DENY;
        return SQLITE_OK;
    }
    void internal(Connection &c, const char *sql, Job *j = nullptr)
    {
        c.internal = true;
        int rc = sqlite3_exec(c.db, sql, nullptr, nullptr, nullptr);
        c.internal = false;
        sql_check(rc, c.db, j);
    }
    void configure(Connection &c, const std::string &path, bool readonly, Job &job)
    {
        int rc = sqlite3_open_v2(path.c_str(), &c.db,
                                 readonly ? SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX
                                          : SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOMUTEX,
                                 nullptr);
        sql_check(rc, c.db, &job);
        sqlite3_extended_result_codes(c.db, 1);
        sqlite3_progress_handler(c.db, 1000, progress, &job);
        sqlite3_busy_handler(c.db, busy, &job);
        sqlite3_limit(c.db, SQLITE_LIMIT_LENGTH, int(MiB));
        sqlite3_limit(c.db, SQLITE_LIMIT_SQL_LENGTH, int(MiB));
        sqlite3_limit(c.db, SQLITE_LIMIT_VARIABLE_NUMBER, 1000);
        sqlite3_limit(c.db, SQLITE_LIMIT_COLUMN, 512);
        sqlite3_db_config(c.db, SQLITE_DBCONFIG_DEFENSIVE, 1, nullptr);
        sqlite3_set_authorizer(c.db, authorize, &c);
        internal(c,
                 "PRAGMA foreign_keys=ON; PRAGMA trusted_schema=OFF; PRAGMA cache_size=-4096; PRAGMA "
                 "temp_store=FILE;",
                 &job);
        if (readonly)
            internal(c, "PRAGMA query_only=ON;", &job);
        else
        {
            internal(c,
                     "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA fullfsync=ON; "
                     "PRAGMA checkpoint_fullfsync=ON; PRAGMA wal_autocheckpoint=1000;",
                     &job);
            sqlite3_stmt *s = nullptr;
            c.internal = true;
            rc = sqlite3_prepare_v2(c.db, "PRAGMA journal_mode", -1, &s, nullptr);
            c.internal = false;
            sql_check(rc, c.db, &job);
            rc = sqlite3_step(s);
            const auto *mode = rc == SQLITE_ROW ? sqlite3_column_text(s, 0) : nullptr;
            bool wal = mode && std::strcmp((const char *)mode, "wal") == 0;
            sqlite3_finalize(s);
            require(wal, UF_STATE, "WAL journal mode unavailable");
        }
    }
    void open_database(Job &j)
    {
        check_cancel(j);
        Reader r{j.input.data(), j.input.size()};
        auto mode = r.number(4);
        auto path = r.string();
        auto min_free = r.number(8), max_wal = r.number(8);
        require(mode <= 2 && r.left == 0 && path.find('\0') == std::string::npos &&
                    std::filesystem::u8path(path).is_absolute() && max_wal > 0,
                UF_ARGUMENT, "Invalid open options");
        auto d = j.database;
        d->readonly = mode == 2;
        d->min_free = min_free;
        d->max_wal = max_wal;
        bool created = false;
        try
        {
            auto absolute = std::filesystem::u8path(path);
            path = (mode == 0 ? std::filesystem::canonical(absolute.parent_path()) / absolute.filename()
                              : std::filesystem::canonical(absolute)).u8string();
            if (!d->readonly)
                d->path_owner.acquire(path);
            if (mode == 0)
                require_new_target(path);
#ifdef _WIN32
            d->owner = CreateFileW(std::filesystem::u8path(path).c_str(),
                                   d->readonly ? GENERIC_READ : GENERIC_READ | GENERIC_WRITE,
                                   FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                                   mode == 0 ? CREATE_NEW : OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
            require(d->owner != INVALID_HANDLE_VALUE, UF_IO, "Cannot open database owner");
            created = mode == 0;
            if (!d->readonly)
            {
                // Windows byte locks also block I/O. Keep the owner byte beyond SQLite's
                // maximum database size, away from its data and reserved locking page.
                OVERLAPPED lock{};
                lock.Offset = 0xfffffffe;
                lock.OffsetHigh = 0x7fffffff;
                if (!LockFileEx(d->owner, LOCKFILE_EXCLUSIVE_LOCK | LOCKFILE_FAIL_IMMEDIATELY,
                                0, 1, 0, &lock))
                    throw Failure(GetLastError() == ERROR_LOCK_VIOLATION ? UF_STATE : UF_IO,
                                  "Cannot acquire database ownership");
            }
            BY_HANDLE_FILE_INFORMATION f{};
            require(GetFileInformationByHandle(d->owner, &f), UF_IO, "Cannot identify database");
            require(mode == 0 || f.nFileSizeHigh != 0 || f.nFileSizeLow != 0, UF_STATE,
                    "Existing database is empty");
            d->identity = std::to_string(f.dwVolumeSerialNumber) + ":" + std::to_string(f.nFileIndexHigh) +
                          ":" + std::to_string(f.nFileIndexLow);
#else
            // Opening and closing an extra descriptor for an already owned database
            // would drop this process's POSIX record locks. Identify existing files with stat.
            if (mode == 0)
            {
                d->owner = ::open(path.c_str(), O_CREAT | O_EXCL | O_RDWR | O_CLOEXEC, 0600);
                require(d->owner >= 0, UF_IO, "Cannot create database");
                created = true;
                ::close(d->owner);
                d->owner = -1;
            }
            struct stat s{};
            require(stat(path.c_str(), &s) == 0, UF_IO, "Cannot identify database");
            require(mode == 0 || s.st_size > 0, UF_STATE, "Existing database is empty");
            d->identity = std::to_string(s.st_dev) + ":" + std::to_string(s.st_ino);
#endif
            d->path = std::filesystem::canonical(std::filesystem::u8path(path)).u8string();
            {
                auto &e = engine();
                std::lock_guard<std::mutex> l(e.mutex);
                require(!e.owners.count(d->identity), UF_STATE, "Database already owned");
                e.owners.emplace(d->identity, d->id);
            }
            if (!d->readonly)
                configure(d->write, d->path, false, j);
            configure(d->read, d->path, true, j);
            internal(d->read, "SELECT count(*) FROM sqlite_schema;", &j);
            check_cancel(j);
            for (auto c : {&d->write, &d->read})
                if (c->db)
                {
                    sqlite3_progress_handler(c->db, 0, nullptr, nullptr);
                    sqlite3_busy_handler(c->db, nullptr, nullptr);
                }
            d->ready = true;
        }
        catch (...)
        {
            int write_error = d->write.close(), read_error = d->read.close();
            j.completion.reserved = write_error != SQLITE_OK ? write_error : read_error;
            d->close_file();
            auto &e = engine();
            {
                std::lock_guard<std::mutex> l(e.mutex);
                auto it = e.owners.find(d->identity);
                if (it != e.owners.end() && it->second == d->id)
                    e.owners.erase(it);
            }
            if (created)
            {
                for (const auto &owned : {path, path + "-wal", path + "-shm", path + "-journal"})
                {
                    std::error_code ec;
                    std::filesystem::remove(std::filesystem::u8path(owned), ec);
                    if (ec && !j.completion.reserved)
                        j.completion.reserved = SQLITE_IOERR_DELETE;
                }
            }
            d->path_owner.close();
            throw;
        }
    }
    struct Statement
    {
        Connection &c;
        std::string key;
        sqlite3_stmt *value = nullptr;
        bool valid = false;
        Statement(Connection &connection, std::string sql, Job &job) : c(connection), key(std::move(sql))
        {
            for (auto i = c.cache.begin(); i != c.cache.end(); ++i)
                if (i->stmt && i->sql == key)
                {
                    value = i->stmt;
                    i->stmt = nullptr;
                    ++job.cache_hits;
                    return;
                }
            ++job.cache_misses;
            const char *tail = nullptr;
            sql_check(sqlite3_prepare_v3(c.db, key.c_str(), int(key.size() + 1), SQLITE_PREPARE_PERSISTENT,
                                         &value, &tail),
                      c.db);
            try
            {
                require(value != nullptr, UF_ARGUMENT, "Empty SQL");
                // Let SQLite parse any trailing text; comments are accepted, extra statements rejected.
                sqlite3_stmt *extra = nullptr;
                int rc = sqlite3_prepare_v2(c.db, tail, -1, &extra, nullptr);
                if (extra)
                {
                    sqlite3_finalize(extra);
                    throw Failure(UF_ARGUMENT, "Only one SQL statement per command");
                }
                sql_check(rc, c.db);
            }
            catch (...)
            {
                sqlite3_finalize(value);
                value = nullptr;
                throw;
            }
        }
        ~Statement()
        {
            if (!value)
                return;
            sqlite3_reset(value);
            sqlite3_clear_bindings(value);
            if (valid && key.size() < 16384 &&
                sqlite3_stmt_status(value, SQLITE_STMTSTATUS_MEMUSED, 0) < 65536)
            {
                auto slot = c.cache.begin();
                for (auto i = c.cache.begin(); i != c.cache.end(); ++i)
                {
                    if (!i->stmt)
                    {
                        slot = i;
                        break;
                    }
                    if (i->age < slot->age)
                        slot = i;
                }
                sqlite3_finalize(slot->stmt);
                slot->sql = std::move(key);
                slot->stmt = value;
                slot->age = ++c.cache_clock;
                return;
            }
            sqlite3_finalize(value);
        }
    };
    void bind(Reader &r, sqlite3_stmt *s, sqlite3 *db)
    {
        auto count = r.number(4);
        require(count == uint64_t(sqlite3_bind_parameter_count(s)), UF_ARGUMENT, "Parameter count mismatch");
        for (uint64_t i = 1; i <= count; ++i)
        {
            auto type = r.number(1);
            int rc;
            if (type == 0)
                rc = sqlite3_bind_null(s, int(i));
            else if (type == 1)
                rc = sqlite3_bind_int64(s, int(i), int64_t(r.number(8)));
            else if (type == 2)
            {
                uint64_t bits = r.number(8);
                double d;
                std::memcpy(&d, &bits, 8);
                rc = sqlite3_bind_double(s, int(i), d);
            }
            else
            {
                require(type == 3 || type == 4, UF_ARGUMENT, "Unknown parameter type");
                auto n = r.number(4);
                require(n <= r.left && n <= MiB, UF_ARGUMENT, "Invalid value length");
                static const uint8_t empty = 0;
                const auto data = n ? r.p : &empty;
                rc = type == 3 ? sqlite3_bind_text(s, int(i), (const char *)data, int(n), SQLITE_STATIC)
                               : sqlite3_bind_blob(s, int(i), data, int(n), SQLITE_STATIC);
                r.p += n;
                r.left -= n;
            }
            sql_check(rc, db);
        }
    }
    uint64_t file_bytes(const std::string &path)
    {
        std::error_code ec;
        auto size = std::filesystem::file_size(std::filesystem::u8path(path), ec);
        if (ec == std::errc::no_such_file_or_directory)
            return 0;
        require(!ec, UF_IO, "Cannot inspect database file size");
        return size;
    }
    void check_space(Database &d)
    {
        require(file_bytes(d.path + "-wal") < d.max_wal, UF_CAPACITY,
                "WAL high water reached; checkpoint or close before further writes");
        require(std::filesystem::space(std::filesystem::u8path(d.path)).available >= d.min_free, UF_CAPACITY,
                "Free disk space below configured write reserve");
    }
    void maintenance(Job &j)
    {
        auto d = j.database;
        require(!d->fault, UF_FAULTED, "Database context faulted");
        check_cancel(j);
        int log = 0, checkpointed = 0;
        bool blocked = false;
        if (j.kind == UF_CHECKPOINT)
        {
            require(d->write.db, UF_STATE, "Read-only database cannot checkpoint");
            Reader r{j.input.data(), j.input.size()};
            auto mode = r.number(4);
            require(mode <= 1 && r.left == 0, UF_ARGUMENT, "Invalid checkpoint mode");
            // No busy handler: a reader still holding pages is an observable incomplete
            // checkpoint, never a reason to wait or repeat the maintenance operation.
            int rc = sqlite3_wal_checkpoint_v2(
                d->write.db, "main", mode == 0 ? SQLITE_CHECKPOINT_PASSIVE : SQLITE_CHECKPOINT_TRUNCATE, &log,
                &checkpointed);
            blocked = rc == SQLITE_BUSY || (log >= 0 && checkpointed < log);
            if (rc != SQLITE_BUSY)
                sql_check(rc, d->write.db);
        }
        else
            require(j.input.empty(), UF_ARGUMENT, "Storage query takes no payload");
        Writer w(j.result_limit);
        w.number(1, 4);
        w.number(6, 4);
        w.number(1, 4);
        w.number(0, 8);
        for (auto name :
             {"database_bytes", "wal_bytes", "available_bytes", "log_pages", "checkpointed_pages", "blocked"})
            w.string(name, std::strlen(name));
        uint64_t values[] = {
            file_bytes(d->path), file_bytes(d->path + "-wal"), std::filesystem::space(std::filesystem::u8path(d->path)).available,
            uint64_t(log),       uint64_t(checkpointed),       uint64_t(blocked)};
        for (auto value : values)
        {
            w.number(1, 1);
            w.number(value, 8);
        }
        j.output = std::move(w.bytes);
    }
    bool read_job(uint32_t kind)
    {
        return kind == UF_QUERY || kind == UF_STORAGE;
    }
    void execute(Job &j)
    {
        auto d = j.database;
        require(!d->fault, UF_FAULTED, "Database context faulted");
        auto &c = j.kind == UF_QUERY ? d->read : d->write;
        require(c.db != nullptr, UF_STATE, "Read-only database cannot execute writes");
        sqlite3_progress_handler(c.db, 1000, progress, &j);
        sqlite3_busy_handler(c.db, busy, &j);
        bool transaction = false, committing = false;
        try
        {
            check_cancel(j);
            Reader r{j.input.data(), j.input.size()};
            auto count = r.number(4);
            require(count > 0 && count <= 200 && (j.kind == UF_BATCH || count == 1), UF_ARGUMENT,
                    "Invalid command count");
            Writer w(j.result_limit);
            w.number(count, 4);
            if (j.kind != UF_QUERY)
            {
                check_space(*d);
                internal(c, "BEGIN IMMEDIATE", &j);
                transaction = true;
            }
            uint32_t total_rows = 0;
            for (uint64_t n = 0; n < count; ++n)
            {
                check_cancel(j);
                auto sql = r.string();
                require(sql.find('\0') == std::string::npos, UF_ARGUMENT, "NUL in SQL");
                auto expected = int64_t(r.number(8));
                j.completion.phase = UF_PHASE_PREPARE;
                Statement s(c, std::move(sql), j);
                bind(r, s.value, c.db);
                require(j.kind != UF_QUERY || sqlite3_stmt_readonly(s.value), UF_ARGUMENT,
                        "Query requires read-only SQL");
                auto changes_before = sqlite3_total_changes64(c.db);
                require(j.kind != UF_EXECUTE || sqlite3_column_count(s.value) == 0, UF_ARGUMENT,
                        "Execute does not accept returned rows");
                j.completion.phase = UF_PHASE_EXECUTE;
                int rc = sqlite3_step(s.value);
                sql_check(rc, c.db, &j);
                // sqlite3_step may reprepare a cached statement after a schema change.
                auto cols = sqlite3_column_count(s.value);
                w.number(cols, 4);
                auto row_at = w.bytes.size();
                w.number(0, 4);
                auto changes_at = w.bytes.size();
                w.number(0, 8);
                for (int i = 0; i < cols; ++i)
                {
                    auto name = sqlite3_column_name(s.value, i);
                    if (!name)
                        throw Failure(UF_MEMORY, "Cannot allocate result column metadata", SQLITE_NOMEM);
                    w.string(name, std::strlen(name));
                }
                uint32_t rows = 0;
                while (rc == SQLITE_ROW)
                {
                    check_cancel(j);
                    require(++total_rows <= j.rows, UF_RESULT_LIMIT, "Row limit exceeded");
                    ++rows;
                    for (int i = 0; i < cols; ++i)
                    {
                        int type = sqlite3_column_type(s.value, i);
                        if (type == SQLITE_NULL)
                            w.number(0, 1);
                        else if (type == SQLITE_INTEGER)
                        {
                            w.number(1, 1);
                            w.number(sqlite3_column_int64(s.value, i), 8);
                        }
                        else if (type == SQLITE_FLOAT)
                        {
                            w.number(2, 1);
                            double f = sqlite3_column_double(s.value, i);
                            uint64_t bits;
                            std::memcpy(&bits, &f, 8);
                            w.number(bits, 8);
                        }
                        else
                        {
                            w.number(type == SQLITE_TEXT ? 3 : 4, 1);
                            auto data = type == SQLITE_TEXT ? (const void *)sqlite3_column_text(s.value, i)
                                                            : sqlite3_column_blob(s.value, i);
                            auto bytes = sqlite3_column_bytes(s.value, i);
                            if ((!data && type == SQLITE_TEXT) ||
                                (!data && sqlite3_errcode(c.db) == SQLITE_NOMEM))
                                throw Failure(UF_MEMORY, "Cannot allocate result value", SQLITE_NOMEM);
                            w.number(bytes, 4);
                            w.raw(data, size_t(bytes));
                        }
                    }
                    rc = sqlite3_step(s.value);
                }
                sql_check(rc, c.db, &j);
                auto changes = sqlite3_total_changes64(c.db) == changes_before ? 0 : sqlite3_changes64(c.db);
                require(j.kind != UF_EXECUTE || changes <= INT32_MAX, UF_RESULT_LIMIT,
                        "Affected-row count exceeds Execute result range");
                require(expected < 0 || changes == expected, UF_CONDITION, "Affected-row condition failed");
                w.patch(row_at, rows, 4);
                w.patch(changes_at, changes, 8);
                s.valid = true;
            }
            require(r.left == 0, UF_ARGUMENT, "Trailing request bytes");
            check_cancel(j);
            j.output = std::move(w.bytes);
            if (transaction)
            {
                committing = true;
                j.completion.phase = UF_PHASE_COMMIT;
                auto started = Clock::now();
                try { internal(c, "COMMIT", &j); }
                catch (...)
                {
                    j.commit_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - started).count();
                    throw;
                }
                j.commit_ns = std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - started).count();
                transaction = false;
                j.completion.committed = 1;
            }
            sqlite3_progress_handler(c.db, 0, nullptr, nullptr);
            sqlite3_busy_handler(c.db, nullptr, nullptr);
        }
        catch (...)
        {
            sqlite3_progress_handler(c.db, 0, nullptr, nullptr);
            sqlite3_busy_handler(c.db, nullptr, nullptr);
            int code = sqlite3_extended_errcode(c.db) & 0xff;
            if (code == SQLITE_CORRUPT || code == SQLITE_NOTADB)
                d->fault = true;
            if (transaction)
            {
                if (sqlite3_get_autocommit(c.db))
                {
                    if (committing)
                    {
                        d->fault = true;
                        j.completion.committed = -1;
                    }
                }
                else
                {
                    c.internal = true;
                    int rc = sqlite3_exec(c.db, "ROLLBACK", nullptr, nullptr, nullptr);
                    c.internal = false;
                    if (rc != SQLITE_OK || !sqlite3_get_autocommit(c.db))
                    {
                        d->fault = true;
                        j.completion.reserved = rc == SQLITE_OK ? SQLITE_INTERNAL : rc;
                    }
                }
            }
            throw;
        }
    }
    int close_snapshot(Job &j)
    {
        if (!j.snapshot)
            return SQLITE_OK;
        auto &s = *j.snapshot;
        int error = SQLITE_OK;
        if (s.backup)
        {
            error = sqlite3_backup_finish(s.backup);
            s.backup = nullptr;
        }
        int rc = s.source.close();
        if (error == SQLITE_OK)
            error = rc;
        rc = s.target.close();
        if (error == SQLITE_OK)
            error = rc;
        if (!s.temporary.empty())
        {
            std::error_code ec;
            std::filesystem::remove(std::filesystem::u8path(s.temporary), ec);
            if (ec && error == SQLITE_OK)
                error = SQLITE_IOERR_DELETE;
            s.temporary.clear();
        }
        j.snapshot.reset();
        return error;
    }
    uint64_t pragma_integer(Connection &c, const char *sql)
    {
        sqlite3_stmt *statement = nullptr;
        c.internal = true;
        int rc = sqlite3_prepare_v2(c.db, sql, -1, &statement, nullptr);
        c.internal = false;
        sql_check(rc, c.db);
        rc = sqlite3_step(statement);
        auto value = rc == SQLITE_ROW ? sqlite3_column_int64(statement, 0) : 0;
        sqlite3_finalize(statement);
        sql_check(rc, c.db);
        return uint64_t(value);
    }
    bool snapshot_step(Job &j)
    {
        check_cancel(j);
        require(!j.database->fault, UF_FAULTED, "Source database faulted");
        if (!j.snapshot)
        {
            Reader r{j.input.data(), j.input.size()};
            auto destination = r.string();
            auto max_bytes = r.number(8), max_wal = r.number(8);
            require(r.left == 0 && max_bytes > 0 && max_wal > 0 &&
                        destination.find('\0') == std::string::npos &&
                        std::filesystem::u8path(destination).is_absolute(),
                    UF_ARGUMENT, "Invalid snapshot options");
            j.snapshot = std::make_unique<Snapshot>();
            auto &s = *j.snapshot;
            auto absolute = std::filesystem::u8path(destination);
            destination = (std::filesystem::canonical(absolute.parent_path()) / absolute.filename()).u8string();
            s.destination = destination;
            s.destination_owner.acquire(destination);
            require_new_target(destination);
            s.max_bytes = max_bytes;
            s.max_wal = max_wal;
            configure(s.source, j.database->path, true, j);
            internal(s.source, "BEGIN; SELECT count(*) FROM sqlite_schema;", &j);
            auto page_count = pragma_integer(s.source, "PRAGMA page_count"),
                 page_size = pragma_integer(s.source, "PRAGMA page_size");
            require(page_size > 0 && page_count <= max_bytes / page_size, UF_CAPACITY,
                    "Snapshot exceeds temporary disk budget");
            require(std::filesystem::space(std::filesystem::u8path(destination).parent_path()).available >=
                        page_count * page_size,
                    UF_CAPACITY, "Insufficient snapshot space");
            auto temporary = destination + ".ufsqlite-" + std::to_string(j.id) + ".partial";
            require_new_target(temporary);
#ifdef _WIN32
            auto file = CreateFileW(std::filesystem::u8path(temporary).c_str(), GENERIC_READ | GENERIC_WRITE, 0,
                                    nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr);
            require(file != INVALID_HANDLE_VALUE, UF_IO, "Cannot create snapshot temporary file");
            CloseHandle(file);
#else
            auto fd = ::open(temporary.c_str(), O_RDWR | O_CREAT | O_EXCL | O_CLOEXEC, 0600);
            require(fd >= 0, UF_IO, "Cannot create snapshot temporary file");
            ::close(fd);
#endif
            s.temporary = std::move(temporary);
            sql_check(sqlite3_open_v2(s.temporary.c_str(), &s.target.db,
                                      SQLITE_OPEN_READWRITE | SQLITE_OPEN_NOMUTEX, nullptr),
                      s.target.db);
            sqlite3_progress_handler(s.target.db, 1000, progress, &j);
            sqlite3_busy_handler(s.target.db, busy, &j);
            internal(s.target,
                     "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; "
                     "PRAGMA fullfsync=ON; PRAGMA cache_size=-4096;",
                     &j);
            s.backup = sqlite3_backup_init(s.target.db, "main", s.source.db, "main");
            require(s.backup != nullptr, UF_SQL, "Cannot initialize snapshot");
        }
        auto &s = *j.snapshot;
        std::error_code ec;
        auto wal_bytes = std::filesystem::file_size(std::filesystem::u8path(j.database->path + "-wal"), ec);
        require(!ec || ec == std::errc::no_such_file_or_directory, UF_IO, "Cannot inspect snapshot WAL size");
        if (!ec)
            require(wal_bytes <= s.max_wal, UF_CAPACITY, "Snapshot WAL budget exceeded");
        int rc = sqlite3_backup_step(s.backup, 128);
        if (rc == SQLITE_OK)
            return false;
        sql_check(rc, s.target.db, &j);
        require(rc == SQLITE_DONE, UF_SQL, "Snapshot incomplete");
        rc = sqlite3_backup_finish(s.backup);
        s.backup = nullptr;
        sql_check(rc, s.target.db, &j);
        sqlite3_stmt *check = nullptr;
        sql_check(sqlite3_prepare_v2(s.target.db, "PRAGMA quick_check", -1, &check, nullptr), s.target.db);
        rc = sqlite3_step(check);
        const auto *status = rc == SQLITE_ROW ? sqlite3_column_text(check, 0) : nullptr;
        bool valid = status && std::strcmp((const char *)status, "ok") == 0;
        sqlite3_finalize(check);
        sql_check(rc, s.target.db, &j);
        require(valid, UF_SQL, "Snapshot integrity check failed");
        check_cancel(j);
        sql_check(s.target.close(), nullptr);
        sql_check(s.source.close(), nullptr);
        require_new_target(s.destination);
#ifdef _WIN32
        require(MoveFileExW(std::filesystem::u8path(s.temporary).c_str(),
                            std::filesystem::u8path(s.destination).c_str(), MOVEFILE_WRITE_THROUGH) != 0,
                UF_IO, "Snapshot publication failed");
        s.temporary.clear();
        s.published = true;
#else
        auto fd = ::open(s.temporary.c_str(), O_RDONLY | O_CLOEXEC);
        require(fd >= 0, UF_IO, "Cannot flush snapshot");
        rc = fsync(fd);
        ::close(fd);
        require(rc == 0, UF_IO, "Snapshot flush failed");
        require(::link(s.temporary.c_str(), s.destination.c_str()) == 0, UF_IO,
                "Snapshot publication failed");
        s.published = true;
        j.completion.committed = 1;
        if (::unlink(s.temporary.c_str()) != 0)
        {
            s.temporary.clear();
            j.completion.reserved = SQLITE_IOERR_DELETE;
            throw Failure(UF_IO, "Cannot remove snapshot temporary name");
        }
        s.temporary.clear();
        fd = ::open(std::filesystem::u8path(s.destination).parent_path().c_str(),
                    O_RDONLY | O_CLOEXEC | O_DIRECTORY);
        require(fd >= 0, UF_IO, "Cannot flush snapshot directory");
        rc = fsync(fd);
        ::close(fd);
        require(rc == 0, UF_IO, "Snapshot directory flush failed");
#endif
        j.completion.committed = 1;
        j.snapshot.reset();
        return true;
    }
    bool runnable(const std::shared_ptr<Job> &j, bool writer, const std::list<std::shared_ptr<Job>> &queue)
    {
        auto d = j->database;
        if (!j->submitted)
            return false;
        if (writer && (read_job(j->kind) || j->kind == UF_SNAPSHOT))
            return false;
        if (j->kind == UF_CLOSE)
        {
            for (auto &q : queue)
                if (q != j && q->database == d && q->submitted)
                    return false;
            return !d->read_busy && !d->write_busy && !d->snapshot_busy;
        }
        if (j->kind == UF_SNAPSHOT)
            return true;
        if (read_job(j->kind))
            return !d->read_busy;
        return !d->write_busy;
    }
    void Engine::start()
    {
        {
            std::lock_guard<std::mutex> l(mutex);
            stopping = false;
        }
        workers[0] = std::thread([this] { run(true); });
        try
        {
            workers[1] = std::thread([this] { run(false); });
        }
        catch (...)
        {
            {
                std::lock_guard<std::mutex> l(mutex);
                stopping = true;
            }
            work.notify_all();
            workers[0].join();
            throw;
        }
    }
    Engine::~Engine()
    {
        stop();
    }
    void Engine::stop()
    {
        {
            std::lock_guard<std::mutex> l(mutex);
            stopping = true;
        }
        work.notify_all();
        for (auto &t : workers)
            if (t.joinable())
                t.join();
    }
    void Engine::run(bool writer)
    {
        for (;;)
        {
            std::shared_ptr<Job> j;
            Clock::time_point started;
            {
                std::unique_lock<std::mutex> l(mutex);
                work.wait(l, [&] {
                    if (stopping)
                        return true;
                    return std::any_of(queue.begin(), queue.end(),
                                       [&](auto &q) { return runnable(q, writer, queue); });
                });
                if (stopping)
                    return;
                auto it = std::find_if(queue.begin(), queue.end(),
                                       [&](auto &q) { return runnable(q, writer, queue); });
                j = *it;
                queue.erase(it);
                started = Clock::now();
                auto waiting = uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(started - j->enqueued).count());
                queue_wait_ns += waiting;
                max_queue_wait_ns = std::max(max_queue_wait_ns, waiting);
                ++active;
                if (read_job(j->kind))
                    j->database->read_busy = true;
                else if (j->kind == UF_SNAPSHOT)
                    j->database->snapshot_busy = true;
                else
                    j->database->write_busy = true;
            }
            bool again = false;
            try
            {
                j->completion.phase = j->kind == UF_OPEN ? UF_PHASE_OPEN :
                    j->kind == UF_CLOSE ? UF_PHASE_CLOSE :
                    j->kind == UF_SNAPSHOT ? UF_PHASE_SNAPSHOT :
                    (j->kind == UF_CHECKPOINT || j->kind == UF_STORAGE) ? UF_PHASE_MAINTENANCE : UF_PHASE_EXECUTE;
                if (j->kind == UF_SNAPSHOT)
                {
                    again = !snapshot_step(*j);
                }
                else if (j->kind == UF_OPEN)
                    open_database(*j);
                else if (j->kind == UF_CLOSE)
                {
                    auto d = j->database;
                    int a = d->read.close(), b = d->write.close();
                    d->unlock();
                    require(a == SQLITE_OK && b == SQLITE_OK, UF_SQL, "Database close failed");
                }
                else if (j->kind == UF_CHECKPOINT || j->kind == UF_STORAGE)
                    maintenance(*j);
                else
                    execute(*j);
            }
            catch (const Failure &f)
            {
                j->completion.error = f.code;
                j->completion.sqlite_code = f.sql;
                std::strncpy(j->completion.message, f.what(), 255);
                j->output.clear();
            }
            catch (const std::bad_alloc &)
            {
                j->completion.error = UF_MEMORY;
                std::strcpy(j->completion.message, "Allocation failed");
                j->output.clear();
            }
            catch (const std::exception &)
            {
                j->completion.error = UF_IO;
                std::strcpy(j->completion.message, "Native operation failed");
                j->output.clear();
            }
            catch (...)
            {
                j->completion.error = UF_STATE;
                std::strcpy(j->completion.message, "Unexpected native failure");
                j->output.clear();
            }
            if (j->kind == UF_SNAPSHOT && j->completion.error)
            {
                int cleanup = close_snapshot(*j);
                if (cleanup != SQLITE_OK)
                    if (!j->completion.reserved)
                        j->completion.reserved = cleanup;
            }
            {
                std::lock_guard<std::mutex> l(mutex);
                --active;
                auto elapsed = uint64_t(std::chrono::duration_cast<std::chrono::nanoseconds>(Clock::now() - started).count());
                execution_ns += elapsed;
                j->execution_ns += elapsed;
                auto d = j->database;
                if (read_job(j->kind))
                    d->read_busy = false;
                else if (j->kind == UF_SNAPSHOT)
                    d->snapshot_busy = false;
                else
                    d->write_busy = false;
                if (again)
                {
                    try
                    {
                        queue.push_back(j);
                        j->enqueued = Clock::now();
                        work.notify_all();
                        continue;
                    }
                    catch (...)
                    {
                        j->completion.error = UF_MEMORY;
                        std::strcpy(j->completion.message, "Cannot schedule next snapshot step");
                        int cleanup = close_snapshot(*j);
                        if (cleanup && !j->completion.reserved)
                            j->completion.reserved = cleanup;
                    }
                }
                if (j->kind == UF_SNAPSHOT)
                    snapshot_slot = false;
                if (j->kind == UF_CLOSE || (j->kind == UF_OPEN && j->completion.error))
                {
                    databases.erase(d->id);
                    auto o = owners.find(d->identity);
                    if (o != owners.end() && o->second == d->id)
                        owners.erase(o);
                }
                j->done = true;
                j->completion.size = sizeof(uf_completion);
                j->completion.abi = UFSQLITE_ABI;
                j->completion.operation = j->id;
                j->completion.database = d->id;
                j->completion.data = j->output.data();
                j->completion.data_size = j->output.size();
                ++total_completed;
                max_execution_ns = std::max(max_execution_ns, j->execution_ns);
                commit_ns += j->commit_ns;
                if (j->commit_ns && j->completion.committed == 1) ++commits;
                cache_hits += j->cache_hits;
                cache_misses += j->cache_misses;
                if (j->completion.error == UF_CANCELED) ++canceled;
                else if (j->completion.error == UF_TIMEOUT) ++timeouts;
                else if (j->completion.error) ++errors;
                auto &client = clients.at(j->client);
                if (client.last)
                    client.last->completion_next = j.get();
                else
                    client.first = j.get();
                client.last = j.get();
                // Intrusive completion links were allocated with the accepted job.
            }
            completion.notify_all();
            work.notify_all();
        }
    }
    template <class F> int guarded(F f)
    {
        try
        {
            return f();
        }
        catch (const Failure &e)
        {
            return e.code;
        }
        catch (const std::bad_alloc &)
        {
            return UF_MEMORY;
        }
        catch (...)
        {
            return UF_STATE;
        }
    }
    // Called with the engine lock held. Results may be relinquished without a
    // working completion consumer; removing an intrusive link needs no allocation.
    void release_job(Engine &e, const std::shared_ptr<Job> &j)
    {
        auto &client = e.clients.at(j->client);
        Job *previous = nullptr;
        for (auto current = client.first; current; current = current->completion_next)
        {
            if (current == j.get())
            {
                if (previous)
                    previous->completion_next = current->completion_next;
                else
                    client.first = current->completion_next;
                if (client.last == current)
                    client.last = previous;
                break;
            }
            previous = current;
        }
        auto d = j->database;
        e.reserved -= j->charge;
        d->parameters -= j->input.size() * 2;
        d->results -= j->result_limit;
        --d->count;
        --client.operations;
        e.jobs.erase(j->id);
    }
} // namespace
extern "C"
{
    uint32_t ufsqlite_abi()
    {
        return UFSQLITE_ABI;
    }
    const char *ufsqlite_source_id()
    {
        return sqlite3_sourceid();
    }
    const char *ufsqlite_build_id()
    {
        return UFSQLITE_BUILD_ID;
    }
    int ufsqlite_client_create(uint32_t abi, uint64_t *client)
    {
        return guarded([&] {
            require(abi == UFSQLITE_ABI && client, UF_ARGUMENT, "ABI mismatch");
            auto &e = engine();
            // Serialize first-client startup and last-client shutdown without holding
            // the work mutex while joining. Windows DLL unload must not join workers.
            std::lock_guard<std::mutex> lifetime(e.lifecycle);
            uint64_t id;
            bool first;
            {
                std::lock_guard<std::mutex> l(e.mutex);
                id = e.next++;
                first = e.clients.empty();
                e.clients.emplace(id, Client{});
            }
            try
            {
                if (first)
                    e.start();
            }
            catch (...)
            {
                std::lock_guard<std::mutex> l(e.mutex);
                e.clients.erase(id);
                throw;
            }
            *client = id;
            return UF_OK;
        });
    }
    int ufsqlite_client_release(uint64_t client)
    {
        return guarded([&] {
            auto &e = engine();
            std::lock_guard<std::mutex> lifetime(e.lifecycle);
            bool last;
            {
                std::lock_guard<std::mutex> l(e.mutex);
                auto i = e.clients.find(client);
                require(i != e.clients.end(), UF_HANDLE, "Unknown client");
                require(i->second.operations == 0 && i->second.waiters == 0, UF_STATE,
                        "Client owns operations or waits");
                for (auto &d : e.databases)
                    require(d.second->client != client, UF_STATE, "Client owns databases");
                e.clients.erase(i);
                last = e.clients.empty();
            }
            if (last)
                e.stop();
            return UF_OK;
        });
    }
    int ufsqlite_client_stop(uint64_t client)
    {
        return guarded([&] {
            auto &e = engine();
            std::lock_guard<std::mutex> lifetime(e.lifecycle);
            std::unique_lock<std::mutex> l(e.mutex);
            auto ci = e.clients.find(client);
            require(ci != e.clients.end(), UF_HANDLE, "Unknown client");
            require(!ci->second.stopping, UF_STATE, "Client already stopped");
            ci->second.stopping = true;
            for (auto it = e.jobs.begin(); it != e.jobs.end();)
            {
                auto j = (it++)->second;
                if (j->client != client)
                    continue;
                if (!j->submitted)
                {
                    e.queue.erase(std::find(e.queue.begin(), e.queue.end(), j));
                    if (j->kind == UF_OPEN)
                        e.databases.erase(j->database->id);
                    if (j->kind == UF_SNAPSHOT)
                        e.snapshot_slot = false;
                    if (j->kind == UF_CLOSE)
                        j->database->close_reserved = false;
                    release_job(e, j);
                }
                else if (j->kind != UF_CLOSE)
                    j->cancel = true;
            }
            e.work.notify_all();
            e.completion.wait(l, [&] {
                return std::none_of(e.jobs.begin(), e.jobs.end(), [&](const auto &entry) {
                    return entry.second->client == client && !entry.second->done;
                });
            });
            int error = UF_OK;
            for (;;)
            {
                auto it = std::find_if(e.databases.begin(), e.databases.end(), [&](const auto &entry) {
                    return entry.second->client == client;
                });
                if (it == e.databases.end())
                    break;
                auto d = it->second;
                d->closing = true;
                l.unlock();
                int a = d->read.close(), b = d->write.close();
                d->unlock();
                if (a != SQLITE_OK || b != SQLITE_OK)
                    error = UF_SQL;
                l.lock();
                auto owner = e.owners.find(d->identity);
                if (owner != e.owners.end() && owner->second == d->id)
                    e.owners.erase(owner);
                e.databases.erase(d->id);
            }
            return error;
        });
    }
    int ufsqlite_reserve(uint64_t client, uint64_t database, uint32_t kind, uint64_t length, uint32_t rows,
                         uint32_t bytes, uint32_t timeout, uint64_t *operation)
    {
        return guarded([&] {
            require(operation && kind >= UF_OPEN && kind <= UF_STORAGE && timeout > 0 &&
                        length <= ParameterBudget && bytes <= MiB && rows <= 200,
                    UF_ARGUMENT, "Invalid request");
            require(kind != UF_CLOSE || (length == 0 && rows == 0 && bytes == 0), UF_ARGUMENT,
                    "Close takes no input or result budget");
            auto &e = engine();
            std::lock_guard<std::mutex> l(e.mutex);
            auto ci = e.clients.find(client);
            require(ci != e.clients.end(), UF_HANDLE, "Unknown client");
            require(!ci->second.stopping, UF_STATE, "Client not accepting work");
            std::shared_ptr<Database> d;
            if (kind == UF_OPEN)
            {
                require(database == 0, UF_ARGUMENT, "Open database must be zero");
                d = std::make_shared<Database>();
                d->id = e.next++;
                d->client = client;
            }
            else
            {
                auto it = e.databases.find(database);
                require(it != e.databases.end() && it->second->client == client, UF_HANDLE,
                        "Unknown database");
                d = it->second;
                require(d->ready && !d->closing, UF_STATE, "Database not accepting work");
                require(kind != UF_CLOSE || !d->close_reserved, UF_STATE, "Close already reserved");
            }
            if (kind == UF_SNAPSHOT)
                require(!e.snapshot_slot, UF_CAPACITY, "Snapshot slot is busy");
            auto charge = length * 2 + bytes * 2 + sizeof(Job);
            if (kind != UF_CLOSE)
            {
                require(d->count < 128 && d->parameters + length * 2 <= ParameterBudget &&
                            d->results + bytes <= ResultBudget && e.reserved + charge <= GlobalBudget,
                        UF_CAPACITY, "Admission budget exceeded");
                require(!d->fault, UF_FAULTED, "Database faulted");
            }
            auto j = std::make_shared<Job>();
            j->id = e.next++;
            j->client = client;
            j->database = d;
            j->kind = kind;
            j->rows = rows;
            j->result_limit = bytes;
            j->charge = charge;
            j->deadline = Clock::now() + std::chrono::milliseconds(timeout);
            j->input.resize(size_t(length));
            // Allocate all queue nodes before marking the operation accepted.
            bool registered = false, queued = false, opened = false;
            try
            {
                e.jobs.emplace(j->id, j);
                registered = true;
                e.queue.push_back(j);
                queued = true;
                if (kind == UF_OPEN)
                {
                    e.databases.emplace(d->id, d);
                    opened = true;
                }
            }
            catch (...)
            {
                if (opened)
                    e.databases.erase(d->id);
                if (queued)
                    e.queue.pop_back();
                if (registered)
                    e.jobs.erase(j->id);
                throw;
            }
            if (kind == UF_SNAPSHOT)
                e.snapshot_slot = true;
            if (kind == UF_CLOSE)
                d->close_reserved = true;
            d->parameters += length * 2;
            d->results += bytes;
            ++d->count;
            ++ci->second.operations;
            e.reserved += charge;
            *operation = j->id;
            e.work.notify_all();
            return UF_OK;
        });
    }
    int ufsqlite_commit_request(uint64_t client, uint64_t operation, const uint8_t *payload, uint64_t length)
    {
        return guarded([&] {
            auto &e = engine();
            std::lock_guard<std::mutex> l(e.mutex);
            auto i = e.jobs.find(operation);
            require(i != e.jobs.end() && i->second->client == client, UF_HANDLE, "Unknown operation");
            auto j = i->second;
            require(!j->submitted && j->input.size() == length && (length == 0 || payload), UF_ARGUMENT,
                    "Invalid reserved request");
            auto d = j->database;
            require(!e.clients.at(client).stopping && !d->closing, UF_STATE,
                    "Owner not accepting work");
            if (length)
                std::memcpy(j->input.data(), payload, size_t(length));
            // Reuse the preallocated node. Acceptance order is commit order,
            // independent of reservation/encoding order and without new allocation.
            e.queue.splice(e.queue.end(), e.queue, std::find(e.queue.begin(), e.queue.end(), j));
            j->submitted = true;
            j->enqueued = Clock::now();
            j->completion.phase = UF_PHASE_QUEUE;
            if (j->kind == UF_CLOSE)
            {
                d->closing = true;
                for (auto &entry : e.jobs)
                    if (entry.second->database == d && entry.second->kind == UF_SNAPSHOT)
                        entry.second->cancel = true;
            }
            e.work.notify_all();
            return UF_OK;
        });
    }
    int ufsqlite_discard_request(uint64_t client, uint64_t operation)
    {
        return guarded([&] {
            auto &e = engine();
            std::lock_guard<std::mutex> l(e.mutex);
            auto i = e.jobs.find(operation);
            require(i != e.jobs.end() && i->second->client == client, UF_HANDLE, "Unknown operation");
            auto j = i->second;
            require(!j->submitted, UF_STATE, "Request already committed");
            auto d = j->database;
            e.queue.erase(std::find(e.queue.begin(), e.queue.end(), j));
            if (j->kind == UF_OPEN)
                e.databases.erase(d->id);
            if (j->kind == UF_SNAPSHOT)
                e.snapshot_slot = false;
            if (j->kind == UF_CLOSE)
                d->close_reserved = false;
            release_job(e, j);
            e.work.notify_all();
            return UF_OK;
        });
    }
    int ufsqlite_submit(uint64_t client, uint64_t database, uint32_t kind, const uint8_t *payload,
                        uint64_t length, uint32_t rows, uint32_t bytes, uint32_t timeout, uint64_t *operation)
    {
        if (length && !payload)
            return UF_ARGUMENT;
        int rc = ufsqlite_reserve(client, database, kind, length, rows, bytes, timeout, operation);
        if (rc)
            return rc;
        rc = ufsqlite_commit_request(client, *operation, payload, length);
        if (rc)
            ufsqlite_discard_request(client, *operation);
        return rc;
    }
    int ufsqlite_cancel(uint64_t client, uint64_t operation)
    {
        return guarded([&] {
            auto &e = engine();
            std::lock_guard<std::mutex> l(e.mutex);
            auto i = e.jobs.find(operation);
            require(i != e.jobs.end() && i->second->client == client, UF_HANDLE, "Unknown operation");
            if (i->second->kind != UF_CLOSE)
                i->second->cancel = true;
            return UF_OK;
        });
    }
    int ufsqlite_wait(uint64_t client, uf_completion *output, uint32_t capacity, uint32_t timeout,
                      uint32_t *count)
    {
        return guarded([&] {
            require(output && count && capacity > 0 && capacity <= 128, UF_ARGUMENT, "Invalid output");
            auto &e = engine();
            std::unique_lock<std::mutex> l(e.mutex);
            auto ci = e.clients.find(client);
            require(ci != e.clients.end(), UF_HANDLE, "Unknown client");
            auto &current = ci->second;
            struct Waiter
            {
                uint64_t &count;
                explicit Waiter(uint64_t &n) : count(n)
                {
                    ++count;
                }
                ~Waiter()
                {
                    --count;
                }
            } waiter(current.waiters);
            auto available = [&] { return current.first != nullptr; };
            if (!available())
                e.completion.wait_for(l, std::chrono::milliseconds(timeout), available);
            *count = 0;
            while (current.first && *count < capacity)
            {
                auto j = current.first;
                current.first = j->completion_next;
                if (!current.first)
                    current.last = nullptr;
                j->completion_next = nullptr;
                j->claimed = true;
                output[(*count)++] = j->completion;
            }
            return UF_OK;
        });
    }
    int ufsqlite_release_result(uint64_t client, uint64_t operation)
    {
        return guarded([&] {
            auto &e = engine();
            std::lock_guard<std::mutex> l(e.mutex);
            auto i = e.jobs.find(operation);
            require(i != e.jobs.end() && i->second->client == client, UF_HANDLE, "Unknown operation");
            auto j = i->second;
            require(j->done && j->claimed, UF_STATE, "Result not claimed");
            release_job(e, j);
            return UF_OK;
        });
    }
    int ufsqlite_discard_result(uint64_t client, uint64_t operation)
    {
        return guarded([&] {
            auto &e = engine();
            std::lock_guard<std::mutex> l(e.mutex);
            auto it = e.jobs.find(operation);
            require(it != e.jobs.end() && it->second->client == client, UF_HANDLE, "Unknown operation");
            auto j = it->second;
            require(j->done, UF_STATE, "Operation still running");
            release_job(e, j);
            return UF_OK;
        });
    }
    int ufsqlite_get_diagnostics(uf_diagnostics *o)
    {
        return guarded([&] {
            require(o && o->size == sizeof(*o) && o->abi == UFSQLITE_ABI, UF_ARGUMENT,
                    "Diagnostics ABI mismatch");
            auto &e = engine();
            std::lock_guard<std::mutex> l(e.mutex);
            o->databases = e.databases.size();
            o->operations = e.jobs.size();
            o->reserved_bytes = e.reserved;
            o->completed = e.total_completed;
            o->sqlite_bytes = sqlite3_memory_used();
            o->queued = std::count_if(e.queue.begin(), e.queue.end(), [](const auto &j) { return j->submitted; });
            o->active = e.active;
            o->parameter_bytes = o->result_reserved_bytes = o->held_result_bytes = 0;
            for (const auto &entry : e.jobs)
            {
                const auto &j = entry.second;
                o->parameter_bytes += j->input.size() * 2;
                o->result_reserved_bytes += j->result_limit;
                if (j->done) o->held_result_bytes += j->output.size();
            }
            o->queue_wait_ns = e.queue_wait_ns;
            o->max_queue_wait_ns = e.max_queue_wait_ns;
            o->execution_ns = e.execution_ns;
            o->max_execution_ns = e.max_execution_ns;
            o->commit_ns = e.commit_ns;
            o->commits = e.commits;
            o->cache_hits = e.cache_hits;
            o->cache_misses = e.cache_misses;
            o->errors = e.errors;
            o->canceled = e.canceled;
            o->timeouts = e.timeouts;
            return UF_OK;
        });
    }
}
