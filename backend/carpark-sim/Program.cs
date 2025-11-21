using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.IO;

// ------------------------------------------------------------
// Carpark Simulation Engine
// This runs every 5 seconds and updates all carparks in the DB.
// Behaviour changes depending on the real machine time.
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

var app = builder.Build();

// start simulation loop in background
_ = Task.Run(async () =>
{
    Console.WriteLine("Carpark Simulation Engine starting…");

    while (true)
    {
        try
        {
            await RunSimulationTick(connectionString);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Simulation error: " + ex.Message);
        }

        await Task.Delay(5000);
    }
});

app.MapGet("/", () => "Carpark Simulation Engine is running.");
app.Run();


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

    // --- NOLOCK-style behaviour for SQLite ---
    var cfg = conn.CreateCommand();
    cfg.CommandText = @"
        PRAGMA journal_mode = WAL;
        PRAGMA busy_timeout = 2000;
        PRAGMA read_uncommitted = TRUE;
    ";
    await cfg.ExecuteNonQueryAsync();

    // get all carparks
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT carpark_id, total_spaces, occupied_spaces FROM carpark;";

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

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Simulation tick complete.");
}


// ------------------------------------------------------------
// Change rate based on real time
// ------------------------------------------------------------
int DetermineChangeRate(int hour)
{
    if (hour >= 9 && hour < 13) return 3;
    if (hour >= 12 && hour < 16) return -2;
    if (hour >= 19 && hour < 20) return 4;
    if (hour >= 20 && hour < 23) return -3;
    if (hour >= 23 || hour < 7)  return 0;
    return 1;
}


// ------------------------------------------------------------
// Update carpark record
// ------------------------------------------------------------
async Task UpdateCarpark(SqliteConnection conn, int carparkId, int newOccupied)
{
    var cmd = conn.CreateCommand();
    cmd.CommandText = @"
        UPDATE carpark
        SET occupied_spaces = $occ,
            last_updated = CURRENT_TIMESTAMP
        WHERE carpark_id = $id;
    ";

    cmd.Parameters.AddWithValue("$occ", newOccupied);
    cmd.Parameters.AddWithValue("$id", carparkId);

    await cmd.ExecuteNonQueryAsync();
}


// ------------------------------------------------------------
// Log changes
// ------------------------------------------------------------
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
