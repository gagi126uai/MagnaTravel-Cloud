using System.Collections.Generic;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-040 (cuenta corriente del cliente, 2026-06-26): cobertura PURA del evaluador de credito
/// <see cref="ClientCreditPolicy"/> y del resolvedor de modo <see cref="ClientBillingModeResolver"/>.
/// Sin base de datos: solo la logica de la decision (dentro/fuera de limite, moneda sin limite, avisa/frena,
/// mora, multimoneda).
/// </summary>
public class Adr040ClientCreditPolicyTests
{
    private static Dictionary<string, decimal> Money(params (string Currency, decimal Amount)[] rows)
    {
        var dict = new Dictionary<string, decimal>(System.StringComparer.Ordinal);
        foreach (var (currency, amount) in rows) dict[currency] = amount;
        return dict;
    }

    private static ClientCreditContext Ctx(
        Dictionary<string, decimal> limits,
        Dictionary<string, decimal> exposure,
        bool blockWhenOverLimit = true,
        bool inArrears = false,
        decimal thisReservaBalance = 0m)
        => new(limits, exposure, inArrears, blockWhenOverLimit, thisReservaBalance);

    // ===================== Modo efectivo =====================

    [Fact]
    public void Resolve_NullCustomerMode_InheritsAgencyDefault()
    {
        Assert.Equal(CustomerBillingMode.Account,
            ClientBillingModeResolver.Resolve(null, CustomerBillingMode.Account));
        Assert.Equal(CustomerBillingMode.Prepaid,
            ClientBillingModeResolver.Resolve(null, CustomerBillingMode.Prepaid));
    }

    [Fact]
    public void Resolve_CustomerMode_OverridesDefault()
    {
        Assert.Equal(CustomerBillingMode.Account,
            ClientBillingModeResolver.Resolve(CustomerBillingMode.Account, CustomerBillingMode.Prepaid));
        Assert.Equal(CustomerBillingMode.Prepaid,
            ClientBillingModeResolver.Resolve(CustomerBillingMode.Prepaid, CustomerBillingMode.Account));
    }

    // ===================== Dentro / fuera de limite =====================

    [Fact]
    public void WithinLimit_SingleCurrency_AllowedNoWarning()
    {
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("ARS", 300_000m))));

        Assert.True(decision.Allowed);
        Assert.Null(decision.BlockReason);
        Assert.Null(decision.Warning);
    }

    [Fact]
    public void ExactlyAtLimit_IsAllowed()
    {
        // Igualar el limite NO lo supera (la regla es "supera", con tolerancia de redondeo).
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("ARS", 500_000m))));
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void OverLimit_Block_ReturnsBlockedWithTravelingMessage()
    {
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("ARS", 500_001m)), blockWhenOverLimit: true));

        Assert.False(decision.Allowed);
        Assert.Equal(ClientCreditPolicy.OverCreditLimitTravelingMessage, decision.BlockReason);
    }

    [Fact]
    public void OverLimit_WarnOnly_AllowedButWarns()
    {
        // La llave "solo avisar": deja pasar PERO siempre emite el aviso (nunca sin control).
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("ARS", 900_000m)), blockWhenOverLimit: false));

        Assert.True(decision.Allowed);
        Assert.Null(decision.BlockReason);
        Assert.Equal(ClientCreditPolicy.OverCreditLimitWarning, decision.Warning);
    }

    // ===================== Moneda SIN limite = prepago de esa moneda =====================

    [Fact]
    public void DebtInCurrencyWithoutLimit_Block_IsBlocked()
    {
        // Debe en USD pero solo tiene limite en ARS: USD es prepago -> bloquea.
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("USD", 100m)), blockWhenOverLimit: true));

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void DebtInCurrencyWithoutLimit_WarnOnly_StillBlocks_OwnerDecision()
    {
        // DECISION del dueño (2026-06-26): la moneda SIN limite es prepago duro. La llave "solo avisar" NO la
        // afloja: bloquea igual aunque blockWhenOverLimit=false. Contrasta con OverLimit_WarnOnly_*, donde
        // superar un limite DEFINIDO si se afloja.
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("USD", 100m)), blockWhenOverLimit: false));

        Assert.False(decision.Allowed);
        Assert.Equal(ClientCreditPolicy.OverCreditLimitTravelingMessage, decision.BlockReason);
    }

    [Fact]
    public void MixedViolation_WarnOnly_NoLimitCurrencyWins_Blocks()
    {
        // ARS supera un limite definido (aflojable) Y debe en USD sin limite (duro). Con la llave en "solo
        // avisar", el caso DURO manda: bloquea. Asi no se cuela una deuda en moneda sin limite por venir
        // acompañada de un exceso aflojable.
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("ARS", 600_000m), ("USD", 100m)), blockWhenOverLimit: false));

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void NoLimitsAtAll_AnyDebt_IsBlocked()
    {
        // Sin ninguna fila de limite, deber cualquier cosa frena (todas las monedas son prepago).
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(), Money(("ARS", 1m))));
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void ZeroLimitRow_BehavesLikeAbsence_BlocksEvenWarnOnly()
    {
        // N1: una fila con Limit=0 = sin credito en esa moneda = prepago duro. Identico a NO tener fila: FRENA
        // aun con la llave en "solo avisar". El cero explicito (lo mas restrictivo) no puede ser menos estricto
        // que la ausencia.
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("USD", 0m)), Money(("USD", 100m)), blockWhenOverLimit: false));

        Assert.False(decision.Allowed);
        Assert.Equal(ClientCreditPolicy.OverCreditLimitTravelingMessage, decision.BlockReason);
    }

    [Fact]
    public void PositiveLimitOver_WarnOnly_StillPasses_ContrastsWithZeroRow()
    {
        // Contraste con el test de arriba: un limite POSITIVO superado bajo "solo avisar" SI pasa (con aviso).
        // Asi queda claro que solo la fila <= 0 es prepago duro; la fila > 0 sigue gobernada por la llave.
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("USD", 1_000m)), Money(("USD", 5_000m)), blockWhenOverLimit: false));

        Assert.True(decision.Allowed);
        Assert.Equal(ClientCreditPolicy.OverCreditLimitWarning, decision.Warning);
    }

    // ===================== Multimoneda: el saldo a favor de una NO compensa otra =====================

    [Fact]
    public void MultiCurrency_WithinArsButOverUsd_IsBlocked()
    {
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m), ("USD", 1_000m)),
                Money(("ARS", 100_000m), ("USD", 1_500m))));
        Assert.False(decision.Allowed);
    }

    [Fact]
    public void NegativeExposureInCurrency_IsIgnored()
    {
        // Saldo a favor (exposicion negativa) en una moneda NO cuenta como deuda y no la bloquea, aunque esa
        // moneda no tenga limite.
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("ARS", 100_000m), ("USD", -50m))));
        Assert.True(decision.Allowed);
    }

    // ===================== Mora frena TODO (Fase 2; aca probamos el punto de extension) =====================

    [Fact]
    public void InArrears_BlocksEvenWithinLimit()
    {
        var decision = ClientCreditPolicy.EvaluateCanTravel(
            Ctx(Money(("ARS", 500_000m)), Money(("ARS", 1_000m)), blockWhenOverLimit: false, inArrears: true));

        Assert.False(decision.Allowed);
        Assert.Equal(ClientCreditPolicy.InArrearsTravelingMessage, decision.BlockReason);
    }

    // Nota: NO hay test de "cierre" — el cierre de un Account es INCONDICIONAL (no llama a la politica). Ver
    // ADR-040 regla 7 y los tests de cierre a nivel job en Adr040AccountCreditEngineTests.
}
