using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// B1 (2026-06-03): convierte los UNIQUE TOTALES de BookingCancellations
    /// (ReservaId, OriginatingInvoiceId) en UNIQUE PARCIALES con filtro
    /// <c>"Status" &lt;&gt; 6</c> (6 = BookingCancellationStatus.Aborted). Asi una
    /// cancelacion abortada deja de trabar la reserva/factura para siempre y se puede
    /// re-cancelar. La logica de re-cancelacion vive en
    /// <c>BookingCancellationService.DraftAsync</c> (auto-aborta ArcaRejected sin NC viva).
    ///
    /// <para><b>PREVALIDACION OBLIGATORIA antes de aplicar en prod</b> (runbook): convertir
    /// un UNIQUE total a parcial nunca falla por datos (relaja la restriccion). PERO si
    /// quedaran DOS filas activas (Status &lt;&gt; 6) para el mismo ReservaId u
    /// OriginatingInvoiceId, el nuevo indice parcial fallaria al crearse. Correr ANTES:
    /// <code>
    /// SELECT "ReservaId", count(*) FROM "BookingCancellations"
    ///   WHERE "Status" &lt;&gt; 6 GROUP BY "ReservaId" HAVING count(*) &gt; 1;
    /// SELECT "OriginatingInvoiceId", count(*) FROM "BookingCancellations"
    ///   WHERE "Status" &lt;&gt; 6 GROUP BY "OriginatingInvoiceId" HAVING count(*) &gt; 1;
    /// </code>
    /// Ambas deben devolver 0 filas. Si devuelven algo, sanear (abortar los duplicados
    /// muertos) antes de migrar. SQL completo en
    /// <c>tools/sql/b1-partial-unique-index-prevalidation.sql</c>.</para>
    /// </summary>
    public partial class B1_AddBookingCancellationPartialUniqueIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_ReservaId",
                table: "BookingCancellations");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations",
                column: "OriginatingInvoiceId",
                unique: true,
                filter: "\"Status\" <> 6");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_ReservaId",
                table: "BookingCancellations",
                column: "ReservaId",
                unique: true,
                filter: "\"Status\" <> 6");
        }

        /// <inheritdoc />
        /// <remarks>
        /// ADVERTENCIA: este Down recrea los UNIQUE TOTALES. Si despues de aplicar el Up
        /// se ejercito el flujo de re-cancelacion (una reserva quedo con un BC Aborted
        /// + un BC nuevo activo, ambos comparten ReservaId/OriginatingInvoiceId), la
        /// creacion del UNIQUE total fallara por filas duplicadas. Para revertir en ese
        /// escenario hay que sanear/archivar las filas Aborted primero. No se resuelve en
        /// codigo a proposito: un rollback de esta migracion es excepcional y debe pasar
        /// por el runbook con revision humana.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_ReservaId",
                table: "BookingCancellations");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations",
                column: "OriginatingInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_ReservaId",
                table: "BookingCancellations",
                column: "ReservaId",
                unique: true);
        }
    }
}
