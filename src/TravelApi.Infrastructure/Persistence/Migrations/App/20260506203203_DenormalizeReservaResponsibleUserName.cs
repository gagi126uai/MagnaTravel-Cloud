using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// C16 — Saca ApplicationUser (ASP.NET Identity) del proyecto Domain.
    ///
    /// Como Domain ya no puede tener nav prop a ApplicationUser, denormalizamos el
    /// FullName del responsable en una columna de TravelFiles (Reservas).
    /// Patron consistente con Voucher.CreatedByUserName.
    ///
    /// La FK formal "FK_TravelFiles_AspNetUsers_ResponsibleUserId" se mantiene intacta
    /// (declarada desde Infrastructure via HasOne&lt;ApplicationUser&gt;()...HasForeignKey(...)),
    /// por lo que esta migracion es puramente aditiva y no debe contener DropForeignKey.
    /// </summary>
    public partial class DenormalizeReservaResponsibleUserName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ResponsibleUserName",
                table: "TravelFiles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            // Backfill: copiar el FullName del usuario actualmente asignado en cada
            // reserva. Si una reserva no tiene ResponsibleUserId, queda NULL (correcto).
            // Solo afecta filas cuyo ResponsibleUserName ya este NULL para hacer la
            // operacion idempotente si la migracion se reaplicara manualmente.
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles""
                SET ""ResponsibleUserName"" = u.""FullName""
                FROM ""AspNetUsers"" u
                WHERE ""TravelFiles"".""ResponsibleUserId"" = u.""Id""
                  AND ""TravelFiles"".""ResponsibleUserName"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Down() solo revierte la columna. No hace falta deshacer el backfill
            // (al borrar la columna desaparecen todos los valores).
            migrationBuilder.DropColumn(
                name: "ResponsibleUserName",
                table: "TravelFiles");
        }
    }
}
