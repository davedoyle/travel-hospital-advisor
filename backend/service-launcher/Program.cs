using System.Diagnostics;
using System.Net.Http;
using Microsoft.Extensions.Configuration;

// ===============================================
// Travel to Hospital Advisor - Service Launcher
// Starts the backend services and keeps an eye on them
// ===============================================

// Load config the normal way for a console app
var config = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .Build();

// Pull paths from config
LauncherState.MainDbPath = config["Paths:MainDb"] ?? "";
LauncherState.TfiDbPath  = config["Paths:TfiDb"] ?? "";

// Pull ports from config
LauncherState.AdminApiPort   = int.TryParse(config["Ports:AdminApi"], out var p1) ? p1 : 5050;
LauncherState.WeatherApiPort = int.TryParse(config["Ports:WeatherApi"], out var p2) ? p2 : 5028;
LauncherState.TfiApiPort     = int.TryParse(config["Ports:TfiApi"], out var p3) ? p3 : 5030;
LauncherState.CarparkApiPort = int.TryParse(config["Ports:CarparkApi"], out var p4) ? p4 : 5040;

// Start time stamp for uptime later
LauncherState.StartTimeUtc = DateTime.UtcNow;

// simple http listener for status checks
var statusApp = WebApplication.CreateBuilder().Build();

statusApp.MapGet("/", () =>
{
    return Results.Ok(new
    {
        startTimeUtc = LauncherState.StartTimeUtc,
        mainDb = LauncherState.MainDbPath,
        tfiDb = LauncherState.TfiDbPath,
        adminPort = LauncherState.AdminApiPort,
        weatherPort = LauncherState.WeatherApiPort,
        tfiPort = LauncherState.TfiApiPort,
        carparkPort = LauncherState.CarparkApiPort,
        weatherStatus = LauncherState.WeatherStatus,
        tfiStatus = LauncherState.TfiStatus,
        simStatus = LauncherState.SimulationStatus,
        lastWeather = LauncherState.LastWeatherUpdate,
        lastTfi = LauncherState.LastTfiUpdate
    });
});

// ----------------------------------------------
// check endpoint (weather, tfi, sim)
// ----------------------------------------------
statusApp.MapPost("/heartbeat", (HeartbeatUpdate hb) =>
{
    var now = DateTime.UtcNow;

    switch (hb.Service)
    {
        case "weather":
            LauncherState.WeatherStatus = "OK";
            LauncherState.LastWeatherUpdate = now.ToString("yyyy-MM-dd HH:mm:ss");
            break;

        case "tfi":
            LauncherState.TfiStatus = "OK";
            LauncherState.LastTfiUpdate = now.ToString("yyyy-MM-dd HH:mm:ss");
            break;

        case "sim":
            LauncherState.SimulationStatus = hb.Message ?? "Running";
            break;
    }

    return Results.Ok();
});

// run listener
_ = Task.Run(() => statusApp.RunAsync("http://localhost:5199"));

// All services we want to run
var services = new List<ServiceInfo>
{
    new ServiceInfo
    {
        Name = "weather-api",
        ProjectPath = @"..\weather-api\weather-api.csproj",
        HealthUrl = $"http://localhost:{LauncherState.WeatherApiPort}/"
    },
    new ServiceInfo
    {
        Name = "tfi-api",
        ProjectPath = @"..\tfi-api\tfi-api.csproj",
        HealthUrl = $"http://localhost:{LauncherState.TfiApiPort}/"
    },
    new ServiceInfo
    {
        Name = "carpark-sim",
        ProjectPath = @"..\carpark-sim\carpark-sim.csproj",
        HealthUrl = null        // background worker, no HTTP endpoint
    },
    new ServiceInfo
    {
        Name = "carpark-api",
        ProjectPath = @"..\carpark-api\carpark-api.csproj",
        HealthUrl = $"http://localhost:{LauncherState.CarparkApiPort}/"
    },
    new ServiceInfo
    {
        Name = "admin-api",
        ProjectPath = @"..\admin-api\admin-api.csproj",
        HealthUrl = $"http://localhost:{LauncherState.AdminApiPort}/"
    }
};

