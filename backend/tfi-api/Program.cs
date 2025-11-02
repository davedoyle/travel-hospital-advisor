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
using Microsoft.Data.Sqlite;

// ==========================================
// Travel to Hospital Advisor – Backend API
// Live Feed Import + GTFS Query Integration
// ==========================================

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});
var app = builder.Build();
app.UseCors("AllowFrontend");

// -------------------------------------------
// Config
// -------------------------------------------
HttpClient client = new HttpClient();
const string ApiKey = "5f37f29af0364c70a364b3e034deb877";
const string FeedUrl = "https://api.nationaltransport.ie/gtfsr/v2/gtfsr?format=json";
const string DbPath = @"C:\Users\user\Desktop\Final Project\travel-hospital-advisor\db\tfi_db.db";
var hospitalStops = new Dictionary<string, string>
{
    { "CUH", "8370B243341" },
    { "SFH", "8370B2528201" }
};

// -------------------------------------------
// Root
// -------------------------------------------
app.MapGet("/", () => "TFI GTFS Live API + GTFS SQLite Backend");

// -------------------------------------------
// Import Function: Pull TFI feed → SQLite
// -------------------------------------------
async Task ImportLiveFeedToSQLite()
{
    Console.WriteLine("\n[Importer] Fetching live feed...");

    client.DefaultRequestHeaders.Clear();
    client.DefaultRequestHeaders.Add("x-api-key", ApiKey);
    var response = await client.GetAsync(FeedUrl);

    if (!response.IsSuccessStatusCode)
    {
        Console.WriteLine($"Failed: {response.StatusCode}");
        return;
    }

    var json = await response.Content.ReadAsStringAsync();
    var options = new JsonSerializerOptions
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new FlexibleLongConverter() }
    };

    var feed = JsonSerializer.Deserialize<RealtimeFeed>(json, options);
    if (feed?.entity == null)
    {
        Console.WriteLine("No entities found.");
        return;
    }

    using var conn = new SqliteConnection($"Data Source={DbPath}");
    conn.Open();

    var create = conn.CreateCommand();
    create.CommandText = @"
        CREATE TABLE IF NOT EXISTS live_feed (
            entity_id TEXT,
            trip_id TEXT,
            route_id TEXT,
            stop_sequence INTEGER,
            stop_id TEXT,
            arrival_delay INTEGER,
            departure_delay INTEGER,
            arrival_time INTEGER,
            departure_time INTEGER,
            start_time TEXT,
            start_date TEXT,
            fetched_at DATETIME DEFAULT CURRENT_TIMESTAMP
        );";
    create.ExecuteNonQuery();

    var clear = conn.CreateCommand();
    clear.CommandText = "DELETE FROM live_feed;";
    clear.ExecuteNonQuery();

    var insert = conn.CreateCommand();
    insert.CommandText = @"
        INSERT INTO live_feed (
            entity_id, trip_id, route_id, stop_sequence, stop_id,
            arrival_delay, departure_delay, arrival_time, departure_time,
            start_time, start_date
        ) VALUES (
            @entity_id, @trip_id, @route_id, @stop_sequence, @stop_id,
            @arrival_delay, @departure_delay, @arrival_time, @departure_time,
            @start_time, @start_date
        );";
    insert.Parameters.Add("@entity_id", SqliteType.Text);
    insert.Parameters.Add("@trip_id", SqliteType.Text);
    insert.Parameters.Add("@route_id", SqliteType.Text);
    insert.Parameters.Add("@stop_sequence", SqliteType.Integer);
    insert.Parameters.Add("@stop_id", SqliteType.Text);
    insert.Parameters.Add("@arrival_delay", SqliteType.Integer);
    insert.Parameters.Add("@departure_delay", SqliteType.Integer);
    insert.Parameters.Add("@arrival_time", SqliteType.Integer);
    insert.Parameters.Add("@departure_time", SqliteType.Integer);
    insert.Parameters.Add("@start_time", SqliteType.Text);
    insert.Parameters.Add("@start_date", SqliteType.Text);

    var tx = conn.BeginTransaction();
    insert.Transaction = tx;

    int inserted = 0;
    foreach (var entity in feed.entity)
    {
        var trip = entity.trip_update?.trip;
        if (trip == null || entity.trip_update?.stop_time_update == null)
            continue;

        foreach (var st in entity.trip_update.stop_time_update)
        {
            // Only CUH/SFH stops
            if (st.stop_id != "8370B243341" && st.stop_id != "8370B2528201")
                continue;

            insert.Parameters["@entity_id"].Value = entity.id ?? "";
            insert.Parameters["@trip_id"].Value = trip.trip_id ?? "";
            insert.Parameters["@route_id"].Value = trip.route_id ?? "";
            insert.Parameters["@stop_sequence"].Value = st.stop_sequence ?? 0;
            insert.Parameters["@stop_id"].Value = st.stop_id ?? "";
            insert.Parameters["@arrival_delay"].Value = st.arrival?.delay ?? 0;
            insert.Parameters["@departure_delay"].Value = st.departure?.delay ?? 0;
            insert.Parameters["@arrival_time"].Value = st.arrival?.time ?? 0;
            insert.Parameters["@departure_time"].Value = st.departure?.time ?? 0;
            insert.Parameters["@start_time"].Value = trip.start_time ?? "";
            insert.Parameters["@start_date"].Value = trip.start_date ?? "";

            insert.ExecuteNonQuery();
            inserted++;
        }
    }

    tx.Commit();
    Console.WriteLine($"[Importer] Inserted {inserted} live rows.");
}

