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

    // =========================================================================
    // Tabla de verdad completa del criterio IsOperatorRefundPending, con CADA valor real del enum
    // BookingCancellationLineRefundStatus (None/PendingOperatorRefund/Settled) cruzado con RefundCap
    // cero/positivo. Agregada tras el hallazgo del CI (2026-07-17, 3ra pasada): el criterio en SÍ es
    // correcto (hace justo lo que dice: "¿hay una línea con cap>0 sin Settled?"); el bug real estaba en
    // OnAllCreditConsumedAsync, que usaba este criterio para una pregunta que NO es la suya (ver el
    // comentario largo en BookingCancellationService.OnAllCreditConsumedAsync).
    // =========================================================================

    [Theory]
    [InlineData(0, BookingCancellationLineRefundStatus.None, false)]
    [InlineData(0, BookingCancellationLineRefundStatus.PendingOperatorRefund, false)] // cap 0 nunca genera deuda, aunque el status diga "pendiente" (dato inconsistente defensivo)
    [InlineData(0, BookingCancellationLineRefundStatus.Settled, false)]
    [InlineData(1000, BookingCancellationLineRefundStatus.None, true)] // cap>0 sin marcar aun (recien nacida la linea) -> cuenta como pendiente
    [InlineData(1000, BookingCancellationLineRefundStatus.PendingOperatorRefund, true)]
    [InlineData(1000, BookingCancellationLineRefundStatus.Settled, false)]
    public void IsOperatorRefundPending_TablaDeVerdadPorCadaRefundStatusReal(
        decimal refundCap, BookingCancellationLineRefundStatus status, bool expectedPending)
    {
        var lines = new[] { Line(refundCap, status) };
        Assert.Equal(expectedPending, ReservaTerminalDerivation.IsOperatorRefundPending(lines));
    }

    [Fact]
    public void ReembolsoParcialMenorAlCap_SigueContandoComoPendiente_PorDiseño()
    {
        // Reproduce el escenario REAL que rompio 3 tests preexistentes en CI (2026-07-17, 3ra pasada):
        // el operador puede devolver MENOS que el RefundCap completo (el cap es un TOPE, no un monto
        // exigido) y aun asi el circuito del CLIENTE puede quedar totalmente saldado con eso. El criterio
        // PURO sigue diciendo "pendiente" en este caso (500 de 5000 no cubre el cap) — eso es CORRECTO
        // para su proposito (¿el operador llego al tope?). La leccion NO es "arreglar este criterio": es
        // que OnAllCreditConsumedAsync (el cierre por el lado del CLIENTE, ADR-033) NUNCA debe consultarlo,
        // porque mide una pregunta distinta (ver BookingCancellationService.OnAllCreditConsumedAsync).
        var lineaConReembolsoParcial = new BookingCancellationLine
        {
            RefundCap = 5000m,
            ReceivedRefundAmount = 500m, // el operador ya devolvio esto; el cliente lo retiro entero
            RefundStatus = BookingCancellationLineRefundStatus.PendingOperatorRefund, // 500 < 5000, nunca llega a Settled
        };

        Assert.True(ReservaTerminalDerivation.IsOperatorRefundPending(new[] { lineaConReembolsoParcial }));
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
