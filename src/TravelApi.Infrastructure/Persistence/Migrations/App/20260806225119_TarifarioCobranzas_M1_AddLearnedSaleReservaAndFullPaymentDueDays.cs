using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Rediseño de Tarifario y Cobranzas (spec firmada 2026-08-06): migracion 100% ADITIVA, sin ningun
    /// DROP ni ALTER destructivo. Agrega tres cosas:
    ///
    /// <list type="number">
    ///   <item><b><c>RateSupplierSales.LastReservaId</c></b> (M-1): de que reserva salio el ultimo precio
    ///   aprendido, para que el Tarifario pueda mostrar "US$ 48 · 22/05/2026 · F-2026-1042" con el numero
    ///   como enlace. Nullable + FK <c>ON DELETE SET NULL</c> (mismo criterio que
    ///   <c>Rates.CreatedFromReservaId</c>): si esa reserva desapareciera, el precio NO se pierde. Las
    ///   filas que ya existian quedan en null y se completan solas en la proxima venta.</item>
    ///
    ///   <item><b><c>OperationalFinanceSettings.FullPaymentDueDaysBeforeDeparture</c></b> (P15=A / M-8):
    ///   "el saldo tiene que estar completo N dias antes de la salida". Se crea con <b>default 21</b>, el
    ///   numero que firmo el dueño, y se PISA explicitamente la fila existente (que EF crearia en 0). Un 0
    ///   aca haria que toda reserva con deuda apareciera vencida el mismo dia de la salida, asi que el
    ///   backfill no es cosmetico: es correctitud.</item>
    ///
    ///   <item><b>Backfill de <c>Rates.SearchName</c> faltantes</b> (P7 "evitar repetidos a toda costa"):
    ///   el alta desde el formulario largo del tarifario NUNCA escribia el nombre normalizado (recien se
    ///   arregla en esta misma tanda), asi que esos productos eran INVISIBLES para el buscador que aprende
    ///   y para el find-or-create — el mismo hotel se podia cargar dos veces. Se completan solo las filas
    ///   con <c>SearchName IS NULL</c> (idempotente: no pisa nada que ya haya escrito la app), con la
    ///   MISMA regla type-aware del backfill original de ADR-017 F1.1.</item>
    /// </list>
    ///
    /// <para><b>Rollback</b>: el <c>Down</c> saca la FK, el indice y las dos columnas. El backfill de
    /// SearchName NO se revierte a proposito: dejar el nombre normalizado escrito no rompe nada y volver a
    /// ponerlo en null solo reintroduciria el agujero de duplicados.</para>
    /// </summary>
    public partial class TarifarioCobranzas_M1_AddLearnedSaleReservaAndFullPaymentDueDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LastReservaId",
                table: "RateSupplierSales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FullPaymentDueDaysBeforeDeparture",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 21);

            // Red de seguridad: Postgres ya deja las filas existentes en 21 al agregar la columna con
            // DEFAULT, pero si alguna base quedara en 0 (por un ADD COLUMN previo a mano), un 0 haria que
            // TODA reserva con deuda figurara vencida. Barato de asegurar, caro de descubrir en produccion.
            migrationBuilder.Sql(@"
                UPDATE ""OperationalFinanceSettings""
                SET ""FullPaymentDueDaysBeforeDeparture"" = 21
                WHERE ""FullPaymentDueDaysBeforeDeparture"" < 1;");

            migrationBuilder.CreateIndex(
                name: "IX_RateSupplierSales_LastReservaId",
                table: "RateSupplierSales",
                column: "LastReservaId");

            migrationBuilder.AddForeignKey(
                name: "FK_RateSupplierSales_TravelFiles_LastReservaId",
                table: "RateSupplierSales",
                column: "LastReservaId",
                principalTable: "TravelFiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            // Backfill type-aware de SearchName (misma normalizacion SQL que ADR-017 F1.1: minuscula, sin
            // tildes del set español, espacios colapsados). Hotel toma el nombre del hotel si lo tiene; el
            // resto de los tipos, el nombre del producto.
            // SQL copiado TAL CUAL del backfill original de ADR-017 F1.1 (misma regla, mismo resultado):
            // si se re-escribiera "parecido" podrian quedar dos normalizaciones distintas conviviendo.
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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RateSupplierSales_TravelFiles_LastReservaId",
                table: "RateSupplierSales");

            migrationBuilder.DropIndex(
                name: "IX_RateSupplierSales_LastReservaId",
                table: "RateSupplierSales");

            migrationBuilder.DropColumn(
                name: "LastReservaId",
                table: "RateSupplierSales");

            migrationBuilder.DropColumn(
                name: "FullPaymentDueDaysBeforeDeparture",
                table: "OperationalFinanceSettings");
        }
    }
}
