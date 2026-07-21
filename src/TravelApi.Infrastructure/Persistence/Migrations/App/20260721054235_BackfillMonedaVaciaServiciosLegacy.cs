using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// Saneo de datos legacy (2026-07-21): servicios creados por el camino viejo
    /// (pre-moneda / llave EnableCatalogFindOrCreate apagada) quedaron con
    /// Currency NULL o vacia. En PROD son 27 filas (22 hoteles + 5 genericos,
    /// conteo validado contra la base real por ops-diagnostico antes de escribir
    /// esto). Todos esos servicios nacieron "en pesos" — el default historico —
    /// asi que se les estampa ARS explicito. Los agregadores ya normalizan
    /// NULL/'' a ARS al calcular (Monedas.Normalizar), o sea esto NO cambia
    /// ningun saldo: solo deja el dato guardado igual que el dato calculado,
    /// para reportes/exports/queries que no pasen por el normalizador.
    /// Idempotente: correrla de nuevo no toca nada.
    /// </summary>
    public partial class BackfillMonedaVaciaServiciosLegacy : Migration
    {
        private static readonly string[] TablasDeServicios =
        {
            "HotelBookings",
            "FlightSegments",
            "TransferBookings",
            "PackageBookings",
            "AssistanceBookings",
            "Reservations", // los servicios genericos (ServicioReserva) viven en esta tabla
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var tabla in TablasDeServicios)
            {
                migrationBuilder.Sql(
                    $"""UPDATE "{tabla}" SET "Currency" = 'ARS' WHERE "Currency" IS NULL OR "Currency" = '';""");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Sin vuelta atras: no hay forma de distinguir que filas tenian NULL
            // y cuales '' — y el estado anterior era un dato roto, no uno valido.
        }
    }
}
