using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-022 Capa 1 (Libro de Caja persistido, 2026-06-11): migracion ADITIVA, 100% generada por EF
    /// (sin SQL crudo de ALTER por nombre — leccion M2: el incidente fue por SQL crudo usando un nombre de
    /// columna que no coincidia con el HasColumnName historico; aca EF emite el DDL desde el modelo).
    ///
    /// <para><b>Que agrega</b>:
    /// <list type="bullet">
    /// <item>Tabla nueva <c>CashLedgerEntries</c> (el asiento inmutable de caja) con: 5 FKs de origen
    /// (Restrict, asi el asiento sobrevive al borrado del origen), 3 FKs de trazabilidad de negocio,
    /// auto-referencia de reversa, indices de consulta, 5 INDICES UNICOS PARCIALES por origen con predicado
    /// <c>IS NOT NULL AND IsReversal=false AND IsReversed=false</c> (a lo sumo UN asiento vigente por origen,
    /// §4.2/B4) y 3 CHECK constraints (Amount&gt;0, Direction valido, exactamente UN FK de origen no-null).</item>
    /// <item><c>ManualCashMovements.Currency</c> NOT NULL default 'ARS' (T2; los manuales legacy en pesos).</item>
    /// <item><c>ClientCreditEntries.Currency</c> NOT NULL default 'ARS' (bolsillo por moneda, Q2/Q1).</item>
    /// <item><c>ClientCreditEntries.OperatorRefundAllocationId</c> y <c>BookingCancellationId</c> pasan de
    /// NOT NULL a NULLABLE (relajacion aditivo-segura: las filas de cancelacion conservan su valor) + el
    /// origen "sobrepago" (<c>SourcePaymentId</c>/<c>SourceReservaId</c>/<c>CreatedByUserId</c>/<c>CreatedByUserName</c>),
    /// para modelar el sobrepago como ClientCreditEntry (§4.9, Q1).</item>
    /// </list></para>
    ///
    /// <para><b>Sin migrar datos</b>: todas las columnas nuevas tienen default a nivel BD y relajar NOT NULL
    /// -&gt; NULL no toca filas existentes. La tabla del libro arranca vacia; el backfill (Capa 3, job C#
    /// idempotente) la puebla desde el estado vivo. <b>Down</b> dropea la tabla y revierte columnas (el libro
    /// es derivado; los Payment/SupplierPayment/ManualCashMovement originales quedan intactos). Probar
    /// <c>Up</c>/<c>Down</c> en Postgres real antes de mergear (leccion M2).</para>
    /// </summary>
    public partial class Adr022_M1_CashLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "ManualCashMovements",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "ARS");

            migrationBuilder.AlterColumn<int>(
                name: "OperatorRefundAllocationId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "BookingCancellationId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserId",
                table: "ClientCreditEntries",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatedByUserName",
                table: "ClientCreditEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "ClientCreditEntries",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "ARS");

            migrationBuilder.AddColumn<int>(
                name: "SourcePaymentId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceReservaId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CashLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Method = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PaymentId = table.Column<int>(type: "integer", nullable: true),
                    SupplierPaymentId = table.Column<int>(type: "integer", nullable: true),
                    OperatorRefundReceivedId = table.Column<int>(type: "integer", nullable: true),
                    ClientCreditWithdrawalId = table.Column<int>(type: "integer", nullable: true),
                    ManualCashMovementId = table.Column<int>(type: "integer", nullable: true),
                    ReservaId = table.Column<int>(type: "integer", nullable: true),
                    SupplierId = table.Column<int>(type: "integer", nullable: true),
                    CustomerId = table.Column<int>(type: "integer", nullable: true),
                    IsReversal = table.Column<bool>(type: "boolean", nullable: false),
                    ReversedEntryId = table.Column<int>(type: "integer", nullable: true),
                    IsReversed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashLedgerEntries", x => x.Id);
                    table.CheckConstraint("chk_cashledger_amount_positive", "\"Amount\" > 0");
                    table.CheckConstraint("chk_cashledger_direction", "\"Direction\" IN ('Income','Expense')");
                    table.CheckConstraint("chk_cashledger_exactly_one_source", "((\"PaymentId\" IS NOT NULL)::int + (\"SupplierPaymentId\" IS NOT NULL)::int + (\"OperatorRefundReceivedId\" IS NOT NULL)::int + (\"ClientCreditWithdrawalId\" IS NOT NULL)::int + (\"ManualCashMovementId\" IS NOT NULL)::int) = 1");
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_CashLedgerEntries_ReversedEntryId",
                        column: x => x.ReversedEntryId,
                        principalTable: "CashLedgerEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_ClientCreditWithdrawals_ClientCreditWithd~",
                        column: x => x.ClientCreditWithdrawalId,
                        principalTable: "ClientCreditWithdrawals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_ManualCashMovements_ManualCashMovementId",
                        column: x => x.ManualCashMovementId,
                        principalTable: "ManualCashMovements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_OperatorRefundsReceived_OperatorRefundRec~",
                        column: x => x.OperatorRefundReceivedId,
                        principalTable: "OperatorRefundsReceived",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_Payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "Payments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_SupplierPayments_SupplierPaymentId",
                        column: x => x.SupplierPaymentId,
                        principalTable: "SupplierPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashLedgerEntries_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_SourcePaymentId",
                table: "ClientCreditEntries",
                column: "SourcePaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClientCreditEntries_SourceReservaId",
                table: "ClientCreditEntries",
                column: "SourceReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_ClientCreditWithdrawalId",
                table: "CashLedgerEntries",
                column: "ClientCreditWithdrawalId",
                unique: true,
                filter: "\"ClientCreditWithdrawalId\" IS NOT NULL AND \"IsReversal\" = false AND \"IsReversed\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_Currency_OccurredAt",
                table: "CashLedgerEntries",
                columns: new[] { "Currency", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_CustomerId",
                table: "CashLedgerEntries",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_ManualCashMovementId",
                table: "CashLedgerEntries",
                column: "ManualCashMovementId",
                unique: true,
                filter: "\"ManualCashMovementId\" IS NOT NULL AND \"IsReversal\" = false AND \"IsReversed\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_OperatorRefundReceivedId",
                table: "CashLedgerEntries",
                column: "OperatorRefundReceivedId",
                unique: true,
                filter: "\"OperatorRefundReceivedId\" IS NOT NULL AND \"IsReversal\" = false AND \"IsReversed\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_PaymentId",
                table: "CashLedgerEntries",
                column: "PaymentId",
                unique: true,
                filter: "\"PaymentId\" IS NOT NULL AND \"IsReversal\" = false AND \"IsReversed\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_PublicId",
                table: "CashLedgerEntries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_ReservaId",
                table: "CashLedgerEntries",
                column: "ReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_ReversedEntryId",
                table: "CashLedgerEntries",
                column: "ReversedEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_SourceType",
                table: "CashLedgerEntries",
                column: "SourceType");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_SupplierId",
                table: "CashLedgerEntries",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_CashLedgerEntries_SupplierPaymentId",
                table: "CashLedgerEntries",
                column: "SupplierPaymentId",
                unique: true,
                filter: "\"SupplierPaymentId\" IS NOT NULL AND \"IsReversal\" = false AND \"IsReversed\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCreditEntries_Payments_SourcePaymentId",
                table: "ClientCreditEntries",
                column: "SourcePaymentId",
                principalTable: "Payments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ClientCreditEntries_TravelFiles_SourceReservaId",
                table: "ClientCreditEntries",
                column: "SourceReservaId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ClientCreditEntries_Payments_SourcePaymentId",
                table: "ClientCreditEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_ClientCreditEntries_TravelFiles_SourceReservaId",
                table: "ClientCreditEntries");

            migrationBuilder.DropTable(
                name: "CashLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_ClientCreditEntries_SourcePaymentId",
                table: "ClientCreditEntries");

            migrationBuilder.DropIndex(
                name: "IX_ClientCreditEntries_SourceReservaId",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "ManualCashMovements");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "CreatedByUserName",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "SourcePaymentId",
                table: "ClientCreditEntries");

            migrationBuilder.DropColumn(
                name: "SourceReservaId",
                table: "ClientCreditEntries");

            migrationBuilder.AlterColumn<int>(
                name: "OperatorRefundAllocationId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "BookingCancellationId",
                table: "ClientCreditEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);
        }
    }
}
