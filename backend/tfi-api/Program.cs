using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;

// ==============================================
// Travel to Hospital Advisor – Bus Service API
// Combines SQLite static data with TFI realtime
// ==============================================

var builder = WebApplication.CreateBuilder(args);

// Enable CORS for local frontend calls
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend",
        policy => policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowFrontend");

// Shared HttpClient
HttpClient client = new HttpClient();

// SQLite database path
const string DbPath = @"C:\Users\user\Desktop\Final Project\travel-hospital-advisor\db\THA_db.db";

// Known hospital stops
var hospitalStops = new Dictionary<string, List<string>>
{
    { "CUH", new List<string> { "8370B243341" } },
    { "SFH", new List<string> { "8370B2528201" } }
};

// TFI API settings
const string ApiKey = "5f37f29af0364c70a364b3e034deb877";
const string FeedUrl = "https://api.nationaltransport.ie/gtfsr/v2/gtfsr?format=json";

// Root endpoint
app.MapGet("/", () => "TFI GTFS Realtime API – Hospital Bus Feed (CUH / SFH)");


// -------------------------------------------------------------------------
// /api/bus/{hospitalCode} → Combines static + live TFI data
// -------------------------------------------------------------------------
app.MapGet("/api/bus/{hospitalCode}", async (string hospitalCode) =>
{
    hospitalCode = hospitalCode.ToUpper();
    if (!hospitalStops.ContainsKey(hospitalCode))
        return Results.NotFound(new { error = "Unknown hospital code. Use CUH or SFH." });

    string stopId = hospitalStops[hospitalCode].First();
    Console.WriteLine($"Incoming request for hospital: {hospitalCode} (Stop ID: {stopId})");

    try
    {
        // === Step 1: Load scheduled trips from SQLite ===
        var scheduledList = new List<BusSchedule>();
        using (var conn = new SqliteConnection($"Data Source={DbPath}"))
        {
            await conn.OpenAsync();
            string sql = @"
                SELECT 
                    r.route_long_name, 
                    r.route_short_name,
                    t.trip_headsign, 
                    t.service_id,
                    s.arrival_time, 
                    s.trip_id
                FROM routes r
                JOIN trips t ON r.route_id = t.route_id
                JOIN stop_times s ON s.trip_id = t.trip_id
                WHERE s.stop_id = @stopId
                ORDER BY s.arrival_time;";
            using var cmd = new SqliteCommand(sql, conn);
            cmd.Parameters.AddWithValue("@stopId", stopId);
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                scheduledList.Add(new BusSchedule
                {
                    RouteName = reader["route_long_name"].ToString(),
                    RouteShortName = reader["route_short_name"].ToString(),
                    TripHeadsign = reader["trip_headsign"].ToString(),
                    ServiceId = reader["service_id"].ToString(),
                    ArrivalTime = reader["arrival_time"].ToString(),
                    TripId = reader["trip_id"].ToString()
                });
            }
        }

        Console.WriteLine($"SQLite returned {scheduledList.Count} scheduled rows for stop {stopId}");

        // Filter: keep next 10 upcoming trips only
        var upcoming = scheduledList
            .Where(x =>
            {
                if (TimeSpan.TryParse(x.ArrivalTime, out var t))
                    return t > DateTime.Now.TimeOfDay;
                return false;
            })
            .Take(10)
            .ToList();

        Console.WriteLine($"Filtered to {upcoming.Count} upcoming trips for {hospitalCode}");

        // === Step 2: Fetch realtime TFI JSON ===
        client.DefaultRequestHeaders.Clear();
        client.DefaultRequestHeaders.Add("x-api-key", ApiKey);

        var response = await client.GetAsync(FeedUrl);
        Console.WriteLine($"TFI response: {response.StatusCode}");
        var json = await response.Content.ReadAsStringAsync();

        Directory.CreateDirectory("debug");
        await File.WriteAllTextAsync("debug/tfi_debug.json", json);
        var feed = JsonSerializer.Deserialize<RealtimeFeed>(json);

        if (feed?.entity == null)
        {
            Console.WriteLine("⚠️ No realtime entities found.");
            return Results.Json(new { hospital = hospitalCode, buses = upcoming });
        }

        // === Step 3: Combine scheduled + live ===
        var finalList = new List<BusResult>();
        int matchedCount = 0;

        foreach (var sched in upcoming)
        {
            var liveMatch = feed.entity
                .Where(e => e.trip_update?.trip?.trip_id == sched.TripId)
                .SelectMany(e => e.trip_update.stop_time_update ?? new List<StopTimeUpdate>())
                .FirstOrDefault(u => u.stop_id == stopId);

            int? delaySec = liveMatch?.arrival?.delay ?? liveMatch?.departure?.delay;
            int delayMin = delaySec.HasValue ? delaySec.Value / 60 : 0;

            // ignore unrealistic values
            if (delayMin < -5 || delayMin > 60)
                delayMin = 0;

            DateTime schedTime = DateTime.Parse(sched.ArrivalTime);
            DateTime finalDue = schedTime.AddMinutes(delayMin);

            matchedCount++;

            Console.WriteLine(
                $"MATCHED: {sched.RouteShortName} {sched.TripHeadsign} → {finalDue:HH:mm} " +
                (delayMin > 0 ? $"(+{delayMin} min delay)" :
                 delayMin < 0 ? $"({delayMin} min early)" :
                 "(on time)")
            );

            finalList.Add(new BusResult
            {
                Route = sched.RouteName,
                RouteShortName = sched.RouteShortName,
                Destination = sched.TripHeadsign,
                ServiceId = sched.ServiceId,
                Scheduled = schedTime.ToString("HH:mm"),
                DelayMin = delayMin != 0 ? delayMin : null,
                FinalDue = finalDue.ToString("HH:mm"),
                DueIn = delayMin == 0 ? "On Time" : $"{delayMin:+#;-#;0} min"
            });
        }

        Console.WriteLine($"Matched {matchedCount} live trips for stop {stopId}");

        // === Step 4: Return formatted JSON ===
        return Results.Json(new
        {
            hospital = hospitalCode,
            timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            buses = finalList
        });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Bus data retrieval failed: {ex.Message}");
        return Results.Problem($"Bus data retrieval failed: {ex.Message}");
    }
});

