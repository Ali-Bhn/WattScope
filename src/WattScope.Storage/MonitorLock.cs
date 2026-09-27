namespace WattScope.Storage;

/// <summary>
/// Cross-process guard ensuring only one process records into a database at a
/// time. Held as an exclusively-opened file next to the database; the OS
/// releases it automatically if the process dies, which is what lets the next
/// process distinguish "another recorder is running" from "a recorder crashed".
/// </summary>
public sealed class MonitorLock : IDisposable
{
    private readonly FileStream _stream;

    private MonitorLock(FileStream stream) => _stream = stream;

    public static string LockPathFor(string databasePath) => Path.GetFullPath(databasePath) + ".lock";

    /// <summary>Returns null if another process holds the lock.</summary>
    public static MonitorLock? TryAcquire(string databasePath)
    {
        var path = LockPathFor(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            return new MonitorLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
