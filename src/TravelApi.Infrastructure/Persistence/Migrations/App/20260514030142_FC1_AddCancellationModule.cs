using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1 (ADR-002, 2026-05-14): primer paso del modulo de cancelacion/refund.
    ///
    /// Crea:
    ///   - 6 tablas nuevas (BookingCancellations, OperatorRefundsReceived,
    ///     OperatorRefundAllocations, DeductionLines, ClientCreditEntries,
    ///     ClientCreditWithdrawals) con sus FKs e indices.
    ///   - Columnas <c>ClientCreditWithdrawalId</c> y <c>OperatorRefundReceivedId</c>
    ///     (nullable) en <c>ManualCashMovements</c> para que los movimientos
    ///     de caja generados por T2/T3 enlacen al modulo.
    ///   - Columna <c>LastArcaAttemptAt</c> (nullable) en <c>Invoices</c> para
    ///     el job <c>ArcaAnnulmentReconciliationJob</c> (FC2).
    ///
    /// Concurrencia (ADR-002 §2.3.4 / B11):
    ///   - <c>xmin</c> shadow column con <c>rowVersion: true</c> autogenerada por
    ///     EF a partir de <c>UseXminAsConcurrencyToken()</c> en
    ///     <c>BookingCancellation</c>, <c>OperatorRefundReceived</c> y
    ///     <c>ClientCreditEntry</c>. <c>xmin</c> es una columna pseudo-de-sistema
    ///     de Postgres (id de transaccion que modifico la fila), por lo que NO
    ///     se crea fisicamente — el rowVersion=true le dice a EF que la lea como
    ///     concurrency token.
    ///
    /// CHECK constraints SQL (ADR-002 §2.3.3, convencion <c>chk_&lt;tabla&gt;_&lt;concepto&gt;</c>):
    ///   - <c>chk_OperatorRefundsReceived_allocated_not_exceeds</c>: el cache
    ///     denormalizado AllocatedAmount NUNCA puede superar el monto recibido.
    ///   - <c>chk_ClientCreditEntries_remaining_non_negative</c>: el saldo cliente
    ///     nunca queda negativo ni supera el monto inicial.
    ///   - <c>chk_OperatorRefundAllocations_net_positive</c>: deducciones no pueden
    ///     poner el neto en negativo.
    ///   - <c>chk_DeductionLines_amount_positive</c>: no se admiten deducciones
    ///     cero/negativas.
    ///   - <c>chk_TravelFiles_status_valid</c>: solo valores reconocidos en
    ///     <c>TravelFiles.Status</c> (la tabla persiste la entidad <c>Reserva</c>).
    ///   - <c>chk_BookingCancellations_fiscalsnapshot_consistent</c>: BC en estados
    ///     &gt;= AwaitingFiscalConfirmation requiere FiscalSnapshot completo (INV-118).
    ///
    /// Unique partial index (sin API fluida en EF):
    ///   - <c>ix_OperatorRefundAllocations_active_unique_alloc_per_refund_per_bc</c>:
    ///     1 sola allocation NO-voided por pareja (refund, bookingCancellation).
    ///     Permite re-vincular si la cashier se equivoco — la fila vieja queda
    ///     <c>IsVoided=true</c> y la nueva entra porque el indice la excluye.
    ///
    /// Down() revierte los CHECK + el CHECK existente de Reservas.Status (si lo hubiera).
    /// </summary>
    public partial class FC1_AddCancellationModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClientCreditWithdrawalId",
                table: "ManualCashMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OperatorRefundReceivedId",
                table: "ManualCashMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastArcaAttemptAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookingCancellations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    OriginatingInvoiceId = table.Column<int>(type: "integer", nullable: false),
                    CreditNoteInvoiceId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DraftedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedWithClientAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OperatorRequestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    OperatorRefundDueBy = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DraftedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    DraftedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    ConfirmedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AmountPaidAtCancellation = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    EstimatedRefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ReceivedRefundAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FiscalSnapshot_CustomerTaxIdAtEvent = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FiscalSnapshot_CustomerTaxConditionAtEvent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FiscalSnapshot_SupplierTaxIdAtEvent = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    FiscalSnapshot_SupplierTaxConditionAtEvent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FiscalSnapshot_AgencyTaxConditionAtEvent = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    FiscalSnapshot_CurrencyAtEvent = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    FiscalSnapshot_ExchangeRateAtOriginalInvoice = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    FiscalSnapshot_ExchangeRateAtOperatorRefundReceipt = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    FiscalSnapshot_ExchangeRateAtClientWithdrawal = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    FiscalSnapshot_Source = table.Column<int>(type: "integer", nullable: false),
                    FiscalSnapshot_FetchedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FiscalSnapshot_ManualJustification = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FiscalSnapshot_ExtrasJson = table.Column<string>(type: "text", nullable: true),
                    IsLegacyPreCancellationModel = table.Column<bool>(type: "boolean", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCancellations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingCancellations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingCancellations_Invoices_CreditNoteInvoiceId",
                        column: x => x.CreditNoteInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_BookingCancellations_Invoices_OriginatingInvoiceId",
                        column: x => x.OriginatingInvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingCancellations_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingCancellations_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperatorRefundsReceived",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceivedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AllocatedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRateAtReceipt = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ReceivedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ReceivedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorRefundsReceived", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperatorRefundsReceived_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OperatorRefundAllocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorRefundReceivedId = table.Column<int>(type: "integer", nullable: false),
                    BookingCancellationId = table.Column<int>(type: "integer", nullable: false),
                    GrossAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsVoided = table.Column<bool>(type: "boolean", nullable: false),
                    VoidsAllocationId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    AccountingEntryRef = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatorRefundAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperatorRefundAllocations_BookingCancellations_BookingCance~",
                        column: x => x.BookingCancellationId,
                        principalTable: "BookingCancellations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OperatorRefundAllocations_OperatorRefundAllocations_VoidsAl~",
                        column: x => x.VoidsAllocationId,
                        principalTable: "OperatorRefundAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_OperatorRefundAllocations_OperatorRefundsReceived_OperatorR~",
                        column: x => x.OperatorRefundReceivedId,
                        principalTable: "OperatorRefundsReceived",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClientCreditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    OperatorRefundAllocationId = table.Column<int>(type: "integer", nullable: false),
                    BookingCancellationId = table.Column<int>(type: "integer", nullable: false),
                    CreditedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RemainingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsFullyConsumed = table.Column<bool>(type: "boolean", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCreditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCreditEntries_BookingCancellations_BookingCancellatio~",
                        column: x => x.BookingCancellationId,
                        principalTable: "BookingCancellations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientCreditEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientCreditEntries_OperatorRefundAllocations_OperatorRefun~",
                        column: x => x.OperatorRefundAllocationId,
                        principalTable: "OperatorRefundAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeductionLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorRefundAllocationId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CertificateNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CertificatePdfUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CertificateDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Jurisdiction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ForeignCountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SupportingDocumentRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    JustificationComment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    MissingFiscalSupport = table.Column<bool>(type: "boolean", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RequiresAccountingReview = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeductionLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeductionLines_OperatorRefundAllocations_OperatorRefundAllo~",
                        column: x => x.OperatorRefundAllocationId,
                        principalTable: "OperatorRefundAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClientCreditWithdrawals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientCreditEntryId = table.Column<int>(type: "integer", nullable: false),
                    ManualCashMovementId = table.Column<int>(type: "integer", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ExecutedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ExecutedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ExecutedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ApprovalRequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientCreditWithdrawals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClientCreditWithdrawals_ClientCreditEntries_ClientCreditEnt~",
                        column: x => x.ClientCreditEntryId,
                        principalTable: "ClientCreditEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ClientCreditWithdrawals_ManualCashMovements_ManualCashMovem~",
                        column: x => x.ManualCashMovementId,
                        principalTable: "ManualCashMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ManualCashMovements_ClientCreditWithdrawalId",
                table: "ManualCashMovements",
                column: "ClientCreditWithdrawalId");

            migrationBuilder.CreateIndex(
                name: "IX_ManualCashMovements_OperatorRefundReceivedId",
                table: "ManualCashMovements",
                column: "OperatorRefundReceivedId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_CreditNoteInvoiceId",
                table: "BookingCancellations",
                column: "CreditNoteInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_CustomerId",
                table: "BookingCancellations",
                column: "CustomerId");

            // INV-100 (review BR4, 2026-05-14): UNIQUE para que la misma factura
            // original no pueda originar dos cancelaciones distintas. Sin esto,
            // un error en el flow podria emitir dos NCs sobre la misma factura A.
            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations",
                column: "OriginatingInvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_PublicId",
                table: "BookingCancellations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_ReservaId",
                table: "BookingCancellations",
                column: "ReservaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_SupplierId",
                table: "BookingCancellations",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_BookingCancellationId",
                table: "ClientCreditEntries",
                column: "BookingCancellationId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_CustomerId",
                table: "ClientCreditEntries",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_CustomerId_IsFullyConsumed",
                table: "ClientCreditEntries",
                columns: new[] { "CustomerId", "IsFullyConsumed" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_OperatorRefundAllocationId",
                table: "ClientCreditEntries",
                column: "OperatorRefundAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_PublicId",
                table: "ClientCreditEntries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditWithdrawals_ClientCreditEntryId",
                table: "ClientCreditWithdrawals",
                column: "ClientCreditEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditWithdrawals_ExecutedAt",
                table: "ClientCreditWithdrawals",
                column: "ExecutedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditWithdrawals_ManualCashMovementId",
                table: "ClientCreditWithdrawals",
                column: "ManualCashMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditWithdrawals_PublicId",
                table: "ClientCreditWithdrawals",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeductionLines_Kind",
                table: "DeductionLines",
                column: "Kind");

            migrationBuilder.CreateIndex(
                name: "IX_DeductionLines_OperatorRefundAllocationId",
                table: "DeductionLines",
                column: "OperatorRefundAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_DeductionLines_PublicId",
                table: "DeductionLines",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundAllocations_BookingCancellationId",
                table: "OperatorRefundAllocations",
                column: "BookingCancellationId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundAllocations_OperatorRefundReceivedId",
                table: "OperatorRefundAllocations",
                column: "OperatorRefundReceivedId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundAllocations_PublicId",
                table: "OperatorRefundAllocations",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundAllocations_VoidsAllocationId",
                table: "OperatorRefundAllocations",
                column: "VoidsAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundsReceived_PublicId",
                table: "OperatorRefundsReceived",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundsReceived_ReceivedAt",
                table: "OperatorRefundsReceived",
                column: "ReceivedAt");

            migrationBuilder.CreateIndex(
                name: "IX_OperatorRefundsReceived_SupplierId",
                table: "OperatorRefundsReceived",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_ManualCashMovements_ClientCreditWithdrawals_ClientCreditWit~",
                table: "ManualCashMovements",
                column: "ClientCreditWithdrawalId",
                principalTable: "ClientCreditWithdrawals",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_ManualCashMovements_OperatorRefundsReceived_OperatorRefundR~",
                table: "ManualCashMovements",
                column: "OperatorRefundReceivedId",
                principalTable: "OperatorRefundsReceived",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // ============================================================
            //   CHECK constraints SQL — convencion del proyecto:
            //     chk_<tabla>_<concepto> (review BR1, 2026-05-14)
            //   El interceptor SaveChangesInterceptor mapea
            //   PostgresException.SqlState='23514' (check_violation) +
            //   ConstraintName a BusinessInvariantViolationException -> HTTP 409.
            //   Idempotencia local: DROP IF EXISTS + ADD para soportar replay
            //   contra una BD parcial sin romper.
            // ============================================================

            // (a) AllocatedAmount NUNCA supera ReceivedAmount (INV-084 / INV-114).
            //     Critico para concurrencia lock-free N:M (ADR-002 §2.5).
            migrationBuilder.Sql("""
                ALTER TABLE "OperatorRefundsReceived"
                  DROP CONSTRAINT IF EXISTS chk_OperatorRefundsReceived_allocated_not_exceeds;
                ALTER TABLE "OperatorRefundsReceived"
                  ADD CONSTRAINT chk_OperatorRefundsReceived_allocated_not_exceeds
                  CHECK ("AllocatedAmount" >= 0 AND "AllocatedAmount" <= "ReceivedAmount");
                """);

            // (b) Saldo cliente nunca negativo ni superior al credito inicial (INV-085).
            migrationBuilder.Sql("""
                ALTER TABLE "ClientCreditEntries"
                  DROP CONSTRAINT IF EXISTS chk_ClientCreditEntries_remaining_non_negative;
                ALTER TABLE "ClientCreditEntries"
                  ADD CONSTRAINT chk_ClientCreditEntries_remaining_non_negative
                  CHECK ("RemainingBalance" >= 0 AND "RemainingBalance" <= "CreditedAmount");
                """);

            // (c) Neto positivo y Gross >= Net (INV-112: las deducciones no pueden
            //     dejar el neto negativo, y nunca pueden superar el bruto).
            migrationBuilder.Sql("""
                ALTER TABLE "OperatorRefundAllocations"
                  DROP CONSTRAINT IF EXISTS chk_OperatorRefundAllocations_net_positive;
                ALTER TABLE "OperatorRefundAllocations"
                  ADD CONSTRAINT chk_OperatorRefundAllocations_net_positive
                  CHECK ("NetAmount" >= 0 AND "GrossAmount" >= "NetAmount");
                """);

            // (d) DeductionLine.Amount > 0 (INV-112).
            migrationBuilder.Sql("""
                ALTER TABLE "DeductionLines"
                  DROP CONSTRAINT IF EXISTS chk_DeductionLines_amount_positive;
                ALTER TABLE "DeductionLines"
                  ADD CONSTRAINT chk_DeductionLines_amount_positive
                  CHECK ("Amount" > 0);
                """);

            // (e) TravelFiles.Status (entidad Reserva) restringido a valores reconocidos.
            //     Incluye "Archived" (legacy soft-delete) + el nuevo "PendingOperatorRefund"
            //     introducido por este modulo (INV-100).
            //     NOTA: la tabla se llama "TravelFiles" porque la entidad Reserva
            //     fue renombrada via ToTable() — el desalineo es historico (ver
            //     reference_db_naming en memoria del proyecto).
            migrationBuilder.Sql("""
                ALTER TABLE "TravelFiles"
                  DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;
                ALTER TABLE "TravelFiles"
                  ADD CONSTRAINT chk_TravelFiles_status_valid
                  CHECK ("Status" IN (
                    'Budget',
                    'Confirmed',
                    'Traveling',
                    'Closed',
                    'Cancelled',
                    'PendingOperatorRefund',
                    'Archived'
                  ));
                """);

            // (f) FiscalSnapshot consistente para estados post-Drafted (INV-118,
            //     review BR2 2026-05-14). En Drafted permitimos snapshot incompleto
            //     (el cashier todavia esta editando); para confirmar con el cliente
            //     (T0 -> AwaitingFiscalConfirmation) y todos los estados posteriores,
            //     el snapshot DEBE tener fuente elegida, TC original > 0 y currency.
            //     ADR-002 §2.7: el TC original es la garra fiscal de la NC (ARCA exige
            //     coherencia con la factura original). Si llega a persistirse en 0,
            //     la diferencia de cambio T0->T2 no se puede calcular y el modulo se rompe.
            //
            //     Codigo de status: 0=Drafted, 1=AwaitingFiscalConfirmation, 2=AwaitingOperatorRefund,
            //     3=ClientCreditApplied, 4=Closed, 5=AbandonedByOperator, 6=Aborted.
            //     Solo Drafted (0) y Aborted (6) admiten snapshot incompleto (Aborted
            //     ocurre desde Drafted sin haber confirmado nada con el cliente).
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalsnapshot_consistent;
                ALTER TABLE "BookingCancellations"
                  ADD CONSTRAINT chk_BookingCancellations_fiscalsnapshot_consistent
                  CHECK (
                    "Status" IN (0, 6)
                    OR (
                      "FiscalSnapshot_Source" <> 0
                      AND "FiscalSnapshot_ExchangeRateAtOriginalInvoice" > 0
                      AND "FiscalSnapshot_CurrencyAtEvent" IS NOT NULL
                    )
                  );
                """);

            // (g) Unique partial index: 1 sola allocation activa (no voided) por
            //     pareja (refund, bookingCancellation). Sin API fluida en EF para
            //     CREATE INDEX ... WHERE, lo hacemos con SQL crudo.
            //     Renombrado segun convencion ix_<tabla>_<concepto> (BR1).
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "ix_OperatorRefundAllocations_active_unique_alloc_per_refund_per_bc";
                CREATE UNIQUE INDEX "ix_OperatorRefundAllocations_active_unique_alloc_per_refund_per_bc"
                  ON "OperatorRefundAllocations" ("OperatorRefundReceivedId", "BookingCancellationId")
                  WHERE "IsVoided" = false;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revertir CHECK constraints y unique partial index ANTES de dropear
            // tablas e indices auto-generados. Nombres alineados con la convencion
            // del Up() (review BR1, 2026-05-14).
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "ix_OperatorRefundAllocations_active_unique_alloc_per_refund_per_bc";

                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalsnapshot_consistent;

                ALTER TABLE "TravelFiles"
                  DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;

                ALTER TABLE "DeductionLines"
                  DROP CONSTRAINT IF EXISTS chk_DeductionLines_amount_positive;

                ALTER TABLE "OperatorRefundAllocations"
                  DROP CONSTRAINT IF EXISTS chk_OperatorRefundAllocations_net_positive;

                ALTER TABLE "ClientCreditEntries"
                  DROP CONSTRAINT IF EXISTS chk_ClientCreditEntries_remaining_non_negative;

                ALTER TABLE "OperatorRefundsReceived"
                  DROP CONSTRAINT IF EXISTS chk_OperatorRefundsReceived_allocated_not_exceeds;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_ManualCashMovements_ClientCreditWithdrawals_ClientCreditWit~",
                table: "ManualCashMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_ManualCashMovements_OperatorRefundsReceived_OperatorRefundR~",
                table: "ManualCashMovements");

            migrationBuilder.DropTable(
                name: "ClientCreditWithdrawals");

            migrationBuilder.DropTable(
                name: "DeductionLines");

            migrationBuilder.DropTable(
                name: "ClientCreditEntries");

            migrationBuilder.DropTable(
                name: "OperatorRefundAllocations");

            migrationBuilder.DropTable(
                name: "BookingCancellations");

            migrationBuilder.DropTable(
                name: "OperatorRefundsReceived");

            migrationBuilder.DropIndex(
                name: "IX_ManualCashMovements_ClientCreditWithdrawalId",
                table: "ManualCashMovements");

            migrationBuilder.DropIndex(
                name: "IX_ManualCashMovements_OperatorRefundReceivedId",
                table: "ManualCashMovements");

            migrationBuilder.DropColumn(
                name: "ClientCreditWithdrawalId",
                table: "ManualCashMovements");

            migrationBuilder.DropColumn(
                name: "OperatorRefundReceivedId",
                table: "ManualCashMovements");

            migrationBuilder.DropColumn(
                name: "LastArcaAttemptAt",
                table: "Invoices");
        }
    }
}
