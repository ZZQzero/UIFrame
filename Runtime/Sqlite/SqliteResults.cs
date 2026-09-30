using System;
using System.Collections.Generic;
using System.IO;
using UIFrame.Sqlite.Internal;

namespace UIFrame.Sqlite
{
    /// <summary>A row is valid only during its mapping callback. Copy values needed by the caller.</summary>
    public sealed class SqliteRow
    {
        readonly string[] names;
        object[] values;
        internal SqliteRow(string[] names, object[] values)
        {
            this.names = names;
            this.values = values;
        }
        void Check()
        {
            if (values == null)
                throw new ObjectDisposedException(nameof(SqliteRow));
        }
        public int Count
        {
            get {
                Check();
                return values.Length;
            }
        }
        public object this[int index]
        {
            get
            {
                Check();
                return values[index];
            }
        }
        public object this[string name]
        {
            get
            {
                Check();
                int index = Array.IndexOf(names, name);
                if (index < 0)
                    throw new IndexOutOfRangeException(name);
                return values[index];
            }
        }
        public long GetInt64(int index) => (long)this[index];
        public string GetString(int index) => (string)this[index];
        public bool IsNull(int index) => this[index] == null;
        internal void Invalidate()
        {
            values = null;
        }
    }
    /// <summary>Owns a bounded native result independently of its database connection.</summary>
    public sealed class SqliteBatchResult : IDisposable
    {
        readonly object gate = new object();
        NativeResult result;
        ResultPage page;
        internal SqliteBatchResult(NativeResult result)
        {
            this.result = result;
        }
        ResultPage Page
        {
            get {
                if (result == null)
                    throw new ObjectDisposedException(nameof(SqliteBatchResult));
                return page ?? (page = new ResultPage(result.Copy()));
            }
        }
        public IReadOnlyList<T> MapRows<T>(int commandIndex, Func<SqliteRow, T> map)
        {
            if (map == null)
                throw new ArgumentNullException(nameof(map));
            lock (gate) return Page.Map(commandIndex, map);
        }
        public long GetAffectedRows(int commandIndex)
        {
            lock (gate) return Page.Changes(commandIndex);
        }
        public void Dispose()
        {
            NativeResult owned;
            lock (gate)
            {
                owned = result;
                result = null;
                page = null;
            }
            owned?.Dispose();
        }
        internal static IReadOnlyList<T> Decode<T>(byte[] bytes, int commandIndex, Func<SqliteRow, T> map,
                                                   out long changes)
        {
            var page = new ResultPage(bytes);
            changes = page.Changes(commandIndex);
            return map == null ? Array.Empty<T>() : page.Map(commandIndex, map);
        }
    }
    /// <summary>Indexes encoded tables once. Reading a count never materializes unrequested rows or
    /// BLOBs.</summary>
    internal sealed class ResultPage
    {
        readonly byte[] bytes;
        readonly Table[] tables;
        readonly struct Table
        {
            internal readonly int Columns, Rows, NamesOffset, ValuesOffset;
            internal readonly long Changes;
            internal Table(int columns, int rows, int names, int values, long changes)
            {
                Columns = columns;
                Rows = rows;
                NamesOffset = names;
                ValuesOffset = values;
                Changes = changes;
            }
        }
        internal ResultPage(byte[] bytes)
        {
            this.bytes = bytes;
            using (var reader = Reader())
            {
                int count = reader.ReadInt32();
                if (count < 1 || count > 200)
                    throw new InvalidDataException("Invalid result count.");
                tables = new Table[count];
                int totalRows = 0;
                for (int t = 0; t < count; t++)
                {
                    int columns = reader.ReadInt32(), rows = reader.ReadInt32();
                    long changes = reader.ReadInt64();
                    if (columns < 0 || columns > 512 || rows < 0 || rows > 200 || (totalRows += rows) > 200)
                        throw new InvalidDataException("Invalid result dimensions.");
                    int names = checked((int)reader.BaseStream.Position);
                    for (int c = 0; c < columns; c++)
                        SkipString(reader);
                    int values = checked((int)reader.BaseStream.Position);
                    for (int row = 0; row < rows; row++)
                        for (int c = 0; c < columns; c++)
                            SkipValue(reader);
                    tables[t] = new Table(columns, rows, names, values, changes);
                }
                if (reader.BaseStream.Position != bytes.Length)
                    throw new InvalidDataException("Trailing result data.");
            }
        }
        BinaryReader Reader() => new BinaryReader(new MemoryStream(bytes, false), Wire.Utf8);
        static void Skip(BinaryReader reader, int count)
        {
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid result length.");
            reader.BaseStream.Position += count;
        }
        static void SkipString(BinaryReader reader)
        {
            Skip(reader, reader.ReadInt32());
        }
        static void SkipValue(BinaryReader reader)
        {
            switch (reader.ReadByte())
            {
            case 0:
                return;
            case 1:
            case 2:
                Skip(reader, 8);
                return;
            case 3:
            case 4:
                SkipString(reader);
                return;
            default:
                throw new InvalidDataException("Invalid result type.");
            }
        }
        static byte[] ReadBytes(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > reader.BaseStream.Length - reader.BaseStream.Position)
                throw new InvalidDataException("Invalid value length.");
            return reader.ReadBytes(count);
        }
        static object ReadValue(BinaryReader reader)
        {
            switch (reader.ReadByte())
            {
            case 0:
                return null;
            case 1:
                return reader.ReadInt64();
            case 2:
                return reader.ReadDouble();
            case 3:
                return Wire.Utf8.GetString(ReadBytes(reader));
            case 4:
                return ReadBytes(reader);
            default:
                throw new InvalidDataException("Invalid result type.");
            }
        }
        Table Get(int index)
        {
            if (index < 0 || index >= tables.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return tables[index];
        }
        internal long Changes(int index) => Get(index).Changes;
        internal IReadOnlyList<T> Map<T>(int index, Func<SqliteRow, T> map)
        {
            var table = Get(index);
            var result = new List<T>(table.Rows);
            using (var reader = Reader())
            {
                reader.BaseStream.Position = table.NamesOffset;
                var names = new string[table.Columns];
                for (int c = 0; c < names.Length; c++)
                    names[c] = Wire.Utf8.GetString(ReadBytes(reader));
                reader.BaseStream.Position = table.ValuesOffset;
                for (int row = 0; row < table.Rows; row++)
                {
                    var values = new object[table.Columns];
                    for (int c = 0; c < values.Length; c++)
                        values[c] = ReadValue(reader);
                    var view = new SqliteRow(names, values);
                    try
                    {
                        result.Add(map(view));
                    }
                    finally
                    {
                        view.Invalidate();
                    }
                }
            }
            return result;
        }
    }
}
