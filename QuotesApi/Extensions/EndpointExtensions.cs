using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Caching;
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

        group.MapGet("/", async (int page, int size, string? author, ClaimsPrincipal principal, IQuoteRepository repo, CancellationToken ct) =>
        {
            var userId = principal.UserId();
            if (userId is null) return Results.Unauthorized();

            page = page <= 0 ? 1 : page;
            size = size <= 0 ? 10 : Math.Min(size, 100);

            // No owner filter for anyone, admin or not: every signed-in user
            // can see every quote, including the un-owned rows from before
            // accounts existed. Ownership only decides who may delete a row
            // (see CanAccessQuoteOwnedBy on the DELETE endpoint below) - it
            // has never had anything to do with who may look.
            var (items, total) = await repo.GetPagedAsync(page, size, ownerId: null, author, ct);
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

            // No ownership check here any more - the list endpoint already
            // hands every signed-in user every quote, so gating the detail
            // view by ownership would only mean the two disagreed: a card
            // in the list linking to a page that then 404s. 404 still means
            // exactly one thing now - the id does not exist.
            return quote is null
                ? Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." })
                : Results.Ok(QuoteResponse.FromEntity(quote));
        });

        group.MapDelete("/{id:int}", async (
            int id,
            ClaimsPrincipal principal,
            IQuoteRepository repo,
            ICollectionSummaryCacheInvalidator summaryCache,
            CancellationToken ct) =>
        {
            var userId = principal.UserId();
            if (userId is null) return Results.Unauthorized();

            // Loaded before deleting, so the ownership check runs against the
            // row itself. A DELETE that went straight to the repository would
            // delete by id alone - and an id is the one part of this request
            // the caller fully controls.
            var quote = await repo.GetByIdAsync(id, ct);

            if (quote is null)
            {
                return Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." });
            }

            // 403, not 404, for "exists but isn't yours" - unlike the GET
            // endpoint above, this one used to hide that distinction on
            // purpose, because confirming a quote existed to someone who
            // could not otherwise see it would leak information. That
            // reasoning no longer applies: every signed-in user can already
            // see this quote via GET /api/quotes or GET /api/quotes/{id}, so
            // there is nothing left to protect by pretending it is not
            // there, and a real 403 lets the client tell "you can't do that"
            // apart from "nothing to do".
            if (!principal.CanAccessQuoteOwnedBy(quote.OwnerId, userId.Value))
            {
                return Results.Problem(
                    title: "Not your quote",
                    detail: "You can only delete quotes you added yourself.",
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var deleted = await repo.DeleteAsync(id, ct);

            if (deleted)
            {
                // Day 21. A collection summary embeds the author and text of
                // the quotes in its preview, and GetCollectionSummariesHandler
                // drops preview entries whose quote no longer exists. So
                // deleting a quote changes the correct answer for any cached
                // summary that was previewing it - and without this the
                // deleted quote keeps appearing on the collections screen
                // until the entry expires.
                //
                // POST deliberately does not do this. A newly created quote is
                // in no collection yet, so it cannot appear in any preview,
                // and invalidating the whole summary cache on every quote
                // creation would throw away a warm cache for a write that
                // provably cannot have changed what it holds.
                await summaryCache.InvalidateAsync(ct);
            }

            return deleted
                ? Results.NoContent()
                : Results.NotFound(new ProblemDetails { Title = "Quote not found", Status = 404, Detail = $"No quote with id {id}." });
        });

        return app;
    }
}
