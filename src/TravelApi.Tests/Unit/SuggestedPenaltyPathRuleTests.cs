using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Configuracion de multas de cancelacion (2026-07-14): tests de la regla PURA que sugiere el camino de la
/// multa (<see cref="OperatorPenaltySituationRules.SuggestPenaltyPath"/>). Cubre la matriz completa
/// estado x comportamiento del operador, sin tocar base ni servicios (mismo estilo que
/// <see cref="OperatorPenaltySituationRulesTests"/>).
/// </summary>
public class SuggestedPenaltyPathRuleTests
{
    // ---------------------------------------------------------------------------------------------------
    // En la etapa de la PREGUNTA (PendingDecision): la sugerencia sigue al comportamiento configurado.
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void PendingDecision_SupplierRarelyCharges_SuggestsProbablyNoPenalty()
    {
        var result = OperatorPenaltySituationRules.SuggestPenaltyPath(
            OperatorPenaltySituationState.PendingDecision, SupplierPenaltyBehavior.RarelyCharges);

        Assert.Equal("probablyNoPenalty", result);
    }

    [Fact]
    public void PendingDecision_SupplierUsuallyCharges_SuggestsProbablyPenalty()
    {
        var result = OperatorPenaltySituationRules.SuggestPenaltyPath(
            OperatorPenaltySituationState.PendingDecision, SupplierPenaltyBehavior.UsuallyCharges);

        Assert.Equal("probablyPenalty", result);
    }

    [Fact]
    public void PendingDecision_SupplierUnknown_SuggestsNothing()
    {
        // Sin pista configurada para el operador: mejor no sugerir nada que inventar una sugerencia.
        var result = OperatorPenaltySituationRules.SuggestPenaltyPath(
            OperatorPenaltySituationState.PendingDecision, SupplierPenaltyBehavior.Unknown);

        Assert.Null(result);
    }

    // ---------------------------------------------------------------------------------------------------
    // Fuera de la etapa de la pregunta (paso ya resuelto/cerrado/emitido/sin cancelacion en juego): nunca
    // sugiere nada, aunque el operador tenga un comportamiento configurado.
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(OperatorPenaltySituationState.None)]
    [InlineData(OperatorPenaltySituationState.DebitNoteQueued)]
    [InlineData(OperatorPenaltySituationState.DebitNoteFailed)]
    [InlineData(OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency)]
    [InlineData(OperatorPenaltySituationState.ConfirmedNoDebitNote)]
    [InlineData(OperatorPenaltySituationState.Waived)]
    [InlineData(OperatorPenaltySituationState.Done)]
    [InlineData(OperatorPenaltySituationState.MultiOperatorNeedsManualReview)]
    [InlineData(OperatorPenaltySituationState.DebitNoteAnnulling)]
    [InlineData(OperatorPenaltySituationState.DebitNoteAnnulmentFailed)]
    public void AnyResolvedState_SupplierRarelyCharges_NeverSuggests(OperatorPenaltySituationState state)
    {
        var result = OperatorPenaltySituationRules.SuggestPenaltyPath(state, SupplierPenaltyBehavior.RarelyCharges);
        Assert.Null(result);
    }

    [Theory]
    [InlineData(OperatorPenaltySituationState.None)]
    [InlineData(OperatorPenaltySituationState.Done)]
    [InlineData(OperatorPenaltySituationState.Waived)]
    public void AnyResolvedState_SupplierUsuallyCharges_NeverSuggests(OperatorPenaltySituationState state)
    {
        var result = OperatorPenaltySituationRules.SuggestPenaltyPath(state, SupplierPenaltyBehavior.UsuallyCharges);
        Assert.Null(result);
    }
}
