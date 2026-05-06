using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class SupplierService : ISupplierService
{
    private static readonly string[] ValidReservationStatuses =
    {
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.Closed
    };

    private readonly AppDbContext _dbContext;

    public SupplierService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<PagedResponse<SupplierListItemDto>> GetSuppliersAsync(SupplierListQuery query, CancellationToken cancellationToken)
    {
        var suppliersQuery = _dbContext.Suppliers.AsNoTracking();

        if (!query.IncludeInactive)
        {
            suppliersQuery = suppliersQuery.Where(supplier => supplier.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            suppliersQuery = suppliersQuery.Where(supplier =>
                supplier.Name.ToLower().Contains(normalized) ||
                supplier.TaxId != null && supplier.TaxId.ToLower().Contains(normalized) ||
                supplier.ContactName != null && supplier.ContactName.ToLower().Contains(normalized) ||
                supplier.Email != null && supplier.Email.ToLower().Contains(normalized) ||
                supplier.Phone != null && supplier.Phone.ToLower().Contains(normalized));
        }

        var itemsQuery = ApplySupplierOrdering(suppliersQuery, query)
            .Select(supplier => new SupplierListItemDto
            {
                PublicId = supplier.PublicId,
                Name = supplier.Name,
                ContactName = supplier.ContactName,
                Email = supplier.Email,
                Phone = supplier.Phone,
                TaxId = supplier.TaxId,
                TaxCondition = supplier.TaxCondition,
                Address = supplier.Address,
                IsActive = supplier.IsActive,
                CurrentBalance = supplier.CurrentBalance,
                CreatedAt = supplier.CreatedAt
            });

        return await itemsQuery.ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<Supplier> GetSupplierAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

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
        if (existing == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        existing.Name = supplier.Name;
        existing.ContactName = supplier.ContactName;
        existing.Email = supplier.Email;
        existing.Phone = supplier.Phone;
        existing.TaxId = supplier.TaxId;
        existing.TaxCondition = supplier.TaxCondition;
        existing.Address = supplier.Address;
        existing.IsActive = supplier.IsActive;

        await _dbContext.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task DeleteSupplierAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        // Checks legacy preexistentes.
        var hasServices = await _dbContext.Servicios.AnyAsync(service => service.SupplierId == id, cancellationToken);
        if (hasServices)
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene servicios asociados");
        }

        var hasPayments = await _dbContext.SupplierPayments.AnyAsync(payment => payment.SupplierId == id, cancellationToken);
        if (hasPayments)
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene pagos registrados");
        }

        // C24: tambien chequear bookings tipados (Hotel/Transfer/Package/Flight).
        // Antes se podia ELIMINAR un supplier con bookings activos porque la FK
        // estaba en Cascade por convencion EF y arrastraba todo. Ahora se bloquea
        // explicitamente desde el servicio (mensaje de negocio claro) y la BD
        // mantiene Restrict como red de seguridad.
        if (await _dbContext.HotelBookings.AnyAsync(booking => booking.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene reservas de hotel asociadas");
        }

        if (await _dbContext.TransferBookings.AnyAsync(booking => booking.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene transfers asociados");
        }

        if (await _dbContext.PackageBookings.AnyAsync(booking => booking.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene paquetes asociados");
        }

        if (await _dbContext.FlightSegments.AnyAsync(segment => segment.SupplierId == id, cancellationToken))
        {
            throw new InvalidOperationException("No se puede eliminar: el proveedor tiene segmentos de vuelo asociados");
        }

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

    public async Task UpdateBalanceAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null) return;
        
        supplier.CurrentBalance = await CalculateSupplierDebt(id, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<SupplierAccountOverviewDto> GetSupplierAccountOverviewAsync(int id, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers
            .AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new SupplierAccountSupplierDto
            {
                PublicId = item.PublicId,
                Name = item.Name,
                ContactName = item.ContactName,
                Email = item.Email,
                Phone = item.Phone,
                TaxId = item.TaxId,
                TaxCondition = item.TaxCondition,
                Address = item.Address,
                IsActive = item.IsActive,
                CurrentBalance = item.CurrentBalance
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        var servicesQuery = BuildSupplierServicesQuery(id);
        var paymentsQuery = BuildSupplierPaymentsQuery(id);

        var totalPurchases = await servicesQuery
            .Where(item => item.Status == "Confirmado" || item.Status == "Emitido" || item.Status == "HK" || item.Status == "TK" || item.Status == "KK" || item.Status == "KL")
            .SumAsync(item => (decimal?)item.NetCost, cancellationToken) ?? 0m;
        var totalPaid = await paymentsQuery.SumAsync(item => (decimal?)item.Amount, cancellationToken) ?? 0m;
        var serviceCount = await servicesQuery.CountAsync(cancellationToken);
        var paymentCount = await paymentsQuery.CountAsync(cancellationToken);

        return new SupplierAccountOverviewDto
        {
            Supplier = supplier,
            Summary = new SupplierAccountSummaryDto
            {
                TotalPurchases = EconomicRulesHelper.RoundCurrency(totalPurchases),
                TotalPaid = EconomicRulesHelper.RoundCurrency(totalPaid),
                Balance = EconomicRulesHelper.RoundCurrency(totalPurchases - totalPaid),
                ServiceCount = serviceCount,
                PaymentCount = paymentCount
            }
        };
    }

    public async Task<PagedResponse<SupplierAccountServiceListItemDto>> GetSupplierAccountServicesAsync(
        int id,
        SupplierAccountServicesQuery query,
        CancellationToken cancellationToken)
    {
        var servicesQuery = BuildSupplierServicesQuery(id);

        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            var normalizedType = query.Type.Trim().ToLowerInvariant();
            servicesQuery = servicesQuery.Where(item => item.Type.ToLower() == normalizedType);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            servicesQuery = servicesQuery.Where(item =>
                item.Type.ToLower().Contains(normalized) ||
                item.Description != null && item.Description.ToLower().Contains(normalized) ||
                item.Confirmation != null && item.Confirmation.ToLower().Contains(normalized) ||
                item.NumeroReserva != null && item.NumeroReserva.ToLower().Contains(normalized) ||
                item.FileName != null && item.FileName.ToLower().Contains(normalized));
        }

        servicesQuery = ApplySupplierAccountServiceOrdering(servicesQuery, query);
        return await servicesQuery.ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<PagedResponse<SupplierPaymentDto>> GetSupplierAccountPaymentsAsync(
        int id,
        SupplierAccountPaymentsQuery query,
        CancellationToken cancellationToken)
    {
        var paymentsQuery = BuildSupplierPaymentsQuery(id);

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var normalized = query.Search.Trim().ToLowerInvariant();
            paymentsQuery = paymentsQuery.Where(payment =>
                payment.Method.ToLower().Contains(normalized) ||
                payment.Reference != null && payment.Reference.ToLower().Contains(normalized) ||
                payment.Notes != null && payment.Notes.ToLower().Contains(normalized) ||
                payment.NumeroReserva != null && payment.NumeroReserva.ToLower().Contains(normalized) ||
                payment.FileName != null && payment.FileName.ToLower().Contains(normalized));
        }

        paymentsQuery = ApplySupplierPaymentOrdering(paymentsQuery, query);
        return await paymentsQuery.ToPagedResponseAsync(query, cancellationToken);
    }

    public async Task<Guid> AddSupplierPaymentAsync(int id, SupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        var supplier = await _dbContext.Suppliers.FindAsync(new object[] { id }, cancellationToken);
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("El monto debe ser mayor a 0");
        }

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
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(item => item.Id == paymentId && item.SupplierId == id, cancellationToken);
        if (payment == null)
        {
            throw new KeyNotFoundException("Pago no encontrado");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("El monto debe ser mayor a 0");
        }

        var realDebt = await CalculateSupplierDebt(id, cancellationToken);
        var debtPrePayment = realDebt + payment.Amount;

        if (request.Amount > debtPrePayment)
        {
            throw new InvalidOperationException($"La modificacion del pago excede la deuda actual. Deuda: {debtPrePayment:C}, Nuevo Monto: {request.Amount:C}");
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
        if (supplier == null)
        {
            throw new KeyNotFoundException("Proveedor no encontrado");
        }

        var payment = await _dbContext.SupplierPayments.FirstOrDefaultAsync(item => item.Id == paymentId && item.SupplierId == id, cancellationToken);
        if (payment == null)
        {
            throw new KeyNotFoundException("Pago no encontrado");
        }

        var currentDebt = await CalculateSupplierDebt(id, cancellationToken);
        supplier.CurrentBalance = currentDebt + payment.Amount;

        _dbContext.SupplierPayments.Remove(payment);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IEnumerable<SupplierPaymentDto>> GetSupplierPaymentsHistoryAsync(int id, CancellationToken cancellationToken)
    {
        return await ApplySupplierPaymentOrdering(
                BuildSupplierPaymentsQuery(id),
                new SupplierAccountPaymentsQuery())
            .ToListAsync(cancellationToken);
    }

    private IQueryable<SupplierAccountServiceListItemDto> BuildSupplierServicesQuery(int supplierId)
    {
        var flights = _dbContext.FlightSegments
            .AsNoTracking()
            .Where(segment => segment.SupplierId == supplierId && ValidReservationStatuses.Contains(segment.Reserva!.Status))
            .Select(segment => new SupplierAccountServiceListItemDto
            {
                PublicId = segment.PublicId,
                Type = "Vuelo",
                Description = ((segment.AirlineName ?? string.Empty) + " " + (segment.FlightNumber ?? string.Empty) + " (" + (segment.Origin ?? string.Empty) + "-" + (segment.Destination ?? string.Empty) + ")").Trim(),
                Confirmation = segment.PNR ?? segment.TicketNumber,
                NetCost = segment.NetCost,
                SalePrice = segment.SalePrice,
                Date = segment.CreatedAt,
                Status = segment.Status,
                NumeroReserva = segment.Reserva!.NumeroReserva,
                FileName = segment.Reserva!.Name,
                ReservaPublicId = segment.Reserva!.PublicId
            });

        var hotels = _dbContext.HotelBookings
            .AsNoTracking()
            .Where(booking => booking.SupplierId == supplierId && ValidReservationStatuses.Contains(booking.Reserva!.Status))
            .Select(booking => new SupplierAccountServiceListItemDto
            {
                PublicId = booking.PublicId,
                Type = "Hotel",
                Description = ((booking.HotelName ?? string.Empty) + " (" + (booking.City ?? string.Empty) + ")").Trim(),
                Confirmation = booking.ConfirmationNumber,
                NetCost = booking.NetCost,
                SalePrice = booking.SalePrice,
                Date = booking.CreatedAt,
                Status = booking.Status,
                NumeroReserva = booking.Reserva!.NumeroReserva,
                FileName = booking.Reserva!.Name,
                ReservaPublicId = booking.Reserva!.PublicId
            });

        var transfers = _dbContext.TransferBookings
            .AsNoTracking()
            .Where(transfer => transfer.SupplierId == supplierId && ValidReservationStatuses.Contains(transfer.Reserva!.Status))
            .Select(transfer => new SupplierAccountServiceListItemDto
            {
                PublicId = transfer.PublicId,
                Type = "Traslado",
                Description = ((transfer.VehicleType ?? string.Empty) + " (" + (transfer.PickupLocation ?? string.Empty) + " -> " + (transfer.DropoffLocation ?? string.Empty) + ")").Trim(),
                Confirmation = transfer.ConfirmationNumber,
                NetCost = transfer.NetCost,
                SalePrice = transfer.SalePrice,
                Date = transfer.CreatedAt,
                Status = transfer.Status,
                NumeroReserva = transfer.Reserva!.NumeroReserva,
                FileName = transfer.Reserva!.Name,
                ReservaPublicId = transfer.Reserva!.PublicId
            });

        var packages = _dbContext.PackageBookings
            .AsNoTracking()
            .Where(package => package.SupplierId == supplierId && ValidReservationStatuses.Contains(package.Reserva!.Status))
            .Select(package => new SupplierAccountServiceListItemDto
            {
                PublicId = package.PublicId,
                Type = "Paquete",
                Description = package.PackageName,
                Confirmation = package.ConfirmationNumber,
                NetCost = package.NetCost,
                SalePrice = package.SalePrice,
                Date = package.CreatedAt,
                Status = package.Status,
                NumeroReserva = package.Reserva!.NumeroReserva,
                FileName = package.Reserva!.Name,
                ReservaPublicId = package.Reserva!.PublicId
            });

        var services = _dbContext.Servicios
            .AsNoTracking()
            .Where(service => service.SupplierId == supplierId && ValidReservationStatuses.Contains(service.Reserva!.Status))
            .Select(service => new SupplierAccountServiceListItemDto
            {
                PublicId = service.PublicId,
                Type = service.ServiceType,
                Description = service.Description ?? service.ServiceType,
                Confirmation = service.ConfirmationNumber,
                NetCost = service.NetCost,
                SalePrice = service.SalePrice,
                Date = service.CreatedAt,
                Status = service.Status,
                NumeroReserva = service.Reserva!.NumeroReserva,
                FileName = service.Reserva!.Name,
                ReservaPublicId = service.Reserva!.PublicId
            });

        return flights
            .Concat(hotels)
            .Concat(transfers)
            .Concat(packages)
            .Concat(services);
    }

    private IQueryable<SupplierPaymentDto> BuildSupplierPaymentsQuery(int supplierId)
    {
        return _dbContext.SupplierPayments
            .AsNoTracking()
            .Where(payment => payment.SupplierId == supplierId)
            .Select(payment => new SupplierPaymentDto
            {
                PublicId = payment.PublicId,
                Amount = payment.Amount,
                Method = payment.Method,
                PaidAt = payment.PaidAt,
                Reference = payment.Reference,
                Notes = payment.Notes,
                NumeroReserva = payment.Reserva != null ? payment.Reserva.NumeroReserva : null,
                FileName = payment.Reserva != null ? payment.Reserva.Name : null,
                ReservaPublicId = payment.Reserva != null ? (Guid?)payment.Reserva.PublicId : null
            });
    }

    private static IQueryable<Supplier> ApplySupplierOrdering(IQueryable<Supplier> query, SupplierListQuery request)
    {
        var sortBy = (request.SortBy ?? "name").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "createdat" => desc
                ? query.OrderByDescending(supplier => supplier.CreatedAt).ThenByDescending(supplier => supplier.Name)
                : query.OrderBy(supplier => supplier.CreatedAt).ThenBy(supplier => supplier.Name),
            "currentbalance" => desc
                ? query.OrderByDescending(supplier => supplier.CurrentBalance).ThenBy(supplier => supplier.Name)
                : query.OrderBy(supplier => supplier.CurrentBalance).ThenBy(supplier => supplier.Name),
            _ => desc
                ? query.OrderByDescending(supplier => supplier.Name).ThenByDescending(supplier => supplier.CreatedAt)
                : query.OrderBy(supplier => supplier.Name).ThenByDescending(supplier => supplier.CreatedAt)
        };
    }

    private static IQueryable<SupplierAccountServiceListItemDto> ApplySupplierAccountServiceOrdering(
        IQueryable<SupplierAccountServiceListItemDto> query,
        SupplierAccountServicesQuery request)
    {
        var sortBy = (request.SortBy ?? "date").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "netcost" => desc
                ? query.OrderByDescending(item => item.NetCost).ThenByDescending(item => item.Date)
                : query.OrderBy(item => item.NetCost).ThenByDescending(item => item.Date),
            "saleprice" => desc
                ? query.OrderByDescending(item => item.SalePrice).ThenByDescending(item => item.Date)
                : query.OrderBy(item => item.SalePrice).ThenByDescending(item => item.Date),
            _ => desc
                ? query.OrderByDescending(item => item.Date).ThenBy(item => item.Type)
                : query.OrderBy(item => item.Date).ThenBy(item => item.Type)
        };
    }

    private static IQueryable<SupplierPaymentDto> ApplySupplierPaymentOrdering(
        IQueryable<SupplierPaymentDto> query,
        SupplierAccountPaymentsQuery request)
    {
        var sortBy = (request.SortBy ?? "paidAt").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "amount" => desc
                ? query.OrderByDescending(item => item.Amount).ThenByDescending(item => item.PaidAt)
                : query.OrderBy(item => item.Amount).ThenByDescending(item => item.PaidAt),
            _ => desc
                ? query.OrderByDescending(item => item.PaidAt).ThenByDescending(item => item.PublicId)
                : query.OrderBy(item => item.PaidAt).ThenBy(item => item.PublicId)
        };
    }

    private async Task<decimal> CalculateSupplierDebt(int supplierId, CancellationToken cancellationToken)
    {
        var totalPurchases = await BuildSupplierServicesQuery(supplierId)
            .Where(item => item.Status == "Confirmado" || item.Status == "Emitido" || item.Status == "HK" || item.Status == "TK" || item.Status == "KK" || item.Status == "KL")
            .SumAsync(item => (decimal?)item.NetCost, cancellationToken) ?? 0m;

        var totalPaid = await _dbContext.SupplierPayments
            .Where(payment => payment.SupplierId == supplierId)
            .SumAsync(payment => (decimal?)payment.Amount, cancellationToken) ?? 0m;

        return totalPurchases - totalPaid;
    }
}
