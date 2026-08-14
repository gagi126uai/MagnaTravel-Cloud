using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-053 D1.1 (2026-08-13): <see cref="ReservaScheduleCalculator"/> excluye servicios cancelados del
/// MIN/MAX de la reserva con un predicado escrito INLINE (traducible a SQL por EF Core — un helper C#
/// propio invocado dentro de un <c>Where()</c> no se traduce). Este test verifica que ese predicado inline
/// (<c>IsCancelledGenericStatusSql</c>/<c>IsCancelledFlightStatusSql</c>, hechos <c>internal</c> a
/// proposito para este test) da el MISMO resultado, string por string, que la definicion CANONICA de
/// "cancelado" del sistema (<see cref="WorkflowStatusHelper"/>) — si alguien los hace divergir en un
/// refactor futuro (ej. cambia el predicado inline sin actualizar este test), esta prueba lo agarra.
///
/// <para>Cubre el muestreo pedido por el review: <c>"Cancelado"</c>, <c>"Cancelada"</c> (femenino),
/// <c>"CANCELADO"</c> (mayusculas), <c>" Cancelado"</c> (espacio) — y, del lado de los NO cancelados,
/// los mismos falsos-positivos que ya prueba <see cref="WorkflowStatusHelperTests"/> para la rama
/// confirm/emit ("A confirmar", "sin emitir", etc.), que NO deben excluirse.</para>
/// </summary>
public class WorkflowStatusHelperEquivalenceTests
{
    // --- Genérico (Hotel/Traslado/Paquete/Asistencia/servicio genérico) ---

    [Theory]
    [InlineData("Cancelado")]
    [InlineData("Cancelada")]
    [InlineData("CANCELADO")]
    [InlineData(" Cancelado")]
    [InlineData("cancelado")]
    [InlineData("Cancelado ")]
    public void PredicadoGenerico_CoincideConHelper_CasosCancelados(string status)
    {
        var esperado = WorkflowStatusHelper.MapGenericStatus(status) == WorkflowStatuses.Cancelado;
        var real = ReservaScheduleCalculator.IsCancelledGenericStatusSql(status);

        Assert.True(esperado, "Precondición del test: el caso tiene que ser cancelado según el helper canónico.");
        Assert.Equal(esperado, real);
    }

    [Theory]
    [InlineData("Confirmado")]
    [InlineData("Solicitado")]
    [InlineData("Emitido")]
    [InlineData("Finalizado")]
    [InlineData("A confirmar")]
    [InlineData("sin emitir")]
    [InlineData("desconfirmado")]
    [InlineData("no confirmado")]
    [InlineData("")]
    [InlineData(null)]
    public void PredicadoGenerico_CoincideConHelper_CasosNoCancelados(string? status)
    {
        var esperado = WorkflowStatusHelper.MapGenericStatus(status ?? "") == WorkflowStatuses.Cancelado;
        var real = ReservaScheduleCalculator.IsCancelledGenericStatusSql(status);

        Assert.False(esperado, "Precondición del test: el caso NO tiene que ser cancelado según el helper canónico.");
        Assert.Equal(esperado, real);
    }

    // --- Vuelo (código IATA) ---

    [Theory]
    [InlineData("UN")]
    [InlineData("UC")]
    [InlineData("HX")]
    [InlineData("NO")]
    [InlineData("un")]
    [InlineData(" UN")]
    public void PredicadoVuelo_CoincideConHelper_CasosCancelados(string status)
    {
        var esperado = WorkflowStatusHelper.MapFlightStatus(status) == WorkflowStatuses.Cancelado;
        var real = ReservaScheduleCalculator.IsCancelledFlightStatusSql(status);

        Assert.True(esperado);
        Assert.Equal(esperado, real);
    }

    [Theory]
    [InlineData("HK")]
    [InlineData("TK")]
    [InlineData("KK")]
    [InlineData("KL")]
    [InlineData("Finalizado")]
    [InlineData("RR")]
    [InlineData("")]
    [InlineData(null)]
    public void PredicadoVuelo_CoincideConHelper_CasosNoCancelados(string? status)
    {
        var esperado = WorkflowStatusHelper.MapFlightStatus(status ?? "") == WorkflowStatuses.Cancelado;
        var real = ReservaScheduleCalculator.IsCancelledFlightStatusSql(status);

        Assert.False(esperado);
        Assert.Equal(esperado, real);
    }
}
