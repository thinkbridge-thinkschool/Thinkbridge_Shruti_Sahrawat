using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Caching;
using QuotesApi.DTOs;
using QuotesApi.Features.Collections;
using QuotesApi.Repositories;

namespace QuotesApi.Controllers;

/// <summary>
/// Collections, as an MVC controller rather than a minimal-API group.
/// </summary>
/// <remarks>
/// <b>Day 32: this class had no authorization at all, and that was the worst
/// defect in the repository.</b> Not one action carried <c>[Authorize]</c>,
/// there is no fallback policy on <c>AddAuthorization()</c>, and so all seven
/// endpoints below were anonymous - including a DELETE, and a GET that returns
/// every collection in the database. The only thing standing in front of them
/// was the Container Apps Easy Auth sidecar rejecting every request before it
/// reached the application, which is protection by accident: it would have
/// disappeared the moment anyone relaxed the gateway to make the health probe
/// work.
///
/// How it got here is worth recording, because it was not carelessness. The
/// quotes endpoints are a minimal-API group, and Day 25 secured them the right
/// way - <c>RequireAuthorization()</c> on the group, so a new endpoint added to
/// it is protected by default. This controller is the one piece of the app that
/// is not in that group, so it inherited none of that decision. Two styles of
/// endpoint registration, one of which is secure by default, and the other was
/// simply forgotten. Day 27's security pass looked at endpoint groups; Day 31's
/// re-check looked at the capstone. Neither looked here.
///
/// <b>What is fixed.</b> <c>[Authorize]</c> on the class rather than on each
/// action, deliberately, for exactly the reason the quotes group carries it at
/// group level: an eighth action added to this file is then protected without
/// anybody remembering to protect it.
///
/// <b>What is not fixed, and is a real hole.</b> Ownership is still whatever
/// the caller says it is. <c>ownerId</c> arrives from the query string on the
/// read paths and from <c>dto.OwnerId</c> on create, and nothing compares
/// either against the authenticated user. So any signed-in caller can read,
/// add to, and delete from any other owner's collection by passing their id -
/// classic IDOR, and the same mistake the capstone makes with <c>curatorId</c>.
///
/// It is left in place today rather than half-fixed. Deriving the owner from
/// <c>ClaimsPrincipal</c> changes the read-model contract, the cache keys
/// (ownerId is part of them - see <see cref="GetSummaries"/>), and the Angular
/// client that sends it. That is a feature-sized change, and shipping it
/// untested on the last day of the project would be trading a known hole for
/// an unknown one. <c>QuoteEndpoints</c> already shows the shape of the fix:
/// it reads the user from the principal via ClaimsPrincipalExtensions and
/// never accepts an owner from the request at all.
/// </remarks>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CollectionsController : ControllerBase
{
    // Day 21: bounds on the two values that become part of a cache key. See
    // GetSummaries for why caching is what made these worth enforcing.
    private const int MinPreviewSize = 1;
    private const int MaxPreviewSize = 25;
    private const int MaxOwnerIdLength = 64;

    private readonly IMediator _mediator;
    private readonly ICollectionRepository _repository;
    private readonly ICollectionSummaryReader _summaries;
    private readonly ICollectionSummaryCacheInvalidator _cache;

    // Day 21 added the last two. The reader is what the cached read path is
    // behind; the invalidator is called by every action on this controller
    // that changes what a summary would say.
    //
    // Invalidation lives here rather than in the command handlers because the
    // three write paths do not share one chokepoint today: CreateCollection
    // and AddQuote go through MediatR, RemoveQuote goes straight to the
    // repository. Putting it in the two handlers would silently miss the
    // third, which is the failure that shows up as a stale screen nobody can
    // reproduce. Once RemoveQuote moves onto MediatR too, a post-processor or
    // a notification is the better home for this and the controller goes back
    // to not knowing a cache exists.
    public CollectionsController(
        IMediator mediator,
        ICollectionRepository repository,
        ICollectionSummaryReader summaries,
        ICollectionSummaryCacheInvalidator cache)
    {
        _mediator = mediator;
        _repository = repository;
        _summaries = summaries;
        _cache = cache;
    }

    // READ PATH - goes to the query handler, which projects straight from the
    // DbContext. Returns the read model, not the aggregate.
    [HttpGet("summaries")]
    public async Task<IActionResult> GetSummaries(
        [FromQuery] string? ownerId,
        [FromQuery] int previewSize = 3,
        CancellationToken cancellationToken = default)
    {
        // Day 21. Both parameters are bounded before they reach the reader,
        // because caching changed what an unbounded parameter costs. Every
        // distinct (ownerId, previewSize) pair mints a new cache entry - in
        // memory, and in Redis when it is configured. Uncached, a caller
        // passing a thousand different ownerId values bought a thousand
        // queries and nothing else; cached, it also buys a thousand resident
        // entries that nothing evicts before their expiry. Caching turned a
        // load problem into a memory-growth one, so the guard belongs with the
        // change that introduced it.
        //
        // This comment used to begin "This endpoint is anonymous", which was
        // true when it was written and stopped being true on Day 32. The
        // unbounded-cache-key argument does not depend on anonymity, though:
        // one authenticated caller can still mint a thousand entries, because
        // ownerId is still a free-text parameter rather than the caller's own
        // identity. See the class remarks.
        //
        // previewSize is clamped rather than rejected: it feeds a Take() in
        // the query, so an absurd value is a real cost on the database too,
        // and a caller asking for 10,000 preview items has not made a request
        // worth honouring literally.
        if (previewSize < MinPreviewSize) previewSize = MinPreviewSize;
        if (previewSize > MaxPreviewSize) previewSize = MaxPreviewSize;

        if (ownerId is { Length: > MaxOwnerIdLength })
        {
            return Problem(
                detail: $"ownerId cannot be longer than {MaxOwnerIdLength} characters.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Bad Request");
        }

        var summaries = await _summaries.GetAsync(ownerId, previewSize, cancellationToken);

        return Ok(summaries);
    }

    // Dapper counterpart of the endpoint above, for a side-by-side comparison.
    // Same query contract, same response shape - see DAPPER.md.
    [HttpGet("summaries-dapper")]
    public async Task<IActionResult> GetSummariesDapper(
        [FromQuery] string? ownerId,
        [FromQuery] int previewSize = 3,
        CancellationToken cancellationToken = default)
    {
        var summaries = await _mediator.Send(
            new GetCollectionSummariesDapperQuery(ownerId, previewSize), cancellationToken);

        return Ok(summaries);
    }

    // WRITE PATH - goes to the command handler, which goes through the
    // aggregate. Returns the id, not the entity.
    [HttpPost]
    public async Task<IActionResult> CreateCollection(
        [FromBody] CreateCollectionDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var id = await _mediator.Send(
                new CreateCollectionCommand(dto.Name, dto.OwnerId), cancellationToken);

            // A new collection changes the unfiltered summary list and that
            // owner's filtered one, so every cached variant is dropped rather
            // than trying to reason about which keys this affected.
            await _cache.InvalidateAsync(cancellationToken);

            return CreatedAtAction(nameof(GetById), new { id }, new { id });
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
    }

    [HttpPost("{id}/items/{quoteId}")]
    public async Task<IActionResult> AddQuote(
        int id, int quoteId, CancellationToken cancellationToken)
    {
        try
        {
            var found = await _mediator.Send(
                new AddQuoteToCollectionCommand(id, quoteId), cancellationToken);

            // Only on an actual write. Invalidating after a not-found would
            // throw away a warm cache because somebody asked for a collection
            // that does not exist.
            if (found)
            {
                await _cache.InvalidateAsync(cancellationToken);
            }

            return found ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Invariant Violation");
        }
    }

    // Endpoints below still use the repository directly. Left deliberately: the
    // exercise splits one feature, and mixing both approaches in one file makes
    // the difference legible rather than hiding it behind a uniform style.

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var collections = await _repository.GetAllAsync(cancellationToken);
        return Ok(collections);
    }

    [HttpDelete("{id}/items/{quoteId}")]
    public async Task<IActionResult> RemoveQuote(
        int id, int quoteId, CancellationToken cancellationToken)
    {
        var collection = await _repository.GetByIdAsync(id, cancellationToken);
        if (collection == null) return NotFound();

        try
        {
            collection.RemoveItem(quoteId);
            await _repository.UpdateAsync(collection, cancellationToken);

            // The path that does not go through MediatR - and so the one a
            // handler-level invalidation would have missed.
            await _cache.InvalidateAsync(cancellationToken);

            return Ok(collection);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var collection = await _repository.GetByIdAsync(id, cancellationToken);
        return collection == null ? NotFound() : Ok(collection);
    }
}
