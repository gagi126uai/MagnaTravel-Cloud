using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3.6b (ADR-009 §2.12 round 3 + plan tactico FC1.3 §FC1.3.6b, 2026-05-21):
/// job recurrente que reconcilia <see cref="ApprovalRequest"/> tipo
/// <c>PartialCreditNoteApproval</c> ya resueltos (Approved/Rejected) cuyo
/// <see cref="BookingCancellation"/> asociado quedo trabado en
/// <see cref="BookingCancellationStatus.ManualReviewPending"/>.
///
/// <para>
/// <b>Por que existe</b>: cuando un admin aprueba/rechaza un
/// <c>PartialCreditNoteApproval</c>, <c>ApprovalRequestService</c> persiste
/// la AR (Approved/Rejected) y DESPUES dispara el callback del bridge en una
/// transaccion aparte. Si esa 2da tx falla (db caida, deadlock, excepcion en
/// el bridge), la AR queda OK pero el BC queda huerfano en
/// <c>ManualReviewPending</c>. Este job detecta esos huerfanos cada 30 min y
/// reaplica el callback. El bridge es idempotente por contrato (ADR-009 §2.7
/// N-007), asi que reaplicar no genera duplicados.
/// </para>
///
/// <para>
/// <b>Que NO hace</b>:
/// <list type="bullet">
///   <item>Alertar sobre BCs en ManualReviewPending por riesgo RG 4540 — eso
///   es del <see cref="PartialCreditNoteReviewAlertJob"/> (FC1.3.6).</item>
///   <item>Forzar transiciones sin invocar al bridge — el bridge sigue siendo
///   la unica via para mutar el BC. Si el bridge tira siempre, este job no
///   lo "saltea": despues de N intentos avisa al admin y para de intentar.</item>
///   <item>Emitir NCs ni tocar AFIP — eso lo hace el bridge cuando llega al
///   estado <c>ManualReviewApproved</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Anti-spam de notificaciones</b> (N-003 round 3): cuando el counter
/// <c>BridgeRetryCount</c> alcanza <c>BridgeReconciliationMaxRetries</c>, el
/// job dispara UNA SOLA notificacion adicional avisando que la situacion
/// requiere intervencion manual (admin debe usar
/// <c>POST /api/approvals/{publicId}/force-bridge-callback</c>) y a partir de
/// alli el filtro de la query <c>BridgeRetryCount &lt; maxRetries</c> deja al
/// approval afuera del job. Si el admin fuerza el callback exitoso, ese
/// endpoint resetea el counter a 0 y el job vuelve a intentar.
/// </para>
///
/// <para>
/// <b>Patron</b>: copia el de <see cref="PartialCreditNoteReviewAlertJob"/>
/// (FC1.3.6) — service Scoped, registrado como recurring Hangfire en
/// Program.cs. Cron por default <c>*/30 * * * *</c>; el filtro de antiguedad
/// (<c>BridgeReconciliationStalenessMinutes</c>) corre EN LA QUERY, no en la
/// cron. Eso permite cambiar la "ventana de gracia" sin redeployar Hangfire.
/// </para>
/// </summary>
public class PartialCreditNoteBridgeReconciliationJob
{
    private readonly AppDbContext _dbContext;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly IPartialCreditNoteApprovalBridge _bridge;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<PartialCreditNoteBridgeReconciliationJob> _logger;

