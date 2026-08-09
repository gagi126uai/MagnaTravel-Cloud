using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Configuracion de la inteligencia artificial guardada en la base (spec firmada 2026-08-07
    /// §15, M-28). Migracion 100% ADITIVA: crea UNA tabla nueva y no toca ni una fila existente.
    ///
    /// <para><b>Que guarda</b>: con cual de las IA de la calle trabaja la agencia, su direccion y
    /// modelo, la clave CIFRADA (mismo mecanismo que ya protege los datos sensibles de ARCA), los
    /// 4 caracteres del principio de la clave —lo unico mostrable—, como le fue a la ultima prueba
    /// de conexion, y quien la toco por ultima vez.</para>
    ///
    /// <para><b>Por que la clave va a la base, si ADR-016 decia que no</b>: derogacion firmada del
    /// dueño el 2026-08-07 (§15.11 de la spec). El dueño de una agencia tiene que poder configurar
    /// su IA desde la pantalla, sin un tecnico y sin tocar el servidor. Las variables de entorno
    /// <c>Ai__*</c> quedan como respaldo. La clave nunca se guarda en claro y nunca sale por la API.</para>
    ///
    /// <para><b>Vuelta atras</b>: el <c>Down</c> borra la tabla. Lo unico que se pierde es la
    /// configuracion de la IA, que se vuelve a cargar desde la pantalla en un minuto; ninguna
    /// reserva, factura ni cobro depende de esta tabla.</para>
    ///
    /// <para><b>Ojo con el bootstrapper de SQL crudo</b> (bug historico 42701): esta tabla la crea
    /// SOLO esta migracion. No se agrega a <c>OperationalFinanceSchemaBootstrapper</c> ni a ningun
    /// otro parche de arranque — duplicar la creacion es justo lo que rompia el arranque.</para>
    /// </summary>
    public partial class AiSettings_M1_AddAiSettingsTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Model = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ApiKeyPrefix = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    LastTestOutcome = table.Column<int>(type: "integer", nullable: true),
                    LastTestAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UpdatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    UpdatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiSettings");
        }
    }
}
