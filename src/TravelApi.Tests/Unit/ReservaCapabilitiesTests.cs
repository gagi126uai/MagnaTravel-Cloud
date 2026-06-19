using System.Linq;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-035 (2026-06-19): cobertura PURA de la politica de capacidades (ReservaCapabilityPolicy). Verifica la
/// matriz EXACTA del ADR para los 10 estados x cada capacidad, que todo "No" trae motivo, y las transiciones
/// (incluido el camino nuevo Closed revierte tambien a ToSettle, Decision 4-bis).
///
/// <para>El test cruzado de coherencia politica-vs-guard (C2, gate bloqueante de merge) vive en
/// <see cref="ReservaCapabilitiesCrossCheckTests"/>.</para>
/// </summary>
public class ReservaCapabilitiesTests
{
    private static readonly string[] AllStatuses =
    {
        EstadoReserva.Quotation,
        EstadoReserva.Budget,
        EstadoReserva.InManagement,
        EstadoReserva.Confirmed,
        EstadoReserva.Traveling,
        EstadoReserva.ToSettle,
        EstadoReserva.Closed,
        EstadoReserva.Lost,
        EstadoReserva.Cancelled,
        EstadoReserva.PendingOperatorRefund,
    };

    /// <summary>Contexto con deuda (Balance>0) y sin ataduras fiscales, salvo que el test diga lo contrario.</summary>
    private static ReservaCapabilityContext Ctx(string status, decimal balance = 100m, bool hasLiveCae = false)
        => new(status, balance, HasLiveCae: hasLiveCae, HasLiveVoucher: false, HasLiveEditAuth: false, HasAnyPayment: false);

