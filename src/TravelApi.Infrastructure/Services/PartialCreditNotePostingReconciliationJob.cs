using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// FC1.3.F2.6a (plan tactico FC1.3 Fase 2 §FC1.3.F2.6a, rehecho 2026-05-28): job recurrente
/// que detecta Notas de Credito (NC) PARCIALES colgadas en <c>Resultado='PENDING'</c> y las
/// reconcilia contra ARCA.
///
/// <para>
/// <b>Por que existe (el "barrendero" de NC parciales trabadas)</b>: la emision de una NC
/// parcial es asincrona. <c>InvoiceService.ProcessPartialCreditNoteJob</c> crea la NC en
/// PENDING, POSTea a ARCA y persiste el resultado (A / R). Si el proceso se cae ENTRE el POST
/// y la persistencia del resultado (crash, timeout, host reiniciado), la NC queda en PENDING
/// para siempre: el bloqueo fiscal de la factura origen no se levanta y el BookingCancellation
/// (BC) asociado nunca avanza. Este job corre cada 30 min y reconcilia.
/// </para>
///
/// <para>
/// <b>QUE CAMBIO en el rehacer (revision Changes Required)</b>: la version anterior tenia tres
/// bugs de fondo. Este job ahora DELEGA la decision fiscal en
/// <see cref="IInvoiceService.ReconcileStuckPartialCreditNoteAsync"/>, que reutiliza el MISMO
/// arbitro de idempotencia que el emisor (stale-key recovery de F2.2). Eso arregla:
/// <list type="bullet">
///   <item><b>B-1 (era codigo muerto)</b>: el arbitro consulta ARCA con el
///   <c>LastSeenNumeroBeforePost</c> REAL guardado en la <c>ArcaIdempotencyKey</c>, no con
///   <c>null</c>. Con null, <c>QueryLastAuthorizedWithDetailsAsync</c> devuelve SIEMPRE
///   <c>Found:false</c> (verificado en <c>AfipService.cs:1861</c>) y la reconciliacion no
///   reconciliaba nada — solo notificaba a los 10 dias.</item>
///   <item><b>B-2 (confirmaba con el CAE equivocado)</b>: ya no se matchea "por monto + factura
///   origen"; el arbitro matchea por COMPROBANTE ASOCIADO especifico. Dos NC del mismo monto
///   sobre la misma factura ya no se confunden.</item>
///   <item><b>M-1 (pisaba al emisor en vuelo)</b>: el arbitro lee la idempotency key; si esta
///   activa (no vencida), devuelve <c>InFlight</c> y el job NO toca nada.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Que sigue siendo responsabilidad del job (no del service)</b>: la capa de AGENDA.
/// <list type="number">
///   <item><b>Rate-limit por NC</b> (anti-spam de consultas a ARCA): usamos la columna EXISTENTE
///   <see cref="Invoice.LastArcaAttemptAt"/> para no reconsultar la misma NC en cada corrida.
///   Sin columna nueva (decision de diseno: no tocar el schema de una tabla fiscal central).</item>
///   <item><b>Escalado a revision manual</b>: si la NC supera la ventana de gracia
///   (<see cref="OperationalFinanceSettings.ManualReviewMaxDaysBeforeRg4540Alert"/>) y sigue sin
///   resolverse, notificamos a los admins UNA VEZ POR DIA (dedup intra-dia sobre
///   <c>Notifications</c>).</item>
///   <item><b>M-2 (no dejar BC huerfano silencioso)</b>: cuando el service confirma una NC pero
///   el callback al BC FALLA, el service propaga la excepcion (no la traga). El job la captura
///   aca: loguea CRITICO + metric, NO marca la NC como reconciliada exitosa, y la deja para el
///   proximo ciclo (donde la NC sigue PENDING y se re-detecta). Asi un fallo del bridge nunca
///   queda invisible.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Patron</b>: igual que <see cref="PartialCreditNoteBridgeReconciliationJob"/> — service
/// Scoped, registrado como recurring Hangfire en Program.cs (cron <c>*/30 * * * *</c>); no-op si
/// <see cref="OperationalFinanceSettings.EnablePartialCreditNotes"/> esta apagado; el filtro de
/// antiguedad corre EN LA QUERY (no en la cron) para ajustar la ventana de gracia sin redeployar.
/// </para>
/// </summary>
public class PartialCreditNotePostingReconciliationJob
{
    private readonly AppDbContext _dbContext;
    private readonly IOperationalFinanceSettingsService _operationalFinanceSettingsService;
    private readonly IInvoiceService _invoiceService;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<PartialCreditNotePostingReconciliationJob> _logger;

