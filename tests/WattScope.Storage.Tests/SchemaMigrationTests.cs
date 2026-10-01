using Microsoft.Data.Sqlite;

namespace WattScope.Storage.Tests;

public class SchemaMigrationTests
{
    private static object? Pragma(string path, string pragma)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {pragma};";
        return command.ExecuteScalar();
    }

    [Fact]
    public void FreshDatabase_IsMigratedToLatestVersion_WithWalAndIncrementalVacuum()
    {
        using var db = new TestDatabase();

        using (var store = db.Open())
            Assert.Equal(3, store.SchemaVersion);

        Assert.Equal(3L, Pragma(db.FilePath, "user_version"));
        Assert.Equal("wal", Pragma(db.FilePath, "journal_mode"));
        Assert.Equal(2L, Pragma(db.FilePath, "auto_vacuum")); // 2 = INCREMENTAL
    }

    [Fact]
    public void ReopeningAnExistingDatabase_IsIdempotent_AndKeepsData()
    {
        using var db = new TestDatabase();
        using (var store = db.Open())
            store.SetRawRetentionDays(3);

        using (var store = db.Open())
        {
            Assert.Equal(3, store.SchemaVersion);
            Assert.Equal(3, store.GetRawRetentionDays());
            Assert.Equal("ok", store.CheckIntegrity());
        }
    }

    [Fact]
    public void DatabaseFromANewerVersion_IsRefused_AndNotModified()
    {
        using var db = new TestDatabase();
        using (db.Open()) { }
        using (var connection = new SqliteConnection($"Data Source={db.FilePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<DatabaseSchemaTooNewException>(db.Open);

        Assert.Equal(99, ex.FoundVersion);
        Assert.Equal(99L, Pragma(db.FilePath, "user_version"));
    }
}
