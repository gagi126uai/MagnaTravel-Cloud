using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Decision firmada 2026-08-19 ("avisos que trabajan, no que gritan"): el banner naranja global
    /// (<c>UrgentBannerStack</c>) queda RESERVADO a caidas de TODO el sistema; todo aviso de negocio
    /// puntual pasa a vivir SOLO en la campanita, como "Normal". El 2026-08-22 se bajaron los ULTIMOS
    /// 6 productores que todavia creaban avisos "Urgent" (ver el commit de ese dia): el monitor de
    /// reservas que salen sin cobrar, el vigia de coherencia, y las 3 alertas de notas de credito
    /// parciales trabadas + la marca "confirmada con cambios".
    ///
    /// <para><b>Por que hace falta esta migracion ademas del fix en el codigo</b>: cambiar el codigo
    /// SOLO afecta los avisos que se crean de aca en mas. Los avisos "Urgent" que estos mismos 6 jobs
    /// ya habian creado y que TODAVIA estan vivos (nadie los resolvio, leyo ni descarto) se quedarian
    /// disparando el banner naranja para siempre si nadie les toca el dato viejo. Esta migracion
    /// normaliza ESOS avisos residuales a "Normal", una sola vez.</para>
    ///
    /// <para><b>Por que es seguro tocar por <c>RelatedEntityType</c></b>: de los 6 tipos, 5 son
    /// EXCLUSIVOS de estos jobs (nadie mas los crea). El sexto (<c>"Reserva"</c>) tambien lo usa
    /// <c>ServiceResolutionFailureNotifier</c>, pero ese productor SIEMPRE crea sus avisos en "Normal"
    /// desde que existe (2026-08-18) — nunca hay una fila "Reserva"+"Urgent" que no venga de
    /// <c>ReservaAutoStateService</c>. El filtro <c>"Priority" = 'Urgent'</c> ademas asegura que un
    /// aviso "Reserva" ya Normal no se toque (no hace falta, pero es la doble red de seguridad barata).</para>
    ///
    /// <para><b>Por que solo los NO resueltos</b>: un aviso ya <c>ResolvedAt IS NOT NULL</c> es
    /// historico — nadie lo va a volver a ver en la campanita ni en un banner. Tocarlo no cambia nada
    /// visible y solo agrega ruido al UPDATE; por eso el WHERE lo excluye.</para>
    ///
    /// <para><b>Idempotente</b>: correrla una segunda vez (o en una base que ya nacio con el codigo
    /// nuevo, sin filas "Urgent" de estos tipos) actualiza cero filas — el propio WHERE deja de
    /// matchear en cuanto la fila pasa a "Normal".</para>
    ///
    /// <para><b>Down() vacio a proposito</b>: no hay "vuelta atras" deseable — revertir significaria
    /// resucitar el banner naranja para avisos de negocio puntuales, exactamente lo que la decision del
    /// dueño del 2026-08-19 pidió sacar (mismo criterio que
    /// <c>BackfillManualCashMovementLegacyDescriptions</c>, T-8: migracion correctiva de datos, no
    /// estructural).</para>
    /// </summary>
    public partial class BackfillUrgentToNormalOperationalNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE ""Notifications""
                SET ""Priority"" = 'Normal'
                WHERE ""Priority"" = 'Urgent'
                  AND ""ResolvedAt"" IS NULL
                  AND ""RelatedEntityType"" IN (
                    'ReservaUnpaidDeparture',
                    'CoherenceWatchdogReport',
                    'PartialCreditNoteBridgeReconciliationFailed',
                    'PartialCreditNotePostingStuck',
                    'PartialCreditNoteReviewPending',
                    'Reserva'
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin revertir a proposito: ver el XML-doc de la clase ("Down() vacio a proposito").
        }
    }
}
