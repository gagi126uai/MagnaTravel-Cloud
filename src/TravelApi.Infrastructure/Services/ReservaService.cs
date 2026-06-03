using AutoMapper;
using AutoMapper.QueryableExtensions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Linq;
using System.Security.Claims;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;
using TravelApi.Infrastructure.Time;

namespace TravelApi.Infrastructure.Services;

public class ReservaService : IReservaService
{
    private readonly AppDbContext _context;
    private readonly IMapper _mapper;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ReservaService> _logger;
    private readonly IUserPermissionResolver? _permissionResolver;
    private readonly IHttpContextAccessor? _httpContextAccessor;

    /// <summary>
    /// cbteTipo de las Notas de Credito de AFIP (3=A, 8=B, 13=C, 53=M). Se usa para
    /// EXCLUIR las NC del guard fiscal de cancelacion: una NC no es una "factura viva".
    ///
    /// Espejo del mismo conjunto en <c>MutationGuards.LiveInvoiceCreditNoteTypes</c> y
    /// en <c>InvoiceComprobanteHelpers.IsCreditNote</c>. Se replica inline porque EF Core
    /// no traduce el helper a SQL. Mantener los tres sincronizados si cambia la lista.
    /// </summary>
    private static readonly int[] CreditNoteComprobanteTypes = { 3, 8, 13, 53 };

    public ReservaService(
        AppDbContext context,
        IMapper mapper,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        UserManager<ApplicationUser> userManager,
        ILogger<ReservaService> logger,
        IUserPermissionResolver? permissionResolver = null,
        IHttpContextAccessor? httpContextAccessor = null)
    {
        _context = context;
        _mapper = mapper;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _userManager = userManager;
        _logger = logger;
        // B1.15 Fase 2a: estos dos son opcionales para no romper tests unitarios
        // que instancian ReservaService directamente con el ctor de 5 args.
        _permissionResolver = permissionResolver;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>
    /// B1.15 Fase 2a: id del usuario actual desde el HttpContext, o null si no
    /// hay HttpContext (tests unitarios). Centralizado para los chequeos de
    /// view_all/cobranzas.see_cost/cancel_with_payment/etc.
    /// </summary>
    private string? GetCurrentUserIdOrNull()
        => _httpContextAccessor?.HttpContext?.User?.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier);

    /// <summary>
    /// B1.15 Fase 2a: chequea un permiso para el user actual. Devuelve false si
    /// no hay user resoluble o no hay resolver inyectado (modo test).
    /// </summary>
    private async Task<bool> CurrentUserHasPermissionAsync(string permission, CancellationToken ct)
    {
        if (_permissionResolver is null) return false;
        var userId = GetCurrentUserIdOrNull();
        if (string.IsNullOrEmpty(userId)) return false;
        var perms = await _permissionResolver.GetPermissionsAsync(userId, ct);
        return perms.Contains(permission);
    }

    /// <summary>
    /// B1.15 Fase 2a: chequea un permiso para un user explicito. Util cuando el
    /// controller pasa el actor por parametro (ej: UpdateStatusAsync con
    /// validacion de cancel/cancel_with_payment).
    /// </summary>
    private async Task<bool> UserHasPermissionAsync(string? userId, string permission, CancellationToken ct)
    {
        if (_permissionResolver is null || string.IsNullOrEmpty(userId)) return false;
        var perms = await _permissionResolver.GetPermissionsAsync(userId, ct);
        return perms.Contains(permission);
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

    public async Task<ReservaDto> UpdateDatesAsync(string reservaPublicIdOrLegacyId, UpdateReservaDatesRequest request, CancellationToken ct = default)
    {
        var reservaId = await ResolveRequiredIdAsync<Reserva>(reservaPublicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == reservaId, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // B1.15 Fase 0' (CODE-03): cambiar fechas con factura AFIP viva o voucher
        // emitido rompe la coherencia con el periodo declarado en el comprobante.
        var blockReason = await MutationGuards.GetReservaDatesMutationBlockReasonAsync(_context, reservaId, ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateDatesAsync rejected. ReservaId={ReservaId}. Reason={Reason}",
                reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        // Permite editar StartDate/EndDate explicitamente. Pasar `clearXxxDate=true`
        // borra el valor; pasar la fecha en el campo lo setea; null sin clear no toca.
        // Las fechas se normalizan a Kind=Utc porque las columnas Postgres son
        // 'timestamp with time zone' y Npgsql exige Kind=Utc al persistir.
        if (request.ClearStartDate)
            reserva.StartDate = null;
        else if (request.StartDate.HasValue)
            reserva.StartDate = NormalizeUtcOrNull(request.StartDate);

        if (request.ClearEndDate)
            reserva.EndDate = null;
        else if (request.EndDate.HasValue)
            reserva.EndDate = NormalizeUtcOrNull(request.EndDate);

        if (reserva.StartDate.HasValue && reserva.EndDate.HasValue
            && reserva.EndDate.Value.Date < reserva.StartDate.Value.Date)
        {
            throw new ArgumentException("La fecha de regreso no puede ser anterior a la fecha de salida.");
        }

        await _context.SaveChangesAsync(ct);
        return await GetReservaByIdAsync(reservaId);
    }

    /// <summary>
    /// Normaliza un DateTime opcional a Kind=Utc para persistirlo en columnas
    /// 'timestamp with time zone' de Postgres. Tambien actua como guard contra
    /// inputs vacios serializados como DateTime.MinValue ("0001-01-01").
    /// </summary>
    private static DateTime? NormalizeUtcOrNull(DateTime? value)
    {
        if (!value.HasValue) return null;
        if (value.Value == DateTime.MinValue) return null;
        return value.Value.Kind switch
        {
            DateTimeKind.Utc => value.Value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            // Unspecified (caso tipico del binder JSON con "yyyy-mm-dd"): asumimos
            // que el operador eligio una fecha calendario en su zona, no un instante.
            // Tomamos solo la parte Date y la marcamos Utc para evitar offsets.
            _ => DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc)
        };
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
                AssignmentServiceType.Assistance => await _context.AssistanceBookings.AsNoTracking()
                    .Where(a => ids.Contains(a.Id))
                    .Select(a => new { a.Id, a.PublicId })
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
            AssignmentServiceType.Assistance => await ResolveRequiredIdAsync<AssistanceBooking>(request.ServicePublicIdOrLegacyId, ct),
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
            AssignmentServiceType.Assistance => await _context.AssistanceBookings.AnyAsync(a => a.Id == serviceId && a.ReservaId == reservaId, ct),
            AssignmentServiceType.Generic => await _context.Servicios.AnyAsync(s => s.Id == serviceId && s.ReservaId == reservaId, ct),
            _ => false
        };
        if (!serviceBelongsToReserva)
            throw new InvalidOperationException("El servicio no pertenece a esta reserva.");

        // Idempotencia: si ya existe la asignacion, devolver la existente.
        // El check de capacidad va DESPUES — sino una re-asignacion idempotente
        // bloquearia indebidamente cuando el servicio ya esta lleno con ESTE mismo pax.
        var existing = await _context.PassengerServiceAssignments
            .Include(a => a.Passenger)
            .FirstOrDefaultAsync(a => a.PassengerId == passengerId && a.ServiceType == request.ServiceType && a.ServiceId == serviceId, ct);
        if (existing != null)
        {
            var existingPublicId = await ResolveServicePublicIdAsync(request.ServiceType, serviceId, ct);
            return MapAssignment(existing, existingPublicId);
        }

        // Phase 2.3: bloquear si el servicio ya esta lleno (capacidad por servicio).
        // Solo aplica a Hotel/Transfer/Package — Flight/Generic no declaran capacidad.
        var serviceLabel = await BuildServiceLabelAsync(request.ServiceType, serviceId, ct);
        var fullBlockReason = await ReservaCapacityRules.GetServiceFullBlockReasonAsync(
            _context, request.ServiceType, serviceId, serviceLabel, ct);
        if (fullBlockReason != null) throw new InvalidOperationException(fullBlockReason);

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
            AssignmentServiceType.Assistance => await _context.AssistanceBookings.AsNoTracking()
                .Where(a => a.Id == serviceId).Select(a => (Guid?)a.PublicId).FirstOrDefaultAsync(ct),
            AssignmentServiceType.Generic => await _context.Servicios.AsNoTracking()
                .Where(s => s.Id == serviceId).Select(s => (Guid?)s.PublicId).FirstOrDefaultAsync(ct),
            _ => null
        };
    }

