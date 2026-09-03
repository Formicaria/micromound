using Micromound.Evidence;
using Micromound.Protocol;
using Micromound.Sync;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The durable evidence store: the in-memory store's retention policy (proven at v0.9.0) over a
/// directory of files, so proof survives a restart. These prove what the in-memory tests cannot —
/// that items, their order, their acknowledgements, and the not-yet-reported eviction/spill counts all
/// come back after the store is closed and reopened — plus the crash-recovery shapes the on-disk layout
/// is designed for: an orphan marker, a stale temp file, a corrupt item.
/// </summary>
public sealed class FileEvidenceStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mm-evstore-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ } }

    private static EvidenceItem Item(string id, double value = 1) =>
        EvidenceReadings.Create(id, "sense.soil_moisture", value, Now, unit: "pct", source: "test");

    private FileEvidenceStore Open(int capacity = 2000, int? ceiling = null) => new(_dir, capacity, ceiling);

    [Fact]
    public void Items_order_and_acknowledgements_survive_a_reopen()
    {
        var store = Open();
        store.Add(Item("a")); store.Add(Item("b")); store.Add(Item("c"));
        store.Acknowledge(["b"]);

        var reopened = Open();

        Assert.Equal(3, reopened.Count);
        Assert.True(reopened.TryGet("b", out var b) && b.EvidenceId == "b");
        // Pending is oldest-first and excludes the acknowledged item — exactly as before the restart.
        Assert.Equal(["a", "c"], reopened.Pending().Select(i => i.EvidenceId));
    }

    [Fact]
    public void Acknowledged_proof_is_reclaimed_first_past_the_soft_capacity_like_the_in_memory_store()
    {
        var store = Open(capacity: 3);
        store.Add(Item("a")); store.Add(Item("b")); store.Add(Item("c"));
        store.Acknowledge(["a", "b"]);
        store.Add(Item("d"));                         // 4 > 3: the oldest ACKED item goes

        Assert.False(store.TryGet("a", out _));       // a was acknowledged → evicted
        Assert.True(store.TryGet("b", out _));        // only one over: b stays
        Assert.Equal(1, store.TakeEvictedCount());
        Assert.Equal(0, store.TakeSpilledCount());
    }

    [Fact]
    public void Unacknowledged_proof_spills_only_past_the_hard_ceiling_and_the_count_survives_a_restart()
    {
        var store = Open(capacity: 2, ceiling: 3);
        store.Add(Item("a")); store.Add(Item("b")); store.Add(Item("c"));   // 3, all unacked: at the ceiling, nothing spills
        Assert.Equal(3, store.Count);
        store.Add(Item("d"));                                                 // 4 > 3: the oldest unacked spills

        Assert.False(store.TryGet("a", out _));
        Assert.Equal(3, store.Count);

        // The spill has NOT been reported yet (TakeSpilledCount not called). A restart must not make
        // that loss silent: the count comes back.
        var reopened = Open(capacity: 2, ceiling: 3);
        Assert.Equal(1, reopened.TakeSpilledCount());
        Assert.Equal(0, reopened.TakeSpilledCount());   // taken once, then gone

        // ...and the reset is durable too.
        Assert.Equal(0, Open(capacity: 2, ceiling: 3).TakeSpilledCount());
    }

    [Fact]
    public void Evicted_count_survives_a_restart_until_taken()
    {
        var store = Open(capacity: 1);
        store.Add(Item("a")); store.Acknowledge(["a"]); store.Add(Item("b"));   // evicts a
        Assert.Equal(1, Open(capacity: 1).TakeEvictedCount());
    }

    [Fact]
    public void Re_adding_a_known_id_replaces_in_place_and_keeps_its_position()
    {
        var store = Open();
        store.Add(Item("a", 1)); store.Add(Item("b", 1));
        store.Add(Item("a", 42));   // same id: overwrite, no re-append

        Assert.Equal(2, store.Count);
        Assert.Equal(["a", "b"], store.Pending().Select(i => i.EvidenceId));   // a keeps first place
        Assert.True(EvidenceReadings.TryRead(store.Pending()[0], out var v) && v == 42);

        // One item file per id, not one per write.
        Assert.Equal(2, Directory.EnumerateFiles(_dir, "*.json").Count(p => Path.GetFileName(p) != "counters.json"));

        var reopened = Open();
        Assert.Equal(["a", "b"], reopened.Pending().Select(i => i.EvidenceId));
    }

    [Fact]
    public void The_on_disk_shape_is_one_item_file_per_item_and_one_marker_per_acknowledgement()
    {
        var store = Open();
        store.Add(Item("a")); store.Add(Item("b"));
        store.Acknowledge(["a"]);

        var names = Directory.EnumerateFiles(_dir).Select(Path.GetFileName).Where(n => n != "counters.json").OrderBy(n => n).ToList();
        Assert.Equal(["0000000000000000.ack", "0000000000000000.json", "0000000000000001.json"], names);
    }

    [Fact]
    public void An_orphan_acknowledgement_marker_is_ignored_and_swept_on_open()
    {
        // The crash shape the delete order is designed for: the item was unlinked, the marker was not.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "0000000000000007.ack"), "");

        var store = Open();

        Assert.Equal(0, store.Count);
        Assert.Empty(store.OpenFaults);
        Assert.False(File.Exists(Path.Combine(_dir, "0000000000000007.ack")));   // swept
    }

    [Fact]
    public void A_stale_temporary_is_swept_on_open_and_never_read_as_proof()
    {
        var store = Open();
        store.Add(Item("a"));
        File.WriteAllText(Path.Combine(_dir, ".mmtmp", "abandoned-write"), "{\"evidence_id\":\"ghost\"}");

        var reopened = Open();

        Assert.Equal(1, reopened.Count);
        Assert.False(reopened.TryGet("ghost", out _));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, ".mmtmp")));
    }

    [Fact]
    public void A_corrupt_item_file_is_skipped_and_reported_never_treated_as_proof_or_fatal()
    {
        var store = Open();
        store.Add(Item("a")); store.Add(Item("b"));
        File.WriteAllText(Path.Combine(_dir, "0000000000000001.json"), "{ not json");   // b's file, corrupted

        var reopened = Open();

        Assert.Equal(1, reopened.Count);                        // a loaded
        Assert.True(reopened.TryGet("a", out _));
        Assert.False(reopened.TryGet("b", out _));               // b is gone, not fabricated
        Assert.Single(reopened.OpenFaults);                      // and the loss is reported, loudly
        Assert.Contains("0000000000000001.json", reopened.OpenFaults[0]);
    }

    [Fact]
    public void Reopening_under_a_smaller_capacity_brings_the_store_back_inside_the_bound()
    {
        var store = Open(capacity: 10);
        for (var i = 0; i < 5; i++) store.Add(Item("i" + i));
        store.Acknowledge(["i0", "i1", "i2"]);

        var shrunk = Open(capacity: 3);   // configured smaller: 5 > 3, reclaim acked oldest-first now

        Assert.Equal(3, shrunk.Count);
        Assert.False(shrunk.TryGet("i0", out _));
        Assert.False(shrunk.TryGet("i1", out _));
        Assert.True(shrunk.TryGet("i2", out _));   // acked but only two needed to go
        Assert.Equal(2, shrunk.TakeEvictedCount());
    }

    [Fact]
    public void New_items_after_a_reopen_continue_the_sequence_and_keep_order()
    {
        var store = Open();
        store.Add(Item("a")); store.Add(Item("b"));

        var reopened = Open();
        reopened.Add(Item("c"));

        Assert.Equal(["a", "b", "c"], reopened.Pending().Select(i => i.EvidenceId));
        Assert.Equal(["a", "b", "c"], Open().Pending().Select(i => i.EvidenceId));   // and again after another reopen
    }

    [Fact]
    public void An_item_with_no_id_is_ignored_like_the_in_memory_store()
    {
        var store = Open();
        store.Add(new EvidenceItem());
        Assert.Equal(0, store.Count);
    }
}
