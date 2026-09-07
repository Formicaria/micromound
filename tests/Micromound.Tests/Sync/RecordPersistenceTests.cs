using Micromound.Capabilities;
using Micromound.Protocol;
using Micromound.Runtime;
using Micromound.Sync;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The durable uplink queue: the chain is enforced at the door, retention is governed by
/// acknowledgement, and a restart loses nothing.
/// </summary>
public class UplinkQueueTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static Envelope Next(IUplinkQueue queue, string kind = EnvelopeKinds.MoundSync) => new()
    {
        MoundId = "mm-1",
        Seq = queue.NextSeq,
        SentAt = Now.ToWire(),
        Kind = kind,
        Body = System.Text.Json.JsonSerializer.SerializeToElement(new { }, ProtocolJson.Options),
        PrevDigest = queue.LastDigest,
        Signature = "ed25519:" + new string('a', 128)
    };

    // ---- P0.7: the audit path is bounded, and not rewritten whole (v0.9.34) --------------------

    /// <remarks>
    /// The queue had no bound at all, so a mound offline long enough filled its disk — and every
    /// mutation reserialized the ENTIRE queue into one document. Measured on this repository's own
    /// store: 4,000 queued action records was a 2.4 MB state file rewritten in full on every
    /// enqueue. Each envelope is now its own small document keyed by sequence.
    /// </remarks>
    [Fact]
    public void An_enqueue_writes_one_segment_and_a_head_not_the_whole_queue()
    {
        var store = new InMemoryStateStore();
        var queue = new DurableUplinkQueue(store);

        for (var i = 0; i < 20; i++) queue.Enqueue(Next(queue));

        // One head plus one document per envelope — not one document containing all of them.
        Assert.Equal(21, store.Count);
        Assert.True(store.TryGet("sync:uplink-queue", out var head));
        Assert.DoesNotContain("\"pending\":[{", head);
    }

    [Fact]
    public void The_queue_is_bounded_and_spills_oldest_first_counting_what_it_dropped()
    {
        var store = new InMemoryStateStore();
        var queue = new DurableUplinkQueue(store, maxPending: 5);

        for (var i = 0; i < 12; i++) queue.Enqueue(Next(queue));

        Assert.Equal(5, queue.Depth);
        Assert.Equal(7, queue.TakeSpilledCount());
        Assert.Equal(0, queue.TakeSpilledCount());   // read-and-clear: reported once

        // The chain head never retreats, so what survives still verifies as a chain — with a
        // visible gap where the dropped sequence numbers were.
        Assert.Equal(12, queue.NextSeq);
        Assert.Equal(7, queue.Peek(10)[0].Seq);
    }

    [Fact]
    public void A_byte_bound_holds_even_when_the_item_count_does_not()
    {
        var store = new InMemoryStateStore();
        var queue = new DurableUplinkQueue(store, maxPending: 1000, maxPendingBytes: 4096);

        for (var i = 0; i < 40; i++) queue.Enqueue(Next(queue));

        Assert.True(queue.PendingBytes <= 4096, $"retained {queue.PendingBytes} bytes");
        Assert.True(queue.Depth < 40);
    }

    [Fact]
    public void Segments_and_watermarks_survive_a_restart()
    {
        var store = new InMemoryStateStore();
        var first = new DurableUplinkQueue(store);
        for (var i = 0; i < 6; i++) first.Enqueue(Next(first));
        first.AcknowledgeThrough(2);

        var reborn = new DurableUplinkQueue(store);

        Assert.Equal(3, reborn.Depth);
        Assert.Equal(6, reborn.NextSeq);
        Assert.Equal(first.LastDigest, reborn.LastDigest);
        Assert.Equal(3, reborn.Peek(10)[0].Seq);
    }

    /// <remarks>
    /// A mound upgrading in the field has a queue written in the old whole-document format holding
    /// signed records the controller has not seen. "We changed our storage format" is not a reason
    /// to put a gap in a chain, so the old shape is migrated in place rather than dropped.
    /// </remarks>
    [Fact]
    public void A_queue_written_in_the_old_format_is_migrated_not_lost()
    {
        var store = new InMemoryStateStore();
        var legacy = new DurableUplinkQueue(store);
        for (var i = 0; i < 4; i++) legacy.Enqueue(Next(legacy));

        // Rewrite the head the way v0.9.33 and earlier did: everything inline, no `segmented` flag.
        var inline = legacy.Peek(10);
        foreach (var envelope in inline) store.Delete("sync:uplink/" + envelope.Seq.ToString("D12"));
        store.Put("sync:uplink-queue", System.Text.Json.JsonSerializer.Serialize(new
        {
            next_seq = legacy.NextSeq,
            last_digest = legacy.LastDigest,
            acked_through = -1L,
            pending = inline
        }, ProtocolJson.Options));

        var upgraded = new DurableUplinkQueue(store);

        Assert.Equal(4, upgraded.Depth);
        Assert.Equal(legacy.LastDigest, upgraded.LastDigest);
        Assert.True(store.TryGet("sync:uplink/000000000000", out _));   // now a segment
        Assert.True(store.TryGet("sync:uplink-queue", out var head));
        Assert.DoesNotContain("\"pending\":[{", head);                  // ...and the head is small again
    }

    [Fact]
    public void The_chain_is_enforced_at_enqueue_not_discovered_at_the_controller()
    {
        var queue = new DurableUplinkQueue();
        queue.Enqueue(Next(queue));

        var skipsSequence = Next(queue);
        skipsSequence.Seq += 5;
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(skipsSequence));

        var wrongAnchor = Next(queue);
        wrongAnchor.PrevDigest = "sha256:0000";
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(wrongAnchor));

        var unsigned = Next(queue);
        unsigned.Signature = "";
        Assert.Throws<InvalidOperationException>(() => queue.Enqueue(unsigned));

        Assert.Equal(1, queue.Depth);   // none of the rejects got in
    }

    [Fact]
    public void Acknowledgement_governs_retention_and_never_moves_backwards()
    {
        var queue = new DurableUplinkQueue();
        for (var i = 0; i < 4; i++) queue.Enqueue(Next(queue));

        queue.AcknowledgeThrough(1);
        Assert.Equal(2, queue.Depth);
        Assert.Equal(new[] { 2L, 3L }, queue.Peek(10).Select(e => e.Seq).ToArray());

        queue.AcknowledgeThrough(0);   // stale ack: nothing returns
        Assert.Equal(2, queue.Depth);
        Assert.Equal(1, queue.AcknowledgedThroughSeq);
    }

    [Fact]
    public void The_chain_head_survives_acknowledgement()
    {
        var queue = new DurableUplinkQueue();
        queue.Enqueue(Next(queue));
        queue.Enqueue(Next(queue));

        var headBefore = queue.LastDigest;
        queue.AcknowledgeThrough(1);

        // Eviction is bookkeeping; the chain is history. The next envelope still anchors to the
        // last one ever enqueued, or the controller would see a break exactly where the mound
        // was being tidy.
        Assert.Equal(headBefore, queue.LastDigest);

        var next = Next(queue);
        Assert.Equal(2, next.Seq);
        Assert.Equal(headBefore, next.PrevDigest);
        queue.Enqueue(next);
    }

    [Fact]
    public void A_restart_loses_nothing_that_was_not_acknowledged()
    {
        var store = new InMemoryStateStore();
        var queue = new DurableUplinkQueue(store);
        for (var i = 0; i < 3; i++) queue.Enqueue(Next(queue));
        queue.AcknowledgeThrough(0);

        var reborn = new DurableUplinkQueue(store);

        Assert.Equal(queue.NextSeq, reborn.NextSeq);
        Assert.Equal(queue.LastDigest, reborn.LastDigest);
        Assert.Equal(queue.AcknowledgedThroughSeq, reborn.AcknowledgedThroughSeq);
        Assert.Equal(new[] { 1L, 2L }, reborn.Peek(10).Select(e => e.Seq).ToArray());

        // And the restored chain still verifies end to end.
        Assert.True(EnvelopeValidator.ValidateChain(reborn.Peek(10), queue.Peek(10)[0].PrevDigest).IsValid);
    }

    [Fact]
    public void A_corrupt_snapshot_restores_to_an_empty_queue_not_to_an_exception()
    {
        var store = new InMemoryStateStore();
        store.Put("sync:uplink-queue", "{not json");

        var queue = new DurableUplinkQueue(store);

        Assert.Equal(0, queue.Depth);
        Assert.Equal(0, queue.NextSeq);
    }
}

