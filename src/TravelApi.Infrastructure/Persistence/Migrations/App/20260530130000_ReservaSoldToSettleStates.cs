using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// Rediseño maquina de estados Reserva (Fase A+B, 2026-05-30): agrega los dos estados
    /// nuevos del ciclo de vida ("Sold" / Vendida y "ToSettle" / A liquidar) y el flag
    /// maestro que los gobierna. Migracion 100% ADITIVA: no borra datos, no reescribe filas.
    ///
    /// <para>Dos cambios:</para>
    /// <list type="number">
    /// <item><c>OperationalFinanceSettings.EnableSoldToSettleStates</c> = bool NOT NULL default
    /// <c>false</c>. Las filas existentes quedan en false -&gt; el ciclo de vida se comporta
    /// byte-identico a hoy. El flag se prende a mano (SQL/seed) cuando el frontend este listo.</item>
    /// <item>El CHECK <c>chk_TravelFiles_status_valid</c> se dropea y recrea con 9 valores
    /// (los 7 historicos + 'Sold' + 'ToSettle'). Sin esto, intentar persistir una reserva en
    /// los estados nuevos rebota con violacion de CHECK aunque el flag este prendido.</item>
    /// </list>
    ///
    /// <para>NOTA: el review descarto el concurrency token <c>xmin</c> en Reserva (se activaba con
    /// el flag apagado y exponia caminos viejos a DbUpdateConcurrencyException). Por eso esta
    /// migracion NO registra ninguna shadow property xmin en el snapshot.</para>
    ///
    /// <para><b>Orden de deploy</b>: aplicar esta migracion ANTES de subir el binario nuevo
    /// (orden estandar del repo). La app vieja ignora la columna nueva y nunca escribe los
    /// estados nuevos; la app nueva los usa solo si el flag esta prendido.</para>
    /// </summary>
    public partial class ReservaSoldToSettleStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // (1) Flag maestro de los estados nuevos. OFF en prod hasta que el frontend
            //     muestre los chips/labels de Sold y ToSettle.
            migrationBuilder.AddColumn<bool>(
                name: "EnableSoldToSettleStates",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // (2) Reescritura del CHECK de status: 7 valores historicos + 'Sold' + 'ToSettle'.
            //     La tabla se llama "TravelFiles" (la entidad Reserva fue renombrada via ToTable;
            //     desalineo historico). Postgres: sintaxis "doublequotes".
            //     DROP IF EXISTS + ADD para que sea idempotente y no dependa del nombre exacto
            //     que tenia el constraint previo.
            migrationBuilder.Sql("""
                ALTER TABLE "TravelFiles"
                  DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;
                ALTER TABLE "TravelFiles"
                  ADD CONSTRAINT chk_TravelFiles_status_valid
                  CHECK ("Status" IN (
                    'Budget',
                    'Sold',
                    'Confirmed',
                    'Traveling',
                    'ToSettle',
                    'Closed',
                    'Cancelled',
                    'PendingOperatorRefund',
                    'Archived'
                  ));
                """);

            // (Nota) NO se registra xmin como concurrency token de Reserva: el review lo descarto
            //         porque se activaba con el flag apagado y exponia caminos viejos a
            //         DbUpdateConcurrencyException. La migracion queda: AddColumn flag + CHECK 9 valores.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rollback: el CHECK vuelve a los 7 valores historicos (sin Sold ni ToSettle).
            // PRECONDICION para rollback seguro: no debe quedar ninguna reserva en 'Sold' o
            // 'ToSettle', sino el ADD CONSTRAINT falla. Como el flag arranca OFF y los estados
            // nuevos solo se escriben con el flag ON, en un entorno donde nunca se prendio el
            // flag el rollback es limpio. Si se prendio y hay reservas en los estados nuevos,
            // primero hay que moverlas a un estado historico antes de revertir.
            migrationBuilder.Sql("""
                ALTER TABLE "TravelFiles"
                  DROP CONSTRAINT IF EXISTS chk_TravelFiles_status_valid;
                ALTER TABLE "TravelFiles"
                  ADD CONSTRAINT chk_TravelFiles_status_valid
                  CHECK ("Status" IN (
                    'Budget',
                    'Confirmed',
                    'Traveling',
                    'Closed',
                    'Cancelled',
                    'PendingOperatorRefund',
                    'Archived'
                  ));
                """);

            migrationBuilder.DropColumn(
                name: "EnableSoldToSettleStates",
                table: "OperationalFinanceSettings");
        }
    }
}
