using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;

namespace QuotesApi.Extensions;

// Day 11 profiling target. Two endpoints returning identical data: one written
// with the anti-patterns the exercise is about, one written correctly.
public static class ProfilingEndpoints
{
    public static IEndpointRouteBuilder MapProfilingEndpoints(this IEndpointRouteBuilder app)
    {
        // SLOW: N+1 over authors, and every inner query filters on Author,
        // which has no index. One query for the author list, then one full
        // table scan per author.
        app.MapGet("/api/profiling/author-stats-slow",
            async (QuotesDbContext db, CancellationToken ct) =>
        {
            var authors = await db.Quotes
                .Where(q => !q.IsDeleted)
                .Select(q => q.Author)
                .Distinct()
                .ToListAsync(ct);

            var results = new List<AuthorStats>();

            foreach (var author in authors)
            {
                // Executes once per author. No index on Author, so each of
                // these is a full scan of the table.
                var quotes = await db.Quotes
                    .Where(q => q.Author == author && !q.IsDeleted)
                    .ToListAsync(ct);

                results.Add(new AuthorStats(
                    author,
                    quotes.Count,
                    quotes.Max(q => q.CreatedAt)));
            }

            return Results.Ok(results.OrderByDescending(r => r.QuoteCount).Take(20));
        });

        // FAST: one query, aggregated in the database. The GroupBy projects to
        // an anonymous type - EF cannot translate a record constructor inside
        // the Select, so the DTO is built after materialisation.
        app.MapGet("/api/profiling/author-stats-fast",
            async (QuotesDbContext db, CancellationToken ct) =>
        {
            var rows = await db.Quotes
                .Where(q => !q.IsDeleted)
                .GroupBy(q => q.Author)
                .Select(g => new
                {
                    Author = g.Key,
                    QuoteCount = g.Count(),
                    MostRecent = g.Max(q => q.CreatedAt)
                })
                .OrderByDescending(r => r.QuoteCount)
                .Take(20)
                .ToListAsync(ct);

            var results = rows
                .Select(r => new AuthorStats(r.Author, r.QuoteCount, r.MostRecent))
                .ToList();

            return Results.Ok(results);
        });

        return app;
    }
}

public record AuthorStats(string Author, int QuoteCount, DateTime MostRecent);