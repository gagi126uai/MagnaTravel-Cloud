using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3.6 (ADR-009 §2.10 + plan tactico FC1.3 §FC1.3.6, 2026-05-21): job
/// recurrente que detecta <see cref="BookingCancellation"/> trabados en
/// <see cref="BookingCancellationStatus.ManualReviewPending"/> y alerta a los
/// admins porque el plazo fiscal RG 4540 (15 dias para emitir la NC desde la
/// confirmacion con el cliente) esta por vencer.
///
/// <para>
/// <b>Por que existe</b>: cuando el clasificador FC1.3 detecta un caso que
/// requiere revision manual (factura A, items no reintegrables, monto sobre
/// threshold, etc.), el BC queda esperando que un admin resuelva. Si nadie
/// lo atiende, la NC podria emitirse fuera del plazo RG 4540 y eso es una
/// multa fiscal. Este job nos avisa antes de que pase.
/// </para>
///
/// <para>
/// <b>Que NO hace</b> (out of scope FC1.3.6):
/// <list type="bullet">
///   <item>Reconciliacion bridge entre <c>ApprovalRequest.Status=Approved</c>
///   y <c>BC.Status=ManualReviewPending</c> (eso es FC1.3.6b).</item>
///   <item>Endpoint admin force-callback (FC1.3.6b).</item>
///   <item>Emision automatica de la NC al vencerse el plazo (decision
///   explicita: la decision fiscal la toma una persona, no el job).</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Patron</b>: copia el de <see cref="OperationalFinanceMonitorService"/> —
/// service registrado como Scoped en DI, expuesto como recurring job Hangfire
/// que lo invoca via <c>RecurringJob.AddOrUpdate</c> en Program.cs. La logica
/// es idempotente: si el job corre dos veces el mismo dia, la deduplicacion
/// de notificaciones se hace por (UserId, RelatedEntityId, RelatedEntityType,
/// fecha de hoy) — copiamos el mismo guard que el monitor anterior.
/// </para>
/// </summary>
public class PartialCreditNoteReviewAlertJob
{
    private readonly AppDbContext _dbContext;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<PartialCreditNoteReviewAlertJob> _logger;

