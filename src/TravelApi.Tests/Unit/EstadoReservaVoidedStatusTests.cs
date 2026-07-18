using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-048 B3/T4 (2026-07-17, modelo de estados derivados): cobertura PURA de
/// <see cref="EstadoReserva.IsVoidedStatus"/>, la fuente ÚNICA de "reserva sin efecto"
/// (el par Cancelled + PendingOperatorRefund). Antes este par estaba hardcodeado suelto
/// en varios lugares del backend Y del frontend; este test fija el contrato que todos
/// esos lugares ahora comparten.
/// </summary>
public class EstadoReservaVoidedStatusTests
{
    [Fact]
    public void Cancelled_EsSinEfecto()
    {
        Assert.True(EstadoReserva.IsVoidedStatus(EstadoReserva.Cancelled));
    }

    [Fact]
    public void PendingOperatorRefund_EsSinEfecto()
    {
        // El paso intermedio de toda anulacion (el operador todavia no devolvio la plata)
        // TAMBIEN es "sin efecto" — la mentira que corrige B3 es justo tratarlo distinto.
        Assert.True(EstadoReserva.IsVoidedStatus(EstadoReserva.PendingOperatorRefund));
    }

    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.InManagement)]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Lost)]
    public void EstadosVivosOTerminalesNoAnulados_NoSonSinEfecto(string status)
    {
        Assert.False(EstadoReserva.IsVoidedStatus(status));
    }

    [Fact]
    public void EstadoNuloOVacio_NoEsSinEfecto()
    {
        Assert.False(EstadoReserva.IsVoidedStatus(null));
        Assert.False(EstadoReserva.IsVoidedStatus(string.Empty));
    }

    [Fact]
    public void VoidedStatuses_ContieneExactamenteElParEsperado()
    {
        // El conjunto vive en UN solo lugar: si algun dia se agrega un tercer estado
        // "sin efecto", este test avisa que el contrato cambio (no se rompe en silencio).
        Assert.Equal(2, EstadoReserva.VoidedStatuses.Length);
        Assert.Contains(EstadoReserva.Cancelled, EstadoReserva.VoidedStatuses);
        Assert.Contains(EstadoReserva.PendingOperatorRefund, EstadoReserva.VoidedStatuses);
    }
}
