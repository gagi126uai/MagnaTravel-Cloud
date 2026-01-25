using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Contracts.Files;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/travelfiles")]
[Authorize]
public class TravelFilesController : ControllerBase
{
    private readonly AppDbContext _context;

    public TravelFilesController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> GetFiles()
    {
        var files = await _context.TravelFiles
            .Include(f => f.Payer)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync();
        return Ok(files);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetFile(int id)
    {
        var file = await _context.TravelFiles
            .Include(f => f.Payer)
            .Include(f => f.Reservations)
                .ThenInclude(r => r.Supplier)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (file == null) return NotFound();
        return Ok(file);
    }

    [HttpPost]
    public async Task<IActionResult> CreateFile(CreateFileRequest request)
    {
        var nextId = await _context.TravelFiles.CountAsync() + 1000;
        var file = new TravelFile
        {
            Name = request.Name,
            FileNumber = $"F-{DateTime.Now.Year}-{nextId}",
            PayerId = request.PayerId,
            StartDate = request.StartDate,
            Description = request.Description,
            Status = FileStatus.Budget
        };
        
        _context.TravelFiles.Add(file);
        await _context.SaveChangesAsync();
        
        return CreatedAtAction(nameof(GetFile), new { id = file.Id }, file);
    }
}
