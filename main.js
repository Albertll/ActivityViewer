// Auth init
fetch('/auth/me').then(r => { if (r.ok) return r.json(); throw 'not logged in'; })
    .then(u => {
        const ui = document.getElementById('userInfo');
        const lb = document.getElementById('logoutBtn');
        if (ui) ui.textContent = '👤 ' + u.name;
        if (lb) lb.style.display = '';
    }).catch(() => {});

// Inicjalizacja mapy
const map = L.map('map').setView([52.2297, 21.0122], 10); // Warszawa jako domyślna lokalizacja

L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
    attribution: 'Map data © <a href="https://openstreetmap.org">OpenStreetMap</a> contributors'
}).addTo(map);

let currentGpxLayer = null;

// Definicja zielonego markera Leaflet
const greenIcon = new L.Icon({
    iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-2x-green.png',
    shadowUrl: 'https://unpkg.com/leaflet-gpx@1.7.0/pin-shadow.png',
    iconSize: [25, 41],
    iconAnchor: [12, 41],
    popupAnchor: [1, -34],
    shadowSize: [41, 41]
});
const endIcon = new L.Icon({
    iconUrl: 'https://raw.githubusercontent.com/pointhi/leaflet-color-markers/master/img/marker-icon-2x-red.png',
    shadowUrl: 'https://unpkg.com/leaflet-gpx@1.7.0/pin-shadow.png',
    iconSize: [25, 41],
    iconAnchor: [12, 41],
    popupAnchor: [1, -34],
    shadowSize: [41, 41]
});




const gpxList = document.getElementById('gpxList');
const showAllBtn = document.getElementById('showAllBtn');
// Funkcja do ładowania i wyświetlania wielu tras GPX

// Funkcja do sekwencyjnego ładowania wielu tras GPX (batch)


// Funkcja pomocnicza do pobierania i parsowania pliku GPX
async function fetchAndParseGpx(filename) {
    const response = await fetch(`/api/gpx/${filename}`);
    if (!response.ok) throw new Error('Błąd pobierania pliku');
    const text = await response.text();
    const parser = new DOMParser();
    const xml = parser.parseFromString(text, 'application/xml');
    const trkpts = Array.from(xml.getElementsByTagName('trkpt'));
    if (trkpts.length === 0) throw new Error('Brak punktów trasy');
    return trkpts;
}

// Funkcja do ładowania i wyświetlania wielu tras GPX z filtrem typu
async function showAllTracks(filter = 'all') {
    const res = await fetch('/api/gpx/list');
    const files = await res.json();
    if (currentGpxLayer) map.removeLayer(currentGpxLayer);
    let group = L.featureGroup();
    let loadedCount = 0;
    let errorCount = 0;
    const errorFiles = [];
    function getColor(i) {
        const palette = [
            '#e6194b', '#3cb44b', '#ffe119', '#4363d8', '#f58231', '#911eb4', '#46f0f0', '#f032e6',
            '#bcf60c', '#fabebe', '#008080', '#e6beff', '#9a6324', '#fffac8', '#800000', '#aaffc3',
            '#808000', '#ffd8b1', '#000075', '#808080'
        ];
        return palette[i % palette.length];
    }
    let i = 0;
    for (const f of files) {
        try {
            const trkpts = await fetchAndParseGpx(f);
            const stats = getGpxStats(trkpts);
            // Filtrowanie wg typu
            if (filter === 'bike' && stats.avgSpeed * 3.6 <= 15) continue;
            if (filter === 'run' && stats.avgSpeed * 3.6 > 15) continue;
            const latlngs = trkpts.map(pt => [
                parseFloat(pt.getAttribute('lat')),
                parseFloat(pt.getAttribute('lon'))
            ]);
            const polyline = L.polyline(latlngs, {
                color: getColor(i),
                opacity: 0.8,
                weight: 4,
                className: 'gpx-batch-polyline'
            });
            polyline.on('click', function() {
                showStats(stats, f);
            });
            group.addLayer(polyline);
            loadedCount++;
            i++;
        } catch (e) {
            errorCount++;
            errorFiles.push(f);
        }
    }
    group.addTo(map);
    const bounds = group.getBounds();
    if (bounds.isValid()) {
        map.fitBounds(bounds);
    }
    currentGpxLayer = group;
    if (errorFiles.length > 0) {
        alert(`Załadowano tras: ${loadedCount}\nBłędnych plików: ${errorCount}\nBłędne pliki:\n` + errorFiles.join('\n'));
    }
}

if (showAllBtn) {
    showAllBtn.addEventListener('click', () => showAllTracks('all'));
}

const filterAllBtn = document.getElementById('filterAllBtn');
const filterBikeBtn = document.getElementById('filterBikeBtn');
const filterRunBtn = document.getElementById('filterRunBtn');
if (filterAllBtn) filterAllBtn.addEventListener('click', () => showAllTracks('all'));
if (filterBikeBtn) filterBikeBtn.addEventListener('click', () => showAllTracks('bike'));
if (filterRunBtn) filterRunBtn.addEventListener('click', () => showAllTracks('run'));


fetch('/api/gpx/list')
    .then(res => res.json())
    .then(files => {
        gpxList.innerHTML = '';
        files.forEach(f => {
            const li = document.createElement('li');
            li.textContent = f;
            li.style.cursor = 'pointer';
            li.style.padding = '0.5em 1em';
            li.style.borderBottom = '1px solid #eee';
            li.tabIndex = 0;
            li.addEventListener('click', () => selectGpxFile(f, li));
            li.addEventListener('keydown', (e) => { if (e.key === 'Enter' || e.key === ' ') selectGpxFile(f, li); });
            gpxList.appendChild(li);
        });
    })
    .catch(() => {
        gpxList.innerHTML = '<li style="padding:0.5em 1em; color:red;">Brak połączenia z serwerem</li>';
    });

