using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Obra "Empezar de cero" (2026-07-27, fix bloqueante de revisión b): backfill de
    /// <c>Invoice.WasIssuedInProduction</c> para los comprobantes HISTÓRICOS (emitidos antes de que la columna
    /// existiera, que quedaron en <c>NULL</c>).
    ///
    /// <para><b>Regla FIRMADA por el dueño</b> (memoria del proyecto, banner activo en cada PDF vía
    /// <c>InvoicePdfService</c>): "las pruebas en PROD son SIEMPRE en el ambiente de HOMOLOGACIÓN de ARCA,
    /// nunca en modo productivo" (ver <c>regla-pruebas-prod-solo-homologacion-facturas</c>). Bajo esa regla,
    /// TODO comprobante histórico con CAE es, por construcción, de homologación — ninguno es un comprobante
    /// fiscal real. Por eso este backfill marca <c>WasIssuedInProduction = FALSE</c> (no <c>TRUE</c>) para esos
    /// históricos: es la ÚNICA lectura consistente con la regla firmada, y habilita que "Empezar de cero" no
    /// quede bloqueado para siempre por facturas de prueba antiguas que nunca tuvieron validez fiscal.</para>
    ///
    /// <para><b>El cinturón-y-tiradores del candado sigue intacto</b> (ver
    /// <c>SystemDataWipeService.EvaluateFiscalLockAsync</c>): si algún día aparece una factura con CAE y
    /// <c>WasIssuedInProduction IS NULL</c> (por ejemplo, un backfill futuro que no corrió, o una fila cargada
    /// por afuera del flujo normal), el candado la sigue cubriendo consultando
    /// <c>AfipSettings.IsProduction</c> actual + existencia de algún CAE. Este backfill NO reemplaza esa capa,
    /// solo cierra el hueco de los históricos YA CONOCIDOS a la fecha de este deploy.</para>
    ///
    /// <para><b>Idempotente</b>: el <c>WHERE</c> solo toca filas con la columna en <c>NULL</c>; correrla de
    /// nuevo sobre una base ya backfillada no cambia nada.</para>
    /// </summary>
    public partial class Adr051_DataWipe_BackfillWasIssuedInProductionForHistoricInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "Invoices"
                SET "WasIssuedInProduction" = FALSE
                WHERE "WasIssuedInProduction" IS NULL
                  AND "CAE" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin vuelta atras: no hay forma de distinguir, entre las filas que hoy tienen FALSE, cuales las
            // toco este backfill vs cuales las seteo la app despues (AfipService, camino normal de emision).
            // El estado anterior (NULL) era un dato incompleto, no uno valido — mismo criterio que otros
            // backfills de este repo (ver BackfillMonedaVaciaServiciosLegacy).
        }
    }
}
