using MediatR;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.DTOs;
using QuotesApi.Features.Collections;
using QuotesApi.Repositories;

namespace QuotesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CollectionsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly ICollectionRepository _repository;

    public CollectionsController(IMediator mediator, ICollectionRepository repository)
    {
        _mediator = mediator;
        _repository = repository;
    }

    // READ PATH - goes to the query handler, which projects straight from the
    // DbContext. Returns the read model, not the aggregate.
    [HttpGet("summaries")]
    public async Task<IActionResult> GetSummaries(
        [FromQuery] string? ownerId,
        [FromQuery] int previewSize = 3,
        CancellationToken cancellationToken = default)
    {
        var summaries = await _mediator.Send(
            new GetCollectionSummariesQuery(ownerId, previewSize), cancellationToken);

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