    public PartialCreditNotePostingReconciliationJob(
        AppDbContext dbContext,
        IOperationalFinanceSettingsService operationalFinanceSettingsService,
        IInvoiceService invoiceService,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        ILogger<PartialCreditNotePostingReconciliationJob> logger)
    {
        _dbContext = dbContext;
        _operationalFinanceSettingsService = operationalFinanceSettingsService;
        _invoiceService = invoiceService;
        _notificationService = notificationService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Una pasada del job. Hangfire lo invoca via la cron registrada en Program.cs; tambien se
    /// puede invocar manualmente (admin script o tests).
    /// </summary>
    /// <remarks>
    /// Algoritmo:
    /// <list type="number">
    ///   <item>Si <c>EnablePartialCreditNotes=false</c> -> no-op (modulo apagado).</item>
    ///   <item>Traemos las NC parciales en <c>Resultado='PENDING'</c> mas viejas que la ventana
    ///   de staleness. El discriminador de "parcial" (monto NC &lt; monto factura origen) corre
    ///   en la query.</item>
    ///   <item>Para cada NC: rate-limit por <see cref="Invoice.LastArcaAttemptAt"/>. Si toca,
    ///   delegamos en <see cref="IInvoiceService.ReconcileStuckPartialCreditNoteAsync"/> y
    ///   reaccionamos al resultado (escalar a manual solo si sigue sin resolverse).</item>
    /// </list>
    /// </remarks>
    public async Task RunAsync(CancellationToken ct = default)
    {
        var settings = await _operationalFinanceSettingsService.GetEntityAsync(ct);

        // Modulo apagado: no hay NC parciales nuevas. No corremos para no generar trafico a ARCA
        // ni ruido en logs cuando alguien apaga el modulo por crisis.
        if (!settings.EnablePartialCreditNotes)
        {
            _logger.LogDebug(
                "PartialCreditNotePostingReconciliationJob: EnablePartialCreditNotes=false, skip.");
            return;
        }

        // Ventana de staleness: una NC PENDING solo se considera "colgada" pasados N minutos
        // desde su creacion. Reusamos IdempotencyKeyStaleThresholdMinutes (el mismo umbral que el
        // stale-key recovery del emisor). Defensive: minimo 1 min.
        var stalenessMinutes = Math.Max(settings.IdempotencyKeyStaleThresholdMinutes, 1);
        var stalenessThreshold = DateTime.UtcNow.AddMinutes(-stalenessMinutes);

        // Candidatas: NC (OriginalInvoiceId != null) en PENDING, creadas hace mas que la ventana,
        // y PARCIALES (ImporteTotal < ImporteTotal de la factura origen). Include(OriginalInvoice)
        // y Reserva para los datos de la notificacion. Limitamos a 200 por pasada.
        var candidates = await _dbContext.Invoices
            .Include(nc => nc.OriginalInvoice)
            .Include(nc => nc.Reserva)
            .Where(nc => nc.OriginalInvoiceId != null
                      && nc.Resultado == "PENDING"
                      && nc.CreatedAt < stalenessThreshold
                      // Discriminador NC parcial vs NC total: monto NC < monto factura origen.
                      && nc.OriginalInvoice != null
                      && nc.ImporteTotal < nc.OriginalInvoice.ImporteTotal)
            .OrderBy(nc => nc.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        if (candidates.Count == 0)
        {
            _logger.LogDebug(
                "PartialCreditNotePostingReconciliationJob: ninguna NC parcial colgada (threshold={Threshold:o}, stalenessMinutes={Stale}).",
                stalenessThreshold, stalenessMinutes);
            return;
        }

        foreach (var creditNote in candidates)
        {
            // Cancelacion cooperativa: si el host se esta apagando, salimos limpio. Las NC que
            // quedan se retoman en la proxima pasada (no perdimos estado).
            ct.ThrowIfCancellationRequested();

            await ReconcileSingleCreditNoteAsync(creditNote, settings, ct);
        }
    }

    /// <summary>
    /// Reconcilia UNA NC parcial colgada. Aislado en su propio metodo para mantener
    /// <see cref="RunAsync"/> legible y para que un fallo con una NC no aborte el resto de la
    /// pasada (cada NC se procesa en su propio try/catch).
    /// </summary>
    private async Task ReconcileSingleCreditNoteAsync(
        Invoice creditNote,
        OperationalFinanceSettings settings,
        CancellationToken ct)
    {
        // Rate-limit por NC: no consultamos ARCA en cada corrida para la misma NC. Si ya
        // intentamos hace menos que la ventana de staleness, esperamos al proximo ciclo.
        // LastArcaAttemptAt es una columna EXISTENTE (no agregamos schema). Null = nunca se
        // intento -> reconciliamos.
        var minMinutesBetweenAttempts = Math.Max(settings.IdempotencyKeyStaleThresholdMinutes, 1);
        if (creditNote.LastArcaAttemptAt.HasValue
            && creditNote.LastArcaAttemptAt.Value > DateTime.UtcNow.AddMinutes(-minMinutesBetweenAttempts))
        {
            _logger.LogDebug(
                "PartialCreditNotePostingReconciliationJob: NC {CreditNotePublicId} reconciliada hace poco ({LastAttempt:o}). Espera el proximo ciclo.",
                creditNote.PublicId, creditNote.LastArcaAttemptAt);
            return;
        }

        try
        {
            // Marcamos el intento ANTES de reconciliar: aunque el proceso se caiga durante la
            // consulta a ARCA, el timestamp queda y el rate-limit funciona en la proxima pasada.
            creditNote.LastArcaAttemptAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync(ct);

            // Delegamos la decision fiscal en el service (reutiliza el arbitro de idempotencia del
            // emisor). El job NO consulta ARCA por su cuenta ni matchea montos: eso vive en un
            // unico lugar (InvoiceService), que fue lo que arreglo B-1 / B-2 / M-1.
            var result = await _invoiceService.ReconcileStuckPartialCreditNoteAsync(creditNote.Id, ct);

            await HandleReconcileResultAsync(creditNote, settings, result, ct);
        }
        catch (Exception ex)
        {
            // M-2 (fix): si el service confirmo la NC pero el callback al BookingCancellation
            // fallo, el service PROPAGA la excepcion (no la traga). Aca la registramos como
            // CRITICO + metric y NO la re-lanzamos: el job no re-POSTea a ARCA en su catch, asi
            // que no hay riesgo de doble-POST. La NC quedo en el estado que dejo el service (si se
            // confirmo, ya esta 'A' y la factura origen anulada; el BC es lo que falto sincronizar)
            // y el job de reconciliacion del bridge (PartialCreditNoteBridgeReconciliationJob) +
            // el proximo ciclo de ESTE job la re-detectan. Un fallo de red consultando ARCA cae
            // aca tambien: se reintenta el proximo ciclo (LastArcaAttemptAt ya quedo seteado).
            _logger.LogError(
                ex,
                "PartialCreditNotePostingReconciliationJob: fallo reconciliando NC {CreditNotePublicId} " +
                "(OriginalInvoiceId={OriginalInvoiceId}). Se reintenta en el proximo ciclo. Si la NC quedo " +
                "aprobada pero el BC no avanzo, la sincronizacion del BC se recupera por el bridge reconciliation job.",
                creditNote.PublicId, creditNote.OriginalInvoiceId);
            _logger.LogError(
                "metric:bc_bridge_failed | creditNoteInvoiceId={CreditNoteInvoiceId} originatingInvoiceId={OriginatingInvoiceId} errorType={ErrorType} stage=PostingReconciliationJob",
                creditNote.Id, creditNote.OriginalInvoiceId, ex.GetType().Name);
        }
    }

    /// <summary>
    /// Reacciona al desenlace de la reconciliacion. Solo el caso "sigue sin resolverse" puede
    /// escalar a revision manual (notificar admins). Los demas casos son terminales o transitorios
    /// y no necesitan accion del job mas alla de loguear.
    /// </summary>
    private async Task HandleReconcileResultAsync(
        Invoice creditNote,
        OperationalFinanceSettings settings,
        PartialCreditNotePostingReconcileResult result,
        CancellationToken ct)
    {
        switch (result.Outcome)
        {
            case PartialCreditNotePostingReconcileOutcome.Confirmed:
                // ARCA confirmo la NC: el service la marco 'A', anulo la factura origen y
                // sincronizo el BC. Nada mas que hacer.
                _logger.LogWarning(
                    "PartialCreditNotePostingReconciliationJob: NC {CreditNotePublicId} reconciliada como EMITIDA en ARCA. {Detail}",
                    creditNote.PublicId, result.Detail);
                return;

            case PartialCreditNotePostingReconcileOutcome.InFlight:
                // El emisor original esta posteando AHORA. No tocamos nada (M-1). Proximo ciclo.
                _logger.LogDebug(
                    "PartialCreditNotePostingReconciliationJob: NC {CreditNotePublicId} con emisor en vuelo. {Detail}",
                    creditNote.PublicId, result.Detail);
                return;

            case PartialCreditNotePostingReconcileOutcome.ReEnqueuedEmission:
                // ARCA no confirma la NC: el service re-disparo la emision idempotente. No
                // escalamos a manual: la NC esta en camino de re-emitirse.
                _logger.LogWarning(
                    "PartialCreditNotePostingReconciliationJob: NC {CreditNotePublicId} re-disparada (emision idempotente). {Detail}",
                    creditNote.PublicId, result.Detail);
                return;

            case PartialCreditNotePostingReconcileOutcome.NeedsManualReview:
                // No se pudo reconciliar de forma segura. Si ademas supera la ventana de gracia,
                // escalamos a revision manual (notificacion 1 vez/dia).
                await EscalateToManualReviewIfStaleAsync(creditNote, settings, result, ct);
                return;
        }
    }

    /// <summary>
    /// La NC no se pudo reconciliar automaticamente. Si lleva mas dias colgada que el umbral de
    /// gracia, notificamos a los admins (1 vez/dia) para intervencion manual. Dentro de la
    /// ventana de gracia no escalamos: el proximo ciclo puede resolverla.
    /// </summary>
    private async Task EscalateToManualReviewIfStaleAsync(
        Invoice creditNote,
        OperationalFinanceSettings settings,
        PartialCreditNotePostingReconcileResult result,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "PartialCreditNotePostingReconciliationJob: NC {CreditNotePublicId} requiere revision manual. {Detail}",
            creditNote.PublicId, result.Detail);

        // Umbral de gracia para escalar. Reusamos el setting del review alert (mismo significado
        // conceptual: "hace cuanto esta colgado esto"). Defensive: minimo 1 dia.
        var maxDaysBeforeAlert = Math.Max(settings.ManualReviewMaxDaysBeforeRg4540Alert, 1);
        var ageDays = (DateTime.UtcNow - creditNote.CreatedAt).TotalDays;

        // Todavia dentro de la ventana de gracia: no escalamos, esperamos al proximo ciclo.
        if (ageDays < maxDaysBeforeAlert)
        {
            return;
        }

        await NotifyAdminsOfStuckCreditNoteAsync(creditNote, (int)ageDays, ct);
    }

    /// <summary>
    /// Notifica a los admins (1 vez por dia, dedup intra-dia) que una NC parcial lleva demasiado
    /// tiempo colgada en PENDING y necesita intervencion manual. Mismo patron de dedup que
    /// <see cref="PartialCreditNoteReviewAlertJob"/> (por UserId + RelatedEntityId +
    /// RelatedEntityType + fecha de hoy).
    /// </summary>
    private async Task NotifyAdminsOfStuckCreditNoteAsync(
        Invoice creditNote,
        int ageDays,
        CancellationToken ct)
    {
        var adminUsers = await _userManager.GetUsersInRoleAsync("Admin");
        if (adminUsers.Count == 0)
        {
            _logger.LogWarning(
                "PartialCreditNotePostingReconciliationJob: NC {CreditNotePublicId} colgada hace {AgeDays} dia(s) pero NO hay usuarios en rol Admin para notificar.",
                creditNote.PublicId, ageDays);
            return;
        }

        // Etiqueta canonica del numero de factura origen: "PV-NNNNN" (igual que el alert job).
        var originalInvoice = creditNote.OriginalInvoice;
        var originalInvoiceLabel = originalInvoice is not null
            ? $"{originalInvoice.PuntoDeVenta:D5}-{originalInvoice.NumeroComprobante:D8}"
            : "s/factura-origen";
        var reservaNumero = creditNote.Reserva?.NumeroReserva
            ?? creditNote.ReservaId?.ToString()
            ?? "s/n";

        var message =
            $"La nota de credito parcial de la reserva {reservaNumero} (sobre factura {originalInvoiceLabel}) " +
            $"quedo trabada en estado pendiente hace {ageDays} dia(s). " +
            "ARCA no confirma que se haya emitido y la reconciliacion automatica no pudo resolverla. " +
            "Requiere revision manual: verificar el comprobante en ARCA y reconciliar o re-emitir desde back-office.";

        // D5 (2026-07-05): dedup por AVISO VIVO con la misma clave ("PartialCreditNotePostingStuck:{ncId}"), no por
        // "creado hoy". El job corre cada 30 min y la NC puede quedar colgada dias: antes eso re-creaba el aviso cada
        // dia (spam). Ahora solo se re-crea si el anterior ya se atendio/resolvio y la NC sigue trabada.
        var resolutionKey = NotificationResolutionKeys.ForEntity("PartialCreditNotePostingStuck", creditNote.Id);

        foreach (var admin in adminUsers)
        {
            var hasLiveAlert = await _dbContext.Notifications.AnyAsync(n =>
                n.UserId == admin.Id
                && n.ResolutionKey == resolutionKey
                && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed, ct);

            if (hasLiveAlert)
                continue;

            await _notificationService.CreateAndSendAsync(new Notification
            {
                UserId = admin.Id,
                Type = "Error",
                Priority = "Urgent",
                RelatedEntityId = creditNote.Id,
                RelatedEntityType = "PartialCreditNotePostingStuck",
                Message = message,
            }, ct);
        }
    }
}
