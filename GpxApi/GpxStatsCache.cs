using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;

public class GpxStatsCache
{
    private readonly string gpxDir;
    private readonly string cacheFile;
    private List<GpxStats> _stats = new();
    private readonly object _lock = new();

    public GpxStatsCache(string gpxDir)
    {
        this.gpxDir = gpxDir;
        this.cacheFile = Path.Combine(gpxDir, "gpx_stats.json");
        LoadCache();
    }

    public List<GpxStats> GetStats()
    {
        lock (_lock)
        {
            // Zawsze synchronizuj cache z plikami GPX
            SyncCacheWithFiles();
            return _stats.ToList();
        }
    }

    // Synchronizuje cache z aktualną listą plików GPX (dodaje nowe, usuwa nieistniejące)
    private void SyncCacheWithFiles()
    {
        var files = Directory.GetFiles(gpxDir, "*.gpx");
        var fileSet = new HashSet<string>(files.Select(f => Path.GetFileName(f)));
        var statsByName = _stats.ToDictionary(s => s.name ?? "", s => s);

        // Dodaj nowe pliki (najpierw zbierz do listy, potem dodaj, by nie modyfikować kolekcji w foreach)
        var toAdd = new List<GpxStats>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            if (!statsByName.ContainsKey(name))
            {
                try
                {
                    var stat = ParseGpxStats(file);
                    if (stat != null)
                    {
                        toAdd.Add(stat);
                    }
                }
                catch { }
            }
        }
        if (toAdd.Count > 0)
            _stats.AddRange(toAdd);

        // Usuń statystyki dla plików, których już nie ma
        _stats = _stats.Where(s => s.name != null && fileSet.Contains(s.name)).ToList();

        // Zapisz cache na dysk
        try
        {
            File.WriteAllText(cacheFile, JsonSerializer.Serialize(_stats));
        }
        catch { }
    }

    public void RefreshCache()
    {
        lock (_lock)
        {
            var files = Directory.GetFiles(gpxDir, "*.gpx");
            var statsList = new List<GpxStats>();
            foreach (var file in files)
            {
                try
                {
                    var stat = ParseGpxStats(file);
                    if (stat != null) statsList.Add(stat);
                }
                catch { }
            }
            _stats = statsList;
            try
            {
                File.WriteAllText(cacheFile, JsonSerializer.Serialize(_stats));
            }
            catch { }
        }
    }

    private void LoadCache()
    {
        lock (_lock)
        {
            if (File.Exists(cacheFile))
            {
                try
                {
                    var json = File.ReadAllText(cacheFile);
                    _stats = JsonSerializer.Deserialize<List<GpxStats>>(json) ?? new List<GpxStats>();
                }
                catch { _stats = new List<GpxStats>(); }
            }
            else
            {
                RefreshCache();
            }
        }
    }

    private GpxStats? ParseGpxStats(string filePath)
    {
        var doc = XDocument.Load(filePath);
        var ns = doc.Root?.Name.Namespace ?? "";
        var trkpts = doc.Descendants(ns + "trkpt").ToList();
        if (trkpts.Count == 0) return null;
        double dist = 0, maxSpeed = 0, sumSpeed = 0; int speedCount = 0;
        DateTime? start = null, end = null; XElement? prev = null;
        for (int i = 0; i < trkpts.Count; i++)
        {
            var pt = trkpts[i];
            double lat = double.Parse(pt.Attribute("lat")?.Value ?? "0", CultureInfo.InvariantCulture);
            double lon = double.Parse(pt.Attribute("lon")?.Value ?? "0", CultureInfo.InvariantCulture);
            var timeEl = pt.Element(ns + "time");
            DateTime? time = null;
            if (timeEl != null && DateTime.TryParse(timeEl.Value, null, DateTimeStyles.AdjustToUniversal, out var t))
                time = t;
            if (i == 0) start = time;
            if (i == trkpts.Count - 1) end = time;
            if (prev != null && time != null)
            {
                double plat = double.Parse(prev.Attribute("lat")?.Value ?? "0", CultureInfo.InvariantCulture);
                double plon = double.Parse(prev.Attribute("lon")?.Value ?? "0", CultureInfo.InvariantCulture);
                var prevTimeEl = prev.Element(ns + "time");
                DateTime? prevTime = null;
                if (prevTimeEl != null && DateTime.TryParse(prevTimeEl.Value, null, DateTimeStyles.AdjustToUniversal, out var ptm))
                    prevTime = ptm;
                double d = GetDistanceFromLatLonInM(lat, lon, plat, plon);
                dist += d;
                if (prevTime != null)
                {
                    double dt = (time.Value - prevTime.Value).TotalSeconds;
                    if (dt > 0 && d > 0)
                    {
                        double speed = d / dt;
                        if (speed < 20)
                        {
                            sumSpeed += speed;
                            speedCount++;
                            if (speed > maxSpeed) maxSpeed = speed;
                        }
                    }
                }
            }
            prev = pt;
        }
        double duration = (end.HasValue && start.HasValue) ? (end.Value - start.Value).TotalSeconds : 0;
        return new GpxStats
        {
            name = Path.GetFileName(filePath),
            start = start,
            end = end,
            duration = duration,
            dist = dist,
            avgSpeed = speedCount > 0 ? sumSpeed / speedCount : 0,
            maxSpeed = maxSpeed
        };
    }

    private static double GetDistanceFromLatLonInM(double lat1, double lon1, double lat2, double lon2)
    {
        var R = 6371000.0;
        var dLat = (lat2 - lat1) * Math.PI / 180.0;
        var dLon = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        var d = R * c;
        return d;
    }

    public class GpxStats
    {
        public string? name { get; set; }
        public DateTime? start { get; set; }
        public DateTime? end { get; set; }
        public double duration { get; set; }
        public double dist { get; set; }
        public double avgSpeed { get; set; }
        public double maxSpeed { get; set; }
    }
}
