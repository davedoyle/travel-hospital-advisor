using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;

var builder = WebApplication.CreateBuilder(args);

/* 
   NOTE: The TFI Bus API was structured the same way as my Weather API — 
   keeping the same REST-style approach for easy browser calls from my HTML frontend. 
   I’ll later replace the mocked data with real TFI API calls using my issued API key. 
*/

// Register CORS so the frontend can call this service locally
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend",
        policy => policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowFrontend");

// Shared HttpClient for live calls later
HttpClient client = new HttpClient();

// Hardcoded mapping of hospital to nearest TFI stop (for now)
var hospitalStops = new Dictionary<string, string>
{
    { "CUH", "3420" }, // Example stop ID, to be verified later
    { "SFH", "4187" }
};

// Root route (for friendly message)
app.MapGet("/", () => "TFI API for Travel to Hospital Advisor - use /api/bus/{hospitalCode}");

// Main endpoint
app.MapGet("/api/bus/{hospitalCode}", async (string hospitalCode) =>
{
    hospitalCode = hospitalCode.ToUpper();

    if (!hospitalStops.ContainsKey(hospitalCode))
    {
        return Results.NotFound(new { error = "Unknown hospital code. Use CUH or SFH." });
    }

    try
    {
        // MOCK DATA for now — this will be replaced with real API data later
        var mockData = new[]
        {
            new { route = "208", destination = "City Centre", due_in = "5 min" },
            new { route = "214", destination = "Bishopstown", due_in = "9 min" },
            new { route = "216", destination = "Mount Oval", due_in = "13 min" }
        };

        var result = new
        {
            hospital = hospitalCode,
            stop_id = hospitalStops[hospitalCode],
            buses = mockData,
            timestamp = DateTime.UtcNow.ToString("s")
        };

        return Results.Json(result);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to retrieve bus data: {ex.Message}");
    }
});

// Run on a different port so it doesn't clash with the Weather API
app.Run("http://localhost:5030");
