using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC4 (saldo a favor aplicado a otra reserva, 2026-06-14): migracion ADITIVA, 100% generada por EF (sin
    /// SQL crudo — leccion del repo: el SQL crudo solo se prueba de verdad en Postgres). Agrega
    /// <c>Payments.AppliedFromCreditWithdrawalId</c> (int? nullable) + FK a <c>ClientCreditWithdrawals</c>
    /// (Restrict) + indice de consulta NO unico. La columna nace NULL en todas las filas existentes (ningun
    /// pago legacy es un puente de aplicacion); no hay backfill. Restrict: no se puede borrar un withdrawal
    /// mientras exista el Payment puente que lo respalda (preserva la trazabilidad credito->pago). <b>Down</b>
    /// dropea FK + indice + columna. Probar Up/Down en Postgres real antes de mergear.
    /// </summary>
    public partial class Adr030_M1_AddAppliedCreditBridgeToPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AppliedFromCreditWithdrawalId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AppliedFromCreditWithdrawalId",
                table: "Payments",
                column: "AppliedFromCreditWithdrawalId");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_ClientCreditWithdrawals_AppliedFromCreditWithdrawa~",
                table: "Payments",
                column: "AppliedFromCreditWithdrawalId",
                principalTable: "ClientCreditWithdrawals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_ClientCreditWithdrawals_AppliedFromCreditWithdrawa~",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_AppliedFromCreditWithdrawalId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "AppliedFromCreditWithdrawalId",
                table: "Payments");
        }
    }
}
