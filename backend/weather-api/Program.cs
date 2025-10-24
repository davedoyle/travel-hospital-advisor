using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
//using Newtonsoft.Json.Linq; --no longer needed



var builder = WebApplication.CreateBuilder(args);

/*Note: The Weather API was originally planned as a gRPC service, 
 but browsers can’t call gRPC directly without a proxy layer like gRPC-Web. 
 I switched to a simple REST API so my HTML frontend could fetch live data easily. 
 The backend logic and structure still follow the same service-based design as my gRPC setup.*/

//Register CORS service -- issue with values passing and being blocked at browser level
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend",
        policy => policy.AllowAnyOrigin()
                        .AllowAnyMethod()
                        .AllowAnyHeader());
});


var app = builder.Build();

// Use CORS before endpoints
app.UseCors("AllowFrontend");


// Shared HttpClient
HttpClient client = new HttpClient();

// Static hospital coordinates
var hospitals = new Dictionary<string, (double lat, double lon)>
{
    { "CUH", (51.8847, -8.5333) },
    { "SFH", (51.8903, -8.4376) }
};

// Simple code-to-text lookup for weather conditions
static string TranslateWeatherCode(int code)
{
    return code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Sleet",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Unknown"
    };
}

// Root route (for a friendly message)
app.MapGet("/", () => "Weather API for Travel to Hospital Advisor - use /api/weather/{hospitalCode}");

// Main endpoint
app.MapGet("/api/weather/{hospitalCode}", async (string hospitalCode) =>
{
    // keep hospital code at upper case (just incase the values are manually passed in testing)
    hospitalCode = hospitalCode.ToUpper();

    if (!hospitals.ContainsKey(hospitalCode))
    {
        return Results.NotFound(new { error = "Unknown hospital code. Use CUH or SFH." });
    }

    var (lat, lon) = hospitals[hospitalCode];
    //string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true";
    string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true&daily=uv_index_max&timezone=Europe/Dublin";

try
{
    var response = await client.GetStringAsync(url);
    using var jsonDoc = JsonDocument.Parse(response);

    var root = jsonDoc.RootElement;

    // Current weather data
    var current = root.GetProperty("current_weather");

    double temperature = current.GetProperty("temperature").GetDouble();
    double windspeed = current.GetProperty("windspeed").GetDouble();
    int weatherCode = current.GetProperty("weathercode").GetInt32();
    string condition = TranslateWeatherCode(weatherCode);
    string timestamp = current.GetProperty("time").GetString() ?? DateTime.UtcNow.ToString("s");

    // UV index — comes from the "daily" section
    double uv = 0;
    if (root.TryGetProperty("daily", out JsonElement daily))
        {
        if (daily.TryGetProperty("uv_index_max", out JsonElement uvArray) && uvArray.GetArrayLength() > 0)
            {
            uv = uvArray[0].GetDouble();
            }
         }

    var result = new
        {
        hospital = hospitalCode,
        temperature,
        windspeed,
        weatherCode,
        condition,
        uv,
        timestamp
         };

        return Results.Json(result);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to fetch weather data: {ex.Message}");
    }
});

//run at set port
app.Run("http://localhost:5028");