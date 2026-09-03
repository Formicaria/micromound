using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Micromound.Crypto;
using Micromound.Protocol;
using Micromound.Sync;

namespace Micromound.Host;

/// <summary>
/// Enrollment over HTTP — PROTOCOL.md §3. The device presents its one-time, operator-minted token
/// and its own public key to <c>&lt;controller&gt;/micromound/v0/enroll</c>; the controller binds the
/// key to the mound record, burns the token, and returns its own public key so the device can from
/// then on verify downlink. This is the one bootstrap exchange that precedes signed traffic: the
/// controller does not yet know the device key, so the token is what authorizes the request.
///
/// <para><b>What the device says about itself, and why each field is there.</b> The controller
/// (ANTHILL) looks the mound up BY THE TOKEN — which mound this is was settled by the operator who
/// minted it, and is not a claim a device gets to make. The device still sends its manifest
/// <c>mound_id</c> as a <em>cross-check</em>: the device signs every later uplink with that id, so if
/// the operator minted the token for a different mound the mismatch is refused here, loudly, instead
/// of surfacing as a signature refusal on every beat that nobody could explain. <c>tier</c> must be
/// one of <see cref="ControllerTiers"/> — the controller refuses an unknown tier. <c>protocol_version</c>
/// is sent explicitly so a version skew is caught at the door rather than assumed away by a default.
/// <c>capabilities</c> is the structured list (the fleet view is built from it); <c>hardware_profile</c>
/// is the older flat summary, kept for controllers that read only that.</para>
///
/// <para>Failure is two different things, kept distinct. A refusal is a DEFINITE answer (4xx) —
/// retrying will never help, so it returns false with the controller's own reason and the caller
/// stops. An unreachable controller is transient — the device simply is not enrolled yet and can try
/// again on the next boot. Neither throws.</para>
/// </summary>
public sealed class HttpEnrollmentClient : IEnrollmentClient, IDisposable
{
    /// <summary>The enroll path under the controller base, per PROTOCOL.md §1 (<c>/micromound/v0/*</c>).</summary>
    public const string EnrollPath = "micromound/v0/enroll";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _enrollEndpoint;
    private readonly TimeSpan _timeout;
    private readonly string _moundId;
    private readonly string _hardwareProfile;
    private readonly string _tier;
    private readonly IReadOnlyList<string> _capabilities;

    /// <param name="moundId">This device's manifest mound id, sent as the cross-check described above. Empty skips the check.</param>
    /// <param name="hardwareProfile">A flat summary of the mound's hardware, sent per PROTOCOL.md §3.2.</param>
    /// <param name="tier">The mound's tier — one of <see cref="ControllerTiers"/>. Defaults to <see cref="ControllerTiers.EdgeQueen"/>.</param>
    /// <param name="capabilities">The mound's declared capability ids, sent as a structured list.</param>
    public HttpEnrollmentClient(Uri controllerBaseUrl, HttpClient? http = null, TimeSpan? timeout = null,
        string hardwareProfile = "", string tier = ControllerTiers.EdgeQueen, string moundId = "",
        IReadOnlyList<string>? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(controllerBaseUrl);
        if (!ControllerTiers.IsKnown(tier))
            throw new ArgumentException(
                $"'{tier}' is not a tier the controller accepts; use {ControllerTiers.EdgeQueen} or {ControllerTiers.DeterministicController}",
                nameof(tier));

        var root = controllerBaseUrl.AbsoluteUri.EndsWith('/') ? controllerBaseUrl : new Uri(controllerBaseUrl.AbsoluteUri + "/");
        _enrollEndpoint = new Uri(root, EnrollPath);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { MaxResponseContentBufferSize = 64 * 1024 };
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _moundId = moundId;
        _hardwareProfile = hardwareProfile;
        _tier = tier;
        _capabilities = capabilities ?? [];
    }

    public bool TryEnroll(string token, byte[] devicePublicKey, out ControllerEnrollment? enrollment, out string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(devicePublicKey);
        enrollment = null;

        try
        {
            var request = new EnrollRequest(
                token,
                _moundId,
                Convert.ToHexString(devicePublicKey).ToLowerInvariant(),
                _hardwareProfile,
                _tier,
                _capabilities,
                ProtocolVersion.Current);
            var json = JsonSerializer.Serialize(request, EnrollJson);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(_timeout);

            using var response = _http.PostAsync(_enrollEndpoint, content, cts.Token).GetAwaiter().GetResult();
            var body = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();

            // A 4xx is a definite refusal — a burned or unknown token, a wrong tier, a version skew —
            // not something a retry fixes. The controller says WHY in the body; carry that up, because
            // the operator minted this token minutes ago and is standing next to the hardware.
            if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
            {
                var reason = TryReadRefusalReason(body);
                detail = $"enrollment refused: HTTP {(int)response.StatusCode}" +
                         (reason is null ? " (token burned or unknown)" : $" — {reason}");
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                detail = $"controller returned HTTP {(int)response.StatusCode}; enrollment not yet complete";
                return false;
            }

            var parsed = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<EnrollResponse>(body, EnrollJson);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.ControllerPublicKey))
            {
                detail = "controller response carried no controller_public_key";
                return false;
            }

