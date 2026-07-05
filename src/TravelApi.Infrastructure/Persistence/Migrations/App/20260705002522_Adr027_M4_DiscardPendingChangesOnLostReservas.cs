using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// (2026-07-04, ADR-027) REPARACION DE DATOS LEGACY — continuación de <c>Adr027_M3</c> para el estado
    /// <c>Lost</c> (Perdido). La M3 limpió la marca "confirmada con cambios" colgada en las reservas terminales
    /// <c>PendingOperatorRefund</c>, <c>Cancelled</c> y <c>Closed</c>, pero su <c>WHERE</c> NO incluía <c>Lost</c>
    /// (hueco B2 de la auditoría 2026-07-04). Una cotización/presupuesto que se editó de precio y luego se marcó
    /// como Perdida seguía mostrando el cartel "Se editaron precios..." y el badge "Con cambios", igual que pasaba
    /// con las anuladas. Esta migración cierra ese hueco de datos.
    ///
    /// <para><b>Que hace</b>: para las reservas cuyo <c>Status</c> es <c>Lost</c>, borra las filas de detalle
    /// <c>ReservaPendingChanges</c> y apaga la marca (<c>HasUnacknowledgedChanges</c> + <c>ChangesPendingSince</c>).
    /// El fix de código (el PUNTO ÚNICO de transición, que descarta la marca al entrar a Lost) cubre los casos
    /// NUEVOS; esta migración arregla los que ya existían.</para>
    ///
    /// <para><b>Por que es SEGURA</b>: NO cambia el esquema (0 columnas/índices; por eso el ModelSnapshot no
    /// cambia). Es un <c>DELETE</c> + <c>UPDATE</c> acotados por <c>WHERE</c> e IDEMPOTENTES (correrla dos veces no
    /// hace nada: el segundo pase ya no encuentra filas marcadas ni detalle que borrar). No toca la auditoría del OK
    /// humano (<c>ChangesAckBy*</c>): esas reservas nunca fueron "revisadas", los cambios se descartan al perderse.
    /// No toca reservas vivas (InManagement/Confirmed/Traveling), donde la marca SI debe seguir visible. Mismo
    /// patrón exacto que <c>Adr027_M3</c>, solo cambia el estado del <c>WHERE</c>.</para>
    /// </summary>
    public partial class Adr027_M4_DiscardPendingChangesOnLostReservas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Borrar el DETALLE de cambios pendientes ("qué cambió") de las reservas Perdidas. Va primero para no
            //    dejar filas hijas huerfanas. Idempotente: si ya no hay filas, el DELETE no afecta nada. OJO nombres
            //    REALES: la tabla de reservas es "TravelFiles"; el detalle vive en "ReservaPendingChanges" con FK
            //    "ReservaId".
            migrationBuilder.Sql(@"
                DELETE FROM ""ReservaPendingChanges"" pc
                USING ""TravelFiles"" tf
                WHERE pc.""ReservaId"" = tf.""Id""
                  AND tf.""Status"" = 'Lost';
            ");

            // 2) Apagar la marca en las reservas Perdidas que todavia la tengan encendida. El WHERE extra (marca
            //    encendida O fecha presente) hace la migracion idempotente y evita reescribir filas ya limpias (menos
            //    churn de xmin / menos filas tocadas).
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles""
                SET ""HasUnacknowledgedChanges"" = FALSE,
                    ""ChangesPendingSince"" = NULL
                WHERE ""Status"" = 'Lost'
                  AND (""HasUnacknowledgedChanges"" = TRUE OR ""ChangesPendingSince"" IS NOT NULL);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reparacion de datos de una sola via: no se puede "des-descartar" un cambio pendiente que ya se borro
            // (no guardamos snapshot del estado previo). El Down es un no-op deliberado (revertir el esquema no
            // aplica: esta migracion no toca el esquema).
        }
    }
}
