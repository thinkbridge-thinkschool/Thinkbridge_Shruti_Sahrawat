using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using QuotesApi.Data;

namespace Quotes.Benchmark;

// Demonstrates what EF actually sends to the database: a whole-entity query,
// the same query rewritten as a projection, and an accidental client-side
// evaluation.
public static class Projections
{
    public static void Run()
    {
        var dbPath = DbLocator.Find();

        QuotesDbContext New() =>
            new(new DbContextOptionsBuilder<QuotesDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .LogTo(Console.WriteLine, new[] { DbLoggerCategory.Database.Command.Name },
                       LogLevel.Information)
                .EnableSensitiveDataLogging()
                .Options);

        Console.WriteLine("========================================================");
        Console.WriteLine(" 1. WHOLE ENTITY - pulls every column");
        Console.WriteLine("========================================================");

        using (var ctx = New())
        {
            var rows = ctx.Quotes
                .Where(q => !q.IsDeleted)
                .OrderBy(q => q.Id)
                .Take(5)
                .ToList();

            Console.WriteLine($"\n--> {rows.Count} entities materialised\n");
        }

        Console.WriteLine("========================================================");
        Console.WriteLine(" 2. PROJECTION - only the columns actually used");
        Console.WriteLine("========================================================");

        using (var ctx = New())
        {
            var rows = ctx.Quotes
                .Where(q => !q.IsDeleted)
                .OrderBy(q => q.Id)
                .Take(5)
                .Select(q => new QuoteSummary(q.Id, q.Author, q.CreatedAt))
                .ToList();

            Console.WriteLine($"\n--> {rows.Count} DTOs materialised\n");
        }

        Console.WriteLine("========================================================");
        Console.WriteLine(" 3. ACCIDENTAL CLIENT EVALUATION");
        Console.WriteLine("========================================================");
        Console.WriteLine("\n-- BROKEN: AsEnumerable() before the Where --");

        using (var ctx = New())
        {
            // AsEnumerable() ends the IQueryable. Everything after it runs in
            // LINQ-to-Objects on the client, so the filter never reaches SQL.
            var rows = ctx.Quotes
                .AsEnumerable()
                .Where(q => q.Author.StartsWith("Author 1"))
                .Take(5)
                .ToList();

            Console.WriteLine($"\n--> {rows.Count} rows returned to the caller");
            Console.WriteLine("    but look at the SQL above: no WHERE, no LIMIT.");
        }

        Console.WriteLine("\n-- FIXED: filter stays in the IQueryable --");

        using (var ctx = New())
        {
            var rows = ctx.Quotes
                .Where(q => q.Author.StartsWith("Author 1"))
                .Take(5)
                .Select(q => new QuoteSummary(q.Id, q.Author, q.CreatedAt))
                .ToList();

            Console.WriteLine($"\n--> {rows.Count} rows, filtered and limited in SQL");
        }

        Console.WriteLine("\n========================================================");
        Console.WriteLine(" 4. ROW COUNTS - what each query actually pulled back");
        Console.WriteLine("========================================================");

        using (var ctx = New())
        {
            var total = ctx.Quotes.Count();
            var matching = ctx.Quotes.Count(q => q.Author.StartsWith("Author 1"));
            Console.WriteLine($"\nTable holds {total} rows.");
            Console.WriteLine($"{matching} match the filter.");
            Console.WriteLine($"The client-eval version transferred all {total} to return 5.");
        }
    }
}

public record QuoteSummary(int Id, string Author, DateTime CreatedAt);