    /// <summary>Construye un label legible del servicio para mensajes de error.</summary>
    private async Task<string> BuildServiceLabelAsync(string serviceType, int serviceId, CancellationToken ct)
    {
        return serviceType switch
        {
            AssignmentServiceType.Hotel => await _context.HotelBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Hotel {b.HotelName ?? "sin nombre"}")
                .FirstOrDefaultAsync(ct) ?? "Hotel",
            AssignmentServiceType.Transfer => await _context.TransferBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Transfer {b.VehicleType ?? ""}".Trim())
                .FirstOrDefaultAsync(ct) ?? "Transfer",
            AssignmentServiceType.Package => await _context.PackageBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Paquete {b.PackageName ?? "sin nombre"}")
                .FirstOrDefaultAsync(ct) ?? "Paquete",
            AssignmentServiceType.Assistance => await _context.AssistanceBookings.AsNoTracking()
                .Where(b => b.Id == serviceId)
                .Select(b => $"Asistencia {b.PlanType ?? "seguro"}")
                .FirstOrDefaultAsync(ct) ?? "Asistencia",
            _ => serviceType
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

    public async Task<ReservaDto> UpdateStatusAsync(string publicIdOrLegacyId, string status, string? actorUserId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);

        // B1.15 Fase 2a (Decision 6): cancelacion exige reservas.cancel y, si la
        // reserva tiene cobros o facturas, ademas reservas.cancel_with_payment.
        // Admin bypass: si el user actual es Admin, ya pasa por el handler de
        // permisos arriba; este chequeo es para el caso del Vendedor.
        //
        // B1.15 Fase 2a (FIX 7 — fiscal critico): bloqueo simetrico al de
        // RevertStatusAsync. Una reserva con factura AFIP CAE vivo (no anulada
        // via NC aprobada) NO se puede cancelar. La cancelacion sin NC dejaria
        // un comprobante fiscal valido para una reserva inexistente. El usuario
        // debe ejecutar primero <c>POST /api/invoices/{id}/annul</c> y esperar
        // a que <c>AnnulmentStatus = Succeeded</c>. El controller traduce
        // InvalidOperationException a 400/409 segun camino actual.
        //
        // FIX 2026-05-30 (mismo criterio que MutationGuards): EXCLUIMOS las Notas de
        // Credito del conteo. Una NC tambien es una fila Invoice con su propio CAE y
        // AnnulmentStatus=None, pero NACE para anular/corregir una factura — nunca se
        // anula a si misma. Si la contaramos, tras emitir una NC TOTAL la reserva
        // quedaria bloqueada para siempre aunque la factura original ya este Succeeded.
        // Solo bloquea que quede una FACTURA viva; en NC parcial la factura original
        // sigue viva por el resto, asi que igual bloquea (decision del dueño).
        if (status == EstadoReserva.Cancelled)
        {
            var hasLiveCae = await _context.Invoices.AnyAsync(
                i => i.ReservaId == id
                    && !CreditNoteComprobanteTypes.Contains(i.TipoComprobante) // excluye NC
                    && !string.IsNullOrEmpty(i.CAE)
                    && i.AnnulmentStatus != AnnulmentStatus.Succeeded,
                ct);
            if (hasLiveCae)
            {
                throw new InvalidOperationException(
                    "La reserva tiene facturas con CAE vigentes. Debe anularlas (se emitira Nota de Credito) antes de cancelar la reserva.");
            }

            // Solo aplicamos validacion B1.15 si tenemos un actor concreto.
            // En tests unitarios sin HttpContext el actorUserId puede llegar null
            // (camino legacy); preservamos el comportamiento previo en ese caso.
            if (!string.IsNullOrEmpty(actorUserId))
            {
                var httpContextUser = _httpContextAccessor?.HttpContext?.User;
                var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
                if (!isAdmin)
                {
                    var hasCancel = await UserHasPermissionAsync(actorUserId, Permissions.ReservasCancel, ct);
                    if (!hasCancel)
                    {
                        throw new UnauthorizedAccessException("No tenes permiso para cancelar reservas.");
                    }

                    var hasPaymentsOrInvoices = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted, ct)
                        || await _context.Invoices.AnyAsync(i => i.ReservaId == id, ct);
                    if (hasPaymentsOrInvoices)
                    {
                        var hasCancelWithPayment = await UserHasPermissionAsync(actorUserId, Permissions.ReservasCancelWithPayment, ct);
                        if (!hasCancelWithPayment)
                        {
                            throw new UnauthorizedAccessException(
                                "Cancelar una reserva con cobros o facturas asociadas requiere autorizacion adicional.");
                        }
                    }
                }
            }
        }

        await UpdateStatusAsync(id, status, actorUserId);
        return await GetReservaByIdAsync(id);
    }

