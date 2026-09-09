using Micromound.Capabilities;
using Micromound.Sync;

namespace Micromound.Runtime;

/// <summary>
/// Wires the uplink queue's remaining room to the capability kernel's check 14, so the kernel can
/// refuse new physical work it would not be able to record.
///
/// <para><b>Why an adapter and not an interface on the queue.</b> The layering runs one way:
/// <c>Micromound.Sync</c> is Layer 1 and knows nothing about capabilities, while the kernel is
/// Layer 3 and must not learn what an uplink queue is made of. Having the queue implement
/// <see cref="IAuditCapacity"/> directly would need a reference from Layer 1 up to Layer 3 — the
/// wrong direction, for one small pair of integers. Runtime already sees both, so the join happens
/// here, where the mound is composed.</para>
/// </summary>
public sealed class UplinkAuditCapacity(DurableUplinkQueue queue) : IAuditCapacity
{
    public int PendingRecords => queue.PendingRecords;

    public int CapacityForNewWork => queue.CapacityForNewWork;
}
