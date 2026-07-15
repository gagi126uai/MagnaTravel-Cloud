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

    // ============= Factura de venta: {Confirmed, Traveling, Closed} (ADR-037, desacople) =============

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    // ADR-037: la factura se desacopla del estado. En viaje SI se factura; en Finalizada tambien (sin reabrir).
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.Closed, true)]
    // Pre-venta: NO facturable (servicios sin resolver / sin confirmar).
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.InManagement, false)]
    // Anulados: NO facturable (la venta no existe).
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

    // ===================== Voucher: SOLO {Confirmed, Traveling} (B3 2026-06-24 saco Closed) =====================

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.Closed, false)]   // B3: terminal = solo lectura; en Finalizada NO se emite/modifica voucher (ver/reimprimir si)
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

    // ===================== CanAnnul: anular formal (deshacer con plata viva, emite NC) =====================

    [Fact]
    public void CanAnnul_WithLiveCae_Allowed()
    {
        // Confirmed con factura (CAE) -> anular formal SI aplica (es el complemento de canCancel=No).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Confirmed, balance: 0m, hasLiveCae: true));
        Assert.True(caps.CanAnnul.Allowed);
        // Coherencia: canCancel No (hay que anularla) y canAnnul Yes -> el boton del front se muestra por canAnnul.
        Assert.False(caps.CanCancel.Allowed);
    }

    [Fact]
    public void CanAnnul_WithLivePayment_Allowed()
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Confirmed, balance: 0m, hasAnyPayment: true));
        Assert.True(caps.CanAnnul.Allowed);
    }

    [Fact]
    public void CanAnnul_CleanFirmReserva_NotAllowed()
    {
        // Sin plata viva no hay anulacion formal: el camino es la baja simple (canCancel=Yes).
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Confirmed, balance: 0m, hasLiveCae: false, hasAnyPayment: false));
        Assert.False(caps.CanAnnul.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.NoLiveMoneyToAnnulReason, caps.CanAnnul.Reason);
        Assert.True(caps.CanCancel.Allowed); // sin plata, deshacer = baja simple
    }

    [Theory]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    public void CanAnnul_TerminalStates_NotAllowed_EvenWithMoney(string status)
    {
        // Estados terminales: ni con plata viva se anula (una Cerrada/En viaje/Perdida/Anulada/Esperando
        // reembolso no se deshace por anulacion formal).
        var caps = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m, hasLiveCae: true, hasAnyPayment: true));
        Assert.False(caps.CanAnnul.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(caps.CanAnnul.Reason));
    }

    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.InManagement)]
    [InlineData(EstadoReserva.Confirmed)]
    public void Button_AnularReserva_VisibleInLiveStates_WithOrWithoutMoney(string status)
    {
        // Invariante del boton "Anular reserva" del front: en estados vivos SIEMPRE hay un camino para
        // deshacer (baja simple si no hay plata, anulacion formal si la hay). El front usa
        // canCancel.Allowed || canAnnul.Allowed; aca verificamos que esa union sea true en ambos casos.
        var sinPlata = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m, hasLiveCae: false, hasAnyPayment: false));
        Assert.True(sinPlata.CanCancel.Allowed || sinPlata.CanAnnul.Allowed);

        var conPlata = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m, hasLiveCae: true, hasAnyPayment: false));
        Assert.True(conPlata.CanCancel.Allowed || conPlata.CanAnnul.Allowed);
    }

    // ===================== Eliminar: solo pre-venta sin plata viva =====================

    [Theory]
    // Pre-venta sin plata: se puede ELIMINAR fisicamente.
    [InlineData(EstadoReserva.Quotation, true)]
    [InlineData(EstadoReserva.Budget, true)]
    // Mas alla de pre-venta: NO se borra (se cancela/anula).
    [InlineData(EstadoReserva.InManagement, false)]
    [InlineData(EstadoReserva.Confirmed, false)]
    [InlineData(EstadoReserva.Traveling, false)]
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void CanDelete_OnlyInPreSaleWithoutLiveMoney(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m, hasLiveCae: false, hasAnyPayment: false));
        Assert.Equal(expected, caps.CanDelete.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanDelete.Reason));
    }

    [Fact]
    public void CanDelete_PreSaleWithPayment_NotAllowed()
    {
        // Un presupuesto con cobros NO se borra (la plata viva exige anular). Antes el backend no mandaba esta
        // capacidad y el front mostraba "Eliminar" igual: este es el caso que cierra el agujero.
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Budget, balance: 0m, hasLiveCae: false, hasAnyPayment: true));
        Assert.False(caps.CanDelete.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.HasLiveMoneyCannotDeleteReason, caps.CanDelete.Reason);
    }

    [Fact]
    public void CanDelete_PreSaleWithLiveCae_NotAllowed()
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Budget, balance: 0m, hasLiveCae: true, hasAnyPayment: false));
        Assert.False(caps.CanDelete.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.HasLiveMoneyCannotDeleteReason, caps.CanDelete.Reason);
    }

    [Fact]
    public void CanDelete_PreSaleWithOperatorConfirmedService_NotAllowed()
    {
        // (2026-06-26) Un presupuesto SIN plata pero con un servicio ya confirmado con el operador NO se borra
        // (hay compromiso con el proveedor). La capacidad debe COINCIDIR con DeleteGuards (que bloquea ese caso
        // aun en pre-venta); antes la capacidad mentía y el front mostraba "Eliminar" -> 409 al clickear.
        var ctx = new ReservaCapabilityContext(
            EstadoReserva.Budget, Balance: 0m, HasLiveCae: false, HasLiveVoucher: false, HasLiveEditAuth: false,
            HasAnyPayment: false, HasPendingOperatorPenalty: false, HasOperatorConfirmedService: true);
        var caps = ReservaCapabilityPolicy.For(ctx);
        Assert.False(caps.CanDelete.Allowed);
        Assert.Equal(ReservaCapabilityPolicy.HasOperatorConfirmedServiceCannotDeleteReason, caps.CanDelete.Reason);
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
            AssertReasonWhenDenied(caps.CanAnnul, status, nameof(caps.CanAnnul));
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
    public void AllowedRevert_Budget_DoesNotReturnToLegacyQuotation()
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Budget, balance: 0m));
        Assert.Empty(caps.AllowedRevert);
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

    // ===================== G3: cancelar SERVICIO solo en {InManagement, Confirmed} =====================

    [Theory]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Quotation, false)]   // pre-venta: el servicio se BORRA, no se cancela
    [InlineData(EstadoReserva.Budget, false)]      // pre-venta
    [InlineData(EstadoReserva.Traveling, false)]   // en viaje no se cancela (NC/ajuste)
    [InlineData(EstadoReserva.Closed, false)]
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void CanCancelServices_OnlyInActiveCollectionStates(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanCancelServices.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanCancelServices.Reason));
    }

    // ===================== G5: reprogramar viaje solo en {Confirmed, Traveling} =====================

    [Theory]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]    // el caso central: el viaje se atraso y se corre
    [InlineData(EstadoReserva.Quotation, false)]
    [InlineData(EstadoReserva.Budget, false)]
    [InlineData(EstadoReserva.InManagement, false)] // pre-firme: armar fechas != reprogramar
    [InlineData(EstadoReserva.Closed, false)]      // terminal: el viaje ya termino
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void CanReschedule_OnlyFromConfirmedOnward(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanReschedule.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanReschedule.Reason));
    }

    // ===================== B3: documentos NO se agregan/modifican en terminales =====================

    [Theory]
    [InlineData(EstadoReserva.Quotation, true)]
    [InlineData(EstadoReserva.Budget, true)]
    [InlineData(EstadoReserva.InManagement, true)]
    [InlineData(EstadoReserva.Confirmed, true)]
    [InlineData(EstadoReserva.Traveling, true)]
    [InlineData(EstadoReserva.Closed, false)]      // terminal: documentos solo lectura
    [InlineData(EstadoReserva.Lost, false)]
    [InlineData(EstadoReserva.Cancelled, false)]
    [InlineData(EstadoReserva.PendingOperatorRefund, false)]
    public void CanUploadDocument_BlockedOnlyOnTerminalStates(string status, bool expected)
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.Equal(expected, caps.CanUploadDocument.Allowed);
        if (!expected) Assert.False(string.IsNullOrWhiteSpace(caps.CanUploadDocument.Reason));
    }

    // ============ H3: "Confirmar multa del operador" SOLO con multa pendiente (no por estado) ============

    /// <summary>
    /// H3 (2026-06-24): la capacidad depende EXCLUSIVAMENTE de HasPendingOperatorPenalty, NO del estado. Con la
    /// multa pendiente true, allowed en CUALQUIER estado (la confirmacion diferida ocurre cuando la reserva ya
    /// esta en su estado terminal). Esto ancla el bug: antes el front mostraba el boton por estar en
    /// PendingOperatorRefund; ahora la verdad es "hay multa pendiente".
    /// </summary>
    [Theory]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.Closed)]
    public void CanConfirmOperatorPenalty_AllowedWhenPendingPenalty(string status)
    {
        var ctx = new ReservaCapabilityContext(
            status, Balance: 0m, HasLiveCae: false, HasLiveVoucher: false,
            HasLiveEditAuth: false, HasAnyPayment: false, HasPendingOperatorPenalty: true);
        var caps = ReservaCapabilityPolicy.For(ctx);
        Assert.True(caps.CanConfirmOperatorPenalty.Allowed);
        Assert.Null(caps.CanConfirmOperatorPenalty.Reason);
    }

    /// <summary>
    /// H3: sin multa pendiente, la capacidad es false en TODOS los estados (incluido PendingOperatorRefund, el
    /// estado que antes la habilitaba de mas) y trae motivo legible. Es el corazon del fix: el estado no alcanza.
    /// </summary>
    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    [InlineData(EstadoReserva.InManagement)]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    public void CanConfirmOperatorPenalty_BlockedWhenNoPendingPenalty(string status)
    {
        // Ctx por defecto deja HasPendingOperatorPenalty en false.
        var caps = ReservaCapabilityPolicy.For(Ctx(status));
        Assert.False(caps.CanConfirmOperatorPenalty.Allowed);
        Assert.False(string.IsNullOrWhiteSpace(caps.CanConfirmOperatorPenalty.Reason));
    }

    /// <summary>
    /// H3: PendingOperatorRefund SIN multa pendiente es exactamente el caso del bug del dueño (boton muerto):
    /// el estado de "esperando reembolso" no implica multa por confirmar. Debe quedar bloqueado.
    /// </summary>
    [Fact]
    public void CanConfirmOperatorPenalty_PendingOperatorRefundWithoutPenalty_IsBlocked()
    {
        var caps = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.PendingOperatorRefund));
        Assert.False(caps.CanConfirmOperatorPenalty.Allowed);
        Assert.Equal(
            ReservaCapabilityPolicy.NoPendingOperatorPenaltyReason,
            caps.CanConfirmOperatorPenalty.Reason);
    }
}
