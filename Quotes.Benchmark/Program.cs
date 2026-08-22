using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

// Locates QuotesApi/quotes.db by walking up from the current assembly location
// until it finds it. BenchmarkDotNet runs from a generated subdirectory whose
// depth is not predictable, so counting ".." segments does not work.
public static class DbLocator
{
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null &&
               !File.Exists(Path.Combine(dir.FullName, "QuotesApi", "quotes.db")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
            throw new FileNotFoundException(
                $"Could not locate QuotesApi/quotes.db walking up from {AppContext.BaseDirectory}");

        return Path.Combine(dir.FullName, "QuotesApi", "quotes.db");
    }

    public static QuotesDbContext NewContext(string path) =>
        new(new DbContextOptionsBuilder<QuotesDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options);
}

// A fresh DbContext per iteration, deliberately. Reusing one would leave the
// identity map populated after the first iteration, so later runs would measure
// a warm cache rather than the cost of tracking. Context creation is identical
// in both variants, so including it raises both numbers without distorting the
// comparison.
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 15)]
public class TrackingBenchmarks
{
    private string _dbPath = string.Empty;

    // Tracking overhead should be per-entity, so the relative gap ought to hold
    // roughly constant across these. If it does not, the simple story is wrong.
    [Params(100, 1000, 10000)]
    public int Rows { get; set; }

    [GlobalSetup]
    public void Setup() => _dbPath = DbLocator.Find();

    [Benchmark(Baseline = true)]
    public async Task<List<Quote>> Tracked()
    {
        using var ctx = DbLocator.NewContext(_dbPath);
        return await ctx.Quotes.Take(Rows).ToListAsync();
    }

    [Benchmark]
    public async Task<List<Quote>> AsNoTracking()
    {
        using var ctx = DbLocator.NewContext(_dbPath);
        return await ctx.Quotes.AsNoTracking().Take(Rows).ToListAsync();
    }
}

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Contains("--demo"))
        {
            Demos.Run();
            return;
        }

        if (args.Contains("--projections"))
        {
            Quotes.Benchmark.Projections.Run();
            return;
        }

        if (args.Contains("--collections"))
        {
            BenchmarkRunner.Run<CollectionSummaryBenchmarks>();
            return;
        }

        BenchmarkRunner.Run<TrackingBenchmarks>();
    }
}

// The behavioural differences, which a benchmark cannot show.
public static class Demos
{
    public static void Run()
    {
        var dbPath = DbLocator.Find();
        Console.WriteLine($"Database: {dbPath}\n");

        Console.WriteLine("=== Identity resolution ===");

        using (var ctx = DbLocator.NewContext(dbPath))
        {
            var a = ctx.Quotes.First(q => q.Id == 1);
            var b = ctx.Quotes.First(q => q.Id == 1);
            Console.WriteLine($"  Tracked:    same instance? {ReferenceEquals(a, b)}, " +
                              $"tracker holds {ctx.ChangeTracker.Entries().Count()}");
        }

        using (var ctx = DbLocator.NewContext(dbPath))
        {
            var a = ctx.Quotes.AsNoTracking().First(q => q.Id == 1);
            var b = ctx.Quotes.AsNoTracking().First(q => q.Id == 1);
            Console.WriteLine($"  NoTracking: same instance? {ReferenceEquals(a, b)}, " +
                              $"tracker holds {ctx.ChangeTracker.Entries().Count()}");
        }

        Console.WriteLine("\n=== AsNoTracking cannot save changes ===");

        using (var ctx = DbLocator.NewContext(dbPath))
        {
            var q = ctx.Quotes.AsNoTracking().First();
            q.SoftDelete();
            Console.WriteLine($"  NoTracking: SaveChanges wrote {ctx.SaveChanges()} rows, " +
                              $"tracker holds {ctx.ChangeTracker.Entries().Count()}");
        }

        using (var ctx = DbLocator.NewContext(dbPath))
        {
            var q = ctx.Quotes.First();
            q.SoftDelete();
            Console.WriteLine($"  Tracked:    tracker holds {ctx.ChangeTracker.Entries().Count()}, " +
                              $"state {ctx.Entry(q).State}");
            // Deliberately not calling SaveChanges - read-only against real data.
        }
    }
}