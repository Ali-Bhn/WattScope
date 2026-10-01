namespace WattScope.Core.Gaming;

/// <summary>
/// Decides whether one running process is a game, from passive facts only: its
/// executable name and path, who started it (the process tree), and the user's
/// own mappings. Layers, strongest first:
/// <list type="number">
/// <item>User mapping (mark as game / exclude). An exclusion always wins.</item>
/// <item>Known-executable catalog.</item>
/// <item>Launcher metadata: a non-helper executable started by a game launcher and living outside the launcher's own folder.</item>
/// <item>Game-library folder (steamapps\common, Epic Games, ...).</item>
/// <item>Executable version metadata is used to <em>name</em> a game (the product name), not to classify one.</item>
/// </list>
/// What it never does: treat a process as a game because it uses the GPU or is
/// fullscreen, treat a launcher (or a browser, chat or capture tool) as a game,
/// or inspect anything inside the process. Pure and deterministic given its inputs.
/// </summary>
public sealed class GameIdentifier
{
    private const int MaxAncestorDepth = 10;

    private readonly IExecutableMetadata? _metadata;
    private Dictionary<(string Name, string Path), GameMapping> _mappings = new();

    public GameIdentifier(IExecutableMetadata? metadata = null)
    {
        _metadata = metadata;
    }

    /// <summary>Incremented whenever the mappings change, so callers can invalidate cached verdicts.</summary>
    public int MappingsVersion { get; private set; }

    public void SetMappings(IEnumerable<GameMapping> mappings)
    {
        var next = new Dictionary<(string, string), GameMapping>();
        foreach (var mapping in mappings)
            next[(mapping.ExeName.ToLowerInvariant(), (mapping.ExePath ?? "").ToLowerInvariant())] = mapping;
        _mappings = next;
        MappingsVersion++;
    }

    /// <summary>The user mapping that applies to this executable: a path-specific one first, then a name-only one.</summary>
    public GameMapping? FindMapping(string exeName, string? exePath)
    {
        var name = exeName.ToLowerInvariant();
        if (!string.IsNullOrEmpty(exePath) && _mappings.TryGetValue((name, exePath.ToLowerInvariant()), out var specific))
            return specific;
        return _mappings.TryGetValue((name, ""), out var general) ? general : null;
    }

    public IdentificationResult Identify(ProcessInfo process, IReadOnlyDictionary<int, ProcessInfo> byPid)
    {
        var name = process.ExeName.ToLowerInvariant();

        // 1. The user's word is final.
        if (FindMapping(process.ExeName, process.ExePath) is { } mapping)
        {
            if (mapping.Kind == GameMappingKind.Excluded)
                return new IdentificationResult(null, RejectionReason.UserExcluded);
            return Game(process, GameDetectionSource.UserMapping, DetectionTier.Mapped, mapping.DisplayName);
        }

        // Everyday software and launchers are never games by built-in rules.
        if (GameCatalog.NeverGame.Contains(name) || GameCatalog.Launchers.Contains(name))
            return new IdentificationResult(null, RejectionReason.NeverGame);

        // 2. Known executable.
        if (GameCatalog.KnownGames.TryGetValue(name, out var knownName))
            return Game(process, GameDetectionSource.KnownCatalog, DetectionTier.Mapped, knownName);

        // Heuristics need a path: without one we cannot tell a game from a helper in a launcher's folder.
        var path = process.ExePath;
        if (string.IsNullOrEmpty(path))
            return new IdentificationResult(null, RejectionReason.NoSignal);
        if (GameCatalog.SystemPathPattern.IsMatch(path))
            return new IdentificationResult(null, RejectionReason.SystemPath);
        if (LooksLikeHelper(name))
            return new IdentificationResult(null, RejectionReason.HelperName);

        // 4. Game-library folder.
        if (GameCatalog.LibraryPathPatterns.Any(p => p.IsMatch(path)))
            return Game(process, GameDetectionSource.LibraryPath, DetectionTier.Heuristic, null);

        // 3. Started by a game launcher, but not part of the launcher itself.
        if (FindLauncherAncestor(process, byPid) is { } launcher)
        {
            if (launcher.ExePath is { Length: > 0 } launcherPath
                && Path.GetDirectoryName(launcherPath) is { Length: > 0 } launcherDir
                && path.StartsWith(launcherDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return new IdentificationResult(null, RejectionReason.LauncherOwnFolder);
            return Game(process, GameDetectionSource.LauncherChild, DetectionTier.Heuristic, null);
        }

        return new IdentificationResult(null, RejectionReason.NoSignal);
    }

    /// <summary>True when <paramref name="processInfo"/> is <paramref name="ancestorPid"/> or one of its descendants.</summary>
    public static bool IsDescendantOf(ProcessInfo processInfo, int ancestorPid, IReadOnlyDictionary<int, ProcessInfo> byPid)
    {
        var current = processInfo;
        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            if (current.Pid == ancestorPid)
                return true;
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || !IsPlausibleParent(parent, current))
                return false;
            current = parent;
        }
        return false;
    }

