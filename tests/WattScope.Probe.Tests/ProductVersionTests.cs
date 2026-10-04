using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WattScope.Probe.Serve;

namespace WattScope.Probe.Tests;

/// <summary>
/// One product version everywhere: the sidecar (which stamps it on every session) and the desktop app
/// must be built as the same version. Windows/.NET write it with four numeric parts, so the semantic
/// version 0.1.0 is the assembly and file version 0.1.0.0.
/// </summary>
public class ProductVersionTests
{
    private static readonly Assembly Sidecar = typeof(MonitoringController).Assembly;

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WattScope.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "repository root (WattScope.slnx) not found above the test output folder");
        return Path.Combine([dir!.FullName, .. parts]);
    }

    private static string JsonVersion(params string[] parts)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(RepoFile(parts)));
        return json.RootElement.GetProperty("version").GetString()!;
    }

    private static string DesktopVersion() => JsonVersion("desktop", "src-tauri", "tauri.conf.json");

    [Fact]
    public void DesktopApp_DeclaresOneVersion_InEveryManifest()
    {
        var product = DesktopVersion();

        Assert.Matches(@"^\d+\.\d+\.\d+$", product);
        Assert.Equal(product, JsonVersion("desktop", "package.json"));
        // [package] is the first table of Cargo.toml, so its version is the first one in the file.
        var cargo = Regex.Match(File.ReadAllText(RepoFile("desktop", "src-tauri", "Cargo.toml")), @"(?m)^version\s*=\s*""([^""]+)""");
        Assert.True(cargo.Success, "no version in Cargo.toml");
        Assert.Equal(product, cargo.Groups[1].Value);
    }

    [Fact]
    public void Sidecar_AssemblyVersion_IsTheDesktopAppVersion_InWindowsFourPartForm()
    {
        var version = Sidecar.GetName().Version!;

        Assert.Equal(DesktopVersion(), version.ToString(3));
        Assert.Equal(0, version.Revision);
        // This is the text the sidecar stamps on sessions and reports as its version (Program.cs).
        Assert.Equal(DesktopVersion() + ".0", version.ToString());
    }

    [Fact]
    public void Sidecar_FileAndInformationalVersions_AreTheSameProductVersion()
    {
        var file = Sidecar.GetCustomAttribute<AssemblyFileVersionAttribute>()!.Version;
        var informational = Sidecar.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(DesktopVersion() + ".0", file);
        // Build metadata (the source commit) may follow a '+'; the version before it must be the product version.
        Assert.Equal(DesktopVersion(), informational.Split('+')[0]);
    }
}
