namespace Micromound.Sync;

/// <summary>
/// The persistence seam for operational state — what survives a process or device restart.
///
/// Defined here, next to its first consumer (the durable uplink queue), and wrapped by the Cache
/// Ant in the runtime above. It is a string-keyed document store and nothing more: no queries, no
/// transactions, no schema. Everything MicroMound persists is small, self-contained, and written
/// whole — an active charter, a queue snapshot, a lease expiry — and a store this narrow can be
/// backed by a directory of files on a Pi (M4) or a flash partition on a controller (M5) without
/// either end pretending to be a database.
///
/// Implementations must make <see cref="Put"/> atomic per key: a torn write that leaves half a
/// charter on disk is worse than a missing one, because a missing key restores to observe-only
/// and a corrupt one restores to an argument.
/// </summary>
public interface IStateStore
{
    void Put(string key, string value);

    bool TryGet(string key, out string value);

    void Delete(string key);
}

/// <summary>
/// The in-memory store: the default for the simulator and for tests, and the fallback for a
/// deployment that explicitly opts out of persistence. State kept here does not survive a
/// restart — which is safe by construction, because every restore path in this repository treats
/// a missing key as "start from observe-only", never as an error to work around.
/// </summary>
public sealed class InMemoryStateStore : IStateStore
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public int Count => _values.Count;

    public void Put(string key, string value) => _values[key] = value;

    public bool TryGet(string key, out string value)
    {
        if (_values.TryGetValue(key, out var found))
        {
            value = found;
            return true;
        }

        value = "";
        return false;
    }

    public void Delete(string key) => _values.Remove(key);
}
