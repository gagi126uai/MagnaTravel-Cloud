using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Fix fiscal RI->Monotributista (2026-06-13): la leyenda obligatoria de la Ley 27.618 / RG 5003
/// se imprime en el PDF del comprobante (NO en el envelope WSFEv1, donde no existe ese nodo).
///
/// <para>Estos tests ejercitan el camino nuevo de <c>InvoicePdfService.ComposeFiscalLegend</c>:
/// cuando <c>Invoice.FiscalLegend</c> esta seteada el PDF debe generarse sin romper, y cuando es
/// null el layout de las demas facturas no se altera (tampoco rompe). NO verifican el TEXTO
/// renderizado: QuestPDF produce un PDF binario y el proyecto no tiene una libreria de extraccion
/// de texto, agregar una solo para un assert no esta justificado. Lo que SI blindan es que el
/// bloque condicional de la leyenda no introduce una excepcion de layout en ninguno de los dos
/// caminos (con y sin leyenda).</para>
/// </summary>
public class InvoicePdfFiscalLegendTests
{
    // Factura A (RI -> Monotributista): el unico caso que hoy lleva leyenda Ley 27.618.
    [Fact]
    public void GenerateInvoicePdf_ConLeyenda_GeneraPdfNoVacio()
    {
        var service = new InvoicePdfService();
        var invoice = BuildInvoice(fiscalLegend: InvoiceTypeResolver.LeyendaFacturaAMonotributista);

        var pdfBytes = service.GenerateInvoicePdf(
            invoice,
            BuildReserva(),
            BuildAfipSettings(),
            BuildAgencySettings());

        Assert.NotNull(pdfBytes);
        Assert.NotEmpty(pdfBytes);
        AssertIsPdf(pdfBytes);
    }

    // Cualquier otra factura (FiscalLegend null): el PDF se genera igual, sin la leyenda.
    [Fact]
    public void GenerateInvoicePdf_SinLeyenda_GeneraPdfNoVacio()
    {
        var service = new InvoicePdfService();
        var invoice = BuildInvoice(fiscalLegend: null);

        var pdfBytes = service.GenerateInvoicePdf(
            invoice,
            BuildReserva(),
            BuildAfipSettings(),
            BuildAgencySettings());

        Assert.NotNull(pdfBytes);
        Assert.NotEmpty(pdfBytes);
        AssertIsPdf(pdfBytes);
    }

    /// <summary>
    /// Verifica la cabecera magica "%PDF" para confirmar que lo generado es un PDF real y no
    /// un arreglo de bytes cualquiera (cinturon de seguridad barato, sin libreria de parsing).
    /// </summary>
    private static void AssertIsPdf(byte[] bytes)
    {
        Assert.True(bytes.Length > 4, "El PDF deberia tener mas que la cabecera.");
        Assert.Equal((byte)'%', bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
        Assert.Equal((byte)'D', bytes[2]);
        Assert.Equal((byte)'F', bytes[3]);
    }

    private static Invoice BuildInvoice(string? fiscalLegend)
    {
        return new Invoice
        {
            TipoComprobante = 1, // Factura A
            PuntoDeVenta = 1,
            NumeroComprobante = 100,
            ImporteNeto = 1000m,
            ImporteIva = 210m,
            ImporteTotal = 1210m,
            FiscalLegend = fiscalLegend,
            CAE = "75000000000001",
            VencimientoCAE = System.DateTime.UtcNow.AddDays(10),
            Items =
            {
                new InvoiceItem
                {
                    Description = "Servicios turisticos",
                    Quantity = 1,
                    UnitPrice = 1000m,
                    Total = 1000m,
                },
            },
        };
    }

    private static Reserva BuildReserva()
    {
        return new Reserva
        {
            NumeroReserva = "R-0001",
            Payer = new Customer
            {
                FullName = "Cliente Monotributista",
                TaxCondition = "Monotributo",
                TaxId = "20111111112",
            },
        };
    }

    private static AfipSettings BuildAfipSettings()
    {
        return new AfipSettings
        {
            Cuit = 20111111112,
            PuntoDeVenta = 1,
            TaxCondition = "Responsable Inscripto",
            IsProduction = false,
        };
    }

    private static AgencySettings BuildAgencySettings()
    {
        return new AgencySettings
        {
            AgencyName = "Agencia Demo",
            LegalName = "Agencia Demo SRL",
            TaxCondition = "Responsable Inscripto",
        };
    }
}
