using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Pieza C "tarifario que se llena solo" (2026-05-30): habilita la deteccion
    /// difusa de tarifas duplicadas.
    ///
    /// Que hace:
    ///   1. Instala la extension pg_trgm de Postgres (trae el operador % y la
    ///      funcion similarity(), que miden cuan parecidos son dos textos).
    ///   2. Crea dos indices GIN trigram sobre lower("HotelName") y
    ///      lower("ProductName"), para que la busqueda difusa sea rapida.
    ///
    /// Por que es seguro: es 100% aditivo. No toca datos existentes, no cambia
    /// columnas, no rompe el esquema. Si la extension no se pudiera crear, la
    /// migracion falla limpio en el primer statement (CREATE EXTENSION) ANTES de
    /// tocar nada, asi que no deja la base a medias.
    /// </summary>
    public partial class AddRateFuzzyMatching : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // pg_trgm viene en el paquete contrib de postgres:16 y el usuario
            // owner de la base puede crearla. IF NOT EXISTS la hace idempotente:
            // si ya estaba instalada (otro feature pudo instalarla), no falla.
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS pg_trgm;");

            // Indices GIN trigram sobre el texto en minuscula. La busqueda difusa
            // compara con lower(col) % lower(@input); el indice tiene que estar
            // construido sobre la MISMA expresion (lower(col)) para poder usarlo.
            //
            // gin_trgm_ops es la "clase de operadores" que le dice al indice GIN
            // como partir el texto en trigramas. Sin pg_trgm instalada esta clase
            // no existe; por eso el CREATE EXTENSION va primero.
            //
            // lower(NULL) es NULL y GIN no indexa NULLs: las tarifas sin HotelName
            // o sin ProductName simplemente no entran al indice, sin error.
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Rates_HotelName_trgm""
                    ON ""Rates"" USING GIN (lower(""HotelName"") gin_trgm_ops);");

            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_Rates_ProductName_trgm""
                    ON ""Rates"" USING GIN (lower(""ProductName"") gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Solo borramos los indices que creamos.
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Rates_ProductName_trgm"";");
            migrationBuilder.Sql(@"DROP INDEX IF EXISTS ""IX_Rates_HotelName_trgm"";");

            // A PROPOSITO no dropeamos la extension pg_trgm: otros features (o
            // futuras busquedas) podrian estar usandola. Dropear una extension de
            // la que dependen otros objetos tiraria error o los rompeira en
            // cascada. Si algun dia se quiere sacar, hacerlo en una migracion
            // dedicada despues de verificar que nadie mas la usa.
        }
    }
}