    // ===================== Factura de venta: SOLO {Confirmed, Traveling, ToSettle} =====================

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.ToSettle, true)]
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.InManagement, false)]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void CanInvoiceSale_MatchesAllowList(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanInvoiceSale.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanInvoiceSale.Reason));
    }

    // ===================== Cobrar: venta firme (incluye Closed) con deuda — = EnsureCollectable de ADR-033 ===

    [Theory]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.ToSettle, true)]
    // ADR-033 A1/E2: una Finalizada (Closed) CON deuda SI admite cobro. La compuerta no puede ser mas
    // estricta que EnsureCollectable (que usa SaleFirmStatuses, incluye Closed).
    [InlineData(EstadoReserva.Closed, true)]
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void CanRegisterPayment_WithDebt_MatchesCollectableStates(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status, balance: 100m));
        Assert.Equal(expected, caps.CanRegisterPayment.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanRegisterPayment.Reason));
    }

    [Fact]
    public void CanRegisterPayment_FirmStateButNoDebt_Rejected()
    {
        // Venta firme pero Balance 0: no hay nada para cobrar (motivo distinto al de estado no firme).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Confirmed, balance: 0m));
        Assert.False(caps.CanRegisterPayment.Allowed);
        Assert.Equal(Reserva.NoPendingBalanceForChargeMessage, caps.CanRegisterPayment.Reason);
    }

    // ===================== Editar/borrar cobro: NO en terminales =====================

    [Theory]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.ToSettle, true)]
    [InlineData(EstadoReserva.Quotation, true)]
    [InlineData(EstadoReserva.Budget, true)]
    public void CanEditOrDeletePayment_BlockedOnTerminalStates(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanEditOrDeletePayment.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanEditOrDeletePayment.Reason));
    }

    // ===================== Voucher: SOLO {Confirmed, Traveling, ToSettle, Closed} =====================

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.ToSettle, true)]
    [InlineData(EstadoReserva.Closed, true)]
    [InlineData(EstadoReserva.InManagement, false)] // InManagement NO (decision del dueño)
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void CanEmitVoucher_MatchesConfirmedOnward(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanEmitVoucher.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanEmitVoucher.Reason));
    }

    // ===================== Cancelar: NO en {Closed, Lost, Cancelled, PendingOperatorRefund} =====================

    [Theory]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    [InlineData(EstadoReserva.Quotation, true)]
    [InlineData(EstadoReserva.Budget, true)]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    // ADR-035: En viaje YA NO se cancela (se corrige por NC/ajuste).
    [InlineData(EstadoReserva.Traveling, false)]
    [InlineData(EstadoReserva.ToSettle, true)]
    public void CanCancel_MatchesMatrix(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanCancel.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanCancel.Reason));
    }

    [Fact]
    public void CanCancel_Closed_HasDedicatedReason()
    {
        // Decision 4: una Finalizada no se cancela. Motivo especifico distinto del generico.
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Closed));
        Assert.False(caps.CanCancel.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.ClosedNotCancellableReason, caps.CanCancel.Reason);
    }

    // ===================== NC/ND: solo si hay CAE vivo que corregir =====================

    [Fact]
    public void CanEmitCreditDebitNote_RequiresLiveCae()
    {
        var withoutCae = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Cancelled, hasLiveCae: false));
        Assert.False(withoutCae.CanEmitCreditDebitNote.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(withoutCae.CanEmitCreditDebitNote.Reason));

        var withCae = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Cancelled, hasLiveCae: true));
        Assert.True(withCae.CanEmitCreditDebitNote.Allowed);
    }

    // ===================== Motivos: todo No trae motivo, en TODOS los estados =====================

    [Fact]
    public void EveryDeniedCapability_HasReason_AcrossAllStates()
    {
        foreach (var status in AllStatuses)
        {
            var caps = ReservaCapabilityPolicy.For(Ctx(status, balance: 100m, hasLiveCae: false));
            AssertReasonWhenDenied(caps.CanInvoiceSale, status, nameof(caps.CanInvoiceSale));
            AssertReasonWhenDenied(caps.CanEmitCreditDebitNote, status, nameof(caps.CanEmitCreditDebitNote));
            AssertReasonWhenDenied(caps.CanRegisterPayment, status, nameof(caps.CanRegisterPayment));
            AssertReasonWhenDenied(caps.CanEditOrDeletePayment, status, nameof(caps.CanEditOrDeletePayment));
            AssertReasonWhenDenied(caps.CanEditServices, status, nameof(caps.CanEditServices));
            AssertReasonWhenDenied(caps.CanCancel, status, nameof(caps.CanCancel));
            AssertReasonWhenDenied(caps.CanAdvance, status, nameof(caps.CanAdvance));
            AssertReasonWhenDenied(caps.CanEmitVoucher, status, nameof(caps.CanEmitVoucher));
        }
    }

    private static void AssertReasonWhenDenied(Cap cap, string status, string capabilityName)
    {
        if (!cap.Allowed)
            Assert.False(string.IsNullOrWhiteSpace(cap.Reason), $"{capabilityName} en {status} debe traer motivo.");
    }

    // ===================== Transiciones: Closed revierte a {Traveling, ToSettle} (Decision 4-bis) =====================

    [Fact]
    public void AllowedRevert_Closed_IncludesToSettle()
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Closed, balance: 0m));
        Assert.Contains(EstadoReserva.Traveling, caps.AllowedRevert);
        Assert.Contains(EstadoReserva.ToSettle, caps.AllowedRevert);
    }

    [Fact]
    public void AllowedForward_Closed_IsEmpty()
    {
        // Decision 4: Closed no tiene salida forward (no se cancela una Finalizada).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Closed, balance: 0m));
        Assert.Empty(caps.AllowedForward);
        Assert.False(caps.CanAdvance.Allowed);
    }

    [Fact]
    public void AllowedForward_Traveling_HasClosedAndToSettle_ButNotCancelled()
    {
        // ADR-035 (2026-06-19): En viaje YA NO se cancela (se corrige por NC/ajuste). Cancelled salio de los
        // destinos forward de Traveling; quedan Closed (cierre) y ToSettle (apartar para liquidar).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Traveling, balance: 0m));
        Assert.Contains(EstadoReserva.Closed, caps.AllowedForward);
        Assert.Contains(EstadoReserva.ToSettle, caps.AllowedForward);
        Assert.DoesNotContain(EstadoReserva.Cancelled, caps.AllowedForward);
    }
}
