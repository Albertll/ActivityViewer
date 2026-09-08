using System.Text.Json;

/// <summary>
/// Automatyczna rejestracja subskrypcji webhooka Stravy przy starcie aplikacji.
/// Działa tylko, gdy w konfiguracji jest Strava:WebhookCallbackUrl (publiczny adres
/// endpointu /api/strava/webhook) - lokalnie zwykle brak, więc serwis nic nie robi.
/// </summary>
public class StravaWebhookRegistrationService : BackgroundService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StravaWebhookRegistrationService> _logger;

    public StravaWebhookRegistrationService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<StravaWebhookRegistrationService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var callbackUrl = _configuration["Strava:WebhookCallbackUrl"];
        if (string.IsNullOrWhiteSpace(callbackUrl))
        {
            _logger.LogInformation("[StravaWebhook] Brak Strava:WebhookCallbackUrl w konfiguracji - webhook wyłączony");
            return;
        }

        // Poczekaj, aż serwer zacznie przyjmować żądania - Strava zaraz zweryfikuje endpoint (GET challenge)
        try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            var clientId = _configuration["Strava:ClientId"];
            var clientSecret = _configuration["Strava:ClientSecret"];
            var client = _httpClientFactory.CreateClient();

            // Czy subskrypcja już istnieje?
            var listUrl = $"https://www.strava.com/api/v3/push_subscriptions?client_id={clientId}&client_secret={clientSecret}";
            var listResp = await client.GetAsync(listUrl, stoppingToken);
            if (listResp.IsSuccessStatusCode)
            {
                var subs = JsonDocument.Parse(await listResp.Content.ReadAsStringAsync(stoppingToken)).RootElement;
                foreach (var sub in subs.EnumerateArray())
                {
                    if (sub.TryGetProperty("callback_url", out var cb) && cb.GetString() == callbackUrl)
                    {
                        _logger.LogInformation("[StravaWebhook] Subskrypcja już istnieje (id={Id})",
                            sub.TryGetProperty("id", out var id) ? id.GetInt64() : 0);
                        return;
                    }
                }
            }

            // Utwórz subskrypcję - w trakcie tego POST-a Strava wywoła GET challenge na naszym endpoincie
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id", clientId ?? ""),
                new KeyValuePair<string, string>("client_secret", clientSecret ?? ""),
                new KeyValuePair<string, string>("callback_url", callbackUrl),
                new KeyValuePair<string, string>("verify_token", StravaWebhook.ComputeVerifyToken(_configuration))
            });

            var createResp = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", form, stoppingToken);
            var body = await createResp.Content.ReadAsStringAsync(stoppingToken);

            if (createResp.IsSuccessStatusCode)
                _logger.LogInformation("[StravaWebhook] Zarejestrowano subskrypcję webhooka: {Body}", body);
            else
                _logger.LogWarning("[StravaWebhook] Rejestracja subskrypcji nieudana ({Code}): {Body}", createResp.StatusCode, body);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[StravaWebhook] Błąd rejestracji webhooka");
        }
    }
}
