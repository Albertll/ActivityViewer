# GPX Map & Strava Web App

Aplikacja webowa do przeglądania, filtrowania i generowania tras GPX na mapie z integracją Strava.

## Funkcje

- Wyświetlanie tras GPX na mapie Leaflet.js
- Filtrowanie tras wg typu (rowerowe, biegowe/spacerowe, wszystkie)
- Szczegółowe statystyki trasy (dystans, czas, prędkość, itp.)
- Pobieranie i generowanie plików GPX z aktywności Strava
- Przeglądanie listy tras i aktywności
- Prosty backend ASP.NET Core Web API (serwowanie plików, generowanie GPX, integracja Strava)

## Struktura projektu

- `index.html` – główna mapa z filtrowaniem tras
- `trasy.html` – tabela tras GPX z możliwością podglądu
- `strava.html` – integracja z kontem Strava, generowanie GPX
- `main.js` – logika mapy, statystyki, filtry
- `gpx/` – katalog z plikami GPX
- `GpxApi/` – backend ASP.NET Core Web API
- `old/` – stary backend Node.js (do archiwum)

## Jak uruchomić

1. **Backend**
   - Otwórz folder `GpxApi` w Visual Studio lub uruchom:
     ```
     dotnet run --project GpxApi
     ```
   - Backend nasłuchuje na porcie 5000.

2. **Frontend**
   - Otwórz `index.html` w przeglądarce (np. przez serwer VS Code Live Server lub bezpośrednio, jeśli backend serwuje pliki statyczne).

3. **Strava**
   - Zaloguj się przez Strava na stronie `strava.html` i wygeneruj plik GPX z wybranej aktywności.

## Filtrowanie tras

- Przycisk "Wszystkie" – pokazuje wszystkie trasy
- Przycisk "Rowerowe" – tylko trasy o średniej prędkości > 15 km/h
- Przycisk "Biegowe/Spacerowe" – tylko trasy o średniej prędkości ≤ 15 km/h

## Wymagania

- .NET 7+ (dla backendu)
- Przeglądarka obsługująca JS/ES6

## Rozwój

- Kod JS w `main.js` (logika mapy, statystyki, filtry)
- Backend w `GpxApi/` (kontrolery: GpxController, StravaController)
- Pliki GPX w katalogu `gpx/`

## Pomysły na rozwój

- Wykresy przewyższeń i prędkości
- Import własnych plików GPX
- Eksport wszystkich tras jako ZIP
- Responsywność na mobile
- Zaawansowane filtry (data, dystans, itp.)

## Autor

Projekt demo do własnych zastosowań sportowych i analitycznych.
