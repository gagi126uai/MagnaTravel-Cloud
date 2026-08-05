using System;
using System.Linq;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-035 §11 — TEST CRUZADO DE COHERENCIA (C2, GATE BLOQUEANTE DE MERGE).
///
/// <para>La politica de capacidades es una FACHADA DE LECTURA; los guards de escritura
/// (EnsureCollectable, la allow-list de facturacion, el conjunto de terminales para editar/borrar cobro)
/// son la defensa final. Este test verifica que NUNCA se desincronicen de forma silenciosa: para cada
/// estado/accion donde la politica dice <c>Allowed=true</c>, el guard correspondiente NO debe contradecirla
/// (y viceversa para los casos representativos de <c>Allowed=false</c>).</para>
///
/// <para>Si este test no esta verde, el merge no procede.</para>
/// </summary>
public class ReservaCapabilitiesCrossCheckTests
{
    // ADR-036 (2026-06-21): ToSettle eliminado. 9 estados (Quotation..PendingOperatorRefund) + Archived
    // (lateral legacy, no se modela aca).
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

    private static ReservaCapabilityContext Ctx(string status, decimal balance, bool hasLiveCae = false)
        => new(status, balance, hasLiveCae, HasLiveVoucher: false, HasLiveEditAuth: false, HasAnyPayment: false);

    // =====================================================================================================
    // CanRegisterPayment  vs  Reserva.EnsureCollectable() (el guard fino de alta de cobro).
    // La politica NUNCA puede habilitar un cobro que EnsureCollectable rechazaria.
    // =====================================================================================================

    [Theory]
    [InlineData(100)]  // con deuda
    [InlineData(0)]    // saldado
    [InlineData(-50)]  // saldo a favor
    public void CanRegisterPayment_NeverAllowsWhatEnsureCollectableRejects(int balanceInt)
    {
        decimal balance = balanceInt;
        foreach (var status in AllStatuses)
        {
            var policyAllows = ReservaCapabilityPolicy.For(Ctx(status, balance)).CanRegisterPayment.Allowed;

            // Espejamos el guard fino: EnsureCollectable lanza si NO es venta firme o si Balance <= 0.
            var reserva = new Reserva { Status = status, Balance = balance };
            var guardAllows = TryEnsureCollectable(reserva);

            if (policyAllows)
            {
                Assert.True(guardAllows,
                    $"La politica habilita cobro en {status} (balance {balance}) pero EnsureCollectable lo rechaza.");
            }
        }
    }

