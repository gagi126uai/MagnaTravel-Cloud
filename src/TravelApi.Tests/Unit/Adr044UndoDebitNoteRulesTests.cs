using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): tests PUROS (sin DB) de la sub-regla nueva dentro de
/// <see cref="OperatorPenaltySituationRules"/> — cuando la ND esta <c>Issued</c>, un evento de "deshacer" EN
/// VUELO o FALLIDO pisa el <c>Done</c> de siempre. Molde de <c>OperatorPenaltySituationRulesTests</c>.
/// </summary>
public class Adr044UndoDebitNoteRulesTests
{
    // ============================================================
    // Test 1 (spec): Derive mono-operador.
    // ============================================================

    [Fact]
    public void Derive_IssuedWithPendingAnnulment_ReturnsDebitNoteAnnulling()
    {
        var state = OperatorPenaltySituationRules.Derive(new OperatorPenaltySituationRules.Fields(
            HasLiveCancellation: true,
            PenaltyStatus: PenaltyStatus.Confirmed,
            DebitNoteStatus: DebitNoteStatus.Issued,
            HasDebitNoteInvoice: true,
            IsPendingDecision: false,
            HasPendingDebitNoteAnnulment: true,
            HasFailedDebitNoteAnnulment: false));

        Assert.Equal(OperatorPenaltySituationState.DebitNoteAnnulling, state);
    }

    [Fact]
    public void Derive_IssuedWithFailedAnnulment_ReturnsDebitNoteAnnulmentFailed()
    {
        var state = OperatorPenaltySituationRules.Derive(new OperatorPenaltySituationRules.Fields(
            HasLiveCancellation: true,
            PenaltyStatus: PenaltyStatus.Confirmed,
            DebitNoteStatus: DebitNoteStatus.Issued,
            HasDebitNoteInvoice: true,
            IsPendingDecision: false,
            HasPendingDebitNoteAnnulment: false,
            HasFailedDebitNoteAnnulment: true));

        Assert.Equal(OperatorPenaltySituationState.DebitNoteAnnulmentFailed, state);
    }

    [Fact]
    public void Derive_IssuedWithoutAnnulment_StillReturnsDone()
    {
        // Sin ningun evento de deshacer en juego, el estado sigue siendo el de siempre (regresion: NO romper
        // el camino existente cuando la feature nueva no aplica).
        var state = OperatorPenaltySituationRules.Derive(new OperatorPenaltySituationRules.Fields(
            HasLiveCancellation: true,
            PenaltyStatus: PenaltyStatus.Confirmed,
            DebitNoteStatus: DebitNoteStatus.Issued,
            HasDebitNoteInvoice: true,
            IsPendingDecision: false));

        Assert.Equal(OperatorPenaltySituationState.Done, state);
    }

    [Fact]
    public void Derive_AfterUndoConsumado_DesvinculaYVuelveAConfirmedNoDebitNote()
    {
        // Test 2 (spec): tras desvincular la ND (DebitNoteInvoiceId=null / DebitNoteStatus=NotApplicable), el
        // paso vuelve al estado objetivo ya existente — CERO regla nueva para el estado final (reusa el path
        // que ya existia, V8+V9 del diseño).
        var state = OperatorPenaltySituationRules.Derive(new OperatorPenaltySituationRules.Fields(
            HasLiveCancellation: true,
            PenaltyStatus: PenaltyStatus.Confirmed,
            DebitNoteStatus: DebitNoteStatus.NotApplicable,
            HasDebitNoteInvoice: false,
            IsPendingDecision: false));

        Assert.Equal(OperatorPenaltySituationState.ConfirmedNoDebitNote, state);
    }

    // ============================================================
    // Test 1 (spec): DeriveForOperator (path multi-operador).
    // ============================================================

    [Fact]
    public void DeriveForOperator_IssuedWithPendingAnnulment_ReturnsDebitNoteAnnulling()
    {
        var state = OperatorPenaltySituationRules.DeriveForOperator(new OperatorPenaltySituationRules.LineFields(
            LinePenaltyStatus: PenaltyStatus.Confirmed,
            IsPendingDecision: false,
            BcDebitNoteStatus: DebitNoteStatus.Issued,
            IsOperatorSpecificManual: false,
            HasPendingDebitNoteAnnulment: true,
            HasFailedDebitNoteAnnulment: false));

        Assert.Equal(OperatorPenaltySituationState.DebitNoteAnnulling, state);
    }

    [Fact]
    public void DeriveForOperator_IssuedWithFailedAnnulment_ReturnsDebitNoteAnnulmentFailed()
    {
        var state = OperatorPenaltySituationRules.DeriveForOperator(new OperatorPenaltySituationRules.LineFields(
            LinePenaltyStatus: PenaltyStatus.Confirmed,
            IsPendingDecision: false,
            BcDebitNoteStatus: DebitNoteStatus.Issued,
            IsOperatorSpecificManual: false,
            HasPendingDebitNoteAnnulment: false,
            HasFailedDebitNoteAnnulment: true));

        Assert.Equal(OperatorPenaltySituationState.DebitNoteAnnulmentFailed, state);
    }

    [Fact]
    public void DeriveForOperator_IssuedWithoutAnnulment_StillReturnsDone()
    {
        var state = OperatorPenaltySituationRules.DeriveForOperator(new OperatorPenaltySituationRules.LineFields(
            LinePenaltyStatus: PenaltyStatus.Confirmed,
            IsPendingDecision: false,
            BcDebitNoteStatus: DebitNoteStatus.Issued,
            IsOperatorSpecificManual: false));

        Assert.Equal(OperatorPenaltySituationState.Done, state);
    }

    // ============================================================
    // ToOutcome: los dos estados nuevos colapsan a Confirmed (la multa SIGUE confirmada mientras se deshace).
    // ============================================================

    [Theory]
    [InlineData(OperatorPenaltySituationState.DebitNoteAnnulling)]
    [InlineData(OperatorPenaltySituationState.DebitNoteAnnulmentFailed)]
    public void ToOutcome_NewStates_CollapseToConfirmed(OperatorPenaltySituationState state)
    {
        Assert.Equal(OperatorPenaltyOutcome.Confirmed, OperatorPenaltySituationRules.ToOutcome(state));
    }

    // ============================================================
    // OperatorPenaltyUndoRules.ComputeCollectedPenalty (fix bloqueante seguridad: nunca crédito fantasma).
    // ============================================================

    [Theory]
    // bruto <= 0 -> 0
    [InlineData(0, 0, 0)]
    [InlineData(0, 5000, 0)]
    // saldo <= 0 (reserva anulada saldada / con saldo a favor) -> 0 (NO acuñar el bruto fantasma)
    [InlineData(30000, 0, 0)]
    [InlineData(30000, -5000, 0)]
    // saldo >= bruto (multa íntegramente por cobrar) -> 0 (nada cobrado todavía)
    [InlineData(30000, 30000, 0)]
    [InlineData(30000, 40000, 0)]
    // 0 < saldo < bruto (parcial) -> bruto - saldo (lo cobrado)
    [InlineData(30000, 10000, 20000)]
    [InlineData(30000, 29999, 1)]
    [InlineData(30000, 1, 29999)]
    public void ComputeCollectedPenalty_CoversAllBranches(int gross, int balance, int expected)
    {
        Assert.Equal(expected, OperatorPenaltyUndoRules.ComputeCollectedPenalty(gross, balance));
    }
}