    public async Task<TransitionReadinessDto> GetTransitionReadinessAsync(string publicIdOrLegacyId, string targetStatus, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas
            .Include(r => r.Passengers)
            .Include(r => r.Servicios)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.FlightSegments)
            .Include(r => r.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // Composicion derivada de los servicios cargados.
        var (adults, children, infants, ambiguous) = ComputePaxCompositionFromServices(reserva);

        var dto = new TransitionReadinessDto
        {
            TargetStatus = targetStatus,
            Allowed = true,
            ExpectedAdults = adults,
            ExpectedChildren = children,
            ExpectedInfants = infants,
            AmbiguousComposition = ambiguous,
            ExpectedPassengerCount = adults + children + infants,
            CurrentPassengerCount = reserva.Passengers?.Count ?? 0
        };

        // Reglas de transicion Budget -> Reserved
        if (targetStatus == EstadoReserva.Confirmed && reserva.Status == EstadoReserva.Budget)
        {
            // Bug fix: chequeamos las 5 tablas de servicios (no solo Servicios genericos).
            // El typical caso del agente es cargar un Hotel — antes daba "no hay servicios".
            var hasAnyService = (reserva.Servicios?.Any() ?? false)
                || (reserva.HotelBookings?.Any() ?? false)
                || (reserva.TransferBookings?.Any() ?? false)
                || (reserva.PackageBookings?.Any() ?? false)
                || (reserva.FlightSegments?.Any() ?? false)
                || (reserva.AssistanceBookings?.Any() ?? false);
            if (!hasAnyService)
            {
                dto.Allowed = false;
                dto.BlockingReasons.Add("Cargá al menos un servicio (hotel, vuelo, transfer, paquete o asistencia) antes de confirmar la reserva.");
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

    /// <summary>
    /// Deriva la composicion de pasajeros (adultos/menores/infantes) a partir de los
    /// servicios cargados. El servicio con mayor total (Adults+Children) es el "anchor"
    /// y su composicion se considera la default. Si OTRO servicio tiene mismo total
    /// pero distinta composicion, AmbiguousComposition=true (warning para el agente,
    /// no bloqueo).
    ///
    /// HotelBooking, PackageBooking y AssistanceBooking declaran composicion explicita
    /// (Adults + Children) — los 3 sirven como "anchor". TransferBooking solo tiene
    /// Passengers (total). FlightSegment no declara nada. Por eso esos dos no se usan como
    /// "anchor" — solo extienden el total minimo via fallback. Infants nunca viene de
    /// servicios; queda en 0 a menos que el agente lo ajuste manualmente en el modal.
    /// </summary>
    private static (int adults, int children, int infants, bool ambiguous) ComputePaxCompositionFromServices(Reserva reserva)
    {
        var candidates = new List<(int adults, int children, int total)>();

        foreach (var h in reserva.HotelBookings ?? Enumerable.Empty<HotelBooking>())
        {
            candidates.Add((h.Adults, h.Children, h.Adults + h.Children));
        }
        foreach (var p in reserva.PackageBookings ?? Enumerable.Empty<PackageBooking>())
        {
            candidates.Add((p.Adults, p.Children, p.Adults + p.Children));
        }
        // Asistencia declara Adults+Children (los pasajeros cubiertos por la poliza), igual
        // que Hotel/Package, asi que tambien es un candidato a "anchor" de composicion.
        foreach (var a in reserva.AssistanceBookings ?? Enumerable.Empty<AssistanceBooking>())
        {
            candidates.Add((a.Adults, a.Children, a.Adults + a.Children));
        }

        if (candidates.Count == 0)
        {
            // Sin servicios con composicion explicita. Si hay transfer, usar su Passengers
            // como cantidad de adultos (no se sabe distribucion).
            var transferMax = reserva.TransferBookings?.Max(t => (int?)t.Passengers) ?? 0;
            return (transferMax, 0, 0, false);
        }

        // Anchor: candidato con mayor total. En empate, el primero (orden Hotel -> Package).
        var anchor = candidates.OrderByDescending(c => c.total).First();

        // Ambiguedad: hay otro candidato con mismo total pero distinta composicion?
        var ambiguous = candidates.Any(c =>
            (c.adults != anchor.adults || c.children != anchor.children)
            && c.total == anchor.total);

        return (anchor.adults, anchor.children, 0, ambiguous);
    }

    public async Task<ReservaDto> ArchiveReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        await ArchiveReservaAsync(id);
        return await GetReservaByIdAsync(id);
    }

    // ============= Phase 2.4 — Reversion de Status con autorizacion =============

    // ============================================================
    // Matrices de transicion del ciclo de vida de la Reserva.
    //
    // Hay DOS juegos de matrices, elegidas en runtime por el flag
    // EnableSoldToSettleStates (rediseño Fase A+B, 2026-05-30):
    //  - CLASICO (flag OFF, default historico): Budget -> Confirmed -> Traveling -> Closed.
    //  - NUEVO (flag ON): Budget -> Sold -> Confirmed -> Traveling -> ToSettle -> Closed.
    //
    // Las cuatro matrices (forward/revert x clasico/nuevo) viven como diccionarios
    // estaticos para que la matriz sea facil de leer y de testear sin tocar la logica.
    // ============================================================

    /// <summary>
    /// Forward CLASICO (flag OFF): las transiciones hacia adelante validas de siempre.
    /// Cancelled, PendingOperatorRefund y Archived NO se modelan aca porque se manejan
    /// por flujos dedicados (cancelacion, refund, archivado), no por UpdateStatusAsync.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedForwardTransitionsClassic = new(StringComparer.OrdinalIgnoreCase)
    {
        [EstadoReserva.Budget] = new[] { EstadoReserva.Confirmed },
        [EstadoReserva.Confirmed] = new[] { EstadoReserva.Traveling },
        [EstadoReserva.Traveling] = new[] { EstadoReserva.Closed },
    };

    /// <summary>
    /// Forward NUEVO (flag ON): inserta Sold despues de Budget. ToSettle ("A liquidar") ya NO
    /// es un paso obligatorio sino un DESVIO MANUAL OPCIONAL: con cada operador la plata se
    /// arregla distinto (algunos antes del viaje, otros despues), asi que liquidar post-viaje
    /// no es universal.
    ///
    /// Por eso Traveling tiene DOS destinos forward:
    ///  - Traveling -&gt; Closed: el cierre por DEFAULT (igual que el ciclo clasico, gate Balance == 0).
    ///  - Traveling -&gt; ToSettle: el desvio OPCIONAL (apartar para liquidar con el operador, sin gate).
    /// Y ToSettle -&gt; Closed sigue siendo el cierre manual desde la bandeja "A liquidar".
    ///
    /// Sigue PROHIBIDO el salto directo Budget-&gt;Confirmed (INV-SM-01): readiness vive en Budget-&gt;Sold.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedForwardTransitionsSoldToSettle = new(StringComparer.OrdinalIgnoreCase)
    {
        [EstadoReserva.Budget] = new[] { EstadoReserva.Sold },
        [EstadoReserva.Sold] = new[] { EstadoReserva.Confirmed },
        [EstadoReserva.Confirmed] = new[] { EstadoReserva.Traveling },
        // Traveling: Closed = default (Finalizar), ToSettle = desvio opcional (Marcar a liquidar).
        [EstadoReserva.Traveling] = new[] { EstadoReserva.Closed, EstadoReserva.ToSettle },
        [EstadoReserva.ToSettle] = new[] { EstadoReserva.Closed },
    };

    /// <summary>Revert CLASICO (flag OFF): transiciones hacia atras de siempre.</summary>
    private static readonly Dictionary<string, string[]> AllowedRevertTransitionsClassic = new(StringComparer.OrdinalIgnoreCase)
    {
        [EstadoReserva.Traveling] = new[] { EstadoReserva.Confirmed },
        [EstadoReserva.Confirmed] = new[] { EstadoReserva.Budget },
        [EstadoReserva.Closed] = new[] { EstadoReserva.Traveling },
    };

    /// <summary>
    /// Revert NUEVO (flag ON): reverts de a UN solo paso a lo largo de la cadena nueva.
    /// El gate "volver a Budget sin pagos/facturas/servicios" se mueve a Sold-&gt;Budget
    /// (antes vivia en Confirmed-&gt;Budget).
    ///
    /// Closed -&gt; Traveling (NO -&gt; ToSettle): como ToSettle es opcional, una reserva pudo cerrar
    /// directo Traveling-&gt;Closed sin pasar nunca por ToSettle. Revertir Closed-&gt;ToSettle la
    /// mandaria a un estado por el que nunca estuvo. Por eso el revert de Closed vuelve siempre
    /// a Traveling (el estado anterior real garantizado). ToSettle-&gt;Traveling se mantiene para
    /// las reservas que SI eligieron el desvio manual de liquidacion.
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedRevertTransitionsSoldToSettle = new(StringComparer.OrdinalIgnoreCase)
    {
        [EstadoReserva.Sold] = new[] { EstadoReserva.Budget },
        [EstadoReserva.Confirmed] = new[] { EstadoReserva.Sold },
        [EstadoReserva.Traveling] = new[] { EstadoReserva.Confirmed },
        [EstadoReserva.ToSettle] = new[] { EstadoReserva.Traveling },
        [EstadoReserva.Closed] = new[] { EstadoReserva.Traveling },
    };

    /// <summary>
    /// Elige la matriz de reverts segun el flag EnableSoldToSettleStates.
    /// Centralizado aca para que GetRevertOptionsAsync y RevertStatusAsync usen la misma.
    /// </summary>
    private static Dictionary<string, string[]> GetRevertTransitions(bool soldToSettleEnabled)
        => soldToSettleEnabled ? AllowedRevertTransitionsSoldToSettle : AllowedRevertTransitionsClassic;

    public async Task<RevertOptionsDto> GetRevertOptionsAsync(string publicIdOrLegacyId, string actorUserId, bool actorIsAdmin, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.AsNoTracking()
            .Where(r => r.Id == id)
            .Select(r => new { r.Status })
            .FirstOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        var dto = new RevertOptionsDto
        {
            CurrentStatus = reserva.Status,
            ActorIsAdmin = actorIsAdmin,
            RequiresAuthorization = !actorIsAdmin
        };

        // Rediseño Fase A+B: el juego de reverts depende del flag EnableSoldToSettleStates.
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
        var revertTransitions = GetRevertTransitions(settings.EnableSoldToSettleStates);

        // Targets posibles segun current
        if (revertTransitions.TryGetValue(reserva.Status, out var targets))
        {
            dto.AllowedTargets.AddRange(targets);
        }

        // Hard blockers (no se saltean ni siendo admin):
        // - Reserva con factura AFIP con CAE asignado: revertir rompe historia fiscal.
        var hasInvoiceWithCae = await _context.Invoices.AsNoTracking()
            .AnyAsync(i => i.ReservaId == id && !string.IsNullOrEmpty(i.CAE), ct);
        if (hasInvoiceWithCae)
        {
            dto.HardBlockers.Add("La reserva tiene facturas AFIP emitidas con CAE. No se puede revertir el estado (rompe la historia fiscal). Si necesitas anular, emiti una Nota de Credito primero.");
            dto.AllowedTargets.Clear();
        }

        // Si requiere autorizacion, listar supervisores con permiso
        if (!actorIsAdmin && dto.AllowedTargets.Count > 0)
        {
            var superiors = await _context.Users.AsNoTracking()
                .Where(u => u.IsActive)
                .ToListAsync(ct);
            var allUserRoles = await _context.UserRoles.AsNoTracking()
                .Join(_context.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, RoleName = r.Name! })
                .ToListAsync(ct);
            var allRolePerms = await _context.RolePermissions.AsNoTracking()
                .Where(rp => rp.Permission == Permissions.VouchersAuthorizeException)
                .Select(rp => rp.RoleName)
                .ToListAsync(ct);
            var rolesWithAuth = new HashSet<string>(allRolePerms, StringComparer.OrdinalIgnoreCase);
            rolesWithAuth.Add("Admin"); // admin siempre puede

            foreach (var u in superiors)
            {
                if (u.Id == actorUserId) continue; // no se autoriza a si mismo
                var roles = allUserRoles.Where(r => r.UserId == u.Id).Select(r => r.RoleName);
                if (roles.Any(r => rolesWithAuth.Contains(r)))
                {
                    dto.Supervisors.Add(new SupervisorOptionDto
                    {
                        UserId = u.Id,
                        FullName = u.FullName ?? u.UserName ?? u.Email ?? u.Id
                    });
                }
            }
        }

        return dto;
    }

    public async Task<ReservaDto> RevertStatusAsync(
        string publicIdOrLegacyId,
        RevertStatusRequest request,
        string actorUserId,
        string? actorUserName,
        bool actorIsAdmin,
        CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        var reserva = await _context.Reservas.FirstOrDefaultAsync(r => r.Id == id, ct)
            ?? throw new KeyNotFoundException("Reserva no encontrada");

        // Rediseño Fase A+B: el juego de reverts depende del flag EnableSoldToSettleStates.
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);
        var revertTransitions = GetRevertTransitions(settings.EnableSoldToSettleStates);

        // Validar transicion permitida
        if (!revertTransitions.TryGetValue(reserva.Status, out var allowedTargets) || !allowedTargets.Contains(request.TargetStatus, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"No se puede revertir desde {reserva.Status} a {request.TargetStatus}. " +
                $"Transiciones permitidas desde {reserva.Status}: {(allowedTargets == null ? "(ninguna)" : string.Join(", ", allowedTargets))}.");
        }

        // Hard blockers
        var hasInvoiceWithCae = await _context.Invoices.AnyAsync(i => i.ReservaId == id && !string.IsNullOrEmpty(i.CAE), ct);
        if (hasInvoiceWithCae)
            throw new InvalidOperationException("La reserva tiene facturas AFIP emitidas con CAE. No se puede revertir (rompe la historia fiscal).");

        // Validaciones de coherencia segun el target
        if (request.TargetStatus == EstadoReserva.Budget)
        {
            // No retroceder a Presupuesto si hay pagos o facturas (mismo criterio que UpdateStatusAsync).
            var hasPayments = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted, ct);
            if (hasPayments) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay pagos registrados. Eliminalos primero.");
            var hasInvoices = await _context.Invoices.AnyAsync(i => i.ReservaId == id, ct);
            if (hasInvoices) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay facturas emitidas. Anulalas primero.");
        }

