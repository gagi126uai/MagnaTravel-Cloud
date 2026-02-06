using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;

namespace TravelApi.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SuppliersController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public SuppliersController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Supplier>>> GetSuppliers(CancellationToken cancellationToken)
    {
        var suppliers = await _dbContext.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);

        var validStatuses = new[] { "Reservado", "Operativo", "Cerrado" };
        var supplierIds = suppliers.Select(s => s.Id).ToList();

        // 1. Costos de Vuelos
        var flightCosts = await _dbContext.FlightSegments
            .Where(f => supplierIds.Contains(f.SupplierId) && validStatuses.Contains(f.TravelFile!.Status))
            .GroupBy(f => f.SupplierId)
            .Select(g => new { SupplierId = g.Key, Total = g.Sum(x => x.NetCost) })
            .ToListAsync(cancellationToken);

        // 2. Costos de Hoteles
        var hotelCosts = await _dbContext.HotelBookings
            .Where(h => supplierIds.Contains(h.SupplierId) && validStatuses.Contains(h.TravelFile!.Status))
            .GroupBy(h => h.SupplierId)
            .Select(g => new { SupplierId = g.Key, Total = g.Sum(x => x.NetCost) })
            .ToListAsync(cancellationToken);

        // 3. Costos de Traslados
        var transferCosts = await _dbContext.TransferBookings
            .Where(t => supplierIds.Contains(t.SupplierId) && validStatuses.Contains(t.TravelFile!.Status))
            .GroupBy(t => t.SupplierId)
            .Select(g => new { SupplierId = g.Key, Total = g.Sum(x => x.NetCost) })
            .ToListAsync(cancellationToken);

        // 4. Costos de Paquetes
        var packageCosts = await _dbContext.PackageBookings
            .Where(p => supplierIds.Contains(p.SupplierId) && validStatuses.Contains(p.TravelFile!.Status))
            .GroupBy(p => p.SupplierId)
            .Select(g => new { SupplierId = g.Key, Total = g.Sum(x => x.NetCost) })
            .ToListAsync(cancellationToken);

        // 5. Costos de Reservas Genéricas
        var reservationCosts = await _dbContext.Reservations
            .Where(r => r.SupplierId.HasValue && supplierIds.Contains(r.SupplierId.Value) && validStatuses.Contains(r.TravelFile!.Status))
            .GroupBy(r => r.SupplierId!.Value)
            .Select(g => new { SupplierId = g.Key, Total = g.Sum(x => x.NetCost) })
            .ToListAsync(cancellationToken);

        // 6. Pagos Realizados
        var payments = await _dbContext.SupplierPayments
            .Where(p => supplierIds.Contains(p.SupplierId))
            .GroupBy(p => p.SupplierId)
            .Select(g => new { SupplierId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync(cancellationToken);

        // Calcular saldo para cada proveedor en memoria
        foreach (var supplier in suppliers)
        {
            decimal totalCost = 0;
            totalCost += flightCosts.FirstOrDefault(x => x.SupplierId == supplier.Id)?.Total ?? 0;
            totalCost += hotelCosts.FirstOrDefault(x => x.SupplierId == supplier.Id)?.Total ?? 0;
            totalCost += transferCosts.FirstOrDefault(x => x.SupplierId == supplier.Id)?.Total ?? 0;
            totalCost += packageCosts.FirstOrDefault(x => x.SupplierId == supplier.Id)?.Total ?? 0;
            totalCost += reservationCosts.FirstOrDefault(x => x.SupplierId == supplier.Id)?.Total ?? 0;

            decimal totalPaid = payments.FirstOrDefault(x => x.SupplierId == supplier.Id)?.Total ?? 0;

            // Actualizamos la propiedad solo para la respuesta (no se guarda en DB aquí)
            supplier.CurrentBalance = totalCost - totalPaid;
        }

        return Ok(suppliers);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<Supplier>> GetSupplier(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);

        if (supplier is null)
        {
            return NotFound();
        }

        return supplier;
    }

    [HttpPost]
    public async Task<ActionResult<Supplier>> CreateSupplier(Supplier supplier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplier.Name))
        {
            return BadRequest("El nombre del proveedor es requerido.");
        }

        // Ensure defaults for new suppliers
        supplier.CreatedAt = DateTime.UtcNow;
        supplier.CurrentBalance = 0; // Always start at 0

        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetSupplier), new { id = supplier.Id }, supplier);
    }

    [HttpPut("{id:int}")]
    public async Task<ActionResult<Supplier>> UpdateSupplier(int id, Supplier supplier, CancellationToken cancellationToken)
    {
        if (id != supplier.Id)
        {
            return BadRequest("ID mismatch");
        }

        var existing = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (existing is null)
        {
            return NotFound();
        }

        existing.Name = supplier.Name;
        existing.ContactName = supplier.ContactName;
        existing.Email = supplier.Email;
        existing.Phone = supplier.Phone;
        existing.IsActive = supplier.IsActive;
        // CurrentBalance is usually updated via payments/bills workflow, but allowing edit for now
        existing.CurrentBalance = supplier.CurrentBalance;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(existing);
    }

    [HttpDelete("{id:int}")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> DeleteSupplier(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier is null)
        {
            return NotFound("Proveedor no encontrado");
        }

        // Check for related services
        var hasServices = await _dbContext.Reservations.AnyAsync(r => r.SupplierId == id, cancellationToken);
        if (hasServices)
        {
            return BadRequest("No se puede eliminar: el proveedor tiene servicios asociados");
        }

        // Check for related payments
        var hasPayments = await _dbContext.SupplierPayments.AnyAsync(p => p.SupplierId == id, cancellationToken);
        if (hasPayments)
        {
            return BadRequest("No se puede eliminar: el proveedor tiene pagos registrados");
        }

        _dbContext.Suppliers.Remove(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Proveedor eliminado" });
    }

    /// <summary>
    /// Forzar eliminación de proveedor (desvincula servicios y pagos primero)
    /// Usar SOLO para proveedores corruptos o de prueba
    /// </summary>
    [HttpDelete("{id:int}/force")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult> ForceDeleteSupplier(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier is null)
        {
            return NotFound("Proveedor no encontrado");
        }

        // Desvincular servicios (poner SupplierId = null)
        await _dbContext.Reservations
            .Where(r => r.SupplierId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SupplierId, (int?)null), cancellationToken);

        // Eliminar pagos a este proveedor
        await _dbContext.SupplierPayments
            .Where(p => p.SupplierId == id)
            .ExecuteDeleteAsync(cancellationToken);

        // Ahora sí eliminar el proveedor
        _dbContext.Suppliers.Remove(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Proveedor eliminado (forzado)" });
    }

    /// <summary>
    /// Cuenta corriente del proveedor: servicios comprados, pagos realizados, saldo
    /// </summary>
    /// <summary>
    /// Cuenta corriente del proveedor: servicios comprados, pagos realizados, saldo
    /// </summary>
    [HttpGet("{id:int}/account")]
    public async Task<ActionResult> GetSupplierAccount(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (supplier is null)
        {
            return NotFound("Proveedor no encontrado");
        }

        // Definimos los estados que generan deuda (Confirmados)
        var validStatuses = new[] { "Reservado", "Operativo", "Cerrado" };

        // 1. Obtener Vuelos
        var flights = await _dbContext.FlightSegments
            .AsNoTracking()
            .Include(f => f.TravelFile)
            .Where(f => f.SupplierId == id && 
                        validStatuses.Contains(f.TravelFile!.Status))
            .Select(f => new ServiceDto
            {
                Id = f.Id,
                Type = "Vuelo",
                Description = $"{f.AirlineName} {f.FlightNumber} ({f.Origin}-{f.Destination})",
                Confirmation = f.PNR ?? f.TicketNumber,
                NetCost = f.NetCost,
                SalePrice = f.SalePrice,
                Date = f.DepartureTime,
                Status = f.Status,
                FileNumber = f.TravelFile!.FileNumber,
                FileName = f.TravelFile!.Name
            })
            .ToListAsync(cancellationToken);

        // 2. Obtener Hoteles
        var hotels = await _dbContext.HotelBookings
            .AsNoTracking()
            .Include(h => h.TravelFile)
            .Where(h => h.SupplierId == id && 
                        validStatuses.Contains(h.TravelFile!.Status))
            .Select(h => new ServiceDto
            {
                Id = h.Id,
                Type = "Hotel",
                Description = $"{h.HotelName} ({h.City})",
                Confirmation = h.ConfirmationNumber,
                NetCost = h.NetCost,
                SalePrice = h.SalePrice,
                Date = h.CheckIn,
                Status = h.Status,
                FileNumber = h.TravelFile!.FileNumber,
                FileName = h.TravelFile!.Name
            })
            .ToListAsync(cancellationToken);

        // 3. Obtener Traslados
        var transfers = await _dbContext.TransferBookings
            .AsNoTracking()
            .Include(t => t.TravelFile)
            .Where(t => t.SupplierId == id && 
                        validStatuses.Contains(t.TravelFile!.Status))
            .Select(t => new ServiceDto
            {
                Id = t.Id,
                Type = "Traslado",
                Description = $"{t.VehicleType} ({t.PickupLocation} -> {t.DropoffLocation})",
                Confirmation = t.ConfirmationNumber,
                NetCost = t.NetCost,
                SalePrice = t.SalePrice,
                Date = t.PickupDateTime,
                Status = t.Status,
                FileNumber = t.TravelFile!.FileNumber,
                FileName = t.TravelFile!.Name
            })
            .ToListAsync(cancellationToken);

        // 4. Obtener Paquetes
        var packages = await _dbContext.PackageBookings
            .AsNoTracking()
            .Include(p => p.TravelFile)
            .Where(p => p.SupplierId == id && 
                        validStatuses.Contains(p.TravelFile!.Status))
            .Select(p => new ServiceDto
            {
                Id = p.Id,
                Type = "Paquete",
                Description = p.PackageName,
                Confirmation = p.ConfirmationNumber,
                NetCost = p.NetCost,
                SalePrice = p.SalePrice,
                Date = p.StartDate,
                Status = p.Status,
                FileNumber = p.TravelFile!.FileNumber,
                FileName = p.TravelFile!.Name
            })
            .ToListAsync(cancellationToken);

        // 5. Obtener Reservas Genéricas
        var reservations = await _dbContext.Reservations
            .AsNoTracking()
            .Include(r => r.TravelFile)
            .Where(r => r.SupplierId == id && 
                        validStatuses.Contains(r.TravelFile!.Status))
            .Select(r => new ServiceDto
            {
                Id = r.Id,
                Type = r.ServiceType,
                Description = r.Description ?? r.ServiceType,
                Confirmation = r.ConfirmationNumber,
                NetCost = r.NetCost,
                SalePrice = r.SalePrice,
                Date = r.DepartureDate,
                Status = r.Status,
                FileNumber = r.TravelFile!.FileNumber,
                FileName = r.TravelFile!.Name
            })
            .ToListAsync(cancellationToken);

        // Unificar todo
        var allServices = new List<ServiceDto>();
        allServices.AddRange(flights);
        allServices.AddRange(hotels);
        allServices.AddRange(transfers);
        allServices.AddRange(packages);
        allServices.AddRange(reservations);

        // Ordenar por fecha descendente
        allServices = allServices.OrderByDescending(s => s.Date).ToList();

        // Pagos realizados a este proveedor
        var payments = await _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(p => p.SupplierId == id)
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new
            {
                p.Id,
                p.Amount,
                p.Method,
                p.PaidAt,
                p.Reference,
                p.Notes,
                FileNumber = p.TravelFile != null ? p.TravelFile.FileNumber : null
            })
            .ToListAsync(cancellationToken);

        // Totales
        var totalPurchases = allServices.Sum(s => s.NetCost);
        var totalPaid = payments.Sum(p => p.Amount);
        var balance = totalPurchases - totalPaid; // Positivo = le debemos

        return Ok(new
        {
            Supplier = new
            {
                supplier.Id,
                supplier.Name,
                supplier.ContactName,
                supplier.Email,
                supplier.Phone,
                supplier.TaxId
            },
            Services = allServices, // Enviamos la lista unificada
            Payments = payments,
            Summary = new
            {
                TotalPurchases = totalPurchases,
                TotalPaid = totalPaid,
                Balance = balance, // Positivo = le debemos
                ServiceCount = allServices.Count,
                PaymentCount = payments.Count
            }
        });
    }

    // DTO Helper class
    public class ServiceDto
    {
        public int Id { get; set; }
        public string Type { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Confirmation { get; set; }
        public decimal NetCost { get; set; }
        public decimal SalePrice { get; set; }
        public DateTime Date { get; set; }
        public string Status { get; set; } = "";
        public string? FileNumber { get; set; }
        public string? FileName { get; set; }
    }

    /// <summary>
    /// Registrar un pago al proveedor (egreso)
    /// </summary>
    /// <summary>
    /// Registrar un pago al proveedor (egreso)
    /// </summary>
    /// <summary>
    /// Registrar un pago al proveedor (egreso)
    /// </summary>
    [HttpPost("{id:int}/payments")]
    public async Task<ActionResult> AddSupplierPayment(int id, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier is null) return NotFound("Proveedor no encontrado");

        if (request.Amount <= 0) return BadRequest("El monto debe ser mayor a 0");

        // VALIDACIÓN: Calcular deuda real
        var currentDebt = await CalculateSupplierDebt(id, cancellationToken);
        
        // Si el pago es mayor a la deuda real, no permitirlo.
        if (request.Amount > currentDebt)
        {
            return BadRequest($"El pago ({request.Amount:C}) excede la deuda actual con el proveedor ({currentDebt:C}).");
        }

        var payment = new SupplierPayment
        {
            SupplierId = id,
            Amount = request.Amount,
            Method = request.Method ?? "Transfer",
            Reference = request.Reference,
            Notes = request.Notes,
            TravelFileId = request.TravelFileId,
            ReservationId = request.ReservationId,
            PaidAt = DateTime.UtcNow
        };

        _dbContext.SupplierPayments.Add(payment);
        
        // Actualizamos stored balance también para listados simples
        supplier.CurrentBalance = currentDebt - request.Amount; 
        
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Pago registrado correctamente", PaymentId = payment.Id });
    }

    /// <summary>
    /// Actualizar un pago existente
    /// </summary>
    [HttpPut("{id:int}/payments/{paymentId:int}")]
    public async Task<ActionResult> UpdateSupplierPayment(int id, int paymentId, [FromBody] SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier is null) return NotFound("Proveedor no encontrado");

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.SupplierId == id, cancellationToken);
        if (payment is null) return NotFound("Pago no encontrado");

        if (request.Amount <= 0) return BadRequest("El monto debe ser mayor a 0");

        // Recalcular deuda asumiendo que este pago NO existe (para validar el nuevo monto)
        // Deuda Real = (Servicios) - (Pagos - PagoActual)
        var realDebt = await CalculateSupplierDebt(id, cancellationToken);
        // CalculateSupplierDebt ya resta TODOS los pagos. Sumamos el pago actual para ver la deuda "sin este pago".
        var debtPrePayment = realDebt + payment.Amount;

        if (request.Amount > debtPrePayment)
        {
             return BadRequest($"La modificación del pago excede la deuda actual. Deuda: {debtPrePayment:C}, Nuevo Monto: {request.Amount:C}");
        }

        // Actualizar campos
        payment.Amount = request.Amount;
        payment.Method = request.Method ?? payment.Method;
        payment.Reference = request.Reference;
        payment.Notes = request.Notes;
        payment.TravelFileId = request.TravelFileId;
        
        // Actualizar stored balance
        supplier.CurrentBalance = debtPrePayment - request.Amount;

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Pago actualizado correctamente" });
    }

    /// <summary>
    /// Eliminar un pago y restaurar la deuda
    /// </summary>
    [HttpDelete("{id:int}/payments/{paymentId:int}")]
    public async Task<ActionResult> DeleteSupplierPayment(int id, int paymentId, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier is null) return NotFound("Proveedor no encontrado");

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.SupplierId == id, cancellationToken);
        if (payment is null) return NotFound("Pago no encontrado");

        var currentDebt = await CalculateSupplierDebt(id, cancellationToken);
        supplier.CurrentBalance = currentDebt + payment.Amount;

        _dbContext.SupplierPayments.Remove(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Pago eliminado y saldo restaurado" });
    }

    // Helper para calcular deuda consistente
    private async Task<decimal> CalculateSupplierDebt(int supplierId, CancellationToken cancellationToken)
    {
        var validStatuses = new[] { "Reservado", "Operativo", "Cerrado" };

        var flights = await _dbContext.FlightSegments.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.TravelFile!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var hotels = await _dbContext.HotelBookings.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.TravelFile!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var transfers = await _dbContext.TransferBookings.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.TravelFile!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var packages = await _dbContext.PackageBookings.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.TravelFile!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var reservations = await _dbContext.Reservations.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.TravelFile!.Status)).SumAsync(x => x.NetCost, cancellationToken);

        var totalPurchases = flights + hotels + transfers + packages + reservations;
        var totalPaid = await _dbContext.SupplierPayments.Where(p => p.SupplierId == supplierId).SumAsync(p => p.Amount, cancellationToken);

        return totalPurchases - totalPaid; // Saldo positivo = Deuda
    }

    /// <summary>
    /// Historial de pagos al proveedor
    /// </summary>
    [HttpGet("{id:int}/payments")]
    public async Task<ActionResult> GetSupplierPayments(int id, CancellationToken cancellationToken)
    {
        var payments = await _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(p => p.SupplierId == id)
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new
            {
                p.Id,
                p.Amount,
                p.Method,
                p.PaidAt,
                p.Reference,
                p.Notes,
                FileNumber = p.TravelFile != null ? p.TravelFile.FileNumber : null,
                FileName = p.TravelFile != null ? p.TravelFile.Name : null,
                TravelFileId = p.TravelFileId 
            })
            .ToListAsync(cancellationToken);

        return Ok(payments);
    }
}

public record SupplierPaymentRequest(
    decimal Amount, 
    string? Method, 
    string? Reference, 
    string? Notes,
    int? TravelFileId,
    int? ReservationId
);
