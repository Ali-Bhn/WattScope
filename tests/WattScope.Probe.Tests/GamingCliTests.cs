using WattScope.Core.Gaming;
using WattScope.Probe;
using WattScope.Probe.Commands;
using WattScope.Storage;

namespace WattScope.Probe.Tests;

public class GamingCliTests
{
    [Fact]
    public void GameMark_ParsesExecutable_Name_AndDatabase()
    {
        var o = CliOptions.Parse(["--game-mark", "cod.exe", "--name", "Call of Duty: Warzone", "--db", "x.db"]);

        Assert.Null(o.ParseError);
        Assert.Equal(CliCommand.GameMark, o.Command);
        Assert.Equal("cod.exe", o.GameExecutable);
        Assert.Equal("Call of Duty: Warzone", o.GameName);
        Assert.Equal("x.db", o.DatabasePath);
        Assert.False(o.NeedsHardware);                         // database-only: never touches sensors
    }

    [Theory]
    [InlineData("--game-mark")]
    [InlineData("--game-exclude")]
    [InlineData("--game-unmark")]
    public void MappingCommands_RequireAnExecutable(string flag)
    {
        Assert.NotNull(CliOptions.Parse([flag]).ParseError);
        Assert.NotNull(CliOptions.Parse([flag, "--db", "x.db"]).ParseError);
    }

    [Fact]
    public void Name_IsOnlyAcceptedWithGameMark()
    {
        Assert.NotNull(CliOptions.Parse(["--game-exclude", "a.exe", "--name", "X"]).ParseError);
        Assert.NotNull(CliOptions.Parse(["--gaming", "--name", "X"]).ParseError);
    }

    [Fact]
    public void Gaming_IsADatabaseOnlyCommand()
    {
        var o = CliOptions.Parse(["--gaming"]);

        Assert.Null(o.ParseError);
        Assert.Equal(CliCommand.Gaming, o.Command);
        Assert.False(o.NeedsHardware);
    }

    [Fact]
    public void MarkExcludeAndUnmark_ChangeTheStoredMappings_AndRenameStoredSessions()
    {
        using var db = new TempDb();
        using var store = db.Open();
        var monitoring = store.CreateSession(new WattScope.Core.Monitoring.NewSessionRequest { StartedUtc = DateTimeOffset.UtcNow, Estimation = new WattScope.Core.Estimation.EstimationSettings(), Sensors = [] });
        var id = store.CreateGamingSession(new NewGamingSession
        {
            MonitoringSessionId = monitoring.Id,
            IsManual = false,
            StartedUtc = DateTimeOffset.UtcNow,
            Game = new GameIdentity { Key = "cod.exe", ExeName = "cod.exe", DisplayName = "cod", Source = GameDetectionSource.KnownCatalog, Tier = DetectionTier.Mapped }
        });

        using (var quiet = new StringWriter())
        {
            var original = Console.Out;
            Console.SetOut(quiet);
            try
            {
                Assert.Equal(0, GamingCommands.Mapping(CliOptions.Parse(["--game-mark", "COD.exe", "--name", "Call of Duty"]), store));
                Assert.Equal("Call of Duty", store.GetGamingSession(id)!.DisplayName);                          // history follows the rename

                Assert.Equal(0, GamingCommands.Mapping(CliOptions.Parse(["--game-exclude", "tool.exe"]), store));
                Assert.Equal(2, store.GetGameMappings().Count);

                Assert.Equal(0, GamingCommands.Mapping(CliOptions.Parse(["--game-unmark", "tool.exe"]), store));
                Assert.Single(store.GetGameMappings());

                Assert.Equal(0, GamingCommands.List(store));
                Assert.Contains("Call of Duty", quiet.ToString());
            }
            finally
            {
                Console.SetOut(original);
            }
        }
    }

    [Fact]
    public void InvalidExecutable_IsReported_NotThrown()
    {
        using var db = new TempDb();
        using var store = db.Open();
        var original = Console.Error;
        Console.SetError(new StringWriter());
        try
        {
            Assert.Equal(1, GamingCommands.Mapping(CliOptions.Parse(["--game-mark", "bad|name.exe"]), store));
        }
        finally
        {
            Console.SetError(original);
        }
    }
}
