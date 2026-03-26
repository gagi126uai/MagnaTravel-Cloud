using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using System.Data;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class ReservaService : IReservaService
{
    private readonly AppDbContext _context;
    private readonly IMapper _mapper;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;

    public ReservaService(
        AppDbContext context,
        IMapper mapper,
        IOperationalFinanceSettingsService operationalFinanceSettingsService)
    {
        _context = context;
        _mapper = mapper;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
    }

    public async Task<ReservaListPageDto> GetReservasAsync(ReservaListQuery query, CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var summaryBaseQuery = ApplyReservaSearch(_context.Reservas.AsNoTracking(), query.Search);
        var filteredQuery = ApplyReservaView(summaryBaseQuery, query.View);

        var summary = new ReservaListSummaryDto
        {
            ActiveCount = await summaryBaseQuery.CountAsync(r =>
                r.Status != EstadoReserva.Closed &&
                r.Status != EstadoReserva.Cancelled &&
                r.Status != "Archived",
                cancellationToken),
            ReservedCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Reserved, cancellationToken),
            OperativeCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Operational, cancellationToken),
            ClosedCount = await summaryBaseQuery.CountAsync(r =>
                r.Status == EstadoReserva.Closed ||
                r.Status == EstadoReserva.Cancelled ||
                r.Status == "Archived",
                cancellationToken),
            TotalSaleActive = await summaryBaseQuery
                .Where(r => r.Status != EstadoReserva.Closed && r.Status != EstadoReserva.Cancelled && r.Status != "Archived")
                .SumAsync(r => (decimal?)r.TotalSale, cancellationToken) ?? 0m,
            TotalCostActive = await summaryBaseQuery
                .Where(r => r.Status != EstadoReserva.Closed && r.Status != EstadoReserva.Cancelled && r.Status != "Archived")
                .SumAsync(r => (decimal?)r.TotalCost, cancellationToken) ?? 0m,
            TotalPendingBalance = await summaryBaseQuery
                .Where(r => r.Status != EstadoReserva.Closed && r.Status != EstadoReserva.Cancelled && r.Status != "Archived" && r.Balance > 0)
                .SumAsync(r => (decimal?)r.Balance, cancellationToken) ?? 0m
        };
        summary.GrossProfit = summary.TotalSaleActive - summary.TotalCostActive;

        var reservasQuery = ApplyReservaOrdering(filteredQuery, query)
            .Select(f => new ReservaListDto
            {
                PublicId = f.PublicId,
                NumeroReserva = f.NumeroReserva,
                Name = f.Name,
                Status = f.Status,
                CustomerName = f.Payer != null ? f.Payer.FullName : string.Empty,
                ResponsibleUserId = f.ResponsibleUserId,
                ResponsibleUserName = f.ResponsibleUser != null ? f.ResponsibleUser.FullName : null,
                CreatedAt = f.CreatedAt,
                StartDate = f.StartDate,
                EndDate = f.EndDate,
                PassengerCount = f.Passengers.Count,
                TotalCost = f.TotalCost,
                TotalPaid = f.TotalPaid,
                TotalSale = f.TotalSale,
                Balance = f.Balance
            })
            .AsQueryable();

        var paged = await reservasQuery.ToPagedResponseAsync(query, cancellationToken);
        foreach (var reserva in paged.Items)
        {
            ApplyEconomicFlags(reserva, settings);
        }

        return ReservaListPageDto.Create(paged.Items, paged.Page, paged.PageSize, paged.TotalCount, summary);
    }

    public async Task<ReservaDto> GetReservaByIdAsync(int id)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(CancellationToken.None);
        var file = await _context.Reservas
            .AsNoTracking()
            .Include(f => f.Payer)
            .Include(f => f.ResponsibleUser)
            .Include(f => f.Passengers)
            .Include(f => f.Payments)
            .ThenInclude(p => p.Receipt)
            .Include(f => f.Invoices)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments).ThenInclude(fs => fs.Supplier)
            .Include(f => f.HotelBookings).ThenInclude(hb => hb.Supplier)
            .Include(f => f.TransferBookings).ThenInclude(tb => tb.Supplier)
            .Include(f => f.PackageBookings).ThenInclude(pb => pb.Supplier)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (file == null) 
        {
            throw new KeyNotFoundException($"File with ID {id} not found locally");
        }

        var dto = _mapper.Map<ReservaDto>(file);
        ApplyEconomicFlags(dto, settings);
        return dto;
    }

    public async Task<Reserva> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId)
    {
        await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        int? payerId = null;

        if (!string.IsNullOrWhiteSpace(request.PayerId))
        {
            payerId = await _context.Customers
                .AsNoTracking()
                .ResolveInternalIdAsync(request.PayerId, CancellationToken.None);

            if (!payerId.HasValue)
            {
                throw new KeyNotFoundException("Cliente no encontrado");
            }
        }

        var numeroReserva = await GenerateNumeroReservaAsync(CancellationToken.None);
        
        var fileName = !string.IsNullOrWhiteSpace(request.Name) 
            ? request.Name 
            : $"Reserva {numeroReserva}";

        var file = new Reserva
        {
            Name = fileName,
            NumeroReserva = numeroReserva,
            PayerId = payerId,
            ResponsibleUserId = createdByUserId,
            StartDate = request.StartDate,
            Description = request.Description,
            Status = EstadoReserva.Reserved
        };
        
        _context.Reservas.Add(file);
        await _context.SaveChangesAsync();
        await transaction.CommitAsync();
        
        return file;
    }

    public async Task<(ServicioReserva Reservation, string? Warning)> AddServiceAsync(int reservaId, AddServiceRequest request)
    {
        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");
        int? supplierId = null;

        if (!string.IsNullOrWhiteSpace(request.SupplierId))
        {
            supplierId = await _context.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(request.SupplierId, CancellationToken.None);

            if (!supplierId.HasValue)
            {
                throw new KeyNotFoundException("Proveedor no encontrado");
            }
        }

        if (string.IsNullOrWhiteSpace(request.ServiceType)) throw new ArgumentException("Debe seleccionar un tipo de servicio");
        if (request.DepartureDate == default) throw new ArgumentException("La fecha de salida es obligatoria");
        if (request.SalePrice <= 0) throw new ArgumentException("El precio de venta debe ser mayor a 0");
        if (request.NetCost < 0) throw new ArgumentException("El costo neto no puede ser negativo");

        string? warning = null;
        if (request.NetCost > request.SalePrice)
        {
            warning = $"Atención: el costo ({request.NetCost:C}) supera el precio de venta ({request.SalePrice:C}). Se está vendiendo a pérdida.";
        }

        var reservation = new ServicioReserva
        {
            ReservaId = reservaId,
            ServiceType = request.ServiceType,
            ProductType = request.ServiceType,
            SupplierId = supplierId,
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

        _context.Servicios.Add(reservation);
        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);

        return (reservation, warning);
    }

    public async Task<ServicioReserva> UpdateServiceAsync(int serviceId, AddServiceRequest request)
    {
        var service = await _context.Servicios
            .Include(r => r.Reserva)
            .FirstOrDefaultAsync(r => r.Id == serviceId);

        if (service == null) throw new KeyNotFoundException("Servicio no encontrado");

        int? supplierId = null;
        if (!string.IsNullOrWhiteSpace(request.SupplierId))
        {
            supplierId = await _context.Suppliers
                .AsNoTracking()
                .ResolveInternalIdAsync(request.SupplierId, CancellationToken.None);

            if (!supplierId.HasValue)
            {
                throw new KeyNotFoundException("Proveedor no encontrado");
            }
        }

        if (string.IsNullOrWhiteSpace(request.ServiceType)) throw new ArgumentException("Debe seleccionar un tipo de servicio");
        if (request.SalePrice <= 0) throw new ArgumentException("El precio de venta debe ser mayor a 0");

        service.ServiceType = request.ServiceType;
        service.ProductType = request.ServiceType;
        service.Description = request.Description ?? request.ServiceType;
        service.ConfirmationNumber = request.ConfirmationNumber ?? service.ConfirmationNumber;
        service.DepartureDate = request.DepartureDate.ToUniversalTime();
        service.ReturnDate = request.ReturnDate?.ToUniversalTime();
        service.SupplierId = supplierId;
        service.SalePrice = request.SalePrice;
        service.NetCost = request.NetCost;
        service.Commission = request.SalePrice - request.NetCost;

        await _context.SaveChangesAsync();
        if (service.ReservaId.HasValue) await UpdateBalanceAsync(service.ReservaId.Value);
        return service;
    }

    public async Task RemoveServiceAsync(int serviceId)
    {
        var service = await _context.Servicios
            .Include(r => r.Reserva)
            .FirstOrDefaultAsync(r => r.Id == serviceId);
            
        if (service == null) throw new KeyNotFoundException("Servicio no encontrado");

        if (service.ReservaId.HasValue)
        {
            var hasPayments = await _context.Payments.AnyAsync(p => p.ReservaId == service.ReservaId && !p.IsDeleted);
            if (hasPayments)
                throw new InvalidOperationException("No se pueden eliminar servicios de una reserva con pagos realizados.");
        }

        _context.Servicios.Remove(service);
        var resId = service.ReservaId;
        await _context.SaveChangesAsync();
        if (resId.HasValue) await UpdateBalanceAsync(resId.Value);
    }

    public async Task<IEnumerable<PassengerDto>> GetPassengersAsync(int reservaId)
    {
        return await _context.Passengers
            .Where(p => p.ReservaId == reservaId)
            .OrderBy(p => p.FullName)
            .ProjectTo<PassengerDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
    }

    public async Task<PassengerDto> AddPassengerAsync(int reservaId, Passenger passenger)
    {
        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        if (string.IsNullOrWhiteSpace(passenger.FullName)) throw new ArgumentException("El nombre del pasajero es obligatorio");
        if (passenger.FullName.Length < 3) throw new ArgumentException("El nombre debe tener al menos 3 caracteres");

        if (passenger.BirthDate.HasValue)
        {
            passenger.BirthDate = DateTime.SpecifyKind(passenger.BirthDate.Value, DateTimeKind.Utc);
        }

        passenger.ReservaId = reservaId;
        passenger.CreatedAt = DateTime.UtcNow;

        _context.Passengers.Add(passenger);
        await _context.SaveChangesAsync();

        return _mapper.Map<PassengerDto>(passenger);
    }

    public async Task<PassengerDto> UpdatePassengerAsync(int passengerId, Passenger updated)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) throw new KeyNotFoundException("Pasajero no encontrado");

        if (string.IsNullOrWhiteSpace(updated.FullName)) throw new ArgumentException("El nombre del pasajero es obligatorio");
        if (updated.FullName.Length < 3) throw new ArgumentException("El nombre debe tener al menos 3 caracteres");

        passenger.FullName = updated.FullName;
        passenger.DocumentType = updated.DocumentType;
        passenger.DocumentNumber = updated.DocumentNumber;
        
        if (updated.BirthDate.HasValue)
        {
            passenger.BirthDate = DateTime.SpecifyKind(updated.BirthDate.Value, DateTimeKind.Utc);
        }
        else 
        {
            passenger.BirthDate = null;
        }

        passenger.Nationality = updated.Nationality;
        passenger.Phone = updated.Phone;
        passenger.Email = updated.Email;
        passenger.Gender = updated.Gender;
        passenger.Notes = updated.Notes;

        await _context.SaveChangesAsync();
        return _mapper.Map<PassengerDto>(passenger);
    }

    public async Task RemovePassengerAsync(int passengerId)
    {
        var passenger = await _context.Passengers.FindAsync(passengerId);
        if (passenger == null) throw new KeyNotFoundException("Pasajero no encontrado");

        _context.Passengers.Remove(passenger);
        await _context.SaveChangesAsync();
    }

    public async Task<IEnumerable<PaymentDto>> GetReservaPaymentsAsync(int reservaId)
    {
        return await _context.Payments
            .Where(p => p.ReservaId == reservaId)
            .OrderByDescending(p => p.PaidAt)
            .ProjectTo<PaymentDto>(_mapper.ConfigurationProvider)
            .ToListAsync();
    }

    public async Task<PaymentDto> AddPaymentAsync(int reservaId, Payment payment)
    {
        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        if (payment.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");
        if (string.IsNullOrWhiteSpace(payment.Method)) throw new ArgumentException("Debe seleccionar un método de pago");
        
        payment.ReservaId = reservaId;
        payment.PaidAt = payment.PaidAt == default ? DateTime.UtcNow : payment.PaidAt.ToUniversalTime();
        payment.Status = "Paid";
        payment.EntryType = PaymentEntryTypes.Payment;
        payment.AffectsCash = true;

        _context.Payments.Add(payment);
        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);

        return _mapper.Map<PaymentDto>(payment);
    }

    public async Task<PaymentDto> UpdatePaymentAsync(int reservaId, int paymentId, Payment updatedPayment)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");
        
        if (payment.ReservaId != reservaId) throw new ArgumentException("El pago no corresponde a la Reserva");

        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        if (updatedPayment.Amount <= 0) throw new ArgumentException("El monto debe ser mayor a 0");
        
        payment.Amount = updatedPayment.Amount;
        payment.Method = updatedPayment.Method;
        payment.PaidAt = updatedPayment.PaidAt.ToUniversalTime();
        payment.Reference = updatedPayment.Reference;
        payment.Notes = updatedPayment.Notes;

        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);
        return _mapper.Map<PaymentDto>(payment);
    }

    public async Task DeletePaymentAsync(int reservaId, int paymentId)
    {
        var payment = await _context.Payments.FindAsync(paymentId);
        if (payment == null) throw new KeyNotFoundException("Pago no encontrado");
        
        if (payment.ReservaId != reservaId) throw new ArgumentException("El pago no corresponde a la Reserva");

        var file = await _context.Reservas.FindAsync(reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        payment.IsDeleted = true;
        payment.DeletedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);
    }

    public async Task<Reserva> UpdateStatusAsync(int id, string status)
    {
        var file = await _context.Reservas.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        await UpdateBalanceAsync(id);
        file = await _context.Reservas.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        var validStatuses = new[] { EstadoReserva.Budget, EstadoReserva.Reserved, EstadoReserva.Operational, EstadoReserva.Closed, EstadoReserva.Cancelled };
        if (!validStatuses.Contains(status)) throw new ArgumentException("Estado no válido");

        if (file.Status == EstadoReserva.Reserved && status == EstadoReserva.Budget)
        {
             var hasPayments = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted);
             if (hasPayments) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay pagos registrados. Elimínalos primero.");

             var hasInvoices = await _context.Invoices.AnyAsync(i => i.ReservaId == id);
             if (hasInvoices) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay facturas emitidas. Debes anularlas primero (Nota de Crédito).");

             var hasServices = await HasServicesAsync(id);
             if (hasServices) throw new InvalidOperationException("No se puede volver a Presupuesto porque tiene servicios cargados. Elimínalos primero.");
        }

        if (status == EstadoReserva.Operational)
        {
            var fullReserva = await _context.Reservas
                .Include(r => r.Servicios)
                .Include(r => r.HotelBookings)
                .Include(r => r.FlightSegments)
                .Include(r => r.TransferBookings)
                .Include(r => r.PackageBookings)
                .FirstOrDefaultAsync(r => r.Id == id);
            if (fullReserva == null) throw new KeyNotFoundException("Reserva no encontrada");

            var emptyReason = EconomicRulesHelper.GetEmptyReservaBlockReason(fullReserva);
            if (!string.IsNullOrWhiteSpace(emptyReason))
                throw new InvalidOperationException($"No se puede pasar a Operativo: {emptyReason}");

            var settings = await _operationalFinanceSettingsService.GetEntityAsync(CancellationToken.None);
            var blockReason = EconomicRulesHelper.GetOperativeBlockReason(file, settings);
            if (!string.IsNullOrWhiteSpace(blockReason))
                throw new InvalidOperationException(blockReason);
        }

        if (status == EstadoReserva.Closed)
        {
            if (file.Balance > 0)
                throw new InvalidOperationException($"No se puede cerrar la reserva porque tiene un saldo pendiente de {file.Balance:N2}.");
            file.ClosedAt = DateTime.UtcNow;
        }

        file.Status = status;

        await _context.SaveChangesAsync();
        return file;
    }

    public async Task<Reserva> ArchiveReservaAsync(int id)
    {
        var file = await _context.Reservas
            .Include(r => r.Payments)
            .Include(r => r.Servicios)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        var archiveBlock = EconomicRulesHelper.GetArchiveBlockReason(file);
        if (!string.IsNullOrWhiteSpace(archiveBlock))
            throw new InvalidOperationException(archiveBlock);

        file.Status = "Archived";
        await _context.SaveChangesAsync();
        return file;
    }

    public async Task DeleteReservaAsync(int id)
    {
        using var transaction = await _context.Database.BeginTransactionAsync();
        try
        {
            var file = await _context.Reservas
                .Include(f => f.Payments)
                .Include(f => f.Servicios)
                .Include(f => f.Passengers)
                .Include(f => f.FlightSegments)
                .Include(f => f.HotelBookings)
                .Include(f => f.TransferBookings)
                .Include(f => f.PackageBookings)
                .FirstOrDefaultAsync(f => f.Id == id);

            if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

            if (file.Status != EstadoReserva.Reserved && file.Status != EstadoReserva.Budget)
            {
                throw new InvalidOperationException("Solo se pueden eliminar reservas en estado Presupuesto o Reservado.");
            }

            if (file.Payments.Any())
            {
                throw new InvalidOperationException("No se puede eliminar una Reserva con pagos registrados. Elimine los pagos primero.");
            }

            if (file.Servicios.Any()) _context.Servicios.RemoveRange(file.Servicios);
            if (file.Passengers.Any()) _context.Passengers.RemoveRange(file.Passengers);
            if (file.FlightSegments.Any()) _context.FlightSegments.RemoveRange(file.FlightSegments);
            if (file.HotelBookings.Any()) _context.HotelBookings.RemoveRange(file.HotelBookings);
            if (file.TransferBookings.Any()) _context.TransferBookings.RemoveRange(file.TransferBookings);
            if (file.PackageBookings.Any()) _context.PackageBookings.RemoveRange(file.PackageBookings);

            _context.Reservas.Remove(file);
            
            await _context.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (Exception)
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task UpdateBalanceAsync(int reservaId)
    {
        var file = await _context.Reservas
            .Include(f => f.Payments)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments)
            .Include(f => f.HotelBookings)
            .Include(f => f.TransferBookings)
            .Include(f => f.PackageBookings)
            .FirstOrDefaultAsync(f => f.Id == reservaId);

        if (file == null) return;

        var totalSale = 
            (file.FlightSegments?.Sum(f => f.SalePrice) ?? 0) +
            (file.HotelBookings?.Sum(h => h.SalePrice) ?? 0) +
            (file.TransferBookings?.Sum(t => t.SalePrice) ?? 0) +
            (file.PackageBookings?.Sum(p => p.SalePrice) ?? 0) +
            (file.Servicios?.Sum(r => r.SalePrice) ?? 0);

        var totalCost = 
            (file.FlightSegments?.Sum(f => f.NetCost) ?? 0) +
            (file.HotelBookings?.Sum(h => h.NetCost) ?? 0) +
            (file.TransferBookings?.Sum(t => t.NetCost) ?? 0) +
            (file.PackageBookings?.Sum(p => p.NetCost) ?? 0) +
            (file.Servicios?.Sum(r => r.NetCost) ?? 0);

        var totalPaid = file.Payments?.Where(p => p.Status != "Cancelled" && !p.IsDeleted).Sum(p => p.Amount) ?? 0;

        file.TotalSale = totalSale;
        file.TotalCost = totalCost;
        file.TotalPaid = totalPaid;
        file.Balance = totalSale - totalPaid;

        await _context.SaveChangesAsync();
    }

    private static void ApplyEconomicFlags(ReservaDto dto, OperationalFinanceSettings settings)
    {
        var reserva = new Reserva { Balance = dto.Balance };
        dto.IsEconomicallySettled = EconomicRulesHelper.IsEconomicallySettled(reserva);
        dto.CanMoveToOperativo = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetOperativeBlockReason(reserva, settings));
        dto.CanEmitVoucher = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetVoucherBlockReason(reserva, settings));
        var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);
        dto.CanEmitAfipInvoice = afip.CanEmit || afip.RequiresOverride;
        dto.EconomicBlockReason = EconomicRulesHelper.GetCombinedEconomicBlockReason(reserva, settings);
    }

    private static void ApplyEconomicFlags(ReservaListDto dto, OperationalFinanceSettings settings)
    {
        var reserva = new Reserva { Balance = dto.Balance };
        dto.IsEconomicallySettled = EconomicRulesHelper.IsEconomicallySettled(reserva);
        dto.CanMoveToOperativo = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetOperativeBlockReason(reserva, settings));
        dto.CanEmitVoucher = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetVoucherBlockReason(reserva, settings));
        var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);
        dto.CanEmitAfipInvoice = afip.CanEmit || afip.RequiresOverride;
        dto.EconomicBlockReason = EconomicRulesHelper.GetCombinedEconomicBlockReason(reserva, settings);
    }

    private IQueryable<Reserva> ApplyReservaSearch(IQueryable<Reserva> query, string? search)
    {
        if (string.IsNullOrWhiteSpace(search))
        {
            return query;
        }

        var normalized = search.Trim().ToLower();
        return query.Where(r =>
            r.Name.ToLower().Contains(normalized) ||
            r.NumeroReserva.ToLower().Contains(normalized) ||
            (r.Payer != null && r.Payer.FullName.ToLower().Contains(normalized)));
    }

    private static IQueryable<Reserva> ApplyReservaView(IQueryable<Reserva> query, string? view)
    {
        return (view ?? "active").Trim().ToLowerInvariant() switch
        {
            "reserved" => query.Where(r => r.Status == EstadoReserva.Reserved),
            "operative" => query.Where(r => r.Status == EstadoReserva.Operational),
            "closed" => query.Where(r =>
                r.Status == EstadoReserva.Closed ||
                r.Status == EstadoReserva.Cancelled),
            "archived" => query.Where(r => r.Status == "Archived"),
            _ => query.Where(r =>
                r.Status != EstadoReserva.Closed &&
                r.Status != EstadoReserva.Cancelled &&
                r.Status != "Archived")
        };
    }

    private static IQueryable<Reserva> ApplyReservaOrdering(IQueryable<Reserva> query, ReservaListQuery request)
    {
        var sortBy = (request.SortBy ?? "startDate").Trim().ToLowerInvariant();
        var desc = request.IsSortDescending();

        return sortBy switch
        {
            "createdat" => desc
                ? query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
                : query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id),
            "numeroreserva" => desc
                ? query.OrderByDescending(r => r.NumeroReserva).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.NumeroReserva).ThenByDescending(r => r.CreatedAt),
            "totalsale" => desc
                ? query.OrderByDescending(r => r.TotalSale).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.TotalSale).ThenByDescending(r => r.CreatedAt),
            "balance" => desc
                ? query.OrderByDescending(r => r.Balance).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.Balance).ThenByDescending(r => r.CreatedAt),
            _ => desc
                ? query.OrderBy(r => r.StartDate == null).ThenByDescending(r => r.StartDate).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.StartDate == null).ThenBy(r => r.StartDate).ThenByDescending(r => r.CreatedAt)
        };
    }

    private async Task<bool> HasServicesAsync(int reservaId)
    {
        return await _context.Servicios.AnyAsync(s => s.ReservaId == reservaId)
            || await _context.HotelBookings.AnyAsync(h => h.ReservaId == reservaId)
            || await _context.FlightSegments.AnyAsync(f => f.ReservaId == reservaId)
            || await _context.TransferBookings.AnyAsync(t => t.ReservaId == reservaId)
            || await _context.PackageBookings.AnyAsync(p => p.ReservaId == reservaId);
    }

    private async Task<string> GenerateNumeroReservaAsync(CancellationToken cancellationToken)
    {
        var year = DateTime.UtcNow.Year;
        var sequence = await _context.BusinessSequences
            .FirstOrDefaultAsync(item => item.DocumentType == "Reserva" && item.Year == year, cancellationToken);

        if (sequence is null)
        {
            sequence = new BusinessSequence
            {
                DocumentType = "Reserva",
                Year = year,
                LastValue = 1000,
                UpdatedAt = DateTime.UtcNow
            };

            _context.BusinessSequences.Add(sequence);
        }
        else
        {
            sequence.LastValue += 1;
            if (sequence.LastValue < 1000)
            {
                sequence.LastValue = 1000;
            }

            sequence.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return $"F-{year}-{sequence.LastValue}";
    }
}
