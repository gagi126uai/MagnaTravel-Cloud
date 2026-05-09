using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// B1.15 Fase 2a (FIX 6): agrega trazabilidad fiscal de la anulacion de facturas.
    ///
    /// Columnas nuevas en <c>Invoices</c>:
    ///  - <c>AnnulledByUserId</c> (string?, FK SetNull a AspNetUsers).
    ///  - <c>AnnulledByUserName</c> (string?, max 200) — snapshot del nombre.
    ///  - <c>AnnulledAt</c> (DateTime?) — timestamp de la NC aprobada por AFIP.
    ///  - <c>AnnulmentReason</c> (string?, max 500) — motivo declarado en el request.
    ///  - <c>AnnulmentStatus</c> (int, default 0=None) — None/Pending/Succeeded/Failed.
    ///
    /// Backfill: NO. Las facturas historicas quedan en <c>AnnulmentStatus = None</c>
    /// (default columna). No hay forma confiable de inferir si una factura previa
    /// fue anulada via NC sin parsear el grafo de OriginalInvoiceId, y eso quedaria
    /// con atribucion <c>(legacy)</c> sin valor probatorio.
    ///
    /// Ver tambien la migracion <c>20260507035037_AddAuditFieldsToInvoiceAndPayment</c>
    /// que agrego IssuedBy*. Patron consistente: nullable + FK SetNull.
    /// </summary>
    public partial class AddInvoiceAnnulmentAuditFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AnnulledAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnnulledByUserId",
                table: "Invoices",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnnulledByUserName",
                table: "Invoices",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AnnulmentReason",
                table: "Invoices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AnnulmentStatus",
                table: "Invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_AnnulledByUserId",
                table: "Invoices",
                column: "AnnulledByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_AspNetUsers_AnnulledByUserId",
                table: "Invoices",
                column: "AnnulledByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_AspNetUsers_AnnulledByUserId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_AnnulledByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AnnulledAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AnnulledByUserId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AnnulledByUserName",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AnnulmentReason",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "AnnulmentStatus",
                table: "Invoices");
        }
    }
}
