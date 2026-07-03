using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-042 §3.1/§6/§7 (2026-07-01): tabla hija <c>BookingCancellationCreditNotes</c> (una por factura ->
    /// su NC) para anular reservas multi-factura multimoneda, + columna <c>Invoices.CanMisMonExt</c> (valor
    /// fiscal congelado al emitir). ADITIVA: no toca <c>BookingCancellations</c> ni sus punteros singulares
    /// (el caso mono-factura queda byte-equivalente).
    ///
    /// <para><b>Backfill</b> (dentro del Up, patron del proyecto para datos fiscales):
    /// <list type="bullet">
    ///   <item><c>Invoices.CanMisMonExt = 'N'</c> para las facturas en moneda extranjera existentes (es lo
    ///         que de hecho se emitio: divisa -> "N"). Pesos quedan NULL (no aplica el nodo).</item>
    ///   <item>Una fila hija por cada BC con NC en juego: <c>Succeeded</c> si tiene NC principal viva,
    ///         <c>Pending</c> si esta esperando confirmacion fiscal, <c>Failed</c> si ArcaRejected. Los BC sin
    ///         NC en juego (Drafted/Aborted/ManualReview*) NO reciben hija: el codigo los trata como legacy via
    ///         el puntero singular, y al confirmar crean su hija fresca.</item>
    /// </list></para>
    ///
    /// <para><b>Rollback</b>: <c>Down</c> dropea la tabla + la columna. HUECO documentado (§6): si ya se
    /// emitieron anulaciones multi-factura entre el Up y el Down, se pierden las hijas NO-principales (las
    /// Invoices NC siguen existiendo; se pierde solo el vinculo hijo<->BC). Solo revertir si NO hubo
    /// multi-factura en el medio.</para>
    /// </summary>
    public partial class Adr042_M1_AddBookingCancellationCreditNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanMisMonExt",
                table: "Invoices",
                type: "character varying(1)",
                maxLength: 1,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingCancellationCreditNotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingCancellationId = table.Column<int>(type: "integer", nullable: false),
                    OriginatingInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    CreditNoteInvoiceId = table.Column<int>(type: "integer", nullable: true),
                    ArcaCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ArcaErrorMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCancellationCreditNotes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingCancellationCreditNotes_BookingCancellations_Booking~",
                        column: x => x.BookingCancellationId,
                        principalTable: "BookingCancellations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingCancellationCreditNotes_Invoices_CreditNoteInvoiceId",
                        column: x => x.CreditNoteInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookingCancellationCreditNotes_Invoices_OriginatingInvoiceId",
                        column: x => x.OriginatingInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationCreditNotes_Bc_OriginatingInvoice",
                table: "BookingCancellationCreditNotes",
                columns: new[] { "BookingCancellationId", "OriginatingInvoiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationCreditNotes_CreditNoteInvoiceId",
                table: "BookingCancellationCreditNotes",
                column: "CreditNoteInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationCreditNotes_OriginatingInvoiceId",
                table: "BookingCancellationCreditNotes",
                column: "OriginatingInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationCreditNotes_PublicId",
                table: "BookingCancellationCreditNotes",
                column: "PublicId",
                unique: true);

            // ===== BACKFILL (ADR-042 §6/§7) =====

            // (1) CanMisMonExt = 'N' para facturas en moneda extranjera existentes (lo que ya se emitio).
            //     Pesos (MonId PES / NULL) quedan NULL: el nodo no aplica. Idempotente (solo divisas).
            migrationBuilder.Sql(@"
                UPDATE ""Invoices""
                SET ""CanMisMonExt"" = 'N'
                WHERE ""MonId"" IS NOT NULL
                  AND upper(""MonId"") <> 'PES';");

            // (2) Una fila hija por BC con NC en juego. gen_random_uuid() (PG13+ core) para el PublicId.
            //     Status: 1=Succeeded (tiene NC principal viva), 2=Failed (ArcaRejected sin NC), 0=Pending
            //     (AwaitingFiscalConfirmation). ArcaCurrency = MonId de la factura origen (default PES).
            //     Solo BCs con CreditNoteInvoiceId no-null O en estado 1 (AwaitingFiscalConfirmation) / 7
            //     (ArcaRejected): los demas (Drafted/Aborted/ManualReview*) no tienen NC en juego.
            migrationBuilder.Sql(@"
                INSERT INTO ""BookingCancellationCreditNotes""
                    (""PublicId"", ""BookingCancellationId"", ""OriginatingInvoiceId"", ""CreditNoteInvoiceId"",
                     ""ArcaCurrency"", ""Status"", ""ArcaErrorMessage"", ""CreatedAt"")
                SELECT
                    gen_random_uuid(),
                    bc.""Id"",
                    bc.""OriginatingInvoiceId"",
                    bc.""CreditNoteInvoiceId"",
                    COALESCE(inv.""MonId"", 'PES'),
                    CASE
                        WHEN bc.""CreditNoteInvoiceId"" IS NOT NULL THEN 1
                        WHEN bc.""Status"" = 7 THEN 2
                        ELSE 0
                    END,
                    CASE WHEN bc.""Status"" = 7 THEN bc.""ArcaErrorMessage"" ELSE NULL END,
                    COALESCE(bc.""ConfirmedWithClientAt"", bc.""DraftedAt"", now())
                FROM ""BookingCancellations"" bc
                JOIN ""Invoices"" inv ON inv.""Id"" = bc.""OriginatingInvoiceId""
                WHERE bc.""CreditNoteInvoiceId"" IS NOT NULL
                   OR bc.""Status"" IN (1, 7);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCancellationCreditNotes");

            migrationBuilder.DropColumn(
                name: "CanMisMonExt",
                table: "Invoices");
        }
    }
}
