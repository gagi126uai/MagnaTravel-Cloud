using System;
using System.Linq;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bloque 4 "anulación a medias" (2026-08-19): <c>BookingCancellationService.BuildCreditNotesDto</c> mapea
/// cada <see cref="BookingCancellationCreditNote"/> hija a <see cref="TravelApi.Application.DTOs.BookingCancellationCreditNoteDto"/>.
/// Estos tests son PUROS (sin DB): arman las entidades en memoria y verifican el mapeo, en particular el
/// campo nuevo <c>OriginatingInvoicePublicId</c>/<c>OriginatingInvoiceComprobanteLabel</c> que el cartel EN
/// REVISIÓN del front necesita para armar "Factura B 0001-00012345".
/// </summary>
public class BookingCancellationCreditNotesDtoTests
{
    private static BookingCancellationCreditNote BuildChild(
        Invoice originatingInvoice,
        BookingCancellationCreditNoteStatus status,
        Invoice? creditNoteInvoice = null,
        string? arcaErrorMessage = null)
    {
        return new BookingCancellationCreditNote
        {
            OriginatingInvoiceId = originatingInvoice.Id,
            OriginatingInvoice = originatingInvoice,
            Status = status,
            CreditNoteInvoice = creditNoteInvoice,
            ArcaErrorMessage = arcaErrorMessage,
        };
    }

    [Fact]
    public void OriginatingInvoicePublicIdYLabel_SeMapeanDesdeLaFacturaOrigen_NoDesdeLaNC()
    {
        var facturaOrigen = new Invoice { Id = 1, PublicId = Guid.NewGuid(), TipoComprobante = 6, PuntoDeVenta = 1, NumeroComprobante = 12345 };
        var notaDeCredito = new Invoice { Id = 2, PublicId = Guid.NewGuid(), TipoComprobante = 8, PuntoDeVenta = 1, NumeroComprobante = 999 };

        var bc = new BookingCancellation
        {
            CreditNotes = new[]
            {
                BuildChild(facturaOrigen, BookingCancellationCreditNoteStatus.Succeeded, creditNoteInvoice: notaDeCredito),
            },
        };

        var result = BookingCancellationService.BuildCreditNotesDto(bc);

        var dto = Assert.Single(result);
        Assert.Equal(facturaOrigen.PublicId, dto.OriginatingInvoicePublicId);
        Assert.Equal("Factura B 0001-00012345", dto.OriginatingInvoiceComprobanteLabel);
        // El PublicId "de siempre" (PublicId a secas) sigue siendo el de la NC, no el de la factura origen.
        Assert.Equal(notaDeCredito.PublicId, dto.PublicId);
    }

    [Fact]
    public void NotaFallida_ExponeMotivoDeArcaYFacturaOrigen_ParaElCartelEnRevision()
    {
        var facturaOrigen = new Invoice { Id = 3, PublicId = Guid.NewGuid(), TipoComprobante = 6, PuntoDeVenta = 2, NumeroComprobante = 6789 };

        var bc = new BookingCancellation
        {
            CreditNotes = new[]
            {
                BuildChild(
                    facturaOrigen, BookingCancellationCreditNoteStatus.Failed,
                    creditNoteInvoice: null, arcaErrorMessage: "CUIT del emisor sin habilitación para operar"),
            },
        };

        var result = BookingCancellationService.BuildCreditNotesDto(bc);

        var dto = Assert.Single(result);
        Assert.Equal("Factura B 0002-00006789", dto.OriginatingInvoiceComprobanteLabel);
        Assert.Equal(facturaOrigen.PublicId, dto.OriginatingInvoicePublicId);
        Assert.Null(dto.PublicId); // todavia no tiene NC emitida (CreditNoteInvoice null).
        Assert.Equal("CUIT del emisor sin habilitación para operar", dto.ArcaErrorMessage);
    }
}
