using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Micromound.Crypto;
using Micromound.Sync;

namespace Micromound.Host;

/// <summary>
/// Enrollment over HTTP — PROTOCOL.md §3. The device presents its one-time, operator-minted token
/// and its own public key to <c>&lt;controller&gt;/micromound/v0/enroll</c>; the controller binds the
/// key to the mound record, burns the token, and returns its own public key so the device can from
/// then on verify downlink. This is the one bootstrap exchange that precedes signed traffic: the
/// controller does not yet know the device key, so the token is what authorizes the request.
///
/// <para>Failure is two different things, kept distinct. A burned or unknown token is a DEFINITE
/// refusal (4xx) — retrying will never help, so it returns false with a reason and the caller stops.
/// An unreachable controller is transient — the device simply is not enrolled yet and can try again
/// on the next boot. Neither throws.</para>
/// </summary>
public sealed class HttpEnrollmentClient : IEnrollmentClient, IDisposable
{
    /// <summary>The enroll path under the controller base, per PROTOCOL.md §1 (<c>/micromound/v0/*</c>).</summary>
    public const string EnrollPath = "micromound/v0/enroll";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _enrollEndpoint;
    private readonly TimeSpan _timeout;
    private readonly string _hardwareProfile;
    private readonly string _tier;

    /// <param name="hardwareProfile">A summary of the mound's hardware, sent per PROTOCOL.md §3.2.</param>
    /// <param name="tier">The mound's tier (e.g. <c>mound_major</c>), sent per PROTOCOL.md §3.2.</param>
    public HttpEnrollmentClient(Uri controllerBaseUrl, HttpClient? http = null, TimeSpan? timeout = null,
        string hardwareProfile = "", string tier = "")
    {
        ArgumentNullException.ThrowIfNull(controllerBaseUrl);
        var root = controllerBaseUrl.AbsoluteUri.EndsWith('/') ? controllerBaseUrl : new Uri(controllerBaseUrl.AbsoluteUri + "/");
        _enrollEndpoint = new Uri(root, EnrollPath);
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { MaxResponseContentBufferSize = 64 * 1024 };
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _hardwareProfile = hardwareProfile;
        _tier = tier;
    }

    public bool TryEnroll(string token, byte[] devicePublicKey, out byte[] controllerPublicKey, out string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(devicePublicKey);
        controllerPublicKey = [];

        try
        {
            var request = new EnrollRequest(token, Convert.ToHexString(devicePublicKey).ToLowerInvariant(), _hardwareProfile, _tier);
            var json = JsonSerializer.Serialize(request, EnrollJson);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var cts = new CancellationTokenSource(_timeout);

            using var response = _http.PostAsync(_enrollEndpoint, content, cts.Token).GetAwaiter().GetResult();

            // A 4xx is a definite refusal — a burned or unknown token — not something a retry fixes.
            if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
            {
                detail = $"enrollment refused: HTTP {(int)response.StatusCode} (token burned or unknown)";
                return false;
            }
            if (!response.IsSuccessStatusCode)
            {
                detail = $"controller returned HTTP {(int)response.StatusCode}; enrollment not yet complete";
                return false;
            }

            var body = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
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

            controllerPublicKey = key;
            detail = "enrolled";
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

    private static readonly JsonSerializerOptions EnrollJson = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    private sealed record EnrollRequest(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("device_public_key")] string DevicePublicKey,
        [property: JsonPropertyName("hardware_profile")] string HardwareProfile,
        [property: JsonPropertyName("tier")] string Tier);

    private sealed record EnrollResponse(
        [property: JsonPropertyName("controller_public_key")] string ControllerPublicKey);
}
