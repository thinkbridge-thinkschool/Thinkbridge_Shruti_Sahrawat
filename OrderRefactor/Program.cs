using OrderRefactor.Authentication;
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

// Jwt:Key is deliberately absent from appsettings.json. A signing key committed
// to the repository is a signing key every past and future contributor holds,
// and rotating it means a release rather than a config change.
//
// The options binding above already carries [Required] + [MinLength(32)] and is
// wired with ValidateOnStart, but that validation runs after this line, and
// SymmetricSecurityKey throws an unhelpful "key length must be greater than 0"
// on an empty string. Failing here says what to actually do about it.
if (string.IsNullOrWhiteSpace(jwtOptions.Key) || jwtOptions.Key.Length < 32)
{
    throw new InvalidOperationException(
        "Jwt:Key is missing or shorter than 32 characters.\n" +
        "Local development:  dotnet user-secrets set \"Jwt:Key\" \"<32+ characters>\" --project OrderRefactor\n" +
        "Tests:              supplied in-memory by the WebApplicationFactory setup.\n" +
        "Production:         a Key Vault reference exposed as the Jwt__Key environment variable.");
}

var keyBytes = Encoding.UTF8.GetBytes(jwtOptions.Key);

// Scheme names live on IssuerSchemeSelector so the router and the registrations
// cannot drift apart.
const string InternalSchemeName = IssuerSchemeSelector.InternalScheme;
const string EntraSchemeName = IssuerSchemeSelector.EntraScheme;
const string PolicySchemeName = IssuerSchemeSelector.PolicyScheme;

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
        // Peeks at the token's issuer and forwards to whichever validator owns it.
        // The decision itself lives in IssuerSchemeSelector, which is unit tested
        // across every branch without booting the app or touching the network.
        // The internal issuer is passed from the same options object the internal
        // validator uses for ValidIssuer, so the two cannot disagree.
        options.ForwardDefaultSelector = context =>
            IssuerSchemeSelector.SelectScheme(
                context.Request.Headers.Authorization.FirstOrDefault(),
                jwtOptions.Issuer);
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