let selectedGpxFile = null;
let selectedLi = null;
function selectGpxFile(filename, li) {
    selectedGpxFile = filename;
    if (selectedLi) selectedLi.style.background = '';
    li.style.background = '#d0e6fa';
    selectedLi = li;
    loadAndShowGpx(filename);
}

async function loadAndShowGpx(filename) {
    if (!filename) return;
    clearCurrentGpxLayer();
    try {
        const trkpts = await fetchAndParseGpx(filename);
        const latlngs = trkpts.map(pt => [
            parseFloat(pt.getAttribute('lat')),
            parseFloat(pt.getAttribute('lon'))
        ]);
        const polyline = L.polyline(latlngs, {
            color: '#0074D9',
            opacity: 0.9,
            weight: 5
        });
        const startMarker = L.marker(latlngs[0], {icon: greenIcon});
        const endMarker = L.marker(latlngs[latlngs.length-1], {icon: endIcon});
        currentGpxLayer = L.featureGroup([polyline, startMarker, endMarker]);
        currentGpxLayer.addTo(map);
        map.fitBounds(polyline.getBounds());
        const stats = getGpxStats(trkpts);
        showStats(stats, filename);
    } catch (e) {
        showStats(null, filename, e.message);
    }

// Pomocnicza funkcja do czyszczenia warstwy GPX
function clearCurrentGpxLayer() {
    if (currentGpxLayer) {
        if (typeof currentGpxLayer.remove === 'function') {
            currentGpxLayer.remove();
        } else if (currentGpxLayer.eachLayer) {
            currentGpxLayer.eachLayer(l => map.removeLayer(l));
        } else {
            map.removeLayer(currentGpxLayer);
        }
        currentGpxLayer = null;
    }
}
}



// Funkcja do liczenia statystyk z punktów GPX
function getGpxStats(trkpts) {
    if (!trkpts || trkpts.length === 0) return null;
    let dist = 0;
    let maxSpeed = 0;
    let sumSpeed = 0;
    let speedCount = 0;
    let startTime = null;
    let endTime = null;
    let prev = null;
    for (let i = 0; i < trkpts.length; i++) {
        const pt = trkpts[i];
        const lat = parseFloat(pt.getAttribute('lat'));
        const lon = parseFloat(pt.getAttribute('lon'));
        const ele = pt.getElementsByTagName('ele')[0];
        const timeEl = pt.getElementsByTagName('time')[0];
        const time = timeEl ? new Date(timeEl.textContent) : null;
        if (i === 0) startTime = time;
        if (i === trkpts.length-1) endTime = time;
        if (prev && time && prev.time) {
            const d = getDistanceFromLatLonInM(lat, lon, prev.lat, prev.lon);
            dist += d;
            const dt = (time - prev.time) / 1000; // sekundy
            if (dt > 0 && d > 0) {
                const speed = d / dt; // m/s
                if (speed < 20) { // odrzucamy ewidentne błędy (np. teleporty)
                    sumSpeed += speed;
                    speedCount++;
                    if (speed > maxSpeed) maxSpeed = speed;
                }
            }
        }
        prev = {lat, lon, time};
    }
    const duration = (endTime && startTime) ? (endTime - startTime)/1000 : 0;
    return {
        start: startTime,
        end: endTime,
        duration,
        dist,
        avgSpeed: speedCount ? sumSpeed/speedCount : 0,
        maxSpeed
    };
}

// Funkcja do wyświetlania statystyk pod mapą
function showStats(stats, filename, error) {
    const info = document.getElementById('gpxInfo');
    if (error) {
        info.innerHTML = `<b>${filename}</b><br><span style='color:red'>Błąd: ${error}</span>`;
        return;
    }
    if (!stats) {
        info.innerHTML = `<b>${filename}</b><br><span style='color:red'>Brak danych</span>`;
        return;
    }
    function fmtDate(d) {
        if (!d) return '-';
        return d.toLocaleString();
    }
    function fmtTime(s) {
        if (!s) return '-';
        const h = Math.floor(s/3600);
        const m = Math.floor((s%3600)/60);
        const sec = Math.floor(s%60);
        return `${h}h ${m}m ${sec}s`;
    }
    function fmtDist(m) {
        return (m/1000).toFixed(2) + ' km';
    }
    function fmtSpeed(s) {
        return (s*3.6).toFixed(2) + ' km/h';
    }
    info.innerHTML = `<b>${filename}</b><br>
        Start: ${fmtDate(stats.start)}<br>
        Koniec: ${fmtDate(stats.end)}<br>
        Czas trwania: ${fmtTime(stats.duration)}<br>
        Długość: ${fmtDist(stats.dist)}<br>
        Śr. prędkość: ${fmtSpeed(stats.avgSpeed)}<br>
        Maks. prędkość: ${fmtSpeed(stats.maxSpeed)}<br>`;
}

// Funkcja do obliczania odległości Haversine
function getDistanceFromLatLonInM(lat1,lon1,lat2,lon2) {
    var R = 6371000; // m
    var dLat = (lat2-lat1)*Math.PI/180;
    var dLon = (lon2-lon1)*Math.PI/180;
    var a = 
      Math.sin(dLat/2) * Math.sin(dLat/2) +
      Math.cos(lat1*Math.PI/180) * Math.cos(lat2*Math.PI/180) * 
      Math.sin(dLon/2) * Math.sin(dLon/2)
      ;
    var c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1-a)); 
    var d = R * c;
    return d;
}
