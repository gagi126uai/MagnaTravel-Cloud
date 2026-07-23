using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// FIX bloqueante (2026-07-23): la columna Status de FlightSegments quedo en varchar(2) por un
    /// error de tipeo (el resto de los servicios usa varchar(50)). Postgres tira "22001: value too
    /// long for type character varying(2)" apenas se guarda "Solicitado", asi que dar de alta un
    /// vuelo tiraba 500 SIEMPRE en produccion. Ensancha la columna: es seguro, no pierde datos
    /// (las filas existentes ya entran en 2 caracteres, ahora entran tambien las mas largas).
    /// </summary>
    public partial class Fix500_FlightStatusMaxLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "FlightSegments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(2)",
                oldMaxLength: 2);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // FIX N2 (2026-07-23, nit de review): NO se vuelve a varchar(2). El proyecto es forward-only
            // en PROD (nunca se corre Down contra la base real), pero si alguna vez se ejecutara, achicar
            // la columna con filas que ya tienen "Solicitado"/"Confirmado" guardadas (10+ caracteres)
            // fallaria con el MISMO error 22001 que este fix vino a resolver — un rollback "seguro" no
            // puede reintroducir el bug bloqueante. No-op deliberado: la columna ancha se queda ancha.
        }
    }
}
