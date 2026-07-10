using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T4a (2026-07-10): renombra la columna <c>RateAtNdEmission</c> -&gt; <c>RateAtChargeDay</c> en
    /// <c>BookingCancellationLineTreasuryFxAdjustments</c>. El nombre viejo mentia: bajo M1 lectura (i)
    /// (CONFIRMADO por Gaston 2026-07-10) el TC definitivo del cargo NO es el del dia de emision de la ND, sino
    /// el del DIA EN QUE EL OPERADOR COBRO su cargo. La T3b3 original habia creado la columna con el nombre viejo
    /// (deployada en prod, commit 5c1d39a); esta migracion NUEVA la renombra en vez de editar T3b3 in-place
    /// (EF no re-corre migraciones ya aplicadas: editar T3b3 dejaria la columna de prod con el nombre viejo y el
    /// modelo esperando el nuevo -&gt; "column does not exist" en toda query a la tabla).
    ///
    /// <para><b>Seguridad</b>: la tabla esta VACIA en prod (T3b3 recien se deployo y no hubo aun ninguna
    /// liquidacion con conversion que genere filas), asi que el <c>RenameColumn</c> es trivial; pero aunque
    /// tuviera datos, un rename los preserva (no es drop+add). <c>Down()</c> revierte al nombre viejo.</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura)</b> — confirma que la columna quedo renombrada:
    /// <code>
    /// SELECT column_name FROM information_schema.columns
    /// WHERE table_name = 'BookingCancellationLineTreasuryFxAdjustments'
    ///   AND column_name IN ('RateAtNdEmission', 'RateAtChargeDay');
    /// -- esperado: 1 fila, 'RateAtChargeDay' (la vieja 'RateAtNdEmission' ya no existe)
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T4a_RenameRateAtNdEmissionToRateAtChargeDay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RateAtNdEmission",
                table: "BookingCancellationLineTreasuryFxAdjustments",
                newName: "RateAtChargeDay");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "RateAtChargeDay",
                table: "BookingCancellationLineTreasuryFxAdjustments",
                newName: "RateAtNdEmission");
        }
    }
}
