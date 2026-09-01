using System.Runtime.InteropServices;
using System.Text;

namespace Micromound.Sync;

/// <summary>
/// The durable state store the M4 host runs on: one file per key under a state directory, exactly
/// the "directory of files on a Pi" the <see cref="IStateStore"/> contract describes. No database,
/// no schema, no queries — the same three operations the in-memory store offers, backed by disk so
/// they survive a process or device restart.
///
/// <para>
/// <b>Atomicity and durability.</b> The contract's one hard requirement is that <see cref="Put"/>
/// is atomic per key — a restart must never find half a charter. Each write goes to a uniquely
/// named temporary file in a sibling <c>.mmtmp</c> directory, is flushed to disk, and is then moved
/// over the destination in a single filesystem rename; a crash before the rename leaves the
/// previous whole value in place (or none), a crash after it leaves the new whole value, and there
/// is no window in which the destination key holds a torn write. After the rename — and after a
/// <see cref="Delete"/>'s unlink — the containing directory is flushed on POSIX so the name change
/// itself reaches disk, which is what makes "persist the report, then clear its checkpoint" hold
/// across a power cut and not only a clean process exit. (On Windows the directory flush is a
/// no-op; the real device targets are Linux and the ESP32.) Temporary files live in their own
/// directory, never among the value files, so a crashed write is swept on the next open and can
/// never be mistaken for — or collide with — a committed value.
/// </para>
///
/// <para>
/// <b>Keys to filenames.</b> Keys carry characters a filesystem will not (<c>cache:mission</c>,
/// queue keys with slashes), so each key is reversibly percent-encoded to a safe filename: the
/// encoding is a true injection (<c>%</c> is itself escaped and path separators cannot survive), so
/// distinct keys never share a file and no key can escape the state directory.
/// </para>
///
/// <para>
/// <b>Faults are loud.</b> A missing file is "no such key" and restores to observe-only, as every
/// restore path here expects. A file that exists but cannot be read is a real fault, not a missing
/// key, so the read error propagates rather than being silently reported as absent — the contract's
/// distinction between "a missing key restores to observe-only and a corrupt one restores to an
/// argument". JSON-level corruption of an otherwise-readable value is caught one layer up, where a
/// value is deserialized, and already degrades to "treat as absent".
/// </para>
/// </summary>
public sealed class FileStateStore : IStateStore
{
    private const string ValueSuffix = ".json";
    private const string TempDirName = ".mmtmp";

    private readonly string _directory;
    private readonly string _tempDirectory;
    private readonly object _gate = new();

    /// <summary>
    /// Open (creating if needed) the state directory. Any temporary files left by a write that a
    /// crash interrupted are swept here — they are never a committed value.
    /// </summary>
    public FileStateStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        _tempDirectory = Path.Combine(directory, TempDirName);
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_tempDirectory);
        SweepOrphanedTemporaries();
    }

    public void Put(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var destination = PathFor(key);
        var temp = Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N"));

        lock (_gate)
        {
            // Write the whole value to a private temp file and force it to disk before it is named,
            // so the rename below promotes only a fully-written value.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(value);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            // The atomic step: a single rename over the destination replaces the key's value with no
            // torn intermediate. Then flush the directory so the rename itself survives a power cut.
            File.Move(temp, destination, overwrite: true);
            FlushDirectory(_directory);
        }
    }

    public bool TryGet(string key, out string value)
    {
        ArgumentNullException.ThrowIfNull(key);

        var path = PathFor(key);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                value = "";
                return false;   // a missing key is not an error — it restores to observe-only
            }

            // A read error on a file that exists is a fault, not an absence: let it propagate.
            value = File.ReadAllText(path, Encoding.UTF8);
            return true;
        }
    }

    public void Delete(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            var path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
                FlushDirectory(_directory);   // the unlink must survive a power cut too
            }
        }
    }

    private string PathFor(string key) => Path.Combine(_directory, Encode(key) + ValueSuffix);

    private void SweepOrphanedTemporaries()
    {
        foreach (var leftover in Directory.EnumerateFiles(_tempDirectory))
        {
            try { File.Delete(leftover); }
            catch (Exception) { /* still never read as a value; leave it for the next sweep */ }
        }
    }

    /// <summary>
    /// Reversible, collision-free key→filename encoding: unreserved characters pass through, every
    /// other byte becomes %XX. Deterministic, so a key always maps to the same file.
    /// </summary>
    private static string Encode(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        var sb = new StringBuilder(bytes.Length + 8);
        foreach (var b in bytes)
        {
            var c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')
                || c is '.' or '_' or '-')
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------------
    // Directory durability. A rename or unlink is only guaranteed on disk after the directory that
    // holds the entry is itself flushed. Managed .NET exposes no directory fsync, so on POSIX this
    // opens the directory and fsyncs its descriptor; on Windows (not a device target) it is a no-op.
    // Best-effort: a state store that cannot fsync its directory is still correct on a clean restart.
    // ---------------------------------------------------------------------------------------------

    private static void FlushDirectory(string directory)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            return;

        var fd = -1;
        try
        {
            fd = Open(directory, 0 /* O_RDONLY */);
            if (fd >= 0)
                Fsync(fd);
        }
        catch (Exception)
        {
            // No libc, or the platform refused: the value is still written and atomically named.
        }
        finally
        {
            if (fd >= 0)
                Close(fd);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int Open(string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int fd);
}
