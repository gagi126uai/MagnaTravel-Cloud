using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr044_M_Undo1_AddBookingCancellationDebitNoteAnnulment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceDebitNoteAnnulmentId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingCancellationDebitNoteAnnulments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingCancellationId = table.Column<int>(type: "integer", nullable: false),
                    AnnulledDebitNoteInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    AnnulmentCreditNoteInvoiceId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    RequestedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    RequestedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ArcaErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCancellationDebitNoteAnnulments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingCancellationDebitNoteAnnulments_BookingCancellations~",
                        column: x => x.BookingCancellationId,
                        principalTable: "BookingCancellations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingCancellationDebitNoteAnnulments_Invoices_AnnulledDeb~",
                        column: x => x.AnnulledDebitNoteInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingCancellationDebitNoteAnnulments_Invoices_AnnulmentCr~",
                        column: x => x.AnnulmentCreditNoteInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            // Endurecimiento (post-gate 2026-07-14): índice ÚNICO PARCIAL (WHERE not null) — a lo sumo UN crédito
            // por evento de deshacer. Red dura de la idempotencia del minteo B1 ante retry de Hangfire (Postgres
            // rechaza el segundo con 23505). Parcial para no chocar con los créditos de otros orígenes (null).
            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_SourceDebitNoteAnnulment_OnePerEvent",
                table: "ClientCreditEntries",
                column: "SourceDebitNoteAnnulmentId",
                unique: true,
                filter: "\"SourceDebitNoteAnnulmentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationDebitNoteAnnulments_AnnulmentCreditNoteI~",
                table: "BookingCancellationDebitNoteAnnulments",
                column: "AnnulmentCreditNoteInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationDebitNoteAnnulments_BookingCancellationId",
                table: "BookingCancellationDebitNoteAnnulments",
                column: "BookingCancellationId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationDebitNoteAnnulments_OneLivePerDebitNote",
                table: "BookingCancellationDebitNoteAnnulments",
                column: "AnnulledDebitNoteInvoiceId",
                unique: true,
                filter: "\"Status\" <> 2");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationDebitNoteAnnulments_PublicId",
                table: "BookingCancellationDebitNoteAnnulments",
                column: "PublicId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCreditEntries_BookingCancellationDebitNoteAnnulments_~",
                table: "ClientCreditEntries",
                column: "SourceDebitNoteAnnulmentId",
                principalTable: "BookingCancellationDebitNoteAnnulments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientCreditEntries_BookingCancellationDebitNoteAnnulments_~",
                table: "ClientCreditEntries");

            migrationBuilder.DropTable(
                name: "BookingCancellationDebitNoteAnnulments");

            migrationBuilder.DropIndex(
                name: "IX_ClientCreditEntries_SourceDebitNoteAnnulment_OnePerEvent",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "SourceDebitNoteAnnulmentId",
                table: "ClientCreditEntries");
        }
    }
}
