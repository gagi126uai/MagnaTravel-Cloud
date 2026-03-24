using Microsoft.EntityFrameworkCore;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class SupplierService : ISupplierService
{
    private readonly AppDbContext _dbContext;

    public SupplierService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IEnumerable<Supplier>> GetSuppliersAsync(CancellationToken cancellationToken)
    {
        return await _dbContext.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Supplier> GetSupplierAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) throw new KeyNotFoundException("Proveedor no encontrado");
        return supplier;
    }

    public async Task<Supplier> CreateSupplierAsync(Supplier supplier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(supplier.Name))
        {
            throw new ArgumentException("El nombre del proveedor es requerido.");
        }

        supplier.CreatedAt = DateTime.UtcNow;
        supplier.CurrentBalance = 0; 

        _dbContext.Suppliers.Add(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return supplier;
    }

    public async Task<Supplier> UpdateSupplierAsync(int id, Supplier supplier, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (existing == null) throw new KeyNotFoundException("Proveedor no encontrado");

        existing.Name = supplier.Name;
        existing.ContactName = supplier.ContactName;
        existing.Email = supplier.Email;
        existing.Phone = supplier.Phone;
        existing.IsActive = supplier.IsActive;
        existing.CurrentBalance = supplier.CurrentBalance;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task DeleteSupplierAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) throw new KeyNotFoundException("Proveedor no encontrado");

        var hasServices = await _dbContext.Servicios.AnyAsync(r => r.SupplierId == id, cancellationToken);
        if (hasServices)
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene servicios asociados");
        }

        var hasPayments = await _dbContext.SupplierPayments.AnyAsync(p => p.SupplierId == id, cancellationToken);
        if (hasPayments)
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene pagos registrados");
        }

        _dbContext.Suppliers.Remove(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ForceDeleteSupplierAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) throw new KeyNotFoundException("Proveedor no encontrado");

        await _dbContext.Servicios
            .Where(r => r.SupplierId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.SupplierId, (int?)null), cancellationToken);

        await _dbContext.SupplierPayments
            .Where(p => p.SupplierId == id)
            .ExecuteDeleteAsync(cancellationToken);

        _dbContext.Suppliers.Remove(supplier);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RecalculateAllBalancesAsync(CancellationToken cancellationToken)
    {
        var suppliers = await _dbContext.Suppliers.ToListAsync(cancellationToken);

        foreach (var supplier in suppliers)
        {
            supplier.CurrentBalance = await CalculateSupplierDebt(supplier.Id, cancellationToken);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SupplierAccountDto> GetSupplierAccountAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (supplier == null) throw new KeyNotFoundException("Proveedor no encontrado");

        var validStatuses = new[] { "Reservado", "Operativo", "Cerrado" };

        var flights = await _dbContext.FlightSegments
            .AsNoTracking()
            .Include(f => f.Reserva)
            .Where(f => f.SupplierId == id && 
                        validStatuses.Contains(f.Reserva!.Status))
            .Select(f => new SupplierServiceDto
            {
                PublicId = f.PublicId,
                Type = "Vuelo",
                Description = $"{f.AirlineName} {f.FlightNumber} ({f.Origin}-{f.Destination})",
                Confirmation = f.PNR ?? f.TicketNumber,
                NetCost = f.NetCost,
                SalePrice = f.SalePrice,
                Date = f.CreatedAt,
                Status = f.Status,
                NumeroReserva = f.Reserva!.NumeroReserva,
                FileName = f.Reserva!.Name
            })
            .ToListAsync(cancellationToken);

        var hotels = await _dbContext.HotelBookings
            .AsNoTracking()
            .Include(h => h.Reserva)
            .Where(h => h.SupplierId == id && 
                        validStatuses.Contains(h.Reserva!.Status))
            .Select(h => new SupplierServiceDto
            {
                PublicId = h.PublicId,
                Type = "Hotel",
                Description = $"{h.HotelName} ({h.City})",
                Confirmation = h.ConfirmationNumber,
                NetCost = h.NetCost,
                SalePrice = h.SalePrice,
                Date = h.CreatedAt,
                Status = h.Status,
                NumeroReserva = h.Reserva!.NumeroReserva,
                FileName = h.Reserva!.Name
            })
            .ToListAsync(cancellationToken);

        var transfers = await _dbContext.TransferBookings
            .AsNoTracking()
            .Include(t => t.Reserva)
            .Where(t => t.SupplierId == id && 
                        validStatuses.Contains(t.Reserva!.Status))
            .Select(t => new SupplierServiceDto
            {
                PublicId = t.PublicId,
                Type = "Traslado",
                Description = $"{t.VehicleType} ({t.PickupLocation} -> {t.DropoffLocation})",
                Confirmation = t.ConfirmationNumber,
                NetCost = t.NetCost,
                SalePrice = t.SalePrice,
                Date = t.CreatedAt,
                Status = t.Status,
                NumeroReserva = t.Reserva!.NumeroReserva,
                FileName = t.Reserva!.Name
            })
            .ToListAsync(cancellationToken);

        var packages = await _dbContext.PackageBookings
            .AsNoTracking()
            .Include(p => p.Reserva)
            .Where(p => p.SupplierId == id && 
                        validStatuses.Contains(p.Reserva!.Status))
            .Select(p => new SupplierServiceDto
            {
                PublicId = p.PublicId,
                Type = "Paquete",
                Description = p.PackageName,
                Confirmation = p.ConfirmationNumber,
                NetCost = p.NetCost,
                SalePrice = p.SalePrice,
                Date = p.CreatedAt,
                Status = p.Status,
                NumeroReserva = p.Reserva!.NumeroReserva,
                FileName = p.Reserva!.Name
            })
            .ToListAsync(cancellationToken);

        var reservations = await _dbContext.Servicios
            .AsNoTracking()
            .Include(r => r.Reserva)
            .Where(r => r.SupplierId == id && 
                        validStatuses.Contains(r.Reserva!.Status))
            .Select(r => new SupplierServiceDto
            {
                PublicId = r.PublicId,
                Type = r.ServiceType,
                Description = r.Description ?? r.ServiceType,
                Confirmation = r.ConfirmationNumber,
                NetCost = r.NetCost,
                SalePrice = r.SalePrice,
                Date = r.CreatedAt,
                Status = r.Status,
                NumeroReserva = r.Reserva!.NumeroReserva,
                FileName = r.Reserva!.Name
            })
            .ToListAsync(cancellationToken);

        var allServices = new List<SupplierServiceDto>();
        allServices.AddRange(flights);
        allServices.AddRange(hotels);
        allServices.AddRange(transfers);
        allServices.AddRange(packages);
        allServices.AddRange(reservations);

        allServices = allServices.OrderByDescending(s => s.Date).ToList();

        var payments = await _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(p => p.SupplierId == id)
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new
            {
                p.PublicId,
                p.Amount,
                p.Method,
                p.PaidAt,
                p.Reference,
                p.Notes,
                NumeroReserva = p.Reserva != null ? p.Reserva.NumeroReserva : null,
                ReservaPublicId = p.Reserva != null ? (Guid?)p.Reserva.PublicId : null
            })
            .ToListAsync(cancellationToken);

        var totalPurchases = allServices.Sum(s => s.NetCost);
        var totalPaid = payments.Sum(p => p.Amount);
        var balance = totalPurchases - totalPaid;

        return new SupplierAccountDto
        {
            Supplier = new
            {
                supplier.PublicId,
                supplier.Name,
                supplier.ContactName,
                supplier.Email,
                supplier.Phone,
                supplier.TaxId
            },
            Services = allServices,
            Payments = payments.Cast<object>(),
            Summary = new
            {
                TotalPurchases = totalPurchases,
                TotalPaid = totalPaid,
                Balance = balance,
                ServiceCount = allServices.Count,
                PaymentCount = payments.Count
            }
        };
    }

    public async Task<Guid> AddSupplierPaymentAsync(int id, SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) throw new KeyNotFoundException("Proveedor no encontrado");

        if (request.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");

        var currentDebt = await CalculateSupplierDebt(id, cancellationToken);
        
        if (request.Amount > currentDebt)
        {
            throw new InvalidOperationException($"El pago ({request.Amount:C}) excede la deuda actual con el proveedor ({currentDebt:C}).");
        }

        int? reservaId = null;
        int? servicioReservaId = null;

        if (!string.IsNullOrWhiteSpace(request.ReservaId))
        {
            reservaId = await _dbContext.Reservas
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ReservaId, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.ServicioReservaId))
        {
            servicioReservaId = await _dbContext.Servicios
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ServicioReservaId, cancellationToken);
        }

        var payment = new SupplierPayment
        {
            SupplierId = id,
            Amount = request.Amount,
            Method = request.Method ?? "Transfer",
            Reference = request.Reference,
            Notes = request.Notes,
            ReservaId = reservaId,
            ServicioReservaId = servicioReservaId,
            PaidAt = DateTime.UtcNow
        };

        _dbContext.SupplierPayments.Add(payment);
        supplier.CurrentBalance = currentDebt - request.Amount; 
        
        await _dbContext.SaveChangesAsync(cancellationToken);

        return payment.PublicId;
    }

    public async Task UpdateSupplierPaymentAsync(int id, int paymentId, SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) throw new KeyNotFoundException("Proveedor no encontrado");

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.SupplierId == id, cancellationToken);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");

        if (request.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");

        var realDebt = await CalculateSupplierDebt(id, cancellationToken);
        var debtPrePayment = realDebt + payment.Amount;

        if (request.Amount > debtPrePayment)
        {
             throw new InvalidOperationException($"La modificación del pago excede la deuda actual. Deuda: {debtPrePayment:C}, Nuevo Monto: {request.Amount:C}");
        }

        int? reservaId = null;
        if (!string.IsNullOrWhiteSpace(request.ReservaId))
        {
            reservaId = await _dbContext.Reservas
                .AsNoTracking()
                .ResolveInternalIdAsync(request.ReservaId, cancellationToken);
        }

        payment.Amount = request.Amount;
        payment.Method = request.Method ?? payment.Method;
        payment.Reference = request.Reference;
        payment.Notes = request.Notes;
        payment.ReservaId = reservaId;
        
        supplier.CurrentBalance = debtPrePayment - request.Amount;

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteSupplierPaymentAsync(int id, int paymentId, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) throw new KeyNotFoundException("Proveedor no encontrado");

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(p => p.Id == paymentId && p.SupplierId == id, cancellationToken);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");

        var currentDebt = await CalculateSupplierDebt(id, cancellationToken);
        supplier.CurrentBalance = currentDebt + payment.Amount;

        _dbContext.SupplierPayments.Remove(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<SupplierPaymentDto>> GetSupplierPaymentsHistoryAsync(int id, CancellationToken cancellationToken)
    {
        return await _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(p => p.SupplierId == id)
            .OrderByDescending(p => p.PaidAt)
            .Select(p => new SupplierPaymentDto
            {
                PublicId = p.PublicId,
                Amount = p.Amount,
                Method = p.Method,
                PaidAt = p.PaidAt,
                Reference = p.Reference,
                Notes = p.Notes,
                NumeroReserva = p.Reserva != null ? p.Reserva.NumeroReserva : (string?)null,
                FileName = p.Reserva != null ? p.Reserva.Name : (string?)null,
                ReservaPublicId = p.Reserva != null ? (Guid?)p.Reserva.PublicId : null
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<decimal> CalculateSupplierDebt(int supplierId, CancellationToken cancellationToken)
    {
        var validStatuses = new[] { "Reservado", "Operativo", "Cerrado" };

        var flights = await _dbContext.FlightSegments.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.Reserva!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var hotels = await _dbContext.HotelBookings.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.Reserva!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var transfers = await _dbContext.TransferBookings.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.Reserva!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var packages = await _dbContext.PackageBookings.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.Reserva!.Status)).SumAsync(x => x.NetCost, cancellationToken);
        var reservations = await _dbContext.Servicios.Where(x => x.SupplierId == supplierId && validStatuses.Contains(x.Reserva!.Status)).SumAsync(x => x.NetCost, cancellationToken);

        var totalPurchases = flights + hotels + transfers + packages + reservations;
        var totalPaid = await _dbContext.SupplierPayments.Where(p => p.SupplierId == supplierId).SumAsync(p => p.Amount, cancellationToken);

        return totalPurchases - totalPaid;
    }
}
