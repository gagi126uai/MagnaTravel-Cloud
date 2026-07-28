using Microsoft.EntityFrameworkCore;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Candado fiscal COMPARTIDO (2026-07-28, hallazgo B2 de la revisión de seguridad sobre "Restaurar TOTAL"):
/// la MISMA regla firmada del dueño que ya protege "Empezar de cero" (<see cref="SystemDataWipeService"/>) —
/// nunca se puede tocar la base si hay comprobantes fiscales REALES (emitidos en modo productivo) o si AFIP
/// está configurado en modo productivo ahora mismo. Antes de esta obra, la restauración TOTAL no tenía este
/// candado en absoluto (podía reemplazar la base entera, comprobantes productivos incluidos, sin ningún
/// aviso) — se centraliza acá para que las dos operaciones (borrado masivo y restauración total) usen
/// EXACTAMENTE la misma consulta, en vez de mantener dos copias que se puedan desincronizar en silencio.
///
/// <para><b>Por qué NO devuelve el mensaje directamente</b>: "Empezar de cero" y "Restaurar TOTAL" son verbos
/// distintos ("no se puede borrar" vs "no se puede restaurar") — este evaluador devuelve solo el MOTIVO
/// (<see cref="Reason"/>), y cada caller arma su propio mensaje en criollo con el verbo correcto.</para>
/// </summary>
internal static class FiscalLockEvaluator
{
    public enum Reason
    {
        /// <summary>No hay ningún candado fiscal activo.</summary>
        None,

        /// <summary>Hay al menos una factura marcada como emitida en el ambiente PRODUCTIVO de ARCA.</summary>
        LiveProductionInvoice,

        /// <summary>La configuración de AFIP está en modo productivo ahora mismo (aunque no haya facturas emitidas todavía).</summary>
        AfipInProductionMode,
    }

    public static async Task<Reason> EvaluateAsync(AppDbContext context, CancellationToken ct)
    {
        var hasInvoiceMarkedProduction = await context.Invoices.AsNoTracking()
            .AnyAsync(invoice => invoice.WasIssuedInProduction == true, ct);
        if (hasInvoiceMarkedProduction)
        {
            return Reason.LiveProductionInvoice;
        }

        var afipSettings = await context.AfipSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (afipSettings is { IsProduction: true })
        {
            return Reason.AfipInProductionMode;
        }

        return Reason.None;
    }
}
