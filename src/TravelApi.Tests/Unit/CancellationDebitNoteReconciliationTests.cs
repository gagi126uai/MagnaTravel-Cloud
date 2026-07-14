using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tests UNIT de <see cref="CancellationDebitNoteReconciliation"/> (bug 2026-07-13): la Nota de Debito
/// (ND) de una anulacion se emite async y nadie transicionaba el <c>DebitNoteStatus</c> de Pending a
/// Issued/Failed cuando llegaba el CAE. Esta clase es la regla compartida que ahora corre ni bien
/// ARCA resuelve la ND (desde <c>AfipService.ProcessInvoiceJob</c>), sin depender de que alguien abra
/// la bandeja.
///
/// <para>Se testea la regla PURA (<see cref="CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote"/>)
/// y el metodo de reconciliacion contra un <see cref="AppDbContext"/> InMemory. Instanciar el
/// <c>AfipService</c> entero es inviable en unit (depende de WSFE/HTTP/certificados); por eso el
/// cableado en el job es minimo y la logica vive aca, testeable.</para>
/// </summary>
public class CancellationDebitNoteReconciliationTests
{
    private static AppDbContext NewDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"nd-reconciliation-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Crea un BookingCancellation minimo con su ND vinculada en el estado indicado.</summary>
    private static BookingCancellation NewCancellation(int debitNoteInvoiceId, DebitNoteStatus status)
        => new()
        {
            Reason = "Cliente anulo",
            DraftedByUserId = "vendedor-1",
            DebitNoteInvoiceId = debitNoteInvoiceId,
            DebitNoteStatus = status,
        };

    private static Invoice ApprovedDebitNote(int id = 500)
        => new()
        {
            Id = id,
            TipoComprobante = 2, // ND sobre Factura A
            Resultado = "A",
            CAE = "70000000000123",
        };

    private static Invoice RejectedDebitNote(string? observaciones, int id = 500)
        => new()
        {
            Id = id,
            TipoComprobante = 2,
            Resultado = "R",
            Observaciones = observaciones,
        };

    // -----------------------------------------------------------------------------------------
    // Regla pura: TryApplyResolvedDebitNote.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public void TryApply_ApprovedWithCae_TransitionsToIssuedAndClearsError()
    {
        var cancellation = NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Pending);
        cancellation.DebitNoteArcaErrorMessage = "un error viejo que ya no aplica";

        bool changed = CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(cancellation, ApprovedDebitNote());

