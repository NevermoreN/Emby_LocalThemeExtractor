using System;
using System.Runtime.InteropServices;

namespace LocalThemeExtractor;

internal static class SqliteReader
{
	private const string LibName = "libsqlite3.so.0";

	private const int SQLITE_OK = 0;

	private const int SQLITE_ROW = 100;

	private const int SQLITE_DONE = 101;

	private const int SQLITE_NULL = 5;

	private const int SQLITE_OPEN_READONLY = 1;

	private const int SQLITE_OPEN_NOMUTEX = 32768;

	private const int SQLITE_OPEN_SHAREDCACHE = 131072;

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_open_v2(string filename, out IntPtr db, int flags, IntPtr vfs);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_close(IntPtr db);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_prepare_v2(IntPtr db, string sql, int nBytes, out IntPtr stmt, out IntPtr tail);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_step(IntPtr stmt);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_finalize(IntPtr stmt);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern long sqlite3_column_int64(IntPtr stmt, int col);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_column_int(IntPtr stmt, int col);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr sqlite3_column_text(IntPtr stmt, int col);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern int sqlite3_column_type(IntPtr stmt, int col);

	[DllImport("libsqlite3.so.0", CallingConvention = CallingConvention.Cdecl)]
	private static extern IntPtr sqlite3_errmsg(IntPtr db);

	public static void Query(string dbPath, string sql, Action<Func<int, object>> rowCallback)
	{
		IntPtr db = IntPtr.Zero;
		IntPtr stmt = IntPtr.Zero;
		int num = sqlite3_open_v2(dbPath, out db, 163841, IntPtr.Zero);
		if (num != 0)
		{
			throw new Exception($"sqlite3_open failed: {num} for {dbPath}");
		}
		try
		{
			num = sqlite3_prepare_v2(db, sql, -1, out stmt, out var _);
			if (num != 0)
			{
				string arg = Marshal.PtrToStringAnsi(sqlite3_errmsg(db)) ?? "?";
				throw new Exception($"sqlite3_prepare failed ({num}): {arg}");
			}
			while (sqlite3_step(stmt) == 100)
			{
				IntPtr capturedStmt = stmt;
				rowCallback(delegate(int col)
				{
					if (sqlite3_column_type(capturedStmt, col) == 5)
					{
						return (object)null;
					}
					IntPtr intPtr = sqlite3_column_text(capturedStmt, col);
					return (!(intPtr == IntPtr.Zero)) ? Marshal.PtrToStringAnsi(intPtr) : null;
				});
			}
		}
		finally
		{
			if (stmt != IntPtr.Zero)
			{
				sqlite3_finalize(stmt);
			}
			if (db != IntPtr.Zero)
			{
				sqlite3_close(db);
			}
		}
	}

	public static long GetLong(Func<int, object> read, int col, long @default = 0L)
	{
		object obj = read(col);
		if (obj == null)
		{
			return @default;
		}
		if (!long.TryParse(obj.ToString(), out var result))
		{
			return @default;
		}
		return result;
	}

	public static int GetInt(Func<int, object> read, int col, int @default = 0)
	{
		object obj = read(col);
		if (obj == null)
		{
			return @default;
		}
		if (!int.TryParse(obj.ToString(), out var result))
		{
			return @default;
		}
		return result;
	}

	public static string GetString(Func<int, object> read, int col)
	{
		return read(col)?.ToString();
	}
}
