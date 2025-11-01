using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

// ==============================================
// Travel to Hospital Advisor – Live TFI Feed API
// Focused on CUH and SFH bus stops only
// ==============================================

var builder = WebApplication.CreateBuilder(args);

/*
   NOTE: This service queries the GTFS-Realtime feed directly
   and extracts upcoming live departures for CUH and SFH stops.
   The logic now matches flexible stop IDs (e.g. 243341 vs 8370B243341)
   and stores debug output for troubleshooting.
*/

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

// Shared HttpClient for all calls
HttpClient client = new HttpClient();

// TFI feed configuration
const string ApiKey = "5f37f29af0364c70a364b3e034deb877";
const string FeedUrl = "https://api.nationaltransport.ie/gtfsr/v2/gtfsr?format=json";

// Known hospital stop IDs
var hospitalStops = new Dictionary<string, string>
{
    { "CUH", "8370B243341" },
    { "SFH", "8370B2528201" }
};

// ------------------------------------------------------------
// Root route (friendly message)
// ------------------------------------------------------------
app.MapGet("/", () =>
{
    return "TFI GTFS Realtime Live Feed API – Hospital Stops (CUH / SFH)";
});


// ------------------------------------------------------------
// /api/live/searchstop/{stopId} → Quick search for stop matches
// ------------------------------------------------------------
app.MapGet("/api/live/searchstop/{stopId}", async (string stopId) =>
{
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("x-api-key", ApiKey);
    var response = await client.GetAsync(FeedUrl);
    var json = await response.Content.ReadAsStringAsync();

    var feed = JsonSerializer.Deserialize<RealtimeFeed>(json);
    if (feed?.entity == null)
        return Results.Json(new { error = "No entities found." });

    string coreId = stopId.Length > 6 ? stopId[^6..] : stopId; // last 6 chars
    var matches = feed.entity
        .Where(e => e.trip_update?.stop_time_update != null)
        .SelectMany(e => e.trip_update.stop_time_update
            .Where(s => s.stop_id != null && s.stop_id.Contains(coreId)))
        .ToList();

    Console.WriteLine($"SearchStop: Found {matches.Count} entries matching stop {stopId} (core {coreId}).");

    return Results.Json(new
    {
        stopId,
        matches = matches.Count
    });
});


// ------------------------------------------------------------
// /api/live/hospitals → Pull live arrivals for CUH + SFH
// ------------------------------------------------------------
app.MapGet("/api/live/hospitals", async () =>
{
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("x-api-key", ApiKey);

    Console.WriteLine("Fetching live feed for CUH + SFH...");
    var response = await client.GetAsync(FeedUrl);
    Console.WriteLine($"TFI response: {response.StatusCode}");

    var json = await response.Content.ReadAsStringAsync();
    Directory.CreateDirectory("debug");
    await File.WriteAllTextAsync($"debug/tfi_livefeed_{DateTime.Now:HHmmss}.json", json);

    var feed = JsonSerializer.Deserialize<RealtimeFeed>(json);
    if (feed?.entity == null)
        return Results.Json(new { error = "No entities found in live feed." });

    var stops = new List<object>();

    foreach (var hospital in hospitalStops)
    {
        string hospitalName = hospital.Key;
        string stopId = hospital.Value;
        string coreId = stopId.Length > 6 ? stopId[^6..] : stopId; // last 6 digits

        Console.WriteLine($"Checking hospital {hospitalName} (stop {stopId}, core {coreId})...");

        var liveMatches = feed.entity
            .Where(e => e.trip_update?.stop_time_update != null)
            .SelectMany(e => e.trip_update.stop_time_update
                .Where(s => s.stop_id != null && s.stop_id.Contains(coreId))
                .Select(s => new
                {
                    trip = e.trip_update.trip?.trip_id,
                    route = e.trip_update.trip?.route_id,
                    stop = s.stop_id,
                    arrDelay = s.arrival?.delay,
                    depDelay = s.departure?.delay,
                    arrTime = s.arrival?.time,
                    depTime = s.departure?.time
                }))
            .ToList();

        Console.WriteLine($"Found {liveMatches.Count} matches for {hospitalName}.");

        stops.Add(new
        {
            hospital = hospitalName,
            stop_id = stopId,
            upcoming = liveMatches.Take(5)
        });
    }

    Console.WriteLine("=== SUMMARY ===");
    foreach (var s in stops)
        Console.WriteLine(JsonSerializer.Serialize(s));

    var result = new
    {
        timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        stops
    };

    return Results.Json(result);
});


// ------------------------------------------------------------
// /api/live/raw/{stopId} → Dump all realtime entries for any stop
// ------------------------------------------------------------
app.MapGet("/api/live/raw/{stopId}", async (string stopId) =>
{
    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("x-api-key", ApiKey);
    var response = await client.GetAsync(FeedUrl);
    var json = await response.Content.ReadAsStringAsync();

    var feed = JsonSerializer.Deserialize<RealtimeFeed>(json);
    if (feed?.entity == null)
        return Results.Json(new { error = "No entities found." });

    string coreId = stopId.Length > 6 ? stopId[^6..] : stopId;
    var matches = feed.entity
        .Where(e => e.trip_update?.stop_time_update != null)
        .SelectMany(e => e.trip_update.stop_time_update
            .Where(s => s.stop_id != null && s.stop_id.Contains(coreId))
            .Select(s => new
            {
                tripId = e.trip_update.trip?.trip_id,
                routeId = e.trip_update.trip?.route_id,
                stopId = s.stop_id,
                arrivalDelay = s.arrival?.delay,
                departureDelay = s.departure?.delay,
                arrivalTime = s.arrival?.time,
                departureTime = s.departure?.time
            }))
        .ToList();

    return Results.Json(new
    {
        stopId,
        totalMatches = matches.Count,
        timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
        data = matches.Take(10)
    });
});


// ------------------------------------------------------------
// Run the app on port 5040
// ------------------------------------------------------------
app.Run("http://localhost:5040");


// =======================================
//   GTFS Realtime Feed Models
// =======================================
public class RealtimeFeed
{
    public List<RealtimeEntity> entity { get; set; } = new();
}

public class RealtimeEntity
{
    public string id { get; set; }
    public TripUpdate trip_update { get; set; }
}

public class TripUpdate
{
    public TripDescriptor trip { get; set; }
    public List<StopTimeUpdate> stop_time_update { get; set; }
}

public class TripDescriptor
{
    public string trip_id { get; set; }
    public string route_id { get; set; }
}

public class StopTimeUpdate
{
    public int? stop_sequence { get; set; }
    public StopTimeEvent arrival { get; set; }
    public StopTimeEvent departure { get; set; }
    public string stop_id { get; set; }
}

public class StopTimeEvent
{
    [JsonConverter(typeof(FlexibleLongConverter))]
    public long? time { get; set; }
    public int? delay { get; set; }
}

// Converter for handling string or numeric UNIX timestamps
public class FlexibleLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var val))
            return val;

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (long.TryParse(str, out var val2))
                return val2;
        }
        return null;
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteNumberValue(value.Value);
        else
            writer.WriteNullValue();
    }
}
