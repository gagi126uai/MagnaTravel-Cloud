using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-017 F1.4 (fechas limite + buckets de alerta, 2026-06-06): agrega el setting
    /// <c>OperationalFinanceSettings.ServiceDeadlineAlertDays</c> (ventana en dias de las alertas de
    /// fechas limite de servicio). 100% ADITIVA.
    ///
    /// <para><b>Default 7 a nivel BD</b> (no 0): se setea a mano en la migracion (mismo patron que
    /// <c>StaleCostReferenceDays</c> en M2 y <c>OperatorRefundTimeoutDays</c> en FC1_2_0). Asi la fila de
    /// settings ya existente queda en 7 al aplicar; el inicializador <c>= 7</c> de la entidad solo cubre filas
    /// NUEVAS creadas en codigo, no backfillea las viejas. El snapshot NO lleva default, asi que esto no
    /// genera drift en el proximo <c>migrations add</c>.</para>
    ///
    /// <para><b>Orden de deploy / R8</b>: encolada DETRAS de la cola de migraciones pendientes del VPS, sin
    /// tocar ni reordenar ninguna. Aditiva, default seguro, sin DROP/ALTER destructivo. NO se aplica desde aca.</para>
    /// </summary>
    public partial class Adr017_M3_AddServiceDeadlineAlertDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ServiceDeadlineAlertDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 7);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ServiceDeadlineAlertDays",
                table: "OperationalFinanceSettings");
        }
    }
}
