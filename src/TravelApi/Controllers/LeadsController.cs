using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/leads")]
[Authorize]
public class LeadsController : ControllerBase
{
    private readonly ILeadService _leadService;

    public LeadsController(ILeadService leadService)
    {
        _leadService = leadService;
    }

    [HttpGet]
    public async Task<ActionResult> GetAll(CancellationToken cancellationToken)
    {
        var leads = await _leadService.GetAllAsync(cancellationToken);
        return Ok(leads);
    }

    [HttpGet("pipeline")]
    public async Task<ActionResult> GetPipeline(CancellationToken cancellationToken)
    {
        var pipeline = await _leadService.GetPipelineAsync(cancellationToken);
        return Ok(pipeline);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult> GetById(int id, CancellationToken cancellationToken)
    {
        var lead = await _leadService.GetByIdAsync(id, cancellationToken);
        if (lead == null) return NotFound();
        return Ok(lead);
    }

    [HttpPost]
    public async Task<ActionResult> Create([FromBody] Lead lead, CancellationToken cancellationToken)
    {
        var created = await _leadService.CreateAsync(lead, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult> Update(int id, [FromBody] Lead updated, CancellationToken cancellationToken)
    {
        var lead = await _leadService.UpdateAsync(id, updated, cancellationToken);
        return Ok(lead);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        await _leadService.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPatch("{id}/status")]
    public async Task<ActionResult> UpdateStatus(int id, [FromBody] StatusUpdateRequest request, CancellationToken cancellationToken)
    {
        var lead = await _leadService.UpdateStatusAsync(id, request.Status, cancellationToken);
        return Ok(lead);
    }

    [HttpPost("{id}/activities")]
    public async Task<ActionResult> AddActivity(int id, [FromBody] LeadActivity activity, CancellationToken cancellationToken)
    {
        var created = await _leadService.AddActivityAsync(id, activity, cancellationToken);
        return Ok(created);
    }

    [HttpPost("{id}/convert")]
    public async Task<ActionResult> ConvertToCustomer(int id, CancellationToken cancellationToken)
    {
        var customerId = await _leadService.ConvertToCustomerAsync(id, cancellationToken);
        return Ok(new { customerId });
    }

    [HttpPost("{id}/quote-draft")]
    public async Task<ActionResult> CreateQuoteDraft(int id, CancellationToken cancellationToken)
    {
        var quote = await _leadService.CreateQuoteDraftAsync(id, cancellationToken);
        return Ok(new
        {
            quoteId = quote.Id,
            quoteNumber = quote.QuoteNumber,
            customerId = quote.CustomerId,
            leadId = quote.LeadId
        });
    }

    [HttpGet("{id}/journey")]
    public async Task<ActionResult> GetJourney(int id, CancellationToken cancellationToken)
    {
        var journey = await _leadService.GetJourneyAsync(id, cancellationToken);
        return Ok(journey);
    }
}
