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

    [HttpGet("debug/{id}")]
    public async Task<IActionResult> DebugFile(int id)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- DEBUG REPORT FOR ID: {id} ---");

        try
        {
            // 1. Simple Existence Check
            var exists = await _context.TravelFiles.AnyAsync(f => f.Id == id);
            sb.AppendLine($"1. Exists in DB: {exists}");

            if (!exists) return Ok(sb.ToString());

            // 2. Fetch without Includes
            var simpleFile = await _context.TravelFiles.FirstOrDefaultAsync(f => f.Id == id);
            sb.AppendLine($"2. Fetch Simple: Success. Name={simpleFile?.Name}");

            // 3. Fetch with Reservations only
            try 
            {
                var withRes = await _context.TravelFiles
                    .Include(f => f.Reservations)
                    .FirstOrDefaultAsync(f => f.Id == id);
                sb.AppendLine($"3. Fetch w/ Reservations: Success. Count={withRes?.Reservations.Count}");
                
                if (withRes?.Reservations != null)
                {
                    foreach(var r in withRes.Reservations)
                    {
                         sb.AppendLine($"   - ResId: {r.Id}, Service: {r.ServiceType}, Product: {r.ProductType ?? "NULL"}");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"3. Fetch w/ Reservations FAILED: {ex.Message}");
            }

            // 4. Fetch with Full Includes (The Failing One)
            try
            {
                var fullFile = await _context.TravelFiles
                    .Include(f => f.Payer)
                    .Include(f => f.Passengers)
                    .Include(f => f.Payments)
                    .Include(f => f.Reservations)
                        .ThenInclude(r => r.Supplier)
                    .FirstOrDefaultAsync(f => f.Id == id);
                
                if (fullFile == null) 
                    sb.AppendLine("4. Full Fetch returned NULL (WTF?)");
                else
                    sb.AppendLine("4. Full Fetch Success!");
            }
            catch (Exception ex) 
            {
                sb.AppendLine($"4. Full Fetch FAILED: {ex.Message}");
                sb.AppendLine(ex.StackTrace);
            }

            return Ok(new { Report = sb.ToString() });
        }
        catch (Exception ex)
        {
            sb.AppendLine($"CRITICAL FAILURE: {ex.Message}");
            return Ok(new { Report = sb.ToString() });
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetFile(int id)
    {
        Console.WriteLine($"[START] GetFile({id})");
        try 
        {
            var file = await _context.TravelFiles
                .Include(f => f.Payer)
                .Include(f => f.Passengers)
                .Include(f => f.Payments)
                .Include(f => f.Reservations)
                    .ThenInclude(r => r.Supplier)
                .Include(f => f.HotelBookings)
                .Include(f => f.FlightSegments)
                .Include(f => f.TransferBookings)
                .Include(f => f.PackageBookings)
                .FirstOrDefaultAsync(f => f.Id == id);

            if (file == null) 
            {
                Console.WriteLine($"[WARN] GetFile({id}) returned NULL");
                return NotFound($"File with ID {id} not found locally");
            }

            // Order Payments in memory since we removed it from Include
            file.Payments = file.Payments.OrderByDescending(p => p.PaidAt).ToList();

            // Recalculate Balance
            if (file.Payments != null)
            {
                var totalPaid = file.Payments.Where(p => p.Status != "Cancelled").Sum(p => p.Amount);
                file.Balance = file.TotalSale - totalPaid;
            }

            Console.WriteLine($"[SUCCESS] GetFile({id}) returning data");
            return Ok(file);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] GetFile({id}): {ex.Message} \n {ex.StackTrace}");
            // Return error as JSON to see in frontend if possible
            return StatusCode(500, new { Error = ex.Message, Stack = ex.StackTrace });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateFile(CreateFileRequest request)
    {
        try 
        {
            var nextId = await _context.TravelFiles.CountAsync() + 1000;
            var fileNumber = $"F-{DateTime.Now.Year}-{nextId}";
            
            // Use provided name OR default to FileNumber if empty
            var fileName = !string.IsNullOrWhiteSpace(request.Name) 
                ? request.Name 
                : $"File {fileNumber}";

            var file = new TravelFile
            {
                Name = fileName,
                FileNumber = fileNumber,
                PayerId = request.PayerId,
                StartDate = request.StartDate,
                Description = request.Description,
                Status = FileStatus.Budget
            };
            
            _context.TravelFiles.Add(file);
            await _context.SaveChangesAsync();
            
            return CreatedAtAction(nameof(GetFile), new { id = file.Id }, file);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error creando expediente: {ex.Message}");
        }
    }

    [HttpPost("{id}/services")]
    public async Task<IActionResult> AddService(int id, AddServiceRequest request)
    {
        try
        {
            var file = await _context.TravelFiles.FindAsync(id);
            if (file == null) return NotFound("Expediente no encontrado");

            // Validations with specific error messages
            if (string.IsNullOrWhiteSpace(request.ServiceType)) return BadRequest("Debe seleccionar un tipo de servicio");
            if (request.DepartureDate == default) return BadRequest("La fecha de salida es obligatoria");
            if (request.SalePrice <= 0) return BadRequest("El precio de venta debe ser mayor a 0");
            if (request.NetCost < 0) return BadRequest("El costo neto no puede ser negativo");
            if (request.NetCost > request.SalePrice) return BadRequest("El costo neto no puede ser mayor al precio de venta");

            var reservation = new Reservation
            {
                TravelFileId = id,
                ServiceType = request.ServiceType,
                ProductType = request.ServiceType,
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
            file.Balance += reservation.SalePrice; 

            _context.Reservations.Add(reservation);
            await _context.SaveChangesAsync();

            return Ok(reservation);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error agregando servicio: {ex.Message}");
        }
    }

    [HttpPut("services/{serviceId}")]
    public async Task<IActionResult> UpdateService(int serviceId, AddServiceRequest request)
    {
        try
        {
            var service = await _context.Reservations
                .Include(r => r.TravelFile)
                .FirstOrDefaultAsync(r => r.Id == serviceId);

            if (service == null) return NotFound("Servicio no encontrado");

            // Validations
            if (string.IsNullOrWhiteSpace(request.ServiceType)) return BadRequest("Debe seleccionar un tipo de servicio");
            if (request.SalePrice <= 0) return BadRequest("El precio de venta debe ser mayor a 0");

            // Revert old amounts
            if (service.TravelFile != null)
            {
                service.TravelFile.TotalSale -= service.SalePrice;
                service.TravelFile.TotalCost -= service.NetCost;
                service.TravelFile.Balance -= service.SalePrice;
            }

            // Update service
            service.ServiceType = request.ServiceType;
            service.ProductType = request.ServiceType;
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
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando servicio: {ex.Message}");
        }
    }

    [HttpDelete("services/{serviceId}")]
    public async Task<IActionResult> RemoveService(int serviceId)
    {
        try
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
        catch (Exception ex)
        {
             return StatusCode(500, $"Error eliminando servicio: {ex.Message}");
        }
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
        try
        {
            var file = await _context.TravelFiles.FindAsync(id);
            if (file == null) return NotFound("Expediente no encontrado");

            // Validations
            if (string.IsNullOrWhiteSpace(passenger.FullName)) return BadRequest("El nombre del pasajero es obligatorio");
            if (passenger.FullName.Length < 3) return BadRequest("El nombre debe tener al menos 3 caracteres");

            passenger.TravelFileId = id;
            passenger.CreatedAt = DateTime.UtcNow;

            _context.Passengers.Add(passenger);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetFile), new { id = file.Id }, passenger);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error agregando pasajero: {ex.Message}");
        }
    }

    [HttpPut("passengers/{passengerId}")]
    public async Task<IActionResult> UpdatePassenger(int passengerId, Passenger updated)
    {
        try
        {
            var passenger = await _context.Passengers.FindAsync(passengerId);
            if (passenger == null) return NotFound("Pasajero no encontrado");

            if (string.IsNullOrWhiteSpace(updated.FullName)) return BadRequest("El nombre del pasajero es obligatorio");
            if (updated.FullName.Length < 3) return BadRequest("El nombre debe tener al menos 3 caracteres");

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
        catch (Exception ex)
        {
            return StatusCode(500, $"Error actualizando pasajero: {ex.Message}");
        }
    }

    [HttpDelete("passengers/{passengerId}")]
    public async Task<IActionResult> RemovePassenger(int passengerId)
    {
        try
        {
            var passenger = await _context.Passengers.FindAsync(passengerId);
            if (passenger == null) return NotFound();

            _context.Passengers.Remove(passenger);
            await _context.SaveChangesAsync();
            return NoContent();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando pasajero: {ex.Message}");
        }
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
        try
        {
            var file = await _context.TravelFiles.FindAsync(id);
            if (file == null) return NotFound("Expediente no encontrado");

            if (payment.Amount <= 0) return BadRequest("El monto debe ser mayor a 0");
            if (string.IsNullOrWhiteSpace(payment.Method)) return BadRequest("Debe seleccionar un método de pago");
            if (payment.Amount > file.Balance) return BadRequest($"El pago de ${payment.Amount:N2} excede el saldo pendiente (${file.Balance:N2}).");

            payment.TravelFileId = id;
            payment.PaidAt = DateTime.UtcNow;
            payment.Status = "Paid";

            file.Balance -= payment.Amount;

            _context.Payments.Add(payment);
            await _context.SaveChangesAsync();

            return CreatedAtAction(nameof(GetFile), new { id = file.Id }, payment);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error registrando pago: {ex.Message}");
        }
    }

    [HttpDelete("{id}/payments/{paymentId}")]
    public async Task<IActionResult> DeletePayment(int id, int paymentId)
    {
        try
        {
            var payment = await _context.Payments.FindAsync(paymentId);
            if (payment == null) return NotFound("Pago no encontrado");
            
            if (payment.TravelFileId != id) return BadRequest("El pago no corresponde al File");

            var file = await _context.TravelFiles.FindAsync(id);
            if (file == null) return NotFound("File no encontrado");

            file.Balance += payment.Amount;

            _context.Payments.Remove(payment);
            await _context.SaveChangesAsync();

            return Ok();
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error eliminando pago: {ex.Message}");
        }
    }

    // ==================== STATUS ====================
    [HttpPut("{id}/status")]
    public async Task<IActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
    {
        try
        {
            var file = await _context.TravelFiles.FindAsync(id);
            if (file == null) return NotFound("Expediente no encontrado");

            var validStatuses = new[] { FileStatus.Budget, FileStatus.Reserved, FileStatus.Operational, FileStatus.Closed, FileStatus.Cancelled };
            if (!validStatuses.Contains(request.Status)) return BadRequest("Estado no válido");

            file.Status = request.Status;
            if (request.Status == FileStatus.Closed) file.ClosedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return Ok(file);
        }
        catch (Exception ex)
        {
             return StatusCode(500, $"Error actualizando estado: {ex.Message}");
        }
    }

    [HttpPut("{id}/archive")]
    public async Task<IActionResult> ArchiveFile(int id)
    {
        try
        {
            var file = await _context.TravelFiles.FindAsync(id);
            if (file == null) return NotFound("File no encontrado");
            
            file.Status = "Archived";
            await _context.SaveChangesAsync();
            return Ok(file);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error archivando file: {ex.Message}");
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteFile(int id)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var file = await _context.TravelFiles
                .Include(f => f.Payments)
                .Include(f => f.Reservations)
                .Include(f => f.Passengers)
                .Include(f => f.FlightSegments)
                .Include(f => f.HotelBookings)
                .Include(f => f.TransferBookings)
                .Include(f => f.PackageBookings)
                .FirstOrDefaultAsync(f => f.Id == id);

            if (file == null) return NotFound("File no encontrado");

            // Only allow delete if Budget
            if (file.Status != FileStatus.Budget)
            {
                return BadRequest("Solo se pueden eliminar Files en estado Presupuesto.");
            }

            // Safety check for payments
            if (file.Payments.Any())
            {
                return BadRequest("No se puede eliminar un File con pagos registrados. Elimine los pagos primero.");
            }

            // Delete all related entities explicitly
            if (file.Reservations.Any()) _context.Reservations.RemoveRange(file.Reservations);
            if (file.Passengers.Any()) _context.Passengers.RemoveRange(file.Passengers);
            if (file.FlightSegments.Any()) _context.FlightSegments.RemoveRange(file.FlightSegments);
            if (file.HotelBookings.Any()) _context.HotelBookings.RemoveRange(file.HotelBookings);
            if (file.TransferBookings.Any()) _context.TransferBookings.RemoveRange(file.TransferBookings);
            if (file.PackageBookings.Any()) _context.PackageBookings.RemoveRange(file.PackageBookings);

            _context.TravelFiles.Remove(file);
            
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok();
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return StatusCode(500, $"Error eliminando expediente: {ex.Message}");
        }
    }
}

public record UpdateStatusRequest(string Status);
