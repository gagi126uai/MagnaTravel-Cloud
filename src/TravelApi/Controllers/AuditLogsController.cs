using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AuditLogsController : ControllerBase
{
    private readonly AppDbContext _context;

    public AuditLogsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<AuditLog>>> GetAuditLogs(
        [FromQuery] string? entityName,
        [FromQuery] string? entityId,
        [FromQuery] DateTime? dateFrom,
        [FromQuery] DateTime? dateTo,
        [FromQuery] string? userId)
    {
        var query = _context.AuditLogs.AsQueryable();

        if (!string.IsNullOrEmpty(entityName))
        {
            query = query.Where(l => l.EntityName == entityName);
        }

        if (!string.IsNullOrEmpty(entityId))
        {
            query = query.Where(l => l.EntityId == entityId);
        }

        if (dateFrom.HasValue)
        {
            query = query.Where(l => l.Timestamp >= dateFrom.Value.ToUniversalTime());
        }

        if (dateTo.HasValue)
        {
            query = query.Where(l => l.Timestamp <= dateTo.Value.ToUniversalTime());
        }
        
        if (!string.IsNullOrEmpty(userId))
        {
            query = query.Where(l => l.UserId == userId);
        }

        var logs = await query.OrderByDescending(l => l.Timestamp)
                              .Take(100)
                              .ToListAsync();

        return Ok(logs);
    }
}
