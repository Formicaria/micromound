using Micromound.Sync;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The durable file-backed state store — the substrate the M4 host runs on. These prove the one
/// property the <see cref="IStateStore"/> contract demands of a disk backing (atomic per-key writes
/// that survive a restart) and the key→filename handling that lets colon-bearing keys live as files.
/// </summary>
public sealed class FileStateStoreTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "mm-fsstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_value_round_trips_through_disk()
    {
        var store = new FileStateStore(_dir);
        store.Put("cache:authority", "{\"lease\":42}");

        Assert.True(store.TryGet("cache:authority", out var value));
        Assert.Equal("{\"lease\":42}", value);
    }

    [Fact]
    public void State_survives_a_new_instance_over_the_same_directory()
    {
        new FileStateStore(_dir).Put("cache:mission", "{\"id\":\"ms-1\"}");

        // A brand-new instance over the same directory is exactly what a process restart is.
        var reopened = new FileStateStore(_dir);
        Assert.True(reopened.TryGet("cache:mission", out var value));
        Assert.Equal("{\"id\":\"ms-1\"}", value);
    }

    [Fact]
    public void A_missing_key_is_absent_not_an_error()
    {
        var store = new FileStateStore(_dir);
        Assert.False(store.TryGet("cache:never-written", out var value));
        Assert.Equal("", value);
    }

    [Fact]
    public void Put_overwrites_a_value_wholesale()
    {
        var store = new FileStateStore(_dir);
        store.Put("k", "first");
        store.Put("k", "second");

        Assert.True(new FileStateStore(_dir).TryGet("k", out var value));
        Assert.Equal("second", value);
    }

    [Fact]
    public void Delete_removes_the_key_from_disk()
    {
        var store = new FileStateStore(_dir);
        store.Put("cache:mission", "x");
        store.Delete("cache:mission");

        Assert.False(new FileStateStore(_dir).TryGet("cache:mission", out _));
        Assert.False(store.TryGet("cache:mission", out _));
    }

    [Fact]
    public void Delete_of_a_missing_key_is_a_no_op()
    {
        var store = new FileStateStore(_dir);
        store.Delete("cache:never-there");   // must not throw
        Assert.False(store.TryGet("cache:never-there", out _));
    }

    [Fact]
    public void Keys_with_filesystem_reserved_characters_are_distinct_files()
    {
        var store = new FileStateStore(_dir);
        store.Put("cache:mission", "A");
        store.Put("queue/0001", "B");
        store.Put("cache:authority", "C");

        Assert.True(store.TryGet("cache:mission", out var a) && a == "A");
        Assert.True(store.TryGet("queue/0001", out var b) && b == "B");
        Assert.True(store.TryGet("cache:authority", out var c) && c == "C");
    }

    [Fact]
    public void An_orphaned_temp_from_a_crashed_write_is_swept_and_never_read()
    {
        var store = new FileStateStore(_dir);
        store.Put("cache:mission", "committed");

        // Simulate a crash mid-write: a temp file left behind in the temp directory, never renamed.
        var tempDir = Path.Combine(_dir, ".mmtmp");
        File.WriteAllText(Path.Combine(tempDir, "deadbeef"), "half-written garbage");

        var reopened = new FileStateStore(_dir);   // construction sweeps orphaned temporaries
        Assert.Empty(Directory.GetFiles(tempDir));
        Assert.True(reopened.TryGet("cache:mission", out var value));
        Assert.Equal("committed", value);          // the committed value, never the orphan
    }

    [Fact]
    public void A_key_that_looks_like_a_temp_name_is_still_a_durable_value()
    {
        // Regression: temp files once shared the value directory and were swept by an infix match,
        // so a key whose name contained the temp marker had its value deleted on the next open.
        // Temp files now live in their own directory, so no key can collide with the sweep.
        var store = new FileStateStore(_dir);
        store.Put("a.tmp-1", "survives");
        store.Put("cache:mission.tmp-x", "also survives");

        var reopened = new FileStateStore(_dir);   // sweep runs; must not touch these values
        Assert.True(reopened.TryGet("a.tmp-1", out var v1) && v1 == "survives");
        Assert.True(reopened.TryGet("cache:mission.tmp-x", out var v2) && v2 == "also survives");
    }
}
