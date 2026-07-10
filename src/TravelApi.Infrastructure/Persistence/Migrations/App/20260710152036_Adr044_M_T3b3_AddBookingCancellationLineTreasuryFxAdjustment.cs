using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T3b Decision 3 (2026-07-10): tabla NUEVA <c>BookingCancellationLineTreasuryFxAdjustments</c> —
    /// registro AUDITABLE (gestion interna, sin asiento de mayor formal todavia, firma contable pendiente) de la
    /// diferencia entre el TC con que se emitio la Nota de Debito de un cargo de operador y el TC real con que
    /// ese cargo se liquida de verdad (reembolso recibido si <c>Retenida</c>, o pago al proveedor si
    /// <c>FacturadaAparte</c>). No genera ND/NC, no toca comprobantes, no participa del calculo canonico de saldo
    /// del cliente (<c>ReservaMoneyCalculator</c>) ni del Libro de Caja (<c>CashLedgerEntry</c>).
    ///
    /// <para><b>CHECK "exactamente un origen"</b> (mismo patron que <c>CashLedgerEntry</c>): cada fila tiene O
    /// bien <c>OperatorRefundAllocationId</c> (cargos <c>Retenida</c>) O bien <c>SupplierPaymentId</c> (cargos
    /// <c>FacturadaAparte</c>), nunca ambos ni ninguno.</para>
    ///
    /// <para><b>Indice unico parcial (M4)</b>: a lo sumo UNA fila VIGENTE (<c>IsSuperseded=false</c>) por cargo.
    /// Si la allocation/pago de origen se anula (soft-void ADR-002) y se reemplaza, la fila vieja se marca
    /// superseded (nunca se borra, historia intacta) y se enlaza a la nueva via <c>SupersededByAdjustmentId</c>.</para>
    ///
    /// <para><b>Aditiva, sin migrar datos</b>: tabla nueva vacia — no hay liquidaciones previas que retrocalcular
    /// (los campos de TC estimado de Decision 2 no existian antes de T3b, asi que ningun cargo historico tiene
    /// con que comparar).</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura)</b> — confirma que la tabla, el CHECK y el indice unico
    /// parcial quedaron activos:
    /// <code>
    /// SELECT COUNT(*) FROM "BookingCancellationLineTreasuryFxAdjustments";
    /// -- esperado: 0 justo despues de aplicar (tabla nueva, sin filas)
    ///
    /// SELECT conname FROM pg_constraint
    /// WHERE conrelid = '"BookingCancellationLineTreasuryFxAdjustments"'::regclass AND contype = 'c';
    /// -- esperado: 1 fila (chk_bc_treasury_fx_adjustment_exactly_one_origin)
    ///
    /// SELECT indexname FROM pg_indexes
    /// WHERE tablename = 'BookingCancellationLineTreasuryFxAdjustments'
    ///   AND indexname LIKE '%OperatorChargeId_Vigente%';
    /// -- esperado: 1 fila (el indice unico parcial WHERE "IsSuperseded" = false)
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T3b3_AddBookingCancellationLineTreasuryFxAdjustment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingCancellationLineTreasuryFxAdjustments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperatorChargeId = table.Column<int>(type: "integer", nullable: false),
                    OperatorRefundAllocationId = table.Column<int>(type: "integer", nullable: true),
                    SupplierPaymentId = table.Column<int>(type: "integer", nullable: true),
                    RateAtNdEmission = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    RateAtSettlement = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ChargeAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ChargeCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DeltaAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    SettlementCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    AssumedBy = table.Column<int>(type: "integer", nullable: false),
                    IsSuperseded = table.Column<bool>(type: "boolean", nullable: false),
                    SupersededByAdjustmentId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCancellationLineTreasuryFxAdjustments", x => x.Id);
                    table.CheckConstraint("chk_bc_treasury_fx_adjustment_exactly_one_origin", "((\"OperatorRefundAllocationId\" IS NOT NULL)::int + (\"SupplierPaymentId\" IS NOT NULL)::int) = 1");
                    table.ForeignKey(
                        name: "FK_BookingCancellationLineTreasuryFxAdjustments_OperatorCharge",
                        column: x => x.OperatorChargeId,
                        principalTable: "BookingCancellationLineOperatorCharges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BookingCancellationLineTreasuryFxAdjustments_OperatorRefundAllocation",
                        column: x => x.OperatorRefundAllocationId,
                        principalTable: "OperatorRefundAllocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingCancellationLineTreasuryFxAdjustments_SupersededBy",
                        column: x => x.SupersededByAdjustmentId,
                        principalTable: "BookingCancellationLineTreasuryFxAdjustments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BookingCancellationLineTreasuryFxAdjustments_SupplierPayment",
                        column: x => x.SupplierPaymentId,
                        principalTable: "SupplierPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineTreasuryFxAdjustments_OperatorChargeId_Vigente",
                table: "BookingCancellationLineTreasuryFxAdjustments",
                column: "OperatorChargeId",
                unique: true,
                filter: "\"IsSuperseded\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineTreasuryFxAdjustments_OperatorRefund~",
                table: "BookingCancellationLineTreasuryFxAdjustments",
                column: "OperatorRefundAllocationId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineTreasuryFxAdjustments_PublicId",
                table: "BookingCancellationLineTreasuryFxAdjustments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineTreasuryFxAdjustments_SupersededByAd~",
                table: "BookingCancellationLineTreasuryFxAdjustments",
                column: "SupersededByAdjustmentId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineTreasuryFxAdjustments_SupplierPaymen~",
                table: "BookingCancellationLineTreasuryFxAdjustments",
                column: "SupplierPaymentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCancellationLineTreasuryFxAdjustments");
        }
    }
}
