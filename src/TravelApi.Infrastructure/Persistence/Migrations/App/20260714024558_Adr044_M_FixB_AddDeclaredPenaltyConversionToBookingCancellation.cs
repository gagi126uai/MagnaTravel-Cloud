using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 Fix B (2026-07-13): agrega 6 columnas nullable a <c>BookingCancellations</c> para conservar, en
    /// forma ESTRUCTURADA, el monto original que declaro el operador + el tipo de cambio usado cuando una multa
    /// se convierte a la moneda de la factura (Caso A: multa declarada en USD, factura y lineas del operador en
    /// pesos). Antes, ese original vivia solo en el JSON de auditoria; estas columnas permiten reconstruir el
    /// origen (ej. USD 200) sin leer blobs, para la deuda futura (retencion cross-currency real + treasury FX).
    ///
    /// <para><b>ADITIVA PURA — 0 filas afectadas.</b> Solo <c>ADD COLUMN ... NULL</c>: no reescribe filas, no
    /// hace backfill, no toca datos existentes. Los BCs actuales quedan con las 6 columnas en <c>null</c>
    /// (comportamiento y forma del dato identicos a antes del fix). Compatible hacia atras: la app vieja ignora
    /// columnas que no conoce. <c>Down()</c> hace el drop de las 6 columnas.</para>
    ///
    /// <para><b>Validacion contra prod (leccion 2026-07-09, el SQL crudo se valida contra la base)</b>: como es
    /// aditiva pura no hay backfill que contar. Post-deploy, confirmar que las columnas existen y estan todas en
    /// null:
    /// <code>
    /// -- Las 6 columnas existen (debe devolver 6).
    /// SELECT count(*) FROM information_schema.columns
    /// WHERE table_name = 'BookingCancellations'
    ///   AND column_name IN ('DeclaredPenaltyOriginalAmount','DeclaredPenaltyOriginalCurrency',
    ///     'PenaltyConversionExchangeRate','PenaltyConversionExchangeRateSource',
    ///     'PenaltyConversionExchangeRateAt','PenaltyConversionExchangeRateJustification');
    /// -- Ningun BC quedo con conversion cargada por la migracion (debe devolver 0).
    /// SELECT count(*) FROM "BookingCancellations" WHERE "DeclaredPenaltyOriginalAmount" IS NOT NULL;
    /// </code>
    /// </para>
    /// </summary>
    public partial class Adr044_M_FixB_AddDeclaredPenaltyConversionToBookingCancellation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeclaredPenaltyOriginalAmount",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeclaredPenaltyOriginalCurrency",
                table: "BookingCancellations",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PenaltyConversionExchangeRate",
                table: "BookingCancellations",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PenaltyConversionExchangeRateAt",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PenaltyConversionExchangeRateJustification",
                table: "BookingCancellations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PenaltyConversionExchangeRateSource",
                table: "BookingCancellations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeclaredPenaltyOriginalAmount",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "DeclaredPenaltyOriginalCurrency",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyConversionExchangeRate",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyConversionExchangeRateAt",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyConversionExchangeRateJustification",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyConversionExchangeRateSource",
                table: "BookingCancellations");
        }
    }
}
