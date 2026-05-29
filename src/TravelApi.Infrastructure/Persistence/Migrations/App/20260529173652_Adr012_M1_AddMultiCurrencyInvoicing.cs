using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-012 MVP (facturar en dolares, 2026-05-29): primer paso de facturacion
    /// multimoneda. Migracion 100% ADITIVA, sin DROP ni reescritura de filas:
    ///
    /// <list type="bullet">
    /// <item><c>OperationalFinanceSettings.EnableMultiCurrencyInvoicing</c> = bool NOT NULL
    /// default <c>false</c>. Las filas existentes quedan en false -> facturacion en pesos
    /// byte-identica a hoy. El flag se prende a mano (SQL/seed) recien tras signoff contador.</item>
    /// <item><c>Invoices.ExchangeRateSource</c> = int NULLABLE (enum
    /// <c>ExchangeRateSource</c> persistido como int, igual que FiscalSnapshot.Source).</item>
    /// <item><c>Invoices.ExchangeRateFetchedAt</c> = timestamptz NULLABLE.</item>
    /// <item><c>Invoices.ExchangeRateJustification</c> = varchar(500) NULLABLE.</item>
    /// </list>
    ///
    /// <para>Las tres columnas de Invoices son NULLABLE sin default y SIN backfill: las
    /// facturas en pesos (todo lo existente) las dejan en NULL. No hay lock pesado de tabla
    /// ni riesgo de datos. La columna <c>MonCotiz</c> YA estaba en numeric(18,6) (config EF
    /// previa), por eso esta migracion no la toca.</para>
    ///
    /// <para><b>Orden de deploy</b>: aplicar esta migracion ANTES de subir el binario nuevo
    /// (orden estandar del repo). La app vieja ignora las columnas nuevas; la nueva las usa.</para>
    /// </summary>
    public partial class Adr012_M1_AddMultiCurrencyInvoicing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Flag maestro multimoneda. OFF en prod hasta signoff contador + homologacion ARCA.
            migrationBuilder.AddColumn<bool>(
                name: "EnableMultiCurrencyInvoicing",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Trazabilidad del TC en facturas en moneda extranjera. NULL = factura en pesos.
            migrationBuilder.AddColumn<DateTime>(
                name: "ExchangeRateFetchedAt",
                table: "Invoices",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExchangeRateJustification",
                table: "Invoices",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeRateSource",
                table: "Invoices",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback: dropea las 4 columnas. Sin perdida de datos pre-migracion (las filas
            // existentes solo tenian valores en columnas distintas).
            migrationBuilder.DropColumn(
                name: "EnableMultiCurrencyInvoicing",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "ExchangeRateFetchedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRateJustification",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ExchangeRateSource",
                table: "Invoices");
        }
    }
}
