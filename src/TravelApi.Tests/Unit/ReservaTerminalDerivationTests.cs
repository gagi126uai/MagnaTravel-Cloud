using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-048 (2026-07-17, bloqueante B1 del review): tabla de verdad de
/// <see cref="ReservaTerminalDerivation"/>. El caso critico es el de N cancelaciones (BC) con
/// reembolsos del operador en distinto estado — la regla del terminal se decide a NIVEL RESERVA
/// (todas las lineas de todas las BC), nunca mirando una sola BC aislada.
/// </summary>
public class ReservaTerminalDerivationTests
{
    private static BookingCancellationLine Line(decimal refundCap, BookingCancellationLineRefundStatus status)
        => new() { RefundCap = refundCap, RefundStatus = status };

    [Fact]
    public void SinNingunaLinea_NoHayReembolsoPendiente_TerminalEsAnulada()
    {
        var lines = Array.Empty<BookingCancellationLine>();

        Assert.False(ReservaTerminalDerivation.IsOperatorRefundPending(lines));
        Assert.Equal(EstadoReserva.Cancelled, ReservaTerminalDerivation.DetermineTerminalStatus(lines));
    }

    [Fact]
    public void UnaLineaConCapPendiente_TerminalEsEsperandoReembolso()
    {
        var lines = new[] { Line(1000m, BookingCancellationLineRefundStatus.PendingOperatorRefund) };

        Assert.True(ReservaTerminalDerivation.IsOperatorRefundPending(lines));
        Assert.Equal(EstadoReserva.PendingOperatorRefund, ReservaTerminalDerivation.DetermineTerminalStatus(lines));
    }

    [Fact]
    public void TodasLasLineasSettled_TerminalEsAnulada()
    {
        var lines = new[]
        {
            Line(1000m, BookingCancellationLineRefundStatus.Settled),
            Line(500m, BookingCancellationLineRefundStatus.Settled),
        };

        Assert.False(ReservaTerminalDerivation.IsOperatorRefundPending(lines));
        Assert.Equal(EstadoReserva.Cancelled, ReservaTerminalDerivation.DetermineTerminalStatus(lines));
    }

    [Fact]
    public void LineaSinCap_NoCuentaComoPendiente()
    {
        // RefundCap = 0 significa "no se le pago nada reembolsable a este operador" — no genera deuda.
        var lines = new[] { Line(0m, BookingCancellationLineRefundStatus.None) };

        Assert.False(ReservaTerminalDerivation.IsOperatorRefundPending(lines));
    }

    [Fact]
    public void DosBC_UnaSaldadaYOtraPendiente_ReservaSigueEsperando()
    {
        // B1: simula 2 cancelaciones (BC) de la MISMA reserva, cada una con su propia linea. La primera
        // ya cobro todo del operador (Settled); la segunda todavia no. A NIVEL RESERVA el resultado debe
        // ser "esperando reembolso" — NO "anulada", aunque la primera BC individualmente ya este saldada.
        var lineaDeLaPrimeraBC = new BookingCancellationLine
        {
            BookingCancellationId = 1,
            RefundCap = 1000m,
            RefundStatus = BookingCancellationLineRefundStatus.Settled,
        };
        var lineaDeLaSegundaBC = new BookingCancellationLine
        {
            BookingCancellationId = 2,
            RefundCap = 500m,
            RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund,
        };

        var todasLasLineasDeLaReserva = new[] { lineaDeLaPrimeraBC, lineaDeLaSegundaBC };

        Assert.True(ReservaTerminalDerivation.IsOperatorRefundPending(todasLasLineasDeLaReserva));
        Assert.Equal(
            EstadoReserva.PendingOperatorRefund,
            ReservaTerminalDerivation.DetermineTerminalStatus(todasLasLineasDeLaReserva));

        // Si (por error) solo se mirara la PRIMERA BC en aislado, el resultado seria "anulada" —
        // exactamente el cierre prematuro que este diseño viene a evitar.
        var soloPrimeraBC = new[] { lineaDeLaPrimeraBC };
        Assert.Equal(EstadoReserva.Cancelled, ReservaTerminalDerivation.DetermineTerminalStatus(soloPrimeraBC));
    }

    [Fact]
    public void DosBC_AmbasSaldadas_ReservaQuedaAnulada()
    {
        var lineaDeLaPrimeraBC = new BookingCancellationLine
        {
            BookingCancellationId = 1,
            RefundCap = 1000m,
            RefundStatus = BookingCancellationLineRefundStatus.Settled,
        };
        var lineaDeLaSegundaBC = new BookingCancellationLine
        {
            BookingCancellationId = 2,
            RefundCap = 500m,
            RefundStatus = BookingCancellationLineRefundStatus.Settled,
        };

        var todasLasLineasDeLaReserva = new[] { lineaDeLaPrimeraBC, lineaDeLaSegundaBC };

        Assert.False(ReservaTerminalDerivation.IsOperatorRefundPending(todasLasLineasDeLaReserva));
        Assert.Equal(
            EstadoReserva.Cancelled,
            ReservaTerminalDerivation.DetermineTerminalStatus(todasLasLineasDeLaReserva));
    }

    [Theory]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, false)]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(null, false)]
    public void IsLiveEngineStatus_SoloInManagementYConfirmed(string? status, bool expected)
    {
        // M5 (ADR-036 "en viaje inmutable"): el motor EN VIVO no toca Traveling ni ningun otro estado
        // fuera de {InManagement, Confirmed}. La reparacion unica (migracion) es la unica que barre
        // Traveling con datos legacy.
        Assert.Equal(expected, ReservaTerminalDerivation.IsLiveEngineStatus(status));
    }

    [Theory]
    [InlineData(EstadoReserva.Cancelled, true)]
    [InlineData(EstadoReserva.PendingOperatorRefund, true)]
    [InlineData(EstadoReserva.InManagement, false)]
    [InlineData(EstadoReserva.Confirmed, false)]
    [InlineData(EstadoReserva.Traveling, false)]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(null, false)]
    public void IsInTerminalPar_SoloCancelledYPendingOperatorRefund(string? status, bool expected)
    {
        Assert.Equal(expected, ReservaTerminalDerivation.IsInTerminalPar(status));
    }

    [Theory]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Cancelled, true)]
    [InlineData(EstadoReserva.PendingOperatorRefund, true)]
    [InlineData(EstadoReserva.Traveling, false)]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(null, false)]
    public void CanReDeriveTerminalStatus_VivosMasElPropioPar_NuncaTerminalesAjenos(string? status, bool expected)
    {
        // B-1 (backend review 2026-07-17, 2da pasada): la puerta de entrada para (re)derivar el terminal
        // se amplia a {InManagement, Confirmed, Cancelled, PendingOperatorRefund} — los dos primeros para
        // entrar al par por primera vez, los dos ultimos para CORREGIR entre ellos cuando aparece (o se
        // salda) una deuda del operador que no se vio a tiempo. Traveling/Closed/Lost/Budget/Quotation
        // JAMAS entran aca (M5 + regla "nunca sale del par hacia un estado vivo ni toca otros terminales").
        Assert.Equal(expected, ReservaTerminalDerivation.CanReDeriveTerminalStatus(status));
    }
}
