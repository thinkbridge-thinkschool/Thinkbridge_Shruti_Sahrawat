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

        services.AddDbContext<QuotesDbContext>(options =>
        {
            options.UseSqlite(connectionString);

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
