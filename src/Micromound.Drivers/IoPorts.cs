using Micromound.Protocol;

namespace Micromound.Drivers;

/// <summary>
/// A driver that observes and reports readings — the evidence side of a sensor. Composition wires
/// <see cref="Publish"/> to the mound's evidence sink so a reading reaches the local store and the
/// uplink; a pure actuator produces no evidence and need not implement this. Kept off
/// <see cref="IDriver"/> itself because not every driver is an evidence source.
/// </summary>
public interface IEvidenceSource
{
    /// <summary>Where this driver's readings go. Wired by composition; null until then.</summary>
    Action<EvidenceItem>? Publish { get; set; }
}

/// <summary>
/// The narrow hardware seam a generic driver primitive sits on — one line or one channel, nothing
/// device-specific. A primitive turns a semantic capability (<c>act.water_valve</c>) into reads and
/// writes on one of these, and knows nothing about the board, bus, or part number underneath.
///
/// Real Linux backings (sysfs / libgpiod GPIO, an ADC over I2C/SPI) implement these in a later M4
/// slice; the in-memory ports here back the primitives in the simulator and in tests, so the
/// primitive's logic — capability exposure, limits, safe state, evidence — is proven without any
/// hardware present.
/// </summary>
public interface IDigitalOutput
{
    /// <summary>The line's current logical level.</summary>
    bool State { get; }

    /// <summary>Drive the line high or low.</summary>
    void Write(bool high);
}

/// <summary>One analog input channel — the seam an analog-sensor primitive reads a number from.</summary>
public interface IAnalogInput
{
    /// <summary>Sample the channel now.</summary>
    double Read();
}

/// <summary>An in-memory digital line: the default backing for the simulator and tests.</summary>
public sealed class InMemoryDigitalOutput : IDigitalOutput
{
    public bool State { get; private set; }

    /// <summary>How many times the line was written — the fake world's ground truth for a test.</summary>
    public int Writes { get; private set; }

    public void Write(bool high)
    {
        State = high;
        Writes++;
    }
}

/// <summary>An in-memory analog channel whose value a test or harness sets.</summary>
public sealed class InMemoryAnalogInput : IAnalogInput
{
    /// <summary>The quantity the fake world currently holds.</summary>
    public double Value { get; set; }

    public double Read() => Value;
}
