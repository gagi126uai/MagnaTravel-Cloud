using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Notifications;

/// <summary>
/// Implementacion de <see cref="IServiceResolutionFailureNotifier"/> (decision firmada 2026-08-18). Sigue
/// el mismo patron que <c>CashLedgerRefundReconciliationJob</c>/<c>PartialCreditNoteReviewAlertJob</c>:
/// avisa a TODOS los usuarios en rol Admin, deduplicando por aviso VIVO con la misma clave (si ya hay uno
/// vivo, no se repite — no actualizamos el mensaje del viejo, mismo criterio que esos jobs).
///
/// <para><b>Por que Admin y no el vendedor que hizo el click</b>: el vendedor YA vio el error en la fila al
/// momento del intento (esta notificacion no le agrega informacion nueva a EL). El valor de la campanita
/// aca es que quede rastro para que un admin lo note despues, aunque el vendedor no vuelva a mirar esa
/// reserva — mismo rol que cumplen los demas avisos de "esto quedo trabado" del sistema.</para>
/// </summary>
public class ServiceResolutionFailureNotifier : IServiceResolutionFailureNotifier
{
    private readonly AppDbContext _dbContext;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<ServiceResolutionFailureNotifier> _logger;

    public ServiceResolutionFailureNotifier(
        AppDbContext dbContext,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        ILogger<ServiceResolutionFailureNotifier> logger)
    {
        _dbContext = dbContext;
        _notificationService = notificationService;
        _userManager = userManager;
        _logger = logger;
    }

