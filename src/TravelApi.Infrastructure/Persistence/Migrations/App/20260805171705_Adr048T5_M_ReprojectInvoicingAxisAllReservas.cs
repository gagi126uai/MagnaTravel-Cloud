using Microsoft.EntityFrameworkCore.Migrations;
using TravelApi.Infrastructure.Reservations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Fix bug "Falta facturar" fantasma (2026-08-05, F-9) — RE-PROYECCION del eje de FACTURACION
    /// materializado (<c>Reserva.DerivedInvoicingStatus</c>, columna <c>TravelFiles."DerivedInvoicingStatus"</c>).
    ///
    /// <para><b>Que problema resuelve</b>: el backfill original (<c>20260718012634_Adr048_M2_...</c>) rellenó
    /// esta columna comparando el facturado neto contra <c>TotalSale</c> (venta COTIZADA), no contra
    /// <c>ConfirmedSale</c> (venta FIRME) — el mismo bug que este ciclo de trabajo corrigió en el escritor
    /// go-forward (<c>ReservaMoneyPersister</c>/<c>ReservaDerivedAxesProjector</c>) y en el SQL compartido
    /// (<c>Adr048T5BackfillSql.InvoicingAxisWithInvoices</c>, ya corregido — ver su XML-doc). El escritor
    /// go-forward SOLO re-proyecta la columna cuando la reserva vuelve a mover plata o factura de nuevo
    /// (<c>ReservaMoneyPersister.PersistAsync</c>/<c>RefreshInvoicingAxisOnlyAsync</c>) — una reserva con el
    /// bug (ej. Perdida, con servicios cotizados nunca confirmados y ya facturada) puede quedarse CONGELADA
    /// con el valor viejo para siempre si nadie vuelve a tocarla, y el LISTADO (que lee esta columna
    /// materializada, no el cálculo en vivo) seguiría mostrando el chip equivocado.</para>
    ///
    /// <para><b>Por qué re-proyección INCONDICIONAL (sin <c>WHERE ... IS NULL</c> ni ningún otro filtro de
    /// "todavía no tocado")</b>: la derivación es una función PURA y determinística de datos que ya existen
    /// (comprobantes + <c>ConfirmedSale</c>) — no depende de en qué orden llegaron los eventos ni de un
    /// estado intermedio. Volver a calcularla para TODAS las reservas, aunque ya tuvieran un valor
    /// "correcto" con el criterio viejo, da el mismo resultado que tenían si el criterio viejo y el nuevo
    /// coincidían para esa fila, y CORRIGE las que divergían. Correrla dos veces (por error, o en un
    /// entorno que ya la corrió) es un no-op: mismos datos de entrada, mismo resultado de salida.</para>
    ///
    /// <para><b>Por qué reusa <c>Adr048T5BackfillSql</c> en vez de escribir SQL nuevo</b>: son las MISMAS
    /// dos sentencias que ya corrieron una vez en PROD (mismo texto, ya corregido a <c>ConfirmedSale</c> en
    /// esta tanda) — <c>InvoicingAxisWithInvoices</c> (reservas CON al menos un comprobante con CAE
    /// aprobado) + <c>InvoicingAxisFallback</c> (el resto, sin ningún comprobante aprobado: quedan en
    /// "NotInvoiced"). Entre las dos cubren el universo COMPLETO de <c>TravelFiles</c> sin superponerse
    /// (la primera solo toca filas presentes en el agregado de facturas con <c>Resultado='A'</c>; la
    /// segunda solo las que NO tienen ninguna). No se reinventa el criterio en un tercer lugar: si mañana
    /// cambia, cambia en <c>Adr048T5BackfillSql</c> y esta migración (si hiciera falta correrla de nuevo en
    /// un entorno nuevo) usaría el criterio vigente en ese momento.</para>
    ///
    /// <para><b>OJO nombres REALES en Postgres (lección del incidente del 2026-07-10: una migración con
    /// <c>ReservaId</c> en vez de <c>TravelFileId</c> tumbó PROD 2 horas — validado a mano contra
    /// <c>AppDbContextModelSnapshot.cs</c> antes de escribir este archivo, no asumido)</b>: la reserva es
    /// <c>"TravelFiles"</c> (PK <c>"Id"</c>, columna <c>"DerivedInvoicingStatus"</c> y <c>"ConfirmedSale"</c>
    /// existen ahí sin remapeo — <c>AppDbContextModelSnapshot.cs</c> líneas ~4984/~5093, propiedad C# ==
    /// nombre de columna); <c>"Invoices"</c> referencia la reserva con la columna
    /// <c>"TravelFileId"</c> (NO <c>"ReservaId"</c> — la propiedad C# se llama <c>ReservaId</c> pero
    /// <c>AppDbContext</c> la remapea con <c>HasColumnName("TravelFileId")</c>). Ambos nombres ya estaban
    /// verificados en <c>Adr048T5BackfillSql</c> desde que corrió la primera vez en PROD (2026-07-17); esta
    /// migración no agrega SQL nuevo, solo re-ejecuta el mismo texto ya corregido.</para>
    ///
    /// <para><b>No cambia el esquema</b>: 0 columnas/índices nuevos (las dos columnas ya existen desde
    /// <c>20260718012634_Adr048_M2_...</c>), por eso <c>AppDbContextModelSnapshot.cs</c> no se modifica con
    /// esta migración — mismo patrón que <c>Adr022_M3_BackfillIsReplacedFromLiveOriginStatus</c>.</para>
    ///
    /// <para><b>Solo toca el eje de FACTURACION</b>: el eje de COBRO (<c>DerivedCollectionStatus</c>) no
    /// tenía este bug (ya usaba <c>ConfirmedSale</c> desde H1b, 2026-06-24) — no hace falta re-proyectarlo.</para>
    ///
    /// <para><b>Down = no-op documentado</b>: no hay una versión "anterior" segura a la que volver — revertir
    /// con el mismo criterio (el viejo, con <c>TotalSale</c>) reintroduciría el bug a propósito. Si hiciera
    /// falta deshacer la FUNCIONALIDAD completa (no solo esta re-proyección), se revierte hasta la migración
    /// que agregó las columnas (<c>20260718012634_Adr048_M2_...</c>), que sí las dropea.</para>
    /// </summary>
    public partial class Adr048T5_M_ReprojectInvoicingAxisAllReservas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Mismo orden que el backfill original: primero las reservas CON comprobante aprobado (join),
            // despues el fallback "sin ninguno" (NOT EXISTS). El texto vive en Adr048T5BackfillSql — tocar
            // el criterio ahí, no acá, para que la migración y el detalle/listado en vivo nunca diverjan.
            migrationBuilder.Sql(Adr048T5BackfillSql.InvoicingAxisWithInvoices);
            migrationBuilder.Sql(Adr048T5BackfillSql.InvoicingAxisFallback);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op INTENCIONAL (ver "Down = no-op documentado" en el XML-doc de la clase): no existe un
            // valor "anterior" al que sea correcto volver — el criterio viejo era el bug que esta migración
            // corrige. Revertir la FUNCIONALIDAD completa se hace en la migración que agregó las columnas.
        }
    }
}
