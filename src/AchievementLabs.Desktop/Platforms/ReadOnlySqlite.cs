using System.Runtime.InteropServices;

namespace AchievementLabs.Desktop.Platforms;

// Uses Windows' SQLite runtime. Never opens a launcher database with write/create flags.
internal sealed class ReadOnlySqlite : IDisposable
{
    private IntPtr db;
    public ReadOnlySqlite(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Galaxy database was not found.");
        if (sqlite3_open_v2(path, out db, 1, IntPtr.Zero) != 0) { var message = Marshal.PtrToStringUTF8(sqlite3_errmsg(db)); Dispose(); throw new IOException("Galaxy database could not be opened read-only: " + message); }
        sqlite3_busy_timeout(db, 1500);
    }
    public List<string?[]> Query(string sql, params string[] values)
    {
        if (sqlite3_prepare_v2(db, sql, -1, out var statement, IntPtr.Zero) != 0) throw new IOException("Galaxy cache could not be queried: " + Marshal.PtrToStringUTF8(sqlite3_errmsg(db)));
        try
        {
            for (var i = 0; i < values.Length; i++)
                if (sqlite3_bind_text(statement, i + 1, values[i], -1, new IntPtr(-1)) != 0) throw new IOException("Galaxy query binding failed.");
            var result = new List<string?[]>();
            int step;
            while ((step = sqlite3_step(statement)) == 100)
            {
                if (result.Count >= 50000) throw new IOException("Galaxy cache exceeded the supported row limit.");
                var row = new string?[sqlite3_column_count(statement)];
                for (var i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i));
                result.Add(row);
            }
            if (step != 101) throw new IOException("Galaxy cache read did not complete; refresh after the client finishes syncing.");
            return result;
        }
        finally { sqlite3_finalize(statement); }
    }
    public void Dispose() { if (db != IntPtr.Zero) sqlite3_close(db); db = IntPtr.Zero; }
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(IntPtr db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(IntPtr statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, int bytes, IntPtr destructor);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int index);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(IntPtr db, int milliseconds);
    [DllImport("winsqlite3", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr sqlite3_errmsg(IntPtr db);
}
