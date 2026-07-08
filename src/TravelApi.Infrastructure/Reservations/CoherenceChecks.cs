using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Notifications;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// Un hallazgo del vigía de coherencia: una combinación de datos que no debería existir. Es un value object plano
/// (sin EF): describe QUÉ está mal, sobre qué entidad, con un detalle interno (para el log del servidor) y si el
/// vigía ya lo reparó solo o quedó para que lo mire una persona.
/// </summary>
/// <param name="Code">Código corto del check que lo encontró (W1, W2, W3, W5). Para agrupar en el log/resumen.</param>
/// <param name="EntityType">Tipo de entidad afectada (por ahora siempre "Reserva"). Deja lugar a checks futuros.</param>
/// <param name="EntityId">Id interno de la entidad. NUNCA se muestra al usuario: solo para el log/diagnóstico.</param>
/// <param name="Detail">
/// Detalle técnico del hallazgo (qué marca estaba viva, qué número difería). Va SOLO al log del servidor; nunca a la
/// notificación que ve el usuario (esa lleva un resumen de negocio agregado, sin ids ni jerga).
/// </param>
/// <param name="AutoRepaired">
/// true si el vigía ya lo dejó sano en esta misma corrida (W1/W3). false si solo lo reporta porque tocar la plata o
/// la multa lo decide una persona (W2/W5).
/// </param>
/// <param name="DisplayReference">
/// Número de negocio de la entidad, apto para MOSTRARLE al usuario (ej. el número de reserva "F-2026-1025"). Es lo
/// ÚNICO de este record que puede aparecer en la notificación: a diferencia de <paramref name="EntityId"/> (interno,
/// nunca se muestra), este identifica la reserva en el lenguaje que el dueño ve en pantalla. Queda en null en los
/// hallazgos que no son de una reserva (ej. W4 sobre notificaciones): esos no van al aviso de negocio.
/// </param>
public sealed record CoherenceFinding(
    string Code, string EntityType, int EntityId, string Detail, bool AutoRepaired, string? DisplayReference = null);

/// <summary>
/// Detectores del "vigía de coherencia" (Tanda 4): funciones que barren la base buscando combinaciones de datos
/// incoherentes que, si nadie las ve, terminan confundiendo al dueño (deuda fantasma, anuladas con servicios vivos,
/// cuentas desactualizadas). Cada check devuelve una lista de <see cref="CoherenceFinding"/>.
///
/// <para><b>Filosofía</b>: se AUTO-REPARA únicamente lo trivialmente seguro (marcas de revisión colgadas, proyección
/// de plata desactualizada — ambas cosas derivadas que se recalculan solas). Todo lo que toca plata real o la multa
/// del cliente se REPORTA para que decida una persona; el vigía nunca inventa ni borra plata.</para>
///
/// <para><b>Reutilización, no duplicación</b>: la limpieza de marcas usa la MISMA tabla declarativa que las
/// transiciones normales (<see cref="ReservaStatusTransitioner"/> + <see cref="ReservaStateCleanupRules"/>); el
/// "servicio vivo" usa el MISMO helper que el cálculo de plata (<see cref="ServiceResolutionRules.IsCancelled(FlightSegment)"/>
/// y sus sobrecargas); la plata se reescribe por el ÚNICO escritor canónico
/// (<see cref="ReservaMoneyPersister"/>); y "anulada con deuda sin justificación" sale de la regla de dominio
/// (<see cref="ReservationDebtRules.DeriveForCancelled"/>). Así el vigía nunca aplica un criterio distinto al del
/// resto del sistema.</para>
///
/// <para><b>Orquestación (SaveChanges, orden)</b>: esta clase NO decide el orden ni cierra transacciones — eso lo
/// hace <c>CoherenceWatchdogJob</c>. Los métodos que MUTAN (W1) dejan los cambios en el ChangeTracker sin guardar
/// (el job hace el <c>SaveChanges</c>); el de plata (W3) delega en <see cref="ReservaMoneyPersister.PersistAsync"/>,
/// que guarda por su cuenta cada reserva. Los que solo REPORTAN (W2, W5) leen sin tracking.</para>
///
/// <para><b>W4 (notificaciones zombies) NO va en esta tanda</b>: llega con la Tanda 5, cuando el sistema de
/// notificaciones tenga clave de resolución/auto-resolve. Se deja el hueco documentado a propósito.</para>
/// </summary>
public static class CoherenceChecks
{
    // Estados terminales donde ninguna marca de revisión "viva" tiene sentido (W1). Fuente única: EstadoReserva.
    private static readonly string[] TerminalStatuses =
    {
        EstadoReserva.Cancelled,
        EstadoReserva.PendingOperatorRefund,
        EstadoReserva.Closed,
        EstadoReserva.Lost,
    };

