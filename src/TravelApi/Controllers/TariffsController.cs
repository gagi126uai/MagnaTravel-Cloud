using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Contracts.Tariffs;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/tariffs")]
[Authorize]
public class TariffsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public TariffsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TariffSummaryDto>>> GetTariffs(CancellationToken cancellationToken)
    {
        var tariffs = await _dbContext.Tariffs
            .AsNoTracking()
            .OrderBy(tariff => tariff.Name)
            .Select(tariff => new TariffSummaryDto(
                tariff.Id,
                tariff.Name,
                tariff.ProductType,
                tariff.Currency,
                tariff.DefaultPrice,
                tariff.IsActive,
                tariff.CreatedAt))
            .ToListAsync(cancellationToken);

        return Ok(tariffs);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<TariffDetailDto>> GetTariff(int id, CancellationToken cancellationToken)
    {
        var tariff = await _dbContext.Tariffs
            .AsNoTracking()
            .Include(found => found.Validities)
            .FirstOrDefaultAsync(found => found.Id == id, cancellationToken);

        if (tariff is null)
        {
            return NotFound();
        }

        var dto = new TariffDetailDto(
            tariff.Id,
            tariff.Name,
            tariff.Description,
            tariff.ProductType,
            tariff.Currency,
            tariff.DefaultPrice,
            tariff.IsActive,
            tariff.CreatedAt,
            tariff.Validities
                .OrderByDescending(validity => validity.StartDate)
                .Select(validity => new TariffValidityDto(
                    validity.Id,
                    validity.StartDate,
                    validity.EndDate,
                    validity.Price,
                    validity.IsActive,
                    validity.Notes))
                .ToList());

        return Ok(dto);
    }

    [HttpPost]
    public async Task<ActionResult<TariffSummaryDto>> CreateTariff(
        CreateTariffRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProductType))
        {
            return BadRequest("El producto del tarifario es obligatorio.");
        }

        var tariff = new Tariff
        {
            Name = request.Name,
            Description = request.Description,
            ProductType = request.ProductType,
            Currency = request.Currency,
            DefaultPrice = request.DefaultPrice,
            IsActive = request.IsActive
        };

        _dbContext.Tariffs.Add(tariff);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new TariffSummaryDto(
            tariff.Id,
            tariff.Name,
            tariff.ProductType,
            tariff.Currency,
            tariff.DefaultPrice,
            tariff.IsActive,
            tariff.CreatedAt);

        return CreatedAtAction(nameof(GetTariff), new { id = tariff.Id }, dto);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<TariffSummaryDto>> UpdateTariff(
        int id,
        UpdateTariffRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ProductType))
        {
            return BadRequest("El producto del tarifario es obligatorio.");
        }

        var tariff = await _dbContext.Tariffs.FindAsync(new object[] { id }, cancellationToken);

        if (tariff is null)
        {
            return NotFound();
        }

        tariff.Name = request.Name;
        tariff.Description = request.Description;
        tariff.ProductType = request.ProductType;
        tariff.Currency = request.Currency;
        tariff.DefaultPrice = request.DefaultPrice;
        tariff.IsActive = request.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new TariffSummaryDto(
            tariff.Id,
            tariff.Name,
            tariff.ProductType,
            tariff.Currency,
            tariff.DefaultPrice,
            tariff.IsActive,
            tariff.CreatedAt);

        return Ok(dto);
    }

    [HttpGet("{tariffId:int}/validities")]
    public async Task<ActionResult<IEnumerable<TariffValidityDto>>> GetValidities(
        int tariffId,
        CancellationToken cancellationToken)
    {
        var exists = await _dbContext.Tariffs
            .AsNoTracking()
            .AnyAsync(tariff => tariff.Id == tariffId, cancellationToken);

        if (!exists)
        {
            return NotFound();
        }

        var validities = await _dbContext.TariffValidities
            .AsNoTracking()
            .Where(validity => validity.TariffId == tariffId)
            .OrderByDescending(validity => validity.StartDate)
            .Select(validity => new TariffValidityDto(
                validity.Id,
                validity.StartDate,
                validity.EndDate,
                validity.Price,
                validity.IsActive,
                validity.Notes))
            .ToListAsync(cancellationToken);

        return Ok(validities);
    }

    [HttpGet("{tariffId:int}/validities/{validityId:int}")]
    public async Task<ActionResult<TariffValidityDto>> GetValidity(
        int tariffId,
        int validityId,
        CancellationToken cancellationToken)
    {
        var validity = await _dbContext.TariffValidities
            .AsNoTracking()
            .FirstOrDefaultAsync(
                found => found.TariffId == tariffId && found.Id == validityId,
                cancellationToken);

        if (validity is null)
        {
            return NotFound();
        }

        return Ok(new TariffValidityDto(
            validity.Id,
            validity.StartDate,
            validity.EndDate,
            validity.Price,
            validity.IsActive,
            validity.Notes));
    }

    [HttpPost("{tariffId:int}/validities")]
    public async Task<ActionResult<TariffValidityDto>> CreateValidity(
        int tariffId,
        CreateTariffValidityRequest request,
        CancellationToken cancellationToken)
    {
        var tariff = await _dbContext.Tariffs.FindAsync(new object[] { tariffId }, cancellationToken);
        if (tariff is null)
        {
            return NotFound();
        }

        var validity = new TariffValidity
        {
            TariffId = tariff.Id,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Price = request.Price,
            IsActive = request.IsActive,
            Notes = request.Notes
        };

        if (!validity.HasValidRange())
        {
            return BadRequest("La vigencia debe tener una fecha de fin posterior a la fecha de inicio.");
        }

        _dbContext.TariffValidities.Add(validity);
        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new TariffValidityDto(
            validity.Id,
            validity.StartDate,
            validity.EndDate,
            validity.Price,
            validity.IsActive,
            validity.Notes);

        return CreatedAtAction(nameof(GetValidity), new { tariffId, validityId = validity.Id }, dto);
    }

    [HttpPut("{tariffId:int}/validities/{validityId:int}")]
    public async Task<ActionResult<TariffValidityDto>> UpdateValidity(
        int tariffId,
        int validityId,
        UpdateTariffValidityRequest request,
        CancellationToken cancellationToken)
    {
        var validity = await _dbContext.TariffValidities
            .FirstOrDefaultAsync(found => found.TariffId == tariffId && found.Id == validityId, cancellationToken);

        if (validity is null)
        {
            return NotFound();
        }

        validity.StartDate = request.StartDate;
        validity.EndDate = request.EndDate;
        validity.Price = request.Price;
        validity.IsActive = request.IsActive;
        validity.Notes = request.Notes;

        if (!validity.HasValidRange())
        {
            return BadRequest("La vigencia debe tener una fecha de fin posterior a la fecha de inicio.");
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        var dto = new TariffValidityDto(
            validity.Id,
            validity.StartDate,
            validity.EndDate,
            validity.Price,
            validity.IsActive,
            validity.Notes);

        return Ok(dto);
    }
}
