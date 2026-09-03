using System.Runtime.InteropServices;
using System.Text;

namespace Micromound.Sync;

/// <summary>
/// The durability primitives every file-backed store in this repository is built on — shared so
/// there is exactly one implementation of "write this so a power cut cannot tear it". Both the
/// state store (<see cref="FileStateStore"/>) and the evidence store (<see cref="FileEvidenceStore"/>)
/// are "a directory of files on a Pi", and they owe the same two guarantees:
///
/// <para><b>Atomic replace.</b> A value is written whole to a uniquely named temporary file in a
/// sibling temp directory, flushed to disk, and then moved over its destination in one rename. A
/// crash before the rename leaves the previous whole value (or none); a crash after it leaves the new
/// whole value; there is no window in which the destination holds a torn write. Temporaries live in
/// their own directory, never among the committed files, so a crashed write is swept on the next open
/// and can never be mistaken for a value.</para>
///
/// <para><b>Directory durability.</b> A rename or unlink is only guaranteed on disk once the directory
/// holding the entry is itself flushed. Managed .NET exposes no directory fsync, so on POSIX this opens
/// the directory and fsyncs its descriptor; on Windows (not a device target) it is a no-op. It is
/// best-effort: a store that cannot fsync its directory is still correct on a clean restart.</para>
/// </summary>
internal static class DurableFiles
{
    /// <summary>The temp directory name every store uses; a leading dot keeps it out of casual listings.</summary>
    public const string TempDirName = ".mmtmp";

    /// <summary>Write <paramref name="value"/> to <paramref name="destination"/> atomically and durably.</summary>
    public static void WriteAtomic(string tempDirectory, string destination, string value)
    {
        var temp = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N"));

        // Write the whole value to a private temp file and force it to disk before it is named, so
        // the rename below promotes only a fully-written value.
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
        {
            writer.Write(value);
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }

        // The atomic step: a single rename over the destination replaces the value with no torn
        // intermediate. Then flush the directory so the rename itself survives a power cut.
        File.Move(temp, destination, overwrite: true);
        FlushDirectory(Path.GetDirectoryName(destination)!);
    }

    /// <summary>Unlink <paramref name="path"/> if present, and make the unlink itself durable.</summary>
    public static void Delete(string path)
    {
        if (!File.Exists(path))
            return;
        File.Delete(path);
        FlushDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>Remove temporaries a crashed write left behind. They are never a committed value.</summary>
    public static void SweepTemporaries(string tempDirectory)
    {
        foreach (var leftover in Directory.EnumerateFiles(tempDirectory))
        {
            try { File.Delete(leftover); }
            catch (Exception) { /* still never read as a value; leave it for the next sweep */ }
        }
    }

    /// <summary>
    /// Reversible, collision-free key→filename encoding: unreserved characters pass through, every
    /// other byte becomes %XX. A true injection (<c>%</c> is itself escaped and path separators cannot
    /// survive), so distinct keys never share a file and no key can escape its directory.
    /// </summary>
    public static string Encode(string key)
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

    public static void FlushDirectory(string directory)
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
