using Microsoft.Data.Sqlite;

namespace FinGuard.Api.Data;

/// <summary>Normalises SQLite connection strings so relative file paths never depend on the process working directory.</summary>
public static class SqlitePaths
{
    public const string Default = "Data Source=App_Data/finguard.db";

    /// <summary>
    /// Anchors a relative <c>Data Source</c> to <paramref name="contentRoot"/> and makes sure its folder exists.
    /// In-memory databases and absolute paths are left unchanged.
    /// </summary>
    public static string Resolve(string connectionString, string contentRoot)
    {
        var b = new SqliteConnectionStringBuilder(connectionString);
        var source = b.DataSource;
        var inMemory = string.IsNullOrWhiteSpace(source) || source == ":memory:" || b.Mode == SqliteOpenMode.Memory;
        if (inMemory) return b.ToString();

        if (!Path.IsPathRooted(source)) b.DataSource = Path.GetFullPath(Path.Combine(contentRoot, source));
        var dir = Path.GetDirectoryName(b.DataSource);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        return b.ToString();
    }
}
