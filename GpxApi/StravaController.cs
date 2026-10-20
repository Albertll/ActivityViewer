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
    private readonly ILogger<StravaController> _logger;

    public StravaController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        AppDbContext db,
        EncryptionService encryption,
        StravaActivitySyncService syncService,
        ILogger<StravaController> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _db = db;
        _encryption = encryption;
        _syncService = syncService;
        _logger = logger;
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

        // Bez logów serwera (darmowy hosting) jedynym źródłem diagnozy jest odpowiedź - przy błędzie
        // pokazujemy etap i typy wyjątków (bez treści komunikatów, które mogą zawierać ścieżki i nazwy baz)
        var stage = "wymiana kodu na token w Stravie";
        try
        {
            return await CompleteLoginAsync(code, stageName => stage = stageName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Auth] Logowanie nieudane na etapie: {Stage}", stage);
            return StatusCode(500,
                $"Logowanie nie powiodło się (etap: {stage}; {DescribeException(ex)}). Spróbuj ponownie: /auth/login");
        }
    }

    private async Task<IActionResult> CompleteLoginAsync(string code, Action<string> setStage)
    {
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
        setStage("zapis użytkownika w bazie");
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
        setStage("utworzenie sesji (cookie, klucze Data Protection)");
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
    /// Opis wyjątku do komunikatu dla użytkownika: same typy z łańcucha InnerException plus kody
    /// (numer błędu SQL, kod gniazda, HResult kryptografii) - bez treści komunikatów.
    /// </summary>
    internal static string DescribeException(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null && parts.Count < 4; e = e.InnerException)
        {
            parts.Add(e switch
            {
                Microsoft.Data.SqlClient.SqlException sql => $"SqlException #{sql.Number}",
                System.Net.Sockets.SocketException sock => $"SocketException {sock.SocketErrorCode}",
                CryptographicException crypto => $"CryptographicException 0x{crypto.HResult:X8}",
                _ => e.GetType().Name
            });
        }
        return string.Join(" <- ", parts);
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
