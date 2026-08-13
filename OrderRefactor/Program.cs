using System.IdentityModel.Tokens.Jwt;
using OrderRefactor.Configuration;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OrderRefactor.Data;
using OrderRefactor.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);

// Configure JWT Authentication
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var jwtOptions = builder.Configuration
    .GetSection(JwtOptions.SectionName).Get<JwtOptions>()
    ?? throw new InvalidOperationException("Jwt configuration section is missing");

var jwtKey = jwtOptions.Key;
var keyBytes = Encoding.UTF8.GetBytes(jwtKey);

// Define scheme names
const string InternalSchemeName = "InternalJwt";
const string EntraSchemeName = "EntraJwt";
const string PolicySchemeName = "PolicyScheme";

var azureAdTenantId = builder.Configuration["AzureAd:TenantId"];
var azureAdClientId = builder.Configuration["AzureAd:ClientId"];
var azureAdAudience = builder.Configuration["AzureAd:Audience"];

// Register two schemes + policy scheme
builder.Services.AddAuthentication(PolicySchemeName)
    .AddJwtBearer(InternalSchemeName, options =>
    {
        // Your Day 2 logic, unchanged
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidAudience = jwtOptions.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes)
        };
    })
    .AddJwtBearer(EntraSchemeName, options =>
    {
        // Entra (Azure AD) configuration
        options.Authority = $"https://login.microsoftonline.com/{azureAdTenantId}/v2.0";
        options.Audience = azureAdAudience;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidateIssuer = true,
            ValidateLifetime = true
        };
    })
    .AddPolicyScheme(PolicySchemeName, "Policy Scheme", options =>
    {
        // This scheme peeks at the token and forwards to either InternalJwt or EntraJwt
        options.ForwardDefaultSelector = context =>
        {
            var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer "))
                return InternalSchemeName; // Default fallback

            var token = authHeader.Substring("Bearer ".Length).Trim();
            
            try
            {
                var handler = new JwtSecurityTokenHandler();
                var jwt = handler.ReadJwtToken(token);
                var issuer = jwt.Issuer;

                if (issuer == "OrderRefactorIssuer")
                    return InternalSchemeName;
                
                if (issuer?.StartsWith("https://login.microsoftonline.com/") == true)
                    return EntraSchemeName;
            }
            catch
            {
                // If token is malformed or unreadable, let default handler deal with it
            }

            return InternalSchemeName; // Safe default
        };
    });

// Add Authorization with Policies
builder.Services.AddAuthorization(options =>
{
    // Policy 1: User must have "admin" claim
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireClaim("admin", "true"));
    
    // Policy 2: User can only access their own orders
    options.AddPolicy("CanEditOwnOrders", policy =>
        policy.RequireAssertion(context =>
        {
            var userEmail = context.User.FindFirst("unique_name")?.Value;
            var requestUserId = context.Resource as string;
            return userEmail == requestUserId;
        }));
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
    db.Database.EnsureCreated();
}

// Enable Authentication and Authorization middleware
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();

// Required so WebApplicationFactory in OrderRefactor.Tests can access Program
public partial class Program { }