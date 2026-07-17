using TravelApi.Domain.Entities;

namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-048 (modelo de estados derivados, 2026-07-17): función PURA de dominio que responde
/// "¿esta reserva se quedó SIN EFECTO por sus servicios?" — la mentira #1 que se vio en la
/// reserva F-2026-1046 (el cartel decía "Confirmada" con los 2 de 2 servicios anulados). NO hace
/// <c>SaveChanges</c>, NO conoce EF ni infraestructura: solo mira las 6 colecciones de servicios
/// que el caller ya cargó en la reserva.
///
/// <para><b>Distinción crítica (INV-048-01)</b>: "tuvo servicios y TODOS quedaron anulados" es muy
/// distinto de "nunca tuvo servicios". Una reserva recién creada, todavía vacía, NO está anulada —
/// simplemente no pasó nada todavía. Sin esta distinción, cualquier reserva nueva se auto-anularía
/// al nacer, que es exactamente el bug que esta regla evita.</para>
/// </summary>
public static class ReservaDerivedState
{
    /// <summary>
    /// True cuando la reserva tuvo AL MENOS un servicio (en cualquiera de sus 6 colecciones — vuelos,
    /// hoteles, traslados, paquetes, asistencias o el genérico — vivo o anulado) Y absolutamente TODOS
    /// terminaron anulados. Es la condición que dispara el terminal del par (Anulada / Esperando
    /// reembolso del operador) en <see cref="ReservaTerminalDerivation"/>.
    /// </summary>
    public static bool HadServicesAndAllCancelled(Reserva reserva)
    {
        int totalServiceCount = 0;
        bool allCancelled = true;

        void Check<T>(IEnumerable<T> items, Func<T, bool> isCancelled)
        {
            foreach (var item in items)
            {
                totalServiceCount++;
                if (!isCancelled(item)) allCancelled = false;
            }
        }

        Check(reserva.FlightSegments, ServiceResolutionRules.IsCancelled);
        Check(reserva.HotelBookings, ServiceResolutionRules.IsCancelled);
        Check(reserva.TransferBookings, ServiceResolutionRules.IsCancelled);
        Check(reserva.PackageBookings, ServiceResolutionRules.IsCancelled);
        Check(reserva.AssistanceBookings, ServiceResolutionRules.IsCancelled);
        Check(reserva.Servicios, ServiceResolutionRules.IsCancelled);

        return totalServiceCount >= 1 && allCancelled;
    }
}
