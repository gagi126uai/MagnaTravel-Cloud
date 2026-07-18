using Microsoft.EntityFrameworkCore.Migrations;
using TravelApi.Infrastructure.Reservations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-048 T5 (2026-07-17, hardening — materializacion de los ejes secundarios de la reserva).
    ///
    /// <para><b>Que agrega</b>: dos columnas nuevas y NULLABLES en <c>TravelFiles</c> (la tabla de
    /// <c>Reserva</c>) — <c>DerivedCollectionStatus</c> (eje de COBRO) y <c>DerivedInvoicingStatus</c> (eje
    /// de FACTURACION). De aca en mas el UNICO escritor de estas columnas es la derivacion pura que corre
    /// dentro de <c>ReservaMoneyPersister.PersistAsync</c> (ver <c>ReservaDerivedAxesProjector</c>), en la
    /// MISMA <c>SaveChangesAsync</c> que la plata — nunca un proceso aparte, nunca una pasada nocturna
    /// (regla 9).</para>
    ///
    /// <para><b>BACKFILL, una sola vez (marcador = <c>__EFMigrationsHistory</c>, EF la corre exactamente
    /// una vez)</b>: rellena las dos columnas para TODAS las reservas que ya existen, usando la MISMA
    /// logica que ya corre HOY en produccion para el listado (<c>FillPorMonedaForListAsync</c> /
    /// <c>FillInvoicingStatusForListAsync</c> en <c>ReservaService.cs</c>) — no se reinventa el criterio en
    /// SQL, se traduce el MISMO criterio ya validado:</para>
    /// <list type="bullet">
    /// <item><b>Eje de cobro</b>: para una reserva con filas en <c>ReservaMoneyByCurrency</c> (la tabla
    ///   hija materializada de plata por moneda), "ConDeuda" si alguna moneda tiene <c>Balance &gt;
    ///   0.005</c>; si no, "SaldoAFavor" si alguna tiene <c>Balance &lt; -0.005</c>; si no, "Saldado" si
    ///   hubo actividad (<c>ConfirmedSale &gt; 0</c> o <c>TotalPaid &gt; 0</c> en alguna moneda); si no,
    ///   "SinMovimientos". Para una reserva SIN filas hijas (nunca paso por el persister), el mismo
    ///   criterio pero con el escalar <c>Balance</c>/<c>TotalPaid</c> de la cabecera como unica "moneda".</item>
    /// <item><b>Eje de facturacion</b>: agrega Facturas + Notas de Debito con CAE aprobado
    ///   (<c>Resultado='A'</c>, misma regla que <c>ReservaInvoicingCuadreCalculator.CountsInNetBilled</c>)
    ///   menos las Notas de Credito (tipos 3/8/13/53) para el FACTURADO NETO, y SOLO Facturas+ND (sin
    ///   restar NC) para el BRUTO EMITIDO. "NotInvoiced" si ambos ~0; "FullyReturned" si el neto ~0 pero el
    ///   bruto &gt; 0; "FullyInvoiced" si el neto cubre <c>TotalSale</c>; si no, "PartiallyInvoiced".</item>
    /// </list>
    ///
    /// <para><b>OJO nombres REALES en Postgres</b> (ver leccion "db naming travelfiles"): la reserva es
    /// <c>"TravelFiles"</c> (PK <c>"Id"</c>); <c>"ReservaMoneyByCurrency"."ReservaId"</c> SI se llama
    /// <c>"ReservaId"</c> (sin remapeo); pero <c>"Invoices"</c> mapea su FK a la reserva con el nombre de
    /// COLUMNA <c>"TravelFileId"</c> (la propiedad C# se llama <c>ReservaId</c>, pero
    /// <c>AppDbContext</c> la remapea con <c>HasColumnName("TravelFileId")</c> — la trampa clasica de este
    /// repo si se copia el nombre de la propiedad en SQL crudo).</para>
    ///
    /// <para><b>Por que es SEGURA/reversible</b>: NO toca ningun dato existente (las dos columnas nacen
    /// <c>null</c> por el <c>AddColumn</c> de arriba; el backfill solo las RELLENA, nunca sobreescribe
    /// nada mas). El <c>Down</c> dropea las columnas — no hay perdida de informacion original que
    /// reconstruir (a diferencia de una migracion que TOCA <c>Status</c>, esta es aditiva pura).</para>
    /// </summary>
    public partial class Adr048_M2_AddDerivedStatusColumnsToReserva : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DerivedCollectionStatus",
                table: "TravelFiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DerivedInvoicingStatus",
                table: "TravelFiles",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_DerivedCollectionStatus",
                table: "TravelFiles",
                column: "DerivedCollectionStatus");

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_DerivedInvoicingStatus",
                table: "TravelFiles",
                column: "DerivedInvoicingStatus");

            // ────────────────────────────────────────────────────────────────────────────────────────
            // BACKFILL, 4 sentencias (cobro con filas / cobro fallback / facturacion con comprobantes /
            // facturacion fallback). El TEXTO de las 4 vive en Adr048T5BackfillSql (Reservations), NO
            // inline aca — asi el test de integracion Adr048T5BackfillSqlIntegrationTests corre el MISMO
            // SQL que esta migracion en vez de una copia que se puede desincronizar (MT1, review backend
            // 2026-07-17). Si necesitas tocar el criterio del backfill, tocá esa clase, no un literal
            // nuevo aca — ver su XML-doc para el detalle de cada rama.
            // ────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql(Adr048T5BackfillSql.CollectionAxisWithChildRows);
            migrationBuilder.Sql(Adr048T5BackfillSql.CollectionAxisFallback);
            migrationBuilder.Sql(Adr048T5BackfillSql.InvoicingAxisWithInvoices);
            migrationBuilder.Sql(Adr048T5BackfillSql.InvoicingAxisFallback);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_DerivedCollectionStatus",
                table: "TravelFiles");

            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_DerivedInvoicingStatus",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "DerivedCollectionStatus",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "DerivedInvoicingStatus",
                table: "TravelFiles");
        }
    }
}
