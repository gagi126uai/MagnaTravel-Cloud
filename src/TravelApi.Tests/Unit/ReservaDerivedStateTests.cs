using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-048 (2026-07-17): tabla de verdad de <see cref="ReservaDerivedState.HadServicesAndAllCancelled"/>.
/// El caso mas importante a blindar es la distincion "tuvo servicios y todos anulados" (SI es Anulada) vs
/// "nunca tuvo servicios" (NO es Anulada) — es la regla que evita que una reserva nueva y vacia nazca
/// auto-anulada.
/// </summary>
public class ReservaDerivedStateTests
{
    [Fact]
    public void ReservaVacia_SinNingunServicio_NoEstaAnulada()
    {
        var reserva = new Reserva();

        Assert.False(ReservaDerivedState.HadServicesAndAllCancelled(reserva));
    }

    [Fact]
    public void ReservaConUnServicioVivo_NoEstaAnulada()
    {
        var reserva = new Reserva
        {
            HotelBookings = { new HotelBooking { Status = WorkflowStatuses.Confirmado } }
        };

        Assert.False(ReservaDerivedState.HadServicesAndAllCancelled(reserva));
    }

    [Fact]
    public void ReservaConTodosSusServiciosAnulados_EstaAnulada()
    {
        var reserva = new Reserva
        {
            HotelBookings = { new HotelBooking { Status = WorkflowStatuses.Cancelado } },
            TransferBookings = { new TransferBooking { Status = WorkflowStatuses.Cancelado } }
        };

        Assert.True(ReservaDerivedState.HadServicesAndAllCancelled(reserva));
    }

    [Fact]
    public void ReservaConUnServicioAnuladoYOtroVivo_NoEstaAnulada()
    {
        // Un solo servicio vivo entre varios alcanza para que la reserva NO se considere sin efecto.
        var reserva = new Reserva
        {
            HotelBookings = { new HotelBooking { Status = WorkflowStatuses.Cancelado } },
            PackageBookings = { new PackageBooking { Status = WorkflowStatuses.Confirmado } }
        };

        Assert.False(ReservaDerivedState.HadServicesAndAllCancelled(reserva));
    }

    [Fact]
    public void VueloEmitidoYLuegoCancelado_CuentaComoAnulado()
    {
        // El aereo resuelve por TicketIssuedAt, pero cancela por codigo IATA (UN/UC/HX/NO) — mismo
        // criterio que ServiceResolutionRules.IsCancelled, no por el texto generico "Cancelado".
        var reserva = new Reserva
        {
            FlightSegments = { new FlightSegment { Status = "UN", TicketIssuedAt = DateTime.UtcNow } }
        };

        Assert.True(ReservaDerivedState.HadServicesAndAllCancelled(reserva));
    }

    [Fact]
    public void ReservaConServicioGenericoAnuladoYAereoVivo_NoEstaAnulada()
    {
        var reserva = new Reserva
        {
            Servicios = { new ServicioReserva { Status = WorkflowStatuses.Cancelado } },
            FlightSegments = { new FlightSegment { Status = "HK", TicketIssuedAt = DateTime.UtcNow } }
        };

        Assert.False(ReservaDerivedState.HadServicesAndAllCancelled(reserva));
    }
}