    private static ProcessInfo? FindLauncherAncestor(ProcessInfo process, IReadOnlyDictionary<int, ProcessInfo> byPid)
    {
        var current = process;
        for (var depth = 0; depth < MaxAncestorDepth; depth++)
        {
            if (!byPid.TryGetValue(current.ParentPid, out var parent) || !IsPlausibleParent(parent, current))
                return null;
            if (GameCatalog.Launchers.Contains(parent.ExeName))
                return parent;
            current = parent;
        }
        return null;
    }

    /// <summary>A parent PID can be reused after the real parent exited; a "parent" that started after its child is not one.</summary>
    private static bool IsPlausibleParent(ProcessInfo parent, ProcessInfo child) =>
        parent.Pid != child.Pid && (parent.StartUtc is not { } p || child.StartUtc is not { } c || p <= c);

    private static bool LooksLikeHelper(string lowerName)
    {
        var stem = Path.GetFileNameWithoutExtension(lowerName);
        foreach (var fragment in GameCatalog.HelperNameFragments)
            if (stem.Contains(fragment, StringComparison.Ordinal))
                return true;
        return false;
    }

    private IdentificationResult Game(ProcessInfo process, GameDetectionSource source, DetectionTier tier, string? name) => new(Build(process, source, tier, name), RejectionReason.None);

    private GameIdentity Build(ProcessInfo process, GameDetectionSource source, DetectionTier tier, string? name) => new()
    {
        Key = process.ExeName.ToLowerInvariant(),
        ExeName = process.ExeName,
        ExePath = process.ExePath,
        DisplayName = !string.IsNullOrWhiteSpace(name) ? name!.Trim() : DeriveDisplayName(process),
        Source = source,
        Tier = tier
    };

    private string DeriveDisplayName(ProcessInfo process)
    {
        if (!string.IsNullOrEmpty(process.ExePath) && _metadata?.GetProductName(process.ExePath) is { } product && IsPlausibleProductName(product))
            return product.Trim();
        return Prettify(process.ExeName);
    }

    private static bool IsPlausibleProductName(string product)
    {
        var p = product.Trim();
        return p.Length is > 0 and <= 60
            && !p.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
            && !p.Contains("Windows", StringComparison.OrdinalIgnoreCase)
            && !p.Contains("Unreal Engine", StringComparison.OrdinalIgnoreCase)
            && !p.Equals("Unity", StringComparison.OrdinalIgnoreCase);
    }

    private static string Prettify(string exeName)
    {
        var stem = Path.GetFileNameWithoutExtension(exeName).Replace('_', ' ').Replace('-', ' ').Trim();
        return stem.Length == 0 ? exeName : stem;
    }
}

