using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T3b Decision 1 (2026-07-10): agrega <c>TargetInvoiceId</c> (FK nullable a <c>Invoices</c>) sobre
    /// <c>BookingCancellationLineOperatorCharges</c> — a que factura de venta del cliente se traslada cada cargo
    /// del operador. Reemplaza el candado "ARREGLO 2" para el caso de 2+ facturas activas de la misma reserva
    /// (ADR-042): con 1 factura se autocompleta transparente (sin cambios de comportamiento); con 2+ un humano
    /// elige, y sin elegir el cargo NO se factura solo (revision manual, mismo criterio conservador de siempre).
    ///
    /// <para><b>Aditiva y sin backfill</b>: los cargos ya confirmados antes de esta tanda quedan con
    /// <c>TargetInvoiceId = null</c> (no hay forma mecanica de resolverlos: la ambiguedad de origen es
    /// justamente lo que no existia mecanizado). Caen al mismo fallback que rige hoy con 2+ facturas.</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura)</b> — confirma que la columna y la FK quedaron activas y
    /// que los cargos existentes no se vieron afectados:
    /// <code>
    /// SELECT COUNT(*) FROM "BookingCancellationLineOperatorCharges" WHERE "TargetInvoiceId" IS NOT NULL;
    /// -- esperado: 0 justo despues de aplicar (ningun cargo previo a esta tanda tiene el dato)
    ///
    /// SELECT conname FROM pg_constraint
    /// WHERE conrelid = '"BookingCancellationLineOperatorCharges"'::regclass AND contype = 'f'
    ///   AND conname LIKE '%TargetInvoi%';
    /// -- esperado: 1 fila (la FK nueva hacia "Invoices")
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T3b1_AddTargetInvoiceIdToOperatorCharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TargetInvoiceId",
                table: "BookingCancellationLineOperatorCharges",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineOperatorCharges_TargetInvoiceId",
                table: "BookingCancellationLineOperatorCharges",
                column: "TargetInvoiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_BookingCancellationLineOperatorCharges_Invoices_TargetInvoi~",
                table: "BookingCancellationLineOperatorCharges",
                column: "TargetInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookingCancellationLineOperatorCharges_Invoices_TargetInvoi~",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellationLineOperatorCharges_TargetInvoiceId",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "TargetInvoiceId",
                table: "BookingCancellationLineOperatorCharges");
        }
    }
}
