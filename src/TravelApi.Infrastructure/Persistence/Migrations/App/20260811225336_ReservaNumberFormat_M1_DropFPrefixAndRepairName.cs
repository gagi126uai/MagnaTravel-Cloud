using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Decision del dueño (2026-08-11): el numero de reserva pierde el prefijo "F-" — ya no representaba
    /// nada para el usuario. Formato viejo <c>"F-2026-1067"</c> -&gt; formato nuevo <c>"2026-1067"</c>. El
    /// generador de numeros nuevos ya cambio en el codigo (<c>ReservaService.GenerateNumeroReservaAsync</c>);
    /// esta migracion es SOLO el saneo de los datos historicos que ya nacieron con el prefijo viejo.
    ///
    /// <para><b>Tabla real</b>: el dominio le dice "Reserva" pero en Postgres la tabla es
    /// <c>"TravelFiles"</c> y la columna es <c>"FileNumber"</c> (lección conocida del repo: siempre
    /// validar el SQL crudo contra los nombres reales de la base, no contra los nombres del dominio).</para>
    ///
    /// <para><b>Por que un UPDATE con las dos columnas en el MISMO enunciado</b>: <c>"Name"</c> guarda cosas
    /// como <c>"Reserva F-2026-1067"</c> (nombre autogenerado en el alta) o, mas raro, un nombre que el
    /// usuario escribio a mano y que incluye el numero. Postgres evalua TODAS las expresiones del
    /// <c>SET</c> contra la fila ANTES de aplicar el UPDATE, asi que <c>"FileNumber"</c> del lado del
    /// <c>REPLACE</c> todavia vale el numero VIEJO (con el "F-") aunque esa misma columna se este
    /// actualizando en la linea de abajo. Si <c>"Name"</c> no contiene el numero viejo tal cual,
    /// <c>REPLACE</c> no toca nada (no rompe nombres personalizados que no lo mencionan).</para>
    ///
    /// <para><b>Idempotente</b>: el <c>WHERE</c> exige el prefijo "F-" con el patron exacto
    /// <c>F-AAAA-numero</c>. Despues de correr una vez, ninguna fila matchea mas ese patron (ya quedaron
    /// sin el prefijo), asi que correrla de nuevo no toca nada.</para>
    ///
    /// <para><b>Down() reversible</b>: NO es un caso ambiguo (a diferencia de un NULL vs. vacio que no se
    /// puede distinguir despues de saneado) — el patron nuevo <c>AAAA-numero</c> es reconocible sin
    /// ambiguedad, asi que el rollback puede reponer el prefijo "F-" con el mismo mecanismo simetrico.</para>
    /// </summary>
    public partial class ReservaNumberFormat_M1_DropFPrefixAndRepairName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "TravelFiles"
                SET
                    "Name" = REPLACE("Name", "FileNumber", regexp_replace("FileNumber", '^F-', '')),
                    "FileNumber" = regexp_replace("FileNumber", '^F-', '')
                WHERE "FileNumber" ~ '^F-[0-9]{4}-[0-9]+$';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "TravelFiles"
                SET
                    "Name" = REPLACE("Name", "FileNumber", 'F-' || "FileNumber"),
                    "FileNumber" = 'F-' || "FileNumber"
                WHERE "FileNumber" ~ '^[0-9]{4}-[0-9]+$';
                """);
        }
    }
}
