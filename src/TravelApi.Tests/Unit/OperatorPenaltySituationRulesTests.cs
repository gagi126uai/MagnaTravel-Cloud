using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Spec "el paso de multa vive en la ficha" (A2, 2026-07-08): tests de la regla PURA de derivacion del paso de la
/// multa del operador (<see cref="OperatorPenaltySituationRules.Derive"/>). Cubre la matriz completa de estados sin
/// tocar base ni servicios.
/// </summary>
public class OperatorPenaltySituationRulesTests
{
    private static OperatorPenaltySituationState Derive(
        bool hasLiveCancellation,
        PenaltyStatus penalty,
        DebitNoteStatus debitNote,
        bool hasDebitNoteInvoice = false,
        bool isPendingDecision = false)
        => OperatorPenaltySituationRules.Derive(new OperatorPenaltySituationRules.Fields(
            HasLiveCancellation: hasLiveCancellation,
            PenaltyStatus: penalty,
            DebitNoteStatus: debitNote,
            HasDebitNoteInvoice: hasDebitNoteInvoice,
            IsPendingDecision: isPendingDecision));

    [Fact]
    public void NoLiveCancellation_IsNone()
    {
        Assert.Equal(
            OperatorPenaltySituationState.None,
            Derive(hasLiveCancellation: false, PenaltyStatus.Estimated, DebitNoteStatus.NotApplicable));
    }

    [Fact]
    public void Estimated_PendingDecision_IsPendingDecision()
    {
        Assert.Equal(
            OperatorPenaltySituationState.PendingDecision,
            Derive(true, PenaltyStatus.Estimated, DebitNoteStatus.NotApplicable, isPendingDecision: true));
    }

    [Fact]
    public void Estimated_NotPendingDecision_IsNone()
    {
        // Estimated pero SIN nada que decidir ahora (NC sin CAE aun / flag off): la ficha no muestra el paso.
        Assert.Equal(
            OperatorPenaltySituationState.None,
            Derive(true, PenaltyStatus.Estimated, DebitNoteStatus.NotApplicable, isPendingDecision: false));
    }

    [Fact]
    public void Confirmed_DebitNoteIssued_IsDone()
    {
        Assert.Equal(
            OperatorPenaltySituationState.Done,
            Derive(true, PenaltyStatus.Confirmed, DebitNoteStatus.Issued, hasDebitNoteInvoice: true));
    }

    [Fact]
    public void Confirmed_DebitNotePending_IsQueued()
    {
        Assert.Equal(
            OperatorPenaltySituationState.DebitNoteQueued,
            Derive(true, PenaltyStatus.Confirmed, DebitNoteStatus.Pending, hasDebitNoteInvoice: true));
    }

    [Fact]
    public void Confirmed_DebitNoteFailed_IsFailed()
    {
        Assert.Equal(
            OperatorPenaltySituationState.DebitNoteFailed,
            Derive(true, PenaltyStatus.Confirmed, DebitNoteStatus.Failed));
    }

    [Fact]
    public void Confirmed_DebitNoteManualReview_IsNeedsAmountCurrency()
    {
        Assert.Equal(
            OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency,
            Derive(true, PenaltyStatus.Confirmed, DebitNoteStatus.ManualReview));
    }

    [Fact]
    public void Confirmed_DebitNoteNotApplicable_IsConfirmedNoDebitNote()
    {
        Assert.Equal(
            OperatorPenaltySituationState.ConfirmedNoDebitNote,
            Derive(true, PenaltyStatus.Confirmed, DebitNoteStatus.NotApplicable));
    }

    [Fact]
    public void Waived_IsWaived()
    {
        // Waived gana aunque el DebitNoteStatus quedara en algo previo: la pata se cerro sin multa.
        Assert.Equal(
            OperatorPenaltySituationState.Waived,
            Derive(true, PenaltyStatus.Waived, DebitNoteStatus.NotApplicable));
    }

    // ============================================================
    // ToOutcome: el paso fino colapsa al resultado grueso (N2, 2026-07-08).
    // ============================================================

    [Theory]
    [InlineData(OperatorPenaltySituationState.None, OperatorPenaltyOutcome.None)]
    [InlineData(OperatorPenaltySituationState.PendingDecision, OperatorPenaltyOutcome.Pending)]
    [InlineData(OperatorPenaltySituationState.Waived, OperatorPenaltyOutcome.Waived)]
    [InlineData(OperatorPenaltySituationState.DebitNoteQueued, OperatorPenaltyOutcome.Confirmed)]
    [InlineData(OperatorPenaltySituationState.DebitNoteFailed, OperatorPenaltyOutcome.Confirmed)]
    [InlineData(OperatorPenaltySituationState.DebitNoteNeedsAmountCurrency, OperatorPenaltyOutcome.Confirmed)]
    [InlineData(OperatorPenaltySituationState.ConfirmedNoDebitNote, OperatorPenaltyOutcome.Confirmed)]
    [InlineData(OperatorPenaltySituationState.Done, OperatorPenaltyOutcome.Confirmed)]
    public void ToOutcome_MapsEachState(OperatorPenaltySituationState state, OperatorPenaltyOutcome expected)
    {
        Assert.Equal(expected, OperatorPenaltySituationRules.ToOutcome(state));
    }
}
