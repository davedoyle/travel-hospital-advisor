using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

// CORS so the frontend admin page can talk to this service from localhost
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAdminFrontend", policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod());
});



// Simple token store kept in memory for this demo
builder.Services.AddSingleton<TokenStore>();

var app = builder.Build();

app.UseCors("AllowAdminFrontend");
app.MapGet("/", () => "Admin API for Travel to Hospital Advisor");
// Read DB path from configuration
string? dbPathFromConfig = app.Configuration["DatabasePath"];
string dbPath = dbPathFromConfig ?? "../../../travel_dbs/THA_DB.db";

// -----------------------------------------------------------------------------
// Authentication middleware
// Any /admin/* endpoint except /admin/login must have a valid x-admin-auth token
// -----------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;

    // Only guard /admin/* endpoints, except login
    if (path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) &&
        !path.Equals("/admin/login", StringComparison.OrdinalIgnoreCase))
    {
        var tokenStore = context.RequestServices.GetRequiredService<TokenStore>();

        if (!context.Request.Headers.TryGetValue("x-admin-auth", out var tokenValues) ||
            string.IsNullOrWhiteSpace(tokenValues.FirstOrDefault()) ||
            !tokenStore.IsValid(tokenValues.First()!))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Missing or invalid admin token");
            return;
        }
    }

    await next();
});

// -----------------------------------------------------------------------------
// Helper: create SQLite connection
// -----------------------------------------------------------------------------
SqliteConnection CreateConnection()
{
    var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();
    return conn;
}

// -----------------------------------------------------------------------------
// POST /admin/login
// -----------------------------------------------------------------------------
app.MapPost("/admin/login", async (LoginRequest request, TokenStore tokenStore) =>
{
    await using var conn = CreateConnection();

    const string sql = @"
        SELECT admin_id, password_hash
        FROM admin_user
        WHERE username = @username;
    ";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@username", request.Username);

    await using var reader = await cmd.ExecuteReaderAsync();

    if (!await reader.ReadAsync())
    {
        return Results.Unauthorized();
    }

    string storedHash = reader.GetString(1);

    if (!string.Equals(storedHash, request.PasswordHash, StringComparison.OrdinalIgnoreCase))
    {
        return Results.Unauthorized();
    }

    string token = Guid.NewGuid().ToString("N");
    tokenStore.AddToken(token, request.Username);

    reader.Close();
    const string updateSql = @"
        UPDATE admin_user
        SET last_login = CURRENT_TIMESTAMP
        WHERE username = @username;
    ";

    await using var updateCmd = conn.CreateCommand();
    updateCmd.CommandText = updateSql;
    updateCmd.Parameters.AddWithValue("@username", request.Username);
    await updateCmd.ExecuteNonQueryAsync();

    var response = new LoginResponse(token, request.Username);
    return Results.Ok(response);
});

// -----------------------------------------------------------------------------
// GET /admin/carparks
// -----------------------------------------------------------------------------
    app.MapGet("/admin/carparks", async () =>
    {
        var result = new List<AdminCarpark>();

        await using var conn = CreateConnection();

        const string sql = @"
             SELECT
                c.carpark_id,
                c.heorg_id,
                c.carpark_name,
                c.total_spaces,
                c.status,
                IFNULL(c.occupied_spaces, 0) AS used,
                IFNULL(c.last_updated, '') AS last_updated,
                c.is_active
            FROM carpark c
            WHERE c.is_active = 1
            ORDER BY c.carpark_name;
                ";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var carpark = new AdminCarpark(
            CarparkId: reader.GetInt32(0),

            // heorg_id is a TEXT column (CUH, SFH), so read it as string
            HospitalCode: reader.IsDBNull(1) ? "" : reader.GetString(1),

            // no hospital_name column in DB yet, leave empty
            HospitalName: "",

            Name: reader.GetString(2),
            Capacity: reader.GetInt32(3),
            Status: reader.GetString(4),
            Used: reader.GetInt32(5),
            LastUpdated: reader.IsDBNull(6) ? "" : reader.GetString(6),
            IsActive: reader.GetInt32(7)
        );

            result.Add(carpark);
        }

        return Results.Ok(result);
});


