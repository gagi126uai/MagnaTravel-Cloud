using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-024 item 4 (vinculo basico cobro&lt;-&gt;factura, ex ADR-023 T2.6, 2026-06-12): migracion ADITIVA,
    /// 100% generada por EF (sin SQL crudo — leccion del repo: el SQL crudo solo se prueba de verdad en
    /// Postgres). Agrega <c>Payments.LinkedInvoiceId</c> (int? nullable) + FK a Invoices (SetNull) +
    /// indice de consulta NO unico (IX_Payments_LinkedInvoiceId): una factura puede tener varios cobros
    /// vinculados. Sin backfill: la columna nace NULL en todas las filas existentes (ningun cobro legacy
    /// tiene vinculo). El vinculo es INFORMATIVO: los guards de borrado/edicion y la reconciliacion de NC
    /// NO lo miran (review B1 de ADR-023), asi que agregarlo no congela cobros ni toca saldos. <b>Down</b>
    /// dropea FK + indice + columna. Probar Up/Down en Postgres real antes de mergear.
    /// </summary>
    public partial class Adr024_M1_AddLinkedInvoiceToPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LinkedInvoiceId",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LinkedInvoiceId",
                table: "Payments",
                column: "LinkedInvoiceId");

            migrationBuilder.AddForeignKey(
                name: "FK_Payments_Invoices_LinkedInvoiceId",
                table: "Payments",
                column: "LinkedInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Payments_Invoices_LinkedInvoiceId",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_LinkedInvoiceId",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "LinkedInvoiceId",
                table: "Payments");
        }
    }
}
