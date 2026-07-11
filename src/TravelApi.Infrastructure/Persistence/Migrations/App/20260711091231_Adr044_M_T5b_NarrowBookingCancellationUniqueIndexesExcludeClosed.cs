using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T5 Addendum, Decision C (2026-07-11): ensancha el filtro de los DOS indices UNICOS parciales
    /// de <c>BookingCancellations</c> (por <c>ReservaId</c> y por <c>OriginatingInvoiceId</c>) para excluir
    /// TAMBIEN <c>Status = 4 (Closed)</c>, ademas del <c>Status = 6 (Aborted)</c> que ya excluian. Un BC
    /// <c>Closed</c> es un evento fiscal TERMINADO (NC con CAE, reembolso consumido): sin este cambio, una
    /// reserva con una cancelacion PARCIAL previa ya cerrada queda IMPOSIBLE de volver a anular (total o
    /// parcialmente) para siempre.
    ///
    /// <para><b>IMPORTANTE — esta migracion toca un indice YA APLICADO en produccion.</b> Por eso es una
    /// migracion NUEVA (DropIndex + CreateIndex), nunca se edita la migracion original que creo el indice
    /// (<c>B1_AddBookingCancellationPartialUniqueIndexes</c>). Direccion de cambio SEGURA: se AMPLIA la
    /// exclusion del filtro (mas estados afuera del universo del UNIQUE), lo que solo puede RELAJAR la
    /// restriccion — ninguna fila ya existente puede volverse "duplicada" retroactivamente por ampliar una
    /// exclusion. Sin backfill, sin riesgo de dato.</para>
    ///
    /// <para><b>ATENCION — esta migracion es forward-only: el <c>Down()</c> es un NO-OP deliberado.</b> NO se
    /// puede revertir al filtro viejo (<c>"Status" &lt;&gt; 6</c>, que INCLUYE Closed=4 en el universo del
    /// UNIQUE) una vez que la feature T5 opero: la feature esta DISEÑADA (Decision C) para que convivan en la
    /// misma reserva/factura un BC Closed (evento fiscal terminado) y un BC vivo nuevo. Si al hacer rollback
    /// existiera algun par Closed+vivo, el <c>CREATE UNIQUE INDEX</c> con el filtro viejo encontraria dos filas
    /// con la misma <c>ReservaId</c> (y/o <c>OriginatingInvoiceId</c>) dentro de <c>&lt;&gt; 6</c> y fallaria
    /// con duplicate key, abortando el rollback en el peor momento. Por eso el <c>Down()</c> NO intenta
    /// recrear el filtro viejo (evita el rollback que se rompe a mitad de camino). Para volver atras de verdad
    /// habria que primero resolver a mano los datos (garantizar que no hay pares Closed+vivo) y recrear el
    /// indice manualmente. Deploy: aplicar esta migracion ANTES de levantar la app nueva (el codigo nuevo abre
    /// un BC nuevo asumiendo que el UNIQUE excluye Closed).</para>
    ///
    /// <para><b>Consultas de validacion a correr contra produccion ANTES de aplicar este push</b> (via psql o
    /// el cliente de Postgres que uses; nombres de columna verificados contra
    /// <c>AppDbContextModelSnapshot.cs</c> — tabla <c>"BookingCancellations"</c>, columnas <c>"Id"</c>,
    /// <c>"ReservaId"</c>, <c>"OriginatingInvoiceId"</c>, <c>"Status"</c>):
    ///
    /// <code>
    /// -- 1) Confirmar que los 2 indices existen HOY con el filtro VIEJO (solo excluye Aborted=6). Si el
    ///    filtro ya fuera distinto (por ejemplo si esta migracion ya se aplico), NO volver a correrla.
    /// SELECT indexname, indexdef
    ///   FROM pg_indexes
    ///  WHERE tablename = 'BookingCancellations'
    ///    AND indexname IN ('IX_BookingCancellations_ReservaId', 'IX_BookingCancellations_OriginatingInvoiceId');
    ///
    /// -- 2) Sanity check (informativo, no bloqueante): cuantos BC estan HOY en Closed (Status=4). Ninguno
    ///    de estos puede volverse "problema" con el cambio (el filtro los saca del universo del UNIQUE, no
    ///    los mete), pero sirve para dimensionar cuantas reservas quedan destrabadas por este fix.
    /// SELECT COUNT(*) AS bc_closed_count
    ///   FROM "BookingCancellations"
    ///  WHERE "Status" = 4;
    ///
    /// -- 3) Verificacion CRITICA (debe dar 0 filas, siempre): si diera alguna fila, el nuevo filtro
    ///    encontraria un duplicado real y el CREATE UNIQUE INDEX de este migration fallaria — mejor
    ///    detectarlo ANTES del deploy que en medio de un push a produccion.
    ///    3a) Dos o mas BC NO-Aborted-NI-Closed para la MISMA reserva (violaria el indice por ReservaId):
    /// SELECT "ReservaId", COUNT(*)
    ///   FROM "BookingCancellations"
    ///  WHERE "Status" NOT IN (4, 6)
    ///  GROUP BY "ReservaId"
    /// HAVING COUNT(*) > 1;
    ///
    ///    3b) Dos o mas BC NO-Aborted-NI-Closed para la MISMA factura originante (violaria el indice por
    ///        OriginatingInvoiceId):
    /// SELECT "OriginatingInvoiceId", COUNT(*)
    ///   FROM "BookingCancellations"
    ///  WHERE "Status" NOT IN (4, 6)
    ///  GROUP BY "OriginatingInvoiceId"
    /// HAVING COUNT(*) > 1;
    /// </code>
    /// </summary>
    public partial class Adr044_M_T5b_NarrowBookingCancellationUniqueIndexesExcludeClosed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations");

            migrationBuilder.DropIndex(
                name: "IX_BookingCancellations_ReservaId",
                table: "BookingCancellations");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_OriginatingInvoiceId",
                table: "BookingCancellations",
                column: "OriginatingInvoiceId",
                unique: true,
                filter: "\"Status\" NOT IN (4, 6)");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellations_ReservaId",
                table: "BookingCancellations",
                column: "ReservaId",
                unique: true,
                filter: "\"Status\" NOT IN (4, 6)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // NO-OP deliberado (forward-only). Ver la nota en el resumen de la clase: restaurar el filtro viejo
            // ("Status" <> 6, que vuelve a meter Closed=4 en el universo del UNIQUE) puede fallar con duplicate
            // key si la feature T5 ya produjo algun par Closed+vivo en la misma reserva/factura — y eso abortaria
            // el rollback justo cuando mas se lo necesita. Dejamos el indice ancho (excluye Closed) tambien al
            // revertir la migracion: es el estado SEGURO. Para volver atras de verdad hay que resolver los datos
            // a mano y recrear el indice manualmente.
        }
    }
}
