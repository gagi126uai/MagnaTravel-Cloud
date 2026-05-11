using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// B1.15 Fase 2a (review final, 2026-05-11): gating fiscal del endpoint
/// POST /api/invoices/{id}/annul para tipos de comprobante NO soportados por la
/// anulacion automatica.
///
/// EnqueueAnnulmentAsync usa <see cref="InvoiceComprobanteHelpers.IsSupportedForAnnulment"/>
/// (solo 1/6/11) y lanza <see cref="System.InvalidOperationException"/> para tipos
/// como Nota de Debito A/B/C/M (2/7/12/52), Nota de Credito A/B/C/M (3/8/13/53)
/// y Factura M (51). El controller mapea esa excepcion a HTTP 409 Conflict con un
/// body JSON que contiene <c>message</c>.
///
/// Antes del fix, el flujo caia mas adelante en el switch del job con cbteTipo=0,
/// enviaba esa basura a AFIP y el operador veia un 500/error opaco. Este test
/// blinda el camino fail-fast.
///
/// Se usa Admin (default del TestAuthHandler) para bypassar el workflow de
/// ApprovalRequest y aterrizar directo en el guard fiscal.
/// </summary>
public class InvoicesControllerAnnulFiscalTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public InvoicesControllerAnnulFiscalTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<Guid> SeedDebitNoteMInvoiceAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reservaPublicId = Guid.NewGuid();
        var reserva = new Reserva
        {
            PublicId = reservaPublicId,
            Name = "Reserva ND-M " + reservaPublicId.ToString("N")[..6],
            NumeroReserva = "F-INV-ND-M-" + reservaPublicId.ToString("N")[..6],
            ResponsibleUserId = "admin-test",
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var invoicePublicId = Guid.NewGuid();
        var invoice = new Invoice
        {
            PublicId = invoicePublicId,
            ReservaId = reserva.Id,
            TipoComprobante = 52, // Nota de Debito M — no soportado por anulacion automatica
            PuntoDeVenta = 1,
            NumeroComprobante = 200 + DateTime.UtcNow.Millisecond,
            Resultado = "A",
            CAE = "12345678901234",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        return invoice.PublicId;
    }

    [Fact]
    public async Task POST_AnnulInvoice_DebitNoteMTipo52_Returns409WithMessage()
    {
        var invoicePublicId = await SeedDebitNoteMInvoiceAsync();

        var client = _factory.CreateClient();
        // Sin headers: default Admin (TestAuthHandler) → bypass approval workflow,
        // el guard fiscal de IsSupportedForAnnulment es lo primero que aplica
        // despues de la idempotencia.
        var body = new StringContent("{ \"Reason\": \"Test fiscal guard\" }", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync($"/api/invoices/{invoicePublicId}/annul", body);

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var payload = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.TryGetProperty("message", out var messageElement),
            $"Body should expose 'message'. Body was: {payload}");

        var message = messageElement.GetString() ?? string.Empty;
        Assert.Contains("no soporta anulacion", message, StringComparison.OrdinalIgnoreCase);
    }
}
