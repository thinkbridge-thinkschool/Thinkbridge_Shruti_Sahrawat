using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quotes").WithTags("Quotes");

        group.MapGet("/", async (int page, int size, IQuoteRepository repo, CancellationToken ct) =>
        {
            page = page <= 0 ? 1 : page;
            size = size <= 0 ? 10 : Math.Min(size, 100);

            var (items, total) = await repo.GetPagedAsync(page, size, ct);
            var result = new PagedResult<QuoteResponse>(items.Select(QuoteResponse.FromEntity).ToList(), page, size, total);
            return Results.Ok(result);
        });

        group.MapPost("/", async (CreateQuoteRequest request, IQuoteRepository repo, IClock clock, CancellationToken ct) =>
        {
            var results = new List<ValidationResult>();
            var validationContext = new ValidationContext(request);
            if (!Validator.TryValidateObject(request, validationContext, results, validateAllProperties: true))
            {
                var errors = results
                    .SelectMany(r => r.MemberNames.DefaultIfEmpty(""), (r, member) => (member, r.ErrorMessage))
                    .GroupBy(x => x.member)
                    .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorMessage ?? "Invalid value").ToArray());
                return Results.ValidationProblem(errors);
            }

            var quote = Quote.Create(request.Author, request.Text, clock);
            var created = await repo.AddAsync(quote, ct);
            return Results.Created($"/api/quotes/{created.Id}", QuoteResponse.FromEntity(created));
        });

        group.MapGet("/{id:int}", async (int id, IQuoteRepository repo, CancellationToken ct) =>
        {
            var quote = await repo.GetByIdAsync(id, ct);
            return quote is null
                ? Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." })
                : Results.Ok(QuoteResponse.FromEntity(quote));
        });

        group.MapDelete("/{id:int}", async (int id, IQuoteRepository repo, CancellationToken ct) =>
        {
            var deleted = await repo.DeleteAsync(id, ct);
            return deleted
                ? Results.NoContent()
                : Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." });
        });

        return app;
    }
}