    // Estados "anulada" (W2/W5): su plata se resuelve por el circuito de cancelación, no por cobro normal. Mismo
    // criterio que ReservaService.IsCancelledLikeStatus (no se puede reusar: es privado de ese service).
    private static readonly string[] AnnulledStatuses =
    {
        EstadoReserva.Cancelled,
        EstadoReserva.PendingOperatorRefund,
    };

    // Tolerancia de centavo al comparar plata (W3). Un resto de redondeo por conversión de moneda NO es una
    // proyección "stale": sin esta tolerancia el vigía "corregiría" la misma reserva todas las noches por 0,001.
    private const decimal MoneyTolerance = 0.01m;

    // ============================================================================================
    // W1 — TERMINAL CON MARCA VIVA (auto-repara).
    // Una reserva anulada/cerrada/perdida NO debería seguir mostrando el cartel "confirmada con cambios / revisar"
    // ni tener filas de detalle de cambios pendientes: en un terminal ya no hay nada que revisar. Se limpia
    // aplicando la MISMA regla declarativa que usa cualquier transición (no se duplica la lista de flags).
    // ============================================================================================

    /// <summary>
    /// Detecta reservas en estado terminal (Cancelled/PendingOperatorRefund/Closed/Lost) que quedaron con una marca
    /// de revisión viva (<c>HasUnacknowledgedChanges</c>, <c>ChangesPendingSince</c> o filas
    /// <c>ReservaPendingChanges</c>) y las sanea reutilizando <see cref="ReservaStatusTransitioner.ApplyAsync"/>
    /// (un "set al mismo estado" que no genera log pero dispara la limpieza declarativa por estado destino).
    ///
    /// <para><b>No hace SaveChanges</b>: deja las mutaciones en el tracker; el job las persiste. Devuelve un finding
    /// AutoRepaired=true por cada reserva saneada.</para>
    /// </summary>
    public static async Task<IReadOnlyList<CoherenceFinding>> RepairTerminalMarksAsync(
        AppDbContext db, CancellationToken ct)
    {
        // Candidatas por marca en la propia reserva (flag o timestamp colgado).
        var flaggedReservaIds = await db.Reservas
            .Where(r => TerminalStatuses.Contains(r.Status)
                        && (r.HasUnacknowledgedChanges || r.ChangesPendingSince != null))
            .Select(r => r.Id)
            .ToListAsync(ct);

        // Candidatas por filas de detalle huérfanas (aunque el flag ya estuviera apagado). Dos consultas simples,
        // sin subconsulta correlacionada, para que también corran en el provider InMemory de los tests.
        var terminalReservaIds = await db.Reservas
            .Where(r => TerminalStatuses.Contains(r.Status))
            .Select(r => r.Id)
            .ToListAsync(ct);
        var terminalIdSet = terminalReservaIds.ToHashSet();

        var reservaIdsWithPendingRows = await db.ReservaPendingChanges
            .Select(change => change.ReservaId)
            .Distinct()
            .ToListAsync(ct);

        var candidateIds = new HashSet<int>(flaggedReservaIds);
        foreach (var reservaId in reservaIdsWithPendingRows)
        {
            if (terminalIdSet.Contains(reservaId))
                candidateIds.Add(reservaId);
        }

        if (candidateIds.Count == 0)
            return Array.Empty<CoherenceFinding>();

        // Se cargan TRACKED (las vamos a mutar). Volumen chico (solo terminales con marca colgada).
        var reservas = await db.Reservas
            .Where(r => candidateIds.Contains(r.Id))
            .ToListAsync(ct);

        var findings = new List<CoherenceFinding>();
        foreach (var reserva in reservas)
        {
            ct.ThrowIfCancellationRequested();

            // Foto de qué estaba colgado ANTES de limpiar, solo para el detalle del log (no cambia la reparación).
            var hadFlag = reserva.HasUnacknowledgedChanges || reserva.ChangesPendingSince != null;

            // Reutilización directa: "set al mismo estado" → sin log de transición (isRealChange=false) pero corre
            // la limpieza declarativa de ReservaStateCleanupRules para ese estado terminal (apaga la marca + borra
            // las filas de detalle). NO hace SaveChanges: lo hace el job.
            await ReservaStatusTransitioner.ApplyAsync(
                db,
                reserva,
                toStatus: reserva.Status,
                direction: "Correction",
                actorUserId: null,
                actorUserName: "Vigía de coherencia",
                reason: "Saneo de marca de revisión colgada en estado terminal",
                ct: ct);

            var detail = hadFlag
                ? $"Marca 'confirmada con cambios' viva en estado {reserva.Status}; se apagó y se borró el detalle."
                : $"Detalle de cambios pendiente colgado en estado {reserva.Status}; se borró.";

            findings.Add(new CoherenceFinding(
                Code: "W1",
                EntityType: "Reserva",
                EntityId: reserva.Id,
                Detail: detail,
                AutoRepaired: true));
        }

        return findings;
    }

