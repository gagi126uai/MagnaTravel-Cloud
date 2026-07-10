using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T3b Decision 2 (2026-07-10): 8 columnas nullables sobre <c>BookingCancellationLineOperatorCharges</c>
    /// para convertir un cargo del operador en una moneda DISTINTA a la de su factura destino (ej. cargo en USD,
    /// Nota de Debito del cliente en ARS). NO toca el TC del comprobante (ese sigue SIEMPRE congelado de la
    /// factura original, regla firmada e inamovible) — esto convierte el monto EMBEBIDO de un renglon.
    ///
    /// <para><b>4 campos ESTIMADO</b> (preview informativo al confirmar/cargar el cargo, cuando su moneda difiere
    /// de la de la factura destino): <c>EstimatedExchangeRate*</c>. <b>4 campos DEFINITIVO</b> (el que realmente
    /// viaja al renglon de la ND, fijado AL EMITIRLA — lectura M1 del Addendum, "dia del cargo" = dia de emision):
    /// <c>DefinitiveExchangeRate*</c>. El estimado nunca se pisa: queda como rastro de que se preveia al cargar el
    /// cargo. Ambos juegos viven en la MISMA tabla que Decision 1 (no en <c>Invoice</c>: una ND en ARS con un
    /// cargo embebido en USD no tiene donde guardar ESA conversion en la factura, que describe la valuacion del
    /// COMPROBANTE, no de un renglon).</para>
    ///
    /// <para><b>Aditiva, sin backfill</b>: los cargos anteriores a esta tanda (incluidos los de T3a, mono-moneda)
    /// quedan con los 8 campos en null — comportamiento byte-identico (nunca hubo conversion que hacer).</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura)</b> — confirma que las 8 columnas quedaron activas y que
    /// los cargos existentes no se vieron afectados:
    /// <code>
    /// SELECT COUNT(*) FROM "BookingCancellationLineOperatorCharges" WHERE "EstimatedExchangeRateToClientInvoiceCurrency" IS NOT NULL;
    /// -- esperado: 0 justo despues de aplicar (ningun cargo previo a esta tanda tiene conversion cargada)
    ///
    /// SELECT column_name FROM information_schema.columns
    /// WHERE table_name = 'BookingCancellationLineOperatorCharges' AND column_name LIKE '%ExchangeRate%';
    /// -- esperado: 8 filas (4 Estimated* + 4 Definitive*)
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T3b2_AddEstimatedExchangeRateToOperatorCharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DefinitiveExchangeRateAt",
                table: "BookingCancellationLineOperatorCharges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DefinitiveExchangeRateAtNdEmission",
                table: "BookingCancellationLineOperatorCharges",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefinitiveExchangeRateJustification",
                table: "BookingCancellationLineOperatorCharges",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DefinitiveExchangeRateSource",
                table: "BookingCancellationLineOperatorCharges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EstimatedExchangeRateAt",
                table: "BookingCancellationLineOperatorCharges",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EstimatedExchangeRateJustification",
                table: "BookingCancellationLineOperatorCharges",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EstimatedExchangeRateSource",
                table: "BookingCancellationLineOperatorCharges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "EstimatedExchangeRateToClientInvoiceCurrency",
                table: "BookingCancellationLineOperatorCharges",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefinitiveExchangeRateAt",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "DefinitiveExchangeRateAtNdEmission",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "DefinitiveExchangeRateJustification",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "DefinitiveExchangeRateSource",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "EstimatedExchangeRateAt",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "EstimatedExchangeRateJustification",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "EstimatedExchangeRateSource",
                table: "BookingCancellationLineOperatorCharges");

            migrationBuilder.DropColumn(
                name: "EstimatedExchangeRateToClientInvoiceCurrency",
                table: "BookingCancellationLineOperatorCharges");
        }
    }
}