    private static bool TryEnsureCollectable(Reserva reserva)
    {
        try
        {
            reserva.EnsureCollectable();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// ADR-036 (2026-06-21): ASIMETRIA INTENCIONAL — "En viaje" (Traveling) NO es cobrable en NINGUNA de las
    /// dos capas. La politica lo bloquea con su motivo propio ("en viaje no se cobra") y el guard fino
    /// EnsureCollectable tambien lo rechaza (Traveling salio de SaleFirmStatuses). Ambas capas coinciden en NO:
    /// no es una desincronizacion, es la decision de prepago puro. Este test deja la asimetria documentada.
    /// </summary>
    [Theory]
    [InlineData(100)]  // con deuda
    [InlineData(0)]    // saldado
    public void Traveling_IsNotCollectable_InBothLayers(int balanceInt)
    {
        decimal balance = balanceInt;
        var policyAllows = ReservaCapabilityPolicy.For(Ctx(EstadoReserva.Traveling, balance)).CanRegisterPayment.Allowed;
        Assert.False(policyAllows);

        var guardAllows = TryEnsureCollectable(new Reserva { Status = EstadoReserva.Traveling, Balance = balance });
        Assert.False(guardAllows);
    }

    // =====================================================================================================
    // CanInvoiceSale  vs  la allow-list de facturacion (InvoiceService usa ReservaCapabilityPolicy.InvoiceableStatuses).
    // La politica y la allow-list deben coincidir exactamente para la factura de venta (sin NC/ND).
    // =====================================================================================================

    [Fact]
    public void CanInvoiceSale_MatchesInvoiceableAllowList_Exactly()
    {
        foreach (var status in AllStatuses)
        {
            var policyAllows = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m)).CanInvoiceSale.Allowed;
            var inAllowList = ReservaCapabilityPolicy.InvoiceableStatuses
                .Contains(status, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(inAllowList, policyAllows);
        }
    }

    // =====================================================================================================
    // ADR-043 Fase 1 (2026-08-05, "gate de facturar") — C2: la politica de capacidades (compuerta de UI)
    // nunca puede decir Allowed=true para una FACTURA DE VENTA que InvoiceService.CreateAsync rechazaria por
    // HasUnacknowledgedChanges. El guard real (integracion, con AFIP mockeado) vive en
    // Adr043InvoiceGateChangesReviewTests; aca fijamos la regla PURA de la que depende ese guard: en
    // cualquier estado facturable, con la marca prendida la politica SIEMPRE da No con el motivo estable
    // que InvoiceService compara por igualdad de string para elegir la excepcion tipada (ver
    // InvoiceService.CreateAsync, rama `invoiceCapability.Reason == ReservaCapabilityPolicy.ChangesPendingReviewReason`).
    // =====================================================================================================

    [Fact]
    public void CanInvoiceSale_NuncaPermiteFacturarConCambiosSinRevisar()
    {
        foreach (var status in AllStatuses)
        {
            var ctxConCambios = new ReservaCapabilityContext(
                status, Balance: 0m, HasLiveCae: false, HasLiveVoucher: false, HasLiveEditAuth: false,
                HasAnyPayment: false, HasUnacknowledgedChanges: true);
            var caps = ReservaCapabilityPolicy.For(ctxConCambios);

            Assert.False(caps.CanInvoiceSale.Allowed,
                $"La politica no debe permitir facturar en {status} con cambios sin revisar.");

            // Si el estado YA era facturable de por si, el motivo que se muestra tiene que ser el de
            // "cambios sin revisar" — es el que InvoiceService usa para elegir ReservaChangesPendingReviewException.
            var eraFacturableSinLaMarca = ReservaCapabilityPolicy.InvoiceableStatuses
                .Contains(status, StringComparer.OrdinalIgnoreCase);
            if (eraFacturableSinLaMarca)
            {
                Assert.Equal(ReservaCapabilityPolicy.ChangesPendingReviewReason, caps.CanInvoiceSale.Reason);
            }
        }
    }

    // =====================================================================================================
    // CanEditOrDeletePayment  vs  el conjunto de TERMINALES.
    // La politica habilita editar/borrar exactamente cuando el estado NO es terminal
    // {Closed, Cancelled, Lost, PendingOperatorRefund}.
    // =====================================================================================================

    [Fact]
    public void CanEditOrDeletePayment_MatchesNonTerminalStates_Exactly()
    {
        var terminals = new[]
        {
            EstadoReserva.Closed,
            EstadoReserva.Cancelled,
            EstadoReserva.Lost,
            EstadoReserva.PendingOperatorRefund,
        };

        foreach (var status in AllStatuses)
        {
            var policyAllows = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m)).CanEditOrDeletePayment.Allowed;
            var isTerminal = terminals.Contains(status, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(!isTerminal, policyAllows);
        }
    }

    // =====================================================================================================
    // CanEmitVoucher  vs  el conjunto que enforza el gate de VoucherService (misma lista del dominio).
    // =====================================================================================================

    [Fact]
    public void CanEmitVoucher_MatchesVoucherStatuses_Exactly()
    {
        foreach (var status in AllStatuses)
        {
            var policyAllows = ReservaCapabilityPolicy.For(Ctx(status, balance: 0m)).CanEmitVoucher.Allowed;
            var inVoucherStatuses = ReservaCapabilityPolicy.VoucherStatuses
                .Contains(status, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(inVoucherStatuses, policyAllows);
        }
    }
}
