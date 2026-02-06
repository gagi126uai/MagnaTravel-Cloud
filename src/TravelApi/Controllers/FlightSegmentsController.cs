using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[ApiController]
[Route("api/files/{fileId}/flights")]
[Authorize]
public class FlightSegmentsController : ControllerBase
{
    private readonly AppDbContext _db;

    public FlightSegmentsController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> GetAll(int fileId, CancellationToken ct)
    {
        var flights = await _db.FlightSegments
            .Where(f => f.TravelFileId == fileId)
            .Include(f => f.Supplier)
            .OrderBy(f => f.DepartureTime)
            .Select(f => new {
                f.Id, f.AirlineCode, f.AirlineName, f.FlightNumber,
                f.Origin, f.OriginCity, f.Destination, f.DestinationCity,
                f.DepartureTime, f.ArrivalTime, f.CabinClass, f.Baggage,
                f.TicketNumber, f.FareBase, f.PNR, f.Status,
                f.NetCost, f.SalePrice, f.Commission, f.Tax, f.Notes,
                SupplierId = f.SupplierId,
                SupplierName = f.Supplier != null ? f.Supplier.Name : null
            })
            .ToListAsync(ct);
        return Ok(flights);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Create(int fileId, [FromBody] CreateFlightRequest req, CancellationToken ct)
    {
        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file == null) return NotFound("File no encontrado");

        var flight = new FlightSegment
        {
            TravelFileId = fileId,
            SupplierId = req.SupplierId,
            AirlineCode = req.AirlineCode,
            AirlineName = req.AirlineName,
            FlightNumber = req.FlightNumber,
            Origin = req.Origin,
            OriginCity = req.OriginCity,
            Destination = req.Destination,
            DestinationCity = req.DestinationCity,
            DepartureTime = req.DepartureTime,
            ArrivalTime = req.ArrivalTime,
            CabinClass = req.CabinClass,
            Baggage = req.Baggage,
            PNR = req.PNR,
            NetCost = req.NetCost,
            SalePrice = req.SalePrice,
            Commission = req.Commission,
            Tax = req.Tax,
            Notes = req.Notes
        };

        _db.FlightSegments.Add(flight);
        
        file.TotalCost += flight.NetCost + flight.Tax;
        file.TotalSale += flight.SalePrice;
        file.Balance = file.TotalSale - file.TotalCost;
        
        await _db.SaveChangesAsync(ct);
        return Ok(flight);
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(int fileId, int id, [FromBody] UpdateFlightRequest req, CancellationToken ct)
    {
        var flight = await _db.FlightSegments.FindAsync(new object[] { id }, ct);
        if (flight == null || flight.TravelFileId != fileId) return NotFound();

        flight.TicketNumber = req.TicketNumber;
        flight.PNR = req.PNR;
        flight.Status = req.Status;
        flight.Notes = req.Notes;

        await _db.SaveChangesAsync(ct);
        return Ok(flight);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Delete(int fileId, int id, CancellationToken ct)
    {
        var flight = await _db.FlightSegments.FindAsync(new object[] { id }, ct);
        if (flight == null || flight.TravelFileId != fileId) return NotFound();

        var file = await _db.TravelFiles.FindAsync(new object[] { fileId }, ct);
        if (file != null)
        {
            file.TotalCost -= (flight.NetCost + flight.Tax);
            file.TotalSale -= flight.SalePrice;
            file.Balance = file.TotalSale - file.TotalCost;
        }

        _db.FlightSegments.Remove(flight);
        await _db.SaveChangesAsync(ct);
        return Ok();
    }
}

public record CreateFlightRequest(
    int SupplierId, string AirlineCode, string? AirlineName, string FlightNumber,
    string Origin, string? OriginCity, string Destination, string? DestinationCity,
    DateTime DepartureTime, DateTime ArrivalTime, string CabinClass, string? Baggage, string? PNR,
    decimal NetCost, decimal SalePrice, decimal Commission, decimal Tax, string? Notes
);

public record UpdateFlightRequest(string? TicketNumber, string? PNR, string Status, string? Notes);