        Assert.True(changed);
        Assert.Equal(DebitNoteStatus.Issued, cancellation.DebitNoteStatus);
        Assert.Null(cancellation.DebitNoteArcaErrorMessage);
    }

    [Fact]
    public void TryApply_Rejected_TransitionsToFailedWithMessage()
    {
        var cancellation = NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Pending);

        bool changed = CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(
            cancellation, RejectedDebitNote("CUIT del receptor invalido"));

        Assert.True(changed);
        Assert.Equal(DebitNoteStatus.Failed, cancellation.DebitNoteStatus);
        Assert.Equal("CUIT del receptor invalido", cancellation.DebitNoteArcaErrorMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryApply_RejectedWithoutUsableMessage_UsesDefaultLiteral(string? observaciones)
    {
        // N2 (mejora deliberada sobre el codigo viejo, que solo cubria null): un Observaciones vacio o
        // en blanco tambien cae al literal por defecto en vez de dejar un mensaje de error vacio.
        var cancellation = NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Pending);

        CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(cancellation, RejectedDebitNote(observaciones));

        Assert.Equal(DebitNoteStatus.Failed, cancellation.DebitNoteStatus);
        Assert.Equal(CancellationDebitNoteReconciliation.ArcaRejectedWithoutMessage, cancellation.DebitNoteArcaErrorMessage);
    }

    [Fact]
    public void TryApply_RejectedWithVeryLongMessage_TruncatesTo1000()
    {
        var cancellation = NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Pending);
        var longMessage = new string('x', 2500);

        CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(cancellation, RejectedDebitNote(longMessage));

        Assert.Equal(CancellationDebitNoteReconciliation.ArcaErrorMaxLength, cancellation.DebitNoteArcaErrorMessage!.Length);
    }

    [Fact]
    public void TryApply_ApprovedButNoCae_StaysPending()
    {
        // "A" sin CAE no es una emision valida: la ND sigue en vuelo, no se transiciona.
        var cancellation = NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Pending);
        var noCaeYet = new Invoice { Id = 500, TipoComprobante = 2, Resultado = "A", CAE = null };

        bool changed = CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(cancellation, noCaeYet);

        Assert.False(changed);
        Assert.Equal(DebitNoteStatus.Pending, cancellation.DebitNoteStatus);
    }

    [Fact]
    public void TryApply_StillPendingResult_DoesNotTouch()
    {
        var cancellation = NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Pending);
        var inFlight = new Invoice { Id = 500, TipoComprobante = 2, Resultado = "PENDING" };

        bool changed = CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(cancellation, inFlight);

        Assert.False(changed);
        Assert.Equal(DebitNoteStatus.Pending, cancellation.DebitNoteStatus);
    }

    [Fact]
    public void TryApply_CancellationNotPending_IsNotOverwritten()
    {
        // Ya resuelta por otra via (Issued): un CAE que vuelve a llegar no la pisa.
        var alreadyIssued = NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Issued);

        bool changed = CancellationDebitNoteReconciliation.TryApplyResolvedDebitNote(alreadyIssued, RejectedDebitNote("rechazo tardio"));

        Assert.False(changed);
        Assert.Equal(DebitNoteStatus.Issued, alreadyIssued.DebitNoteStatus);
        Assert.Null(alreadyIssued.DebitNoteArcaErrorMessage);
    }

    // -----------------------------------------------------------------------------------------
    // Reconciliacion contra el DbContext: ReconcileLinkedCancellationFromDebitNoteAsync.
    // -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Reconcile_ApprovedDebitNote_MovesLinkedCancellationToIssued()
    {
        // Caso 1 del encargo: ND aprobada con BC vinculado en Pending -> Issued + limpia el error.
        await using var db = NewDbContext();
        var debitNote = ApprovedDebitNote();
        db.Invoices.Add(debitNote);
        var cancellation = NewCancellation(debitNote.Id, DebitNoteStatus.Pending);
        cancellation.DebitNoteArcaErrorMessage = "error previo";
        db.BookingCancellations.Add(cancellation);
        await db.SaveChangesAsync();

        int changed = await CancellationDebitNoteReconciliation.ReconcileLinkedCancellationFromDebitNoteAsync(
            db, debitNote, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, changed);
        var reloaded = await db.BookingCancellations.FirstAsync();
        Assert.Equal(DebitNoteStatus.Issued, reloaded.DebitNoteStatus);
        Assert.Null(reloaded.DebitNoteArcaErrorMessage);
    }

    [Fact]
    public async Task Reconcile_RejectedDebitNote_MovesLinkedCancellationToFailed()
    {
        // Caso 2 del encargo: ND rechazada -> Failed con el mensaje.
        await using var db = NewDbContext();
        var debitNote = RejectedDebitNote("Punto de venta invalido");
        db.Invoices.Add(debitNote);
        db.BookingCancellations.Add(NewCancellation(debitNote.Id, DebitNoteStatus.Pending));
        await db.SaveChangesAsync();

        int changed = await CancellationDebitNoteReconciliation.ReconcileLinkedCancellationFromDebitNoteAsync(
            db, debitNote, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(1, changed);
        var reloaded = await db.BookingCancellations.FirstAsync();
        Assert.Equal(DebitNoteStatus.Failed, reloaded.DebitNoteStatus);
        Assert.Equal("Punto de venta invalido", reloaded.DebitNoteArcaErrorMessage);
    }

    [Fact]
    public async Task Reconcile_InvoiceThatIsNotAnyCancellationDebitNote_ChangesNothing()
    {
        // Caso 3 del encargo: el job corre para TODAS las invoices. Una factura de venta cuyo Id no es
        // la ND de ningun BC no debe tocar nada.
        await using var db = NewDbContext();
        var unrelatedInvoice = new Invoice { Id = 999, TipoComprobante = 6, Resultado = "A", CAE = "70000000000999" };
        db.Invoices.Add(unrelatedInvoice);
        // Una anulacion existe, pero su ND es OTRA invoice (500), no la 999.
        db.BookingCancellations.Add(NewCancellation(debitNoteInvoiceId: 500, DebitNoteStatus.Pending));
        await db.SaveChangesAsync();

        int changed = await CancellationDebitNoteReconciliation.ReconcileLinkedCancellationFromDebitNoteAsync(
            db, unrelatedInvoice, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, changed);
        var untouched = await db.BookingCancellations.FirstAsync();
        Assert.Equal(DebitNoteStatus.Pending, untouched.DebitNoteStatus);
    }

    [Fact]
    public async Task Reconcile_LinkedCancellationNotPending_IsNotOverwritten()
    {
        // Caso 4 del encargo: BC vinculado pero ya en ManualReview -> el query lo excluye (solo Pending),
        // no se pisa.
        await using var db = NewDbContext();
        var debitNote = ApprovedDebitNote();
        db.Invoices.Add(debitNote);
        db.BookingCancellations.Add(NewCancellation(debitNote.Id, DebitNoteStatus.ManualReview));
        await db.SaveChangesAsync();

        int changed = await CancellationDebitNoteReconciliation.ReconcileLinkedCancellationFromDebitNoteAsync(
            db, debitNote, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(0, changed);
        var untouched = await db.BookingCancellations.FirstAsync();
        Assert.Equal(DebitNoteStatus.ManualReview, untouched.DebitNoteStatus);
    }
}
