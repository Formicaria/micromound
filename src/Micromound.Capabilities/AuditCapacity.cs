namespace Micromound.Capabilities;

/// <summary>
/// How much room the mound's audit path has left — the seam through which the capability kernel
/// asks, before authorizing new physical work, whether it will be able to say that the work
/// happened.
///
/// <para><b>Why the kernel asks at all.</b> Every actuation produces an action record, and that
/// record has to survive until the controller acknowledges it. The uplink queue is bounded (it has
/// to be — an unbounded one fills the disk of a mound that is offline long enough), and until now
/// the bound was enforced <em>after</em> the fact: the work happened, the record was written, and
/// the oldest envelope was spilled to make room. That is the wrong way round. Spilling loses
/// history the mound already owes; refusing loses only work that has not happened yet, and work
/// that has not happened can be asked for again. So the bound moves in front of the effect:
/// <b>a mound that cannot record what it did must not do it.</b></para>
///
/// <para><b>Why two numbers and not a bool.</b> The rule the kernel applies is exactly
/// <c>PendingRecords &lt; CapacityForNewWork</c>, and the C mirror (<c>mm_kernel</c>) applies the
/// same rule over the same two integers. A bool would let the two implementations disagree about
/// what "full" means without any fixture being able to see it; two numbers and one comparison
/// cannot drift. The implementation folds every bound it has — item count, byte ceiling, whatever
/// a future one adds — into <see cref="CapacityForNewWork"/>, so the kernel never needs to learn
/// what the audit path is made of.</para>
///
/// <para><b>The reserve is the implementation's business, not the kernel's.</b> Capacity is
/// deliberately NOT the whole bound: something has to hold the refusal record this check causes,
/// and the acknowledgements and beats that let the mound climb back out. The queue holds a slice
/// of its bound back for those and reports the rest here. A kernel that could refuse but not
/// record the refusal would have swapped one silent failure for another.</para>
/// </summary>
public interface IAuditCapacity
{
    /// <summary>Records already queued and not yet acknowledged by the controller.</summary>
    int PendingRecords { get; }

    /// <summary>
    /// The count at which a new effect's record would no longer fit — the bound minus whatever is
    /// held back for records that explain rather than report. Zero or less means no bound is in
    /// force and the kernel does not apply the check at all.
    /// </summary>
    int CapacityForNewWork { get; }
}
