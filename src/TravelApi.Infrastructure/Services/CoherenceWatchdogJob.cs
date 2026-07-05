using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// (Tanda 4, 2026-07-04) "Vigía de coherencia": job nocturno que barre la base buscando combinaciones de datos
/// incoherentes ANTES de que el dueño las pise, auto-repara SOLO lo trivialmente seguro y reporta el resto con UNA
/// notificación urgente por corrida.
///
/// <para><b>Por qué existe</b>: el saneamiento de las tandas anteriores cerró los huecos que CREABAN datos
/// incoherentes (marcas colgadas, plata desactualizada, anuladas con servicios vivos). Este job es la red de
/// seguridad que los CAZA si algún camino viejo o no cubierto los vuelve a producir: en vez de que el dueño
/// descubra una "deuda fantasma" mirando una pantalla, el sistema lo detecta de noche y —cuando es seguro— lo
/// arregla solo.</para>
///
/// <para><b>Qué repara solo y qué reporta</b> (ver <see cref="CoherenceChecks"/>):
///   - W1 (marca de revisión colgada en estado terminal) y W3 (proyección de plata desactualizada) son datos
///     DERIVADOS que se recalculan solos → se AUTO-REPARAN.
///   - W2 (anulada con servicios vivos) y W5 (anulada con deuda sin Nota de Débito) tocan plata/multa real → se
///     REPORTAN para que decida una persona; el vigía nunca inventa ni borra plata.</para>
///
/// <para><b>Orden de ejecución</b> (importa): PRIMERO se repara, DESPUÉS se evalúa lo reportable. Así W3 no reporta
/// lo que otro paso ya arregló, y W5 evalúa sobre la plata FRESCA (una "deuda" que era solo proyección vieja ya se
/// recalculó y no dispara alarma). La secuencia es: W1 → W3 → recalculador de anuladas → W2 → W5.</para>
///
/// <para><b>Concurrencia</b>: <c>[DisableConcurrentExecution]</c> es el guard de Hangfire contra dos corridas de
/// ESTE job solapadas (programada + reintento). Los checks son idempotentes (una reserva ya sana deja de ser
/// candidata), así que una eventual superposición no duplica reparaciones ni notificaciones.</para>
/// </summary>
public class CoherenceWatchdogJob
{
    private readonly AppDbContext _db;
    private readonly CoherenceMoneyRecalculator _moneyRecalculator;
    private readonly INotificationService _notificationService;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly ILogger<CoherenceWatchdogJob> _logger;

    // Tipo de notificación del vigía (RelatedEntityType del aviso agregado, sin entidad puntual).
    private const string WatchdogNotificationType = "CoherenceWatchdogReport";

    // (Tanda 5) Clave de resolución ESTABLE del aviso del vigía. No lleva id de entidad (el hallazgo es agregado):
    // sin una clave fija, cada corrida con conteos distintos creaba un aviso nuevo y se acumulaban. Con una clave
    // fija hay a lo sumo UN aviso vivo del vigía por admin: si el mensaje cambia, se apaga el viejo y se crea el
    // nuevo (así el dueño ve los números actuales, no una pila de resúmenes viejos). Ver NotifyAdminsAsync.
    private const string WatchdogResolutionKey = "CoherenceWatchdog:daily";

    // Guard de concurrencia PROCESS-WIDE (0 = libre, 1 = corriendo). El [DisableConcurrentExecution] de Hangfire solo
    // cubre la vía programada; este flag cubre TAMBIÉN el endpoint manual y el cruce manual-vs-programada, para que
    // dos corridas nunca reescriban la misma plata a la vez. Es static porque el guard debe ser único por proceso,
    // no por instancia scoped del job.
    private static int _isRunning = 0;