            var key = Convert.FromHexString(parsed.ControllerPublicKey);
            // The mound is about to trust this key for ALL downlink verification and persist it. A
            // wrong-length or all-zero key would verify nothing yet stick forever (a permanent brick),
            // so a malformed key is a failed enrollment, not a trusted controller.
            if (key.Length != Ed25519KeyPair.PublicKeyLength || Array.TrueForAll(key, b => b == 0))
            {
                detail = $"controller returned an invalid public key ({key.Length} bytes); not enrolled";
                return false;
            }

            // The device-side half of the cross-check. The controller bound our key to ITS record of
            // the mound; if that is not the mound our manifest says we are, every beat we sign will be
            // attributed to a mound the controller holds no key for. Refuse now, with the two names.
            var controllerMoundId = parsed.MoundId ?? "";
            if (!string.IsNullOrWhiteSpace(_moundId) && !string.IsNullOrWhiteSpace(controllerMoundId)
                && !string.Equals(_moundId, controllerMoundId, StringComparison.Ordinal))
            {
                detail = $"controller bound this key to mound '{controllerMoundId}' but this device's manifest is '{_moundId}'; " +
                         "the token was minted for a different mound — not enrolled";
                return false;
            }

            // A controller speaking a different protocol version cannot be trusted to mean what its
            // envelopes say. It is refused explicitly rather than discovered as parse failures later.
            if (parsed.ProtocolVersion is { } version && version != ProtocolVersion.Current)
            {
                detail = $"controller speaks protocol version {version}, this device speaks {ProtocolVersion.Current}; not enrolled";
                return false;
            }

            // A non-positive or non-finite cadence is not a cadence — keep the local one.
            double? syncInterval = parsed.SyncIntervalSeconds is { } s && double.IsFinite(s) && s > 0 ? s : null;

            enrollment = new ControllerEnrollment(key, controllerMoundId, syncInterval, parsed.ProtocolVersion, parsed.ColonyVersion ?? "");
            detail = "enrolled" + (syncInterval is { } si ? $" (controller asks for a {si:0.#}s sync cadence)" : "");
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or OperationCanceledException or NotSupportedException or InvalidOperationException or UriFormatException)
        {
            detail = "controller unreachable; not enrolled yet: " + ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            detail = "enrollment response unreadable: " + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }

    /// <summary>The controller's stated reason for a refusal, if its body carried one; null otherwise.</summary>
    private static string? TryReadRefusalReason(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        try
        {
            var refusal = JsonSerializer.Deserialize<EnrollRefusal>(body, EnrollJson);
            return string.IsNullOrWhiteSpace(refusal?.Reason) ? null : refusal.Reason;
        }
        catch (JsonException)
        {
            return null;   // not JSON, or not the shape we know — the status code is still the answer
        }
    }

    private static readonly JsonSerializerOptions EnrollJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record EnrollRequest(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("mound_id")] string MoundId,
        [property: JsonPropertyName("device_public_key")] string DevicePublicKey,
        [property: JsonPropertyName("hardware_profile")] string HardwareProfile,
        [property: JsonPropertyName("tier")] string Tier,
        [property: JsonPropertyName("capabilities")] IReadOnlyList<string> Capabilities,
        [property: JsonPropertyName("protocol_version")] int ProtocolVersion);

    private sealed record EnrollResponse(
        [property: JsonPropertyName("controller_public_key")] string ControllerPublicKey,
        [property: JsonPropertyName("mound_id")] string? MoundId,
        [property: JsonPropertyName("sync_interval_s")] double? SyncIntervalSeconds,
        [property: JsonPropertyName("protocol_version")] int? ProtocolVersion,
        [property: JsonPropertyName("colony_version")] string? ColonyVersion);

    private sealed record EnrollRefusal(
        [property: JsonPropertyName("accepted")] bool? Accepted,
        [property: JsonPropertyName("reason")] string? Reason);
}
