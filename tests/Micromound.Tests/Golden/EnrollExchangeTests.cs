using System.Net;
using System.Text;
using Micromound.Host;
using Micromound.Protocol;
using Xunit;

namespace Micromound.Tests;

/// <summary>
/// Freezes the enrollment exchange (PROTOCOL.md §3) as <c>HttpEnrollmentClient</c> performs it: the
/// exact request body for a fixed device, and the client's verdict — enrolled or not, and the detail
/// line — for a scripted set of controller responses. <c>firmware/micromound-c</c>'s <c>mm_enroll</c>
/// must produce the same body and reach the same verdicts in the same words, so a constrained
/// device enrolls exactly as a Pi does and an operator reads the same line either way.
/// </summary>
public class EnrollExchangeTests
{
    private static readonly Uri Controller = new("https://anthill.local:8443/");
    private const string MoundId = "mm-7f3a0000-0000-4000-8000-000000000001";
    private static readonly byte[] DevicePk = Convert.FromHexString("03a107bff3ce10be1d70dd18e74bc09967e4d6309ba50d5f1ddc8664125531b8");
    private static readonly string ControllerPkHex = "29acbae141bccaf0b22e1a94d34d0bc7361e526d0bfe12c89794bc9322966dd7";

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri?.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond();
        }
    }

    private static HttpResponseMessage Respond(HttpStatusCode status, string? json) =>
        new(status) { Content = json is null ? new StringContent("") : new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpEnrollmentClient Client(StubHandler handler) => new(Controller, new HttpClient(handler),
        hardwareProfile: "sense.temp,act.relay_1",
        tier: ControllerTiers.DeterministicController,
        moundId: MoundId,
        capabilities: ["sense.temp", "act.relay_1"],
        driverSchemas: []);   // a constrained device's hardware is compiled in, not described

    [Fact]
    public void The_enrollment_exchange_is_frozen()
    {
        var report = new StringBuilder();
        report.AppendLine("# MICROMOUND enrollment exchange — golden fixture");
        report.AppendLine("#");
        report.AppendLine("# Frozen by tests/Micromound.Tests/Golden/EnrollExchangeTests.cs from HttpEnrollmentClient (PROTOCOL.md §3).");
        report.AppendLine("# The C client (mm_enroll) must send the same `request:` body and reach each `verdict:` in the same words.");
        report.AppendLine("# Device: mound mm-7f3a0000-…, tier deterministic_controller, capabilities sense.temp + act.relay_1, no driver schemas.");
        report.AppendLine();

        var first = new StubHandler(() => Respond(HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\",\"mound_id\":\"{MoundId}\",\"sync_interval_s\":10,\"protocol_version\":0,\"colony_version\":\"0.3.8.122\"}}"));
        using (var client = Client(first)) client.TryEnroll("tok-0001", DevicePk, out _, out _);
        report.AppendLine($"path:      {first.LastPath}");
        report.AppendLine($"request:   {first.LastBody}");
        report.AppendLine();

        void Case(string label, HttpStatusCode status, string? body)
        {
            var handler = new StubHandler(() => Respond(status, body));
            using var client = Client(handler);
            var ok = client.TryEnroll("tok-0001", DevicePk, out var enrollment, out var detail);
            report.AppendLine($"## {label}");
            report.AppendLine($"response:  {(int)status} {body ?? "(no body)"}");
            report.AppendLine($"verdict:   {(ok ? "enrolled" : "not enrolled")}");
            report.AppendLine($"detail:    {detail}");
            if (ok)
                report.AppendLine($"result:    key={Convert.ToHexStringLower(enrollment!.ControllerPublicKey)} mound={enrollment.MoundId} " +
                                  $"sync={(enrollment.SyncIntervalSeconds is { } s ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : "none")}");
            report.AppendLine();
        }

        Case("accepted, with cadence", HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\",\"mound_id\":\"{MoundId}\",\"sync_interval_s\":10,\"protocol_version\":0,\"colony_version\":\"0.3.8.122\"}}");
        Case("accepted, key only", HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\"}}");
        Case("accepted, fractional cadence", HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\",\"sync_interval_s\":7.5}}");
        Case("accepted, non-positive cadence ignored", HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\",\"sync_interval_s\":0}}");
        Case("accepted, unknown members skipped", HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\",\"future\":{{\"x\":[1,2]}}}}");
        Case("refused with a reason", HttpStatusCode.Conflict, "{\"accepted\":false,\"reason\":\"token already used\"}");
        Case("refused without a reason", HttpStatusCode.NotFound, null);
        Case("refused with an unreadable body", HttpStatusCode.Forbidden, "not json");
        Case("controller error", HttpStatusCode.InternalServerError, "{\"error\":\"db\"}");
        Case("no key in the response", HttpStatusCode.OK, "{\"mound_id\":\"" + MoundId + "\"}");
        Case("empty response", HttpStatusCode.OK, "");
        Case("a zero key", HttpStatusCode.OK, "{\"controller_public_key\":\"" + new string('0', 64) + "\"}");
        Case("a short key", HttpStatusCode.OK, "{\"controller_public_key\":\"0011223344\"}");
        Case("bound to another mound", HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\",\"mound_id\":\"mm-other\"}}");
        Case("protocol version skew", HttpStatusCode.OK, $"{{\"controller_public_key\":\"{ControllerPkHex}\",\"protocol_version\":1}}");

        GoldenFile.Verify("enroll-exchange.txt", report.ToString());
    }
}
