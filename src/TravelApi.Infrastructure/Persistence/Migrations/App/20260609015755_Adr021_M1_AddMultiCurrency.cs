using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-021 Capa 1 (multimoneda por reserva, 2026-06-08): migracion ADITIVA y byte-safe.
    /// Agrega la moneda + el bloque de tipo de cambio (cobro/pago cruzado) y las dos tablas hijas
    /// materializadas que dejan consultar el saldo POR MONEDA en SQL.
    ///
    /// <para><b>Que agrega</b>:
    /// <list type="bullet">
    /// <item>Servicio generico (<c>ServicioReserva</c> -> tabla "Reservations"): <c>Currency</c> nullable
    /// (null = legacy = ARS al leer). Los 5 servicios tipados ya la tenian (AddBookingCurrencyTraceability).</item>
    /// <item><c>Payment</c> y <c>SupplierPayment</c>: <c>Currency</c> NOT NULL default 'ARS' (backfill
    /// automatico de filas legacy a pesos) + <c>ImputedCurrency</c>/<c>ExchangeRate(18,6)</c>/
    /// <c>ExchangeRateSource(int)</c>/<c>ExchangeRateAt</c>/<c>ImputedAmount(18,2)</c> todas NULLABLE
    /// (= pago no cruzado mientras nadie las setee).</item>
    /// <item>Tablas hijas vacias <c>ReservaMoneyByCurrency</c> y <c>SupplierBalanceByCurrency</c>
    /// (FK Cascade, indice unico (padre, Currency) y (Currency, Balance)).</item>
    /// </list></para>
    ///
    /// <para><b>Convencion del TC</b> (ADR-021 §2.2bis): <c>ExchangeRate</c> = ARS por 1 USD (igual
    /// orientacion que <c>Invoice.MonCotiz</c>). Esta migracion solo crea las columnas; la conversion
    /// es Capa 2.</para>
    ///
    /// <para><b>Sin cambio de comportamiento</b>: no se toca ningun importe ni la precision de
    /// <c>Payment.Amount</c>/<c>SupplierPayment.Amount</c>. Todo legacy queda ARS = identico a hoy.
    /// Ningun campo nuevo usa <c>HasColumnName</c> (columna = propiedad), por lo que no hay riesgo del
    /// trap M2; tampoco hay SQL crudo aca.</para>
    ///
    /// <para><b>PENDIENTE Capa 2 (NO en esta migracion)</b>: las tablas hijas arrancan VACIAS. El
    /// backfill por recalculo programatico (poblar una fila ARS por cada reserva con Balance != 0 y
    /// cada proveedor con CurrentBalance != 0, §7.2.6) lo hace la Capa 2 junto con el calculator y el
    /// persister consolidado (<c>ReservaMoneyPersister</c>). Hasta ese backfill, los consumidores que
    /// lean las hijas las veran vacias (= 0); por eso en Capa 1 nadie las lee todavia.</para>
    ///
    /// <para><b>Down</b>: dropea las dos tablas hijas (derivables por recalculo, no pierden datos
    /// crudos) y las columnas. OJO: una vez que existan pagos cruzados con TC persistido, el rollback
    /// de schema pierde ese dato crudo no reconstruible (ADR-021 §10). Seguro solo antes del primer USD.</para>
    /// </summary>
    public partial class Adr021_M1_AddMultiCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "SupplierPayments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "ARS");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "SupplierPayments",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExchangeRateAt",
                table: "SupplierPayments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeRateSource",
                table: "SupplierPayments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ImputedAmount",
                table: "SupplierPayments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImputedCurrency",
                table: "SupplierPayments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Reservations",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Payments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "ARS");

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                table: "Payments",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExchangeRateAt",
                table: "Payments",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ExchangeRateSource",
                table: "Payments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ImputedAmount",
                table: "Payments",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImputedCurrency",
                table: "Payments",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReservaMoneyByCurrency",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS"),
                    TotalSale = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    ConfirmedSale = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalCost = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalPaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReservaMoneyByCurrency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReservaMoneyByCurrency_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SupplierBalanceByCurrency",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS"),
                    ConfirmedPurchases = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TotalPaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Balance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierBalanceByCurrency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierBalanceByCurrency_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReservaMoneyByCurrency_Currency_Balance",
                table: "ReservaMoneyByCurrency",
                columns: new[] { "Currency", "Balance" });

            migrationBuilder.CreateIndex(
                name: "IX_ReservaMoneyByCurrency_ReservaId_Currency",
                table: "ReservaMoneyByCurrency",
                columns: new[] { "ReservaId", "Currency" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceByCurrency_Currency_Balance",
                table: "SupplierBalanceByCurrency",
                columns: new[] { "Currency", "Balance" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierBalanceByCurrency_SupplierId_Currency",
                table: "SupplierBalanceByCurrency",
                columns: new[] { "SupplierId", "Currency" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReservaMoneyByCurrency");

            migrationBuilder.DropTable(
                name: "SupplierBalanceByCurrency");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ExchangeRateAt",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ExchangeRateSource",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ImputedAmount",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "ImputedCurrency",
                table: "SupplierPayments");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExchangeRateAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExchangeRateSource",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ImputedAmount",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ImputedCurrency",
                table: "Payments");
        }
    }
}