    public PartialCreditNoteBridgeReconciliationJob(
        AppDbContext dbContext,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        IPartialCreditNoteApprovalBridge bridge,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        ILogger<PartialCreditNoteBridgeReconciliationJob> logger)
    {
        _dbContext = dbContext;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _bridge = bridge;
        _notificationService = notificationService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Una pasada del job. Hangfire lo invoca via la cron registrada en
    /// Program.cs; tambien se puede invocar manualmente (admin script o tests).
    /// </summary>
    /// <remarks>
    /// Algoritmo:
    /// <list type="number">
    ///   <item>Si <c>EnablePartialCreditNotes=false</c> -> no-op (modulo apagado).</item>
    ///   <item>Calculamos el threshold de antiguedad usando
    ///   <see cref="OperationalFinanceSettings.BridgeReconciliationStalenessMinutes"/>.</item>
    ///   <item>Traemos los <c>ApprovalRequest</c> tipo
    ///   <c>PartialCreditNoteApproval</c> en Approved/Rejected con <c>ResolvedAt</c>
    ///   mas viejo que el threshold Y <c>BridgeRetryCount &lt; maxRetries</c>.</item>
    ///   <item>Para cada AR: chequeamos en una query separada si hay BC asociado
    ///   en <c>ManualReviewPending</c> (si no hay, no es huerfano — el bridge
    ///   ya concilio o el BC fue abortado).</item>
    ///   <item>Llamamos al bridge segun el Status de la AR. Si OK -> limpiamos
    ///   <c>BridgeLastError</c>. Si falla -> incrementamos counter, guardamos
    ///   error, notificamos UNA VEZ al llegar al limite.</item>
    /// </list>
    /// </remarks>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);

        // Modulo apagado: ningun BC nuevo entra a ManualReviewPending. Los
        // residuales legacy se podrian conciliar manualmente si quedaron,
        // pero no es responsabilidad del job correr cuando el flag esta off.
        if (!settings.EnablePartialCreditNotes)
        {
            _logger.LogDebug(
                "PartialCreditNoteBridgeReconciliationJob: EnablePartialCreditNotes=false, skip.");
            return;
        }

        // Defensive: si por mala config los valores son <= 0, usamos defaults
        // razonables para no hacer queries que matcheen todo o que no encuentren
        // nada.
        var stalenessMinutes = Math.Max(settings.BridgeReconciliationStalenessMinutes, 1);
        var maxRetries = Math.Max(settings.BridgeReconciliationMaxRetries, 1);
        var threshold = DateTime.UtcNow.AddMinutes(-stalenessMinutes);

        // Query principal: AR tipo PartialCreditNoteApproval, resuelta hace mas
        // que el staleness, con counter < maxRetries. Limitamos a 200 por pasada
        // para no fundir el job si hay un backlog raro.
        var candidates = await _dbContext.ApprovalRequests
            .Where(ar => ar.RequestType == ApprovalRequestType.PartialCreditNoteApproval
                      && (ar.Status == ApprovalStatus.Approved || ar.Status == ApprovalStatus.Rejected)
                      && ar.ResolvedAt != null
                      && ar.ResolvedAt < threshold
                      && ar.BridgeRetryCount < maxRetries)
            .OrderBy(ar => ar.ResolvedAt)
            .Take(200)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogDebug(
                "PartialCreditNoteBridgeReconciliationJob: ningun candidato (threshold={Threshold:o}, stalenessMinutes={Stale}, maxRetries={Max}).",
                threshold, stalenessMinutes, maxRetries);
            return;
        }

