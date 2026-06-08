using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// ADR-020 F2 (migracion de datos): backfillea <c>ConfirmedSale</c> (suma del SalePrice de los
    /// servicios RESUELTOS por reserva) y recomputa <c>Balance = ConfirmedSale - TotalPaid</c>. La
    /// columna ConfirmedSale ya la creo Adr020_M1; esta migracion es 100% datos.
    ///
    /// <para>Predicados ESPEJO de ReservaMoneyCalculator / ServiceResolutionRules:
    /// genericos = confirm/emit y no cancel; traslado = (confirm/emit OR NoConfirmationRequired)
    /// y no cancel; aereo = TicketIssuedAt no nulo y status no cancelado (UN/UC/HX/NO). El PNR
    /// confirmado NO resuelve, y un vuelo emitido y despues cancelado tampoco suma (deuda fantasma).
    /// El servicio generico vive en la tabla "Reservations" (ToTable historico de ServicioReserva).</para>
    ///
    /// <para><b>Red de seguridad post-deploy</b>: el endpoint admin de mantenimiento que corre
    /// RunDailyDetailedAsync recalcula el saldo app-level (fuente unica ReservaMoneyCalculator) y
    /// reconcilia cualquier divergencia SQL-vs-dominio.</para>
    ///
    /// <para><b>Down (B2)</b>: restaura la formula vieja <c>Balance = TotalSale - TotalPaid</c>. El
    /// drop de la columna ConfirmedSale vive en Adr020_M1.Down (orden de rollback: M2.Down -> M1.Down).</para>
    /// </summary>
    public partial class Adr020_M2_ConfirmedSaleAndBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "TravelFiles" tf SET "ConfirmedSale" =
                    COALESCE((SELECT SUM(h."SalePrice") FROM "HotelBookings" h
                        WHERE h."ReservaId" = tf."Id"
                          AND lower(h."Status") NOT LIKE '%cancel%'
                          AND (lower(h."Status") LIKE '%confirm%' OR lower(h."Status") LIKE '%emit%')), 0)
                  + COALESCE((SELECT SUM(p."SalePrice") FROM "PackageBookings" p
                        WHERE p."ReservaId" = tf."Id"
                          AND lower(p."Status") NOT LIKE '%cancel%'
                          AND (lower(p."Status") LIKE '%confirm%' OR lower(p."Status") LIKE '%emit%')), 0)
                  + COALESCE((SELECT SUM(a."SalePrice") FROM "AssistanceBookings" a
                        WHERE a."ReservaId" = tf."Id"
                          AND lower(a."Status") NOT LIKE '%cancel%'
                          AND (lower(a."Status") LIKE '%confirm%' OR lower(a."Status") LIKE '%emit%')), 0)
                  + COALESCE((SELECT SUM(s."SalePrice") FROM "Reservations" s
                        WHERE s."ReservaId" = tf."Id"
                          AND lower(s."Status") NOT LIKE '%cancel%'
                          AND (lower(s."Status") LIKE '%confirm%' OR lower(s."Status") LIKE '%emit%')), 0)
                  + COALESCE((SELECT SUM(t."SalePrice") FROM "TransferBookings" t
                        WHERE t."ReservaId" = tf."Id"
                          AND lower(t."Status") NOT LIKE '%cancel%'
                          AND ((lower(t."Status") LIKE '%confirm%' OR lower(t."Status") LIKE '%emit%')
                               OR t."NoConfirmationRequired" = true)), 0)
                  + COALESCE((SELECT SUM(f."SalePrice") FROM "FlightSegments" f
                        WHERE f."ReservaId" = tf."Id"
                          AND f."TicketIssuedAt" IS NOT NULL
                          AND upper(trim(f."Status")) NOT IN ('UN','UC','HX','NO')), 0);

                UPDATE "TravelFiles" SET "Balance" = "ConfirmedSale" - "TotalPaid";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // B2: restaurar la formula vieja del saldo. (El drop de ConfirmedSale lo hace Adr020_M1.Down.)
            migrationBuilder.Sql("""
                UPDATE "TravelFiles" SET "Balance" = "TotalSale" - "TotalPaid";
                """);
        }
    }
}
