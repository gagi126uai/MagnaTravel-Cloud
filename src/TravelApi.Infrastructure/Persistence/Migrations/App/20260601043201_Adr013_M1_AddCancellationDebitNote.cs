using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-013 (Nota de Debito por penalidad en cancelacion, 2026-06-01): migracion
    /// 100% ADITIVA. No borra datos, no reescribe filas, sin backfill.
    ///
    /// <para>Tres bloques de cambios:</para>
    /// <list type="number">
    /// <item><c>OperationalFinanceSettings.EnableCancellationDebitNote</c> = bool NOT NULL
    /// default <c>false</c> -> con el flag OFF, las cancelaciones siguen haciendo SOLO la
    /// NC total, byte-identico a hoy.</item>
    /// <item><c>Suppliers.PenaltyOwnership</c> = int NOT NULL default <c>0</c>
    /// (= <c>Operator</c> = pass-through) -> el valor conservador: los operadores
    /// existentes quedan en "la penalidad la retiene el operador" = NO ND.</item>
    /// <item>Columnas en <c>BookingCancellations</c>: clasificacion del evento
    /// (PenaltyStatus, ConceptKind, DebitNotePurpose), vinculo + estado de la ND
    /// (DebitNoteInvoiceId FK SetNull, DebitNoteStatus, DebitNoteArcaErrorMessage) y el
    /// snapshot fiscal congelado (montos, tipos, condicion, quien clasifico/confirmo).
    /// Los enums NOT NULL arrancan en su valor conservador (0 = Estimated /
    /// OperatorPenaltyPassThrough / NotApplicable = NO ND); el resto nullable.</item>
    /// </list>
    ///
    /// <para><b>Orden de deploy</b>: aplicar esta migracion ANTES de subir el binario nuevo
    /// (orden estandar del repo). La app vieja ignora las columnas nuevas; la nueva las usa
    /// solo si el flag <c>EnableCancellationDebitNote</c> esta prendido (default OFF).</para>
    ///
    /// <para><b>Rollback</b>: las NDs ya emitidas con CAE son <c>Invoice</c> normales y NO
    /// dependen de estas columnas para existir; dropear las columnas no las borra. El
    /// rollback apaga la emision futura, no deshace comprobantes emitidos.</para>
    /// </summary>
    public partial class Adr013_M1_AddCancellationDebitNote : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PenaltyOwnership",
                table: "Suppliers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "EnableCancellationDebitNote",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConceptClassifiedAt",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ConceptClassifiedByUserId",
                table: "BookingCancellations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ConceptKind",
                table: "BookingCancellations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DebitNoteArcaErrorMessage",
                table: "BookingCancellations",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DebitNoteCbteTipoAtEvent",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DebitNoteInvoiceId",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DebitNotePurpose",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DebitNoteStatus",
                table: "BookingCancellations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "EmitterTaxConditionAtEvent",
                table: "BookingCancellations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OriginalInvoiceCbteTipoAtEvent",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PenaltyAmountAtEvent",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PenaltyConfirmedAt",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PenaltyConfirmedByUserId",
                table: "BookingCancellations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PenaltyCurrencyAtEvent",
                table: "BookingCancellations",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PenaltyOwnershipAtEvent",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PenaltyStatus",
                table: "BookingCancellations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_DebitNoteInvoiceId",
                table: "BookingCancellations",
                column: "DebitNoteInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_DebitNoteStatus",
                table: "BookingCancellations",
                column: "DebitNoteStatus");

            migrationBuilder.AddForeignKey(
                name: "FK_BookingCancellations_Invoices_DebitNoteInvoiceId",
                table: "BookingCancellations",
                column: "DebitNoteInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BookingCancellations_Invoices_DebitNoteInvoiceId",
                table: "BookingCancellations");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_DebitNoteInvoiceId",
                table: "BookingCancellations");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_DebitNoteStatus",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyOwnership",
                table: "Suppliers");

            migrationBuilder.DropColumn(
                name: "EnableCancellationDebitNote",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "ConceptClassifiedAt",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "ConceptClassifiedByUserId",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "ConceptKind",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "DebitNoteArcaErrorMessage",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "DebitNoteCbteTipoAtEvent",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "DebitNoteInvoiceId",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "DebitNotePurpose",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "DebitNoteStatus",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "EmitterTaxConditionAtEvent",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "OriginalInvoiceCbteTipoAtEvent",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyAmountAtEvent",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyConfirmedAt",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyConfirmedByUserId",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyCurrencyAtEvent",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyOwnershipAtEvent",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyStatus",
                table: "BookingCancellations");
        }
    }
}