    // ============================================================================================
    // W4 — NOTIFICACIÓN ZOMBIE (auto-resuelve).
    // Un aviso que sigue "vivo" pero cuya causa ya murió (factura anulada OK, reserva saldada, marca de revisión
    // bajada) confundiría al dueño con un problema que ya no existe. Normalmente el aviso se apaga en el acto donde
    // la causa se resuelve; W4 es la red de seguridad si algún camino no lo hizo. Se AUTO-RESUELVE (marca ResolvedAt)
    // porque no toca plata: solo apaga un aviso derivado, igual que W1/W3 recalculan datos derivados.
    // ============================================================================================

    /// <summary>
    /// Detecta avisos VIVOS cuya causa ya murió (según <see cref="NotificationCauseResolutionRules"/>) y los apaga
    /// marcándoles <see cref="Notification.ResolvedAt"/>. Cubre "confirmada con cambios", "sale pronto y debe" y el
    /// error de anulación de factura. NO hace SaveChanges (deja los cambios en el tracker; el job los persiste).
    /// Devuelve un finding AutoRepaired=true por aviso apagado.
    /// </summary>
    public static async Task<IReadOnlyList<CoherenceFinding>> ResolveZombieNotificationsAsync(
        AppDbContext db, CancellationToken ct)
    {
        var zombies = await NotificationCauseResolutionRules.FindZombieNotificationsAsync(db, ct);
        if (zombies.Count == 0)
            return Array.Empty<CoherenceFinding>();

        var now = DateTime.UtcNow;
        var findings = new List<CoherenceFinding>();
        foreach (var notification in zombies)
        {
            notification.ResolvedAt = now;

            findings.Add(new CoherenceFinding(
                Code: "W4",
                EntityType: "Notification",
                EntityId: notification.Id,
                Detail: $"Aviso '{notification.Type}' sobre {notification.RelatedEntityType} " +
                        $"{notification.RelatedEntityId} seguía activo con la causa ya resuelta; se apagó.",
                AutoRepaired: true));
        }

        return findings;
    }

    // ============================================================================================
    // W3 — PROYECCIÓN DE PLATA DESACTUALIZADA (auto-repara).
    // La cuenta de la reserva (escalares + tabla hija por moneda) es una PROYECCIÓN de lo que daría el cálculo puro
    // fresco. Si difieren, algún write-path escribió la plata sin pasar por el persister canónico. Se recalcula por
    // el ÚNICO escritor (ReservaMoneyPersister) y se reporta qué difería (pista para cazar ese write-path).
    // ============================================================================================

