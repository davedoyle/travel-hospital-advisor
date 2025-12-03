using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.IO;

// ------------------------------------------------------------
// Carpark Simulation Engine
// Background loop runs every 5 seconds while SimulationRunning = true.
// Admin page can Start, Pause, Single Tick, Fast Forward, or Reset.
// ------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// read DB path from appsettings
var dbPath = builder.Configuration["DatabasePath"];
Console.WriteLine("Resolved DB path = " + Path.GetFullPath(dbPath));

if (string.IsNullOrWhiteSpace(dbPath))
{
    Console.WriteLine("DB path missing in appsettings.json");
    return;
}

var connectionString = $"Data Source={dbPath};";

// ------------------------------
// NEW: Simulation State Flags
// ------------------------------
bool SimulationRunning = true;             // background loop toggle
bool ForceSingleTick = false;              // for /sim/tick
int FastForwardCount = 0;                  // for /sim/fastforward

var app = builder.Build();

// ------------------------------------------------------------
// NEW: Simulation Control Endpoints
// ------------------------------------------------------------

// return simple status for Admin badge
app.MapGet("/sim/status", () =>
{
    return Results.Json(new
    {
        running = SimulationRunning,
        status = SimulationRunning ? "Simulation Running" : "Simulation Paused"
    });
});

// start the background simulation
app.MapPost("/sim/start", () =>
{
    SimulationRunning = true;
    Console.WriteLine("[SIM] Simulation set to RUNNING");
    return Results.Ok(new { message = "Simulation Running" });
});

// pause background simulation
app.MapPost("/sim/pause", () =>
{
    SimulationRunning = false;
    Console.WriteLine("[SIM] Simulation set to PAUSED");
    return Results.Ok(new { message = "Simulation Paused" });
});

// trigger exactly one tick, even when paused
app.MapPost("/sim/tick", () =>
{
    ForceSingleTick = true;
    Console.WriteLine("[SIM] Manual single tick requested");
    return Results.Ok(new { message = "Single Tick Executed" });
});

// trigger 10 ticks quickly
app.MapPost("/sim/fastforward", () =>
{
    FastForwardCount = 10;
    Console.WriteLine("[SIM] Fast forward 10 ticks requested");
    return Results.Ok(new { message = "Fast Forward Started" });
});

// reset DB: set occupied_spaces to zero, clear logs
app.MapPost("/sim/reset", async () =>
{
    using var conn = new SqliteConnection(connectionString);
    await conn.OpenAsync();

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        UPDATE carpark
        SET occupied_spaces = 0,
            last_updated = CURRENT_TIMESTAMP;

        DELETE FROM carpark_log;
    ";
    await cmd.ExecuteNonQueryAsync();

    Console.WriteLine("[SIM] Simulation reset to zero.");
    return Results.Ok(new { message = "Simulation Reset Complete" });
});

// root endpoint for health checks
app.MapGet("/", () => "Carpark Simulation Engine running with admin controls.");

// ------------------------------------------------------------
// BACKGROUND SIMULATION LOOP
// ------------------------------------------------------------
_ = Task.Run(async () =>
{
    Console.WriteLine("Carpark Simulation Engine starting…");

    while (true)
    {
        try
        {
            if (SimulationRunning)
            {
                await RunSimulationTick(connectionString);
                SendHeartbeat("Simulation Running");
            }

            // Manual single tick
            if (ForceSingleTick)
            {
                ForceSingleTick = false;
                await RunSimulationTick(connectionString);
                SendHeartbeat("Single Tick");
            }

            // Manual fast-forward ticks
            while (FastForwardCount > 0)
            {
                FastForwardCount--;
                await RunSimulationTick(connectionString);
                SendHeartbeat("FastForward Tick");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Simulation error: " + ex.Message);
        }

        // Sleep only if not fast-forwarding
        await Task.Delay(5000);
    }
});

// ------------------------------------------------------------
// SIMULATION LOGIC 
// ------------------------------------------------------------
async Task RunSimulationTick(string connString)
{
    var now = DateTime.Now;
    int hour = now.Hour;

    int changeRate = DetermineChangeRate(hour);

    using var conn = new SqliteConnection(connString);
    await conn.OpenAsync();

    var cfg = conn.CreateCommand();
    cfg.CommandText = @"
        PRAGMA journal_mode = WAL;
        PRAGMA busy_timeout = 2000;
        PRAGMA read_uncommitted = TRUE;
    ";
    await cfg.ExecuteNonQueryAsync();

    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT carpark_id, total_spaces, occupied_spaces FROM carpark WHERE is_active = 1;";

    using var reader = await cmd.ExecuteReaderAsync();

    var updates = new List<(int id, int total, int occupied)>();

    while (await reader.ReadAsync())
    {
        updates.Add((
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2)
        ));
    }

    reader.Close();

    foreach (var item in updates)
    {
        var rand = Random.Shared.Next(-1, 2);
        Console.WriteLine($"Tick: hour={hour}, changeRate={changeRate}, rand={rand}");

        int newOccupied = item.occupied + changeRate + rand;

        if (newOccupied < 0) newOccupied = 0;
        if (newOccupied > item.total) newOccupied = item.total;

        await UpdateCarpark(conn, item.id, newOccupied);
        await InsertLog(conn, item.id, item.occupied, newOccupied);
    }

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Tick complete.");
}

int DetermineChangeRate(int hour)
{
    if (hour >= 9 && hour < 13) return 3;
    if (hour >= 12 && hour < 16) return -2;
    if (hour >= 19 && hour < 20) return 4;
    if (hour >= 20 && hour < 23) return -3;
    if (hour >= 23 || hour < 7) return 0;
    return 1;
}

async Task UpdateCarpark(SqliteConnection conn, int carparkId, int newOccupied)
{
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        UPDATE carpark
        SET occupied_spaces = $occ,
            last_updated = CURRENT_TIMESTAMP
        WHERE carpark_id = $id AND is_active = 1;
    ";
    cmd.Parameters.AddWithValue("$occ", newOccupied);
    cmd.Parameters.AddWithValue("$id", carparkId);

    await cmd.ExecuteNonQueryAsync();
}

async Task InsertLog(SqliteConnection conn, int carparkId, int oldValue, int newValue)
{
    string action;
    string detail = $"Auto-sim tick: {oldValue} → {newValue}";

    if (newValue > oldValue)
        action = "FILLED";
    else if (newValue < oldValue)
        action = "EMPTIED";
    else
        action = "NOCHANGE";

    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO carpark_log (carpark_id, action, detail, admin_id)
        VALUES ($cid, $act, $det, NULL);
    ";
    cmd.Parameters.AddWithValue("$cid", carparkId);
    cmd.Parameters.AddWithValue("$act", action);
    cmd.Parameters.AddWithValue("$det", detail);

    await cmd.ExecuteNonQueryAsync();
}

// -----------------------------------
// Send heartbeat to launcher
// -----------------------------------
async void SendHeartbeat(string status)
{
    try
    {
        using var hb = new HttpClient();
        await hb.PostAsJsonAsync("http://localhost:5199/heartbeat",
            new { Service = "sim", Message = status });
    }
    catch { }
}

// Run web API on a fixed port
app.Run("http://localhost:5070");
