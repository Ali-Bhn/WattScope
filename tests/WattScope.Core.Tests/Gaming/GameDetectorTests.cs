using WattScope.Core.Gaming;
using static WattScope.Core.Tests.TestSupport.GamingRig;

namespace WattScope.Core.Tests.Gaming;

public class GameDetectorTests
{
    private static readonly ProcessInfo Cod = Proc(100, "cod.exe", @"D:\Games\Call of Duty\cod.exe");
    private static readonly ProcessInfo Launcher = Proc(5, "Battle.net.exe", @"C:\Program Files (x86)\Battle.net\Battle.net.exe");
    private static readonly ProcessInfo Chrome = Proc(7, "chrome.exe", @"C:\Program Files\Google\Chrome\chrome.exe");

    private static GameDetector NewDetector(GameDetectorOptions? options = null) => new(options ?? new GameDetectorOptions(), new GameIdentifier());

    private static IReadOnlyList<DetectorEvent> Observe(GameDetector d, double t, params ProcessInfo[] processes) => d.Observe(At(t), processes);

    private static DetectorEventKind[] Kinds(IEnumerable<DetectorEvent> events) => events.Select(e => e.Kind).ToArray();

    // ---------------------------------------------------------------- start

    [Fact]
    public void NoProcesses_NothingHappens()
    {
        var d = NewDetector();

        Assert.Empty(Observe(d, 0));
        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }

    [Fact]
    public void LauncherAlone_NeverStartsAnything()
    {
        var d = NewDetector();

        for (var t = 0; t <= 600; t += 5)
            Assert.Empty(Observe(d, t, Launcher, Chrome));

        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }

    [Fact]
    public void KnownGame_BecomesACandidate_ThenConfirmsAfterTheMappedDelay()
    {
        var d = NewDetector();

        Assert.Equal([DetectorEventKind.CandidateStarted], Kinds(Observe(d, 0, Launcher, Cod)));
        Assert.Equal(DetectorPhase.Candidate, d.Phase);

        Assert.Empty(Observe(d, 15, Launcher, Cod));                               // 15 s < 20 s
        var confirmed = Observe(d, 20, Launcher, Cod);

        Assert.Equal([DetectorEventKind.SessionConfirmed], Kinds(confirmed));
        Assert.Equal(DetectorPhase.Confirmed, d.Phase);
        Assert.Equal(At(0), confirmed[0].Context.FirstSeenUtc);                    // the session is dated from first sight, not from confirmation
    }

    [Fact]
    public void TransientProcess_NeverCreatesASession()
    {
        var d = NewDetector();

        Observe(d, 0, Cod);
        var vanished = Observe(d, 5);                                              // gone after 5 s

        Assert.Equal([DetectorEventKind.CandidateRejected], Kinds(vanished));
        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }

    [Fact]
    public void HeuristicCandidate_NeedsTheLongerDelay()
    {
        var d = NewDetector();
        var child = Proc(10, "SomeShooter.exe", @"D:\Games\SomeShooter\SomeShooter.exe", parent: 5);

        Observe(d, 0, Launcher, child);
        Assert.Empty(Observe(d, 30, Launcher, child));                             // enough for a catalog game, not for a guess
        Assert.Empty(Observe(d, 59, Launcher, child));
        Assert.Equal([DetectorEventKind.SessionConfirmed], Kinds(Observe(d, 60, Launcher, child)));
    }

    [Fact]
    public void MappedGame_ReplacesAHeuristicGuess_WhileItIsStillBeingConfirmed()
    {
        var d = NewDetector();
        var guess = Proc(10, "SomeShooter.exe", @"D:\Games\SomeShooter\SomeShooter.exe", parent: 5);

        Observe(d, 0, Launcher, guess);
        var events = Observe(d, 10, Launcher, guess, Cod);

        Assert.Equal([DetectorEventKind.CandidateRejected, DetectorEventKind.CandidateStarted], Kinds(events));
        Assert.Equal("cod.exe", d.Context!.Identity.Key);
    }