    /// <summary>
    /// Detecta reservas NO archivadas cuya proyección de plata (escalares + <c>ReservaMoneyByCurrency</c>) difiere de
    /// lo que daría <see cref="ReservaMoneyCalculator.Calculate"/> fresco, y la corrige con
    /// <see cref="ReservaMoneyPersister.PersistAsync"/> (el escritor canónico, que guarda por su cuenta cada reserva).
    ///
    /// <para>La detección lee sin tracking; la reparación recarga en <see cref="ReservaMoneyPersister"/>. Devuelve un
    /// finding AutoRepaired=true por reserva corregida, con el detalle de qué número difería (valor viejo→nuevo, para
    /// la auditoría del log). Aísla el fallo por reserva: una fila rota se loguea y no aborta la corrida entera.</para>
    /// </summary>
    public static async Task<IReadOnlyList<CoherenceFinding>> RepairStaleMoneyProjectionAsync(
        AppDbContext db, ILogger logger, CancellationToken ct)
    {
        // Barrido acotado a reservas NO archivadas. "Archived" es un estado lateral legacy (soft-delete), no una
        // constante del enum: se compara por literal, igual que el resto del código.
        var reservas = await db.Reservas
            .AsNoTracking()
            .Where(r => r.Status != "Archived")
            .Include(r => r.Payments)
            .Include(r => r.Servicios)
            .Include(r => r.FlightSegments)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .ToListAsync(ct);

        if (reservas.Count == 0)
            return Array.Empty<CoherenceFinding>();

        var reservaIds = reservas.Select(r => r.Id).ToList();

        // Filas hijas por moneda de todas las candidatas, en UNA query, agrupadas por reserva (evita N+1).
        var childRowsByReserva = (await db.ReservaMoneyByCurrency
                .AsNoTracking()
                .Where(row => reservaIds.Contains(row.ReservaId))
                .ToListAsync(ct))
            .GroupBy(row => row.ReservaId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var findings = new List<CoherenceFinding>();

        foreach (var reserva in reservas)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var freshSummary = ReservaMoneyCalculator.Calculate(reserva);
                childRowsByReserva.TryGetValue(reserva.Id, out var childRows);

                if (MoneyProjectionMatches(reserva, freshSummary, childRows ?? new List<ReservaMoneyByCurrency>(), out var diffDetail))
                    continue; // la proyección coincide con el cálculo fresco → nada que corregir.

                // Reparación canónica: reescribe escalares + tabla hija por el único punto de escritura. Guarda por su
                // cuenta (SaveChanges interno). El ChangeTracker se maneja limpio: cargamos la detección AsNoTracking y
                // el persister recarga su propio grafo trackeado (se limpia en el finally).
                await ReservaMoneyPersister.PersistAsync(db, reserva.Id, ct);

                findings.Add(new CoherenceFinding(
                    Code: "W3",
                    EntityType: "Reserva",
                    EntityId: reserva.Id,
                    Detail: $"Proyección de plata desactualizada. {diffDetail} Se recalculó con el escritor canónico.",
                    AutoRepaired: true));
            }
            catch (Exception ex)
            {
                // Aislar el fallo por reserva: una fila con datos rotos NO debe abortar el barrido entero. El detalle
                // técnico queda en el log del servidor con un id seguro (sin datos sensibles); la reserva queda para
                // la próxima corrida.
                logger.LogError(ex,
                    "CoherenceWatchdog W3: no se pudo recalcular la plata de la reserva {ReservaId}", reserva.Id);
            }
            finally
            {
                // Higiene de tracking entre reservas: el persister deja su grafo trackeado; limpiarlo evita que se
                // acumule o interfiera con la siguiente reserva (mismo patrón que CoherenceMoneyRecalculator).
                db.ChangeTracker.Clear();
            }
        }

