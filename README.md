# ActivityViewer

Prywatna przeglądarka aktywności sportowych z integracją Strava: backend ASP.NET Core (.NET 10)
plus lekki, statyczny frontend HTML/JS. Aktywności są pobierane ze Stravy i przechowywane
**zaszyfrowane** w SQL Server (streamy GPS, pełne JSON-y, wygenerowane GPX), a frontend pokazuje
je na mapie Leaflet, w tabeli tras i w widoku analizy treningów interwałowych.

## Funkcje

- Logowanie przez Stravę (OAuth z parametrem `state`) — sesja cookie ważna 30 dni, przeżywa restart aplikacji
- Synchronizacja aktywności: automatyczna po zalogowaniu, ręczna przyciskiem „🔄 Odśwież ze Stravy"
  (pokazuje liczbę nowych) oraz **webhook Stravy** — nowa aktywność wpada sama, bez klikania;
  token Stravy odświeża się sam (refresh token), bez ponownego logowania
- Dane wrażliwe (streamy GPS, JSON-y aktywności, GPX) szyfrowane w bazie — AES-256-CBC
  z kluczem wyprowadzanym per użytkownik
- GPX generowany po stronie serwera ze streamów Stravy (pozycja, czas, wysokość, tętno)
- Mapa tras (Leaflet) z filtrowaniem wg typu aktywności
- Tabela tras ze statystykami i sortowaniem; modal z mapą, wykresem prędkości (Chart.js),
  zaznaczaniem fragmentu trasy i eksportem GPX
- Analiza treningu interwałowego: generator etapów (rozgrzewka/bieg/marsz/schłodzenie),
  statystyki per etap (czas ruchu, dystans, tempo, tętno, zmiana wysokości), zapis definicji
  na serwerze, mapa z podziałem na etapy, wykresy prędkości i **wykres wysokości**
- Wskaźnik czasu działania aplikacji (⏱ w pasku nawigacji, endpoint `/api/uptime`)

## Struktura projektu

- `GpxApi/` — backend ASP.NET Core Web API: kontrolery (`api/activities`, `api/gpx`,
  `api/intervals`, `auth/`), serwis synchronizacji ze Stravą (`StravaActivitySyncService`),
  szyfrowanie (`EncryptionService`), modele i migracje EF Core
- `index.html` + `main.js` — mapa tras
- `trasy.html` — tabela tras z podglądem szczegółów
- `strava.html` — lista aktywności Strava (pobieranie streamów, generowanie GPX, wykres prędkości i wysokości)
- `intervals.html` + `intervals.js` — analiza interwałów
- `common.js` — wspólne funkcje frontendu (geometria, statystyki GPX, licznik uptime, sync)
- `GpxApi.Tests/` — testy jednostkowe (xUnit)

Frontend jest serwowany przez backend z katalogu nadrzędnego względem working directory
aplikacji — nie ma osobnego serwera frontendu, a otwieranie plików `.html` przez `file://`
nie zadziała.

## Uruchomienie lokalne

Wymagania: **.NET 10 SDK**, **SQL Server 2012+** (np. lokalna instancja), konto deweloperskie Strava.

1. **Aplikacja Strava** — utwórz aplikację na <https://www.strava.com/settings/api>
   i ustaw „Authorization Callback Domain" na `localhost`.
2. **Konfiguracja** — skopiuj `GpxApi/appsettings.Example.json` do `GpxApi/appsettings.json`
   i uzupełnij:
   - `Strava:ClientId` / `Strava:ClientSecret` — z panelu aplikacji Strava,
   - `ConnectionStrings:DefaultConnection` — Twój SQL Server,
   - `Encryption:MasterKey` — minimum 32 bajty w Base64; wygenerujesz przez
     `EncryptionService.GenerateMasterKey()`.

   `appsettings.json` jest wpisany do `.gitignore` — sekrety nie trafiają do repozytorium.
3. **Start** — z katalogu `GpxApi/` (working directory ma znaczenie dla serwowania frontendu):

   ```bash
   cd GpxApi
   dotnet run
   ```

   Aplikacja wstaje na <http://localhost:5000> (API + frontend). Baza danych tworzy się
   i aktualizuje sama przy starcie (`db.Database.Migrate()`), więc migracji nie trzeba
   uruchamiać ręcznie.
4. Wejdź na <http://localhost:5000/strava.html> i zaloguj się przez Stravę — pierwsza
   synchronizacja pobierze całą historię aktywności (metadane), a streamy i GPX dociągają się
   w tle (pętla co 15 minut) albo na żądanie.

### Testy

```bash
dotnet test
```

### Webhook Stravy (opcjonalny)

Żeby nowe aktywności wpadały same (push zamiast ręcznego odświeżania), ustaw w `appsettings.json`
publiczny adres endpointu webhooka:

```json
"Strava": { "WebhookCallbackUrl": "https://twoja-domena/api/strava/webhook" }
```

Aplikacja przy starcie sama zarejestruje subskrypcję w API Stravy (wynik w logach
`[StravaWebhook]`). Adres musi być publicznie osiągalny — Strava weryfikuje go challenge'em.

## Wdrożenie na serwer

- Publikuj **poza katalog projektu**, np. `dotnet publish -c Release -o <repo>\wysylka\wwwroot`,
  a pliki frontendu (HTML, `main.js`, `common.js`, `leaflet-gpx.js`) skopiuj do katalogu
  nadrzędnego paczki. Układ na serwerze: katalog główny = frontend, `wwwroot/` = binarki
  (working directory aplikacji).
- Na serwerze zostaw własny `appsettings.json` (inny `MasterKey` = brak możliwości odszyfrowania
  istniejących danych!) oraz katalog `dp-keys/` (klucze Data Protection — ich utrata wylogowuje
  wszystkich użytkowników).
- Przy hostingu IIS zatrzymaj aplikację przed nadpisaniem plików — działający proces blokuje
  `GpxApi.dll`.

## Katalog `dp-keys/`

Klucze ASP.NET Core Data Protection (podpisywanie ciasteczek sesji) trzymane na dysku, żeby
logowania przeżywały restart aplikacji. Katalog jest gitignorowany — nie commitować.

## Autor

Projekt hobbystyczny do własnych zastosowań sportowo-analitycznych.
