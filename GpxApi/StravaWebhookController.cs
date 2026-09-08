using GpxApi.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Webhook Stravy: Strava sama powiadamia o nowych aktywnościach (push zamiast pull).
/// GET = weryfikacja subskrypcji (challenge), POST = zdarzenia. Endpointy anonimowe -
/// wywołuje je Strava, a jedyną akcją jest dociągnięcie danych znanego użytkownika.
/// </summary>
[ApiController]
[Route("api/strava/webhook")]
public class StravaWebhookController : ControllerBase
{
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _db;
    private readonly StravaActivitySyncService _syncService;
    private readonly ILogger<StravaWebhookController> _logger;

    public StravaWebhookController(
        IConfiguration configuration,
        AppDbContext db,
        StravaActivitySyncService syncService,
        ILogger<StravaWebhookController> logger)
    {
        _configuration = configuration;
        _db = db;
        _syncService = syncService;
        _logger = logger;
    }

    /// <summary>
    /// Weryfikacja subskrypcji: Strava wysyła hub.challenge, który trzeba odesłać w JSON-ie.
    /// </summary>
    [HttpGet]
    public IActionResult Verify(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.challenge")] string? challenge,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken)
    {
        if (mode == "subscribe" && challenge != null &&
            verifyToken == StravaWebhook.ComputeVerifyToken(_configuration))
        {
            _logger.LogInformation("[StravaWebhook] Weryfikacja subskrypcji OK");
            return Ok(new Dictionary<string, string> { ["hub.challenge"] = challenge });
        }

        _logger.LogWarning("[StravaWebhook] Nieudana weryfikacja subskrypcji (mode={Mode})", mode);
        return StatusCode(403);
    }

    /// <summary>
    /// Zdarzenie ze Stravy. Nowa aktywność -> sync w tle; cofnięcie autoryzacji -> czyszczenie tokenów.
    /// Zawsze odpowiadamy 200 (Strava wymaga odpowiedzi w 2 s i ponawia przy błędach).
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Event([FromBody] JsonElement evt)
    {
        try
        {
            var objectType = evt.TryGetProperty("object_type", out var ot) ? ot.GetString() : null;
            var aspectType = evt.TryGetProperty("aspect_type", out var at) ? at.GetString() : null;
            var ownerId = evt.TryGetProperty("owner_id", out var oi) ? oi.GetInt64() : 0;

            if (ownerId == 0)
                return Ok();

            var user = await _db.Users.FirstOrDefaultAsync(u => u.StravaAthleteId == ownerId);
            if (user == null)
                return Ok();

            if (objectType == "activity" && aspectType == "create")
            {
                _logger.LogInformation("[StravaWebhook] Nowa aktywność athleteId={AthleteId} - odpalam sync", ownerId);
                _syncService.TriggerSync(user.Id, ownerId, false);
            }
            else if (objectType == "athlete" &&
                     evt.TryGetProperty("updates", out var updates) &&
                     updates.TryGetProperty("authorized", out var authorized) &&
                     authorized.GetString() == "false")
            {
                _logger.LogInformation("[StravaWebhook] Użytkownik athleteId={AthleteId} cofnął autoryzację - czyszczę tokeny", ownerId);
                user.EncryptedAccessToken = null;
                user.EncryptedRefreshToken = null;
                user.TokenExpiresAt = null;
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StravaWebhook] Błąd obsługi zdarzenia");
        }

        return Ok();
    }
}

/// <summary>
/// Token weryfikacyjny webhooka - deterministycznie wyprowadzany z ClientSecret,
/// żeby nie trzymać kolejnego sekretu w konfiguracji (można nadpisać kluczem Strava:WebhookVerifyToken).
/// </summary>
internal static class StravaWebhook
{
    public static string ComputeVerifyToken(IConfiguration configuration)
    {
        var explicitToken = configuration["Strava:WebhookVerifyToken"];
        if (!string.IsNullOrWhiteSpace(explicitToken))
            return explicitToken;

        var secret = configuration["Strava:ClientSecret"] ?? "";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret + "|gpxapp-webhook"));
        return Convert.ToHexString(hash)[..32];
    }
}
