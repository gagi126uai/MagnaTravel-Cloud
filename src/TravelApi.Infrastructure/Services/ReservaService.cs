using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.EntityFrameworkCore;
using System.Data;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
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

    public async Task<ReservaDto> GetReservaByIdAsync(string publicIdOrLegacyId, CancellationToken cancellationToken)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, cancellationToken);
        return await GetReservaByIdAsync(id);
    }

    public async Task<ReservaDto> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId, CancellationToken cancellationToken)
    {
        var reserva = await CreateReservaAsync(request, createdByUserId);
        return await GetReservaByIdAsync(reserva.Id);
    }

    public async Task<ReservationServiceMutationResult> AddServiceAsync(string reservaPublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var (reservation, warning) = await AddServiceAsync(reservaId, request, ct);

        return new ReservationServiceMutationResult
        {
            Servicio = _mapper.Map<ServicioReservaDto>(reservation),
            Warning = warning
        };
    }

    public async Task<ServicioReservaDto> UpdateServiceAsync(string servicePublicIdOrLegacyId, AddServiceRequest request, CancellationToken ct = default)
    {
        var serviceId = await ResolveRequiredIdAsync<ServicioReserva>(servicePublicIdOrLegacyId, ct);
        var service = await UpdateServiceAsync(serviceId, request, ct);
        return _mapper.Map<ServicioReservaDto>(service);
    }

    public async Task RemoveServiceAsync(string servicePublicIdOrLegacyId, CancellationToken ct = default)
    {
        var serviceId = await ResolveRequiredIdAsync<ServicioReserva>(servicePublicIdOrLegacyId, ct);
        await RemoveServiceAsync(serviceId, ct);
    }

    public async Task<IEnumerable<PassengerDto>> GetPassengersAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetPassengersAsync(reservaId);
    }

    public async Task<PassengerDto> AddPassengerAsync(string reservaPublicIdOrLegacyId, PassengerUpsertRequest passenger, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await AddPassengerAsync(reservaId, MapPassenger(passenger));
    }

    public async Task<PassengerDto> UpdatePassengerAsync(string passengerPublicIdOrLegacyId, PassengerUpsertRequest updated, CancellationToken ct = default)
    {
        var passengerId = await ResolveRequiredIdAsync<Passenger>(passengerPublicIdOrLegacyId, ct);
        return await UpdatePassengerAsync(passengerId, MapPassenger(updated));
    }

    public async Task RemovePassengerAsync(string passengerPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var passengerId = await ResolveRequiredIdAsync<Passenger>(passengerPublicIdOrLegacyId, ct);
        await RemovePassengerAsync(passengerId);
    }

    public async Task<ReservaDto> UpdatePassengerCountsAsync(string reservaPublicIdOrLegacyId, PassengerCountsRequest counts, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        if (reserva.Status != EstadoReserva.Budget)
            throw new InvalidOperationException("Las cantidades de pasajeros solo se pueden editar en estado Presupuesto. Si ya pasó a Reservado, cargá los pasajeros nominales.");

        if (counts.AdultCount < 0 || counts.ChildCount < 0 || counts.InfantCount < 0)
            throw new ArgumentException("Las cantidades no pueden ser negativas.");

        reserva.AdultCount = counts.AdultCount;
        reserva.ChildCount = counts.ChildCount;
        reserva.InfantCount = counts.InfantCount;

        await _context.SaveChangesAsync(ct);
        return await GetReservaByIdAsync(reservaId);
    }

    // ============= Phase 2.1 — Pasajero <-> Servicio =============

    public async Task<IReadOnlyList<PassengerServiceAssignmentDto>> GetAssignmentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);

        var passengerIds = await _context.Passengers
            .AsNoTracking()
            .Where(p => p.ReservaId == reservaId)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (passengerIds.Count == 0) return Array.Empty<PassengerServiceAssignmentDto>();

        var assignments = await _context.PassengerServiceAssignments
            .AsNoTracking()
            .Include(a => a.Passenger)
            .Where(a => passengerIds.Contains(a.PassengerId))
            .OrderBy(a => a.ServiceType).ThenBy(a => a.ServiceId).ThenBy(a => a.Id)
            .ToListAsync(ct);

        var publicIdLookup = await BuildServicePublicIdLookupAsync(assignments, ct);

        return assignments.Select(a => MapAssignment(a, ResolveServicePublicId(publicIdLookup, a.ServiceType, a.ServiceId))).ToList();
    }

    /// <summary>
    /// Construye un lookup (serviceType, serviceId) -> publicId con 1 query por tipo presente.
    /// Ej: si hay assignments contra 3 hoteles y 2 transfers, hace 2 queries totales (no 5).
    /// </summary>
    private async Task<Dictionary<string, Dictionary<int, Guid>>> BuildServicePublicIdLookupAsync(
        IReadOnlyCollection<PassengerServiceAssignment> assignments,
        CancellationToken ct)
    {
        var byType = assignments
            .GroupBy(a => a.ServiceType)
            .ToDictionary(g => g.Key, g => g.Select(a => a.ServiceId).Distinct().ToList());

        var result = new Dictionary<string, Dictionary<int, Guid>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (serviceType, ids) in byType)
        {
            if (ids.Count == 0) continue;

            var lookup = serviceType switch
            {
                AssignmentServiceType.Hotel => await _context.HotelBookings.AsNoTracking()
                    .Where(b => ids.Contains(b.Id))
                    .Select(b => new { b.Id, b.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Transfer => await _context.TransferBookings.AsNoTracking()
                    .Where(b => ids.Contains(b.Id))
                    .Select(b => new { b.Id, b.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Package => await _context.PackageBookings.AsNoTracking()
                    .Where(b => ids.Contains(b.Id))
                    .Select(b => new { b.Id, b.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Flight => await _context.FlightSegments.AsNoTracking()
                    .Where(f => ids.Contains(f.Id))
                    .Select(f => new { f.Id, f.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                AssignmentServiceType.Generic => await _context.Servicios.AsNoTracking()
                    .Where(s => ids.Contains(s.Id))
                    .Select(s => new { s.Id, s.PublicId })
                    .ToDictionaryAsync(x => x.Id, x => x.PublicId, ct),
                _ => new Dictionary<int, Guid>()
            };

            result[serviceType] = lookup;
        }

        return result;
    }

    private static Guid? ResolveServicePublicId(
        Dictionary<string, Dictionary<int, Guid>> lookup,
        string serviceType,
        int serviceId)
    {
        if (!lookup.TryGetValue(serviceType, out var byId)) return null;
        return byId.TryGetValue(serviceId, out var publicId) ? publicId : (Guid?)null;
    }

    public async Task<PassengerServiceAssignmentDto> CreateAssignmentAsync(string reservaPublicIdOrLegacyId, CreatePassengerAssignmentRequest request, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);

        if (string.IsNullOrWhiteSpace(request.ServiceType) || !AssignmentServiceType.All.Contains(request.ServiceType))
            throw new ArgumentException($"ServiceType invalido. Valores aceptados: {string.Join(", ", AssignmentServiceType.All)}.");

        // Resolver passenger y validar que pertenezca a la Reserva
        var passengerId = await ResolveRequiredIdAsync<Passenger>(request.PassengerPublicIdOrLegacyId, ct);
        var passenger = await _context.Passengers
            .FirstOrDefaultAsync(p => p.Id == passengerId, ct)
            ?? throw new KeyNotFoundException("Pasajero no encontrado");
        if (passenger.ReservaId != reservaId)
            throw new InvalidOperationException("El pasajero no pertenece a esta reserva.");

        // Resolver el ServiceId segun tipo (cada tipo tiene su tabla)
        var serviceId = request.ServiceType switch
        {
            AssignmentServiceType.Hotel => await ResolveRequiredIdAsync<HotelBooking>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Transfer => await ResolveRequiredIdAsync<TransferBooking>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Package => await ResolveRequiredIdAsync<PackageBooking>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Flight => await ResolveRequiredIdAsync<FlightSegment>(request.ServicePublicIdOrLegacyId, ct),
            AssignmentServiceType.Generic => await ResolveRequiredIdAsync<ServicioReserva>(request.ServicePublicIdOrLegacyId, ct),
            _ => throw new ArgumentException("ServiceType no soportado.")
        };

        // Validar que el servicio pertenezca a la Reserva (defensa en profundidad)
        var serviceBelongsToReserva = request.ServiceType switch
        {
            AssignmentServiceType.Hotel => await _context.HotelBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Transfer => await _context.TransferBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Package => await _context.PackageBookings.AnyAsync(b => b.Id == serviceId && b.ReservaId == reservaId, ct),
            AssignmentServiceType.Flight => await _context.FlightSegments.AnyAsync(f => f.Id == serviceId && f.ReservaId == reservaId, ct),
            AssignmentServiceType.Generic => await _context.Servicios.AnyAsync(s => s.Id == serviceId && s.ReservaId == reservaId, ct),
            _ => false
        };
        if (!serviceBelongsToReserva)
            throw new InvalidOperationException("El servicio no pertenece a esta reserva.");

        // Idempotencia: si ya existe la asignacion, devolver la existente
        var existing = await _context.PassengerServiceAssignments
            .Include(a => a.Passenger)
            .FirstOrDefaultAsync(a => a.PassengerId == passengerId && a.ServiceType == request.ServiceType && a.ServiceId == serviceId, ct);
        if (existing != null)
        {
            var existingPublicId = await ResolveServicePublicIdAsync(request.ServiceType, serviceId, ct);
            return MapAssignment(existing, existingPublicId);
        }

        var assignment = new PassengerServiceAssignment
        {
            PassengerId = passengerId,
            ServiceType = request.ServiceType,
            ServiceId = serviceId,
            RoomNumber = request.RoomNumber,
            SeatNumber = request.SeatNumber?.Trim(),
            Notes = request.Notes?.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _context.PassengerServiceAssignments.Add(assignment);
        await _context.SaveChangesAsync(ct);

        // Re-cargar con Passenger include para el mapeo
        var saved = await _context.PassengerServiceAssignments
            .Include(a => a.Passenger)
            .FirstAsync(a => a.Id == assignment.Id, ct);

        var servicePublicId = await ResolveServicePublicIdAsync(request.ServiceType, serviceId, ct);
        return MapAssignment(saved, servicePublicId);
    }

    private async Task<Guid?> ResolveServicePublicIdAsync(string serviceType, int serviceId, CancellationToken ct)
    {
        return serviceType switch
        {
            AssignmentServiceType.Hotel => await _context.HotelBookings.AsNoTracking()
                .Where(b => b.Id == serviceId).Select(b => (Guid?)b.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Transfer => await _context.TransferBookings.AsNoTracking()
                .Where(b => b.Id == serviceId).Select(b => (Guid?)b.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Package => await _context.PackageBookings.AsNoTracking()
                .Where(b => b.Id == serviceId).Select(b => (Guid?)b.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Flight => await _context.FlightSegments.AsNoTracking()
                .Where(f => f.Id == serviceId).Select(f => (Guid?)f.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Generic => await _context.Servicios.AsNoTracking()
                .Where(s => s.Id == serviceId).Select(s => (Guid?)s.PublicId).FirstOrDefaultAsync(ct),
            _ => null
        };
    }

    public async Task RemoveAssignmentAsync(string assignmentPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var assignmentId = await ResolveRequiredIdAsync<PassengerServiceAssignment>(assignmentPublicIdOrLegacyId, ct);
        var assignment = await _context.PassengerServiceAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId, ct);
        if (assignment == null) throw new KeyNotFoundException("Asignacion no encontrada");

        _context.PassengerServiceAssignments.Remove(assignment);
        await _context.SaveChangesAsync(ct);
    }

    private static PassengerServiceAssignmentDto MapAssignment(PassengerServiceAssignment a, Guid? servicePublicId = null)
    {
        return new PassengerServiceAssignmentDto
        {
            PublicId = a.PublicId,
            PassengerPublicId = a.Passenger?.PublicId ?? Guid.Empty,
            PassengerFullName = a.Passenger?.FullName ?? string.Empty,
            ServiceType = a.ServiceType,
            ServiceId = a.ServiceId,
            ServicePublicId = servicePublicId,
            RoomNumber = a.RoomNumber,
            SeatNumber = a.SeatNumber,
            Notes = a.Notes,
            CreatedAt = a.CreatedAt
        };
    }

    // ============= /Phase 2.1 =============

    public async Task<IEnumerable<PaymentDto>> GetReservaPaymentsAsync(string reservaPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await GetReservaPaymentsAsync(reservaId);
    }

    public async Task<PaymentDto> AddPaymentAsync(string reservaPublicIdOrLegacyId, ReservationPaymentUpsertRequest payment, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        return await AddPaymentAsync(reservaId, MapPayment(payment));
    }

    public async Task<PaymentDto> UpdatePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, ReservationPaymentUpsertRequest updatedPayment, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, ct);
        return await UpdatePaymentAsync(reservaId, paymentId, MapPayment(updatedPayment));
    }

    public async Task DeletePaymentAsync(string reservaPublicIdOrLegacyId, string paymentPublicIdOrLegacyId, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var paymentId = await ResolveRequiredIdAsync<Payment>(paymentPublicIdOrLegacyId, ct);
        await DeletePaymentAsync(reservaId, paymentId);
    }

    public async Task<ReservaDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        await UpdateStatusAsync(id, status);
        return await GetReservaByIdAsync(id);
    }

    public async Task<TransitionReadinessDto> GetTransitionReadinessAsync(string publicIdOrLegacyId, string targetStatus, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas
            .Include(r => r.Passengers)
            .Include(r => r.Servicios)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        var dto = new TransitionReadinessDto
        {
            TargetStatus = targetStatus,
            Allowed = true,
            ExpectedPassengerCount = reserva.AdultCount + reserva.ChildCount + reserva.InfantCount,
            CurrentPassengerCount = reserva.Passengers?.Count ?? 0
        };

        // Reglas de transicion Budget -> Reserved
        if (targetStatus == EstadoReserva.Reserved && reserva.Status == EstadoReserva.Budget)
        {
            if (!(reserva.Servicios?.Any() ?? false))
            {
                dto.Allowed = false;
                dto.BlockingReasons.Add("Cargá al menos un servicio (hotel, vuelo, transfer o paquete) antes de confirmar la reserva.");
            }

            if (dto.ExpectedPassengerCount > 0 && dto.CurrentPassengerCount < dto.ExpectedPassengerCount)
            {
                dto.MissingPassengers = dto.ExpectedPassengerCount - dto.CurrentPassengerCount;
                dto.Allowed = false;
                dto.BlockingReasons.Add(
                    $"Faltan {dto.MissingPassengers} pasajero(s) nominales (cargados: {dto.CurrentPassengerCount} / esperados: {dto.ExpectedPassengerCount}).");
            }
        }

        return dto;
    }

    public async Task<ReservaDto> ArchiveReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        await ArchiveReservaAsync(id);
        return await GetReservaByIdAsync(id);
    }

    public async Task DeleteReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        await DeleteReservaAsync(id);
    }

    public async Task<ReservaListPageDto> GetReservasAsync(ReservaListQuery query, CancellationToken cancellationToken)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var summaryBaseQuery = ApplyReservaSearch(_context.Reservas.AsNoTracking(), query.Search);
        
        if (query.CreatedFrom.HasValue)
        {
            var from = query.CreatedFrom.Value.ToUniversalTime();
            summaryBaseQuery = summaryBaseQuery.Where(r => r.CreatedAt >= from);
        }

        if (query.CreatedTo.HasValue)
        {
            var to = query.CreatedTo.Value.ToUniversalTime();
            summaryBaseQuery = summaryBaseQuery.Where(r => r.CreatedAt <= to);
        }

        if (query.TravelFrom.HasValue)
        {
            var from = query.TravelFrom.Value.ToUniversalTime();
            summaryBaseQuery = summaryBaseQuery.Where(r => r.StartDate.HasValue && r.StartDate.Value >= from);
        }

        if (query.TravelTo.HasValue)
        {
            var to = query.TravelTo.Value.ToUniversalTime();
            summaryBaseQuery = summaryBaseQuery.Where(r => r.StartDate.HasValue && r.StartDate.Value <= to);
        }

        var filteredQuery = ApplyReservaView(summaryBaseQuery, query.View);

        var summary = new ReservaListSummaryDto
        {
            BudgetCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Budget, cancellationToken),
            ActiveCount = await summaryBaseQuery.CountAsync(r =>
                r.Status == EstadoReserva.Reserved ||
                r.Status == EstadoReserva.Operational,
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

    private async Task<int> ResolveRequiredIdAsync<TEntity>(string publicIdOrLegacyId, CancellationToken cancellationToken)
        where TEntity : class, IHasPublicId
    {
        var resolved = await _context.Set<TEntity>()
            .AsNoTracking()
            .ResolveInternalIdAsync(publicIdOrLegacyId, cancellationToken);

        if (!resolved.HasValue && int.TryParse(publicIdOrLegacyId, out var legacyId))
        {
            resolved = legacyId;
        }

        return resolved ?? throw new KeyNotFoundException($"{typeof(TEntity).Name} no encontrado");
    }

    private static Passenger MapPassenger(PassengerUpsertRequest passenger)
    {
        return new Passenger
        {
            FullName = passenger.FullName,
            DocumentType = passenger.DocumentType,
            DocumentNumber = passenger.DocumentNumber,
            BirthDate = passenger.BirthDate,
            Nationality = passenger.Nationality,
            Phone = passenger.Phone,
            Email = passenger.Email,
            Gender = passenger.Gender,
            Notes = passenger.Notes
        };
    }

    private static Payment MapPayment(ReservationPaymentUpsertRequest payment)
    {
        return new Payment
        {
            Amount = payment.Amount,
            PaidAt = payment.PaidAt,
            Method = payment.Method,
            Reference = payment.Reference,
            Notes = payment.Notes
        };
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
        var strategy = _context.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
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
                    Status = string.IsNullOrWhiteSpace(request.Status) 
                        ? EstadoReserva.Budget 
                        : request.Status
                };
                
                _context.Reservas.Add(file);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                
                return file;
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
    }

    public async Task<(ServicioReserva Reservation, string? Warning)> AddServiceAsync(int reservaId, AddServiceRequest request, CancellationToken ct = default)
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

    public async Task<ServicioReserva> UpdateServiceAsync(int serviceId, AddServiceRequest request, CancellationToken ct = default)
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

    public async Task RemoveServiceAsync(int serviceId, CancellationToken ct = default)
    {
        // 1. Try generic service
        var service = await _context.Servicios.FindAsync(new object[] { serviceId }, ct);
        if (service != null)
        {
            await EnsureNoPaymentsAsync(service.ReservaId ?? 0, ct);
            _context.Servicios.Remove(service);
            var resId = service.ReservaId;
            await _context.SaveChangesAsync(ct);
            if (resId.HasValue) await UpdateBalanceAsync(resId.Value);
            return;
        }

        // 2. Try Flight
        var flight = await _context.FlightSegments.FindAsync(new object[] { serviceId }, ct);
        if (flight != null)
        {
            await EnsureNoPaymentsAsync(flight.ReservaId, ct);
            _context.FlightSegments.Remove(flight);
            var resId = flight.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 3. Try Hotel
        var hotel = await _context.HotelBookings.FindAsync(new object[] { serviceId }, ct);
        if (hotel != null)
        {
            await EnsureNoPaymentsAsync(hotel.ReservaId, ct);
            _context.HotelBookings.Remove(hotel);
            var resId = hotel.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 4. Try Transfer
        var transfer = await _context.TransferBookings.FindAsync(new object[] { serviceId }, ct);
        if (transfer != null)
        {
            await EnsureNoPaymentsAsync(transfer.ReservaId, ct);
            _context.TransferBookings.Remove(transfer);
            var resId = transfer.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 5. Try Package
        var package = await _context.PackageBookings.FindAsync(new object[] { serviceId }, ct);
        if (package != null)
        {
            await EnsureNoPaymentsAsync(package.ReservaId, ct);
            _context.PackageBookings.Remove(package);
            var resId = package.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        throw new KeyNotFoundException("Servicio no encontrado en ninguna categoría.");
    }

    private static int ComputeMaxExpectedPaxCount(Reserva reserva)
    {
        var hotel = reserva.HotelBookings?.Sum(h => h.GetExpectedPaxCount()) ?? 0;
        var transfer = reserva.TransferBookings?.Max(t => (int?)t.GetExpectedPaxCount()) ?? 0;
        var package = reserva.PackageBookings?.Sum(p => p.GetExpectedPaxCount()) ?? 0;
        return Math.Max(hotel, Math.Max(transfer, package));
    }

    // La logica de capacidad pasajeros vs servicios vive en ReservaCapacityRules
    // (clase estatica compartida con ReservaLifecycleAutomationService).

    private async Task EnsureNoPaymentsAsync(int reservaId, CancellationToken ct)
    {
        if (reservaId == 0) return;
        var hasPayments = await _context.Payments.AnyAsync(p => p.ReservaId == reservaId && !p.IsDeleted, ct);
        if (hasPayments)
            throw new InvalidOperationException("No se pueden eliminar servicios de una reserva con pagos realizados.");

        var hasIssuedVoucher = await _context.Vouchers.AnyAsync(v => v.ReservaId == reservaId && v.Status == "Issued", ct);
        if (hasIssuedVoucher)
            throw new InvalidOperationException("No se pueden eliminar servicios de una reserva con vouchers ya emitidos. Anula los vouchers primero.");
    }

    public async Task RemoveServiceAsync_OldVersion(int serviceId, CancellationToken ct = default)

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
        var file = await _context.Reservas
            .Include(r => r.Passengers)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .FirstOrDefaultAsync(r => r.Id == reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        if (file.Status == EstadoReserva.Budget)
            throw new InvalidOperationException("No se pueden cargar pasajeros nominales en una Reserva en estado Presupuesto. Usa el contador de cantidades o pasala a Reservado primero.");

        if (string.IsNullOrWhiteSpace(passenger.FullName)) throw new ArgumentException("El nombre del pasajero es obligatorio");
        if (passenger.FullName.Length < 3) throw new ArgumentException("El nombre debe tener al menos 3 caracteres");

        var maxExpected = ComputeMaxExpectedPaxCount(file);
        if (maxExpected > 0 && file.Passengers.Count >= maxExpected)
        {
            throw new InvalidOperationException(
                $"La reserva ya tiene los {maxExpected} pasajeros que esperan los servicios cargados. " +
                "Para sumar mas, ampliá la capacidad de algun servicio o agregá uno nuevo.");
        }

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
        var passenger = await _context.Passengers
            .Include(p => p.Reserva)
            .FirstOrDefaultAsync(p => p.Id == passengerId);
        if (passenger == null) throw new KeyNotFoundException("Pasajero no encontrado");

        if (passenger.Reserva != null && (passenger.Reserva.Status == EstadoReserva.Operational || passenger.Reserva.Status == EstadoReserva.Closed))
            throw new InvalidOperationException("No se puede eliminar un pasajero de una reserva en estado Operativo o Cerrado.");

        var assignedToVoucher = await _context.VoucherPassengerAssignments
            .AnyAsync(a => a.PassengerId == passengerId);
        if (assignedToVoucher)
            throw new InvalidOperationException("No se puede eliminar el pasajero: esta asignado a uno o mas vouchers. Anula los vouchers primero.");

        var reservaHasIssuedVoucher = await _context.Vouchers
            .AnyAsync(v => v.ReservaId == passenger.ReservaId && v.Status == "Issued");
        if (reservaHasIssuedVoucher)
            throw new InvalidOperationException("No se puede eliminar el pasajero: la reserva ya tiene vouchers emitidos.");

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
        if (string.IsNullOrWhiteSpace(payment.Method)) throw new ArgumentException("Debe seleccionar un mÃ©todo de pago");
        
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
        if (!validStatuses.Contains(status)) throw new ArgumentException("Estado no vÃ¡lido");

        if (file.Status == EstadoReserva.Budget && status == EstadoReserva.Reserved)
        {
            var hasServices = await HasServicesAsync(id);
            if (!hasServices)
                throw new InvalidOperationException("No se puede confirmar la reserva porque no tiene ningun servicio cargado. Agrega al menos un servicio antes de reservar.");

            var expectedPax = file.AdultCount + file.ChildCount + file.InfantCount;
            if (expectedPax > 0)
            {
                var currentPax = await _context.Passengers.CountAsync(p => p.ReservaId == id);
                if (currentPax < expectedPax)
                {
                    throw new InvalidOperationException(
                        $"Faltan {expectedPax - currentPax} pasajero(s) nominales para confirmar la reserva " +
                        $"(cargados: {currentPax} / esperados: {expectedPax}). Cargá los nombres y documentos antes de continuar.");
                }
            }
        }

        if (file.Status == EstadoReserva.Reserved && status == EstadoReserva.Budget)
        {
             var hasPayments = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted);
             if (hasPayments) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay pagos registrados. ElimÃ­nalos primero.");

             var hasInvoices = await _context.Invoices.AnyAsync(i => i.ReservaId == id);
             if (hasInvoices) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay facturas emitidas. Debes anularlas primero (Nota de CrÃ©dito).");

             var hasServices = await HasServicesAsync(id);
             if (hasServices) throw new InvalidOperationException("No se puede volver a Presupuesto porque tiene servicios cargados. ElimÃ­nalos primero.");
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

            // Inconsistencia de capacidad pasajeros vs servicios — bloqueo independiente del estado financiero.
            var capacityReason = await ReservaCapacityRules.GetBlockReasonAsync(_context, id, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(capacityReason))
                throw new InvalidOperationException($"No se puede pasar a Operativo: {capacityReason}");

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
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
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
                    throw new InvalidOperationException("Solo se pueden eliminar reservas en estado Presupuesto o Reservado. Para reservas en otro estado, archivala (Cancelado).");
                }

                if (file.Payments.Any(payment => !payment.IsDeleted))
                {
                    throw new InvalidOperationException("No se puede eliminar una Reserva con pagos registrados. Elimine los pagos primero.");
                }

                var hasIssuedVoucher = await _context.Vouchers.AnyAsync(v => v.ReservaId == id && v.Status == "Issued");
                if (hasIssuedVoucher)
                {
                    throw new InvalidOperationException("No se puede eliminar una Reserva con vouchers emitidos. Anula los vouchers primero o cambia el estado a Cancelado.");
                }

                var hasInvoiceWithCae = await _context.Invoices.AnyAsync(i => i.ReservaId == id && !string.IsNullOrEmpty(i.CAE));
                if (hasInvoiceWithCae)
                {
                    throw new InvalidOperationException("No se puede eliminar una Reserva con facturas AFIP emitidas (CAE asignado). Marca la Reserva como Cancelada.");
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
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        });
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
            (file.FlightSegments?.Where(f => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapFlightStatus(f.Status))).Sum(f => f.SalePrice) ?? 0) +
            (file.HotelBookings?.Where(h => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(h.Status))).Sum(h => h.SalePrice) ?? 0) +
            (file.TransferBookings?.Where(t => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(t.Status))).Sum(t => t.SalePrice) ?? 0) +
            (file.PackageBookings?.Where(p => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(p.Status))).Sum(p => p.SalePrice) ?? 0) +
            (file.Servicios?.Where(r => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(r.Status))).Sum(r => r.SalePrice) ?? 0);

        var totalCost = 
            (file.FlightSegments?.Where(f => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapFlightStatus(f.Status))).Sum(f => f.NetCost) ?? 0) +
            (file.HotelBookings?.Where(h => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(h.Status))).Sum(h => h.NetCost) ?? 0) +
            (file.TransferBookings?.Where(t => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(t.Status))).Sum(t => t.NetCost) ?? 0) +
            (file.PackageBookings?.Where(p => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(p.Status))).Sum(p => p.NetCost) ?? 0) +
            (file.Servicios?.Where(r => TravelApi.Domain.Entities.WorkflowStatusHelper.CountsForReservaBalance(TravelApi.Domain.Entities.WorkflowStatusHelper.MapGenericStatus(r.Status))).Sum(r => r.NetCost) ?? 0);

        var totalPaid = file.Payments?.Where(p => p.Status != "Cancelled" && !p.IsDeleted).Sum(p => p.Amount) ?? 0;

        file.TotalSale = totalSale;
        file.TotalCost = totalCost;
        file.TotalPaid = totalPaid;
        file.Balance = totalSale - totalPaid;

        await _context.SaveChangesAsync();
    }

    private static void ApplyEconomicFlags(ReservaDto dto, OperationalFinanceSettings settings)
    {
        var reserva = new Reserva { Balance = dto.Balance, Status = dto.Status };
        dto.IsEconomicallySettled = EconomicRulesHelper.IsEconomicallySettled(reserva);
        dto.CanMoveToOperativo = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetOperativeBlockReason(reserva, settings));
        dto.CanEmitVoucher = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetVoucherBlockReason(reserva, settings));
        var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);
        dto.CanEmitAfipInvoice = afip.CanEmit || afip.RequiresOverride;
        dto.EconomicBlockReason = EconomicRulesHelper.GetCombinedEconomicBlockReason(reserva, settings);
        dto.IsInProgress = ComputeIsInProgress(dto.Status, dto.StartDate, dto.EndDate);
    }

    private static void ApplyEconomicFlags(ReservaListDto dto, OperationalFinanceSettings settings)
    {
        var reserva = new Reserva { Balance = dto.Balance, Status = dto.Status };
        dto.IsEconomicallySettled = EconomicRulesHelper.IsEconomicallySettled(reserva);
        dto.CanMoveToOperativo = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetOperativeBlockReason(reserva, settings));
        dto.CanEmitVoucher = string.IsNullOrWhiteSpace(EconomicRulesHelper.GetVoucherBlockReason(reserva, settings));
        var afip = EconomicRulesHelper.EvaluateAfip(reserva, settings);
        dto.CanEmitAfipInvoice = afip.CanEmit || afip.RequiresOverride;
        dto.EconomicBlockReason = EconomicRulesHelper.GetCombinedEconomicBlockReason(reserva, settings);
        dto.IsInProgress = ComputeIsInProgress(dto.Status, dto.StartDate, dto.EndDate);
    }

    private static bool ComputeIsInProgress(string status, DateTime? startDate, DateTime? endDate)
    {
        if (status != EstadoReserva.Operational) return false;
        if (!startDate.HasValue) return false;
        var today = DateTime.UtcNow.Date;
        if (startDate.Value.Date > today) return false;
        if (endDate.HasValue && endDate.Value.Date < today) return false;
        return true;
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
            "budget" => query.Where(r => r.Status == EstadoReserva.Budget),
            "reserved" => query.Where(r => r.Status == EstadoReserva.Reserved),
            "operative" => query.Where(r => r.Status == EstadoReserva.Operational),
            "closed" => query.Where(r =>
                r.Status == EstadoReserva.Closed ||
                r.Status == EstadoReserva.Cancelled),
            "archived" => query.Where(r => r.Status == "Archived"),
            // "active" = Reservado + Operativo (Presupuesto tiene su propio tab)
            _ => query.Where(r =>
                r.Status == EstadoReserva.Reserved ||
                r.Status == EstadoReserva.Operational)
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
            "startdate" => desc
                ? query.OrderBy(r => r.StartDate == null).ThenByDescending(r => r.StartDate).ThenByDescending(r => r.CreatedAt)
                : query.OrderBy(r => r.StartDate == null).ThenBy(r => r.StartDate).ThenByDescending(r => r.CreatedAt),
            _ => query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
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


