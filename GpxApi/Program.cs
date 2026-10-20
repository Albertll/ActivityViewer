using GpxApi.Data;
using GpxApi.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();

// --- Data Protection (klucze szyfrowania cookie przetrwają restart aplikacji) ---
// Bez ProtectKeysWithDpapi: na hostingu (IIS bez załadowanego profilu użytkownika) DPAPI rzuca
// CryptographicException przy tworzeniu nowego klucza, więc po wygaśnięciu klucza (90 dni) logowanie
// kończyło się błędem 500. Klucze leżą w dp-keys/ poza zasięgiem serwowanych statyk.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "..", "dp-keys")))
    .SetApplicationName("GpxApp");

// --- Baza danych ---
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// --- Szyfrowanie ---
builder.Services.AddSingleton<EncryptionService>();

// --- Uwierzytelnianie cookie (sesja po logowaniu przez Stravę) ---
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/auth/login";
        options.LogoutPath = "/auth/logout";
        options.Cookie.Name = "GpxApp.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            // Dla API i auth/me zwracaj 401 zamiast przekierowania
            if (context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/auth"))
            {
                context.Response.StatusCode = 401;
                return Task.CompletedTask;
            }
            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });

// Dodaj serwis synchronizacji aktywności Strava
builder.Services.AddSingleton<StravaActivitySyncService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<StravaActivitySyncService>());

// Automatyczna rejestracja webhooka Stravy (aktywna tylko przy skonfigurowanym Strava:WebhookCallbackUrl)
builder.Services.AddHostedService<StravaWebhookRegistrationService>();

var app = builder.Build();

// Automatycznie utwórz/zaktualizuj bazę danych
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();
}

// Frontend: statyki z katalogu nadrzędnego (lokalnie korzeń repo, na serwerze korzeń FTP), ale WYŁĄCZNIE
// pliki leżące bezpośrednio w nim i tylko o rozszerzeniach frontendu. Obok leżą wwwroot/ z appsettings.json
// (sekrety), dp-keys/ i inne katalogi - nie wolno ich wystawić.
var frontendContentTypes = new FileExtensionContentTypeProvider();
frontendContentTypes.Mappings.Clear();
frontendContentTypes.Mappings[".html"] = "text/html";
frontendContentTypes.Mappings[".js"] = "text/javascript";
frontendContentTypes.Mappings[".css"] = "text/css";
frontendContentTypes.Mappings[".ico"] = "image/x-icon";
frontendContentTypes.Mappings[".png"] = "image/png";
frontendContentTypes.Mappings[".svg"] = "image/svg+xml";

app.UseDefaultFiles();
app.UseWhen(
    ctx => ctx.Request.Path.HasValue && ctx.Request.Path.Value!.LastIndexOfAny(['/', '\\']) == 0,
    branch => branch.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(Path.Combine(Directory.GetCurrentDirectory(), "..")),
        ContentTypeProvider = frontendContentTypes,
        ServeUnknownFileTypes = false
    }));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// Licznik czasu działania aplikacji (m.in. do podglądu usypiania na serwerze)
var startedAtUtc = DateTime.UtcNow;
app.MapGet("/api/uptime", () => Results.Ok(new
{
    startedAtUtc,
    uptimeSeconds = (long)(DateTime.UtcNow - startedAtUtc).TotalSeconds
}));

app.Run();
