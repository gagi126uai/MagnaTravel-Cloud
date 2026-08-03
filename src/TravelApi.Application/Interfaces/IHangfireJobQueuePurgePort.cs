namespace TravelApi.Application.Interfaces;

/// <summary>
/// B7 (plan 2026-07-31 tarde, deuda ADR-052) + retomo 2026-08-03 (decisión "limpiar y avisar" firmada por el
/// dueño): después de "Restaurar TODO", las colas de Hangfire quedan con jobs que VINIERON ADENTRO del
/// resguardo restaurado (el dump incluye el esquema <c>hangfire</c> completo, porque el storage de Hangfire
/// vive en la MISMA base que se restaura — <c>Program.cs</c> cae a <c>DefaultConnection</c> si no hay
/// <c>JobStorageConnection</c> propia, y no la hay en ningún ambiente hoy).
///
/// <para><b>OJO, corrección sobre la versión anterior de este doc</b>: esto NO son jobs "de antes del
/// restore" — esos mueren solos con el intercambio de nombres de bases (la base vieja deja de existir bajo
/// ese nombre). Lo que hay que purgar es lo contrario: jobs que quedaron encolados/programados DENTRO de la
/// FOTO restaurada (coherentes con esos datos viejos), que nadie va a re-encolar porque ningún job recurrente
/// los repone. Incluyen jobs fiscales: la emisión de un comprobante (<c>AfipService.ProcessInvoiceJob</c>) o
/// la anulación de uno (<c>InvoiceService.ProcessAnnulmentJob</c>) que habían quedado pendientes en el momento
/// exacto que se sacó ese resguardo.</para>
///
/// <para><b>Qué se purga y qué NO</b>: solo los jobs ENCOLADOS (Enqueued) y PROGRAMADOS (Scheduled) — los
/// que están esperando para correr. Los jobs RECURRENTES DE SISTEMA (los definidos con
/// <c>RecurringJob.AddOrUpdate</c>, ej. el chequeo nocturno de vencimientos) NO se tocan: Hangfire los
/// vuelve a disparar solo según su propio cron, no dependen de nada encolado.</para>
///
/// <para><b>Decisión del dueño (2026-08-03)</b>: la purga se queda (nada fiscal se dispara solo después de
/// una restauración), pero tiene que CONTAR LA VERDAD — devolver el desglose por categoría de negocio (no
/// un total ciego) para que la auditoría y el aviso al usuario puedan distinguir "se perdió una emisión de
/// comprobante" de "se perdió una tarea cualquiera".</para>
/// </summary>
public interface IHangfireJobQueuePurgePort
{
    /// <summary>
    /// Purga los jobs encolados/programados y devuelve el desglose por categoría de negocio. Implementación
    /// BEST-EFFORT: la llama el caller dentro de un try/catch propio, un restore ya exitoso no se puede
    /// convertir en error porque Hangfire no respondió.
    /// </summary>
    Task<HangfireJobPurgeResult> PurgeQueuedAndScheduledJobsAsync(CancellationToken ct = default);
}

/// <summary>
/// Estados posibles de <see cref="HangfireJobPurgeResult.Estado"/>: contar la verdad sobre CÓMO salió la
/// purga, no solo cuántos jobs se borraron (bloqueante de seguridad del retomo 2026-08-03: antes, cuando el
/// storage de Hangfire tiraba una excepción, el catch devolvía 0 y la auditoría quedaba IDÉNTICA a "no había
/// nada para purgar" — dos situaciones muy distintas que no se podían distinguir después).
/// </summary>
public static class HangfireJobPurgeStatus
{
    /// <summary>Se recorrieron todas las colas y todos los programados sin cortarse por el tope de paginación ni por ninguna excepción.</summary>
    public const string Completa = "completa";

    /// <summary>El storage de Hangfire tiró una excepción antes de poder purgar nada (o de saber cuánto había).</summary>
    public const string Fallo = "falló";

    /// <summary>Se purgó ALGO pero no se pudo asegurar que fuera TODO (se cortó por el tope de paginación, o algún job puntual no se pudo borrar).</summary>
    public const string Incompleta = "incompleta";
}

