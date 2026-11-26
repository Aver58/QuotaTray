using System.Runtime.InteropServices;

namespace CodexQuotaCore;

internal sealed class WindowsSqlite : IDisposable
{
    private const string Library = "winsqlite3.dll";
    private nint _db;

    internal WindowsSqlite(string file)
    {
        var code = Open(file, out _db, 1, 0);
        try
        {
            Check(code);
            Check(Timeout(_db, 1000));
            // Both queries must see the same version of the session, including its WAL.
            using var begin = Prepare("BEGIN DEFERRED");
            begin.Read();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal Statement Prepare(string sql)
    {
        Check(Prepare(_db, sql, -1, out var statement, 0));
        return new Statement(statement);
    }

    private static void Check(int code)
    {
        if (code != 0) throw new InvalidDataException($"SQLite 无法读取会话（{code}）。");
    }

    public void Dispose()
    {
        if (_db == 0) return;
        Close(_db);
        _db = 0;
    }

    internal sealed class Statement(nint handle) : IDisposable
    {
        private nint _handle = handle;

        internal void BindIndex(int value)
        {
            Check(Reset(_handle));
            Check(BindInt(_handle, 1, value));
        }

        internal bool Read() => Advance(_handle) switch
        {
            100 => true,
            101 => false,
            var error => throw new InvalidDataException($"SQLite 无法读取记录（{error}）。")
        };

        internal byte[] Blob()
        {
            var pointer = ColumnBlob(_handle, 0);
            var length = ColumnBytes(_handle, 0);
            if (pointer == 0 || length <= 0) return [];
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }

        public void Dispose()
        {
            if (_handle == 0) return;
            FinalizeStatement(_handle);
            _handle = 0;
        }
    }

    [DllImport(Library, EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string file, out nint db, int flags, nint vfs);
    [DllImport(Library, EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Close(nint db);
    [DllImport(Library, EntryPoint = "sqlite3_busy_timeout", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Timeout(nint db, int milliseconds);
    [DllImport(Library, EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Prepare(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string query, int length, out nint statement, nint tail);
    [DllImport(Library, EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Advance(nint statement);
    [DllImport(Library, EntryPoint = "sqlite3_reset", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Reset(nint statement);
    [DllImport(Library, EntryPoint = "sqlite3_bind_int", CallingConvention = CallingConvention.Cdecl)]
    private static extern int BindInt(nint statement, int position, int value);
    [DllImport(Library, EntryPoint = "sqlite3_column_blob", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint ColumnBlob(nint statement, int column);
    [DllImport(Library, EntryPoint = "sqlite3_column_bytes", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ColumnBytes(nint statement, int column);
    [DllImport(Library, EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int FinalizeStatement(nint statement);
}
