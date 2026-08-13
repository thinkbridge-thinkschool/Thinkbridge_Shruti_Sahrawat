using Serilog;
using QuotesApi.Services;
using QuotesApi.Repositories;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Middleware;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}      TraceId={TraceId} {Message:lj}{NewLine}{Exception}"));

builder.Services.AddControllers();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddScoped<ICollectionRepository, CollectionRepository>();
builder.Services.AddSingleton<IClock, SystemClock>();

var app = builder.Build();

app.UseSerilogRequestLogging();
app.UseCorrelationId();
app.UseExceptionHandling();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<QuotesDbContext>();
    db.Database.Migrate();
}

app.MapQuoteEndpoints();
app.MapControllers();

app.Run();