    public CoherenceWatchdogJob(
        AppDbContext db,
        CoherenceMoneyRecalculator moneyRecalculator,
        INotificationService notificationService,
        UserManager<ApplicationUser> userManager,
        ILogger<CoherenceWatchdogJob> logger)
    {
        _db = db;
        _moneyRecalculator = moneyRecalculator;
        _notificationService = notificationService;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// Resumen de una corrida del vigía. Números crudos; el controller/log arma el texto de negocio.
    /// </summary>
    /// <param name="AutoRepairedMarks">Reservas terminales con marca colgada que se limpiaron (W1).</param>
    /// <param name="AutoRepairedMoney">Reservas con proyección de plata desactualizada que se recalcularon (W3).</param>
    /// <param name="AutoResolvedNotifications">Avisos zombie (vivos con la causa ya muerta) que se apagaron (W4).</param>
    /// <param name="AnnulledMoneyRecalculated">Reservas anuladas que el recalculador dejó con otro saldo (paso legacy).</param>
    /// <param name="AnnulledWithLiveServices">Reservas anuladas con servicios sin cancelar reportadas (W2).</param>
    /// <param name="AnnulledWithUnjustifiedDebt">Reservas anuladas con deuda sin comprobante reportadas (W5).</param>
    /// <param name="NotificationSent">Si se emitió la notificación urgente a los admins en esta corrida.</param>
    public sealed record CoherenceWatchdogResult(
        int AutoRepairedMarks,
        int AutoRepairedMoney,
        int AutoResolvedNotifications,
        int AnnulledMoneyRecalculated,
        int AnnulledWithLiveServices,
        int AnnulledWithUnjustifiedDebt,
        bool NotificationSent);

    /// <summary>
    /// Envoltorio que invoca Hangfire desde la cron. La cron necesita un método que devuelva <c>Task</c> (no
    /// <c>Task&lt;T&gt;</c>): este descarta el resumen —que solo consumen el endpoint manual y los tests— y corre la
    /// misma pasada. <c>[DisableConcurrentExecution]</c> va acá porque es el método que Hangfire encola; si además una
    /// corrida MANUAL está en curso, el guard interno lanza <see cref="CoherenceWatchdogBusyException"/> y acá se
    /// omite en silencio (no marca el job programado como fallido).
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunScheduledAsync(CancellationToken ct = default)
    {
        try
        {
            await RunAsync(ct);
        }
        catch (CoherenceWatchdogBusyException)
        {
            _logger.LogInformation(
                "CoherenceWatchdog: ya hay una corrida en curso (manual), se omite la corrida programada de esta vez.");
        }
    }

    /// <summary>
    /// Ejecuta una pasada del vigía y devuelve el resumen. La invocan el envoltorio de Hangfire
    /// (<see cref="RunScheduledAsync"/>), el endpoint admin de corrida manual y los tests. Repara lo seguro, evalúa
    /// lo reportable sobre datos ya frescos y, si hay algo para una persona, emite UNA notificación urgente dedupeada.
    ///
    /// <para>Lanza <see cref="CoherenceWatchdogBusyException"/> si ya hay otra corrida en curso (guard de
    /// concurrencia process-wide): así una corrida manual y la programada nunca reescriben la misma plata a la vez.</para>
    /// </summary>
    public async Task<CoherenceWatchdogResult> RunAsync(CancellationToken ct = default)
    {
        // Guard de concurrencia: tomar el flag de forma atómica. Si ya estaba en 1, hay otra corrida → rechazar.
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            throw new CoherenceWatchdogBusyException();

        try
        {
            return await RunCoreAsync(ct);
        }
        finally
        {
            // Liberar SIEMPRE el guard, incluso si el barrido falló, para no dejar el vigía trabado hasta reiniciar.
            Interlocked.Exchange(ref _isRunning, 0);
        }
    }

    /// <summary>Cuerpo real de una pasada (ya dentro del guard de concurrencia). Ver <see cref="RunAsync"/>.</summary>
    private async Task<CoherenceWatchdogResult> RunCoreAsync(CancellationToken ct)
    {
        // ---- Fase de reparación (primero, para no reportar lo que se arregla solo) ----

        // W1: marcas de revisión colgadas en estados terminales. Muta el tracker; se persiste acá.
        var markFindings = await CoherenceChecks.RepairTerminalMarksAsync(_db, ct);
        if (markFindings.Count > 0)
            await _db.SaveChangesAsync(ct);
        // Higiene de tracking antes del próximo paso (los recálculos recargan su propio grafo trackeado).
        _db.ChangeTracker.Clear();

        // Paso 2 legacy (Tanda 3): recalcular la plata de las anuladas con los persisters canónicos, INCLUIDA la
        // deuda del OPERADOR (que W3 no toca). Reutilización directa del recalculador.
        //
        // ORDEN CRÍTICO — va ANTES de W3 (corrección del backend reviewer): el recalculador elige sus candidatas
        // mirando la señal de plata del CLIENTE (Balance/ConfirmedSale != 0). Si W3 corriera primero y dejara esa
        // señal en 0, una anulada legacy con plata de cliente ya saldada pero DEUDA DE OPERADOR vieja se caería del
        // set de candidatas y su SupplierDebtPersister nunca correría → deuda fantasma del operador sobreviviría.
        // Corriendo el recalculador primero, ve la señal original y procesa completo (cliente + operador). Además,
        // así W5 (más abajo) evalúa "deuda sin justificación" sobre el saldo ya fresco.
        var annulledMoneyResult = await _moneyRecalculator.RecalculateAnnulledReservasMoneyAsync(ct);
        _db.ChangeTracker.Clear();

        // W3: proyección de plata desactualizada del RESTO (reservas no anuladas, o cualquier stale que el paso
        // anterior no cubriera). Cada reserva se reescribe por el escritor canónico, que guarda por su cuenta; el
        // método aísla el fallo por reserva y limpia el tracker entre reservas.
        var moneyFindings = await CoherenceChecks.RepairStaleMoneyProjectionAsync(_db, _logger, ct);
        _db.ChangeTracker.Clear();

        // W4: avisos zombie (vivos con la causa ya muerta). Va DESPUÉS de W1/W3/recalculador porque esas reparaciones
        // pueden haber matado la causa de un aviso (ej. W1 bajó la marca "confirmada con cambios", el recálculo dejó
        // la reserva saldada) — así en la misma corrida el aviso derivado también se apaga. Muta el tracker; se
        // persiste acá.
        var zombieNotificationFindings = await CoherenceChecks.ResolveZombieNotificationsAsync(_db, ct);
        if (zombieNotificationFindings.Count > 0)
            await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        // ---- Fase de reporte (sobre datos ya reparados) ----

        var liveServiceFindings = await CoherenceChecks.DetectAnnulledWithLiveServicesAsync(_db, ct);
        var unjustifiedDebtFindings = await CoherenceChecks.DetectAnnulledInconsistentDebtAsync(_db, ct);

        var reportable = liveServiceFindings.Concat(unjustifiedDebtFindings).ToList();

        // El detalle técnico de cada hallazgo va SOLO al log del servidor (nunca a la notificación del usuario).
        LogFindings(markFindings, moneyFindings, zombieNotificationFindings, liveServiceFindings, unjustifiedDebtFindings);

        var notificationSent = false;
        if (reportable.Count > 0)
        {
            notificationSent = await NotifyAdminsAsync(
                liveServiceFindings.Count, unjustifiedDebtFindings.Count, ct);
        }
        else
        {
            _logger.LogInformation(
                "CoherenceWatchdog: corrida sin hallazgos para revisar (W1 reparadas={W1}, W3 reparadas={W3}, " +
                "avisos apagados={W4}, anuladas recalculadas={Recalc}). Sin notificación.",
                markFindings.Count, moneyFindings.Count, zombieNotificationFindings.Count, annulledMoneyResult.Corrected);
        }

        return new CoherenceWatchdogResult(
            AutoRepairedMarks: markFindings.Count,
            AutoRepairedMoney: moneyFindings.Count,
            AutoResolvedNotifications: zombieNotificationFindings.Count,
            AnnulledMoneyRecalculated: annulledMoneyResult.Corrected,
            AnnulledWithLiveServices: liveServiceFindings.Count,
            AnnulledWithUnjustifiedDebt: unjustifiedDebtFindings.Count,
            NotificationSent: notificationSent);
    }

    /// <summary>
    /// Emite UNA notificación urgente por admin con el resumen de negocio (sin ids ni jerga técnica), dedupeada:
    /// si ese admin ya tiene una notificación NO leída del vigía con el mismo mensaje, no se crea otra. Devuelve
    /// true si se creó al menos una notificación nueva.
    /// </summary>
    private async Task<bool> NotifyAdminsAsync(int liveServicesCount, int unjustifiedDebtCount, CancellationToken ct)
    {
        var message = BuildBusinessSummary(liveServicesCount, unjustifiedDebtCount);

        var admins = await _userManager.GetUsersInRoleAsync("Admin");
        if (admins.Count == 0)
        {
            _logger.LogWarning(
                "CoherenceWatchdog: hay {Count} reserva(s) anulada(s) para revisar pero NO hay usuarios Admin a quién avisar.",
                liveServicesCount + unjustifiedDebtCount);
            return false;
        }

        var createdAny = false;
        foreach (var admin in admins)
        {
            // Aviso VIVO del vigía para este admin (a lo sumo uno, por la clave estable).
            var liveForAdmin = await _db.Notifications
                .Where(n => n.UserId == admin.Id
                            && n.ResolutionKey == WatchdogResolutionKey
                            && n.ResolvedAt == null && !n.IsRead && !n.IsDismissed)
                .ToListAsync(ct);

            // Misma situación aún sin atender (mismo mensaje) → no repetir.
            if (liveForAdmin.Any(n => n.Message == message))
                continue;

            // Situación cambió (otros conteos) o el admin ya vio el anterior: apagamos cualquier aviso vivo del vigía
            // que haya quedado con números viejos y creamos el nuevo. Así nunca se acumulan resúmenes desactualizados.
            if (liveForAdmin.Count > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var stale in liveForAdmin)
                    stale.ResolvedAt = now;
                await _db.SaveChangesAsync(ct);
            }

            await _notificationService.CreateAndSendAsync(new Notification
            {
                UserId = admin.Id,
                Type = "Warning",
                Priority = "Urgent",
                RelatedEntityType = WatchdogNotificationType,
                // Sin RelatedEntityId: el hallazgo es agregado (varias reservas), no una entidad puntual. La clave de
                // resolución es fija (no derivable de la entidad), por eso se setea a mano.
                ResolutionKey = WatchdogResolutionKey,
                Message = message,
            }, ct);

            createdAny = true;
        }

        return createdAny;
    }

