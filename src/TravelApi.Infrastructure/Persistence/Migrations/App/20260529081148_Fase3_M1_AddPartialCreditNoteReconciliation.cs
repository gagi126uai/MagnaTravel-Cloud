using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.3 Fase 3 (ADR-010, 2026-05-29): crea las dos tablas de la bandeja de
    /// reconciliacion de NC parciales con recibos vivos.
    ///
    /// <para><b>Que crea</b>:</para>
    /// <list type="bullet">
    /// <item><c>PartialCreditNoteReconciliations</c> (el caso): FK Restrict a la NC y a
    /// la factura original (evidencia fiscal, no se borra en cascada), FK SetNull a la
    /// reserva (tabla fisica "TravelFiles"), indice UNICO en <c>CreditNoteInvoiceId</c>
    /// (un caso por NC = red de defensa de idempotencia, ADR-010 B2), xmin como
    /// concurrency token.</item>
    /// <item><c>PartialCreditNoteReconciliationReceipts</c> (snapshot de recibos vivos):
    /// FK Cascade al caso padre, FK Restrict al PaymentReceipt real.</item>
    /// </list>
    ///
    /// <para><b>CHECK constraints SQL crudo</b> (Postgres, comillas dobles). El
    /// interceptor mapea SqlState 23514 a 409:</para>
    /// <list type="bullet">
    /// <item><c>chk_pcnr_status</c>: Status solo puede ser 'Pending' o 'Resolved'.</item>
    /// <item><c>chk_pcnr_resolved_consistency</c>: si Status='Resolved', entonces
    /// ResolvedAt y ResolvedByUserId NOT NULL (no se cierra sin trazabilidad).</item>
    /// </list>
    ///
    /// <para><b>100% aditiva</b>. No toca tablas existentes. Sin backfill (decision D3:
    /// solo casos nuevos). El <c>Down()</c> dropea las dos tablas; como nada apunta hacia
    /// ellas, el rollback es limpio. Los recibos reales NO se pierden (viven en
    /// PaymentReceipts). La NC ya emitida es independiente de estas tablas.</para>
    /// </summary>
    public partial class Fase3_M1_AddPartialCreditNoteReconciliation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PartialCreditNoteReconciliations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreditNoteInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    OriginalInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: true),
                    FiscalAmountCredited = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OpenedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    OpenedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    OpenedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ResolvedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ResolvedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ResolutionNotes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ClosedWithLiveReceipts = table.Column<bool>(type: "boolean", nullable: false),
                    FourEyesBypassApplied = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartialCreditNoteReconciliations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartialCreditNoteReconciliations_Invoices_CreditNoteInvoice~",
                        column: x => x.CreditNoteInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartialCreditNoteReconciliations_Invoices_OriginalInvoiceId",
                        column: x => x.OriginalInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PartialCreditNoteReconciliations_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PartialCreditNoteReconciliationReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReconciliationId = table.Column<int>(type: "integer", nullable: false),
                    PaymentReceiptId = table.Column<int>(type: "integer", nullable: false),
                    PaymentId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    StatusAtOpen = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartialCreditNoteReconciliationReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartialCreditNoteReconciliationReceipts_PartialCreditNoteRe~",
                        column: x => x.ReconciliationId,
                        principalTable: "PartialCreditNoteReconciliations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PartialCreditNoteReconciliationReceipts_PaymentReceipts_Pay~",
                        column: x => x.PaymentReceiptId,
                        principalTable: "PaymentReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PartialCreditNoteReconciliationReceipts_PaymentReceiptId",
                table: "PartialCreditNoteReconciliationReceipts",
                column: "PaymentReceiptId");

            migrationBuilder.CreateIndex(
                name: "IX_PartialCreditNoteReconciliationReceipts_ReconciliationId",
                table: "PartialCreditNoteReconciliationReceipts",
                column: "ReconciliationId");

            migrationBuilder.CreateIndex(
                name: "IX_PartialCreditNoteReconciliations_CreditNoteInvoiceId",
                table: "PartialCreditNoteReconciliations",
                column: "CreditNoteInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartialCreditNoteReconciliations_OriginalInvoiceId",
                table: "PartialCreditNoteReconciliations",
                column: "OriginalInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PartialCreditNoteReconciliations_PublicId",
                table: "PartialCreditNoteReconciliations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PartialCreditNoteReconciliations_ReservaId",
                table: "PartialCreditNoteReconciliations",
                column: "ReservaId");

            // ============================================================
            // CHECK constraints SQL crudo (ADR-010 §4). Sintaxis PostgreSQL:
            // identificadores con comillas dobles. El interceptor
            // BusinessInvariantInterceptor mapea SqlState='23514' (CHECK violation)
            // a 409, igual que el resto del modulo FC1.3.
            // ============================================================

            // chk_pcnr_status: Status solo puede ser uno de los dos valores del enum
            // (persistido como string). Bloquea que un bug escriba un estado invalido.
            migrationBuilder.Sql("""
                ALTER TABLE "PartialCreditNoteReconciliations"
                  DROP CONSTRAINT IF EXISTS chk_pcnr_status;
                ALTER TABLE "PartialCreditNoteReconciliations"
                  ADD CONSTRAINT chk_pcnr_status
                  CHECK ("Status" IN ('Pending', 'Resolved'));
                """);

            // chk_pcnr_resolved_consistency: un caso Resolved DEBE tener trazabilidad de
            // cierre (cuando + quien). No se puede marcar resuelto sin dejar el rastro.
            // El service siempre los setea juntos; este CHECK es la red de seguridad de la BD.
            migrationBuilder.Sql("""
                ALTER TABLE "PartialCreditNoteReconciliations"
                  DROP CONSTRAINT IF EXISTS chk_pcnr_resolved_consistency;
                ALTER TABLE "PartialCreditNoteReconciliations"
                  ADD CONSTRAINT chk_pcnr_resolved_consistency
                  CHECK (
                    "Status" <> 'Resolved'
                    OR ("ResolvedAt" IS NOT NULL AND "ResolvedByUserId" IS NOT NULL)
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartialCreditNoteReconciliationReceipts");

            migrationBuilder.DropTable(
                name: "PartialCreditNoteReconciliations");
        }
    }
}
