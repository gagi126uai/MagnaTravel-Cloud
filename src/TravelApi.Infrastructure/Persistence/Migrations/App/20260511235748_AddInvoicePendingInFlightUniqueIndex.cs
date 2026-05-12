using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 (2026-05-11, fiscal critico): backstop a nivel base de datos para el guard
    /// anti-doble-emision en InvoiceService.CreateAsync.
    ///
    /// El guard aplicativo (AnyAsync + lanzar InvalidOperationException) sigue siendo
    /// la primera linea de defensa para devolver un 409 con mensaje claro. Pero bajo
    /// concurrencia (doble click, T1 y T2 corriendo simultaneamente) la secuencia
    /// "AnyAsync -> SaveChanges" no es atomica: T1 lee "no hay PENDING", T2 lee "no
    /// hay PENDING", ambos insertan en paralelo y se encolan 2 ProcessInvoiceJob a la
    /// vez. Cada job pide CAE a AFIP -> doble factura, ruptura de correlativa fiscal
    /// (numeracion AFIP, libros IVA), incidente grave dificil de revertir.
    ///
    /// Indice 1 — UX_Invoices_OnePendingPerReserva (UNIQUE, PARCIAL):
    ///  - Garantiza a nivel Postgres que para cada TravelFileId pueda existir, COMO MAXIMO,
    ///    una Invoice en estado "PENDING" que NO este anulada (AnnulmentStatus != 2 == Succeeded).
    ///  - Cuando el guard aplicativo se pasa por una race, el segundo INSERT recibe
    ///    23505 (unique_violation) y el InvoiceService traduce a la MISMA
    ///    InvalidOperationException — el contrato del endpoint no cambia (sigue 409).
    ///  - Filtro AnnulmentStatus != 2: una factura con NC aprobada ya no esta en vuelo
    ///    (correlativa cerrada), no debe bloquear otras emisiones futuras sobre la reserva.
    ///  - Filtro Resultado='PENDING': solo bloquea cuando hay job ProcessInvoiceJob en
    ///    curso. Facturas A (aprobada) y R (rechazada) son estados terminales y multiples
    ///    facturas por reserva siguen permitidas (cobranzas parciales, NCs).
    ///  - Filtro TravelFileId IS NOT NULL: la columna es nullable y el unique index no
    ///    debe colisionar entre invoices huerfanas (caso teorico, no productivo).
    ///
    /// Indice 2 — IX_Invoices_TravelFileId_Resultado (NO UNICO):
    ///  - Performance backstop para las 3 subqueries correlated de
    ///    BuildInvoicingWorkItemsQuery (AlreadyInvoiced, ForcedByUserName,
    ///    HasInvoiceInProgress). Filtran por (ReservaId, Resultado) y el index existente
    ///    sobre TravelFileId solo no es selectivo cuando hay muchas invoices por reserva.
    ///  - Marcado IF NOT EXISTS por consistencia con AddOperationalIndexes y para que
    ///    la migracion sea re-runnable si quedan rastros de un intento previo.
    ///
    /// Ambos drops en Down() usan IF EXISTS para rollback seguro.
    /// </summary>
    public partial class AddInvoicePendingInFlightUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "UX_Invoices_OnePendingPerReserva"
                    ON "Invoices" ("TravelFileId")
                    WHERE "Resultado" = 'PENDING'
                      AND "AnnulmentStatus" <> 2
                      AND "TravelFileId" IS NOT NULL;

                CREATE INDEX IF NOT EXISTS "IX_Invoices_TravelFileId_Resultado"
                    ON "Invoices" ("TravelFileId", "Resultado");
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_Invoices_TravelFileId_Resultado";
                DROP INDEX IF EXISTS "UX_Invoices_OnePendingPerReserva";
            """);
        }
    }
}
