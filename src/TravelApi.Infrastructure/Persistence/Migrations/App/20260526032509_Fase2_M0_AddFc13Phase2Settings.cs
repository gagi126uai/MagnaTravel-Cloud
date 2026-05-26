using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.0, 2026-05-22): primera migracion
    /// de Fase 2. Agrega 5 columnas nuevas a <c>OperationalFinanceSettings</c>
    /// para soportar la emision real de NC parcial contra ARCA + el flow dual
    /// (NC total + factura nueva).
    ///
    /// <para><b>Por que defaults explicitos</b>: el generador de EF NO propaga los
    /// initializers de C# (<c>= 10</c>, <c>= 0.01m</c>) al <c>defaultValue</c> de la
    /// migracion — por defecto pone <c>0</c> para int/decimal. Para que las filas
    /// existentes en prod queden con los defaults productivos (no con 0), seteamos
    /// los <c>defaultValue</c> a mano. Esto es CRITICO para
    /// <c>IdempotencyKeyStaleThresholdMinutes</c>: si quedaba en 0, el job de
    /// reconciliacion declararia "huerfana" cualquier key recien insertada (0 min
    /// staleness) y dispararia <c>FECompUltimoAutorizado</c> innecesariamente.</para>
    ///
    /// <para><b>Aditiva, sin DROP</b>: solo agrega columnas, no toca datos
    /// existentes. <c>Down()</c> hace el rollback sin perdida.</para>
    ///
    /// <para><b>Defaults aplicados</b>:</para>
    /// <list type="bullet">
    /// <item><c>EnablePartialCreditNoteRealEmission</c> = false (flag maestro Fase 2 OFF).</item>
    /// <item><c>EnableTotalPlusNewInvoiceAutoProcessing</c> = false (flow dual OFF).</item>
    /// <item><c>IvaProrrateoMode</c> = 0 (ProportionalToNet, criterio conservador).</item>
    /// <item><c>PartialCreditNoteRoundingTolerance</c> = 0.01 (un centavo).</item>
    /// <item><c>IdempotencyKeyStaleThresholdMinutes</c> = 10 minutos.</item>
    /// </list>
    /// </summary>
    public partial class Fase2_M0_AddFc13Phase2Settings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Flag maestro Fase 2 (real emission). OFF en prod hasta signoff QA + contador.
            migrationBuilder.AddColumn<bool>(
                name: "EnablePartialCreditNoteRealEmission",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Flag flow dual (NC total + factura nueva). OFF hasta cumplir criterio G-F2-A.
            migrationBuilder.AddColumn<bool>(
                name: "EnableTotalPlusNewInvoiceAutoProcessing",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // RH2-004: umbral en minutos para considerar huerfana una key de
            // ArcaIdempotencyKeys. Default 10. CRITICO setearlo aca: si queda en 0
            // (default de EF), el job de reconciliacion declararia "huerfana"
            // cualquier key recien insertada y rompe la idempotencia.
            migrationBuilder.AddColumn<int>(
                name: "IdempotencyKeyStaleThresholdMinutes",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 10);

            // RH-005: modo de prorrateo de IVA. 0 = ProportionalToNet (default).
            // Si el contador confirma PerItem (=1), se cambia desde panel admin sin redeploy.
            migrationBuilder.AddColumn<int>(
                name: "IvaProrrateoMode",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Tolerancia validacion pre-envio ARCA. Default 0.01 (un centavo) en
            // la moneda original del comprobante. CRITICO setearlo aca: si queda
            // en 0 (default de EF), CUALQUIER diferencia de redondeo dispararia
            // throw + log error y rechazaria todas las NC parciales.
            migrationBuilder.AddColumn<decimal>(
                name: "PartialCreditNoteRoundingTolerance",
                table: "OperationalFinanceSettings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0.01m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback: dropea las 5 columnas. Los datos pre-migracion (FC1.2 + FC1.3 Fase 1)
            // no se tocan — son tablas y columnas distintas. Sin perdida de datos.
            migrationBuilder.DropColumn(
                name: "EnablePartialCreditNoteRealEmission",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "EnableTotalPlusNewInvoiceAutoProcessing",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "IdempotencyKeyStaleThresholdMinutes",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "IvaProrrateoMode",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "PartialCreditNoteRoundingTolerance",
                table: "OperationalFinanceSettings");
        }
    }
}
