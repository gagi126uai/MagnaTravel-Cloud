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
            .Include(f => f.Passengers)
            .Include(f => f.Payments.OrderByDescending(p => p.PaidAt))
            .Include(f => f.Reservations)
                .ThenInclude(r => r.Supplier)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (file == null) return NotFound();

        // Recalculate Balance on read to ensure consistency
        var totalPaid = file.Payments.Where(p => p.Status != "Cancelled").Sum(p => p.Amount);
        file.Balance = file.TotalSale - totalPaid;

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
    [HttpPost("{id}/services")]
    public async Task<IActionResult> AddService(int id, AddServiceRequest request)
    {
        var file = await _context.TravelFiles.FindAsync(id);
        if (file == null) return NotFound("Expediente no encontrado");

        // Validations with specific error messages
        if (string.IsNullOrWhiteSpace(request.ServiceType))
        {
            return BadRequest("Debe seleccionar un tipo de servicio");
        }

        if (request.DepartureDate == default)
        {
            return BadRequest("La fecha de salida es obligatoria");
        }

        if (request.SalePrice <= 0)
        {
            return BadRequest("El precio de venta debe ser mayor a 0");
        }

        if (request.NetCost < 0)
        {
            return BadRequest("El costo neto no puede ser negativo");
        }

        if (request.NetCost > request.SalePrice)
        {
            return BadRequest("El costo neto no puede ser mayor al precio de venta");
        }

        var reservation = new Reservation
        {
            TravelFileId = id,
            ServiceType = request.ServiceType,
            ProductType = request.ServiceType, // Mismo valor para ProductType y ServiceType
            SupplierId = request.SupplierId,
            CustomerId = file.PayerId,
            Description = request.Description ?? request.ServiceType,
            ConfirmationNumber = request.ConfirmationNumber ?? "PENDIENTE",
            Status = "Solicitado",
            DepartureDate = request.DepartureDate.ToUniversalTime(),
            ReturnDate = request.ReturnDate?.ToUniversalTime(),
            SalePrice = request.SalePrice,
            NetCost = request.NetCost,
            Commission = request.SalePrice - request.NetCost,
            CreatedAt = DateTime.UtcNow
        };

        // Update File Totals
        file.TotalSale += reservation.SalePrice;
        file.TotalCost += reservation.NetCost;
        file.Balance += reservation.SalePrice; // Add to balance (will be reduced by payments)

        _context.Reservations.Add(reservation);
        await _context.SaveChangesAsync();

        return Ok(reservation);
    }

    [HttpPut("services/{serviceId}")]
    public async Task<IActionResult> UpdateService(int serviceId, AddServiceRequest request)
    {
        var service = await _context.Reservations
            .Include(r => r.TravelFile)
            .FirstOrDefaultAsync(r => r.Id == serviceId);

        if (service == null) return NotFound("Servicio no encontrado");

        // Validations
        if (string.IsNullOrWhiteSpace(request.ServiceType))
        {
            return BadRequest("Debe seleccionar un tipo de servicio");
        }

        if (request.SalePrice <= 0)
        {
            return BadRequest("El precio de venta debe ser mayor a 0");
        }

        // Revert old amounts
        if (service.TravelFile != null)
        {
            service.TravelFile.TotalSale -= service.SalePrice;
            service.TravelFile.TotalCost -= service.NetCost;
            service.TravelFile.Balance -= service.SalePrice;
        }

        // Update service
        service.ServiceType = request.ServiceType;
        service.ProductType = request.ServiceType; // Mismo valor
        service.Description = request.Description ?? request.ServiceType;
        service.ConfirmationNumber = request.ConfirmationNumber ?? service.ConfirmationNumber;
        service.DepartureDate = request.DepartureDate.ToUniversalTime();
        service.ReturnDate = request.ReturnDate?.ToUniversalTime();
        service.SupplierId = request.SupplierId;
        service.SalePrice = request.SalePrice;
        service.NetCost = request.NetCost;
        service.Commission = request.SalePrice - request.NetCost;

        // Apply new amounts
        if (service.TravelFile != null)
        {
            service.TravelFile.TotalSale += service.SalePrice;
            service.TravelFile.TotalCost += service.NetCost;
            service.TravelFile.Balance += service.SalePrice;
        }

        await _context.SaveChangesAsync();
        return Ok(service);
    }

    [HttpDelete("services/{serviceId}")]
    public async Task<IActionResult> RemoveService(int serviceId)
    {
        var service = await _context.Reservations
            .Include(r => r.TravelFile)
            .FirstOrDefaultAsync(r => r.Id == serviceId);
            
        if (service == null) return NotFound();

        // Revert Totals based on what was saved
        if (service.TravelFile != null)
        {
            service.TravelFile.TotalSale -= service.SalePrice;
            service.TravelFile.TotalCost -= service.NetCost;
            service.TravelFile.Balance -= service.SalePrice;
        }

        _context.Reservations.Remove(service);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // ==================== PASSENGERS ====================
    [HttpGet("{id}/passengers")]
    public async Task<IActionResult> GetPassengers(int id)
    {
        var passengers = await _context.Passengers
            .Where(p => p.TravelFileId == id)
            .OrderBy(p => p.FullName)
            .ToListAsync();
        return Ok(passengers);
    }

    [HttpPost("{id}/passengers")]
    public async Task<IActionResult> AddPassenger(int id, Passenger passenger)
    {
        var file = await _context.TravelFiles.FindAsync(id);
        if (file == null) return NotFound("Expediente no encontrado");

        // Validations with specific error messages
        if (string.IsNullOrWhiteSpace(passenger.FullName))
        {
            return BadRequest("El nombre del pasajero es obligatorio");
        }

        if (passenger.FullName.Length < 3)
        {
            return BadRequest("El nombre debe tener al menos 3 caracteres");
        }

        passenger.TravelFileId = id;
        passenger.CreatedAt = DateTime.UtcNow;

        _context.Passengers.Add(passenger);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetFile), new { id = file.Id }, passenger);
    }

    [HttpPut("passengers/{passengerId}")]
    public async Task<IActionResult> UpdatePassenger(int passengerId, Passenger updated)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) return NotFound("Pasajero no encontrado");

        // Validations
        if (string.IsNullOrWhiteSpace(updated.FullName))
        {
            return BadRequest("El nombre del pasajero es obligatorio");
        }

        if (updated.FullName.Length < 3)
        {
            return BadRequest("El nombre debe tener al menos 3 caracteres");
        }

        // Update fields
        passenger.FullName = updated.FullName;
        passenger.DocumentType = updated.DocumentType;
        passenger.DocumentNumber = updated.DocumentNumber;
        passenger.BirthDate = updated.BirthDate;
        passenger.Nationality = updated.Nationality;
        passenger.Phone = updated.Phone;
        passenger.Email = updated.Email;
        passenger.Gender = updated.Gender;
        passenger.Notes = updated.Notes;

        await _context.SaveChangesAsync();
        return Ok(passenger);
    }

    [HttpDelete("passengers/{passengerId}")]
    public async Task<IActionResult> RemovePassenger(int passengerId)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) return NotFound();

        _context.Passengers.Remove(passenger);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // ==================== PAYMENTS ====================
    [HttpGet("{id}/payments")]
    public async Task<IActionResult> GetFilePayments(int id)
    {
        var payments = await _context.Payments
            .Where(p => p.TravelFileId == id)
            .OrderByDescending(p => p.PaidAt)
            .ToListAsync();
        return Ok(payments);
    }

    [HttpPost("{id}/payments")]
    public async Task<IActionResult> AddPayment(int id, Payment payment)
    {
        var file = await _context.TravelFiles.FindAsync(id);
        if (file == null) return NotFound("Expediente no encontrado");

        // Validations with specific error messages
        if (payment.Amount <= 0)
        {
            return BadRequest("El monto debe ser mayor a 0");
        }

        if (string.IsNullOrWhiteSpace(payment.Method))
        {
            return BadRequest("Debe seleccionar un método de pago");
        }

        // Prevent negative balance
        if (payment.Amount > file.Balance)
        {
            return BadRequest($"El monto (${payment.Amount:N2}) supera el saldo pendiente (${file.Balance:N2}). No se puede generar saldo negativo.");
        }

        payment.TravelFileId = id;
        payment.PaidAt = DateTime.UtcNow;
        payment.Status = "Paid";

        // Update file balance
        file.Balance -= payment.Amount;

        _context.Payments.Add(payment);
        await _context.SaveChangesAsync();

        return CreatedAtAction(nameof(GetFile), new { id = file.Id }, payment);
    }

    // ==================== STATUS ====================
    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        var file = await _context.TravelFiles.FindAsync(id);
        if (file == null) return NotFound("Expediente no encontrado");

        var validStatuses = new[] { FileStatus.Budget, FileStatus.Reserved, FileStatus.Operational, FileStatus.Closed, FileStatus.Cancelled };
        if (!validStatuses.Contains(request.Status))
        {
            return BadRequest("Estado no válido");
        }

        file.Status = request.Status;
        
        if (request.Status == FileStatus.Closed)
        {
            file.ClosedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync();
        return Ok(file);
    }
}

public record UpdateStatusRequest(string Status);
