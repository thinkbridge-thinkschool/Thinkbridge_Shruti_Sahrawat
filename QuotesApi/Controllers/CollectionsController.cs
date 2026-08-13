using Microsoft.AspNetCore.Mvc;
using QuotesApi.Domain;
using QuotesApi.DTOs;
using QuotesApi.Repositories;

namespace QuotesApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CollectionsController : ControllerBase
{
    private readonly ICollectionRepository _repository;

    public CollectionsController(ICollectionRepository repository) => _repository = repository;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var collections = await _repository.GetAllAsync(cancellationToken);
        return Ok(collections);
    }

    [HttpPost]
    public async Task<IActionResult> CreateCollection([FromBody] CreateCollectionDto dto, CancellationToken cancellationToken)
    {
        try
        {
            var collection = new Collection(dto.Name, dto.OwnerId);
            await _repository.AddAsync(collection, cancellationToken);
            return CreatedAtAction(nameof(GetById), new { id = collection.Id }, collection);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Bad Request");
        }
    }

    [HttpPost("{id}/items/{quoteId}")]
    public async Task<IActionResult> AddQuote(int id, int quoteId, CancellationToken cancellationToken)
    {
        var collection = await _repository.GetByIdAsync(id, cancellationToken);
        if (collection == null) return NotFound();

        try
        {
            collection.AddItem(quoteId);
            await _repository.UpdateAsync(collection, cancellationToken);
            return Ok(collection);
        }
        catch (InvalidOperationException ex)
        {
            return Problem(detail: ex.Message, statusCode: 400, title: "Invariant Violation");
        }
    }

    [HttpDelete("{id}/items/{quoteId}")]
    public async Task<IActionResult> RemoveQuote(int id, int quoteId, CancellationToken cancellationToken)
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
