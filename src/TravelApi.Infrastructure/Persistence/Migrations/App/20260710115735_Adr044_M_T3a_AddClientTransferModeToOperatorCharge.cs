using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T3a (2026-07-10): 2 columnas nuevas sobre <c>BookingCancellationLineOperatorCharges</c> (como se
    /// traslada CADA cargo al cliente en la Nota de Debito multi-operador) + 1 columna nueva en
    /// <c>OperationalFinanceSettings</c> (parametro sin firma contable, ver el XML-doc de la entidad).
    ///
    /// <para><b>ClientTransferMode</b> (int, NOT NULL, default 0=AsIs): aditiva y segura sobre datos existentes
    /// — todo cargo YA persistido (T2) nace con AsIs, que es EXACTAMENTE el comportamiento de siempre (el cargo
    /// se traslada tal cual, sin fee ni absorcion). Cero regresion.</para>
    ///
    /// <para><b>ManagementFeeAmount</b> (numeric(18,2), nullable): monto ADICIONAL del fee de gestion, solo tiene
    /// sentido cuando <c>ClientTransferMode = WithManagementFee (1)</c>. Los 2 CHECK de abajo blindan esa regla
    /// a nivel base (mismo patron que el CHECK de <c>DocumentRef</c> de la migracion T2b).</para>
    ///
    /// <para><b>CancellationDebitNoteRiPassThroughAlicuotaIvaId</b> (int, nullable en
    /// <c>OperationalFinanceSettings</c>): default null a proposito (ver el XML-doc de la entidad) — mientras
    /// quede en null, la ND automatica de un emisor Responsable Inscripto con cargos pass-through NO se emite
    /// sola y queda en revision manual con un mensaje claro.</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura)</b> — confirma que los cargos existentes (T2) quedaron
    /// con el default AsIs y que los 2 CHECK nuevos quedaron activos:
    /// <code>
    /// SELECT COUNT(*) FROM "BookingCancellationLineOperatorCharges" WHERE "ClientTransferMode" &lt;&gt; 0;
    /// -- esperado: 0 justo despues de aplicar (todo cargo previo nace AsIs)
    ///
    /// SELECT conname FROM pg_constraint
    /// WHERE conrelid = '"BookingCancellationLineOperatorCharges"'::regclass AND contype = 'c';
    /// -- esperado: sumar 2 filas nuevas (managementfeeamount_required_when_withfee,
    /// -- managementfeeamount_empty_unless_withfee) a las 2 ya existentes de T2b
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T3a_AddClientTransferModeToOperatorCharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CancellationDebitNoteRiPassThroughAlicuotaIvaId",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClientTransferMode",
                table: "BookingCancellationLineOperatorCharges",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "ManagementFeeAmount",
                table: "BookingCancellationLineOperatorCharges",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // ManagementFeeAmount es obligatorio (y > 0) SOLO cuando ClientTransferMode = WithManagementFee (1).
            // En cualquier otro modo (AsIs=0, Absorbed=2) tiene que quedar vacio: un monto "fantasma" ahi
            // confundiria a quien lea el cargo despues (¿por que hay un fee cargado si no se traslada con fee?).
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellationLineOperatorCharges_managementfee_required_when_withfee;
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  ADD CONSTRAINT chk_BookingCancellationLineOperatorCharges_managementfee_required_when_withfee
                  CHECK ("ClientTransferMode" <> 1 OR ("ManagementFeeAmount" IS NOT NULL AND "ManagementFeeAmount" > 0));
                """);

            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellationLineOperatorCharges_managementfee_empty_unless_withfee;
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  ADD CONSTRAINT chk_BookingCancellationLineOperatorCharges_managementfee_empty_unless_withfee
                  CHECK ("ClientTransferMode" = 1 OR "ManagementFeeAmount" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancellationDebitNoteRiPassThroughAlicuotaIvaId",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "ClientTransferMode",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "ManagementFeeAmount",
                table: "BookingCancellationLineOperatorCharges");
        }
    }
}
