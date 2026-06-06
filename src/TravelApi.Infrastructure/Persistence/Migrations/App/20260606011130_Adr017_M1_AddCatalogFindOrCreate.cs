using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-017 F1.1 (catalogo find-or-create, 2026-06-05): migracion 1 de 2, 100% ADITIVA.
    /// Solo crea la ESTRUCTURA del catalogo; en F1.1 nadie la escribe todavia (eso es F1.2/F1.3).
    ///
    /// <para>Que agrega:
    /// <list type="number">
    ///   <item><c>Rates.SearchName</c> (nombre normalizado para el buscador), <c>Rates.CreatedInSale</c>
    ///   (pill "creado en venta", default false) y <c>Rates.CreatedFromReservaId</c> (FK opcional a la
    ///   Reserva de origen, ON DELETE SET NULL).</item>
    ///   <item>Tabla <c>RateSupplierSales</c> (memoria "ultima venta por producto y operador") con UNIQUE
    ///   (RateId, SupplierId) e indice (RateId, LastSoldAt DESC). Nace vacia.</item>
    ///   <item>Indice GIN trigram sobre <c>SearchName</c> (SQL crudo, IF NOT EXISTS — mismo patron que
    ///   <c>AddRateFuzzyMatching</c>; reutiliza la extension pg_trgm ya instalada).</item>
    ///   <item>Backfill TYPE-AWARE de <c>SearchName</c> (ver nota abajo).</item>
    /// </list></para>
    ///
    /// <para><b>Backfill type-aware (gap B3 del review)</b>: Hotel toma el nombre real del hotel
    /// (<c>HotelName</c> si no esta vacio, si no <c>ProductName</c>); el resto de los tipos toma
    /// <c>ProductName</c>. En hoteles legacy el nombre real vive en HotelName y ProductName suele ser
    /// generico; si backfilleramos ProductName para todos, el anti-duplicados naceria roto para hoteles.
    /// La normalizacion SQL replica <c>TextNormalizer.NormalizeForCatalog</c> para los casos comunes
    /// (minuscula, sin tildes del set español via translate, espacios colapsados). RESIDUO CONOCIDO
    /// (para F1.2): el SQL NO des-acentua alfabetos no-español (NFD) ni colapsa puntuacion repetida como
    /// la funcion de la app; ese residuo se corrige solo en la primera escritura de la app sobre la fila
    /// y el matching es difuso, asi que lo tolera. Solo se escribe donde <c>SearchName IS NULL</c>
    /// (idempotente: re-correr no pisa lo que ya escribio la app).</para>
    ///
    /// <para><b>Orden de deploy / R8</b>: encolada DETRAS de la cola de migraciones pendientes del VPS,
    /// sin tocar ni reordenar ninguna. Aditiva: no hay DROP/ALTER destructivo, defaults seguros. NO se
    /// aplica desde aca (la aplica el pipeline de migraciones del VPS).</para>
    /// </summary>
    public partial class Adr017_M1_AddCatalogFindOrCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreatedFromReservaId",
                table: "Rates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CreatedInSale",
                table: "Rates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SearchName",
                table: "Rates",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RateSupplierSales",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RateId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    LastSoldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastNetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    LastTax = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    LastSalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    LastCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    LastPriceUnit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SalesCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RateSupplierSales", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RateSupplierSales_Rates_RateId",
                        column: x => x.RateId,
                        principalTable: "Rates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RateSupplierSales_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Rates_CreatedFromReservaId",
                table: "Rates",
                column: "CreatedFromReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_RateSupplierSales_RateId_LastSoldAt",
                table: "RateSupplierSales",
                columns: new[] { "RateId", "LastSoldAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_RateSupplierSales_RateId_SupplierId",
                table: "RateSupplierSales",
                columns: new[] { "RateId", "SupplierId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RateSupplierSales_SupplierId",
                table: "RateSupplierSales",
                column: "SupplierId");

            migrationBuilder.AddForeignKey(
                name: "FK_Rates_TravelFiles_CreatedFromReservaId",
                table: "Rates",
                column: "CreatedFromReservaId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Backfill TYPE-AWARE de SearchName (ver nota de la cabecera). Se ejecuta DESPUES de crear
            // la columna y ANTES del indice trigram (asi el GIN se construye una sola vez sobre los
            // valores ya backfilleados, en lugar de actualizarse fila por fila).
            //
            // Replica TextNormalizer.NormalizeForCatalog para los casos comunes:
            //   - translate(...) saca las tildes del set español (incluye mayusculas);
            //   - lower(...) pasa a minuscula;
            //   - regexp_replace('\s+',' ') colapsa corridas de espacios en uno solo;
            //   - trim(...) (interno sobre el nombre crudo y externo sobre el resultado) saca bordes.
            // COALESCE(NULLIF(trim("HotelName"),''), "ProductName") = "HotelName si no esta vacio, si no
            // ProductName", misma semantica que el string.IsNullOrWhiteSpace de la app.
            // Solo filas con SearchName NULL: idempotente, no pisa lo que ya escribio la app.
            migrationBuilder.Sql(@"
                UPDATE ""Rates""
                SET ""SearchName"" = trim(regexp_replace(
                    lower(translate(
                        trim(
                            CASE
                                WHEN ""ServiceType"" ILIKE 'hotel'
                                    THEN COALESCE(NULLIF(trim(""HotelName""), ''), ""ProductName"")
                                ELSE ""ProductName""
                            END
                        ),
                        'áéíóúüñÁÉÍÓÚÜÑ',
                        'aeiouunAEIOUUN'
                    )),
                    '\s+', ' ', 'g'
                ))
                WHERE ""SearchName"" IS NULL;");

            // Indice GIN trigram sobre SearchName, para que el buscador find-or-create de la venta
            // sea rapido. IF NOT EXISTS = idempotente (mismo patron que AddRateFuzzyMatching). La
            // extension pg_trgm y la clase gin_trgm_ops ya las instalo AddRateFuzzyMatching, mas
            // arriba en la cola; por eso aca no hace falta CREATE EXTENSION.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Rates_SearchName_trgm""
                    ON ""Rates"" USING GIN (""SearchName"" gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // El indice trigram se crea por SQL crudo (no esta en el modelo), asi que se dropea explicito.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Rates_SearchName_trgm"";");

            migrationBuilder.DropForeignKey(
                name: "FK_Rates_TravelFiles_CreatedFromReservaId",
                table: "Rates");

            migrationBuilder.DropTable(
                name: "RateSupplierSales");

            migrationBuilder.DropIndex(
                name: "IX_Rates_CreatedFromReservaId",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "CreatedFromReservaId",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "CreatedInSale",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "SearchName",
                table: "Rates");
        }
    }
}
