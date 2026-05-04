using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Renombra los strings persistidos de Reserva.Status de espanol a ingles para
    /// alinearlos con los nombres de los miembros del enum EstadoReserva (Confirmed,
    /// Traveling, Closed, Budget, Cancelled). El cambio en el codigo cambio los strings
    /// del enum, esta migracion actualiza los datos existentes para que sigan siendo
    /// consultables.
    ///
    /// IMPORTANTE: tambien recategoriza el viejo "Operativo" en dos buckets segun
    /// fecha (decision funcional del refactor de ciclo de vida):
    ///   - StartDate &lt;= hoy -> Traveling (el viaje realmente arranco)
    ///   - StartDate &gt; hoy o NULL -> Confirmed (estaba en Operativo solo porque
    ///     pago todo, pero el cliente no estaba viajando)
    /// </summary>
    public partial class RenameReservaStatusToEnglish : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Recategorizar Operativo segun fecha. Las que aun no salieron pasan a
            //    Confirmed (no estaban realmente viajando). Las que ya salieron pasan
            //    a Traveling.
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles""
                SET ""Status"" = 'Confirmed'
                WHERE ""Status"" = 'Operativo'
                  AND (""StartDate"" IS NULL OR ""StartDate""::date > CURRENT_DATE);
            ");

            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles""
                SET ""Status"" = 'Traveling'
                WHERE ""Status"" = 'Operativo';
            ");

            // 2) Renames directos del resto.
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Confirmed' WHERE ""Status"" = 'Reservado';
            ");
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Closed' WHERE ""Status"" = 'Cerrado';
            ");
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Budget' WHERE ""Status"" = 'Presupuesto';
            ");
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Cancelled' WHERE ""Status"" = 'Cancelado';
            ");
            // 'Archived' queda como esta (legacy en ingles).
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback: vuelve los strings a espanol. La recategorizacion de Operativo
            // NO se puede revertir 1:1 (perdimos la distincion al separar), asi que
            // mapeamos Confirmed -> Reservado y Traveling -> Operativo, lo cual deja
            // todo en el estado anterior aceptable.
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Reservado' WHERE ""Status"" = 'Confirmed';
            ");
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Operativo' WHERE ""Status"" = 'Traveling';
            ");
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Cerrado' WHERE ""Status"" = 'Closed';
            ");
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Presupuesto' WHERE ""Status"" = 'Budget';
            ");
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles"" SET ""Status"" = 'Cancelado' WHERE ""Status"" = 'Cancelled';
            ");
        }
    }
}
