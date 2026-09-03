using Capstone.Catalog.Contracts;
using Capstone.Catalog.Infrastructure;
using Capstone.Curation.Application.Abstractions;
using Capstone.Curation.Application.PublishCollection;
using Capstone.Curation.Domain;
using Capstone.Curation.Infrastructure;
using Capstone.Curation.Infrastructure.Outbox;
using Capstone.SharedKernel;
using Capstone.Sharing.Application;
using Capstone.Sharing.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Composition. Each module is wired in one block, and the blocks do not know
// about each other - the only shared types below are Contracts. Splitting these
// into AddCurationModule()/AddSharingModule() extension methods owned by each
// module is the obvious next step and the reason they are already grouped.
//
// Everything is a singleton because every store is a dictionary in memory. Real
// persistence makes the unit of work and the repositories scoped, one per
// request, for the reason Day 2 set out: a DbContext is a per-request unit of
// work and is not thread-safe.
// ---------------------------------------------------------------------------

// Catalog - the supplier. Seeded, standing in for the existing quote tables.
builder.Services.AddSingleton<IQuoteCatalog>(_ => new InMemoryQuoteCatalog(
    new Dictionary<int, QuoteSummary>
    {
        [1] = new(1, "Grace Hopper", "The most damaging phrase in the language is: it's always been done that way."),
        [2] = new(2, "Leslie Lamport", "A distributed system is one where a machine you didn't know existed can render your own unusable."),
        [3] = new(3, "Melvin Conway", "Organizations design systems that mirror their own communication structure."),
    }));

// Curation - the core.
builder.Services.AddSingleton<InMemoryOutboxStore>();
builder.Services.AddSingleton<IOutboxStore>(sp => sp.GetRequiredService<InMemoryOutboxStore>());
builder.Services.AddSingleton<UnitOfWork>();
builder.Services.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<UnitOfWork>());
builder.Services.AddSingleton<InMemoryCollectionRepository>();
builder.Services.AddSingleton<ICollectionRepository>(sp => sp.GetRequiredService<InMemoryCollectionRepository>());
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PublishCollectionHandler>();

// Sharing - the subscriber.
builder.Services.AddSingleton<InMemoryFollowerDirectory>();
builder.Services.AddSingleton<IFollowerDirectory>(sp => sp.GetRequiredService<InMemoryFollowerDirectory>());
builder.Services.AddSingleton<InMemoryFeedWriter>();
builder.Services.AddSingleton<IFeedWriter>(sp => sp.GetRequiredService<InMemoryFeedWriter>());
builder.Services.AddSingleton<IProcessedMessageLog, InMemoryProcessedMessageLog>();
builder.Services.AddSingleton<CollectionPublishedHandler>();

// The stand-in for Day 20's relay process.
builder.Services.AddSingleton<Capstone.Api.InProcessRelay>();

var app = builder.Build();

// A broken invariant is the caller's mistake, not the server's, so it answers
// 400 with the domain's own message. Everything else keeps the default 500 -
// the distinction DomainException exists to make possible.
app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (DomainException ex) when (!context.Response.HasStarted)
    {
        // Guarded: once the response has started, the status code is already on
        // the wire and writing a second body corrupts it. An exception that
        // late has to be logged and the connection dropped instead.
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

app.MapPost("/api/collections", async (
    CreateCollectionRequest request,
    ICollectionRepository repository,
    IUnitOfWork unitOfWork,
    CancellationToken cancellationToken) =>
{
    var collection = Collection.Start(new CuratorId(request.CuratorId), request.Name);
    repository.Add(collection);
    await unitOfWork.CommitAsync(cancellationToken);

    return Results.Ok(new { collectionId = collection.Id.Value });
});

app.MapPost("/api/collections/{id:guid}/items", async (
    Guid id,
    AddItemRequest request,
    ICollectionRepository repository,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    CancellationToken cancellationToken) =>
{
    var collection = await repository.FindAsync(new CollectionId(id), cancellationToken);

    if (collection is null)
    {
        return Results.NotFound();
    }

    collection.AddItem(new QuoteId(request.QuoteId), clock.GetUtcNow());
    await unitOfWork.CommitAsync(cancellationToken);

    return Results.Ok(new { items = collection.Items.Count });
});

// The slice. Publish commits; the relay then carries the announcement to
// Sharing. Draining inline is the scaffold's shortcut, not the design - see
// InProcessRelay.
app.MapPost("/api/collections/{id:guid}/publish", async (
    Guid id,
    PublishCollectionRequest request,
    PublishCollectionHandler handler,
    Capstone.Api.InProcessRelay relay,
    CancellationToken cancellationToken) =>
{
    await handler.HandleAsync(new PublishCollectionCommand(id, request.CuratorId), cancellationToken);

    var delivered = await relay.DrainAsync(cancellationToken);

    return Results.Ok(new { published = true, messagesRelayed = delivered });
});

app.MapPost("/api/follows", (FollowRequest request, InMemoryFollowerDirectory directory) =>
{
    directory.Follow(request.CuratorId, request.FollowerId);
    return Results.NoContent();
});

app.MapGet("/api/feed/{followerId}", (string followerId, InMemoryFeedWriter feed)
    => Results.Ok(feed.FeedFor(followerId)));

app.Run();

internal sealed record CreateCollectionRequest(string CuratorId, string Name);
internal sealed record AddItemRequest(int QuoteId);
internal sealed record PublishCollectionRequest(string CuratorId);
internal sealed record FollowRequest(string CuratorId, string FollowerId);