        foreach (var approvalRequest in candidates)
        {
            // Cancelacion cooperativa: si el host se esta apagando entre
            // iteraciones, salimos limpio. Los approvals que quedan se retoman
            // en la proxima pasada (el counter no se incremento, no hay perdida).
            ct.ThrowIfCancellationRequested();

            // 1) Confirmamos que sea HUERFANO: BC asociado todavia en
            //    ManualReviewPending. Si no hay BC o ya transiciono, no hay
            //    nada para conciliar -> skip silencioso (no contamos como
            //    intento, no notificamos).
            var bookingCancellation = await _dbContext.BookingCancellations
                .FirstOrDefaultAsync(
                    bc => bc.PartialCreditNoteApprovalRequestId == approvalRequest.Id
                       && bc.Status == BookingCancellationStatus.ManualReviewPending,
                    ct);

            if (bookingCancellation is null)
            {
                _logger.LogDebug(
                    "PartialCreditNoteBridgeReconciliationJob: AR {ApprovalPublicId} no es huerfana (no hay BC en ManualReviewPending). Skip.",
                    approvalRequest.PublicId);
                continue;
            }

            // 2) Marcamos el intento ANTES de llamar al bridge para que aunque
            //    se caiga el proceso en medio, el ultimo timestamp quede registrado.
            approvalRequest.BridgeLastAttemptAt = DateTime.UtcNow;

            try
            {
                if (approvalRequest.Status == ApprovalStatus.Approved)
                {
                    await _bridge.OnApprovedAsync(
                        approvalRequest.Id,
                        approvalRequest.ResolvedByUserId ?? string.Empty,
                        approvalRequest.ResolvedByUserName,
                        approvalRequest.ResolverNotes,
                        ct);
                }
                else
                {
                    // Status == Rejected (la query filtra solo estos 2).
                    await _bridge.OnRejectedAsync(
                        approvalRequest.Id,
                        approvalRequest.ResolvedByUserId ?? string.Empty,
                        approvalRequest.ResolvedByUserName,
                        approvalRequest.ResolverNotes,
                        ct);
                }

                // 3) Exito: limpiamos el error pero NO tocamos el counter — el
                //    counter solo se resetea desde el endpoint force-callback
                //    (decision explicita de admin) o desde el bridge real cuando
                //    el flujo nuevo arranca limpio. Si el bridge ya transiciono
                //    el BC, en la proxima pasada del job el filtro de BC en
                //    ManualReviewPending ya no matchea y la AR sale de candidatos.
                approvalRequest.BridgeLastError = null;
                await _dbContext.SaveChangesAsync(ct);

                _logger.LogInformation(
                    "PartialCreditNoteBridgeReconciliationJob: AR {ApprovalPublicId} conciliada (Status={Status}, BC={BcPublicId}).",
                    approvalRequest.PublicId, approvalRequest.Status, bookingCancellation.PublicId);
            }
            catch (Exception ex)
            {
                // Cualquier excepcion del bridge (db, integracion, validacion):
                // contamos el intento, guardamos el error truncado, decidimos
                // si avisar al admin.
                approvalRequest.BridgeRetryCount++;
                approvalRequest.BridgeLastError = TruncateErrorMessage(ex.Message);

                // Snapshot del valor previo al limite para chequear la transicion
                // "ahora alcance el limite". Solo notificamos en ESE punto, no
                // en cada reintento posterior — anti-spam.
                var justReachedLimit = approvalRequest.BridgeRetryCount == maxRetries;

                await _dbContext.SaveChangesAsync(ct);

                _logger.LogError(
                    ex,
                    "PartialCreditNoteBridgeReconciliationJob: bridge fallo para AR {ApprovalPublicId} (Status={Status}). RetryCount={Retry}/{Max}.",
                    approvalRequest.PublicId,
                    approvalRequest.Status,
                    approvalRequest.BridgeRetryCount,
                    maxRetries);

                if (justReachedLimit)
                {
                    await NotifyAdminsOfManualInterventionAsync(
                        approvalRequest,
                        bookingCancellation,
                        maxRetries,
                        ct);
                }
            }
        }
    }

    /// <summary>
    /// Notifica una sola vez a TODOS los admins cuando un approval agoto los
    /// reintentos automaticos y necesita force-callback manual. Re-usamos el
    /// patron de <see cref="PartialCreditNoteReviewAlertJob"/>: una notification
    /// por admin via <see cref="INotificationService.CreateAndSendAsync"/>.
    /// </summary>
    /// <remarks>
    /// No tenemos un endpoint <c>NotifyAdminsAsync(message)</c> dedicado en
    /// <see cref="INotificationService"/>; el patron canonico del repo es
    /// traerse los admins con <c>UserManager.GetUsersInRoleAsync("Admin")</c>
    /// y crear una notification por cada uno.
    /// </remarks>
    private async Task NotifyAdminsOfManualInterventionAsync(
        ApprovalRequest approvalRequest,
        BookingCancellation bookingCancellation,
        int maxRetries,
        CancellationToken ct)
    {
        var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
        if (adminUsers.Count == 0)
        {
            _logger.LogWarning(
                "PartialCreditNoteBridgeReconciliationJob: AR {ApprovalPublicId} alcanzo {MaxRetries} reintentos pero NO hay usuarios en rol Admin para notificar.",
                approvalRequest.PublicId, maxRetries);
            return;
        }

        var message =
            $"La aprobacion {approvalRequest.PublicId} (BC {bookingCancellation.PublicId}) " +
            $"alcanzo {maxRetries} reintentos del bridge sin exito. " +
            $"Requiere intervencion manual: usar el endpoint force-bridge-callback con InvariantOverride. " +
            $"Ultimo error: {approvalRequest.BridgeLastError}";

        foreach (var admin in adminUsers)
        {
            await _notificationService.CreateAndSendAsync(new Notification
            {
                UserId = admin.Id,
                Type = "Error",
                Priority = "Urgent",
                RelatedEntityId = approvalRequest.Id,
                RelatedEntityType = "PartialCreditNoteBridgeReconciliationFailed",
                Message = message,
            }, ct);
        }
    }

    /// <summary>
    /// Truncado defensivo del mensaje de error a 2000 chars para que entre en
    /// la columna <c>varchar(2000)</c>. Si el mensaje es null usamos texto
    /// generico — la columna en BD igual permite null, pero queremos que el
    /// admin vea ALGO en la UI sin tener que ir a los logs.
    /// </summary>
    private static string TruncateErrorMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return "(sin mensaje de error)";

        return message.Length <= 2000 ? message : message.Substring(0, 2000);
    }
}
