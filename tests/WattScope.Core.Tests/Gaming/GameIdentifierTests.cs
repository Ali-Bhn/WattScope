using WattScope.Core.Gaming;
using static WattScope.Core.Tests.TestSupport.GamingRig;

namespace WattScope.Core.Tests.Gaming;

public class GameIdentifierTests
{
    private static IdentificationResult Identify(GameIdentifier identifier, ProcessInfo process, params ProcessInfo[] others)
    {
        var byPid = others.Append(process).ToDictionary(p => p.Pid);
        return identifier.Identify(process, byPid);
    }

    private sealed class FakeMetadata(string? product) : IExecutableMetadata
    {
        public string? GetProductName(string exePath) => product;
    }

    // ---------------------------------------------------------------- candidate detection

    [Fact]
    public void KnownExecutable_IsRecognised_AsMappedCatalogGame()
    {
        var result = Identify(new GameIdentifier(), Proc(10, "cod.exe", @"D:\Games\Call of Duty\cod.exe"));

        Assert.True(result.IsGame);
        Assert.Equal("cod.exe", result.Identity!.Key);
        Assert.Equal("Call of Duty", result.Identity.DisplayName);
        Assert.Equal(GameDetectionSource.KnownCatalog, result.Identity.Source);
        Assert.Equal(DetectionTier.Mapped, result.Identity.Tier);
    }

    [Fact]
    public void UserMapping_MarksAnArbitraryExecutableAsAGame_AndSuppliesTheName()
    {
        var identifier = new GameIdentifier();
        identifier.SetMappings([new GameMapping { ExeName = "MyGame.exe", Kind = GameMappingKind.Game, DisplayName = "My Game" }]);

        var result = Identify(identifier, Proc(10, "mygame.exe", @"C:\Stuff\mygame.exe"));

        Assert.Equal(GameDetectionSource.UserMapping, result.Identity!.Source);
        Assert.Equal("My Game", result.Identity.DisplayName);
    }

    [Fact]
    public void UserMapping_WinsOverBuiltInNeverGameRules()
    {
        var identifier = new GameIdentifier();
        identifier.SetMappings([new GameMapping { ExeName = "chrome.exe", Kind = GameMappingKind.Game, DisplayName = "Browser Game" }]);

        Assert.True(Identify(identifier, Proc(10, "chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe")).IsGame);
    }

    [Fact]
    public void PathSpecificMapping_BeatsNameOnlyMapping()
    {
        var identifier = new GameIdentifier();
        identifier.SetMappings(
        [
            new GameMapping { ExeName = "game.exe", Kind = GameMappingKind.Game, DisplayName = "Any Game" },
            new GameMapping { ExeName = "game.exe", ExePath = @"E:\Other\game.exe", Kind = GameMappingKind.Excluded }
        ]);

        Assert.Equal("Any Game", Identify(identifier, Proc(1, "game.exe", @"D:\Games\game.exe")).Identity!.DisplayName);
        Assert.Equal(RejectionReason.UserExcluded, Identify(identifier, Proc(2, "game.exe", @"E:\OTHER\Game.exe")).Reason);
    }

    [Fact]
    public void LauncherChild_OutsideTheLaunchersOwnFolder_IsAHeuristicCandidate()
    {
        var launcher = Proc(5, "Battle.net.exe", @"C:\Program Files (x86)\Battle.net\Battle.net.exe");
        var child = Proc(10, "SomeShooter.exe", @"D:\Games\SomeShooter\SomeShooter.exe", parent: 5);

        var result = Identify(new GameIdentifier(), child, launcher);

        Assert.Equal(GameDetectionSource.LauncherChild, result.Identity!.Source);
        Assert.Equal(DetectionTier.Heuristic, result.Identity.Tier);
        Assert.Equal("SomeShooter", result.Identity.DisplayName);
    }

    [Fact]
    public void GameInALibraryFolder_IsAHeuristicCandidate_EvenWithoutALauncherParent()
    {
        var result = Identify(new GameIdentifier(), Proc(10, "Hades.exe", @"C:\Program Files (x86)\Steam\steamapps\common\Hades\x64\Hades.exe"));

        Assert.Equal(GameDetectionSource.LibraryPath, result.Identity!.Source);
    }

    [Fact]
    public void ProductName_FromVersionMetadata_IsUsedToNameAHeuristicGame()
    {
        var identifier = new GameIdentifier(new FakeMetadata("Hades II"));

        var result = Identify(identifier, Proc(10, "hades2.exe", @"C:\Steam\steamapps\common\Hades II\hades2.exe"));

        Assert.Equal("Hades II", result.Identity!.DisplayName);
    }

    [Fact]
    public void GenericProductName_IsNotUsed()
    {
        var identifier = new GameIdentifier(new FakeMetadata("Microsoft(R) Windows(R) Operating System"));

        var result = Identify(identifier, Proc(10, "rogue_like.exe", @"C:\Steam\steamapps\common\Rogue\rogue_like.exe"));

        Assert.Equal("rogue like", result.Identity!.DisplayName);
    }

