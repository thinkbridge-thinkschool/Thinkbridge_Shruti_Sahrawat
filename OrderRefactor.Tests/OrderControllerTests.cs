using System.Data.Common;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OrderRefactor.Data;
using OrderRefactor.Models;
using Xunit;

namespace OrderRefactor.Tests;

public class OrderControllerTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly DbConnection _connection;
    private readonly string _jwtKey;
    private readonly string _jwtIssuer = "OrderRefactorIssuer";
    private readonly string _jwtAudience = "OrderRefactorAudience";

    public OrderControllerTests(WebApplicationFactory<Program> factory)
    {
        // Open an in-memory SQLite connection that persists for the test lifetime
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        // Same key the module initializer put in the environment for the host,
        // so tokens minted here validate against tokens the host expects.
        _jwtKey = TestJwt.Key;

        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Remove existing DbContext registration
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<OrdersDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                // Re-register DbContext using the SQLite in-memory connection
                services.AddDbContext<OrdersDbContext>(options =>
                {
                    options.UseSqlite(_connection);
                });
            });
        });
    }

    [Fact]
    public async Task CreateOrder_WithTwoItems_IncludesBothItemsInTotal()
    {
        var client = _factory.CreateClient();

        // CreateOrder now requires the AdminOnly policy, so this test needs a valid admin token.
        // The behaviour under test is the item count and total, not authentication.
        var adminToken = GenerateJwtToken(email: "grace@example.com", hasAdminClaim: true);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {adminToken}");

        var request = new CreateOrderRequest
        {
            CustomerName = "Grace Hopper",
            CustomerEmail = "grace@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Compiler Manual", Price = 25.00m, Quantity = 2 },
                new() { ProductName = "Debugging Kit", Price = 15.50m, Quantity = 1 }
            }
        };

        var response = await client.PostAsJsonAsync("/api/orders", request);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<OrderResponse>();

        Assert.NotNull(result);
        Assert.Equal(2, result!.ItemCount);
        Assert.True(result.Total > 65m);
    }

    /// <summary>
    /// Test 1: Anonymous request (no token) -> 401 Unauthorized
    /// </summary>
    [Fact]
    public async Task CreateOrder_WithoutToken_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();

        var request = new CreateOrderRequest
        {
            CustomerName = "Test User",
            CustomerEmail = "test@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Item", Price = 10.00m, Quantity = 1 }
            }
        };

        var response = await client.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Test 2: Valid token but missing "admin" claim -> 403 Forbidden
    /// </summary>
    [Fact]
    public async Task CreateOrder_WithValidTokenButNoAdminClaim_Returns403Forbidden()
    {
        var client = _factory.CreateClient();

        var token = GenerateJwtToken(email: "user@example.com", hasAdminClaim: false);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        var request = new CreateOrderRequest
        {
            CustomerName = "Test User",
            CustomerEmail = "user@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Item", Price = 10.00m, Quantity = 1 }
            }
        };

        var response = await client.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(System.Net.HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Test 3: Valid token WITH admin claim -> 201 Created
    /// </summary>
    [Fact]
    public async Task CreateOrder_WithValidTokenAndAdminClaim_Returns201Created()
    {
        var client = _factory.CreateClient();

        var token = GenerateJwtToken(email: "admin@example.com", hasAdminClaim: true);
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        var request = new CreateOrderRequest
        {
            CustomerName = "Admin User",
            CustomerEmail = "admin@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Item", Price = 10.00m, Quantity = 1 }
            }
        };

        var response = await client.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// Test 4: Expired token -> 401 Unauthorized
    /// </summary>
    [Fact]
    public async Task CreateOrder_WithExpiredToken_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();

        var token = GenerateExpiredJwtToken();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {token}");

        var request = new CreateOrderRequest
        {
            CustomerName = "Test User",
            CustomerEmail = "test@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Item", Price = 10.00m, Quantity = 1 }
            }
        };

        var response = await client.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Test 5: Malformed token -> 401 Unauthorized
    /// </summary>
    [Fact]
    public async Task CreateOrder_WithMalformedToken_Returns401Unauthorized()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer invalid.token.here");

        var request = new CreateOrderRequest
        {
            CustomerName = "Test User",
            CustomerEmail = "test@example.com",
            Items = new List<CreateOrderItemRequest>
            {
                new() { ProductName = "Item", Price = 10.00m, Quantity = 1 }
            }
        };

        var response = await client.PostAsJsonAsync("/api/orders", request);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ============= Helper Methods =============

    private string GenerateJwtToken(string email, bool hasAdminClaim)
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtKey);

        var claims = new List<System.Security.Claims.Claim>
        {
            new("unique_name", email),
        };

        if (hasAdminClaim)
        {
            claims.Add(new("admin", "true"));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(15),
            Issuer = _jwtIssuer,
            Audience = _jwtAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private string GenerateExpiredJwtToken()
    {
        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtKey);
        var now = DateTime.UtcNow;

        // Issued two hours ago, expired one hour ago.
        // NotBefore must be set explicitly, otherwise it defaults to "now",
        // which would be after Expires and the library refuses to build the token.
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new System.Security.Claims.ClaimsIdentity(new[]
            {
                new System.Security.Claims.Claim("unique_name", "expired@example.com"),
                new System.Security.Claims.Claim("admin", "true")
            }),
            NotBefore = now.AddHours(-2),
            IssuedAt = now.AddHours(-2),
            Expires = now.AddHours(-1),
            Issuer = _jwtIssuer,
            Audience = _jwtAudience,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(key),
                SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}