/// <summary>
/// The Cache Ant: operational persistence, and the restart rules that make a power cut boring.
/// </summary>
public class CacheAntTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-14T12:00:00Z");

    private static Charter Benign(string moundId, int ttl = 900) => new()
    {
        CharterId = "c-1",
        MoundId = moundId,
        MissionRef = "m-1",
        IssuedAt = Now.ToWire(),
        ExpiresAt = Now.AddHours(2).ToWire(),
        LeaseTtlSeconds = ttl,
        ActionCeiling = "benign",
        Capabilities = ["sense.temp", "act.relay_1"],
        SafeState = "all_actuators_off"
    };

    [Fact]
    public void Values_round_trip_and_corrupt_values_read_as_absent()
    {
        var cache = new CacheAnt(new InMemoryStateStore());

        cache.Save("charter", Benign("mm-1"));
        Assert.True(cache.TryLoad<Charter>("charter", out var loaded));
        Assert.Equal("c-1", loaded.CharterId);

        Assert.False(cache.TryLoad<Charter>("nothing", out _));

        var store = new InMemoryStateStore();
        store.Put("cache:broken", "{{{");
        Assert.False(new CacheAnt(store).TryLoad<Charter>("broken", out _));
    }

    [Fact]
    public void A_restart_mid_lease_restores_authority_without_extending_it()
    {
        var store = new InMemoryStateStore();
        var cache = new CacheAnt(store);

        var authority = new KernelAuthority("mm-1");
        authority.AcceptCharter(Benign("mm-1"), Now);
        cache.SaveAuthority(authority);
        var savedExpiry = authority.LeaseExpiresAt;

        // The process dies; five minutes pass; a new process restores.
        var reborn = new KernelAuthority("mm-1");
        Assert.True(cache.TryRestoreAuthority(reborn, Now.AddMinutes(5), out var result));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));

        Assert.Equal(MoundStates.Chartered, reborn.State);
        Assert.Equal(savedExpiry, reborn.LeaseExpiresAt);   // the saved value, never now + ttl
        Assert.True(reborn.LeaseAlive(Now.AddMinutes(5)));
    }

    [Fact]
    public void A_restart_after_the_lease_ran_out_comes_back_quiesced()
    {
        var store = new InMemoryStateStore();
        var cache = new CacheAnt(store);

        var authority = new KernelAuthority("mm-1");
        authority.AcceptCharter(Benign("mm-1"), Now);
        cache.SaveAuthority(authority);

        var reborn = new KernelAuthority("mm-1");
        cache.TryRestoreAuthority(reborn, Now.AddSeconds(901), out _);

        Assert.Equal(MoundStates.Quiesced, reborn.State);
        Assert.False(reborn.LeaseAlive(Now.AddSeconds(901)));
    }

    [Fact]
    public void A_restart_never_clears_a_stop()
    {
        var store = new InMemoryStateStore();
        var cache = new CacheAnt(store);

        var authority = new KernelAuthority("mm-1");
        authority.AcceptCharter(Benign("mm-1"), Now);
        authority.Stop();
        cache.SaveAuthority(authority);

        var reborn = new KernelAuthority("mm-1");
        cache.TryRestoreAuthority(reborn, Now.AddSeconds(5), out _);

        // Power-cycling the mound is not a way around an operator's stop order.
        Assert.Equal(MoundStates.Stopped, reborn.State);
        Assert.Null(reborn.ActiveCharter);
    }

    [Fact]
    public void A_charter_the_device_can_no_longer_honour_restores_to_observe_only()
    {
        var store = new InMemoryStateStore();
        var cache = new CacheAnt(store);

        var authority = new KernelAuthority("mm-1");
        authority.AcceptCharter(Benign("mm-1"), Now);
        cache.SaveAuthority(authority);

        // The hardware changed while the process was down: the relay is gone.
        var reborn = new KernelAuthority("mm-1");
        cache.TryRestoreAuthority(reborn, Now.AddMinutes(1), out var result,
            deviceCapabilities: new HashSet<string>(StringComparer.Ordinal) { "sense.temp" });

        Assert.False(result.IsValid);
        Assert.Equal(MoundStates.ObserveOnly, reborn.State);
    }

    [Fact]
    public void Restore_is_refused_over_live_authority()
    {
        var store = new InMemoryStateStore();
        var cache = new CacheAnt(store);

        var authority = new KernelAuthority("mm-1");
        authority.AcceptCharter(Benign("mm-1"), Now);
        cache.SaveAuthority(authority);

        // Same instance already holds a charter: replaying the snapshot must not downgrade it.
        Assert.True(cache.TryRestoreAuthority(authority, Now.AddMinutes(1), out var result));
        Assert.False(result.IsValid);
        Assert.Equal(MoundStates.Chartered, authority.State);
    }

    [Fact]
    public void A_restart_keeps_the_operators_device_limits_or_it_would_widen_by_rebooting()
    {
        var store = new InMemoryStateStore();
        var cache = new CacheAnt(store);

        var authority = new KernelAuthority("mm-1");
        authority.ApplyManifest(new MoundManifest
        {
            ManifestId = "mf-1",
            MoundId = "mm-1",
            IssuedAt = Now.ToWire(),
            DeviceLimits = { ["act.relay_1"] = new CapabilityLimits { MaxOnSeconds = 8 } },
            SafeState = "valves_closed"
        });
        authority.AcceptCharter(Benign("mm-1"), Now);
        cache.SaveAuthority(authority);

        var reborn = new KernelAuthority("mm-1");
        cache.TryRestoreAuthority(reborn, Now.AddMinutes(1), out var result);

        // Without this, a power cycle would quietly enforce hardware ∩ charter instead of
        // hardware ∩ device ∩ charter — the one way a reboot could widen what the mound may do.
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(8, reborn.DeviceLimitsFor("act.relay_1")?.MaxOnSeconds);
    }

    [Fact]
    public void A_fresh_mound_has_no_snapshot_and_starts_observe_only()
    {
        var cache = new CacheAnt(new InMemoryStateStore());
        var authority = new KernelAuthority("mm-1");

        Assert.False(cache.TryRestoreAuthority(authority, Now, out _));
        Assert.Equal(MoundStates.ObserveOnly, authority.State);
    }
}