        // Autorizacion
        string? authSuperiorId = null;
        string? authSuperiorName = null;
        var reason = (request.Reason ?? "").Trim();

        if (!actorIsAdmin)
        {
            if (string.IsNullOrWhiteSpace(request.AuthorizedBySuperiorUserId))
                throw new InvalidOperationException("Necesitas autorizacion de un supervisor para revertir el estado de la reserva. Selecciona un supervisor en el formulario.");
            if (reason.Length < 10)
                throw new InvalidOperationException("Indica un motivo de la reversion (al menos 10 caracteres).");

            var superior = await _context.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == request.AuthorizedBySuperiorUserId && u.IsActive, ct)
                ?? throw new InvalidOperationException("El supervisor seleccionado no existe o esta inactivo.");

            var superiorRoles = await _context.UserRoles.AsNoTracking()
                .Where(ur => ur.UserId == superior.Id)
                .Join(_context.Roles.AsNoTracking(), ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
                .ToListAsync(ct);
            var isSuperiorAdmin = superiorRoles.Any(r => string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase));
            var canAuthorize = isSuperiorAdmin || await _context.RolePermissions.AsNoTracking()
                .AnyAsync(p => superiorRoles.Contains(p.RoleName) && p.Permission == Permissions.VouchersAuthorizeException, ct);
            if (!canAuthorize)
                throw new InvalidOperationException("El supervisor seleccionado no tiene permiso para autorizar reversiones.");