// -----------------------------------------------------------------------------
// POST /admin/carparks    (add new)
// -----------------------------------------------------------------------------
app.MapPost("/admin/carparks", async (HttpContext ctx, AddCarparkRequest req, TokenStore tokenStore) =>
{
    Console.WriteLine($"DEBUG ADD: '{req.HeorgId}' '{req.CarparkName}' '{req.TotalSpaces}' '{req.Status}'");

    await using var conn = CreateConnection();

    // Get the admin token from headers
    var token = ctx.Request.Headers["x-admin-auth"].FirstOrDefault();
    var username = tokenStore.GetUsername(token) ?? "unknown";

    const string sql = @"
        
        INSERT INTO carpark (
            heorg_id, 
            carpark_name, 
            total_spaces,
            occupied_spaces,
            status,
            last_updated,
            last_modified_by,
            is_active
        )
        SELECT 
            @heorg_id,
            @name,
            @total,
            0,
            @status,
            CURRENT_TIMESTAMP,
            au.admin_id,
            1
        FROM admin_user au
        WHERE au.username = @user;
    ";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    cmd.Parameters.AddWithValue("@heorg_id", req.HeorgId);
    cmd.Parameters.AddWithValue("@name", req.CarparkName);
    cmd.Parameters.AddWithValue("@total", req.TotalSpaces);
    cmd.Parameters.AddWithValue("@status", req.Status);
    cmd.Parameters.AddWithValue("@user", username);

    var rows = await cmd.ExecuteNonQueryAsync();
    return rows == 1
        ? Results.Ok(new { message = "Carpark added." })
        : Results.Problem("Insert failed.");
});


// -----------------------------------------------------------------------------
// PUT /admin/carparks/{id}/archive   (soft delete)
// -----------------------------------------------------------------------------
app.MapPut("/admin/carparks/{id:int}/archive", async (HttpContext ctx, int id, TokenStore tokenStore) =>
{
    // pull username from token (same approach as add/edit)
    var token = ctx.Request.Headers["x-admin-auth"].FirstOrDefault();
    var username = tokenStore.GetUsername(token) ?? "admin";

    await using var conn = CreateConnection();

    // same trick as before – join to admin_user so FK resolves correctly
    const string sql = @"
        UPDATE carpark
        SET 
            is_active = 0,
            last_updated = CURRENT_TIMESTAMP,
            last_modified_by = (
                SELECT au.admin_id 
                FROM admin_user au 
                WHERE au.username = @user
            )
        WHERE carpark_id = @id 
          AND is_active = 1;
    ";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;

    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@user", username);

    var rows = await cmd.ExecuteNonQueryAsync();

    return rows == 1
        ? Results.Ok(new { message = "Carpark archived." })
        : Results.Problem("Archive failed or carpark not found.");
});


// -----------------------------------------------------------------------------
// PUT /admin/carparks/{id}   (edit basic fields)
// -----------------------------------------------------------------------------
app.MapPut("/admin/carparks/{id:int}", async (HttpContext ctx, int id, EditCarparkRequest req, TokenStore tokenStore) =>
{
    // get username from token
    var token = ctx.Request.Headers["x-admin-auth"].FirstOrDefault();
    var username = tokenStore.GetUsername(token) ?? "admin";

    await using var conn = CreateConnection();

    // use same FK-friendly pattern as add/archive
    const string sql = @"
        UPDATE carpark
        SET
            heorg_id = @heorg_id,
            carpark_name = @name,
            total_spaces = @total,
            status = @status,
            last_updated = CURRENT_TIMESTAMP,
            last_modified_by = (
                SELECT admin_id
                FROM admin_user
                WHERE username = @user
            )
        WHERE carpark_id = @id
          AND is_active = 1;
    ";

    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;

    cmd.Parameters.AddWithValue("@heorg_id", req.HeorgId);
    cmd.Parameters.AddWithValue("@name", req.CarparkName);
    cmd.Parameters.AddWithValue("@total", req.TotalSpaces);
    cmd.Parameters.AddWithValue("@status", req.Status);
    cmd.Parameters.AddWithValue("@user", username);
    cmd.Parameters.AddWithValue("@id", id);

    var rows = await cmd.ExecuteNonQueryAsync();

    return rows == 1
        ? Results.Ok(new { message = "Carpark updated." })
        : Results.Problem("Update failed or carpark not found.");
});



// -----------------------------------------------------------------------------
// SIMULATION CONTROL - PROXY TO CARPARK-SIM API (port 5070)
// -----------------------------------------------------------------------------

const string simBaseUrl = "http://localhost:5070";

