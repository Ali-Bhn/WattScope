namespace WattScope.Storage.Tests;

/// <summary>Runs alone: it changes a process-wide environment variable.</summary>
[Collection("EnvironmentVariables")]
public class DatabasePathTests
{
    [Fact]
    public void TheDefaultDatabasePath_IsTheKnownFolder_UnlessTheEnvironmentVariableOverridesIt()
    {
        var original = Environment.GetEnvironmentVariable(SqliteEnergyStore.DatabasePathEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(SqliteEnergyStore.DatabasePathEnvironmentVariable, null);
            var normal = SqliteEnergyStore.DefaultDatabasePath;
            Assert.EndsWith(Path.Combine("WattScope", "wattscope.db"), normal);

            var scratch = Path.Combine(Path.GetTempPath(), "wattscope-env-override.db");
            Environment.SetEnvironmentVariable(SqliteEnergyStore.DatabasePathEnvironmentVariable, scratch);
            Assert.Equal(scratch, SqliteEnergyStore.DefaultDatabasePath);

            Environment.SetEnvironmentVariable(SqliteEnergyStore.DatabasePathEnvironmentVariable, "");
            Assert.Equal(normal, SqliteEnergyStore.DefaultDatabasePath);       // an empty value is "not set"
        }
        finally
        {
            Environment.SetEnvironmentVariable(SqliteEnergyStore.DatabasePathEnvironmentVariable, original);
        }
    }
}