    public PartialCreditNoteReviewAlertJob(
        AppDbContext dbContext,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        ILogger<PartialCreditNoteReviewAlertJob> logger)
    {
        _dbContext = dbContext;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _notificationService = notificationService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Ejecuta una pasada del job. Hangfire lo invoca con la cron registrada
    /// en Program.cs. Tambien es invocable manualmente (admin script o tests
    /// unitarios).
    /// </summary>
    /// <remarks>
    /// Algoritmo:
    /// <list type="number">
    ///   <item>Si <c>EnablePartialCreditNotes=false</c> -> no-op (modulo apagado).</item>
    ///   <item>Calculamos el threshold de antiguedad usando
    ///   <see cref="OperationalFinanceSettings.ManualReviewMaxDaysBeforeRg4540Alert"/>.</item>
    ///   <item>Buscamos BCs en <see cref="BookingCancellationStatus.ManualReviewPending"/>
    ///   con <c>ConfirmedWithClientAt</c> mas viejo que el threshold.</item>
    ///   <item>Para cada BC: log warning + creamos una <see cref="Notification"/>
    ///   por cada admin (dedup intra-dia para no spam).</item>
    /// </list>
    /// </remarks>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);

        // Si el modulo FC1.3 esta apagado, no tiene sentido alertar — no hay
        // BCs en ManualReviewPending nuevos. (Pueden quedar residuales de
        // staging, pero la decision es no notificar en agencias donde el flag
        // este off para evitar ruido cuando alguien apaga el modulo por crisis).
        if (!settings.EnablePartialCreditNotes)
        {
            _logger.LogDebug(
                "PartialCreditNoteReviewAlertJob: EnablePartialCreditNotes=false, skip.");
            return;
        }

        // Defensa: si por config rota el valor es <= 0, usamos 1 dia como minimo
        // razonable para no devolver un threshold "futuro" que matchee todo.
        var alertDays = Math.Max(settings.ManualReviewMaxDaysBeforeRg4540Alert, 1);

        // Threshold: BCs que confirmaron con el cliente HACE alertDays o mas
        // se consideran "stale" y disparan alerta. Comparamos contra UtcNow
        // porque ConfirmedWithClientAt se persiste en UTC.
        var threshold = DateTime.UtcNow.AddDays(-alertDays);

        // No traemos OperatorRefundAllocations / FiscalSnapshot — el mensaje
        // solo necesita PublicId + numero de reserva + invoice number. El
        // Include de OriginatingInvoice es para mostrar el numero de factura
        // en el texto (mas informativo para el admin).
        var staleBookingCancellations = await _dbContext.BookingCancellations
            .AsNoTracking()
            .Include(bc => bc.OriginatingInvoice)
            .Include(bc => bc.Reserva)
            .Where(bc => bc.Status == BookingCancellationStatus.ManualReviewPending
                      && bc.ConfirmedWithClientAt != null
                      && bc.ConfirmedWithClientAt < threshold)
            .ToListAsync(ct);

        if (staleBookingCancellations.Count == 0)
        {
            _logger.LogDebug(
                "PartialCreditNoteReviewAlertJob: ningun BC stale (threshold={Threshold:o}, alertDays={AlertDays}).",
                threshold, alertDays);
            return;
        }

        // Buscamos los admins UNA sola vez para evitar N+1 contra Identity.
        var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
        if (adminUsers.Count == 0)
        {
            _logger.LogWarning(
                "PartialCreditNoteReviewAlertJob: hay {Count} BC(s) stale pero NO hay usuarios en rol Admin para notificar.",
                staleBookingCancellations.Count);
            return;
        }

        var today = DateTime.UtcNow.Date;

        foreach (var bookingCancellation in staleBookingCancellations)
        {
            // Log warning siempre (auditoria operativa, queda en Serilog aunque
            // las notificaciones fallen por algun motivo).
            _logger.LogWarning(
                "PartialCreditNoteReviewAlertJob: BC {PublicId} (Reserva {ReservaId}) en ManualReviewPending desde {ConfirmedAt:o} - riesgo plazo RG 4540 (15 dias).",
                bookingCancellation.PublicId,
                bookingCancellation.ReservaId,
                bookingCancellation.ConfirmedWithClientAt);

            // Calculamos hace cuantos dias se confirmo (para que el mensaje sea
            // util para el admin sin tener que ir a la base).
            var daysSinceConfirmation = bookingCancellation.ConfirmedWithClientAt.HasValue
                ? (int)(DateTime.UtcNow - bookingCancellation.ConfirmedWithClientAt.Value).TotalDays
                : alertDays;

            // El formato canonico de "numero de factura" en este repo es
            // "PV-NNNNN" (PuntoDeVenta + NumeroComprobante). Lo armamos asi
            // porque Invoice NO tiene una columna unica con el numero formateado.
            var invoice = bookingCancellation.OriginatingInvoice;
            var invoiceLabel = invoice is null
                ? "s/n"
                : $"{invoice.PuntoDeVenta:D5}-{invoice.NumeroComprobante:D8}";
            var reservaNumero = bookingCancellation.Reserva?.NumeroReserva ?? bookingCancellation.ReservaId.ToString();

            var message =
                $"La cancelacion de la reserva {reservaNumero} (factura {invoiceLabel}) " +
                $"esta esperando revision manual hace {daysSinceConfirmation} dia(s). " +
                $"Plazo RG 4540 (15 dias) en riesgo: emitir NC o rechazar lo antes posible.";

            foreach (var admin in adminUsers)
            {
                // Dedup intra-dia: si ya notificamos este BC a este admin hoy,
                // no creamos otra Notification. Esto evita spam cuando el job
                // corre cada hora (config), aunque la cron por default es diaria.
                var alreadyExistsToday = await _dbContext.Notifications.AnyAsync(n =>
                    n.UserId == admin.Id
                    && n.RelatedEntityType == "PartialCreditNoteReviewPending"
                    && n.RelatedEntityId == bookingCancellation.Id
                    && n.CreatedAt.Date == today, ct);

                if (alreadyExistsToday)
                    continue;

                await _notificationService.CreateAndSendAsync(new Notification
                {
                    UserId = admin.Id,
                    Type = "Warning",
                    Priority = "Urgent",
                    RelatedEntityId = bookingCancellation.Id,
                    RelatedEntityType = "PartialCreditNoteReviewPending",
                    Message = message,
                }, ct);
            }
        }
    }
}
