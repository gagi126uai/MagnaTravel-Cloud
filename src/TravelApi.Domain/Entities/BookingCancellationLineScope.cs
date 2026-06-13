namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-025 (DT.1.1, 2026-06-13): distingue si una linea de cancelacion forma parte
/// de cancelar TODO el file o de cancelar UN servicio dejando el resto vivo.
///
/// <para>La distincion importa para el estado resultante de la reserva (decision
/// sellada #1 de Gaston): cancelar un servicio <see cref="Partial"/> NO mueve la
/// reserva a Cancelada (solo se tacha el servicio + contador "N de M cancelados");
/// las lineas <see cref="Full"/> son las N lineas de una cancelacion total
/// multi-operador, donde la reserva si pasa a Cancelada.</para>
/// </summary>
public enum BookingCancellationLineScope
{
    /// <summary>Cancelacion total del file: esta linea es una de N que cancelan toda la reserva.</summary>
    Full = 0,

    /// <summary>Cancelacion parcial: este servicio se cancela pero el resto del file sigue vivo.</summary>
    Partial = 1,
}
