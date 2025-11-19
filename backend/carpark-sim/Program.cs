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

// full SQLite connection string
var connectionString = $"Data Source={dbPath};";

// simple flag so we can shut it down safely later if needed
var app = builder.Build();

// start simulation loop in background
_ = Task.Run(async () =>
{
    Console.WriteLine("Carpark Simulation Engine starting…");

    while (true)
    {
        try
        {
            RunSimulationTick(connectionString);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Simulation error: " + ex.Message);
        }

        await Task.Delay(5000); // 5-second update window
    }
});

app.MapGet("/", () => "Carpark Simulation Engine is running.");
app.Run();


// ------------------------------------------------------------
// SIMULATION LOGIC
// ------------------------------------------------------------
void RunSimulationTick(string connString)
{
    var now = DateTime.Now;
    int hour = now.Hour;

    // decide behaviour for this hour
    // positive = filling, negative = emptying
    int changeRate = DetermineChangeRate(hour); //restore post test
    //int changeRate = 5;//testing
    using var conn = new SqliteConnection(connString);
    conn.Open();

    // get all carparks
    var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT carpark_id, total_spaces, occupied_spaces FROM carpark;";
    using var reader = cmd.ExecuteReader();

    var updates = new List<(int id, int total, int occupied)>();

    while (reader.Read())
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
        // small random element so it's not robotic
        var rand = Random.Shared.Next(-1, 2); // -1, 0, or +1
        Console.WriteLine($"Tick: hour={hour}, changeRate={changeRate}, rand={rand}");// testing
        int newOccupied = item.occupied + changeRate + rand;

        // basic bounds: can't exceed total spaces or drop below zero
        if (newOccupied < 0) newOccupied = 0;
        if (newOccupied > item.total) newOccupied = item.total;

        UpdateCarpark(conn, item.id, newOccupied);
        InsertLog(conn, item.id, item.occupied, newOccupied);
    }

    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Simulation tick complete.");
}


// ------------------------------------------------------------
// Adjust filling/emptying based on real clock
// ------------------------------------------------------------
int DetermineChangeRate(int hour)
{
    // 09:00–13:00 → filling quickly
    if (hour >= 9 && hour < 13)
        return 3;

    // 12:00–16:00 → emptying
    if (hour >= 12 && hour < 16)
        return -2;

    // 19:00–20:00 → visiting hour, filling
    if (hour >= 19 && hour < 20)
        return 4;

    // 20:00–23:00 → emptying
    if (hour >= 20 && hour < 23)
        return -3;

    // 23:00–07:00 → quiet overnight
    if (hour >= 23 || hour < 7)
        return 0;

    // general daytime (7–9am, 16–19)
    return 1; // trickle in
}


// ------------------------------------------------------------
// Update carpark record
// ------------------------------------------------------------
void UpdateCarpark(SqliteConnection conn, int carparkId, int newOccupied)
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

    cmd.ExecuteNonQuery();
}


// ------------------------------------------------------------
// Log changes
// ------------------------------------------------------------
void InsertLog(SqliteConnection conn, int carparkId, int oldValue, int newValue)
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

    cmd.ExecuteNonQuery();
}
