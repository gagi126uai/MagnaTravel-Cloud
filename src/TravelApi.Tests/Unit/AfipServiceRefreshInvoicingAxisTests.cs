using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-048 T5 fix B1 (2026-07-17, review backend — <c>docs/architecture/2026-07-17-t5-review-backend.md</c>
/// §B1): cubre <see cref="AfipService.RefreshInvoicingAxisForNonCreditNoteAsync"/>, el método que
/// <c>AfipService.ProcessInvoiceJob</c> ahora llama (en los DOS lugares donde una factura de venta o una
/// Nota de Débito quedan <c>Resultado="A"</c>) para que el eje de facturación materializado
/// (<c>Reserva.DerivedInvoicingStatus</c>) nunca quede con el valor viejo.
///
/// <para><b>Por qué InMemory con <c>AfipService</c> real (mismo patrón que
/// <c>AfipServiceCascadeReceiptVoidTests</c>)</b>: <c>ProcessInvoiceJob</c> en sí necesita un SOAP real
/// contra ARCA (este repo no tiene infraestructura para fakear WSFE/WSAA); en cambio, el método que
/// EXTRAJIMOS del bloque post-CAE-aprobado es una unidad chica y testeable sin tocar ARCA — se invoca
/// directo, como si ya hubiéramos llegado al punto donde <c>invoice.Resultado == "A"</c> (el mismo
/// artificio que usan los tests del cascade de Notas de Crédito de este archivo hermano).</para>
/// </summary>
public class AfipServiceRefreshInvoicingAxisTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static AfipService BuildAfipService(AppDbContext context)
        => new(context, NullLogger<AfipService>.Instance, new HttpClient(), new NoopSensitiveDataProtector(), auditService: null);

    /// <summary>
    /// El escenario EXACTO de B1: reserva ya "Facturada total" en los datos (factura con CAE que cubre
    /// toda la venta) pero la columna materializada quedó VIEJA en "NotInvoiced" (simulando el estado
    /// ANTES del fix, cuando nada la refrescaba). Tras llamar al método, la columna queda al día.
    /// </summary>
    [Fact]
    public async Task RefreshInvoicingAxisForNonCreditNoteAsync_ActualizaLaColumnaViejaAFullyInvoiced()
    {
        await using var db = NewContext();
        var reserva = new Reserva
        {
            Name = "R-B1", Status = EstadoReserva.Confirmed, TotalSale = 1000m,
            // Columna vieja: nadie la refrescó todavía (el bug B1 antes del fix).
            DerivedInvoicingStatus = ReservaInvoicingStatus.NotInvoiced,
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            ReservaId = reserva.Id, TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "A",
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var afipService = BuildAfipService(db);
        await afipService.RefreshInvoicingAxisForNonCreditNoteAsync(invoice);

        var reloaded = await db.Reservas.FindAsync(reserva.Id);
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, reloaded!.DerivedInvoicingStatus);
    }

    /// <summary>
    /// Nota de Débito (multa): mismo mecanismo que una factura de venta — no es Nota de Crédito, así que
    /// también pasa por este refresco liviano (el otro camino, antes del fix, que quedaba mudo).
    /// </summary>
    [Fact]
    public async Task RefreshInvoicingAxisForNonCreditNoteAsync_NotaDeDebito_TambienActualizaLaColumna()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R-ND", Status = EstadoReserva.Confirmed, TotalSale = 500m };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var debitNote = new Invoice
        {
            ReservaId = reserva.Id, TipoComprobante = 2, ImporteTotal = 500m, Resultado = "A", // ND A
        };
        db.Invoices.Add(debitNote);
        await db.SaveChangesAsync();

        var afipService = BuildAfipService(db);
        await afipService.RefreshInvoicingAxisForNonCreditNoteAsync(debitNote);

        var reloaded = await db.Reservas.FindAsync(reserva.Id);
        Assert.Equal(ReservaInvoicingStatus.FullyInvoiced, reloaded!.DerivedInvoicingStatus);
    }

    /// <summary>Factura suelta sin reserva asociada (dato legacy): no-op, no revienta.</summary>
    [Fact]
    public async Task RefreshInvoicingAxisForNonCreditNoteAsync_SinReservaAsociada_EsNoOp()
    {
        await using var db = NewContext();
        var invoice = new Invoice { ReservaId = null, TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "A" };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var afipService = BuildAfipService(db);

        // No debe lanzar excepcion.
        await afipService.RefreshInvoicingAxisForNonCreditNoteAsync(invoice);
    }

    /// <summary>Stub minimal para satisfacer el ctor de AfipService en tests. Mismo patron que el resto de los tests de AfipService.</summary>
    private sealed class NoopSensitiveDataProtector : ISensitiveDataProtector
    {
        public string? ProtectString(string? value) => value;
        public string? UnprotectString(string? value) => value;
        public byte[]? ProtectBytes(byte[]? value) => value;
        public byte[]? UnprotectBytes(byte[]? value) => value;
    }
}
