using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Extensions;

public static class EndpointExtensions
{
    public static IEndpointRouteBuilder MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        // RequireAuthorization on the group, not on each endpoint.
        //
        // The difference matters the next time somebody adds a fifth endpoint
        // here: with the group protected, a new route is private unless it says
        // otherwise, and forgetting to think about auth leaves it locked. With
        // per-endpoint attributes, forgetting leaves it open to the internet -
        // and nothing fails, so nothing tells you.
        var group = app.MapGroup("/api/quotes").WithTags("Quotes").RequireAuthorization();

        group.MapGet("/", async (int page, int size, ClaimsPrincipal principal, IQuoteRepository repo, CancellationToken ct) =>
        {
            var userId = principal.UserId();
            if (userId is null) return Results.Unauthorized();

            page = page <= 0 ? 1 : page;
            size = size <= 0 ? 10 : Math.Min(size, 100);

            // An admin gets no filter and therefore sees everything, including
            // the un-owned rows from before accounts existed. Everyone else
            // sees exactly their own.
            var ownerFilter = principal.OwnerFilterFor(userId.Value);

            var (items, total) = await repo.GetPagedAsync(page, size, ownerFilter, ct);
            var result = new PagedResult<QuoteResponse>(items.Select(QuoteResponse.FromEntity).ToList(), page, size, total);
            return Results.Ok(result);
        });

        group.MapPost("/", async (CreateQuoteRequest request, ClaimsPrincipal principal, IQuoteRepository repo, IClock clock, CancellationToken ct) =>
        {
            var userId = principal.UserId();
            if (userId is null) return Results.Unauthorized();

            if (!RequestValidation.TryValidate(request, out var problem)) return problem;

            // The owner comes from the token, never from the request body.
            // CreateQuoteRequest has no owner field precisely so that there is
            // nothing for a client to set: the DTO's shape is the enforcement,
            // not a check somebody has to remember to write.
            var quote = Quote.Create(request.Author, request.Text, clock, userId.Value);
            var created = await repo.AddAsync(quote, ct);
            return Results.Created($"/api/quotes/{created.Id}", QuoteResponse.FromEntity(created));
        });

        group.MapGet("/{id:int}", async (int id, ClaimsPrincipal principal, IQuoteRepository repo, CancellationToken ct) =>
        {
            var userId = principal.UserId();
            if (userId is null) return Results.Unauthorized();

            var quote = await repo.GetByIdAsync(id, ct);

            // 404 for "not yours", not 403.
            //
            // 403 would confirm the quote exists, which turns /api/quotes/1,
            // /api/quotes/2, /api/quotes/3 into a way to count how much data
            // other people have and where the gaps are. From outside, "not
            // there" and "not yours" are deliberately indistinguishable.
            return quote is null || !principal.CanAccessQuoteOwnedBy(quote.OwnerId, userId.Value)
                ? Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." })
                : Results.Ok(QuoteResponse.FromEntity(quote));
        });

        group.MapDelete("/{id:int}", async (int id, ClaimsPrincipal principal, IQuoteRepository repo, CancellationToken ct) =>
        {
            var userId = principal.UserId();
            if (userId is null) return Results.Unauthorized();

            // Loaded before deleting, so the ownership check runs against the
            // row itself. A DELETE that went straight to the repository would
            // delete by id alone - and an id is the one part of this request
            // the caller fully controls.
            var quote = await repo.GetByIdAsync(id, ct);

            if (quote is null || !principal.CanAccessQuoteOwnedBy(quote.OwnerId, userId.Value))
            {
                return Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." });
            }

            var deleted = await repo.DeleteAsync(id, ct);
            return deleted
                ? Results.NoContent()
                : Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." });
        });

        return app;
    }
}