    public async Task NotifyFailureAsync(
        ServiceResolutionKind kind,
        string servicePublicIdOrLegacyId,
        string businessErrorMessage,
        CancellationToken ct)
    {
        try
        {
            var info = await ResolveServiceInfoAsync(kind, servicePublicIdOrLegacyId, ct);
            if (info is null)
            {
                _logger.LogWarning(
                    "ServiceResolutionFailureNotifier: no se encontro el servicio {Kind}/{ServiceIdOrPublicId}, no se avisa el fallo.",
                    kind, servicePublicIdOrLegacyId);
                return;
            }

            var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
            if (adminUsers.Count == 0)
            {
                _logger.LogWarning(
                    "ServiceResolutionFailureNotifier: fallo el servicio {Kind}/{ServiceId} pero no hay usuarios Admin a quien avisar.",
                    kind, info.ServiceId);
                return;
            }

            var resolutionKey = NotificationResolutionKeys.ForServiceResolutionError(kind.ToString(), info.ServiceId);

            // Gate de exposicion de datos: el mensaje habla de la reserva por su NUMERO de negocio
            // (ej. 2026-1067), jamas por un id interno ni un GUID.
            var message = $"No se pudo confirmar un servicio con el operador — Reserva {info.NumeroReserva}: {businessErrorMessage}";

            foreach (var admin in adminUsers)
            {
                // Dedup por aviso VIVO (mismo patron que los jobs recurrentes): si este admin ya tiene un
                // aviso sin resolver de este MISMO servicio, no se repite. Si el motivo del rechazo cambio
                // entre un intento y el siguiente, el aviso viejo se queda con el mensaje viejo — se
                // actualiza recien cuando alguien lo lee/descarta y vuelve a fallar (mismo criterio que
                // CashLedgerRefundReconciliationJob/PartialCreditNoteReviewAlertJob: solo saltear, nunca
                // pisar un aviso ya creado).
                var hasLiveAlert = await _dbContext.Notifications.AnyAsync(n =>
                    n.UserId == admin.Id
                    && n.ResolutionKey == resolutionKey
                    && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed, ct);

                if (hasLiveAlert)
                    continue;

                await _notificationService.CreateAndSendAsync(new Notification
                {
                    UserId = admin.Id,
                    Type = NotificationTypes.Error,
                    // Normal (no Urgent): la decision firmada pide que este aviso NO dispare el banner
                    // urgente, solo sume un punto en la campanita.
                    Priority = "Normal",
                    RelatedEntityId = info.ReservaId,
                    RelatedEntityType = NotificationRelatedEntityTypes.Reserva,
                    ResolutionKey = resolutionKey,
                    Message = message,
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw; // el apagado del request/worker no se traga
        }
        catch (Exception ex)
        {
            // Blindaje explicito (decision 2026-08-18): que falle ESTE aviso nunca puede tumbar la
            // respuesta del endpoint que lo disparo — esa respuesta ya salio antes de llegar aca.
            _logger.LogError(ex,
                "ServiceResolutionFailureNotifier: fallo creando el aviso de error de resolucion para {Kind}/{ServiceIdOrPublicId}.",
                kind, servicePublicIdOrLegacyId);
        }
    }

    public async Task NotifyResolvedAsync(ServiceResolutionKind kind, string servicePublicIdOrLegacyId, CancellationToken ct)
    {
        try
        {
            var info = await ResolveServiceInfoAsync(kind, servicePublicIdOrLegacyId, ct);
            if (info is null)
                return;

            var resolutionKey = NotificationResolutionKeys.ForServiceResolutionError(kind.ToString(), info.ServiceId);
            await _notificationService.ResolveByKeyAsync(resolutionKey, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "ServiceResolutionFailureNotifier: fallo apagando el aviso de error de resolucion para {Kind}/{ServiceIdOrPublicId}.",
                kind, servicePublicIdOrLegacyId);
        }
    }

    /// <summary>Servicio + su reserva, lo minimo que necesita el aviso (id interno del servicio para la clave de dedup, id y numero de la reserva para el Message y el RelatedEntityId).</summary>
    private sealed class ServiceInfo
    {
        public int ServiceId { get; init; }
        public int ReservaId { get; init; }
        public string NumeroReserva { get; init; } = string.Empty;
    }

    /// <summary>
    /// Busca el servicio por su id de ruta (GUID publico o id legacy numerico, igual convencion que
    /// <c>OwnershipResolver</c>) y trae los 3 datos que necesita el aviso. Un metodo por tipo (misma forma
    /// que <c>OwnershipResolver.Resolve*ResponsibleAsync</c>): cada tipo vive en su propia tabla, no hay
    /// una query generica posible aca.
    /// </summary>
    private Task<ServiceInfo?> ResolveServiceInfoAsync(ServiceResolutionKind kind, string publicIdOrLegacyId, CancellationToken ct)
    {
        var (publicId, legacyId) = ParseId(publicIdOrLegacyId);
        if (publicId is null && legacyId is null)
            return Task.FromResult<ServiceInfo?>(null);

        return kind switch
        {
            ServiceResolutionKind.FlightSegment => _dbContext.FlightSegments.AsNoTracking()
                .Where(s => publicId.HasValue ? s.PublicId == publicId.Value : s.Id == legacyId!.Value)
                .Where(s => s.Reserva != null)
                .Select(s => new ServiceInfo { ServiceId = s.Id, ReservaId = s.ReservaId, NumeroReserva = s.Reserva!.NumeroReserva })
                .FirstOrDefaultAsync(ct),
            ServiceResolutionKind.HotelBooking => _dbContext.HotelBookings.AsNoTracking()
                .Where(s => publicId.HasValue ? s.PublicId == publicId.Value : s.Id == legacyId!.Value)
                .Where(s => s.Reserva != null)
                .Select(s => new ServiceInfo { ServiceId = s.Id, ReservaId = s.ReservaId, NumeroReserva = s.Reserva!.NumeroReserva })
                .FirstOrDefaultAsync(ct),
            ServiceResolutionKind.TransferBooking => _dbContext.TransferBookings.AsNoTracking()
                .Where(s => publicId.HasValue ? s.PublicId == publicId.Value : s.Id == legacyId!.Value)
                .Where(s => s.Reserva != null)
                .Select(s => new ServiceInfo { ServiceId = s.Id, ReservaId = s.ReservaId, NumeroReserva = s.Reserva!.NumeroReserva })
                .FirstOrDefaultAsync(ct),
            ServiceResolutionKind.PackageBooking => _dbContext.PackageBookings.AsNoTracking()
                .Where(s => publicId.HasValue ? s.PublicId == publicId.Value : s.Id == legacyId!.Value)
                .Where(s => s.Reserva != null)
                .Select(s => new ServiceInfo { ServiceId = s.Id, ReservaId = s.ReservaId, NumeroReserva = s.Reserva!.NumeroReserva })
                .FirstOrDefaultAsync(ct),
            ServiceResolutionKind.AssistanceBooking => _dbContext.AssistanceBookings.AsNoTracking()
                .Where(s => publicId.HasValue ? s.PublicId == publicId.Value : s.Id == legacyId!.Value)
                .Where(s => s.Reserva != null)
                .Select(s => new ServiceInfo { ServiceId = s.Id, ReservaId = s.ReservaId, NumeroReserva = s.Reserva!.NumeroReserva })
                .FirstOrDefaultAsync(ct),
            _ => Task.FromResult<ServiceInfo?>(null),
        };
    }

    /// <summary>Mismo parseo que <c>OwnershipResolver.ParseId</c>: la ruta trae un GUID publico o, en datos legacy, un id numerico.</summary>
    private static (Guid? publicId, int? legacyId) ParseId(string raw)
    {
        if (Guid.TryParse(raw, out var guid))
            return (guid, null);
        if (int.TryParse(raw, out var legacy) && legacy > 0)
            return (null, legacy);
        return (null, null);
    }
}
