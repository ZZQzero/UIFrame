using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace UIFrame.Sqlite
{
    public enum SqliteOpenMode
    {
        CreateNew,
        OpenExistingReadWrite,
        OpenExistingReadOnly
    }
    public enum SqliteError
    {
        Argument = 1,
        InvalidHandle,
        CapacityExceeded,
        Sql,
        Canceled,
        Timeout,
        IO,
        InvalidState,
        Memory,
        ConditionFailed,
        ResultLimit,
        Faulted
    }
    public sealed class SqliteException : Exception
    {
        public SqliteError Error { get; }
        public int ExtendedCode { get; }
        public ulong OperationId { get; }
        public bool CommitOutcomeUnknown { get; }
        public bool HasCommittedChanges { get; }
        public int CleanupCode { get; }
        internal SqliteException(SqliteError error, string message, int code = 0, ulong operation = 0,
                                 int committed = 0, int cleanupCode = 0)
            : base(message)
        {
            Error = error;
            ExtendedCode = code;
            OperationId = operation;
            CommitOutcomeUnknown = committed < 0;
            HasCommittedChanges = committed > 0;
            CleanupCode = cleanupCode;
        }
    }
    public sealed class SqliteOpenOptions
    {
        public string Path { get; }
        public SqliteOpenMode Mode { get; }
        public long MinFreeDiskBytes { get; }
        public long MaxWalBytes { get; }
        public SqliteOpenOptions(string path, SqliteOpenMode mode, long minFreeDiskBytes = 32L * 1024 * 1024,
                                 long maxWalBytes = 128L * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathRooted(path) ||
                path.IndexOf('\0') >= 0)
                throw new ArgumentException("An absolute database path is required.", nameof(path));
            if (mode < SqliteOpenMode.CreateNew || mode > SqliteOpenMode.OpenExistingReadOnly)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (minFreeDiskBytes < 0)
                throw new ArgumentOutOfRangeException(nameof(minFreeDiskBytes));
            if (maxWalBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxWalBytes));
            Path = System.IO.Path.GetFullPath(path);
            Mode = mode;
            MinFreeDiskBytes = minFreeDiskBytes;
            MaxWalBytes = maxWalBytes;
        }
    }
    public sealed class SqliteQueryBudget
    {
        public int MaxRows { get; }
        public int MaxBytes { get; }
        public TimeSpan Timeout { get; }
        public SqliteQueryBudget(int maxRows = 200, int maxBytes = 1024 * 1024, TimeSpan? timeout = null)
        {
            if (maxRows < 1 || maxRows > 200)
                throw new ArgumentOutOfRangeException(nameof(maxRows));
            if (maxBytes < 20 || maxBytes > 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            var duration = timeout ?? TimeSpan.FromSeconds(5);
            if (duration.TotalMilliseconds < 1 || duration.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout));
            MaxRows = maxRows;
            MaxBytes = maxBytes;
            Timeout = duration;
        }
        internal void Validate()
        {
            if (MaxRows == 0)
                throw new ArgumentException("An initialized query budget is required.");
        }
    }
    /// <summary>SQL and parameter references are captured here. Parameters are copied at asynchronous
    /// admission. Do not mutate byte arrays during admission. Values support null, string, byte[],
    /// Int64-compatible integers, double and bool.</summary>
    public sealed class SqliteCommand
    {
        public string Sql { get; }
        public long? ExpectedAffectedRows { get; }
        internal readonly object[] Parameters;
        public SqliteCommand(string sql, params object[] parameters) : this(sql, null, parameters)
        {
        }
        SqliteCommand(string sql, long? expectedAffectedRows, object[] parameters)
        {
            if (string.IsNullOrWhiteSpace(sql) || sql.IndexOf('\0') >= 0)
                throw new ArgumentException("One SQL statement required.", nameof(sql));
            if (expectedAffectedRows < 0)
                throw new ArgumentOutOfRangeException(nameof(expectedAffectedRows));
            if (parameters == null || parameters.Length > 1000)
                throw new ArgumentException("At most 1000 parameters are supported.", nameof(parameters));
            Sql = sql;
            ExpectedAffectedRows = expectedAffectedRows;
            Parameters = (object[])parameters.Clone();
        }
        public SqliteCommand ExpectAffectedRows(long count) => new SqliteCommand(Sql, count, Parameters);
    }
    public sealed class SqliteBatch
    {
        internal readonly SqliteCommand[] Commands;
        public SqliteQueryBudget Budget { get; }
        public SqliteBatch(SqliteQueryBudget budget, params SqliteCommand[] commands)
        {
            if (budget == null)
                throw new ArgumentNullException(nameof(budget));
            budget.Validate();
            if (commands == null || commands.Length == 0 || commands.Length > 200)
                throw new ArgumentException("A batch requires 1–200 statements.", nameof(commands));
            foreach (var command in commands)
                if (command == null)
                    throw new ArgumentException("A command is null.", nameof(commands));
            Commands = (SqliteCommand[])commands.Clone();
            Budget = budget;
        }
    }
    public readonly struct SqliteDiagnostics
    {
        public ulong OpenDatabases { get; }
        public ulong OutstandingOperations { get; }
        public ulong ReservedBytes { get; }
        public ulong CompletedOperations { get; }
        public ulong EngineBytes { get; }
        internal SqliteDiagnostics(Internal.NativeMethods.Diagnostics v)
        {
            OpenDatabases = v.Databases;
            OutstandingOperations = v.Operations;
            ReservedBytes = v.ReservedBytes;
            CompletedOperations = v.Completed;
            EngineBytes = v.SqliteBytes;
        }
    }
}