// Helper to call sim API with proper admin token
async Task<IResult> ProxyToSim(HttpRequest request, string simEndpoint)
{
    try
    {
        using var client = new HttpClient();

        // Pass the admin token along
        if (request.Headers.TryGetValue("x-admin-auth", out var token))
        {
            client.DefaultRequestHeaders.Add("x-admin-auth", token.ToString());
        }

        var response = await client.PostAsync($"{simBaseUrl}{simEndpoint}", null);
        var json = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode)
            return Results.Content(json, "application/json");

        return Results.Problem($"Simulation service returned error: {response.StatusCode}");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error contacting simulation: {ex.Message}");
    }
}

// START
app.MapPost("/admin/sim/start", async (HttpRequest req) =>
    await ProxyToSim(req, "/sim/start")
);

// PAUSE
app.MapPost("/admin/sim/pause", async (HttpRequest req) =>
    await ProxyToSim(req, "/sim/pause")
);

// SINGLE TICK
app.MapPost("/admin/sim/tick", async (HttpRequest req) =>
    await ProxyToSim(req, "/sim/tick")
);

// FAST FORWARD
app.MapPost("/admin/sim/fastforward", async (HttpRequest req) =>
    await ProxyToSim(req, "/sim/fastforward")
);

// RESET
app.MapPost("/admin/sim/reset", async (HttpRequest req) =>
    await ProxyToSim(req, "/sim/reset")
);




// -----------------------------------------------------------------------------
// Stub admin endpoints — these are placeholders
// -----------------------------------------------------------------------------

app.MapPut("/admin/carparks/{id:int}/status", (int id) =>
{
    return Results.Ok(new { message = $"Change status for carpark {id} not implemented yet." });
});



// -----------------------------------------------------------------------------
// System info endpoint
// -----------------------------------------------------------------------------
app.MapGet("/admin/system/info", async () =>
{
    // read DB counts
    int carparkCount = 0;
    int adminUserCount = 0;

    await using var conn = CreateConnection();

    // carparks
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM carpark;";
        carparkCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // admin users
    await using (var cmd = conn.CreateCommand())
    {
        cmd.CommandText = "SELECT COUNT(*) FROM admin_user;";
        adminUserCount = Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    // pull launcher info
    using var http = new HttpClient();
    var launcherJson = await http.GetStringAsync("http://localhost:5199/");
    var launcherObj = System.Text.Json.JsonDocument.Parse(launcherJson).RootElement;

    // shape a nice response
    var info = new
    {
        dbPath = dbPath,
        carparkCount,
        adminUserCount,

        launcher = new
        {
            startTimeUtc = launcherObj.GetProperty("startTimeUtc").GetDateTime(),
            mainDb = launcherObj.GetProperty("mainDb").GetString(),
            tfiDb = launcherObj.GetProperty("tfiDb").GetString(),
            adminPort = launcherObj.GetProperty("adminPort").GetInt32(),
            weatherPort = launcherObj.GetProperty("weatherPort").GetInt32(),
            tfiPort = launcherObj.GetProperty("tfiPort").GetInt32(),
            carparkPort = launcherObj.GetProperty("carparkPort").GetInt32(),
            weatherStatus = launcherObj.GetProperty("weatherStatus").GetString(),
            tfiStatus = launcherObj.GetProperty("tfiStatus").GetString(),
            simStatus = launcherObj.GetProperty("simStatus").GetString(),
            lastWeather = launcherObj.GetProperty("lastWeather").GetString(),
            lastTfi = launcherObj.GetProperty("lastTfi").GetString()
        }
    };

    return Results.Ok(info);
});

app.Run($"http://localhost:5050");


// ============================================================================
// TYPES
// ============================================================================

public record LoginRequest(string Username, string PasswordHash);


public record AddCarparkRequest(
    string HeorgId,
    string CarparkName,
    int TotalSpaces,
    string Status
);

public record EditCarparkRequest(
    string HeorgId,
    string CarparkName,
    int TotalSpaces,
    string Status
);

public record LoginResponse(string Token, string Username);

public record AdminCarpark(
    int CarparkId,
    string HospitalCode,
    string HospitalName,
    string Name,
    int Capacity,
    string Status,
    int Used,
    string LastUpdated,
    int IsActive
);

public record SystemInfoResponse(
    string DbPath,
    int CarparkCount,
    int AdminUserCount,
    DateTime ServerTime
);

public class TokenStore
{
    private readonly ConcurrentDictionary<string, string> _tokens = new();

    public void AddToken(string token, string username)
    {
        _tokens[token] = username;
    }

    public bool IsValid(string token)
    {
        return _tokens.ContainsKey(token);
    }

    public string? GetUsername(string token)
    {
        _tokens.TryGetValue(token, out var name);
        return name;
    }

}
