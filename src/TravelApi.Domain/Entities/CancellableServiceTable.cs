namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-025 (DT.1.1, 2026-06-13): identifica EN QUE TABLA vive el servicio que una
/// <see cref="BookingCancellationLine"/> cancela. Es la primera mitad de la
/// referencia "(tabla, id)" al servicio cancelado (ADR-015 §6.3 opcion a): NO se
/// usa polimorfismo de EF porque los servicios viven en 6 tablas distintas (la
/// generica historica + las 5 tipadas) y un discriminador EF acoplaria el modelo.
///
/// <para><b>Por que un enum y no el string del tipo</b>: el "ServiceType" textual
/// (Hotel/Vuelo/...) ya existe en la tabla generica pero es texto libre con
/// variantes; este enum es el discriminador ESTABLE que dice exactamente en que
/// DbSet buscar el servicio. El centinela <see cref="Generic"/> con ServiceId=0 lo
/// usa el backfill (ADR-025 DT.1.3): una linea historica que no apunta a un
/// servicio puntual.</para>
/// </summary>
public enum CancellableServiceTable
{
    /// <summary>Tabla generica historica <c>ServicioReserva</c> (DbSet <c>Servicios</c>). Tambien centinela de backfill.</summary>
    Generic = 0,

    /// <summary>Tabla tipada de aereos (DbSet <c>FlightSegments</c>).</summary>
    Flight = 1,

    /// <summary>Tabla tipada de hoteles (DbSet <c>HotelBookings</c>).</summary>
    Hotel = 2,

    /// <summary>Tabla tipada de traslados (DbSet <c>TransferBookings</c>).</summary>
    Transfer = 3,

    /// <summary>Tabla tipada de paquetes (DbSet <c>PackageBookings</c>).</summary>
    Package = 4,

    /// <summary>Tabla tipada de asistencias (DbSet <c>AssistanceBookings</c>).</summary>
    Assistance = 5,
}