        return findings;
    }

    /// <summary>
    /// Compara la proyección guardada de una reserva (escalares + filas por moneda) contra el summary fresco. Devuelve
    /// true si coinciden (dentro de la tolerancia de centavo). Si no, arma en <paramref name="diffDetail"/> el detalle
    /// de qué difería CON LOS VALORES viejo→nuevo de cada campo. Es rastro de auditoría (solo va al log del servidor):
    /// permite ver exactamente qué escribió mal un write-path que no pasó por el persister canónico.
    /// </summary>
    private static bool MoneyProjectionMatches(
        Reserva reserva,
        ReservaMoneySummary freshSummary,
        IReadOnlyList<ReservaMoneyByCurrency> childRows,
        out string diffDetail)
    {
        var differences = new List<string>();

        // 1) Escalares de compat de la reserva (viejo→nuevo por cada uno que difiera).
        AddScalarDiff(differences, "venta total", reserva.TotalSale, freshSummary.TotalSale);
        AddScalarDiff(differences, "venta confirmada", reserva.ConfirmedSale, freshSummary.ConfirmedSale);
        AddScalarDiff(differences, "costo", reserva.TotalCost, freshSummary.TotalCost);
        AddScalarDiff(differences, "cobrado", reserva.TotalPaid, freshSummary.TotalPaid);
        AddScalarDiff(differences, "saldo", reserva.Balance, freshSummary.Balance);

        // 2) Filas hijas por moneda: cada moneda del cálculo fresco debe existir con los mismos números, y no debe
        //    sobrar ninguna fila que el cálculo ya no produce (moneda fantasma).
        var childByCurrency = childRows.ToDictionary(row => row.Currency, StringComparer.Ordinal);

        foreach (var (currency, freshLine) in freshSummary.PorMoneda)
        {
            if (!childByCurrency.TryGetValue(currency, out var storedRow))
            {
                differences.Add($"falta la fila por moneda {currency} (nueva: saldo {Fmt(freshLine.Balance)})");
                continue;
            }

            AddScalarDiff(differences, $"[{currency}] venta total", storedRow.TotalSale, freshLine.TotalSale);
            AddScalarDiff(differences, $"[{currency}] venta confirmada", storedRow.ConfirmedSale, freshLine.ConfirmedSale);
            AddScalarDiff(differences, $"[{currency}] costo", storedRow.TotalCost, freshLine.TotalCost);
            AddScalarDiff(differences, $"[{currency}] cobrado", storedRow.TotalPaid, freshLine.TotalPaid);
            AddScalarDiff(differences, $"[{currency}] saldo", storedRow.Balance, freshLine.Balance);
        }

        foreach (var storedCurrency in childByCurrency.Keys)
        {
            if (!freshSummary.PorMoneda.ContainsKey(storedCurrency))
                differences.Add($"sobra la fila por moneda {storedCurrency}");
        }

        if (differences.Count == 0)
        {
            diffDetail = string.Empty;
            return true;
        }

        diffDetail = "Difería en: " + string.Join("; ", differences) + ".";
        return false;
    }

    /// <summary>Si el campo difiere (fuera de la tolerancia), agrega "etiqueta viejo→nuevo" a la lista de diferencias.</summary>
    private static void AddScalarDiff(List<string> differences, string label, decimal stored, decimal fresh)
    {
        if (!AlmostEqual(stored, fresh))
            differences.Add($"{label} {Fmt(stored)}→{Fmt(fresh)}");
    }

    /// <summary>Formatea un monto para el log de auditoría, siempre con punto decimal (cultura invariante).</summary>
    private static string Fmt(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static bool AlmostEqual(decimal a, decimal b) => Math.Abs(a - b) <= MoneyTolerance;

    // ============================================================================================
    // W2 — ANULADA CON SERVICIOS VIVOS (reporta).
    // Una reserva anulada NO debería tener servicios sin cancelar: mientras haya un servicio vivo, su venta puede
    // seguir sumando y la plata queda contradictoria. La reparación (cancelar los servicios) TOCA plata → la decide
    // una persona (endpoint admin de la Tanda 3). Acá solo se reporta.
    // ============================================================================================

    /// <summary>
    /// Detecta reservas anuladas (Cancelled/PendingOperatorRefund) que todavía tienen al menos un servicio VIVO (no
    /// cancelado según <see cref="ServiceResolutionRules"/>). Solo REPORTA (finding AutoRepaired=false); no toca nada.
    /// </summary>
    public static async Task<IReadOnlyList<CoherenceFinding>> DetectAnnulledWithLiveServicesAsync(
        AppDbContext db, CancellationToken ct)
    {
        var reservas = await db.Reservas
            .AsNoTracking()
            .Where(r => AnnulledStatuses.Contains(r.Status))
            // Orden estable por número de reserva: los números viajan DENTRO del mensaje del aviso y la dedup compara
            // el mensaje entero; sin OrderBy, Postgres podría devolver las mismas reservas en otro orden y el aviso
            // se apagaría/recrearía idéntico cada noche sin que nada haya cambiado.
            .OrderBy(r => r.NumeroReserva)
            .Include(r => r.Servicios)
            .Include(r => r.FlightSegments)
            .Include(r => r.HotelBookings)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .ToListAsync(ct);

        var findings = new List<CoherenceFinding>();
        foreach (var reserva in reservas)
        {
            ct.ThrowIfCancellationRequested();

            if (!HasAnyLiveService(reserva))
                continue;

            findings.Add(new CoherenceFinding(
                Code: "W2",
                EntityType: "Reserva",
                EntityId: reserva.Id,
                Detail: $"Reserva anulada ({reserva.Status}) con al menos un servicio sin cancelar.",
                AutoRepaired: false,
                // El número de reserva es lo ÚNICO mostrable: el aviso al dueño lo lista para que sepa CUÁL revisar.
                DisplayReference: reserva.NumeroReserva));
        }

        return findings;
    }

    /// <summary>
    /// True si la reserva tiene AL MENOS un servicio vivo (no cancelado). Recorre las 6 colecciones usando el MISMO
    /// helper de "cancelado" que el cálculo de plata (<see cref="ServiceResolutionRules.IsCancelled(FlightSegment)"/>
    /// y sus sobrecargas), para no divergir de la semántica oficial.
    /// </summary>
    private static bool HasAnyLiveService(Reserva reserva)
    {
        if (reserva.FlightSegments != null
            && reserva.FlightSegments.Any(f => !ServiceResolutionRules.IsCancelled(f))) return true;
        if (reserva.HotelBookings != null
            && reserva.HotelBookings.Any(h => !ServiceResolutionRules.IsCancelled(h))) return true;
        if (reserva.TransferBookings != null
            && reserva.TransferBookings.Any(t => !ServiceResolutionRules.IsCancelled(t))) return true;
        if (reserva.PackageBookings != null
            && reserva.PackageBookings.Any(p => !ServiceResolutionRules.IsCancelled(p))) return true;
        if (reserva.AssistanceBookings != null
            && reserva.AssistanceBookings.Any(a => !ServiceResolutionRules.IsCancelled(a))) return true;
        if (reserva.Servicios != null
            && reserva.Servicios.Any(s => !ServiceResolutionRules.IsCancelled(s))) return true;

        return false;
    }

    // ============================================================================================
    // W5 — ANULADA CON DEUDA SIN JUSTIFICACIÓN (reporta).
    // Una anulada con saldo positivo (deuda) SIN una Nota de Débito de multa que la respalde es un dato roto: no hay
    // comprobante que justifique cobrarle al cliente. El vigía NUNCA arregla plata/multa solo → solo reporta.
    // ============================================================================================

    /// <summary>
    /// Detecta reservas anuladas cuyo contexto de plata, según la regla de dominio
    /// <see cref="ReservationDebtRules.DeriveForCancelled"/>, cae en
    /// <see cref="ReservationDebtRules.CancelledMoneyContext.Inconsistent"/> (saldo positivo sin NINGÚN rastro de
    /// multa que lo respalde). Solo REPORTA (finding AutoRepaired=false); no modifica la reserva.
    ///
    /// <para><b>Qué reporta y qué NO</b>: W5 reporta ÚNICAMENTE el caso <c>Inconsistent</c> (saldo a cobrar sin
    /// multa viva ni multa en revisión = dato roto sin explicación). NO reporta el caso
    /// <see cref="ReservationDebtRules.CancelledMoneyContext.PenaltyUnderReview"/> (multa confirmada con Nota de
    /// Débito fallida / en resolución manual): ese caso YA lo vigila la bandeja de back-office
    /// GET /cancellations/debit-notes/pending, que es quien la puede destrabar. Por eso, para separar "sin ningún
    /// rastro" de "en revisión", W5 consulta los DOS predicados compartidos (viva + en revisión) y solo levanta
    /// cuando no cae en ninguno.</para>
    ///
    /// <para>Se evalúa DESPUÉS de las reparaciones de plata del job (W3 + recalculador de anuladas), así el saldo que
    /// mira es el fresco: si la "deuda" era solo una proyección vieja, ya se recalculó y no cae acá.</para>
    /// </summary>
    public static async Task<IReadOnlyList<CoherenceFinding>> DetectAnnulledInconsistentDebtAsync(
        AppDbContext db, CancellationToken ct)
    {
        // Saldo por reserva anulada. Se lee el escalar Balance (surrogate/semáforo): en anuladas mono-moneda es el
        // saldo real; en multimoneda es > 0 sii alguna moneda debe → suficiente para la regla de "deuda o no".
        var annulledReservas = await db.Reservas
            .AsNoTracking()
            .Where(r => AnnulledStatuses.Contains(r.Status))
            // Orden estable por número de reserva (mismo motivo que en W2): los números van dentro del mensaje del
            // aviso y la dedup compara el mensaje entero; un orden no determinístico recrearía el aviso sin cambios.
            .OrderBy(r => r.NumeroReserva)
            // Se agrega NumeroReserva a la proyección: es el número mostrable que va al aviso (DisplayReference).
            .Select(r => new { r.Id, r.Balance, r.NumeroReserva })
            .ToListAsync(ct);

        if (annulledReservas.Count == 0)
            return Array.Empty<CoherenceFinding>();

        var annulledIds = annulledReservas.Select(r => r.Id).ToList();

        // Reservas anuladas con multa VIVA, en UNA query (evita N+1). Reusa el predicado compartido
        // CancellationPenaltyRules.LiveDebitNotePredicate (misma definición que usa el contexto de plata de la ficha),
        // así el vigía y la pantalla no divergen.
        var reservaIdsWithLiveDebitNote = (await db.BookingCancellations
                .AsNoTracking()
                .Where(bc => annulledIds.Contains(bc.ReservaId))
                .Where(CancellationPenaltyRules.LiveDebitNotePredicate)
                .Select(bc => bc.ReservaId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        // Reservas anuladas con multa EN REVISIÓN (ND fallida / manual). No son dato roto: las vigila la bandeja de
        // back-office, no W5. Las excluimos del reporte.
        var reservaIdsWithPenaltyUnderReview = (await db.BookingCancellations
                .AsNoTracking()
                .Where(bc => annulledIds.Contains(bc.ReservaId))
                .Where(CancellationPenaltyRules.PenaltyUnderReviewPredicate)
                .Select(bc => bc.ReservaId)
                .Distinct()
                .ToListAsync(ct))
            .ToHashSet();

        var findings = new List<CoherenceFinding>();
        foreach (var reserva in annulledReservas)
        {
            ct.ThrowIfCancellationRequested();

            // Live tiene prioridad; luego UnderReview; si no está en ninguno, no hay respaldo.
            ReservationDebtRules.DebitNoteBacking backing;
            if (reservaIdsWithLiveDebitNote.Contains(reserva.Id))
                backing = ReservationDebtRules.DebitNoteBacking.Live;
            else if (reservaIdsWithPenaltyUnderReview.Contains(reserva.Id))
                backing = ReservationDebtRules.DebitNoteBacking.UnderReview;
            else
                backing = ReservationDebtRules.DebitNoteBacking.None;

            var context = ReservationDebtRules.DeriveForCancelled(reserva.Balance, backing);

            // Solo el dato roto sin explicación (Inconsistent). PenaltyUnderReview NO se reporta acá (la bandeja lo mira).
            if (context != ReservationDebtRules.CancelledMoneyContext.Inconsistent)
                continue;

            findings.Add(new CoherenceFinding(
                Code: "W5",
                EntityType: "Reserva",
                EntityId: reserva.Id,
                Detail: "Reserva anulada con saldo a cobrar sin Nota de Débito de multa que lo respalde.",
                AutoRepaired: false,
                // El número de reserva es lo ÚNICO mostrable: el aviso al dueño lo lista para que sepa CUÁL revisar.
                DisplayReference: reserva.NumeroReserva));
        }

        return findings;
    }
}
