using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;

// ==============================================
// Travel to Hospital Advisor – Carpark API
// Reads live carpark data from THA_DB.db
// ==============================================

var builder = WebApplication.CreateBuilder(args);

// Read DB path from appsettings.json
var dbPath = builder.Configuration["DatabasePath"];
if (string.IsNullOrWhiteSpace(dbPath))
{
    Console.WriteLine("ERROR: DatabasePath is not set in appsettings.json");
    return;
}

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowFrontend");

// Root check
app.MapGet("/", () =>
    "Carpark API for Travel to Hospital Advisor - use /api/carparks/{hospitalCode}");


// ------------------------------------------------------
// GET /api/carparks/{hospitalCode}
// ------------------------------------------------------
app.MapGet("/api/carparks/{hospitalCode}", async (string hospitalCode) =>
{
    hospitalCode = hospitalCode.ToUpper();

    var carparks = new List<CarparkDto>();

    try
    {
        using var conn = new SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        // NOLOCK equivalent for SQLite
        var cfg = conn.CreateCommand();
        cfg.CommandText = @"
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 2000;
            PRAGMA read_uncommitted = TRUE;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
        ";
        await cfg.ExecuteNonQueryAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT 
                c.carpark_id,
                c.heorg_id,
                h.heorg_name,
                c.carpark_name,
                c.total_spaces,
                c.occupied_spaces,
                c.status,
                c.last_updated
            FROM carpark c
            JOIN health_org h ON c.heorg_id = h.heorg_id
            WHERE c.heorg_id = @heorg
            ORDER BY c.carpark_name;
        ";

        cmd.Parameters.AddWithValue("@heorg", hospitalCode);
        cmd.CommandTimeout = 2; // << key

        using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            int total = reader.GetInt32(4);
            int occupied = reader.GetInt32(5);
            int free = Math.Max(0, total - occupied);

            carparks.Add(new CarparkDto
            {
                Id = reader.GetInt32(0),
                HospitalCode = reader.GetString(1),
                HospitalName = reader.GetString(2),
                Name = reader.GetString(3),
                Total = total,
                Occupied = occupied,
                Free = free,
                Status = reader["status"]?.ToString() ?? "UNKNOWN",
                LastUpdated = reader["last_updated"]?.ToString() ?? ""
            });
        }
    }
    catch (SqliteException ex)
    {
        return Results.Problem($"SQLite error: {ex.Message}");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to load carparks: {ex.Message}");
    }

    if (carparks.Count == 0)
    {
        return Results.NotFound(new
        {
            error = $"No carparks found for hospital '{hospitalCode}'.",
            hospital = hospitalCode
        });
    }

    var response = new
    {
        hospital = hospitalCode,
        carparks
    };

    return Results.Json(response);
});

app.Run("http://localhost:5020");


// ---------------------------------------
// DTO used for JSON response
// ---------------------------------------
public class CarparkDto
{
    public int Id { get; set; }
    public string HospitalCode { get; set; }
    public string HospitalName { get; set; }
    public string Name { get; set; }
    public int Total { get; set; }
    public int Occupied { get; set; }
    public int Free { get; set; }
    public string Status { get; set; }
    public string LastUpdated { get; set; }
}