// -------------------------------------------------------------------------
// /api/debug/fullfeed/{stopId} – dump all trips + delays for a given stop ---debug is constantly changing!
// -------------------------------------------------------------------------
app.MapGet("/api/debug/fullfeed/{stopId}", async (string stopId) =>
{
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("x-api-key", ApiKey);

    var response = await client.GetAsync(FeedUrl);
    Console.WriteLine($"[DEBUG] Pulling full feed for stop {stopId}, status {response.StatusCode}");
    var content = await response.Content.ReadAsStringAsync();

    var feed = JsonSerializer.Deserialize<RealtimeFeed>(content);
    if (feed?.entity == null)
        return Results.Json(new { error = "No entities found in realtime feed." });

    var allMatches = new List<object>();

    foreach (var e in feed.entity)
    {
        var tripId = e.trip_update?.trip?.trip_id;
        var routeId = e.trip_update?.trip?.route_id;

        if (e.trip_update?.stop_time_update == null)
            continue;

        foreach (var s in e.trip_update.stop_time_update)
        {
            if (s.stop_id == stopId)
            {
                allMatches.Add(new
                {
                    tripId,
                    routeId,
                    stopId = s.stop_id,
                    arrivalDelay = s.arrival?.delay,
                    departureDelay = s.departure?.delay,
                    arrivalTime = s.arrival?.time,
                    departureTime = s.departure?.time
                });
            }
        }
    }

    Console.WriteLine($"[DEBUG] Found {allMatches.Count} raw updates for stop {stopId}");

    // Return raw, no filtering
    return Results.Json(new
    {
        stopId,
        totalMatches = allMatches.Count,
        timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        data = allMatches
    });
});



app.Run("http://localhost:5030");
