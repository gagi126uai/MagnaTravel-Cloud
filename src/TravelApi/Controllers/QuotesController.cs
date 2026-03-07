using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/quotes")]
[Authorize]
public class QuotesController : ControllerBase
{
    private readonly IQuoteService _quoteService;

    public QuotesController(IQuoteService quoteService)
    {
        _quoteService = quoteService;
    }

    [HttpGet]
    public async Task<ActionResult> GetAll(CancellationToken cancellationToken)
    {
        var quotes = await _quoteService.GetAllAsync(cancellationToken);
        return Ok(quotes);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var quote = await _quoteService.GetByIdAsync(id, cancellationToken);
        if (quote == null) return NotFound();
        return Ok(quote);
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] Quote quote, CancellationToken cancellationToken)
    {
        var created = await _quoteService.CreateAsync(quote, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, [FromBody] Quote updated, CancellationToken cancellationToken)
    {
        var quote = await _quoteService.UpdateAsync(id, updated, cancellationToken);
        return Ok(quote);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await _quoteService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("{id}/items")]
    public async Task<ActionResult> AddItem(int id, [FromBody] QuoteItem item, CancellationToken cancellationToken)
    {
        var quote = await _quoteService.AddItemAsync(id, item, cancellationToken);
        return Ok(quote);
    }

    [HttpDelete("{quoteId}/items/{itemId}")]
    public async Task<ActionResult> RemoveItem(int quoteId, int itemId, CancellationToken cancellationToken)
    {
        await _quoteService.RemoveItemAsync(quoteId, itemId, cancellationToken);
        return NoContent();
    }

    [HttpPatch("{id}/status")]
    public async Task<ActionResult> UpdateStatus(int id, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        var quote = await _quoteService.UpdateStatusAsync(id, request.Status, cancellationToken);
        return Ok(quote);
    }

    [HttpPost("{id}/convert")]
    public async Task<ActionResult> ConvertToFile(int id, CancellationToken cancellationToken)
    {
        var fileId = await _quoteService.ConvertToFileAsync(id, cancellationToken);
        return Ok(new { fileId });
    }
}

public record StatusUpdateRequest(string Status);
