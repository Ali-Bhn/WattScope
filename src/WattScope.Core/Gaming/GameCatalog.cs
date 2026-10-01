using System.Text.RegularExpressions;

namespace WattScope.Core.Gaming;

/// <summary>
/// The built-in, deliberately small knowledge base used by <see cref="GameIdentifier"/>.
/// It is data, not behaviour: nothing in the detector is specific to any one game.
/// Everything here can be overridden per executable by the user (mark as game /
/// exclude / rename), and none of it involves looking inside a process.
/// </summary>
public static class GameCatalog
{
    /// <summary>Executable name (lower-case) -> display name, for well-known games. Intentionally not exhaustive; the user mappings cover the rest.</summary>
    public static readonly IReadOnlyDictionary<string, string> KnownGames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // Call of Duty family (Battle.net / Steam). The exact exe name differs by title and
        // version; users can add the one their install actually uses.
        ["cod.exe"] = "Call of Duty",
        ["modernwarfare.exe"] = "Call of Duty: Modern Warfare",
        ["blackopscoldwar.exe"] = "Call of Duty: Black Ops Cold War",
        ["cod23-cod.exe"] = "Call of Duty",
        ["eldenring.exe"] = "Elden Ring",
        ["cs2.exe"] = "Counter-Strike 2",
        ["dota2.exe"] = "Dota 2",
        ["r5apex.exe"] = "Apex Legends",
        ["fortniteclient-win64-shipping.exe"] = "Fortnite",
        ["valorant-win64-shipping.exe"] = "Valorant",
        ["overwatch.exe"] = "Overwatch 2",
        ["rocketleague.exe"] = "Rocket League",
        ["gta5.exe"] = "Grand Theft Auto V",
        ["rdr2.exe"] = "Red Dead Redemption 2",
        ["cyberpunk2077.exe"] = "Cyberpunk 2077",
        ["witcher3.exe"] = "The Witcher 3",
        ["bg3.exe"] = "Baldur's Gate 3",
        ["bg3_dx11.exe"] = "Baldur's Gate 3",
        ["starfield.exe"] = "Starfield",
        ["hogwartslegacy.exe"] = "Hogwarts Legacy",
        ["helldivers2.exe"] = "Helldivers 2",
        ["tslgame.exe"] = "PUBG: Battlegrounds",
        ["destiny2.exe"] = "Destiny 2",
        ["rainbowsix.exe"] = "Rainbow Six Siege",
        ["wow.exe"] = "World of Warcraft",
        ["diablo iv.exe"] = "Diablo IV",
        ["league of legends.exe"] = "League of Legends",
        ["palworld-win64-shipping.exe"] = "Palworld",
        ["factorygame.exe"] = "Satisfactory",
        ["minecraft.windows.exe"] = "Minecraft",
    };

    /// <summary>Game launchers and stores. Being running, or being the parent of something, never makes the launcher itself a game.</summary>
    public static readonly IReadOnlySet<string> Launchers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "battle.net.exe", "steam.exe", "epicgameslauncher.exe", "galaxyclient.exe", "eadesktop.exe", "ealauncher.exe",
        "origin.exe", "upc.exe", "ubisoftconnect.exe", "riotclientservices.exe", "xboxpcapp.exe", "gamelaunchhelper.exe",
    };

    /// <summary>
    /// Everyday software that is never classified as a game by the built-in rules
    /// (browsers, chat, capture, system, overlays, the launchers above, WattScope
    /// itself). A user mapping can still override any of these.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverGame = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Windows shell and services
        "explorer.exe", "svchost.exe", "system", "registry", "smss.exe", "csrss.exe", "wininit.exe", "services.exe", "lsass.exe",
        "winlogon.exe", "dwm.exe", "fontdrvhost.exe", "conhost.exe", "runtimebroker.exe", "searchhost.exe", "searchindexer.exe",
        "startmenuexperiencehost.exe", "shellexperiencehost.exe", "textinputhost.exe", "sihost.exe", "taskhostw.exe", "ctfmon.exe",
        "applicationframehost.exe", "taskmgr.exe", "msiexec.exe", "dllhost.exe", "wmiprvse.exe", "audiodg.exe", "spoolsv.exe",
        "securityhealthsystray.exe", "widgets.exe", "lockapp.exe", "systemsettings.exe", "backgroundtaskhost.exe",
        // Terminals and shells
        "cmd.exe", "powershell.exe", "pwsh.exe", "windowsterminal.exe", "wt.exe", "bash.exe",
        // Browsers
        "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe", "msedgewebview2.exe",
        // Chat, calls, productivity
        "discord.exe", "discordptb.exe", "discordcanary.exe", "slack.exe", "teams.exe", "ms-teams.exe", "zoom.exe", "telegram.exe",
        "whatsapp.exe", "signal.exe", "spotify.exe", "vlc.exe", "wmplayer.exe", "notepad.exe", "code.exe", "devenv.exe",
        "rider64.exe", "idea64.exe", "winword.exe", "excel.exe", "powerpnt.exe", "outlook.exe", "onedrive.exe", "dropbox.exe",
        // Capture / streaming / overlays / tuning (they run next to games, they are not the game)
        "obs64.exe", "obs32.exe", "obs.exe", "streamlabs obs.exe", "medal.exe", "nvidia share.exe", "nvcontainer.exe",
        "nvidia overlay.exe", "nvidia app.exe", "nvdisplay.container.exe", "radeonsoftware.exe", "msiafterburner.exe",
        "rtss.exe", "hwinfo64.exe", "hwinfo32.exe", "cpuz.exe", "gpuz.exe", "lghub.exe", "icue.exe", "razer synapse 3.exe",
        // Launcher / store helpers (the launchers themselves are in Launchers; also never games)
        "steamwebhelper.exe", "steamservice.exe", "epicwebhelper.exe", "agent.exe", "battle.net helper.exe", "uplaywebcore.exe",
        // WattScope
        "wattscope.exe", "wattscope-probe.exe", "wattscope-probe-x86_64-pc-windows-msvc.exe",
    };

    /// <summary>Substrings that mark an executable as support software, not the game (installers, crash reporters, anti-cheat services, ...). Applied to heuristic detections only.</summary>
    public static readonly IReadOnlyList<string> HelperNameFragments =
    [
        "crash", "handler", "redist", "setup", "install", "unins", "update", "helper", "report", "error", "anticheat",
        "easyanticheat", "battleye", "beservice", "subprocess", "overlay", "bootstrap", "prereq", "launcher", "service",
        "diagnos", "repair", "dxwebsetup", "vc_redist", "eac_", "cefprocess",
    ];

    /// <summary>
    /// Folders where game launchers put installed games. An executable here is a
    /// heuristic game candidate even when nothing launched it. Matched against the
    /// full image path (case-insensitive).
    /// </summary>
    public static readonly IReadOnlyList<Regex> LibraryPathPatterns =
    [
        new(@"\\steamapps\\common\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\\epic games\\(?!launcher\\|epicgameslauncher)[^\\]+\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\\xboxgames\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\\gog galaxy\\games\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\\gog games\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\\ubisoft game launcher\\games\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\\ea games\\", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    /// <summary>Operating-system folders: nothing under them is ever a heuristic game candidate.</summary>
    public static readonly Regex SystemPathPattern = new(@"^[a-z]:\\windows\\", RegexOptions.IgnoreCase | RegexOptions.Compiled);
}