/// <summary>
/// Categorías de negocio en las que se clasifica cada job purgado. T-5 (nunca nombres técnicos internos hacia
/// afuera): el mapeo desde el nombre real del método del job a estas categorías en criollo se hace ADENTRO de
/// <see cref="HangfireJobCategoryClassifier"/> y de la implementación real del puerto — ningún JobId ni nombre
/// de método sale de esa frontera.
/// </summary>
public static class HangfireJobCategory
{
    public const string EmisionDeComprobante = "EmisionDeComprobante";
    public const string AnulacionDeComprobante = "AnulacionDeComprobante";
    public const string OtrasTareas = "OtrasTareas";
}

/// <summary>
/// Desglose de la purga de colas de Hangfire, por categoría de negocio en vez de un total ciego. Sin esto, la
/// auditoría de una restauración no podía distinguir "perdimos una emisión de factura pendiente" (hay que
/// revisarla a mano) de "perdimos un recordatorio cualquiera" (no importa).
/// </summary>
public sealed record HangfireJobPurgeResult(
    int EmisionesDeComprobantePurgadas,
    int AnulacionesDeComprobantePurgadas,
    int OtrasTareasPurgadas,
    string Estado)
{
    /// <summary>Total purgado — solo para el log técnico interno; la auditoría de negocio usa el desglose, no este número.</summary>
    public int Total => EmisionesDeComprobantePurgadas + AnulacionesDeComprobantePurgadas + OtrasTareasPurgadas;

    /// <summary>Cuántos jobs purgados son de un comprobante fiscal (emisión + anulación) — el grupo que amerita avisarle al usuario.</summary>
    public int TotalFiscal => EmisionesDeComprobantePurgadas + AnulacionesDeComprobantePurgadas;

    /// <summary>Resultado "no se pudo purgar nada" para el camino BEST-EFFORT cuando el storage de Hangfire explota antes de poder contar algo.</summary>
    public static HangfireJobPurgeResult Fallida() => new(0, 0, 0, HangfireJobPurgeStatus.Fallo);
}

/// <summary>
/// Mapea el nombre TÉCNICO del método de un job de Hangfire (ej. <c>"ProcessInvoiceJob"</c>) a su categoría de
/// negocio en criollo. Puro y testeable sin tocar Hangfire para nada: solo mira un string.
///
/// <para><b>Por qué <c>nameof</c> contra la interfaz del servicio, no un string suelto</b>: si algún día
/// renombran <c>ProcessInvoiceJob</c>, este mapeo deja de COMPILAR en vez de quedar mudo comparando contra un
/// nombre viejo que ya no matchea ningún job real — un bug silencioso que solo se notaría el día que un
/// restore real dejara pasar una emisión de factura sin clasificar.</para>
/// </summary>
public static class HangfireJobCategoryClassifier
{
    public static string Classify(string? jobMethodName)
    {
        if (string.Equals(jobMethodName, nameof(IAfipService.ProcessInvoiceJob), StringComparison.Ordinal))
        {
            return HangfireJobCategory.EmisionDeComprobante;
        }

        if (string.Equals(jobMethodName, nameof(IInvoiceService.ProcessAnnulmentJob), StringComparison.Ordinal))
        {
            return HangfireJobCategory.AnulacionDeComprobante;
        }

        // Una nota de credito parcial (cancelacion parcial de un servicio ya facturado) es, para este
        // clasificador, una EMISION de comprobante: crea y postea a ARCA un comprobante nuevo (la NC), igual
        // que ProcessInvoiceJob. Antes de este fix caia en "OtrasTareas" y el aviso fiscal post-restore no
        // se disparaba (hallazgo de seguridad, retomo 2026-08-03).
        if (string.Equals(jobMethodName, nameof(IInvoiceService.ProcessPartialCreditNoteJob), StringComparison.Ordinal))
        {
            return HangfireJobCategory.EmisionDeComprobante;
        }

        return HangfireJobCategory.OtrasTareas;
    }
}
