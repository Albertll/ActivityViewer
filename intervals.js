// intervals.js - logika widoku analizy interwałów (wydzielona z intervals.html)
    // --- AUTH & STATE ---
    let currentUser = null;
    let currentActivity = null;
    let currentStreams = null;
    let allActivities = [];
    let map = null;
    let chart = null;
    let chartGps = null;
    let chartElev = null;

    // Kolory etapów
    const stageColors = {
        'warmup': '#ffc107',
        'sprint': '#9b59b6',
        'run': '#28a745',
        'jog': '#8bc34a',
        'walk': '#dc3545',
        'cooldown': '#17a2b8'
    };
    const stageNames = {
        'warmup': 'Rozgrzewka',
        'sprint': 'Sprint',
        'run': 'Bieg',
        'jog': 'Trucht',
        'walk': 'Marsz',
        'cooldown': 'Schłodzenie'
    };
    // Nazwy do wierszy podsumowań (liczba mnoga)
    const stageNamesPlural = {
        'sprint': 'SPRINTY',
        'run': 'BIEGI',
        'jog': 'TRUCHTY',
        'walk': 'MARSZE'
    };

    // --- INICJALIZACJA ---
    document.getElementById('loadActivityBtn').onclick = loadSelectedActivity;
    initAuth();

    async function initAuth() {
        try {
            const res = await fetch('/auth/me');
            if (res.ok) {
                currentUser = await res.json();
                document.getElementById('stravaLoginBtn').style.display = 'none';
                document.getElementById('syncBtn').style.display = '';
                document.getElementById('userInfo').textContent = '👤 ' + currentUser.name;
                document.getElementById('logoutBtn').style.display = '';
                loadActivitiesList();
            } else {
                document.getElementById('stravaLoginBtn').style.display = '';
                document.getElementById('activitySelect').innerHTML = '<option value="">-- Zaloguj się aby zobaczyć aktywności --</option>';
            }
        } catch (e) {
            console.error('Błąd sprawdzania auth:', e);
            document.getElementById('stravaLoginBtn').style.display = '';
        }
        
        // Dodaj domyślne etapy
        addStage('warmup', 5);
    }

    async function loadActivitiesList() {
        const select = document.getElementById('activitySelect');
        select.innerHTML = '<option value="">Ładowanie...</option>';
        
        try {
            const res = await fetch('/api/activities/list');
            if (res.status === 401) {
                select.innerHTML = '<option value="">-- Zaloguj się aby zobaczyć aktywności --</option>';
                document.getElementById('stravaLoginBtn').style.display = '';
                return;
            }
            allActivities = (await res.json()).filter(act => act.has_stream);

            // Uzupełnij filter o typy które są w danych
            const typeFilter = document.getElementById('activityTypeFilter');
            const existingTypes = new Set([...typeFilter.options].map(o => o.value).filter(Boolean));
            const dataTypes = [...new Set(allActivities.map(a => a.type))];
            dataTypes.forEach(t => {
                if (!existingTypes.has(t)) {
                    const opt = document.createElement('option');
                    opt.value = t;
                    opt.textContent = t;
                    typeFilter.appendChild(opt);
                }
            });

            filterActivities();
        } catch (e) {
            select.innerHTML = '<option value="">Błąd ładowania aktywności</option>';
            console.error('Błąd:', e);
        }
    }

    function filterActivities() {
        const select = document.getElementById('activitySelect');
        const typeFilter = document.getElementById('activityTypeFilter').value;

        const filtered = typeFilter
            ? allActivities.filter(act => act.type === typeFilter)
            : allActivities;

        select.innerHTML = '<option value="">-- Wybierz aktywność --</option>';
        filtered.forEach(act => {
            const date = act.start_date ? new Date(act.start_date).toLocaleDateString('pl-PL') : '?';
            const dist = (act.distance / 1000).toFixed(2);
            const option = document.createElement('option');
            option.value = act.id;
            option.textContent = `${date} - ${act.name} (${dist} km) [${act.type}]`;
            option.dataset.activity = JSON.stringify(act);
            select.appendChild(option);
        });
    }

    async function loadSelectedActivity() {
        const select = document.getElementById('activitySelect');
        const selectedOption = select.options[select.selectedIndex];
        
        if (!selectedOption.value) {
            alert('Wybierz aktywność z listy');
            return;
        }
        
        currentActivity = JSON.parse(selectedOption.dataset.activity);
        showActivityInfo(currentActivity);
        
        // Pobierz stream z serwera (odszyfrowany po stronie serwera)
        try {
            const streamsRes = await fetch(`/api/activities/${currentActivity.id}/stream`);
            if (streamsRes.status === 401) {
                alert('Sesja wygasła. Zaloguj się ponownie.');
                window.location.href = '/auth/login';
                return;
            }
            if (!streamsRes.ok) {
                alert('Brak danych stream dla tej aktywności');
                return;
            }
            currentStreams = await streamsRes.json();

            // Jeśli brak velocity_smooth, oblicz z distance i time
            if (!currentStreams.velocity_smooth && currentStreams.distance && currentStreams.time) {
                const dist = currentStreams.distance.data;
                const time = currentStreams.time.data;
                const velocity = [0];
                for (let i = 1; i < dist.length; i++) {
                    const dd = dist[i] - dist[i - 1];
                    const dt = time[i] - time[i - 1];
                    velocity.push(dt > 0 ? dd / dt : 0);
                }
                currentStreams.velocity_smooth = { data: velocity };
            }
        } catch (e) {
            alert('Błąd pobierania danych GPS');
            console.error(e);
            return;
        }

        // Automatycznie wczytaj zapisane interwały z serwera
        const savedStages = await loadIntervalsFromServer(currentActivity.id);
        if (savedStages && savedStages.length > 0) {
            applyStagesFromServer(savedStages);
            showSaveStatus('📂 Wczytano zapisane interwały');
            if (currentStreams && currentStreams.time) {
                analyzeIntervals();
            }
        }
    }

    function showActivityInfo(act) {
        const infoDiv = document.getElementById('activityInfo');
        infoDiv.style.display = 'flex';
        infoDiv.innerHTML = `
            <div class="activity-info-item">
                <div class="value">${(act.distance / 1000).toFixed(2)}</div>
                <div class="label">km</div>
            </div>
            <div class="activity-info-item">
                <div class="value">${formatTime(act.moving_time)}</div>
                <div class="label">czas</div>
            </div>
            <div class="activity-info-item">
                <div class="value">${formatPace(act.moving_time, act.distance)}</div>
                <div class="label">tempo</div>
            </div>
            <div class="activity-info-item">
                <div class="value">${act.total_elevation_gain ? act.total_elevation_gain.toFixed(0) : '-'}</div>
                <div class="label">m wznios</div>
            </div>
        `;
    }

    // --- ETAPY TRENINGU ---
    let stageId = 0;

    function addStage(type = 'run', duration = 4, name = '') {
        const container = document.getElementById('stagesContainer');
        const id = ++stageId;
        
        const row = document.createElement('div');
        row.className = 'stage-row';
        row.id = `stage-${id}`;
        row.innerHTML = `
            <span style="font-weight:bold; min-width:30px;">#${container.children.length + 1}</span>
            <select class="stage-type" onchange="updateStageColor(${id})">
                <option value="warmup" ${type === 'warmup' ? 'selected' : ''}>🔥 Rozgrzewka</option>
                <option value="sprint" ${type === 'sprint' ? 'selected' : ''}>⚡ Sprint</option>
                <option value="run" ${type === 'run' ? 'selected' : ''}>🏃 Bieg</option>
                <option value="jog" ${type === 'jog' ? 'selected' : ''}>🐢 Trucht</option>
                <option value="walk" ${type === 'walk' ? 'selected' : ''}>🚶 Marsz</option>
                <option value="cooldown" ${type === 'cooldown' ? 'selected' : ''}>❄️ Schłodzenie</option>
            </select>
            <label>Czas: <input type="number" class="stage-duration" value="${duration}" min="0.5" step="0.5"> min</label>
            <label>Nazwa: <input type="text" class="stage-name" value="${name}" placeholder="opcjonalnie"></label>
            <button class="remove-stage" onclick="removeStage(${id})">✕</button>
        `;
        container.appendChild(row);
        updateStageColor(id);
        renumberStages();
    }

    function removeStage(id) {
        const row = document.getElementById(`stage-${id}`);
        if (row) row.remove();
        renumberStages();
    }

    function renumberStages() {
        const rows = document.querySelectorAll('.stage-row');
        rows.forEach((row, idx) => {
            row.querySelector('span').textContent = `#${idx + 1}`;
        });
    }

    function updateStageColor(id) {
        const row = document.getElementById(`stage-${id}`);
        const type = row.querySelector('.stage-type').value;
        row.style.borderLeft = `4px solid ${stageColors[type]}`;
    }

    // --- SZABLONY ---
    function generateWorkout() {
        document.getElementById('stagesContainer').innerHTML = '';
        stageId = 0;
        
        const reps = parseInt(document.getElementById('templateReps').value) || 5;
        const runTime = parseFloat(document.getElementById('templateRunTime').value) || 4;
        const walkTime = parseFloat(document.getElementById('templateWalkTime').value);
        const warmupTime = parseFloat(document.getElementById('templateWarmup').value);
        const cooldownTime = parseFloat(document.getElementById('templateCooldown').value);
        const fastType = document.getElementById('templateFastType').value;
        const restType = document.getElementById('templateRestType').value;

        // Rozgrzewka
        if (warmupTime > 0) {
            addStage('warmup', warmupTime, 'Rozgrzewka');
        }

        // Interwały
        for (let i = 1; i <= reps; i++) {
            addStage(fastType, runTime, `${stageNames[fastType]} ${i}`);
            if (i < reps && walkTime > 0) {
                addStage(restType, walkTime, `${stageNames[restType]} ${i}`);
            }
        }
        
        // Schłodzenie
        if (cooldownTime > 0) {
            addStage('cooldown', cooldownTime, 'Schłodzenie');
        }
    }
    
    function loadTemplate(name) {
        if (name === 'clear') {
            document.getElementById('stagesContainer').innerHTML = '';
            stageId = 0;
            addStage('warmup', 5);
        }
    }

    // --- ZAPIS / ODCZYT INTERWAŁÓW Z SERWERA ---
    async function saveIntervalsToServer(activityId, stages) {
        try {
            const res = await fetch(`/api/intervals/${activityId}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(stages)
            });
            if (res.ok) {
                console.log('Interwały zapisane na serwerze');
                showSaveStatus('✅ Interwały zapisane');
            } else {
                console.error('Błąd zapisu interwałów');
                showSaveStatus('❌ Błąd zapisu');
            }
        } catch (e) {
            console.error('Błąd zapisu interwałów:', e);
            showSaveStatus('❌ Błąd zapisu');
        }
    }

    async function loadIntervalsFromServer(activityId) {
        try {
            const res = await fetch(`/api/intervals/${activityId}`);
            if (res.ok) {
                const stages = await res.json();
                return stages;
            }
        } catch (e) {
            console.error('Błąd odczytu interwałów:', e);
        }
        return null;
    }

    function showSaveStatus(message) {
        let statusEl = document.getElementById('saveStatus');
        if (!statusEl) {
            statusEl = document.createElement('span');
            statusEl.id = 'saveStatus';
            statusEl.style.cssText = 'margin-left:1em; font-size:0.9em; transition: opacity 0.5s;';
            document.querySelector('.add-stage-btn[onclick*="analyzeIntervals"]').after(statusEl);
        }
        statusEl.textContent = message;
        statusEl.style.opacity = '1';
        setTimeout(() => { statusEl.style.opacity = '0'; }, 3000);
    }

    function applyStagesFromServer(stages) {
        document.getElementById('stagesContainer').innerHTML = '';
        stageId = 0;
        stages.forEach(s => {
            addStage(s.type, s.duration / 60, s.name);
        });
    }

    // --- ANALIZA INTERWAŁÓW ---
    function analyzeIntervals() {
        if (!currentStreams || !currentStreams.time) {
            alert('Najpierw załaduj aktywność');
            return;
        }
        
        const stages = getStagesDefinition();
        if (stages.length === 0) {
            alert('Dodaj przynajmniej jeden etap');
            return;
        }
        
        const results = calculateStageStats(stages);

        // Pokaż panele PRZED rysowaniem - Leaflet i Chart.js źle mierzą rozmiar ukrytych (display:none) kontenerów
        document.getElementById('statsPanel').style.display = 'block';
        document.getElementById('mapPanel').style.display = 'block';
        document.getElementById('chartPanel').style.display = 'block';
        document.getElementById('chartGpsPanel').style.display = 'block';

        displayStats(results);
        displayMap(results);
        displayChart(results);
        displayChartGps(results);
        displayChartElevation(results);

        // Zapisz interwały na serwer
        if (currentActivity && currentActivity.id) {
            saveIntervalsToServer(currentActivity.id, stages);
        }
    }

    function getStagesDefinition() {
        const rows = document.querySelectorAll('.stage-row');
        const stages = [];
        
        rows.forEach(row => {
            stages.push({
                type: row.querySelector('.stage-type').value,
                duration: parseFloat(row.querySelector('.stage-duration').value) * 60, // sekundy
                name: row.querySelector('.stage-name').value || stageNames[row.querySelector('.stage-type').value]
            });
        });
        
        return stages;
    }

    function calculateStageStats(stages) {
        const times = currentStreams.time.data;
        const latlngs = currentStreams.latlng ? currentStreams.latlng.data : [];
        const distances = currentStreams.distance ? currentStreams.distance.data : [];
        const velocities = currentStreams.velocity_smooth ? currentStreams.velocity_smooth.data : [];
        const heartrates = currentStreams.heartrate ? currentStreams.heartrate.data : [];
        const altitudes = currentStreams.altitude ? currentStreams.altitude.data : [];
        
        // Oblicz skumulowany czas ruchu (moving time) - pomijaj pauzy
        const movingThreshold = 0.3; // m/s (~1 km/h) - próg ruchu
        const movingTime = [0];
        for (let i = 1; i < times.length; i++) {
            const dt = times[i] - times[i - 1];
            const isMoving = velocities.length > i && velocities[i] > movingThreshold;
            movingTime.push(movingTime[i - 1] + (isMoving ? dt : 0));
        }
        
        let currentMovingTime = 0;
        const results = [];
        
        stages.forEach((stage, idx) => {
            const startMoving = currentMovingTime;
            const targetEndMoving = currentMovingTime + stage.duration;
            
            // Znajdź indeksy według czasu ruchu
            let startIdx = movingTime.findIndex(t => t >= startMoving);
            let endIdx = movingTime.findIndex(t => t >= targetEndMoving);
            
            if (startIdx === -1) startIdx = 0;
            if (endIdx === -1) endIdx = times.length - 1;
            
            // Czas ruchu w tym etapie
            const actualMovingDuration = movingTime[endIdx] - movingTime[startIdx];
            // Czas zegarowy (elapsed)
            const elapsedStart = times[startIdx];
            const elapsedEnd = times[endIdx];
            const distance = distances.length > 0 ? distances[endIdx] - distances[startIdx] : 0;
            
            // Średnia prędkość
            const stageVelocities = velocities.slice(startIdx, endIdx + 1).filter(v => v > movingThreshold);
            const avgVelocity = stageVelocities.length > 0 
                ? stageVelocities.reduce((a, b) => a + b, 0) / stageVelocities.length 
                : 0;
            
            // Tętno
            const stageHR = heartrates.slice(startIdx, endIdx + 1).filter(h => h > 0);
            const avgHR = stageHR.length > 0 
                ? Math.round(stageHR.reduce((a, b) => a + b, 0) / stageHR.length) 
                : null;
            const minHR = stageHR.length > 0 ? Math.min(...stageHR) : null;
            const maxHR = stageHR.length > 0 ? Math.max(...stageHR) : null;
            
            // Punkty GPS
            const stageLatLngs = latlngs.slice(startIdx, endIdx + 1);
            
            // Dystans GPS (suma Haversine)
            let gpsDistance = 0;
            for (let i = startIdx + 1; i <= endIdx; i++) {
                if (latlngs[i] && latlngs[i - 1]) {
                    gpsDistance += haversineDistance(latlngs[i - 1][0], latlngs[i - 1][1], latlngs[i][0], latlngs[i][1]);
                }
            }

            // Zmiana wysokości (różnica koniec - początek etapu)
            const elevChange = (altitudes.length > endIdx && altitudes[startIdx] != null && altitudes[endIdx] != null)
                ? altitudes[endIdx] - altitudes[startIdx]
                : null;
            
            results.push({
                index: idx + 1,
                type: stage.type,
                name: stage.name,
                startTime: elapsedStart,
                startMovingTime: startMoving,
                plannedDuration: stage.duration,
                actualDuration: actualMovingDuration,
                distance: distance,
                gpsDistance: gpsDistance,
                elevChange: elevChange,
                avgVelocity: avgVelocity,
                avgHR: avgHR,
                minHR: minHR,
                maxHR: maxHR,
                latlngs: stageLatLngs,
                startIdx: startIdx,
                endIdx: endIdx
            });
            
            // Następny etap zaczyna się tam gdzie skończył się ten wg czasu ruchu
            currentMovingTime = movingTime[endIdx];
        });
        
        return results;
    }

    function displayStats(results) {
        const tbody = document.getElementById('statsTableBody');
        tbody.innerHTML = '';
        
        let totalDistance = 0;
        let totalGpsDistance = 0;
        let totalTime = 0;
        let totalHRWeightedSum = 0;
        let totalHRWeightedTime = 0;
        let allMaxHR = [];
        let totalElev = null;

        // Sumy per typ etapu (bieg/sprint/trucht/marsz liczone osobno)
        const byType = {};
        
        results.forEach(r => {
            const row = document.createElement('tr');
            row.className = r.type;
            
            const pace = r.distance > 0 ? formatPace(r.actualDuration, r.distance) : '-';
            const gpsPace = r.gpsDistance > 0 ? formatPace(r.actualDuration, r.gpsDistance) : '-';
            
            row.innerHTML = `
                <td>${r.index}</td>
                <td><span style="color:${stageColors[r.type]}">●</span> ${r.name}</td>
                <td>${formatTime(r.startTime)}</td>
                <td>${formatTime(r.plannedDuration)}</td>
                <td>${formatTime(r.actualDuration)}</td>
                <td>${r.distance.toFixed(0)}</td>
                <td>${r.gpsDistance.toFixed(0)}</td>
                <td>${formatElev(r.elevChange)}</td>
                <td>${pace}</td>
                <td>${gpsPace}</td>
                <td>${r.avgHR || '-'}</td>
                <td>${r.minHR || '-'}</td>
                <td>${r.maxHR || '-'}</td>
            `;
            tbody.appendChild(row);
            
            totalDistance += r.distance;
            totalGpsDistance += r.gpsDistance;
            totalTime += r.actualDuration;
            if (r.elevChange !== null) totalElev = (totalElev ?? 0) + r.elevChange;

            const t = byType[r.type] ??= { distance: 0, gpsDistance: 0, time: 0, hrSum: 0, hrTime: 0, minHR: [], maxHR: [], elev: null };
            t.distance += r.distance;
            t.gpsDistance += r.gpsDistance;
            t.time += r.actualDuration;
            if (r.elevChange !== null) t.elev = (t.elev ?? 0) + r.elevChange;
            if (r.avgHR && r.actualDuration > 0) { t.hrSum += r.avgHR * r.actualDuration; t.hrTime += r.actualDuration; }
            if (r.minHR) t.minHR.push(r.minHR);
            if (r.maxHR) t.maxHR.push(r.maxHR);

            if (r.avgHR && r.actualDuration > 0) {
                totalHRWeightedSum += r.avgHR * r.actualDuration;
                totalHRWeightedTime += r.actualDuration;
            }
            if (r.maxHR) allMaxHR.push(r.maxHR);
        });
        
        // Podsumowanie
        const avgTotalHR = totalHRWeightedTime > 0 ? Math.round(totalHRWeightedSum / totalHRWeightedTime) : '-';
        const maxTotalHR = allMaxHR.length > 0 ? Math.max(...allMaxHR) : '-';
        
        // Wiersz sumy całkowitej
        const totalRow = document.createElement('tr');
        totalRow.className = 'summary-row';
        totalRow.innerHTML = `
            <td colspan="3"><strong>SUMA CAŁKOWITA</strong></td>
            <td>-</td>
            <td>${formatTime(totalTime)}</td>
            <td>${totalDistance.toFixed(0)}</td>
            <td>${totalGpsDistance.toFixed(0)}</td>
            <td>${formatElev(totalElev)}</td>
            <td>${formatPace(totalTime, totalDistance)}</td>
            <td>${formatPace(totalTime, totalGpsDistance)}</td>
            <td>${avgTotalHR}</td>
            <td>-</td>
            <td>${maxTotalHR}</td>
        `;
        tbody.appendChild(totalRow);
        
        // Wiersze sum per typ etapu (kolejność wg intensywności; rozgrzewka/schłodzenie bez sumy)
        ['sprint', 'run', 'jog', 'walk'].forEach(type => {
            const t = byType[type];
            if (!t || t.distance <= 0) return;
            const row = document.createElement('tr');
            row.className = 'summary-row';
            row.style.background = hexToRgba(stageColors[type], 0.22);
            row.innerHTML = `
                <td colspan="3"><strong>SUMA ${stageNamesPlural[type]}</strong></td>
                <td>-</td>
                <td>${formatTime(t.time)}</td>
                <td>${t.distance.toFixed(0)}</td>
                <td>${t.gpsDistance.toFixed(0)}</td>
                <td>${formatElev(t.elev)}</td>
                <td>${formatPace(t.time, t.distance)}</td>
                <td>${formatPace(t.time, t.gpsDistance)}</td>
                <td>${t.hrTime > 0 ? Math.round(t.hrSum / t.hrTime) : '-'}</td>
                <td>${t.minHR.length > 0 ? Math.min(...t.minHR) : '-'}</td>
                <td>${t.maxHR.length > 0 ? Math.max(...t.maxHR) : '-'}</td>
            `;
            tbody.appendChild(row);
        });
    }

    function displayMap(results) {
        const mapDiv = document.getElementById('intervalMap');
        
        if (map) {
            map.remove();
        }
        
        map = L.map('intervalMap').setView([52.2297, 21.0122], 13);
        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            attribution: 'Map data © OpenStreetMap contributors'
        }).addTo(map);
        
        const allLatLngs = [];
        
        results.forEach(r => {
            if (r.latlngs && r.latlngs.length > 0) {
                const polyline = L.polyline(r.latlngs, {
                    color: stageColors[r.type],
                    weight: 5,
                    opacity: 0.8
                }).addTo(map);
                
                polyline.bindPopup(`
                    <strong>${r.name}</strong><br>
                    Dystans: ${r.distance.toFixed(0)} m<br>
                    Czas: ${formatTime(r.actualDuration)}<br>
                    Tempo: ${r.distance > 0 ? formatPace(r.actualDuration, r.distance) : '-'}
                `);
                
                allLatLngs.push(...r.latlngs);
            }
        });
        
        if (allLatLngs.length > 0) {
            map.fitBounds(L.latLngBounds(allLatLngs));
        }

        // Zabezpieczenie: kontener mógł dopiero co stać się widoczny - przelicz rozmiar i dopasuj widok
        setTimeout(() => {
            map.invalidateSize();
            if (allLatLngs.length > 0) {
                map.fitBounds(L.latLngBounds(allLatLngs));
            }
        }, 100);
    }

    function displayChart(results) {
        const ctx = document.getElementById('intervalChart').getContext('2d');
        
        if (chart) {
            chart.destroy();
        }
        
        const times = currentStreams.time.data;
        const velocities = currentStreams.velocity_smooth ? currentStreams.velocity_smooth.data : [];
        
        // Przygotuj dane jako punkty {x, y} z osią liniową
        const dataPoints = [];
        for (let i = 0; i < times.length; i++) {
            const speed = velocities[i] * 3.6;
            dataPoints.push({
                x: times[i] / 60,
                y: speed > 20 ? 20 : (speed < 0 ? 0 : speed)
            });
        }
        
        // Tła etapów (czas zegarowy) - wspólny helper
        const annotations = buildStageAnnotations(results, times);
        
        chart = new Chart(ctx, {
            type: 'scatter',
            data: {
                datasets: [{
                    label: 'Prędkość [km/h]',
                    data: dataPoints,
                    borderColor: '#0074D9',
                    backgroundColor: 'rgba(0, 116, 217, 0.1)',
                    pointRadius: 0,
                    borderWidth: 1.5,
                    fill: true,
                    showLine: true
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: true },
                    annotation: { annotations: annotations }
                },
                scales: {
                    x: {
                        type: 'linear',
                        title: { display: true, text: 'Czas [min]' },
                        ticks: {
                            maxTicksLimit: 20,
                            callback: function(value) { return value.toFixed(0); }
                        }
                    },
                    y: {
                        title: { display: true, text: 'Prędkość [km/h]' },
                        min: 0,
                        max: 20
                    }
                }
            }
        });
    }

    function displayChartGps(results) {
        const ctx = document.getElementById('intervalChartGps').getContext('2d');
        
        if (chartGps) {
            chartGps.destroy();
        }
        
        const times = currentStreams.time.data;
        const latlngs = currentStreams.latlng ? currentStreams.latlng.data : [];
        
        // Oblicz prędkość z GPS (Haversine) - surowe dane
        const rawSpeeds = [0];
        for (let i = 1; i < times.length; i++) {
            const dt = times[i] - times[i - 1];
            let speed = 0;
            if (dt > 0 && latlngs[i] && latlngs[i - 1]) {
                const dist = haversineDistance(latlngs[i - 1][0], latlngs[i - 1][1], latlngs[i][0], latlngs[i][1]);
                speed = (dist / dt) * 3.6;
            }
            rawSpeeds.push(speed);
        }
        
        // Wygładzanie średnią kroczącą (okno = 10 punktów)
        const windowSize = 10;
        const smoothed = [];
        for (let i = 0; i < rawSpeeds.length; i++) {
            const start = Math.max(0, i - Math.floor(windowSize / 2));
            const end = Math.min(rawSpeeds.length, i + Math.ceil(windowSize / 2));
            let sum = 0;
            for (let j = start; j < end; j++) sum += rawSpeeds[j];
            smoothed.push(sum / (end - start));
        }
        
        const dataPoints = smoothed.map((speed, i) => ({
            x: times[i] / 60,
            y: speed > 20 ? 20 : (speed < 0 ? 0 : speed)
        }));
        
        // Tła etapów - wspólny helper
        const annotations = buildStageAnnotations(results, times);
        
        chartGps = new Chart(ctx, {
            type: 'scatter',
            data: {
                datasets: [{
                    label: 'Prędkość GPS [km/h]',
                    data: dataPoints,
                    borderColor: '#e67e22',
                    backgroundColor: 'rgba(230, 126, 34, 0.1)',
                    pointRadius: 0,
                    borderWidth: 1.5,
                    fill: true,
                    showLine: true
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: true },
                    annotation: { annotations: annotations }
                },
                scales: {
                    x: {
                        type: 'linear',
                        title: { display: true, text: 'Czas [min]' },
                        ticks: {
                            maxTicksLimit: 20,
                            callback: function(value) { return value.toFixed(0); }
                        }
                    },
                    y: {
                        title: { display: true, text: 'Prędkość GPS [km/h]' },
                        min: 0,
                        max: 20
                    }
                }
            }
        });
    }

    // Wspólne tła etapów dla wykresów (boxy w osi czasu zegarowego [min])
    function buildStageAnnotations(results, times) {
        const annotations = {};
        results.forEach((r, idx) => {
            annotations[`stage${idx}`] = {
                type: 'box',
                xMin: times[r.startIdx] / 60,
                xMax: times[r.endIdx] / 60,
                backgroundColor: hexToRgba(stageColors[r.type], 0.25),
                borderColor: stageColors[r.type],
                borderWidth: 1,
                label: {
                    display: true,
                    content: r.name,
                    position: 'start',
                    font: { size: 10, weight: 'bold' },
                    color: stageColors[r.type]
                }
            };
        });
        return annotations;
    }

    // Wykres wysokości z tłami etapów (panel chowa się, gdy aktywność nie ma strumienia altitude)
    function displayChartElevation(results) {
        const panel = document.getElementById('chartElevPanel');
        const altitudes = currentStreams.altitude ? currentStreams.altitude.data : [];

        if (chartElev) {
            chartElev.destroy();
            chartElev = null;
        }
        if (!altitudes.length) {
            panel.style.display = 'none';
            return;
        }
        panel.style.display = 'block';

        const ctx = document.getElementById('intervalChartElev').getContext('2d');
        const times = currentStreams.time.data;

        const dataPoints = [];
        for (let i = 0; i < times.length && i < altitudes.length; i++) {
            dataPoints.push({ x: times[i] / 60, y: altitudes[i] });
        }

        chartElev = new Chart(ctx, {
            type: 'scatter',
            data: {
                datasets: [{
                    label: 'Wysokość [m n.p.m.]',
                    data: dataPoints,
                    borderColor: '#8e44ad',
                    backgroundColor: 'rgba(142, 68, 173, 0.1)',
                    pointRadius: 0,
                    borderWidth: 1.5,
                    fill: true,
                    showLine: true
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: true },
                    annotation: { annotations: buildStageAnnotations(results, currentStreams.time.data) }
                },
                scales: {
                    x: {
                        type: 'linear',
                        title: { display: true, text: 'Czas [min]' },
                        ticks: {
                            maxTicksLimit: 20,
                            callback: function(value) { return value.toFixed(0); }
                        }
                    },
                    y: {
                        title: { display: true, text: 'Wysokość [m n.p.m.]' }
                    }
                }
            }
        });
    }

    // --- FUNKCJE POMOCNICZE ---
    function formatTime(seconds) {
        if (!seconds || seconds <= 0) return '-';
        const h = Math.floor(seconds / 3600);
        const m = Math.floor((seconds % 3600) / 60);
        const s = Math.floor(seconds % 60);
        if (h > 0) return `${h}:${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
        return `${m}:${s.toString().padStart(2, '0')}`;
    }

    function formatPace(timeSeconds, distanceMeters) {
        if (!distanceMeters || distanceMeters <= 0) return '-';
        const paceSecondsPerKm = (timeSeconds / distanceMeters) * 1000;
        const paceMin = Math.floor(paceSecondsPerKm / 60);
        const paceSec = Math.floor(paceSecondsPerKm % 60);
        return `${paceMin}:${paceSec.toString().padStart(2, '0')}`;
    }

    function formatElev(meters) {
        if (meters === null || meters === undefined || isNaN(meters)) return '-';
        const rounded = Math.round(meters);
        return `${rounded > 0 ? '+' : ''}${rounded}`;
    }

    function hexToRgba(hex, alpha) {
        const r = parseInt(hex.slice(1, 3), 16);
        const g = parseInt(hex.slice(3, 5), 16);
        const b = parseInt(hex.slice(5, 7), 16);
        return `rgba(${r}, ${g}, ${b}, ${alpha})`;
    }
