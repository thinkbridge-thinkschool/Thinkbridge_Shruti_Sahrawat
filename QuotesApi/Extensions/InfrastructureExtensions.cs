using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuotesApi.Data;
using QuotesApi.Repositories;

namespace QuotesApi.Extensions;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        var connectionString = configuration.GetConnectionString("Default") ?? "Data Source=quotes.db";

        // Chosen explicitly via config rather than sniffed from the connection
        // string's shape - a "Server=..." substring is not a reliable enough
        // signal, and an explicit setting fails loudly (wrong provider, not a
        // silently wrong one) if it is ever missing where it matters.
        // Unset (local dev, and every environment before this one) keeps the
        // original SQLite behaviour - existing migrations, existing tests,
        // untouched.
        var provider = configuration["Database:Provider"] ?? "Sqlite";
        var useSqlServer = string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase);

        services.AddDbContext<QuotesDbContext>(options =>
        {
            if (useSqlServer)
            {
                options.UseSqlServer(connectionString);
            }
            else
            {
                options.UseSqlite(connectionString);
            }

            if (isDevelopment)
            {
                // Development only. EnableSensitiveDataLogging puts parameter
                // VALUES in the log, which would leak user data in production.
                options.LogTo(Console.WriteLine, LogLevel.Information)
                       .EnableSensitiveDataLogging()
                       .EnableDetailedErrors();
            }
        });

        services.AddScoped<IQuoteRepository, QuoteRepository>();
        return services;
    }
}
