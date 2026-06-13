using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-025 (DT.1, 2026-06-13): tabla nueva <c>BookingCancellationLines</c> (lineas hijas del BC).
    /// 100% ADITIVA y EF PURA (sin SQL crudo — leccion del trap M2 de ADR-020 que rompio prod por una
    /// columna que solo existia InMemory): no toca <c>BookingCancellations</c> ni el UNIQUE INV-081.
    ///
    /// <para>Habilita el modelo BC-padre + lineas: cancelar UN servicio (Scope=Partial) y cancelar un
    /// file multi-operador (N lineas Full, levanta INV-152). Cada linea lleva operador, moneda, penalidad
    /// y circuito de refund a nivel linea; la cara fiscal hacia el cliente (factura/NC) sigue UNICA en el
    /// padre. Ver <c>BookingCancellationLine</c>.</para>
    ///
    /// <para><b>Numeracion</b>: es Adr028_M1 (no Adr025) porque Adr025=vencimientos, Adr026=comisiones y
    /// Adr027=confirmado-con-cambios ya estaban tomadas cuando se construyo. La numeracion de migracion
    /// NO sigue la del ADR: van por orden de construccion (M2 del review del diseño).</para>
    ///
    /// <para><b>Backfill</b>: NO va en este <c>Up</c> (no SQL crudo). Lo corre
    /// <c>BookingCancellationLineBackfillService</c> post-migrate (1 linea sintetica por BC historico,
    /// centinela ServiceId=0), idempotente. Ver ese servicio.</para>
    ///
    /// <para><b>Rollback</b>: <c>Down</c> dropea la tabla. Sin perdida de dato fiscal: el padre BC y sus
    /// facturas/NC no se tocan.</para>
    /// </summary>
    public partial class Adr028_M1_AddBookingCancellationLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingCancellationLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingCancellationId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    ServiceTable = table.Column<int>(type: "integer", nullable: false),
                    ServiceId = table.Column<int>(type: "integer", nullable: false),
                    Scope = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    LineSaleAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ConceptKind = table.Column<int>(type: "integer", nullable: false),
                    PenaltyStatus = table.Column<int>(type: "integer", nullable: false),
                    PenaltyAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    PenaltyConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OperatorPenaltyConfirmedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConceptClassifiedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ConceptClassifiedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConceptClassifiedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DebitNoteInvoiceId = table.Column<int>(type: "integer", nullable: true),
                    DebitNoteStatus = table.Column<int>(type: "integer", nullable: false),
                    DebitNoteArcaErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RefundCap = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReceivedRefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RefundStatus = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCancellationLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingCancellationLines_BookingCancellations_BookingCancel~",
                        column: x => x.BookingCancellationId,
                        principalTable: "BookingCancellations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingCancellationLines_Invoices_DebitNoteInvoiceId",
                        column: x => x.DebitNoteInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookingCancellationLines_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLines_BookingCancellationId",
                table: "BookingCancellationLines",
                column: "BookingCancellationId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLines_DebitNoteInvoiceId",
                table: "BookingCancellationLines",
                column: "DebitNoteInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLines_PublicId",
                table: "BookingCancellationLines",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLines_SupplierId",
                table: "BookingCancellationLines",
                column: "SupplierId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCancellationLines");
        }
    }
}
