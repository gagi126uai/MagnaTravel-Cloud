using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Tarifario inteligente, fase 1 (spec firmada 2026-08-07): la memoria de precios pasa a ser POR
    /// HABITACION y el bibliotecario gana sus tablas. Migracion ADITIVA salvo un cambio de indice.
    ///
    /// <list type="number">
    ///   <item><b><c>RateSupplierSales.VariantKey</c> + <c>VariantLabel</c></b> (M-12): la variante
    ///   (habitacion+regimen en hotel, cabina en aereo, vehiculo en traslado). Nacen en <c>''</c>: TODAS
    ///   las filas existentes quedan validas, son "la memoria sin variante", que es lo que eran hasta hoy.
    ///   No se adivina la habitacion de las ventas viejas: ese dato nunca se guardo.</item>
    ///
    ///   <item><b>El indice unico pasa de (Rate, Operador) a (Rate, Operador, Variante)</b>. Unico paso NO
    ///   aditivo. <b>Por que no puede fallar</b>: el indice nuevo es MAS permisivo (agrega una columna),
    ///   asi que todo lo que entraba antes entra ahora. La migracion corre ANTES de que el codigo nuevo
    ///   atienda pedidos, y solo el codigo nuevo hace <c>ON CONFLICT</c> contra la clave de tres columnas.</item>
    ///
    ///   <item><b><c>RateSupplierSales.AbsorbedByTidyUpActionId</c> + <c>FromManualLoad</c></b>: la fila
    ///   que pierde un choque al unir queda ESCONDIDA en vez de borrarse (orden del dueño 2026-08-03:
    ///   nada se borra), y se distingue el precio cargado a mano del aprendido vendiendo.</item>
    ///
    ///   <item><b><c>Rates.MergedIntoRateId</c> + <c>MergedAt</c></b> (M-17) y
    ///   <b><c>HotelBookings.RoomCategory</c></b> (el nombre fino de la habitacion, que no tenia donde
    ///   guardarse en la venta). Nullable: las ventas viejas no cambian.</item>
    ///
    ///   <item><b>Tres tablas nuevas</b>: <c>CatalogTidyUpActions</c> (que unio el sistema y cuando),
    ///   <c>CatalogTidyUpSaleChanges</c> (la FOTO de cada fila de precio antes de tocarla — sin esto el
    ///   Deshacer no puede devolver los importes que una union piso) y <c>CatalogNotDuplicatePairs</c>
    ///   (los pares que alguien ya marco como distintos).</item>
    /// </list>
    ///
    /// <para><b>Borrado selectivo</b>: las tablas del bibliotecario estan en <c>TarifarioTables</c> de
    /// <c>SystemDataWipeService</c> (sus foreign keys a <c>Rates</c> son obligatorias, mueren con el
    /// tarifario). Y si un borrado se lleva la memoria de precios SIN llevarse el rastro, las uniones
    /// pendientes quedan marcadas como no reversibles (ver <c>InvalidatePendingTidyUpUndosAsync</c>).</para>
    ///
    /// <para><b>Rollback</b>: el <c>Down</c> devuelve el indice viejo y saca columnas y tablas. Si ya se
    /// hubieran guardado dos precios del mismo producto+operador con habitaciones distintas, volver atras
    /// chocaria contra el indice unico viejo — por eso la vuelta atras real se hace restaurando un backup,
    /// no corriendo el Down (criterio ADR-051).</para>
    /// </summary>
    public partial class TarifarioInteligente_M1_VariantesYBibliotecario : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RateSupplierSales_RateId_SupplierId",
                table: "RateSupplierSales");

            migrationBuilder.AddColumn<int>(
                name: "AbsorbedByTidyUpActionId",
                table: "RateSupplierSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "FromManualLoad",
                table: "RateSupplierSales",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "VariantKey",
                table: "RateSupplierSales",
                type: "character varying(120)",
                maxLength: 120,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VariantLabel",
                table: "RateSupplierSales",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MergedAt",
                table: "Rates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MergedIntoRateId",
                table: "Rates",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RoomCategory",
                table: "HotelBookings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CatalogNotDuplicatePairs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LowRateId = table.Column<int>(type: "integer", nullable: false),
                    HighRateId = table.Column<int>(type: "integer", nullable: false),
                    MarkedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    MarkedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogNotDuplicatePairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogNotDuplicatePairs_Rates_HighRateId",
                        column: x => x.HighRateId,
                        principalTable: "Rates",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CatalogNotDuplicatePairs_Rates_LowRateId",
                        column: x => x.LowRateId,
                        principalTable: "Rates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatalogTidyUpActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    SurvivingRateId = table.Column<int>(type: "integer", nullable: false),
                    AbsorbedRateId = table.Column<int>(type: "integer", nullable: false),
                    AbsorbedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SurvivingName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VariantLabelRescued = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    VariantKeyRescued = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    AbsorbedProductName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecidedByTheSystem = table.Column<bool>(type: "boolean", nullable: false),
                    PerformedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    PerformedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UndoneAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UndoneByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UndoBlockedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogTidyUpActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogTidyUpActions_Rates_AbsorbedRateId",
                        column: x => x.AbsorbedRateId,
                        principalTable: "Rates",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_CatalogTidyUpActions_Rates_SurvivingRateId",
                        column: x => x.SurvivingRateId,
                        principalTable: "Rates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CatalogTidyUpSaleChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TidyUpActionId = table.Column<int>(type: "integer", nullable: false),
                    RateSupplierSaleId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PreviousRateId = table.Column<int>(type: "integer", nullable: false),
                    PreviousSupplierId = table.Column<int>(type: "integer", nullable: false),
                    PreviousVariantKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    PreviousVariantLabel = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PreviousSoldAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreviousNetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PreviousTax = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PreviousSalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    PreviousCurrency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    PreviousPriceUnit = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PreviousReservaId = table.Column<int>(type: "integer", nullable: true),
                    PreviousSalesCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CatalogTidyUpSaleChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CatalogTidyUpSaleChanges_CatalogTidyUpActions_TidyUpActionId",
                        column: x => x.TidyUpActionId,
                        principalTable: "CatalogTidyUpActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CatalogTidyUpSaleChanges_RateSupplierSales_RateSupplierSale~",
                        column: x => x.RateSupplierSaleId,
                        principalTable: "RateSupplierSales",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_RateSupplierSales_AbsorbedByTidyUpActionId",
                table: "RateSupplierSales",
                column: "AbsorbedByTidyUpActionId");

            // Indice unico PARCIAL: solo cuentan las filas VISIBLES. Las que una union escondio quedan
            // afuera, para que no ocupen el casillero (volver a unir despues de deshacer chocaba) ni se
            // lleven el ON CONFLICT de una venta nueva (el precio se aprendia en una fila invisible).
            migrationBuilder.CreateIndex(
                name: "IX_RateSupplierSales_RateId_SupplierId_VariantKey",
                table: "RateSupplierSales",
                columns: new[] { "RateId", "SupplierId", "VariantKey" },
                unique: true,
                filter: "\"AbsorbedByTidyUpActionId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Rates_MergedIntoRateId",
                table: "Rates",
                column: "MergedIntoRateId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogNotDuplicatePairs_HighRateId",
                table: "CatalogNotDuplicatePairs",
                column: "HighRateId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogNotDuplicatePairs_LowRateId_HighRateId",
                table: "CatalogNotDuplicatePairs",
                columns: new[] { "LowRateId", "HighRateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTidyUpActions_AbsorbedRateId",
                table: "CatalogTidyUpActions",
                column: "AbsorbedRateId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTidyUpActions_SurvivingRateId",
                table: "CatalogTidyUpActions",
                column: "SurvivingRateId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTidyUpActions_UndoneAt_PerformedAt",
                table: "CatalogTidyUpActions",
                columns: new[] { "UndoneAt", "PerformedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTidyUpSaleChanges_RateSupplierSaleId",
                table: "CatalogTidyUpSaleChanges",
                column: "RateSupplierSaleId");

            migrationBuilder.CreateIndex(
                name: "IX_CatalogTidyUpSaleChanges_TidyUpActionId",
                table: "CatalogTidyUpSaleChanges",
                column: "TidyUpActionId");

            migrationBuilder.AddForeignKey(
                name: "FK_Rates_Rates_MergedIntoRateId",
                table: "Rates",
                column: "MergedIntoRateId",
                principalTable: "Rates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Rates_Rates_MergedIntoRateId",
                table: "Rates");

            migrationBuilder.DropTable(
                name: "CatalogNotDuplicatePairs");

            migrationBuilder.DropTable(
                name: "CatalogTidyUpSaleChanges");

            migrationBuilder.DropTable(
                name: "CatalogTidyUpActions");

            migrationBuilder.DropIndex(
                name: "IX_RateSupplierSales_AbsorbedByTidyUpActionId",
                table: "RateSupplierSales");

            migrationBuilder.DropIndex(
                name: "IX_RateSupplierSales_RateId_SupplierId_VariantKey",
                table: "RateSupplierSales");

            migrationBuilder.DropIndex(
                name: "IX_Rates_MergedIntoRateId",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "AbsorbedByTidyUpActionId",
                table: "RateSupplierSales");

            migrationBuilder.DropColumn(
                name: "FromManualLoad",
                table: "RateSupplierSales");

            migrationBuilder.DropColumn(
                name: "VariantKey",
                table: "RateSupplierSales");

            migrationBuilder.DropColumn(
                name: "VariantLabel",
                table: "RateSupplierSales");

            migrationBuilder.DropColumn(
                name: "MergedAt",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "MergedIntoRateId",
                table: "Rates");

            migrationBuilder.DropColumn(
                name: "RoomCategory",
                table: "HotelBookings");

            migrationBuilder.CreateIndex(
                name: "IX_RateSupplierSales_RateId_SupplierId",
                table: "RateSupplierSales",
                columns: new[] { "RateId", "SupplierId" },
                unique: true);
        }
    }
}
