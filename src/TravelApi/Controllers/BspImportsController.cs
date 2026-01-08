using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Bsp;
using TravelApi.Data;
using TravelApi.Services.Bsp;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/bsp-imports")]
[Authorize]
public class BspImportsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly BspImportService _importService;

    public BspImportsController(AppDbContext dbContext, BspImportService importService)
    {
        _dbContext = dbContext;
        _importService = importService;
    }

    [HttpPost]
    public async Task<ActionResult<BspImportResponse>> ImportAsync(
        BspImportRequest request,
        CancellationToken cancellationToken)
    {
        var batch = await _importService.ImportAsync(
            request.FileName,
            request.Format,
            request.Content,
            cancellationToken);

        var response = new BspImportResponse(
            batch.Id,
            batch.Status,
            batch.RawRecords.Count,
            batch.NormalizedRecords.Count,
            batch.Reconciliations.Count);

        return Ok(response);
    }

    [HttpGet("{batchId:int}/summary")]
    public async Task<ActionResult<BspBatchSummaryResponse>> GetSummary(
        int batchId,
        CancellationToken cancellationToken)
    {
        var batch = await _dbContext.BspImportBatches
            .AsNoTracking()
            .Include(b => b.Reconciliations)
            .Include(b => b.NormalizedRecords)
            .Include(b => b.RawRecords)
            .FirstOrDefaultAsync(b => b.Id == batchId, cancellationToken);

        if (batch is null)
        {
            return NotFound();
        }

        var matchedCount = batch.Reconciliations.Count(entry => entry.Status == "Matched");
        var mismatchCount = batch.Reconciliations.Count(entry => entry.Status == "AmountMismatch");
        var missingCount = batch.Reconciliations.Count(entry => entry.Status == "MissingReservation");
        var totalImportedAmount = batch.NormalizedRecords.Sum(record => record.TotalAmount);
        var matchedIds = batch.Reconciliations
            .Where(entry => entry.Status == "Matched")
            .Select(entry => entry.BspNormalizedRecordId)
            .ToHashSet();
        var totalMatchedAmount = batch.NormalizedRecords
            .Where(record => matchedIds.Contains(record.Id))
            .Sum(record => record.TotalAmount);

        var response = new BspBatchSummaryResponse(
            batch.Id,
            batch.FileName,
            batch.Format,
            batch.Status,
            batch.ImportedAt,
            batch.ClosedAt,
            batch.RawRecords.Count,
            batch.NormalizedRecords.Count,
            matchedCount,
            mismatchCount,
            missingCount,
            totalImportedAmount,
            totalMatchedAmount);

        return Ok(response);
    }

    [HttpGet("{batchId:int}/reconciliations")]
    public async Task<ActionResult<IEnumerable<BspReconciliationItemResponse>>> GetReconciliations(
        int batchId,
        CancellationToken cancellationToken)
    {
        var records = await _dbContext.BspNormalizedRecords
            .AsNoTracking()
            .Include(record => record.ReconciliationEntry)
            .Where(record => record.BspImportBatchId == batchId)
            .OrderBy(record => record.Id)
            .Select(record => new BspReconciliationItemResponse(
                record.Id,
                record.TicketNumber,
                record.ReservationReference,
                record.TotalAmount,
                record.ReconciliationEntry!.Status,
                record.ReconciliationEntry!.DifferenceAmount))
            .ToListAsync(cancellationToken);

        return Ok(records);
    }

    [HttpPost("{batchId:int}/close")]
    public async Task<ActionResult<BspImportResponse>> CloseBatch(
        int batchId,
        CancellationToken cancellationToken)
    {
        var batch = await _importService.CloseBatchAsync(batchId, cancellationToken);

        var response = new BspImportResponse(
            batch.Id,
            batch.Status,
            batch.RawRecords.Count,
            batch.NormalizedRecords.Count,
            batch.Reconciliations.Count);

        return Ok(response);
    }
}
