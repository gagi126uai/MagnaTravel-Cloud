using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// (2026-07-03, ADR-027) REPARACION DE DATOS LEGACY: limpia la marca "confirmada con cambios" que quedo colgada
    /// en reservas ya anuladas/finalizadas. La marca (<c>HasUnacknowledgedChanges</c> + <c>ChangesPendingSince</c>)
    /// solo se ponia en estados vivos y solo se limpiaba al dar el OK; el flujo de anulacion nunca la tocaba, asi
    /// que reservas que se anularon con un cambio de precio pendiente de revisar seguian mostrando el cartel
    /// "Se editaron precios..." y el badge "Con cambios" en la ficha, aunque el viaje ya estuviera sin efecto.
    ///
    /// <para><b>Que hace</b>: para las reservas cuyo <c>Status</c> es <c>PendingOperatorRefund</c>,
    /// <c>Cancelled</c> o <c>Closed</c> (los estados terminales donde una revision de cambios ya no tiene sentido),
    /// borra las filas de detalle <c>ReservaPendingChanges</c> y apaga la marca. El fix de codigo (que descarta la
    /// marca al anular) cubre las anulaciones NUEVAS; esta migracion arregla las que ya existian.</para>
    ///
    /// <para><b>Por que es SEGURA</b>: NO cambia el esquema (0 columnas/indices). Es un UPDATE + DELETE acotados por
    /// <c>WHERE</c> e IDEMPOTENTES (correrla dos veces no hace nada: el segundo pase ya no encuentra filas marcadas
    /// ni detalle que borrar). No toca la auditoria del OK humano (<c>ChangesAckBy*</c>): esas reservas nunca fueron
    /// "revisadas", los cambios se descartan por la anulacion. No toca reservas vivas
    /// (InManagement/Confirmed/Traveling), donde la marca SI debe seguir visible.</para>
    /// </summary>
    public partial class Adr027_M3_DiscardPendingChangesOnAnnulledReservas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Borrar el DETALLE de cambios pendientes ("qué cambió") de las reservas terminales. Va primero para
            //    no dejar filas hijas huerfanas. Idempotente: si ya no hay filas, el DELETE no afecta nada.
            //    OJO nombres REALES: la tabla de reservas es "TravelFiles"; el detalle vive en
            //    "ReservaPendingChanges" con FK "ReservaId".
            migrationBuilder.Sql(@"
                DELETE FROM ""ReservaPendingChanges"" pc
                USING ""TravelFiles"" tf
                WHERE pc.""ReservaId"" = tf.""Id""
                  AND tf.""Status"" IN ('PendingOperatorRefund', 'Cancelled', 'Closed');
            ");

            // 2) Apagar la marca en las reservas terminales que todavia la tengan encendida. El WHERE extra
            //    (marca encendida O fecha presente) hace la migracion idempotente y evita reescribir filas que ya
            //    estan limpias (menos churn de xmin / menos filas tocadas).
            migrationBuilder.Sql(@"
                UPDATE ""TravelFiles""
                SET ""HasUnacknowledgedChanges"" = FALSE,
                    ""ChangesPendingSince"" = NULL
                WHERE ""Status"" IN ('PendingOperatorRefund', 'Cancelled', 'Closed')
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
