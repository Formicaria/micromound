using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Micromound.Drivers;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// The driver-settings schema (<see cref="DriverSchemaCatalog"/>): the machine-readable description of
/// every driver type a manifest can bind and the settings each reads, so a controller can build a
/// plain-language hardware form. The load-bearing property is that the catalog TELLS THE TRUTH about
/// the drivers: every setting a driver actually reads is described, nothing described is unread, and
/// every default the catalog states is the driver's real default. These tests pin that with a
/// recording settings dictionary — the drivers cannot read a key without it being seen.
/// </summary>
public sealed class DriverSchemaTests : IDisposable
{
    private readonly string _sysfs = Path.Combine(Path.GetTempPath(), "mm-schema-" + Guid.NewGuid().ToString("N"));

    public DriverSchemaTests()
    {
        Directory.CreateDirectory(Path.Combine(_sysfs, "gpio17"));   // the kernel would create this on export
    }

    public void Dispose() { try { Directory.Delete(_sysfs, recursive: true); } catch (IOException) { } }

    /// <summary>A settings dictionary that records every key a driver asks for.</summary>
    private sealed class RecordingSettings(Dictionary<string, string> inner) : IReadOnlyDictionary<string, string>
    {
        public HashSet<string> Asked { get; } = new(StringComparer.Ordinal);
        public string this[string key] { get { Asked.Add(key); return inner[key]; } }
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<string> Values => inner.Values;
        public int Count => inner.Count;
        public bool ContainsKey(string key) { Asked.Add(key); return inner.ContainsKey(key); }
        public bool TryGetValue(string key, out string value) { Asked.Add(key); return inner.TryGetValue(key, out value!); }
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => inner.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>An I2C device that answers like an idle ADS1115 — enough for the probe and a read.</summary>
    private sealed class IdleChip : II2cBus
    {
        public void Write(ReadOnlySpan<byte> data) { }
        public void Read(Span<byte> buffer) { buffer[0] = 0x85; buffer[1] = 0x83; }
    }

    private static Dictionary<string, string> FullSettings(DriverTypeSchema schema, string capability)
    {
        // A value for EVERY described setting, so a driver that reads all of them is exercised on all.
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in schema.Settings)
        {
            settings[s.Name] = s.Name switch
            {
                "capability" => capability,
                "pin" => "17",
                "channel" => "1",
                "class" => "controlled",
                "gain" => "2.048",
                "address" => "0x49",
                "bus" => "0",
                "unit" => "pct",
                _ => s.Default ?? (s.Kind == SettingKinds.Boolean ? "true" : "1")
            };
        }
        return settings;
    }

    // ---- the catalog is well-formed ----

