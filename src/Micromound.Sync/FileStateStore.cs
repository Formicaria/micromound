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
        _tempDirectory = Path.Combine(directory, DurableFiles.TempDirName);
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(_tempDirectory);
        DurableFiles.SweepTemporaries(_tempDirectory);
    }

    public void Put(string key, string value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var destination = PathFor(key);

        // Temp-write, flush, rename, directory-flush — the shared primitive (DurableFiles) so the
        // evidence store and this one cannot disagree about what "durable" means.
        lock (_gate)
            DurableFiles.WriteAtomic(_tempDirectory, destination, value);
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
            DurableFiles.Delete(PathFor(key));   // the unlink must survive a power cut too
    }

    private string PathFor(string key) => Path.Combine(_directory, DurableFiles.Encode(key) + ValueSuffix);
}