    /// <summary>
    /// Arma el resumen en castellano de negocio que ve el admin. Solo menciona las categorías que realmente tienen
    /// hallazgos. NO expone nada técnico (ids, estados crudos, nombres internos).
    /// </summary>
    private static string BuildBusinessSummary(int liveServicesCount, int unjustifiedDebtCount)
    {
        var parts = new List<string>();
        if (liveServicesCount > 0)
            parts.Add($"{liveServicesCount} con servicios sin cancelar");
        if (unjustifiedDebtCount > 0)
            parts.Add($"{unjustifiedDebtCount} con una deuda sin comprobante que la justifique");

        var total = liveServicesCount + unjustifiedDebtCount;

        // Concordancia singular/plural: con una sola reserva el texto va en singular ("reserva anulada" / "Revisala").
        var reservaWord = total == 1 ? "reserva anulada" : "reservas anuladas";
        var closing = total == 1 ? "Revisala cuando puedas." : "Revisalas cuando puedas.";

        return $"El chequeo nocturno encontró {total} {reservaWord} con datos para revisar: " +
               string.Join(" y ", parts) + ". " + closing;
    }

    /// <summary>Vuelca el detalle técnico de cada hallazgo al log del servidor (nunca al usuario).</summary>
    private void LogFindings(
        IReadOnlyList<CoherenceFinding> markFindings,
        IReadOnlyList<CoherenceFinding> moneyFindings,
        IReadOnlyList<CoherenceFinding> zombieNotificationFindings,
        IReadOnlyList<CoherenceFinding> liveServiceFindings,
        IReadOnlyList<CoherenceFinding> unjustifiedDebtFindings)
    {
        foreach (var finding in markFindings.Concat(moneyFindings).Concat(zombieNotificationFindings))
        {
            _logger.LogInformation(
                "CoherenceWatchdog auto-reparó [{Code}] {EntityType} {EntityId}: {Detail}",
                finding.Code, finding.EntityType, finding.EntityId, finding.Detail);
        }

        foreach (var finding in liveServiceFindings.Concat(unjustifiedDebtFindings))
        {
            _logger.LogWarning(
                "CoherenceWatchdog reporta para revisión [{Code}] {EntityType} {EntityId}: {Detail}",
                finding.Code, finding.EntityType, finding.EntityId, finding.Detail);
        }
    }
}

/// <summary>
/// Se lanza cuando se pide correr el vigía mientras YA hay otra corrida en curso (guard de concurrencia
/// process-wide). El endpoint manual la traduce a un 409 con mensaje de negocio; la vía programada de Hangfire la
/// omite en silencio. No lleva detalle técnico: no hay nada sensible que transportar.
/// </summary>
public sealed class CoherenceWatchdogBusyException : Exception
{
    public CoherenceWatchdogBusyException()
        : base("Ya hay un chequeo de consistencia en curso.")
    {
    }
}
