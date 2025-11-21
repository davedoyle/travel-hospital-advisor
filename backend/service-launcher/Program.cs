using System.Diagnostics;
using System.Net.Http;

// ===============================================
// Travel to Hospital Advisor - Service Launcher
// Starts all backend services and monitors them
// ===============================================

// ------------------------------------------------
// TOP-LEVEL STATEMENTS MUST COME FIRST
// ------------------------------------------------

// We will run this from backend/service-launcher
// So project paths are relative to this folder.
var services = new List<ServiceInfo>
{
    new ServiceInfo
    {
        Name = "weather-api",
        ProjectPath = @"..\weather-api\weather-api.csproj",
        HealthUrl = "http://localhost:5028/"      // root check
    },
    new ServiceInfo
    {
        Name = "tfi-api",
        ProjectPath = @"..\tfi-api\tfi-api.csproj",
        HealthUrl = "http://localhost:5030/"      // root check
    },
    new ServiceInfo
    {
        Name = "carpark-sim",
        ProjectPath = @"..\carpark-sim\carpark-sim.csproj",
        HealthUrl = null                          // background worker, no HTTP check
    },
    new ServiceInfo
    {
        Name = "carpark-api",
        ProjectPath = @"..\carpark-api\carpark-api.csproj",
        HealthUrl = "http://localhost:5040/"      // root check
    }
};

Console.WriteLine("===============================================");
Console.WriteLine(" Travel to Hospital Advisor - Service Launcher ");
Console.WriteLine("===============================================");
Console.WriteLine("Working directory: " + Directory.GetCurrentDirectory());
Console.WriteLine();

// Start all services
foreach (var svc in services)
{
    LauncherHelpers.StartService(svc);
}

// Start health check loop in the background
_ = Task.Run(() => LauncherHelpers.HealthCheckLoopAsync(services));

// Handle Ctrl+C so we can stop all services cleanly
Console.CancelKeyPress += (sender, eventArgs) =>
{
    Console.WriteLine();
    Console.WriteLine("Stopping all services...");
    foreach (var svc in services)
    {
        LauncherHelpers.TryStopService(svc);
    }

    eventArgs.Cancel = false;
};

Console.WriteLine();
Console.WriteLine("All services started (or attempted).");
Console.WriteLine("Press Ctrl+C to stop everything.");
Console.WriteLine();

await Task.Delay(Timeout.Infinite);


// ------------------------------------------------
// EVERYTHING BELOW THIS POINT MUST BE DECLARATIONS
// ------------------------------------------------

// ===============================================
// Small model for each service we want to run
// ===============================================
class ServiceInfo
{
    public string Name { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string? HealthUrl { get; set; }          // null = no HTTP health check
    public Process? Process { get; set; }
}


// ===============================================
// Helper methods must be inside a class
// ===============================================
static class LauncherHelpers
{
    // -----------------------------------------------
    // Start a service
    // -----------------------------------------------
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
                {
                    Console.WriteLine($"[{svc.Name}] {e.Data}");
                }
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    Console.WriteLine($"[{svc.Name}][ERR] {e.Data}");
                }
            };

            process.Exited += (_, _) =>
            {
                Console.WriteLine($"[{svc.Name}] Process exited with code {process.ExitCode}");
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

    // -----------------------------------------------
    // Stop a service
    // -----------------------------------------------
    public static void TryStopService(ServiceInfo svc)
    {
        try
        {
            if (svc.Process == null)
                return;

            if (svc.Process.HasExited)
                return;

            Console.WriteLine($"[{svc.Name}] Stopping...");

            svc.Process.Kill(entireProcessTree: true);
            svc.Process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{svc.Name}] ERROR stopping service: {ex.Message}");
        }
        finally
        {
            svc.Process = null;
        }
    }

    // -----------------------------------------------
    // Health Check Loop
    // -----------------------------------------------
    public static async Task HealthCheckLoopAsync(List<ServiceInfo> services)
    {
        using var httpClient = new HttpClient();

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
                    var response = await httpClient.GetAsync(svc.HealthUrl);
                    if (response.IsSuccessStatusCode)
                        Console.WriteLine($"[health] {svc.Name}: OK");
                    else
                        Console.WriteLine($"[health] {svc.Name}: HTTP {(int)response.StatusCode}");
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