// -------------------------------------------
// /api/bus/{hospitalCode}
// -------------------------------------------
app.MapGet("/api/bus/{hospitalCode}", async (string hospitalCode) =>
{
    hospitalCode = hospitalCode.ToUpper();
    if (!hospitalStops.ContainsKey(hospitalCode))
        return Results.NotFound(new { error = "Unknown hospital code (CUH/SFH)" });

    // Run importer before query
    await ImportLiveFeedToSQLite();

    string stopId = hospitalStops[hospitalCode];
    Console.WriteLine($"[API] Querying data for {hospitalCode} ({stopId})...");

    using var conn = new SqliteConnection($"Data Source={DbPath}");
    await conn.OpenAsync();

    string sql = @"
WITH ActiveService AS (
    SELECT service_id
    FROM calendar
    WHERE (
        (strftime('%w', 'now', 'localtime') = '1' AND monday = 1) OR
        (strftime('%w', 'now', 'localtime') = '2' AND tuesday = 1) OR
        (strftime('%w', 'now', 'localtime') = '3' AND wednesday = 1) OR
        (strftime('%w', 'now', 'localtime') = '4' AND thursday = 1) OR
        (strftime('%w', 'now', 'localtime') = '5' AND friday = 1) OR
        (strftime('%w', 'now', 'localtime') = '6' AND saturday = 1) OR
        (strftime('%w', 'now', 'localtime') = '0' AND sunday = 1)
    )
    AND start_date <= strftime('%Y%m%d', 'now', 'localtime')
    AND end_date   >= strftime('%Y%m%d', 'now', 'localtime')
),
WindowedTrips AS (
    SELECT 
        r.route_long_name,
        r.route_short_name,
        t.trip_headsign,
        t.trip_id,
        s.stop_id,
        s.arrival_time
    FROM stop_times s
    JOIN trips t ON s.trip_id = t.trip_id
    JOIN routes r ON t.route_id = r.route_id
    WHERE s.stop_id = @stopId
      AND t.service_id IN (SELECT service_id FROM ActiveService)
      AND (
            time(s.arrival_time) BETWEEN time('now', '-1 hour', 'localtime')
            AND time('now', '+1 hour', 'localtime')
          )
)
SELECT 
    w.route_short_name AS route,
    w.route_long_name AS description,
    w.trip_headsign AS destination,
    w.arrival_time AS scheduled_arrival,
    IFNULL(l.arrival_delay / 60, 0) AS delay_minutes,
    CASE 
        WHEN IFNULL(l.arrival_delay, 0) > 0 THEN 'Delayed'
        WHEN IFNULL(l.arrival_delay, 0) < 0 THEN 'Early'
        ELSE 'On Time'
    END AS delay_status,
    time(strftime('%s', w.arrival_time) + IFNULL(l.arrival_delay, 0), 'unixepoch') AS expected_arrival,
    CASE WHEN l.trip_id IS NOT NULL THEN 'Y' ELSE 'N' END AS live_hit,
    l.fetched_at
FROM WindowedTrips w
LEFT JOIN live_feed l ON w.trip_id = l.trip_id
ORDER BY w.arrival_time;";

    using var cmd = new SqliteCommand(sql, conn);
    cmd.Parameters.AddWithValue("@stopId", stopId);

    var reader = await cmd.ExecuteReaderAsync();
    var results = new List<object>();

    while (await reader.ReadAsync())
    {
        results.Add(new
        {
            route = reader["route"].ToString(),
            description = reader["description"].ToString(),
            destination = reader["destination"].ToString(),
            scheduled = reader["scheduled_arrival"].ToString(),
            delay_min = reader["delay_minutes"].ToString(),
            delay_status = reader["delay_status"].ToString(),
            expected = reader["expected_arrival"].ToString(),
            live_hit = reader["live_hit"].ToString(),
            fetched_at = reader["fetched_at"].ToString()
        });
    }

    return Results.Json(new
    {
        hospital = hospitalCode,
        timestamp = DateTime.Now.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
        results
    });
});

// -------------------------------------------
app.Run("http://localhost:5030");

// ===========================================
//   Model Classes
// ===========================================
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
    public string start_time { get; set; }
    public string start_date { get; set; }
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

public class FlexibleLongConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var val))
            return val;
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (long.TryParse(s, out var parsed))
                return parsed;
        }
        return null;
    }
    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