            authSuperiorId = superior.Id;
            authSuperiorName = superior.FullName ?? superior.UserName ?? superior.Id;
        }
        else
        {
            // Admin: la reason es opcional pero se loguea si vino.
            if (string.IsNullOrWhiteSpace(reason)) reason = "(reversion por admin sin motivo declarado)";
        }

        var fromStatus = reserva.Status;
        reserva.Status = request.TargetStatus;
        // Re-abrir una reserva cerrada borra el ClosedAt. En AMBOS ciclos el revert de Closed va a
        // Traveling (no a ToSettle: ToSettle es un desvio opcional y una reserva pudo cerrar directo
        // desde Traveling sin pasar por el). Se incluye ToSettle en la condicion solo por defensa
        // (el revert ToSettle->Traveling no tiene ClosedAt, pero si alguna vez se permite Closed->ToSettle
        // hay que limpiarlo igual). Sino la reserva figura "cerrada el dia X" pero esta abierta -> dato inconsistente.
        if ((request.TargetStatus == EstadoReserva.Traveling || request.TargetStatus == EstadoReserva.ToSettle)
            && reserva.ClosedAt.HasValue)
            reserva.ClosedAt = null;

        _context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = id,
            FromStatus = fromStatus,
            ToStatus = request.TargetStatus,
            Direction = "Revert",
            ByUserId = actorUserId,
            ByUserName = actorUserName,
            AuthorizedBySuperiorUserId = authSuperiorId,
            AuthorizedBySuperiorUserName = authSuperiorName,
            Reason = reason,
            OccurredAt = DateTime.UtcNow
        });

        await _context.SaveChangesAsync(ct);
        return await GetReservaByIdAsync(id);
    }

    // ============= /Phase 2.4 =============

    public async Task DeleteReservaAsync(string publicIdOrLegacyId, CancellationToken ct = default)
    {
        var id = await ResolveRequiredIdAsync<Reserva>(publicIdOrLegacyId, ct);
        await DeleteReservaAsync(id);
    }

    public async Task<ReservaListPageDto> GetReservasAsync(ReservaListQuery query, CancellationToken cancellationToken)
    {
        var (page, _) = await GetReservasInternalAsync(query, applyOwnerScope: false, cancellationToken);
        return page;
    }

    /// <summary>
    /// B1.15 Fase 2a: variante que decide el scope segun el permiso del user
    /// actual. Si tiene <c>reservas.view_all</c>, devuelve todas. Sino, filtra
    /// por <c>ResponsibleUserId == currentUserId</c>.
    /// </summary>
    public async Task<(ReservaListPageDto Page, string Scope)> GetReservasWithScopeAsync(ReservaListQuery query, CancellationToken cancellationToken)
    {
        // Admin: bypass total — ve todas. El handler de permisos hace lo mismo,
        // pero aca tenemos que decidir el scope para el header X-Permission-Scope.
        var httpContextUser = _httpContextAccessor?.HttpContext?.User;
        var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
        var hasViewAll = isAdmin || await CurrentUserHasPermissionAsync(Permissions.ReservasViewAll, cancellationToken);

        if (hasViewAll)
        {
            var (allPage, _) = await GetReservasInternalAsync(query, applyOwnerScope: false, cancellationToken);
            return (allPage, "all");
        }

        // Sin view_all: filtrar por ResponsibleUserId = currentUserId. Si no
        // hay user resoluble, fail-safe: filtramos por una cadena vacia que no
        // coincide con ningun ResponsibleUserId (=> 0 resultados).
        var (minePage, _) = await GetReservasInternalAsync(query, applyOwnerScope: true, cancellationToken);
        return (minePage, "mine");
    }

    private async Task<(ReservaListPageDto Page, string? OwnerFilterUserId)> GetReservasInternalAsync(ReservaListQuery query, bool applyOwnerScope, CancellationToken cancellationToken)
    {
        // B1.15 Fase 2a: si el caller pidio aplicar scope "mine" y no podemos
        // resolver el user, devolvemos lista vacia (fail-safe). NO ejecutar
        // queries con userId vacio que mantengan la base sin filtrar.
        string? ownerFilterUserId = null;
        if (applyOwnerScope)
        {
            ownerFilterUserId = GetCurrentUserIdOrNull();
            if (string.IsNullOrEmpty(ownerFilterUserId))
            {
                // Sentinel imposible: ningun ResponsibleUserId real coincide con string.Empty.
                ownerFilterUserId = "__no_user__";
            }
        }

        var settings = await _operationalFinanceSettingsService.GetEntityAsync(cancellationToken);
        var summaryBaseQuery = ApplyReservaSearch(_context.Reservas.AsNoTracking(), query.Search);
        if (ownerFilterUserId is not null)
        {
            summaryBaseQuery = summaryBaseQuery.Where(r => r.ResponsibleUserId == ownerFilterUserId);
        }
        
        // B1.15 Fase D' (2026-05-11): filtros de fecha convertidos via AgencyTimezone.
        // El query string entrega DateTime con Kind=Unspecified; las columnas son
        // timestamptz en Postgres y Npgsql tira 500 al comparar Unspecified.
        // Ademas, rango cerrado-abierto [from, to+1day) captura todo el dia local
        // final sin perder eventos posteriores a la medianoche UTC.
        if (query.CreatedFrom.HasValue)
        {
            var fromUtc = AgencyTimezone.ToUtcFromAgencyDay(query.CreatedFrom.Value, isEndOfDay: false);
            summaryBaseQuery = summaryBaseQuery.Where(r => r.CreatedAt >= fromUtc);
        }

        if (query.CreatedTo.HasValue)
        {
            var toUtc = AgencyTimezone.ToUtcFromAgencyDay(query.CreatedTo.Value, isEndOfDay: true);
            // EXCLUSIVE end: rango cerrado-abierto [from, to+1day). Captura todo el dia "to" local.
            summaryBaseQuery = summaryBaseQuery.Where(r => r.CreatedAt < toUtc);
        }

        if (query.TravelFrom.HasValue)
        {
            var fromUtc = AgencyTimezone.ToUtcFromAgencyDay(query.TravelFrom.Value, isEndOfDay: false);
            summaryBaseQuery = summaryBaseQuery.Where(r => r.StartDate.HasValue && r.StartDate.Value >= fromUtc);
        }

        if (query.TravelTo.HasValue)
        {
            var toUtc = AgencyTimezone.ToUtcFromAgencyDay(query.TravelTo.Value, isEndOfDay: true);
            // EXCLUSIVE end para no perder reservas que arrancan al final del dia "to" local.
            summaryBaseQuery = summaryBaseQuery.Where(r => r.StartDate.HasValue && r.StartDate.Value < toUtc);
        }

        // Rediseño Fase A+B: el flag decide el ciclo de vida y, con el, que cuenta como "activa"
        // y que vistas (tabs) hay disponibles.
        var soldToSettleEnabled = settings.EnableSoldToSettleStates;
        var filteredQuery = ApplyReservaView(summaryBaseQuery, query.View, soldToSettleEnabled);

        var summary = new ReservaListSummaryDto
        {
            BudgetCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Budget, cancellationToken),
            // ActiveCount = "en gestion, no cerrada ni cancelada". Con el flag ON suma los dos
            // estados nuevos (Sold = vendida activa, ToSettle = a liquidar, todavia pre-cierre).
            // Con el flag OFF nunca hay filas en esos estados, asi que el resultado es identico a hoy.
            ActiveCount = await summaryBaseQuery.CountAsync(r =>
                r.Status == EstadoReserva.Sold ||
                r.Status == EstadoReserva.Confirmed ||
                r.Status == EstadoReserva.Traveling ||
                r.Status == EstadoReserva.ToSettle,
                cancellationToken),
            ReservedCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Confirmed, cancellationToken),
            OperativeCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Traveling, cancellationToken),
            // SoldCount / ToSettleCount: 0 con el flag OFF (no hay filas). Aditivos para la fase UI.
            SoldCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.Sold, cancellationToken),
            ToSettleCount = await summaryBaseQuery.CountAsync(r => r.Status == EstadoReserva.ToSettle, cancellationToken),
            ClosedCount = await summaryBaseQuery.CountAsync(r =>
                r.Status == EstadoReserva.Closed ||
                r.Status == EstadoReserva.Cancelled ||
                r.Status == "Archived",
                cancellationToken),
            // Totales "activos" via patron NEGATIVO (todo lo que NO esta cerrado/cancelado/archivado).
            // Fase D (rediseño Sold/ToSettle): este patron YA incluye Sold y ToSettle a proposito.
            // Una reserva Sold cuenta como venta igual que la vieja Confirmed, y una ToSettle es
            // pre-cierre (todavia activa). NO se reescribe a un conjunto positivo (evita regresiones).
            // Con el flag OFF no hay filas en esos estados -> resultado identico al historico.
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
                ResponsibleUserName = f.ResponsibleUserName,
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

        // B1.15 Fase 2a (Decision 4): si el user actual NO tiene
        // cobranzas.see_cost, ocultar TotalCost (solo ven precio de venta).
        // Admin bypass: si es Admin, no se enmascara.
        bool seeCost = true;
        var httpContextUser = _httpContextAccessor?.HttpContext?.User;
        var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
        if (!isAdmin)
        {
            seeCost = await CurrentUserHasPermissionAsync(Permissions.CobranzasSeeCost, cancellationToken);
        }

        foreach (var reserva in paged.Items)
        {
            ApplyEconomicFlags(reserva, settings);
            if (!seeCost)
            {
                reserva.TotalCost = 0m;
            }
        }

        // El summary tambien expone costos agregados — enmascarar si no aplica.
        if (!seeCost)
        {
            summary.TotalCostActive = 0m;
            summary.GrossProfit = 0m;
        }

        var page = ReservaListPageDto.Create(paged.Items, paged.Page, paged.PageSize, paged.TotalCount, summary);
        return (page, ownerFilterUserId);
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
            .Include(f => f.Passengers)
            .Include(f => f.Payments)
            .ThenInclude(p => p.Receipt)
            .Include(f => f.Invoices).ThenInclude(i => i.OriginalInvoice)
            .Include(f => f.Servicios)
            .Include(f => f.FlightSegments).ThenInclude(fs => fs.Supplier)
            .Include(f => f.HotelBookings).ThenInclude(hb => hb.Supplier)
            .Include(f => f.TransferBookings).ThenInclude(tb => tb.Supplier)
            .Include(f => f.PackageBookings).ThenInclude(pb => pb.Supplier)
            .Include(f => f.AssistanceBookings).ThenInclude(ab => ab.Supplier)
            .FirstOrDefaultAsync(f => f.Id == id);

        if (file == null) 
        {
            throw new KeyNotFoundException($"File with ID {id} not found locally");
        }

        var dto = _mapper.Map<ReservaDto>(file);
        ApplyEconomicFlags(dto, settings);

        // Sugerencia de fechas computadas desde los servicios cargados — la UI las
        // usa para pre-rellenar inputs cuando StartDate/EndDate estan en null.
        // Costo: 5 queries chicas en una operacion de detalle (no es hot path).
        var (suggestedStart, suggestedEnd) = await ReservaScheduleCalculator.ComputeAsync(_context, file.Id);
        dto.SuggestedStartDate = suggestedStart;
        dto.SuggestedEndDate = suggestedEnd;

        // B1.15 Fase 2a (Decision 4): mascara de costos para roles sin
        // cobranzas.see_cost. Admin bypass.
        await ApplyCostMaskingAsync(dto, CancellationToken.None);

        return dto;
    }

    /// <summary>
    /// B1.15 Fase 2a (Decision 4): si el user actual NO tiene
    /// <c>cobranzas.see_cost</c>, oculta NetCost/TotalCost/Commission de la
    /// reserva y de cada coleccion de servicios. Admin bypass.
    ///
    /// Centralizado aca para garantizar que cualquier endpoint de detalle aplique
    /// la mascara antes de devolver el DTO al frontend.
    /// </summary>
    private async Task ApplyCostMaskingAsync(ReservaDto dto, CancellationToken ct)
    {
        var httpContextUser = _httpContextAccessor?.HttpContext?.User;
        var isAdmin = httpContextUser?.IsInRole("Admin") ?? false;
        if (isAdmin) return;

        var seeCost = await CurrentUserHasPermissionAsync(Permissions.CobranzasSeeCost, ct);
        if (seeCost) return;

        // Reserva-level totals.
        dto.TotalCost = 0m;

        // Servicios genericos.
        if (dto.Servicios is not null)
        {
            foreach (var s in dto.Servicios)
            {
                s.NetCost = 0m;
                s.Commission = 0m;
            }
        }

        if (dto.HotelBookings is not null)
        {
            foreach (var b in dto.HotelBookings) b.NetCost = 0m;
        }
        if (dto.FlightSegments is not null)
        {
            foreach (var f in dto.FlightSegments) f.NetCost = 0m;
        }
        if (dto.PackageBookings is not null)
        {
            foreach (var p in dto.PackageBookings) p.NetCost = 0m;
        }
        if (dto.TransferBookings is not null)
        {
            foreach (var t in dto.TransferBookings) t.NetCost = 0m;
        }
        if (dto.AssistanceBookings is not null)
        {
            foreach (var a in dto.AssistanceBookings) a.NetCost = 0m;
        }
    }

    public async Task<Reserva> CreateReservaAsync(CreateReservaRequest request, string? createdByUserId)
    {
        // C16: ApplicationUser ya no es nav prop de Reserva — denormalizamos el FullName del
        // responsable al crear. Si el lookup no encuentra usuario, dejamos null (no rompemos
        // la creacion por un nombre faltante; el FK se valida igual al persistir).
        string? responsibleUserName = null;
        if (!string.IsNullOrWhiteSpace(createdByUserId))
        {
            var responsibleUser = await _userManager.FindByIdAsync(createdByUserId);
            responsibleUserName = responsibleUser?.FullName;
        }

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
                    ResponsibleUserName = responsibleUserName,
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

        // B1.15 Fase 0' (CODE-05): inmutabilidad post-CAE / post-voucher. Cambiar
        // monto/proveedor/fechas del servicio rompe la coherencia con la factura
        // AFIP emitida o el voucher entregado al cliente.
        var blockReason = await MutationGuards.GetServiceMutationBlockReasonAsync(_context, serviceId, ct);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdateServiceAsync rejected. ServiceId={ServiceId} ReservaId={ReservaId}. Reason={Reason}",
                serviceId, service.ReservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

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
            await EnsureCanRemoveServiceAsync(service.ReservaId ?? 0, ct);
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
            await EnsureCanRemoveServiceAsync(flight.ReservaId, ct);
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
            await EnsureCanRemoveServiceAsync(hotel.ReservaId, ct);
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
            await EnsureCanRemoveServiceAsync(transfer.ReservaId, ct);
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
            await EnsureCanRemoveServiceAsync(package.ReservaId, ct);
            _context.PackageBookings.Remove(package);
            var resId = package.ReservaId;
            await _context.SaveChangesAsync(ct);
            await UpdateBalanceAsync(resId);
            return;
        }

        // 6. Try Assistance (Bloque 3): si no la contemplamos aca, borrar una asistencia por
        // este path generico tiraria "no encontrado" sin tocar el saldo -> descuadre silencioso.
        var assistance = await _context.AssistanceBookings.FindAsync(new object[] { serviceId }, ct);
        if (assistance != null)
        {
            await EnsureCanRemoveServiceAsync(assistance.ReservaId, ct);
            _context.AssistanceBookings.Remove(assistance);
            var resId = assistance.ReservaId;
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
        var assistance = reserva.AssistanceBookings?.Sum(a => a.GetExpectedPaxCount()) ?? 0;
        return Math.Max(hotel, Math.Max(transfer, Math.Max(package, assistance)));
    }

    // La logica de capacidad pasajeros vs servicios vive en ReservaCapacityRules
    // (clase estatica compartida con ReservaLifecycleAutomationService).

    private async Task EnsureCanRemoveServiceAsync(int reservaId, CancellationToken ct)
    {
        // Reglas de borrado de servicios viven en DeleteGuards (compartidas con BookingService).
        // GetServiceDeleteBlockReasonAsync incluye el state guard C26 (solo Budget) ademas
        // de los guards historicos (pagos vivos, vouchers emitidos).
        var blockReason = await DeleteGuards.GetServiceDeleteBlockReasonAsync(_context, reservaId, ct, _logger);
        if (blockReason != null)
        {
            // Information: rechazo por estado/contenido. No hay riesgo fiscal en este nivel.
            _logger.LogInformation(
                "RemoveServiceAsync rejected. ReservaId={ReservaId}. Reason={Reason}",
                reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }
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
            .Include(r => r.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.Id == reservaId);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // Nota: NO se bloquea la carga en estado Presupuesto. El modal de Confirmar
        // Reserva (Phase 1.2) carga los pasajeros nominales JUSTO ANTES de transicionar
        // a Reservado. La transicion misma valida via UpdateStatusAsync que la cantidad
        // de pasajeros == cantidad esperada por los servicios — eso garantiza coherencia.

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

        // B1.15 Fase 0' (CODE-14): solo bloqueamos si el request cambia DATOS
        // PERSONALES (nombre, documento, fecha de nacimiento, nacionalidad,
        // genero). Email/Phone/Notes son campos de contacto y se permiten editar
        // libremente — son parte de la operativa de la reserva, no del voucher.
        var personalDataChanged =
            !string.Equals(passenger.FullName, updated.FullName, StringComparison.Ordinal) ||
            !string.Equals(passenger.DocumentType, updated.DocumentType, StringComparison.Ordinal) ||
            !string.Equals(passenger.DocumentNumber, updated.DocumentNumber, StringComparison.Ordinal) ||
            passenger.BirthDate != updated.BirthDate ||
            !string.Equals(passenger.Nationality, updated.Nationality, StringComparison.Ordinal) ||
            !string.Equals(passenger.Gender, updated.Gender, StringComparison.Ordinal);

        if (personalDataChanged)
        {
            var blockReason = await MutationGuards.GetPassengerMutationBlockReasonAsync(_context, passengerId);
            if (blockReason != null)
            {
                // PII: no logueamos nombre/documento, solo IDs.
                _logger.LogWarning(
                    "UpdatePassengerAsync rejected. PassengerId={PassengerId} ReservaId={ReservaId}. Reason={Reason}",
                    passengerId, passenger.ReservaId, blockReason);
                throw new InvalidOperationException(blockReason);
            }
        }

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

        var blockReason = await DeleteGuards.GetPassengerDeleteBlockReasonAsync(_context, passengerId);
        if (blockReason != null)
        {
            // Warning: el guard incluye un check fiscal (factura emitida con CAE — C27).
            // El reviewer pidio Warning para marcar potencial riesgo fiscal/auditoria;
            // mantenemos Warning para todos los rechazos del guard para no bifurcar
            // por motivo (todos los demas son tambien rechazos sensibles: vouchers,
            // estado Operativo/Cerrado).
            // No loguear nombre/documento del pasajero (PII) — solo IDs y motivo.
            _logger.LogWarning(
                "RemovePassengerAsync rejected. PassengerId={PassengerId} ReservaId={ReservaId}. Reason={Reason}",
                passengerId, passenger.ReservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

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

        // B1.15 Fase 0' (CODE-01): mismo guard que PaymentService.UpdatePaymentAsync
        // — este es el path legacy "via reserva nested". Sin esto, el bypass del
        // controller nested deja editar pagos con recibo o factura AFIP viva.
        var blockReason = await MutationGuards.GetPaymentMutationBlockReasonAsync(_context, paymentId);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "UpdatePaymentAsync (legacy via reserva) rejected. PaymentId={PaymentId} ReservaId={ReservaId}. Reason={Reason}",
                paymentId, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

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

        // C28: mismo guard que PaymentService.DeletePaymentAsync — este es el path
        // legacy "via reserva nested" (ReservasController.DeletePayment).
        var blockReason = await DeleteGuards.GetPaymentDeleteBlockReasonAsync(_context, paymentId);
        if (blockReason != null)
        {
            _logger.LogWarning(
                "DeletePaymentAsync (legacy via reserva) rejected. PaymentId={PaymentId} ReservaId={ReservaId}. Reason={Reason}",
                paymentId, reservaId, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        payment.IsDeleted = true;
        payment.DeletedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        await UpdateBalanceAsync(reservaId);
    }

    public async Task<Reserva> UpdateStatusAsync(int id, string status, string? actorUserId = null)
    {
        var file = await _context.Reservas.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        await UpdateBalanceAsync(id);
        file = await _context.Reservas.FindAsync(id);
        if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

        // Capturamos el estado origen ANTES de la transicion para el rastro auditable (FIX 5).
        var fromStatus = file.Status;

        // Rediseño Fase A+B (2026-05-30): el flag elige el ciclo de vida. Lo leemos ANTES del
        // whitelist porque el set de estados aceptables depende del flag.
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(CancellationToken.None);

        // B1 del review (byte-identico con flag OFF): la whitelist de estados aceptables se gatea
        // por el flag. Con OFF, Sold/ToSettle NO existen para el resto del sistema, asi que un POST
        // directo con status="Sold"/"ToSettle" debe rebotar con el ArgumentException de siempre
        // (igual que cualquier string desconocido). Con ON, se aceptan como string (que la transicion
        // concreta sea legal lo decide despues la matriz forward nueva).
        var validStatuses = settings.EnableSoldToSettleStates
            ? new[]
              {
                  EstadoReserva.Budget, EstadoReserva.Sold, EstadoReserva.Confirmed,
                  EstadoReserva.Traveling, EstadoReserva.ToSettle, EstadoReserva.Closed,
                  EstadoReserva.Cancelled
              }
            : new[]
              {
                  EstadoReserva.Budget, EstadoReserva.Confirmed,
                  EstadoReserva.Traveling, EstadoReserva.Closed,
                  EstadoReserva.Cancelled
              };
        if (!validStatuses.Contains(status)) throw new ArgumentException("Estado no válido");

        if (settings.EnableSoldToSettleStates)
        {
            await ApplySoldToSettleTransitionAsync(file, id, status, settings);

            // FIX 5 (A1): rastro auditable de las transiciones forward de la cadena nueva.
            // Solo logueamos cuando hubo un cambio real de estado y NO es una cancelacion
            // (la cancelacion tiene su propio flujo/auditoria). El camino clasico (flag OFF)
            // NO se loguea: es deuda preexistente fuera de scope.
            var isRealForwardChange =
                !string.Equals(fromStatus, status, StringComparison.OrdinalIgnoreCase)
                && status != EstadoReserva.Cancelled;
            if (isRealForwardChange)
            {
                _context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
                {
                    ReservaId = id,
                    FromStatus = fromStatus,
                    ToStatus = status,
                    Direction = "Forward",
                    ByUserId = actorUserId,
                    OccurredAt = DateTime.UtcNow
                });
            }
        }
        else
        {
            await ApplyClassicTransitionAsync(file, id, status, settings);
        }

        file.Status = status;

        await _context.SaveChangesAsync();
        return file;
    }

    // ============================================================
    // Rediseño Fase A+B: las transiciones del ciclo de vida estan partidas en dos metodos
    // privados (clasico vs nuevo) + helpers de gate reutilizables. Cada metodo valida que
    // la transicion sea legal contra su matriz forward y aplica los gates en el paso correcto.
    // Ninguno hace SaveChanges; el caller (UpdateStatusAsync) persiste una sola vez.
    // ============================================================

    /// <summary>
    /// Camino CLASICO (flag EnableSoldToSettleStates = OFF). Comportamiento byte-identico al
    /// historico: Budget-&gt;Confirmed valida readiness, Cancelled es libre (lo maneja el flujo
    /// de cancelacion), -&gt;Traveling valida capacidad + servicios + economico, -&gt;Closed exige
    /// Balance == 0.
    /// </summary>
    private async Task ApplyClassicTransitionAsync(Reserva file, int id, string status, OperationalFinanceSettings settings)
    {
        // Defensa B1 (byte-identico con flag OFF): el camino clasico NO conoce Sold/ToSettle.
        // El whitelist de UpdateStatusAsync ya los rechaza con el flag OFF, pero dejamos este
        // rechazo explicito como ultima linea: si alguien llega aca con esos targets, abortamos
        // en vez de escribir un estado que el resto del sistema (flag OFF) no entiende.
        if (status == EstadoReserva.Sold || status == EstadoReserva.ToSettle)
            throw new ArgumentException("Estado no válido");

        if (file.Status == EstadoReserva.Budget && status == EstadoReserva.Confirmed)
        {
            // En el ciclo clasico, "confirmar" es vender: aca van los gates de readiness.
            await EnsureReadinessForSaleAsync(id);
        }

        if (file.Status == EstadoReserva.Confirmed && status == EstadoReserva.Budget)
        {
            await EnsureCanRevertToBudgetAsync(id);
        }

        if (status == EstadoReserva.Traveling)
        {
            // En el ciclo clasico, pasar a Operativo valida TODO junto: capacidad, servicios
            // sin confirmar Y economico.
            await EnsureCanStartTravelingAsync(file, id, settings, checkUnconfirmedServices: true);
        }

        if (status == EstadoReserva.Closed)
        {
            EnsureCanCloseAndStampClosedAt(file);
        }
    }

    /// <summary>
    /// Camino NUEVO (flag EnableSoldToSettleStates = ON). Cadena
    /// Budget -&gt; Sold -&gt; Confirmed -&gt; Traveling -&gt; Closed con gates relocalizados, y ToSettle
    /// como DESVIO OPCIONAL colgando de Traveling:
    ///  - Budget-&gt;Sold: readiness (≥1 servicio + normalizar a Solicitado + pasajeros nominales).
    ///  - Sold-&gt;Confirmed: el operador confirmo los servicios (gate de servicios sin confirmar).
    ///  - Confirmed-&gt;Traveling: capacidad + economico (sin re-chequear servicios sin confirmar:
    ///    ya se garantizo en Sold-&gt;Confirmed).
    ///  - Traveling-&gt;Closed: cierre por DEFAULT, Balance == 0 + ClosedAt (igual que el clasico).
    ///  - Traveling-&gt;ToSettle: desvio OPCIONAL, sin gate de balance (es apartar para liquidar,
    ///    no el cierre).
    ///  - ToSettle-&gt;Closed: cierre manual desde la bandeja "A liquidar", Balance == 0 + ClosedAt.
    ///
    /// El salto directo Budget-&gt;Confirmed sigue PROHIBIDO (INV-SM-01): la matriz forward nueva
    /// no lo lista. Traveling-&gt;Closed AHORA SI se permite (es el cierre por default).
    /// </summary>
    private async Task ApplySoldToSettleTransitionAsync(Reserva file, int id, string status, OperationalFinanceSettings settings)
    {
        // Set idempotente (mismo estado): no-op, lo dejamos pasar sin validar la matriz.
        // Reproduce el comportamiento del ciclo clasico, donde poner el mismo estado no rompia.
        if (string.Equals(file.Status, status, StringComparison.OrdinalIgnoreCase))
            return;

        // Cancelled se permite desde cualquier estado (mismo criterio que el ciclo clasico:
        // la cancelacion real la maneja su propio flujo; aca solo no bloqueamos el set).
        if (status == EstadoReserva.Cancelled)
            return;

        // Validacion de matriz: la transicion (from -> to) tiene que estar en la matriz nueva.
        // Si no esta, es ilegal (ej. Budget->Confirmed directo, Traveling->Closed directo).
        if (!AllowedForwardTransitionsSoldToSettle.TryGetValue(file.Status, out var allowedTargets)
            || !allowedTargets.Contains(status, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"No se puede pasar de {file.Status} a {status} con el ciclo de estados nuevo. " +
                $"Transiciones permitidas desde {file.Status}: " +
                $"{(allowedTargets == null || allowedTargets.Length == 0 ? "(ninguna hacia adelante)" : string.Join(", ", allowedTargets))}. " +
                "Recorda que el ciclo nuevo es Presupuesto -> Vendida -> Confirmada -> En viaje -> A liquidar -> Finalizada (de a un paso).");
        }

        if (file.Status == EstadoReserva.Budget && status == EstadoReserva.Sold)
        {
            // Readiness se mueve aca: vender exige servicios + pasajeros nominales.
            await EnsureReadinessForSaleAsync(id);
        }

        if (file.Status == EstadoReserva.Sold && status == EstadoReserva.Confirmed)
        {
            // El operador confirma: el unico gate aca es "no queden servicios sin confirmar".
            var unconfirmedReason = await ReservaCapacityRules.GetUnconfirmedServicesBlockReasonAsync(_context, id, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(unconfirmedReason))
                throw new InvalidOperationException($"No se puede confirmar con el operador: {unconfirmedReason}");
        }

        if (file.Status == EstadoReserva.Confirmed && status == EstadoReserva.Traveling)
        {
            // Pasar a En viaje valida capacidad + economico, PERO ya NO re-chequea servicios
            // sin confirmar (eso quedo garantizado en Sold->Confirmed).
            await EnsureCanStartTravelingAsync(file, id, settings, checkUnconfirmedServices: false);
        }

        // Traveling -> ToSettle: sin gate. Es el desvio OPCIONAL (apartar para liquidar con el
        // operador), no el cierre. La reserva queda en la bandeja "A liquidar" hasta cierre manual.

        // El cierre real (Balance == 0 + ClosedAt) corre tanto en el cierre por DEFAULT
        // (Traveling->Closed) como en el cierre desde la bandeja de liquidacion (ToSettle->Closed).
        // Ambos comparten el mismo gate: no se puede cerrar con saldo pendiente.
        if (status == EstadoReserva.Closed
            && (file.Status == EstadoReserva.Traveling || file.Status == EstadoReserva.ToSettle))
        {
            EnsureCanCloseAndStampClosedAt(file);
        }
    }

    /// <summary>
    /// Gate de readiness para vender una reserva (≥1 servicio + normalizar servicios a
    /// "Solicitado" + pasajeros nominales completos). En el ciclo clasico corre en
    /// Budget-&gt;Confirmed; en el nuevo, en Budget-&gt;Sold. Es la misma logica, solo se movio
    /// de paso.
    /// </summary>
    private async Task EnsureReadinessForSaleAsync(int id)
    {
        var hasServices = await HasServicesAsync(id);
        if (!hasServices)
            throw new InvalidOperationException("No se puede confirmar la reserva porque no tiene ningun servicio cargado. Agrega al menos un servicio antes de reservar.");

        // Normalizacion defensiva: en Presupuesto cualquier servicio debe estar en
        // "Solicitado". Si por algun bypass (API directa, data preexistente) hay
        // alguno con otro status, lo forzamos al pasar al siguiente estado. El agente despues
        // los confirma uno por uno antes de pasar a Operativo.
        await NormalizeAllServicesToSolicitadoAsync(id);

        // Derivamos pax esperados de los servicios (no del campo AdultCount viejo).
        // El frontend ya hace este check via /transition-readiness y un modal forzado
        // (ConfirmReservaModal); esto es last-line defense para evitar bypass via API directa.
        var fullForPax = await _context.Reservas
            .AsNoTracking()
            .Include(r => r.HotelBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.AssistanceBookings)
            .FirstAsync(r => r.Id == id);
        var (adA, adC, adI, _) = ComputePaxCompositionFromServices(fullForPax);
        var expectedPax = adA + adC + adI;
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

    /// <summary>
    /// Gate "volver a Presupuesto": no se puede si hay pagos, facturas o servicios cargados.
    /// En el ciclo clasico corre en Confirmed-&gt;Budget (via UpdateStatusAsync); el revert por
    /// el endpoint dedicado (RevertStatusAsync) tiene su propia copia del mismo criterio.
    /// </summary>
    private async Task EnsureCanRevertToBudgetAsync(int id)
    {
        var hasPayments = await _context.Payments.AnyAsync(p => p.ReservaId == id && !p.IsDeleted);
        if (hasPayments) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay pagos registrados. ElimÃ­nalos primero.");

        var hasInvoices = await _context.Invoices.AnyAsync(i => i.ReservaId == id);
        if (hasInvoices) throw new InvalidOperationException("No se puede volver a Presupuesto porque hay facturas emitidas. Debes anularlas primero (Nota de CrÃ©dito).");

        var hasServices = await HasServicesAsync(id);
        if (hasServices) throw new InvalidOperationException("No se puede volver a Presupuesto porque tiene servicios cargados. ElimÃ­nalos primero.");
    }

    /// <summary>
    /// Gates para pasar a En viaje (Traveling): reserva no vacia + capacidad pax + economico.
    /// El chequeo de "servicios sin confirmar" es opcional: en el ciclo clasico va junto aca
    /// (checkUnconfirmedServices=true), en el nuevo ya se hizo en Sold-&gt;Confirmed
    /// (checkUnconfirmedServices=false).
    /// </summary>
    private async Task EnsureCanStartTravelingAsync(Reserva file, int id, OperationalFinanceSettings settings, bool checkUnconfirmedServices)
    {
        var fullReserva = await _context.Reservas
            .Include(r => r.Servicios)
            .Include(r => r.HotelBookings)
            .Include(r => r.FlightSegments)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (fullReserva == null) throw new KeyNotFoundException("Reserva no encontrada");

        var emptyReason = EconomicRulesHelper.GetEmptyReservaBlockReason(fullReserva);
        if (!string.IsNullOrWhiteSpace(emptyReason))
            throw new InvalidOperationException($"No se puede pasar a Operativo: {emptyReason}");

        // Inconsistencia de capacidad pasajeros vs servicios — bloqueo independiente del estado financiero.
        var capacityReason = await ReservaCapacityRules.GetBlockReasonAsync(_context, id, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(capacityReason))
            throw new InvalidOperationException($"No se puede pasar a Operativo: {capacityReason}");

        if (checkUnconfirmedServices)
        {
            // Servicios sin confirmar con el proveedor — no entran al balance, datos sucios.
            var unconfirmedReason = await ReservaCapacityRules.GetUnconfirmedServicesBlockReasonAsync(_context, id, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(unconfirmedReason))
                throw new InvalidOperationException($"No se puede pasar a Operativo: {unconfirmedReason}");
        }

        var blockReason = EconomicRulesHelper.GetOperativeBlockReason(file, settings);
        if (!string.IsNullOrWhiteSpace(blockReason))
            throw new InvalidOperationException(blockReason);
    }

    /// <summary>
    /// Gate de cierre: no se puede cerrar con saldo pendiente. Si pasa, estampa ClosedAt.
    /// En el ciclo clasico corre en -&gt;Closed; en el nuevo, en ToSettle-&gt;Closed.
    /// </summary>
    private static void EnsureCanCloseAndStampClosedAt(Reserva file)
    {
        if (file.Balance > 0)
            throw new InvalidOperationException($"No se puede cerrar la reserva porque tiene un saldo pendiente de {file.Balance:N2}.");
        file.ClosedAt = DateTime.UtcNow;
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
        // Pre-flight guard antes de abrir transaccion: si esta bloqueado, evitamos
        // tocar la BD. Las consultas son AsNoTracking, asi que no interfieren con
        // el SaveChanges posterior.
        var blockReason = await DeleteGuards.GetReservaDeleteBlockReasonAsync(_context, id);
        if (blockReason != null)
        {
            // Information: rechazo benigno por estado/contenido. No hay riesgo fiscal.
            _logger.LogInformation(
                "DeleteReservaAsync rejected. ReservaId={ReservaId}. Reason={Reason}",
                id, blockReason);
            throw new InvalidOperationException(blockReason);
        }

        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var file = await _context.Reservas
                    .Include(f => f.Servicios)
                    .Include(f => f.Passengers)
                    .Include(f => f.FlightSegments)
                    .Include(f => f.HotelBookings)
                    .Include(f => f.TransferBookings)
                    .Include(f => f.PackageBookings)
                    .Include(f => f.AssistanceBookings)
                    .FirstOrDefaultAsync(f => f.Id == id);

                if (file == null) throw new KeyNotFoundException("Reserva no encontrada");

                if (file.Servicios.Any()) _context.Servicios.RemoveRange(file.Servicios);
                if (file.Passengers.Any()) _context.Passengers.RemoveRange(file.Passengers);
                if (file.FlightSegments.Any()) _context.FlightSegments.RemoveRange(file.FlightSegments);
                if (file.HotelBookings.Any()) _context.HotelBookings.RemoveRange(file.HotelBookings);
                if (file.TransferBookings.Any()) _context.TransferBookings.RemoveRange(file.TransferBookings);
                if (file.PackageBookings.Any()) _context.PackageBookings.RemoveRange(file.PackageBookings);
                // Bloque 3: borrar las asistencias junto con la reserva (cascade explicito, igual
                // que los otros 4 tipos). El DeleteBehavior.Cascade en BD es la red de seguridad.
                if (file.AssistanceBookings.Any()) _context.AssistanceBookings.RemoveRange(file.AssistanceBookings);

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
            .Include(f => f.AssistanceBookings)
            .FirstOrDefaultAsync(f => f.Id == reservaId);

        if (file == null) return;

        // P1 refactor (behavior-preserving): la matematica del saldo (venta/costo/pagado/saldo)
        // vive ahora en el calculador de dominio ReservaMoneyCalculator, unica fuente de la cuenta.
        // Aca solo cargamos los Includes (arriba), calculamos y persistimos. Los numeros son
        // identicos a la version inline anterior.
        var money = TravelApi.Domain.Reservations.ReservaMoneyCalculator.Calculate(file);

        file.TotalSale = money.TotalSale;
        file.TotalCost = money.TotalCost;
        file.TotalPaid = money.TotalPaid;
        file.Balance = money.Balance;

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
        dto.IsFullyPaid = dto.Balance == 0m;
        dto.HasOverdueDebt = dto.EndDate.HasValue
            && dto.EndDate.Value.Date < DateTime.UtcNow.Date
            && dto.Balance > 0m;
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
        dto.IsFullyPaid = dto.Balance == 0m;
        dto.HasOverdueDebt = dto.EndDate.HasValue
            && dto.EndDate.Value.Date < DateTime.UtcNow.Date
            && dto.Balance > 0m;
    }

    private static bool ComputeIsInProgress(string status, DateTime? startDate, DateTime? endDate)
    {
        if (status != EstadoReserva.Traveling) return false;
        if (!startDate.HasValue) return false;
        // Sin fecha de fin no podemos saber si esta en curso. Antes retornabamos true
        // y dejaba reservas marcadas "• En curso" indefinidamente (bug observado en
        // reservas viejas con EndDate=null cuyas fechas ya habian pasado).
        if (!endDate.HasValue) return false;
        var today = DateTime.UtcNow.Date;
        if (startDate.Value.Date > today) return false;
        if (endDate.Value.Date < today) return false;
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

    private static IQueryable<Reserva> ApplyReservaView(IQueryable<Reserva> query, string? view, bool soldToSettleEnabled)
    {
        return (view ?? "active").Trim().ToLowerInvariant() switch
        {
            "budget" => query.Where(r => r.Status == EstadoReserva.Budget),
            // Rediseño Fase A+B: vistas dedicadas para los estados nuevos. Con el flag OFF
            // estas vistas devuelven 0 filas (nadie esta en esos estados) y no se ofrecen en
            // la UI; con el flag ON filtran correctamente.
            "sold" => query.Where(r => r.Status == EstadoReserva.Sold),
            "to-settle" => query.Where(r => r.Status == EstadoReserva.ToSettle),
            "reserved" => query.Where(r => r.Status == EstadoReserva.Confirmed),
            "operative" => query.Where(r => r.Status == EstadoReserva.Traveling),
            "closed" => query.Where(r =>
                r.Status == EstadoReserva.Closed ||
                r.Status == EstadoReserva.Cancelled),
            "archived" => query.Where(r => r.Status == "Archived"),
            // "active" = todo lo que esta en gestion (ni Presupuesto, que tiene su propio tab,
            // ni cerrada/cancelada/archivada). Con el flag ON suma Sold y ToSettle; con el flag
            // OFF nunca hay filas en esos estados, asi que el resultado es identico a hoy
            // (Confirmed + Traveling).
            _ when soldToSettleEnabled => query.Where(r =>
                r.Status == EstadoReserva.Sold ||
                r.Status == EstadoReserva.Confirmed ||
                r.Status == EstadoReserva.Traveling ||
                r.Status == EstadoReserva.ToSettle),
            _ => query.Where(r =>
                r.Status == EstadoReserva.Confirmed ||
                r.Status == EstadoReserva.Traveling)
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
            || await _context.PackageBookings.AnyAsync(p => p.ReservaId == reservaId)
            || await _context.AssistanceBookings.AnyAsync(a => a.ReservaId == reservaId);
    }

    /// <summary>
    /// Defensa al pasar de Presupuesto a Reservado: cualquier servicio con Status
    /// distinto de "Solicitado" se normaliza. Esto cubre bypasses por API directa o
    /// data preexistente. En el flujo normal, los servicios creados en Presupuesto
    /// ya quedan en "Solicitado" gracias a ReservaCapacityRules.ShouldForceSolicitadoStatusAsync
    /// que aplica BookingService al crear/actualizar.
    /// </summary>
    private async Task NormalizeAllServicesToSolicitadoAsync(int reservaId)
    {
        var hotels = await _context.HotelBookings.Where(h => h.ReservaId == reservaId && h.Status != "Solicitado").ToListAsync();
        foreach (var h in hotels) h.Status = "Solicitado";

        var transfers = await _context.TransferBookings.Where(t => t.ReservaId == reservaId && t.Status != "Solicitado").ToListAsync();
        foreach (var t in transfers) t.Status = "Solicitado";

        var packages = await _context.PackageBookings.Where(p => p.ReservaId == reservaId && p.Status != "Solicitado").ToListAsync();
        foreach (var p in packages) p.Status = "Solicitado";

        var flights = await _context.FlightSegments.Where(f => f.ReservaId == reservaId && f.Status != "Solicitado").ToListAsync();
        foreach (var f in flights) f.Status = "Solicitado";

        var assistances = await _context.AssistanceBookings.Where(a => a.ReservaId == reservaId && a.Status != "Solicitado").ToListAsync();
        foreach (var a in assistances) a.Status = "Solicitado";

        var generics = await _context.Servicios.Where(s => s.ReservaId == reservaId && s.Status != "Solicitado").ToListAsync();
        foreach (var g in generics) g.Status = "Solicitado";

        // SaveChanges sucede al final de UpdateStatusAsync.
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


