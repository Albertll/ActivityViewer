using GpxApi.Data;
using GpxApi.Models;
using GpxApi.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

[ApiController]
[Route("auth")]
public class StravaController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly AppDbContext _db;
    private readonly EncryptionService _encryption;
    private readonly StravaActivitySyncService _syncService;

    public StravaController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        AppDbContext db,
        EncryptionService encryption,
        StravaActivitySyncService syncService)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _db = db;
        _encryption = encryption;
        _syncService = syncService;
    }

    /// <summary>
    /// Przekierowanie do Strava OAuth
    /// </summary>
    [HttpGet("login")]
    public IActionResult Login()
    {
        // Parametr state chroni callback przed CSRF (podstawieniem cudzego code)
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        Response.Cookies.Append(OAuthStateCookie, state, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            MaxAge = TimeSpan.FromMinutes(10),
            IsEssential = true
        });

        var clientId = _configuration["Strava:ClientId"];
        var redirectUri = $"{Request.Scheme}://{Request.Host}/auth/callback";
        var url = $"https://www.strava.com/oauth/authorize?client_id={clientId}&response_type=code&redirect_uri={Uri.EscapeDataString(redirectUri)}&approval_prompt=auto&scope=activity:read_all&state={state}";
        return Redirect(url);
    }

    private const string OAuthStateCookie = "strava_oauth_state";

    /// <summary>
    /// Callback po zalogowaniu przez Stravę - walidacja state, wymiana kodu na token, tworzenie sesji
    /// </summary>
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string code, [FromQuery] string? state = null)
    {
        if (string.IsNullOrEmpty(code))
            return BadRequest("Brak code");

        var expectedState = Request.Cookies[OAuthStateCookie];
        Response.Cookies.Delete(OAuthStateCookie);
        if (string.IsNullOrEmpty(expectedState) || state != expectedState)
            return BadRequest("Nieprawidłowy state OAuth - rozpocznij logowanie od /auth/login");

        var clientId = _configuration["Strava:ClientId"];
        var clientSecret = _configuration["Strava:ClientSecret"];

        var client = _httpClientFactory.CreateClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", clientId!),
            new KeyValuePair<string, string>("client_secret", clientSecret!),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("grant_type", "authorization_code")
        });

        var resp = await client.PostAsync("https://www.strava.com/oauth/token", content);
        var json = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            return StatusCode(502, "Błąd wymiany tokenu ze Stravą");

        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("access_token", out var tokenEl) ||
            !root.TryGetProperty("athlete", out var athleteEl) ||
            !athleteEl.TryGetProperty("id", out var athleteIdEl))
        {
            return StatusCode(502, "Nieprawidłowa odpowiedź ze Stravy");
        }

        var accessToken = tokenEl.GetString()!;
        var athleteId = athleteIdEl.GetInt64();
        var athleteName = "";
        if (athleteEl.TryGetProperty("firstname", out var fn))
            athleteName += fn.GetString();
        if (athleteEl.TryGetProperty("lastname", out var ln))
            athleteName += " " + ln.GetString();
        athleteName = athleteName.Trim();

        string? refreshToken = null;
        long? expiresAt = null;
        if (root.TryGetProperty("refresh_token", out var rtEl))
            refreshToken = rtEl.GetString();
        if (root.TryGetProperty("expires_at", out var expEl))
            expiresAt = expEl.GetInt64();

        // Znajdź lub utwórz użytkownika
        var user = await _db.Users.FirstOrDefaultAsync(u => u.StravaAthleteId == athleteId);
        bool isFirstLogin = false;
        if (user == null)
        {
            isFirstLogin = true;
            user = new AppUser
            {
                StravaAthleteId = athleteId,
                StravaName = athleteName,
                CreatedAt = DateTime.UtcNow
            };
            _db.Users.Add(user);
        }
        else
        {
            isFirstLogin = !user.IsFirstSyncComplete;
        }

        user.StravaName = athleteName;
        user.LastLoginAt = DateTime.UtcNow;
        user.EncryptedAccessToken = _encryption.EncryptToken(accessToken);
        user.TokenExpiresAt = expiresAt;
        if (!string.IsNullOrEmpty(refreshToken))
            user.EncryptedRefreshToken = _encryption.EncryptToken(refreshToken);

        await _db.SaveChangesAsync();

        // Zaloguj użytkownika (cookie)
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new("StravaAthleteId", athleteId.ToString()),
            new(ClaimTypes.Name, athleteName)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
            new AuthenticationProperties { IsPersistent = true, ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30) });

        // Uruchom synchronizację aktywności w tle (token pobierze z bazy, odświeży jeśli trzeba)
        _syncService.TriggerSync(user.Id, athleteId, isFirstLogin);

        return Redirect("/intervals.html");
    }

    /// <summary>
    /// Wylogowanie
    /// </summary>
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Redirect("/intervals.html");
    }

    /// <summary>
    /// Informacje o zalogowanym użytkowniku
    /// </summary>
    [HttpGet("me")]
    public IActionResult Me()
    {
        if (User.Identity?.IsAuthenticated != true)
            return Unauthorized(new { error = "Niezalogowany" });

        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        var athleteId = User.FindFirstValue("StravaAthleteId");
        var name = User.FindFirstValue(ClaimTypes.Name);

        return Ok(new { userId, athleteId, name });
    }

}