    // ---------------------------------------------------------------- user mappings

    [Fact]
    public void UserMapping_MakesAnUnknownExecutableDetectable()
    {
        var d = NewDetector();
        var custom = Proc(50, "indie.exe", @"C:\Tools\indie.exe");
        Observe(d, 0, custom);
        Assert.Equal(DetectorPhase.NoGame, d.Phase);

        d.ApplyMappings(At(1), [new GameMapping { ExeName = "indie.exe", Kind = GameMappingKind.Game, DisplayName = "Indie Game" }]);
        Observe(d, 2, custom);

        Assert.Equal(DetectorPhase.Candidate, d.Phase);
        Assert.Equal("Indie Game", d.Context!.Identity.DisplayName);
    }

    [Fact]
    public void Exclusion_StopsAKnownGameFromBeingDetected()
    {
        var d = NewDetector();
        d.ApplyMappings(At(0), [new GameMapping { ExeName = "cod.exe", Kind = GameMappingKind.Excluded }]);

        Assert.Empty(Observe(d, 1, Cod));
        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }

    [Fact]
    public void ExcludingARunningGame_EndsItAtOnce_WithReasonExcluded()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);

        var events = d.ApplyMappings(At(25), [new GameMapping { ExeName = "cod.exe", Kind = GameMappingKind.Excluded }]);

        var ended = Assert.Single(events);
        Assert.Equal(DetectorEventKind.SessionEnded, ended.Kind);
        Assert.Equal(GamingEndReason.Excluded, ended.EndReason);
        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }

    [Fact]
    public void RenamingTheRunningGame_IsReported_AndKeepsTheSession()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);

        var events = d.ApplyMappings(At(30), [new GameMapping { ExeName = "cod.exe", Kind = GameMappingKind.Game, DisplayName = "Call of Duty: Warzone" }]);

        Assert.Equal([DetectorEventKind.NameChanged], Kinds(events));
        Assert.Equal("Call of Duty: Warzone", d.Context!.Identity.DisplayName);
        Assert.Equal(DetectorPhase.Confirmed, d.Phase);
    }

    // ---------------------------------------------------------------- grace period

    [Fact]
    public void GameDisappearing_StartsGrace_AndEndsAfterTheGracePeriod_DatedFromWhenItWasMissed()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);
        Observe(d, 100, Cod);

        Assert.Equal([DetectorEventKind.GraceStarted], Kinds(Observe(d, 105)));
        Assert.Equal(DetectorPhase.EndingGrace, d.Phase);
        Assert.Empty(Observe(d, 120));                                             // 15 s into a 30 s grace

        var end = Observe(d, 135);                                                 // 30 s after it was missed
        var ended = Assert.Single(end);
        Assert.Equal(DetectorEventKind.SessionEnded, ended.Kind);
        Assert.Equal(GamingEndReason.ProcessExited, ended.EndReason);
        Assert.Equal(At(105), ended.EndedUtc);                                     // not 135: the grace period is not gaming time
    }

    [Fact]
    public void ShortRestartWithinGrace_DoesNotSplitTheSession()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);
        Observe(d, 50, Cod);

        Observe(d, 55);                                                            // helper/game restarts...
        var resumed = Observe(d, 70, Proc(200, "cod.exe", @"D:\Games\Call of Duty\cod.exe"));   // ...with a new PID, 15 s later

        Assert.Equal([DetectorEventKind.GraceResumed], Kinds(resumed));
        Assert.Equal(DetectorPhase.Confirmed, d.Phase);
        Assert.Equal(At(0), d.Context!.FirstSeenUtc);                              // same context, same start
    }

    [Fact]
    public void RestartAfterTheGracePeriod_IsANewSession()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);
        Observe(d, 50);
        var ended = Observe(d, 80);

        Assert.Equal(DetectorEventKind.SessionEnded, ended.Single().Kind);
        Assert.Equal([DetectorEventKind.CandidateStarted], Kinds(Observe(d, 90, Cod)));
        Assert.Equal(At(90), d.Context!.FirstSeenUtc);
    }

    [Fact]
    public void LauncherActivity_AfterTheGameCloses_DoesNotKeepOrStartASession()
    {
        var d = NewDetector();
        Observe(d, 0, Launcher, Cod);
        Observe(d, 20, Launcher, Cod);
        Observe(d, 40, Launcher);                                                  // game gone, launcher still open
        var end = Observe(d, 70, Launcher);

        Assert.Equal(DetectorEventKind.SessionEnded, end.Single().Kind);
        Assert.Empty(Observe(d, 100, Launcher));
        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }

    // ---------------------------------------------------------------- multiple processes / one context

    [Fact]
    public void MultipleCandidates_ResolveDeterministically_StrongestSourceFirst()
    {
        var shooter = Proc(10, "SomeShooter.exe", @"D:\Games\SomeShooter\SomeShooter.exe", parent: 5);
        var libraryGame = Proc(11, "Hades.exe", @"C:\Steam\steamapps\common\Hades\Hades.exe");

        var forward = NewDetector();
        Observe(forward, 0, Launcher, shooter, libraryGame, Cod);
        var reverse = NewDetector();
        Observe(reverse, 0, Cod, libraryGame, shooter, Launcher);

        Assert.Equal("cod.exe", forward.Context!.Identity.Key);                    // catalog beats launcher-child beats library
        Assert.Equal("cod.exe", reverse.Context!.Identity.Key);                    // independent of enumeration order
    }

    [Fact]
    public void TwoEquallyStrongCandidates_AreOrderedByName_NotByEnumerationOrder()
    {
        var a = Proc(10, "alpha.exe", @"C:\Steam\steamapps\common\A\alpha.exe");
        var b = Proc(11, "beta.exe", @"C:\Steam\steamapps\common\B\beta.exe");

        var one = NewDetector();
        Observe(one, 0, a, b);
        var two = NewDetector();
        Observe(two, 0, b, a);

        Assert.Equal("alpha.exe", one.Context!.Identity.Key);
        Assert.Equal("alpha.exe", two.Context!.Identity.Key);
    }

    [Fact]
    public void OnlyOneGameContext_ASecondRunningGameIsIgnored()
    {
        var d = NewDetector();
        var other = Proc(300, "eldenring.exe", @"D:\Games\EldenRing\eldenring.exe");
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);

        var events = Observe(d, 30, Cod, other);

        Assert.Empty(events);
        Assert.Equal("cod.exe", d.Context!.Identity.Key);
    }

    [Fact]
    public void WhenOneGameCloses_AnotherStillRunning_StartsItsOwnCandidate_AfterTheFirstEnded()
    {
        var d = NewDetector();
        var other = Proc(300, "eldenring.exe", @"D:\Games\EldenRing\eldenring.exe");
        Observe(d, 0, Cod, other);
        Observe(d, 20, Cod, other);
        Observe(d, 40, other);                                                     // cod closed; grace begins at 40
        var events = Observe(d, 70, other);                                        // grace over: cod ended, elden ring becomes a candidate

        Assert.Equal([DetectorEventKind.SessionEnded, DetectorEventKind.CandidateStarted], Kinds(events));
        Assert.Equal(At(40), events[0].EndedUtc);
        Assert.Equal(At(70), events[1].Context.FirstSeenUtc);                      // not back-dated into the first session's time
    }

    [Fact]
    public void HelperProcessesOfTheRunningGame_DoNotCreateAnotherContext()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);
        var helper = Proc(101, "cod_helper.exe", @"D:\Games\Call of Duty\cod_helper.exe", parent: 100);

        Assert.Empty(Observe(d, 25, Cod, helper));
        Assert.Equal(DetectorPhase.Confirmed, d.Phase);
    }

    // ---------------------------------------------------------------- manual sessions

    [Fact]
    public void ManualSession_StartsConfirmed_AndIgnoresProcessesUntilStopped()
    {
        var d = NewDetector();

        var started = d.StartManual(At(0), "Retro Game", "retro.exe");
        Assert.Equal([DetectorEventKind.SessionConfirmed], Kinds(started));
        Assert.Equal(GameDetectionSource.Manual, d.Context!.Identity.Source);
        Assert.True(d.Context.IsManual);

        Assert.Empty(Observe(d, 10));                                              // no process: still running
        Assert.Empty(Observe(d, 100, Cod));                                        // a real game: ignored

        var ended = d.StopManual(At(200)).Single();
        Assert.Equal(GamingEndReason.ManualStop, ended.EndReason);
        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }

    [Fact]
    public void ManualStart_IsRefused_WhileAGameSessionIsRunning()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);

        Assert.Throws<InvalidOperationException>(() => d.StartManual(At(30), "Another"));
        Assert.Equal("cod.exe", d.Context!.Identity.Key);                          // the existing session is untouched
    }

    [Fact]
    public void ManualStart_SupersedesAPendingCandidate()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);

        var events = d.StartManual(At(5), "My Session");

        Assert.Equal([DetectorEventKind.CandidateRejected, DetectorEventKind.SessionConfirmed], Kinds(events));
        Assert.True(d.Context!.IsManual);
    }

    [Fact]
    public void StopManual_DoesNothing_ForAnAutomaticSession()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);

        Assert.Empty(d.StopManual(At(30)));
        Assert.Equal(DetectorPhase.Confirmed, d.Phase);
    }

    [Fact]
    public void ManualStart_RequiresAName()
    {
        Assert.Throws<ArgumentException>(() => NewDetector().StartManual(At(0), "  "));
    }

    // ---------------------------------------------------------------- reset / sleep

    [Fact]
    public void Reset_EndsARunningSession_WithTheGivenReason_AndDropsACandidate()
    {
        var running = NewDetector();
        Observe(running, 0, Cod);
        Observe(running, 20, Cod);
        Assert.Equal(GamingEndReason.MonitoringStopped, running.Reset(At(30)).Single().EndReason);

        var pending = NewDetector();
        Observe(pending, 0, Cod);
        Assert.Equal([DetectorEventKind.CandidateRejected], Kinds(pending.Reset(At(5))));
    }

    [Fact]
    public void Reset_DuringGrace_EndsAtTheMomentTheProcessWasMissed()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);
        Observe(d, 50);

        var ended = d.Reset(At(60)).Single();

        Assert.Equal(At(50), ended.EndedUtc);
    }

    [Fact]
    public void DropCandidate_LeavesARunningSessionAlone()
    {
        var d = NewDetector();
        Observe(d, 0, Cod);
        Observe(d, 20, Cod);

        Assert.Empty(d.DropCandidate(At(25)));
        Assert.Equal(DetectorPhase.Confirmed, d.Phase);
    }

    [Fact]
    public void ZeroConfirmDelay_ConfirmsOnFirstSight()
    {
        var d = NewDetector(new GameDetectorOptions { ConfirmDelayMapped = TimeSpan.Zero });

        Assert.Equal([DetectorEventKind.CandidateStarted, DetectorEventKind.SessionConfirmed], Kinds(Observe(d, 0, Cod)));
    }

    [Fact]
    public void ProcessCache_IsBounded_ToWhatIsRunning()
    {
        var d = NewDetector();
        // 2000 short-lived processes, one poll each: nothing accumulates (this would be a memory leak if the cache kept them).
        for (var i = 0; i < 2000; i++)
            Observe(d, i, Proc(1000 + i, "short.exe", @"C:\Tools\short.exe"));

        Assert.Equal(DetectorPhase.NoGame, d.Phase);
    }
}
