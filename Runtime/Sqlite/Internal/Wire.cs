using System;
using System.Text;

namespace UIFrame.Sqlite.Internal
{
    internal static class Wire
    {
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal const int ParameterLimit = 2 * 1024 * 1024;
        internal static int StringSize(string value)
        {
            int bytes = Utf8.GetByteCount(value);
            if (bytes > 1024 * 1024)
                throw new ArgumentException("UTF-8 value exceeds 1 MiB.");
            return checked(4 + bytes);
        }
        internal static int Measure(SqliteCommand[] commands)
        {
            long length = 4;
            foreach (var command in commands)
            {
                length += StringSize(command.Sql) + 8 + 4;
                foreach (var value in command.Parameters)
                {
                    length++;
                    if (value == null || value == DBNull.Value)
                        continue;
                    if (value is string text)
                        length += StringSize(text);
                    else if (value is byte[] bytes)
                    {
                        if (bytes.Length > 1024 * 1024)
                            throw new ArgumentException("BLOB exceeds 1 MiB.");
                        length += 4L + bytes.Length;
                    }
                    else if (value is double || value is float)
                    {
                        double number = Convert.ToDouble(value);
                        if (double.IsNaN(number) || double.IsInfinity(number))
                            throw new ArgumentException("Non-finite values are not supported.");
                        length += 8;
                    }
                    else if (value is long || value is int || value is short || value is byte ||
                             value is uint || value is ushort || value is sbyte || value is bool)
                        length += 8;
                    else
                        throw new ArgumentException("Unsupported SQLite parameter type: " +
                                                    value.GetType().FullName);
                    if (length > ParameterLimit)
                        throw new SqliteException(SqliteError.CapacityExceeded, "Parameter budget exceeded.");
                }
                if (length > ParameterLimit)
                    throw new SqliteException(SqliteError.CapacityExceeded, "Parameter budget exceeded.");
            }
            return checked((int)length);
        }
        sealed class Buffer
        {
            internal readonly byte[] Data;
            int position;
            internal Buffer(int size)
            {
                Data = new byte[size];
            }
            internal void Number(ulong value, int size)
            {
                for (int i = 0; i < size; i++)
                    Data[position++] = (byte)(value >> (8 * i));
            }
            internal void String(string value)
            {
                int size = Utf8.GetByteCount(value);
                Number((ulong)size, 4);
                position += Utf8.GetBytes(value, 0, value.Length, Data, position);
            }
            internal void Blob(byte[] value)
            {
                Number((ulong)value.Length, 4);
                System.Buffer.BlockCopy(value, 0, Data, position, value.Length);
                position += value.Length;
            }
        }
        internal static byte[] Encode(SqliteCommand[] commands, int size)
        {
            var writer = new Buffer(size);
            writer.Number((ulong)commands.Length, 4);
            foreach (var command in commands)
            {
                writer.String(command.Sql);
                writer.Number(unchecked((ulong)(command.ExpectedAffectedRows ?? -1)), 8);
                writer.Number((ulong)command.Parameters.Length, 4);
                foreach (var value in command.Parameters)
                {
                    if (value == null || value == DBNull.Value)
                        writer.Number(0, 1);
                    else if (value is string text)
                    {
                        writer.Number(3, 1);
                        writer.String(text);
                    }
                    else if (value is byte[] bytes)
                    {
                        writer.Number(4, 1);
                        writer.Blob(bytes);
                    }
                    else if (value is double || value is float)
                    {
                        writer.Number(2, 1);
                        writer.Number(
                            unchecked((ulong)BitConverter.DoubleToInt64Bits(Convert.ToDouble(value))), 8);
                    }
                    else
                    {
                        writer.Number(1, 1);
                        writer.Number(unchecked((ulong)Convert.ToInt64(value)), 8);
                    }
                }
            }
            return writer.Data;
        }
        internal static byte[] Snapshot(SqliteSnapshotOptions options)
        {
            var writer = new Buffer(checked(StringSize(options.Destination) + 16));
            writer.String(options.Destination);
            writer.Number((ulong)options.MaxTemporaryBytes, 8);
            writer.Number((ulong)options.MaxWalBytes, 8);
            return writer.Data;
        }
        internal static byte[] Open(SqliteOpenOptions options)
        {
            var writer = new Buffer(checked(20 + StringSize(options.Path)));
            writer.Number((ulong)options.Mode, 4);
            writer.String(options.Path);
            writer.Number((ulong)options.MinFreeDiskBytes, 8);
            writer.Number((ulong)options.MaxWalBytes, 8);
            return writer.Data;
        }
    }
}
