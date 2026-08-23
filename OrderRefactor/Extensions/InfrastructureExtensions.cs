using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OrderRefactor.Data;
using OrderRefactor.Repositories;
using OrderRefactor.Services;

namespace OrderRefactor.Extensions;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default") ?? "Data Source=orders.db";
        services.AddDbContext<OrdersDbContext>(options =>
            options.UseSqlite(connectionString)
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        // Scoped: one per request. DbContext is not thread-safe and its change
        // tracker is per-unit-of-work, so anything that touches it inherits the
        // same lifetime.
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IOrderService, OrderService>();

        // Transient: stateless, cheap, and nothing is gained by sharing an
        // instance across a request.
        services.AddTransient<IDiscountCalculator, DiscountCalculator>();

        // Singleton: genuinely stateless and cross-cutting. A singleton holding
        // a scoped dependency would be the classic captive-dependency bug;
        // this one holds nothing at all.
        services.AddSingleton<IClock, SystemClock>();

        return services;
    }
}
