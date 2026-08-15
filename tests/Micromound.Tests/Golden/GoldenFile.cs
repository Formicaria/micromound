using System.Runtime.CompilerServices;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Approval-style fixtures for the canonical wire bytes.
///
/// These files are the artifact the M3 ESP32 firmware's C protocol mirror is verified against:
/// same input, same canonical bytes, same digest, or the two implementations have drifted.
/// That makes an unexpected diff here a *protocol change*, never a test to re-baseline in
/// passing — which is why a missing or regenerated golden fails the run instead of quietly
/// writing itself green.
///
/// Bootstrapping or an intentional protocol change:
///
///     MICROMOUND_UPDATE_GOLDEN=1 dotnet test Micromound.sln
///
/// then read the diff, make sure it is the change you meant, and commit it.
/// </summary>
public static class GoldenFile
{
    private const string UpdateVariable = "MICROMOUND_UPDATE_GOLDEN";

    public static void Verify(string name, string actual, [CallerFilePath] string callerFile = "")
    {
        var directory = Path.Combine(Path.GetDirectoryName(callerFile)!, "files");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);

        var update = Environment.GetEnvironmentVariable(UpdateVariable) == "1";
        var normalized = Normalize(actual);
        var existed = File.Exists(path);

        if (!existed || update)
        {
            File.WriteAllText(path, normalized + "\n");
            Assert.Fail(
                $"Golden file '{name}' was {(existed ? "regenerated" : "created")} at {path}. " +
                "Review it, confirm the bytes are the wire format you intend to freeze, and commit it. " +
                "The M3 C firmware mirror is verified against this file, so a change here is a protocol change.");
        }

        Assert.Equal(Normalize(File.ReadAllText(path)), normalized);
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n").TrimEnd();
}
