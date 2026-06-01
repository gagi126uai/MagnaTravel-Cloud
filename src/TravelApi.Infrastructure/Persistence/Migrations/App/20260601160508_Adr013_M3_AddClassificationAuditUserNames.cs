using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-013 (M1, review 2026-06-01): agrega el NOMBRE legible del usuario que clasifico
    /// el concepto (<c>ConceptClassifiedByUserName</c>) y del que confirmo la penalidad
    /// (<c>PenaltyConfirmedByUserName</c>). Antes solo se guardaba el Id; el resto del modulo
    /// de auditoria fiscal persiste tambien el nombre para que el back-office no tenga que
    /// resolverlo contra AspNetUsers.
    ///
    /// <para><b>Aditiva y segura</b>: dos columnas nullable, sin default, sin tocar datos
    /// existentes (los BCs previos quedan con NULL = "no clasificado con el wiring nuevo").
    /// <b>NO toca el token de concurrencia xmin</b>: BookingCancellation ya lo tenia desde
    /// FC1.1 (no es una columna fisica, es el system column de Postgres). Rollback simple:
    /// drop de las dos columnas.</para>
    /// </summary>
    public partial class Adr013_M3_AddClassificationAuditUserNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ConceptClassifiedByUserName",
                table: "BookingCancellations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PenaltyConfirmedByUserName",
                table: "BookingCancellations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ConceptClassifiedByUserName",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "PenaltyConfirmedByUserName",
                table: "BookingCancellations");
        }
    }
}
