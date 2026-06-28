using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace FunctionApp;

public class TicketFunction
{
    private readonly ILogger<TicketFunction> _logger;

    public TicketFunction(ILogger<TicketFunction> logger)
    {
        _logger = logger;
    }

    // POST /api/BookTicket
    // Body: { "customerName": "John" }
    [Function("BookTicket")]
    public async Task<HttpResponseData> BookTicket(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post")] HttpRequestData req)
    {
        var body = await JsonSerializer.DeserializeAsync<BookingRequest>(
            req.Body,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (string.IsNullOrWhiteSpace(body?.CustomerName))
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("customerName is required");
            return bad;
        }

        await using var conn = await GetConnectionAsync();
        await EnsureTableAsync(conn);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Tickets (CustomerName) VALUES (@name)";
        cmd.Parameters.AddWithValue("@name", body.CustomerName);
        await cmd.ExecuteNonQueryAsync();

        _logger.LogInformation("Booked ticket for {Customer}", body.CustomerName);

        var ok = req.CreateResponse(HttpStatusCode.OK);
        await ok.WriteStringAsync("Booking confirmed");
        return ok;
    }

    // GET /api/GetTickets
    [Function("GetTickets")]
    public async Task<HttpResponseData> GetTickets(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get")] HttpRequestData req)
    {
        await using var conn = await GetConnectionAsync();
        await EnsureTableAsync(conn);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, CustomerName, BookedAt FROM Tickets ORDER BY BookedAt DESC";

        var tickets = new List<Ticket>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tickets.Add(new Ticket(
                reader.GetInt32(0),
                reader.GetString(1),
                reader.GetDateTime(2).ToString("yyyy-MM-dd HH:mm:ss")
            ));
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(tickets);
        return response;
    }

    private static async Task<SqlConnection> GetConnectionAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("SqlConnectionString")
            ?? throw new InvalidOperationException("SqlConnectionString not configured");

        // Connection string already contains Authentication=Active Directory Managed Identity
        // SqlClient handles the token acquisition internally — no manual token needed
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task EnsureTableAsync(SqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'Tickets')
            CREATE TABLE Tickets (
                Id           INT IDENTITY(1,1) PRIMARY KEY,
                CustomerName NVARCHAR(100)     NOT NULL,
                BookedAt     DATETIME2         NOT NULL DEFAULT GETUTCDATE()
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private record BookingRequest(string CustomerName);
    private record Ticket(int Id, string CustomerName, string BookedAt);
}
