using System.Linq;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-035 / ADR-036: cobertura PURA de la politica de capacidades (ReservaCapabilityPolicy). Verifica la
/// matriz EXACTA para los 10 estados (ADR-036 elimino ToSettle) x cada capacidad, que todo "No" trae motivo,
/// y las transiciones.
///
/// <para>ADR-036 (2026-06-21, prepago puro): se quito ToSettle; "En viaje" (Traveling) pasa a SOLO LECTURA
/// total (no edita, no cobra, no factura); la factura de venta es SOLO en Confirmed; Closed revierte solo a
/// Traveling; reserva con plata viva no se cancela (se anula).</para>
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
        EstadoReserva.Closed,
        EstadoReserva.Lost,
        EstadoReserva.Cancelled,
        EstadoReserva.PendingOperatorRefund,
    };

    /// <summary>Contexto con deuda (Balance>0) y sin ataduras fiscales, salvo que el test diga lo contrario.</summary>
    private static ReservaCapabilityContext Ctx(
        string status, decimal balance = 100m, bool hasLiveCae = false, bool hasAnyPayment = false)
        => new(status, balance, HasLiveCae: hasLiveCae, HasLiveVoucher: false, HasLiveEditAuth: false, HasAnyPayment: hasAnyPayment);

    // ===================== Factura de venta: SOLO {Confirmed} (ADR-036) =====================

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    // ADR-036: en viaje NO se factura (la factura de venta se emite antes de viajar).
    [InlineData(EstadoReserva.Traveling, false)]
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

    // ===================== Cobrar: venta firme (incluye Closed, NO Traveling) con deuda =====================

    [Theory]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    // ADR-036: en viaje NO se cobra (el viaje ya empezo; para llegar a Traveling el cliente quedo saldado).
    [InlineData(EstadoReserva.Traveling, false)]
    // ADR-033 A1/E2: una Finalizada (Closed) CON deuda SI admite cobro.
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
    public void CanRegisterPayment_Traveling_HasDedicatedReason()
    {
        // ADR-036: motivo propio "en viaje no se cobra" (no el generico de estado no firme).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Traveling, balance: 100m));
        Assert.False(caps.CanRegisterPayment.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.TravelingNotChargeableReason, caps.CanRegisterPayment.Reason);
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
    [InlineData(EstadoReserva.Quotation, true)]
    [InlineData(EstadoReserva.Budget, true)]
    public void CanEditOrDeletePayment_BlockedOnTerminalStates(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanEditOrDeletePayment.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanEditOrDeletePayment.Reason));
    }

    // ===================== Voucher: SOLO {Confirmed, Traveling, Closed} (ADR-036 quito ToSettle) =====================

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
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

    // ===================== Cancelar: NO en {Traveling, Closed, Lost, Cancelled, PendingOperatorRefund} =====================

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
    public void CanCancel_MatchesMatrix(string status, bool expected)
    {
        // Estado limpio (sin plata viva): la cancelabilidad depende solo del estado.
        var caps = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m, hasLiveCae: false, hasAnyPayment: false));
        Assert.Equal(expected, caps.CanCancel.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanCancel.Reason));
    }

    [Fact]
    public void CanCancel_Closed_HasDedicatedReason()
    {
        // Decision 4: una Finalizada no se cancela. Motivo especifico distinto del generico.
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Closed, balance: 0m));
        Assert.False(caps.CanCancel.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.ClosedNotCancellableReason, caps.CanCancel.Reason);
    }

    // ===================== ADR-036: reserva con plata viva no se cancela (se anula) =====================

    [Fact]
    public void CanCancel_WithLivePayment_RoutesToAnnul()
    {
        // Estado cancelable por matriz (Confirmed) PERO con cobro vivo -> No, con motivo "hay que anularla".
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Confirmed, balance: 0m, hasAnyPayment: true));
        Assert.False(caps.CanCancel.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.HasLiveMoneyMustAnnulReason, caps.CanCancel.Reason);
    }

    [Fact]
    public void CanCancel_WithLiveCae_RoutesToAnnul()
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Confirmed, balance: 0m, hasLiveCae: true));
        Assert.False(caps.CanCancel.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.HasLiveMoneyMustAnnulReason, caps.CanCancel.Reason);
    }

    [Fact]
    public void CanCancel_CleanFirmReserva_Allowed()
    {
        // Confirmed sin plata viva: SI se puede cancelar (baja simple).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Confirmed, balance: 0m, hasLiveCae: false, hasAnyPayment: false));
        Assert.True(caps.CanCancel.Allowed);
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

    // ===================== Transiciones (ADR-036) =====================

    [Fact]
    public void AllowedRevert_Closed_OnlyTraveling()
    {
        // ADR-036: Closed revierte SOLO a Traveling (Closed->ToSettle murio; corregir factura = NC/ND).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Closed, balance: 0m));
        Assert.Contains(EstadoReserva.Traveling, caps.AllowedRevert);
        Assert.Single(caps.AllowedRevert);
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
    public void AllowedForward_Traveling_OnlyClosed()
    {
        // ADR-036: la unica salida forward de Traveling es Closed (ToSettle murio; Cancelled ya estaba fuera).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Traveling, balance: 0m));
        Assert.Contains(EstadoReserva.Closed, caps.AllowedForward);
        Assert.Single(caps.AllowedForward);
        Assert.DoesNotContain(EstadoReserva.Cancelled, caps.AllowedForward);
    }
}
