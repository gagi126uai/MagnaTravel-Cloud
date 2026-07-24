using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Obra "anular sin factura" (2026-07-23, decisión del dueño, respaldo fiscal Ley de IVA art. 5 inc. b) —
    /// última pieza. Relaja <c>chk_BookingCancellations_fiscalsnapshot_consistent</c> (INV-118, creado en
    /// FC1_AddCancellationModule) para que un BC SIN ancla fiscal (<c>OriginatingInvoiceId IS NULL</c>) pueda
    /// caminar 2 (AwaitingOperatorRefund) → 3 (ClientCreditApplied) → 4 (Closed) SIN necesitar el
    /// <c>FiscalSnapshot</c> completo, porque un BC sin ancla NUNCA emite Nota de Crédito (nunca hay evento
    /// fiscal que fotografiar — guard R4 en <c>BookingCancellationService.ConfirmAsync</c>).
    ///
    /// <para><b>Por qué la excepción es GLOBAL (cualquier Status) y no solo para 2/3/4</b>: la regla de negocio
    /// real es "sin factura, nunca hace falta snapshot fiscal", sin importar en qué estado esté. Enumerar los
    /// estados exactos (0, 2, 3, 4, 6) sería frágil: cualquier estado nuevo que se agregue al camino sin-factura
    /// el día de mañana quedaría bloqueado hasta acordarse de tocar este CHECK de nuevo. Con
    /// <c>"OriginatingInvoiceId" IS NULL</c> como cláusula GLOBAL del OR, un BC sin ancla nunca puede violar
    /// este CHECK, sea cual sea su Status — la regla queda expresada UNA vez, correcta para siempre.</para>
    ///
    /// <para><b>Las filas CON factura NO cambian</b>: siguen exigiendo exactamente lo mismo que antes
    /// (Status IN (0, 6) O snapshot completo). Este es un WIDENING puro (agrega una tercera vía al OR que
    /// nunca se puede activar con "OriginatingInvoiceId" no nulo) — no hace falta backfill, ninguna fila
    /// existente cambia de válida a inválida ni al revés.</para>
    /// </summary>
    public partial class AnnulWithoutInvoice_RelaxFiscalSnapshotCheck : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalsnapshot_consistent;
                ALTER TABLE "BookingCancellations"
                  ADD CONSTRAINT chk_BookingCancellations_fiscalsnapshot_consistent
                  CHECK (
                    "Status" IN (0, 6)
                    OR "OriginatingInvoiceId" IS NULL
                    OR (
                      "FiscalSnapshot_Source" <> 0
                      AND "FiscalSnapshot_ExchangeRateAtOriginalInvoice" > 0
                      AND "FiscalSnapshot_CurrencyAtEvent" IS NOT NULL
                    )
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Forward-only en PROD (mismo criterio que el resto de las migraciones de esta tanda): NO se
            // revierte a mano. Si algún día hiciera falta, hay que auditar primero cuántos BC sin ancla en
            // Status >= AwaitingOperatorRefund existen con snapshot incompleto — angostar el CHECK sin ese
            // chequeo dejaría filas reales violando la restricción y el rollback fallaría a mitad de camino.
        }
    }
}