Console.WriteLine("===============================================");
Console.WriteLine(" Travel to Hospital Advisor - Service Launcher ");
Console.WriteLine("===============================================");
Console.WriteLine("Working directory: " + Directory.GetCurrentDirectory());
Console.WriteLine();
Console.WriteLine("Main DB : " + LauncherState.MainDbPath);
Console.WriteLine("TFI  DB : " + LauncherState.TfiDbPath);
Console.WriteLine("Ports   : Admin " + LauncherState.AdminApiPort +
                  ", Weather " + LauncherState.WeatherApiPort +
                  ", TFI " + LauncherState.TfiApiPort +
                  ", Carpark " + LauncherState.CarparkApiPort);
Console.WriteLine();

// Start everything
foreach (var svc in services)
{
    LauncherHelpers.StartService(svc);
}

// Background health checks
_ = Task.Run(() => LauncherHelpers.HealthCheckLoopAsync(services));

// Handle Ctrl+C properly
Console.CancelKeyPress += (sender, eventArgs) =>
{
    Console.WriteLine();
    Console.WriteLine("Stopping all services...");
    foreach (var svc in services)
        LauncherHelpers.TryStopService(svc);

    eventArgs.Cancel = false;
};

Console.WriteLine();
Console.WriteLine("All services started.");
Console.WriteLine("Press Ctrl+C to stop everything.");
Console.WriteLine();





await Task.Delay(Timeout.Infinite);




// ------------------------------------------------
// helper stuff
// ------------------------------------------------

public record HeartbeatUpdate(
    string Service,
    string? Message
);


class ServiceInfo
{
    public string Name { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string? HealthUrl { get; set; }
    public Process? Process { get; set; }
}

static class LauncherHelpers
{
    // Start a service
    public static void StartService(ServiceInfo svc)
    {
        try
        {
            TryStopService(svc);

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{svc.ProjectPath}\"",
                WorkingDirectory = Directory.GetCurrentDirectory(),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Console.WriteLine($"[{svc.Name}] {e.Data}");
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    Console.WriteLine($"[{svc.Name}][ERR] {e.Data}");
            };

            if (process.Start())
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                svc.Process = process;
                Console.WriteLine($"[{svc.Name}] Started.");
            }
            else
            {
                Console.WriteLine($"[{svc.Name}] Failed to start.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{svc.Name}] ERROR starting service: {ex.Message}");
        }
    }

    // Try stop
    public static void TryStopService(ServiceInfo svc)
    {
        try
        {
            if (svc.Process == null || svc.Process.HasExited)
                return;

            Console.WriteLine($"[{svc.Name}] Stopping...");
            svc.Process.Kill(entireProcessTree: true);
            svc.Process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{svc.Name}] ERROR stopping service: {ex.Message}");
        }
        finally { svc.Process = null; }
    }

    // Loop and watch everything
    public static async Task HealthCheckLoopAsync(List<ServiceInfo> services)
    {
        using var http = new HttpClient();

        while (true)
        {
            foreach (var svc in services)
            {
                if (svc.HealthUrl == null)
                {
                    if (svc.Process == null || svc.Process.HasExited)
                    {
                        Console.WriteLine($"[health] {svc.Name}: restarting...");
                        StartService(svc);
                    }
                    continue;
                }

                if (svc.Process == null || svc.Process.HasExited)
                {
                    Console.WriteLine($"[health] {svc.Name}: restarting...");
                    StartService(svc);
                    continue;
                }

                try
                {
                    var result = await http.GetAsync(svc.HealthUrl);

                    if (result.IsSuccessStatusCode)
                        Console.WriteLine($"[health] {svc.Name}: OK");
                    else
                        Console.WriteLine($"[health] {svc.Name}: HTTP {(int)result.StatusCode}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[health] {svc.Name}: ERROR {ex.Message}");
                }
            }

            await Task.Delay(20000);
        }
    }
}

// Shared launcher-level info for the admin-api to query later
public static class LauncherState
{
    public static DateTime StartTimeUtc;
    public static string MainDbPath = "";
    public static string TfiDbPath = "";

    public static int AdminApiPort;
    public static int WeatherApiPort;
    public static int TfiApiPort;
    public static int CarparkApiPort;

    // These will be updated later once we wire the endpoints
    public static string WeatherStatus = "Unknown";
    public static string TfiStatus = "Unknown";
    public static string SimulationStatus = "Unknown";

    public static string LastWeatherUpdate = "--";
    public static string LastTfiUpdate = "--";
}
