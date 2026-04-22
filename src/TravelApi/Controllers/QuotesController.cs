using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Contracts.Shared;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

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
    public async Task<ActionResult<IEnumerable<QuoteSummaryDto>>> GetAll(CancellationToken cancellationToken)
    {
        return Ok(await _quoteService.GetAllAsync(cancellationToken));
    }

    [HttpGet("{publicIdOrLegacyId}")]
    public async Task<ActionResult<QuoteDetailDto>> GetById(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var quote = await _quoteService.GetByIdAsync(publicIdOrLegacyId, cancellationToken);
        if (quote == null)
        {
            return NotFound();
        }

        return Ok(quote);
    }

    [HttpPost]
    public async Task<ActionResult<QuoteDetailDto>> Create([FromBody] UpsertQuoteRequest request, CancellationToken cancellationToken)
    {
        var created = await _quoteService.CreateAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { publicIdOrLegacyId = created.PublicId }, created);
    }

    [HttpPut("{publicIdOrLegacyId}")]
    public async Task<ActionResult<QuoteDetailDto>> Update(string publicIdOrLegacyId, [FromBody] UpsertQuoteRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _quoteService.UpdateAsync(publicIdOrLegacyId, request, cancellationToken));
    }

    [HttpDelete("{publicIdOrLegacyId}")]
    public async Task<ActionResult> Delete(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _quoteService.DeleteAsync(publicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    [HttpPost("{publicIdOrLegacyId}/items")]
    public async Task<ActionResult<QuoteDetailDto>> AddItem(string publicIdOrLegacyId, [FromBody] UpsertQuoteItemRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _quoteService.AddItemAsync(publicIdOrLegacyId, request, cancellationToken));
    }

    [HttpDelete("{quotePublicIdOrLegacyId}/items/{itemPublicIdOrLegacyId}")]
    public async Task<ActionResult> RemoveItem(string quotePublicIdOrLegacyId, string itemPublicIdOrLegacyId, CancellationToken cancellationToken)
    {
        await _quoteService.RemoveItemAsync(quotePublicIdOrLegacyId, itemPublicIdOrLegacyId, cancellationToken);
        return NoContent();
    }

    [HttpPatch("{publicIdOrLegacyId}/status")]
    public async Task<ActionResult<QuoteDetailDto>> UpdateStatus(string publicIdOrLegacyId, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _quoteService.UpdateStatusAsync(publicIdOrLegacyId, request.Status, cancellationToken));
    }

    [HttpPost("{publicIdOrLegacyId}/convert")]
    public async Task<ActionResult<QuoteConversionResultDto>> ConvertToFile(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        return Ok(await _quoteService.ConvertToFileAsync(publicIdOrLegacyId, cancellationToken));
    }
}