    // ---------------------------------------------------------------- candidate rejection

    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("msedge.exe")]
    [InlineData("discord.exe")]
    [InlineData("obs64.exe")]
    [InlineData("steam.exe")]
    [InlineData("Battle.net.exe")]
    [InlineData("EpicGamesLauncher.exe")]
    [InlineData("explorer.exe")]
    [InlineData("wattscope.exe")]
    public void LaunchersBrowsersChatCaptureAndWattScope_AreNeverGames(string exe)
    {
        var result = Identify(new GameIdentifier(), Proc(10, exe, $@"C:\Program Files\Something\{exe}"));

        Assert.False(result.IsGame);
        Assert.Equal(RejectionReason.NeverGame, result.Reason);
    }

    [Fact]
    public void LauncherHelperInsideTheLaunchersOwnFolder_IsRejected()
    {
        var launcher = Proc(5, "Battle.net.exe", @"C:\Program Files (x86)\Battle.net\Battle.net.exe");
        var tool = Proc(10, "BattleNetTool.exe", @"C:\Program Files (x86)\Battle.net\Versions\tool\BattleNetTool.exe", parent: 5);

        var result = Identify(new GameIdentifier(), tool, launcher);

        Assert.Equal(RejectionReason.LauncherOwnFolder, result.Reason);
    }

    [Theory]
    [InlineData("UnityCrashHandler64.exe")]
    [InlineData("vc_redist.x64.exe")]
    [InlineData("DXSETUP.exe")]
    [InlineData("EasyAntiCheat_Setup.exe")]
    [InlineData("CrashReportClient.exe")]
    public void HelpersInsideALibraryFolder_AreRejected(string helper)
    {
        var result = Identify(new GameIdentifier(), Proc(10, helper, $@"C:\Steam\steamapps\common\Game\{helper}"));

        Assert.False(result.IsGame);
        Assert.Equal(RejectionReason.HelperName, result.Reason);
    }

    [Fact]
    public void UnknownProgramWithNoSignal_IsNotAGame_EvenIfItIsBig()
    {
        // No launcher parent, no library folder, not in the catalog: GPU use or fullscreen would not change this (it is never looked at).
        var result = Identify(new GameIdentifier(), Proc(10, "render_tool.exe", @"C:\Tools\render_tool.exe"));

        Assert.Equal(RejectionReason.NoSignal, result.Reason);
    }

    [Fact]
    public void ProcessWithoutAKnownPath_IsNotClassifiedByHeuristics()
    {
        var launcher = Proc(5, "steam.exe", @"C:\Steam\steam.exe");
        var child = Proc(10, "mystery.exe", path: null, parent: 5);

        Assert.Equal(RejectionReason.NoSignal, Identify(new GameIdentifier(), child, launcher).Reason);
    }

    [Fact]
    public void OperatingSystemFolder_IsRejected()
    {
        var result = Identify(new GameIdentifier(), Proc(10, "game_like.exe", @"C:\Windows\System32\game_like.exe"));

        Assert.Equal(RejectionReason.SystemPath, result.Reason);
    }

    [Fact]
    public void ReusedParentPid_StartedAfterTheChild_IsNotALauncherAncestor()
    {
        var launcher = Proc(5, "steam.exe", @"C:\Steam\steam.exe", start: At(1000));   // a *new* process that reused pid 5
        var child = Proc(10, "oldchild.exe", @"D:\Apps\oldchild.exe", parent: 5, start: At(10));

        Assert.Equal(RejectionReason.NoSignal, Identify(new GameIdentifier(), child, launcher).Reason);
    }

    // ---------------------------------------------------------------- exclusion

    [Fact]
    public void Exclusion_BeatsTheCatalog()
    {
        var identifier = new GameIdentifier();
        identifier.SetMappings([new GameMapping { ExeName = "COD.EXE", Kind = GameMappingKind.Excluded }]);

        var result = Identify(identifier, Proc(10, "cod.exe", @"D:\cod.exe"));

        Assert.False(result.IsGame);
        Assert.Equal(RejectionReason.UserExcluded, result.Reason);
    }

    [Fact]
    public void ChangingMappings_BumpsTheVersion_SoCachedVerdictsCanBeDropped()
    {
        var identifier = new GameIdentifier();
        var before = identifier.MappingsVersion;

        identifier.SetMappings([]);

        Assert.True(identifier.MappingsVersion > before);
    }

    [Fact]
    public void IsDescendantOf_FollowsTheProcessTree()
    {
        var game = Proc(10, "game.exe", parent: 1);
        var helper = Proc(11, "helper.exe", parent: 10);
        var grandchild = Proc(12, "cef.exe", parent: 11);
        var other = Proc(20, "other.exe", parent: 1);
        var byPid = new[] { game, helper, grandchild, other }.ToDictionary(p => p.Pid);

        Assert.True(GameIdentifier.IsDescendantOf(grandchild, 10, byPid));
        Assert.False(GameIdentifier.IsDescendantOf(other, 10, byPid));
    }
}
