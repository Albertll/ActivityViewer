// ============================================================
// common.js - wspólne funkcje frontendu, ładowane na każdej stronie
// (przed skryptem strony; funkcje globalne, bez modułów)
// ============================================================

// --- Geometria / statystyki GPX ---

// Haversine - odległość w metrach między dwoma punktami GPS
function haversineDistance(lat1, lon1, lat2, lon2) {
    const R = 6371000;
    const toRad = x => x * Math.PI / 180;
    const dLat = toRad(lat2 - lat1);
    const dLon = toRad(lon2 - lon1);
    const a = Math.sin(dLat / 2) ** 2 + Math.cos(toRad(lat1)) * Math.cos(toRad(lat2)) * Math.sin(dLon / 2) ** 2;
    return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
}

// Alias historyczny (main.js / trasy.html)
function getDistanceFromLatLonInM(lat1, lon1, lat2, lon2) {
    return haversineDistance(lat1, lon1, lat2, lon2);
}

// Statystyki z punktów <trkpt> sparsowanego GPX: dystans, czas, prędkości
function getGpxStats(trkpts) {
    if (!trkpts || trkpts.length === 0) return null;
    let dist = 0, maxSpeed = 0, sumSpeed = 0, speedCount = 0;
    let startTime = null, endTime = null, prev = null;
    for (let i = 0; i < trkpts.length; i++) {
        const pt = trkpts[i];
        const lat = parseFloat(pt.getAttribute('lat'));
        const lon = parseFloat(pt.getAttribute('lon'));
        const timeEl = pt.getElementsByTagName('time')[0];
        const time = timeEl ? new Date(timeEl.textContent) : null;
        if (i === 0) startTime = time;
        if (i === trkpts.length - 1) endTime = time;
        if (prev && time && prev.time) {
            const d = haversineDistance(lat, lon, prev.lat, prev.lon);
            dist += d;
            const dt = (time - prev.time) / 1000;
            if (dt > 0 && d > 0) {
                const speed = d / dt; // m/s
                if (speed < 20) { // odrzucamy ewidentne błędy GPS (teleporty)
                    sumSpeed += speed;
                    speedCount++;
                    if (speed > maxSpeed) maxSpeed = speed;
                }
            }
        }
        prev = { lat, lon, time };
    }
    const duration = (endTime && startTime) ? (endTime - startTime) / 1000 : 0;
    return {
        start: startTime,
        end: endTime,
        duration,
        dist,
        avgSpeed: speedCount ? sumSpeed / speedCount : 0,
        maxSpeed
    };
}

// --- Pasek nawigacji: zalogowany użytkownik ---

// Prosta wersja dla stron bez własnej obsługi logowania (index, trasy):
// pokazuje nazwę użytkownika i link Wyloguj, gdy sesja jest aktywna.
function initNavUser() {
    fetch('/auth/me').then(r => { if (r.ok) return r.json(); throw 'not logged in'; })
        .then(u => {
            const ui = document.getElementById('userInfo');
            const lb = document.getElementById('logoutBtn');
            if (ui) ui.textContent = '👤 ' + u.name;
            if (lb) lb.style.display = '';
        }).catch(() => {});
}

// --- Licznik czasu działania backendu (GET /api/uptime) ---

function fmtUptime(s) {
    const d = Math.floor(s / 86400);
    const h = Math.floor((s % 86400) / 3600);
    const m = Math.floor((s % 3600) / 60);
    if (d > 0) return `${d} d ${h} h`;
    if (h > 0) return `${h} h ${m} min`;
    return `${m} min`;
}

async function loadUptime() {
    const el = document.getElementById('uptimeInfo');
    if (!el) return;
    try {
        const res = await fetch('/api/uptime');
        if (!res.ok) return;
        const data = await res.json();
        el.textContent = `⏱ ${fmtUptime(data.uptimeSeconds)}`;
        el.title = 'Aplikacja działa od: ' + new Date(data.startedAtUtc).toLocaleString('pl-PL');
    } catch (e) { /* backend niedostępny - pomiń */ }
}

// Uruchamiany automatycznie na każdej stronie ze spanem #uptimeInfo
document.addEventListener('DOMContentLoaded', loadUptime);

// --- Ręczne dociągnięcie nowych aktywności ze Stravy ---

// Sync działa w tle na serwerze; po chwili odświeża listę przekazaną w reloadList
async function syncStrava(btn, reloadList) {
    btn.disabled = true;
    btn.textContent = 'Synchronizuję...';
    try {
        const res = await fetch('/api/activities/sync', { method: 'POST' });
        if (res.ok) {
            await new Promise(r => setTimeout(r, 3000));
            if (reloadList) await reloadList();
        } else {
            const err = await res.json().catch(() => ({}));
            alert('Błąd synchronizacji: ' + (err.error || res.status));
        }
    } catch (e) {
        alert('Błąd połączenia z serwerem');
    }
    btn.disabled = false;
    btn.textContent = '🔄 Odśwież ze Stravy';
}
