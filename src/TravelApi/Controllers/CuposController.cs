using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Cupos;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Services;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/cupos")]
[Authorize]
public class CuposController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly CupoAllocationService _allocationService;

    public CuposController(AppDbContext dbContext, CupoAllocationService allocationService)
    {
        _dbContext = dbContext;
        _allocationService = allocationService;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<CupoSummaryDto>>> GetCupos(CancellationToken cancellationToken)
    {
        var cupos = await _dbContext.Cupos
            .AsNoTracking()
            .OrderBy(cupo => cupo.TravelDate)
            .ThenBy(cupo => cupo.Name)
            .Select(cupo => new CupoSummaryDto(
                cupo.Id,
                cupo.Name,
                cupo.ProductType,
                cupo.TravelDate,
                cupo.Capacity,
                cupo.Reserved,
                cupo.OverbookingLimit,
                cupo.Capacity + cupo.OverbookingLimit - cupo.Reserved,
                cupo.RowVersion))
            .ToListAsync(cancellationToken);

        return Ok(cupos);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<CupoSummaryDto>> GetCupo(int id, CancellationToken cancellationToken)
    {
        var cupo = await _dbContext.Cupos
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new CupoSummaryDto(
                item.Id,
                item.Name,
                item.ProductType,
                item.TravelDate,
                item.Capacity,
                item.Reserved,
                item.OverbookingLimit,
                item.Capacity + item.OverbookingLimit - item.Reserved,
                item.RowVersion))
            .FirstOrDefaultAsync(cancellationToken);

        if (cupo is null)
        {
            return NotFound();
        }

        return Ok(cupo);
    }

    [HttpPost]
    public async Task<ActionResult<CupoSummaryDto>> CreateCupo(CreateCupoRequest request, CancellationToken cancellationToken)
    {
        if (request.Capacity < 0 || request.OverbookingLimit < 0)
        {
            return BadRequest("La capacidad y la sobreventa deben ser valores positivos.");
        }

        var cupo = new Cupo
        {
            Name = request.Name,
            ProductType = request.ProductType,
            TravelDate = request.TravelDate,
            Capacity = request.Capacity,
            OverbookingLimit = request.OverbookingLimit,
            Reserved = 0,
            RowVersion = Guid.NewGuid()
        };

        _dbContext.Cupos.Add(cupo);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var response = new CupoSummaryDto(
            cupo.Id,
            cupo.Name,
            cupo.ProductType,
            cupo.TravelDate,
            cupo.Capacity,
            cupo.Reserved,
            cupo.OverbookingLimit,
            cupo.Capacity + cupo.OverbookingLimit - cupo.Reserved,
            cupo.RowVersion);

        return CreatedAtAction(nameof(GetCupo), new { id = cupo.Id }, response);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<CupoSummaryDto>> UpdateCupo(int id, UpdateCupoRequest request, CancellationToken cancellationToken)
    {
        var cupo = await _dbContext.Cupos.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        if (cupo is null)
        {
            return NotFound();
        }

        if (cupo.RowVersion != request.RowVersion)
        {
            return Conflict("El cupo fue actualizado por otro usuario.");
        }

        if (request.Capacity < 0 || request.OverbookingLimit < 0)
        {
            return BadRequest("La capacidad y la sobreventa deben ser valores positivos.");
        }

        var maxAllowed = request.Capacity + request.OverbookingLimit;
        if (cupo.Reserved > maxAllowed)
        {
            return Conflict("La capacidad propuesta no cubre los cupos ya asignados.");
        }

        cupo.Name = request.Name;
        cupo.ProductType = request.ProductType;
        cupo.TravelDate = request.TravelDate;
        cupo.Capacity = request.Capacity;
        cupo.OverbookingLimit = request.OverbookingLimit;
        cupo.RowVersion = Guid.NewGuid();

        await _dbContext.SaveChangesAsync(cancellationToken);

        var response = new CupoSummaryDto(
            cupo.Id,
            cupo.Name,
            cupo.ProductType,
            cupo.TravelDate,
            cupo.Capacity,
            cupo.Reserved,
            cupo.OverbookingLimit,
            cupo.Capacity + cupo.OverbookingLimit - cupo.Reserved,
            cupo.RowVersion);

        return Ok(response);
    }

    [HttpPost("{id:int}/assign")]
    public async Task<ActionResult<CupoAssignmentDto>> AssignCupo(int id, AssignCupoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var assignment = await _allocationService.AllocateAsync(id, request.Quantity, request.ReservationId, cancellationToken);

            var response = new CupoAssignmentDto(
                assignment.Id,
                assignment.CupoId,
                assignment.ReservationId,
                assignment.Quantity,
                assignment.AssignedAt);

            return Ok(response);
        }
        catch (CupoNotFoundException)
        {
            return NotFound();
        }
        catch (CupoOverbookingException ex)
        {
            return Conflict(ex.Message);
        }
        catch (CupoConcurrencyException ex)
        {
            return Conflict(ex.Message);
        }
    }
}
