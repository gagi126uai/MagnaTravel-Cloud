using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.2.0 (plan tactico v3, 2026-05-17): preparacion del modulo de servicios
    /// FC1.2 (BookingCancellationService, OperatorRefundService, ClientCreditService).
    ///
    /// Agrega:
    ///   - 5 columnas a <c>OperationalFinanceSettings</c> (flag + politicas).
    ///   - Columna <c>AnnulmentApprovalRequestId</c> en <c>Invoices</c> con FK
    ///     opcional a <c>ApprovalRequests</c> (BR-V2-03 cross-reference fiscal).
    ///
    /// **Defaults intencionales** (los defaults C# solo aplican a entidades nuevas
    /// creadas en memoria; en una <c>AddColumn</c> sobre filas existentes EF aplica
    /// el <c>defaultValue</c> de la migracion). Por eso aca seteamos explicitamente
    /// los valores del plan v3 §10.1 para que la unica fila de
    /// <c>OperationalFinanceSettings</c> en prod quede coherente sin necesidad de
    /// un seed adicional:
    ///   - EnableNewCancellationFlow = false (feature flag OFF en prod).
    ///   - OnePerReservaInvoicePolicy = true (precondicion INV-100 activa).
    ///   - OperatorRefundTimeoutDays = 60 (recomendacion plan v3 §10.1).
    ///   - Ley25345ThresholdAmount = 1.000.000 ARS (confirmar con contador).
    ///   - PhysicalRefundAlertThreshold = 50.000 ARS (alerta admin).
    ///
    /// Rollback: <c>Down()</c> dropea las 5 columnas + FK + indice. Aditiva: los
    /// datos pre-migracion no se tocan; rollback no pierde nada.
    /// </summary>
    public partial class FC1_2_0_AddSettingsAndApprovalRequestFK : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Feature flag maestro FC1.2. OFF por default — habilitar solo tras
            // signoff fiscal OPS-FISCAL-001 (ver §13 del plan v3).
            migrationBuilder.AddColumn<bool>(
                name: "EnableNewCancellationFlow",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Ley 25.345: umbral en ARS para validaciones reforzadas de
            // retiros fisicos. Default 1.000.000 (revisar con contador real).
            migrationBuilder.AddColumn<decimal>(
                name: "Ley25345ThresholdAmount",
                table: "OperationalFinanceSettings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 1000000m);

            // INV-100: una sola cancelacion por reserva activa. Default ON.
            migrationBuilder.AddColumn<bool>(
                name: "OnePerReservaInvoicePolicy",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Dias hasta que el job nocturno abandone la BC por falta de refund.
            // Default 60 (practica retail). Plan v3 §10.1.
            migrationBuilder.AddColumn<int>(
                name: "OperatorRefundTimeoutDays",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 60);

            // Alerta admin sobre retiros fisicos grandes. Informativo, no bloquea.
            migrationBuilder.AddColumn<decimal>(
                name: "PhysicalRefundAlertThreshold",
                table: "OperationalFinanceSettings",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 50000m);

            // BR-V2-03 cross-reference fiscal: FK opcional al ApprovalRequest
            // (InvariantOverride) que autorizo la annulacion de la NC. Null para
            // annulaciones legacy / back-office sin BC asociado.
            migrationBuilder.AddColumn<int>(
                name: "AnnulmentApprovalRequestId",
                table: "Invoices",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AnnulmentApprovalRequestId",
                table: "Invoices",
                column: "AnnulmentApprovalRequestId");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_ApprovalRequests_AnnulmentApprovalRequestId",
                table: "Invoices",
                column: "AnnulmentApprovalRequestId",
                principalTable: "ApprovalRequests",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_ApprovalRequests_AnnulmentApprovalRequestId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_AnnulmentApprovalRequestId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "EnableNewCancellationFlow",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "Ley25345ThresholdAmount",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "OnePerReservaInvoicePolicy",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "OperatorRefundTimeoutDays",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "PhysicalRefundAlertThreshold",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "AnnulmentApprovalRequestId",
                table: "Invoices");
        }
    }
}