    [Fact]
    public void Every_shipped_schema_is_well_formed()
    {
        Assert.NotEmpty(DriverSchemaCatalog.Shipped);
        Assert.Equal(DriverSchemaCatalog.Shipped.Count, DriverSchemaCatalog.Shipped.Select(s => s.DriverType).Distinct().Count());

        foreach (var schema in DriverSchemaCatalog.Shipped)
        {
            Assert.False(string.IsNullOrWhiteSpace(schema.DriverType));
            Assert.False(string.IsNullOrWhiteSpace(schema.Label));
            Assert.False(string.IsNullOrWhiteSpace(schema.Summary));
            Assert.Contains(schema.Role, new[] { DriverRoles.Actuator, DriverRoles.Sensor });
            Assert.Equal(schema.Role == DriverRoles.Actuator ? "act." : "sense.", schema.CapabilityPrefix);
            Assert.Equal(schema.Settings.Count, schema.SettingNames().Distinct().Count());   // no duplicate names

            var capability = Assert.Single(schema.Settings, s => s.Kind == SettingKinds.Capability);
            Assert.True(capability.Required);
            Assert.Equal([schema.CapabilityPrefix], capability.Choices);

            foreach (var s in schema.Settings)
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Name));
                Assert.False(string.IsNullOrWhiteSpace(s.Label), s.Name + " needs a label");
                Assert.False(string.IsNullOrWhiteSpace(s.Help), s.Name + " needs help text");
                Assert.Contains(s.Kind, SettingKinds.All);
                if (s.Required) Assert.Null(s.Default);                              // required means no fallback
                if (s.Kind == SettingKinds.Choice) Assert.NotEmpty(s.Choices);
                if (s.Kind == SettingKinds.Choice && s.Default is not null) Assert.Contains(s.Default, s.Choices);
                if (s.Min is { } min && s.Max is { } max) Assert.True(min <= max);
            }
        }
    }

    [Fact]
    public void The_catalog_survives_the_wire()
    {
        var json = JsonSerializer.Serialize(DriverSchemaCatalog.Shipped, ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<List<DriverTypeSchema>>(json, ProtocolJson.Options)!;

        Assert.Equal(DriverSchemaCatalog.Shipped.Count, back.Count);
        for (var i = 0; i < back.Count; i++)
        {
            var a = DriverSchemaCatalog.Shipped[i];
            var b = back[i];
            Assert.Equal(a.DriverType, b.DriverType);
            Assert.Equal(a.SettingNames(), b.SettingNames());
            Assert.Equal(a.Settings.Select(s => s.Default), b.Settings.Select(s => s.Default));
            Assert.Equal(a.Settings.Select(s => s.HardwareOnly), b.Settings.Select(s => s.HardwareOnly));
        }
        Assert.Contains("\"driver_type\":\"digital_actuator\"", json);
        Assert.Contains("\"hardware_only\":true", json);
    }

    // ---- the catalog tells the truth about the drivers ----

    [Fact]
    public void The_in_memory_actuator_reads_exactly_the_settings_the_catalog_describes() =>
        InMemoryDriverReadsWhatIsDescribed(new DigitalActuatorFactory());

    [Fact]
    public void The_in_memory_sensor_reads_exactly_the_settings_the_catalog_describes() =>
        InMemoryDriverReadsWhatIsDescribed(new AnalogSensorFactory());

    private static void InMemoryDriverReadsWhatIsDescribed(IDriverFactory factory)
    {
        var schema = factory.Schema;
        Assert.Same(DriverSchemaCatalog.Find(factory.DriverType), schema);

        var settings = new RecordingSettings(FullSettings(schema, schema.CapabilityPrefix + "thing"));
        var driver = factory.Create();
        var result = driver.Configure(settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        var described = schema.Settings.Where(s => !s.HardwareOnly).Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        // Every non-hardware setting is read (the in-memory backing ignores the hardware ones)…
        Assert.True(described.IsSubsetOf(settings.Asked), "not read: " + string.Join(", ", described.Except(settings.Asked)));
        // …and nothing is read that the catalog does not describe.
        var undescribed = settings.Asked.Except(schema.SettingNames()).ToList();
        Assert.True(undescribed.Count == 0, "read but undescribed: " + string.Join(", ", undescribed));
    }

    [Fact]
    public void The_hardware_actuator_reads_exactly_the_settings_the_catalog_describes()
    {
        var factory = new SysfsDigitalActuatorFactory(_sysfs);
        var schema = factory.Schema;
        var settings = new RecordingSettings(FullSettings(schema, "act.valve"));

        var result = factory.Create().Configure(settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.True(schema.SettingNames().ToHashSet().SetEquals(settings.Asked),
            $"described: {string.Join(",", schema.SettingNames())} / read: {string.Join(",", settings.Asked)}");
    }

    [Fact]
    public void The_hardware_sensor_reads_exactly_the_settings_the_catalog_describes()
    {
        var factory = new Ads1115AnalogSensorFactory(busFactory: (_, _) => new IdleChip());
        var schema = factory.Schema;
        var settings = new RecordingSettings(FullSettings(schema, "sense.level"));

        var result = factory.Create().Configure(settings);

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.True(schema.SettingNames().ToHashSet().SetEquals(settings.Asked),
            $"described: {string.Join(",", schema.SettingNames())} / read: {string.Join(",", settings.Asked)}");
    }

    [Fact]
    public void Required_settings_are_really_required_and_optional_ones_really_optional()
    {
        // With every required (device) setting present, the hardware drivers configure; drop any one
        // required setting and they refuse; drop every optional one and they still configure.
        var cases = new (IDriverFactory Factory, string Capability)[]
        {
            (new SysfsDigitalActuatorFactory(_sysfs), "act.valve"),
            (new Ads1115AnalogSensorFactory(busFactory: (_, _) => new IdleChip()), "sense.level"),
        };
        foreach (var (factory, capability) in cases)
        {
            var schema = factory.Schema;
            var full = FullSettings(schema, capability);

            var minimal = full.Where(kv => schema.Settings.Single(s => s.Name == kv.Key).Required)
                              .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            Assert.True(factory.Create().Configure(minimal).IsValid, schema.DriverType + " should configure with only its required settings");

            foreach (var required in schema.Settings.Where(s => s.Required))
            {
                var missing = new Dictionary<string, string>(full, StringComparer.Ordinal);
                missing.Remove(required.Name);
                Assert.False(factory.Create().Configure(missing).IsValid, $"{schema.DriverType} configured without required '{required.Name}'");
            }
        }
    }

    [Fact]
    public void Catalog_defaults_are_the_drivers_real_defaults()
    {
        // analog_sensor: scale 1, offset 0, unit "" — observable on the driver.
        var sensorSchema = DriverSchemaCatalog.AnalogSensor;
        var sensor = new AnalogSensorDriver(new InMemoryAnalogInput());
        sensor.Configure(new Dictionary<string, string> { ["capability"] = "sense.x" });
        Assert.Equal(sensor.Scale, Parse(Default(sensorSchema, "scale")));
        Assert.Equal(sensor.Offset, Parse(Default(sensorSchema, "offset")));

        // The ADS1115 defaults: address 0x48, gain 4.096, the bus number the Pi header uses.
        Assert.Equal(Ads1115AnalogInput.DefaultAddress, Convert.ToInt32(Default(sensorSchema, "address")[2..], 16));
        Assert.Equal(4.096, Parse(Default(sensorSchema, "gain")));
        Assert.Equal("1", Default(sensorSchema, "bus"));
        var gains = sensorSchema.Settings.Single(s => s.Name == "gain").Choices.Select(Parse).ToList();
        Assert.Equal(Ads1115AnalogInput.FullScaleRanges, gains);
        var channel = sensorSchema.Settings.Single(s => s.Name == "channel");
        Assert.Equal(0.0, channel.Min!.Value);
        Assert.Equal(3.0, channel.Max!.Value);

        // The catalog's defaults for the ADS1115 factory are what it really uses: with only the
        // required settings, the factory opens bus 1 at 0x48.
        (int Bus, int Address)? opened = null;
        var factory = new Ads1115AnalogSensorFactory(busFactory: (b, a) => { opened = (b, a); return new IdleChip(); });
        Assert.True(factory.Create().Configure(new Dictionary<string, string> { ["capability"] = "sense.x", ["channel"] = "0" }).IsValid);
        Assert.Equal((1, 0x48), opened);

        // digital_actuator: active_high default true → the safe (initial) level is LOW.
        var actuatorSchema = DriverSchemaCatalog.DigitalActuator;
        Assert.Equal("true", Default(actuatorSchema, "active_high"));
        var line = new InMemoryDigitalOutput();
        new DigitalActuatorDriver(line).Configure(new Dictionary<string, string> { ["capability"] = "act.x" });
        Assert.False(line.State);
        Assert.Equal("benign", Default(actuatorSchema, "class"));
        Assert.DoesNotContain("hazardous", actuatorSchema.Settings.Single(s => s.Name == "class").Choices);
    }

    // ---- what the device sends and prints ----

    [Fact]
    public void Both_host_registries_describe_the_same_shipped_catalog()
    {
        var inMemory = MoundHost.DefaultDriverFactories().Describe();
        var hardware = MoundHost.HardwareDriverFactories().Describe();

        Assert.Equal(inMemory.Select(s => s.DriverType), hardware.Select(s => s.DriverType));
        Assert.Equal(DriverSchemaCatalog.Shipped.Select(s => s.DriverType).OrderBy(x => x, StringComparer.Ordinal), inMemory.Select(s => s.DriverType));
        foreach (var s in hardware) Assert.Same(DriverSchemaCatalog.Find(s.DriverType), s);   // one catalog, not copies
    }

    [Fact]
    public void Enrollment_carries_the_driver_schemas()
    {
        var handler = new CapturingHandler();
        using var client = new HttpEnrollmentClient(new Uri("https://controller.test/"), new HttpClient(handler),
            moundId: "mm-1", capabilities: ["sense.x"], driverSchemas: MoundHost.DefaultDriverFactories().Describe());

        client.TryEnroll("tok", new byte[32], out _, out _);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var schemas = body.RootElement.GetProperty("driver_schemas").EnumerateArray().ToList();
        Assert.Equal(DriverSchemaCatalog.Shipped.Count, schemas.Count);
        var actuator = schemas.Single(s => s.GetProperty("driver_type").GetString() == "digital_actuator");
        var pin = actuator.GetProperty("settings").EnumerateArray().Single(s => s.GetProperty("name").GetString() == "pin");
        Assert.True(pin.GetProperty("hardware_only").GetBoolean());
        Assert.True(pin.GetProperty("required").GetBoolean());
        // The fields the reference controller already reads are all still there.
        Assert.Equal("mm-1", body.RootElement.GetProperty("mound_id").GetString());
        Assert.Equal("tok", body.RootElement.GetProperty("token").GetString());
    }

    [Fact]
    public void The_full_catalog_is_the_default_and_an_empty_list_sends_none()
    {
        var handler = new CapturingHandler();
        using (var client = new HttpEnrollmentClient(new Uri("https://controller.test/"), new HttpClient(handler)))
            client.TryEnroll("tok", new byte[32], out _, out _);
        using (var body = JsonDocument.Parse(handler.LastBody!))
            Assert.Equal(DriverSchemaCatalog.Shipped.Count, body.RootElement.GetProperty("driver_schemas").GetArrayLength());

        using (var client = new HttpEnrollmentClient(new Uri("https://controller.test/"), new HttpClient(handler), driverSchemas: []))
            client.TryEnroll("tok", new byte[32], out _, out _);
        using (var body = JsonDocument.Parse(handler.LastBody!))
            Assert.Equal(0, body.RootElement.GetProperty("driver_schemas").GetArrayLength());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var key = Convert.ToHexString(new byte[32]).ToLowerInvariant();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"controller_public_key\":\"{key}\"}}", Encoding.UTF8, "application/json") };
        }
    }

    private static string Default(DriverTypeSchema schema, string name) => schema.Settings.Single(s => s.Name == name).Default!;
    private static double Parse(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
