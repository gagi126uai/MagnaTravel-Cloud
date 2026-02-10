using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Text;
using System.Text.Json;
using TravelApi.Models;

namespace TravelApi.Services;

public class InvoicePdfService : IInvoicePdfService
{
    public InvoicePdfService()
    {
        // License configuration (Community is free for individuals/small companies)
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GenerateInvoicePdf(Invoice invoice, TravelFile travelFile, AfipSettings settings)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(header => ComposeHeader(header, invoice, travelFile, settings));
                page.Content().Element(content => ComposeContent(content, invoice, travelFile));
                page.Footer().Element(footer => ComposeFooter(footer, invoice, settings));
            });
        });

        return document.GeneratePdf();
    }

    void ComposeHeader(IContainer container, Invoice invoice, TravelFile travelFile, AfipSettings settings)
    {
        var titleStyle = TextStyle.Default.FontSize(20).SemiBold().FontColor(Colors.Blue.Medium);
        var letterStyle = TextStyle.Default.FontSize(24).Bold().FontColor(Colors.Black);
        
        container.Row(row =>
        {
            // Left: Logo & Company Info
            row.RelativeItem().Column(column =>
            {
                column.Item().Text($"MAGNA TRAVEL").Style(titleStyle);
                column.Item().Text("Razón Social: Magna Travel S.A.");
                column.Item().Text("Domicilio: Av. Siempre Viva 123, CABA"); // Customize
                column.Item().Text("Condición IVA: Responsable Inscripto");
            });

            // Center: Letter Box (A/B/C)
            row.ConstantItem(60).Column(column =>
            {
                 var letter = GetInvoiceLetter(invoice.TipoComprobante);
                 var letterCode = GetInvoiceCode(invoice.TipoComprobante);

                 column.Item().Border(1).BorderColor(Colors.Black).AlignCenter().Padding(5).Text(letter).Style(letterStyle);
                 column.Item().AlignCenter().Text($"COD. {letterCode:00}").FontSize(8).Bold();
            });

            // Right: Invoice Details
            row.RelativeItem().AlignRight().Column(column =>
            {
                column.Item().Text("FACTURA").FontSize(18).Bold();
                column.Item().Text($"Punto de Venta: {invoice.PuntoDeVenta:0000}   Comp. Nro: {invoice.NumeroComprobante:00000000}").Bold();
                column.Item().Text($"Fecha de Emisión: {invoice.CreatedAt:dd/MM/yyyy}");
                column.Item().Text($"CUIT: {settings.Cuit}");
                column.Item().Text($"Ingresos Brutos: {settings.Cuit}");
                column.Item().Text($"Inicio de Actividades: 01/01/2020");
            });
        });
    }

    void ComposeContent(IContainer container, Invoice invoice, TravelFile travelFile)
    {
        container.PaddingVertical(10).Column(column =>
        {
            // Customer Section
            column.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"Cliente: {travelFile.Payer?.FullName ?? "Consumidor Final"}").Bold();
                    c.Item().Text($"Domicilio: {travelFile.Payer?.Address ?? "-"}");
                    c.Item().Text($"Condición IVA: {travelFile.Payer?.TaxCondition ?? "Consumidor Final"}");
                });

                row.RelativeItem().AlignRight().Column(c =>
                {
                    c.Item().Text($"CUIT/DNI: {travelFile.Payer?.TaxId ?? travelFile.Payer?.DocumentNumber ?? "-"}");
                    // Payment Condition could be here
                    c.Item().Text("Condición de Venta: Contado");
                });
            });
            
            column.Item().PaddingTop(10).Table(table =>
            {
                // Definition
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(40);
                    columns.RelativeColumn();
                    columns.ConstantColumn(80);
                    columns.ConstantColumn(80);
                    columns.ConstantColumn(80);
                });

                // Header
                table.Header(header =>
                {
                    header.Cell().Element(CellStyle).Text("#");
                    header.Cell().Element(CellStyle).Text("Descripción");
                    header.Cell().Element(CellStyle).AlignRight().Text("Cantidad");
                    header.Cell().Element(CellStyle).AlignRight().Text("Precio Unit.");
                    header.Cell().Element(CellStyle).AlignRight().Text("Subtotal");

                    static IContainer CellStyle(IContainer container) => 
                        container.DefaultTextStyle(x => x.SemiBold()).PaddingVertical(5).BorderBottom(1).BorderColor(Colors.Black);
                });

                // Item (Single item for the whole file for now, or get details if available)
                // Assuming Invoice logic just took the file total. 
                // We'll show "Servicios Turísticos - Exp: {FileNumber}"
                
                table.Cell().Element(CellStyle).Text("1");
                table.Cell().Element(CellStyle).Text($"Servicios Turísticos - Expediente {travelFile.FileNumber} - {travelFile.Name}");
                table.Cell().Element(CellStyle).AlignRight().Text("1");
                                
                // Determine logic for Net vs Total based on Invoice Type
                var isA = invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2; // Factura A/Nota Debito A
                var unitPrice = isA ? invoice.ImporteNeto : invoice.ImporteTotal;
                
                table.Cell().Element(CellStyle).AlignRight().Text($"$ {unitPrice:N2}");
                table.Cell().Element(CellStyle).AlignRight().Text($"$ {unitPrice:N2}");

                static IContainer CellStyle(IContainer container) => 
                    container.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(5);
            });

            // Totals Section
            column.Item().PaddingTop(10).AlignRight().Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("Subtotal Neto:").AlignRight();
                    r.ConstantItem(100).Text($"$ {invoice.ImporteNeto:N2}").AlignRight();
                });
                
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("IVA (21%):").AlignRight();
                    r.ConstantItem(100).Text($"$ {invoice.ImporteIva:N2}").AlignRight();
                });
                
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("Otros Tributos:").AlignRight();
                    r.ConstantItem(100).Text($"$ 0.00").AlignRight();
                });

                c.Item().PaddingTop(5).Row(r =>
                {
                    r.RelativeItem().Text("TOTAL:").Bold().FontSize(14).AlignRight();
                    r.ConstantItem(100).Text($"$ {invoice.ImporteTotal:N2}").Bold().FontSize(14).AlignRight();
                });
            });
        });
    }

    void ComposeFooter(IContainer container, Invoice invoice, AfipSettings settings)
    {
        var qrData = GenerateAfipQrData(invoice, settings);
        var afipUrl = "https://www.afip.gob.ar/fe/qr/?p=" + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(qrData)));

        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Item().Text($"CAE: {invoice.CAE}").Bold();
                column.Item().Text($"Vencimiento CAE: {invoice.VencimientoCAE:dd/MM/yyyy}").Bold();
                column.Item().PaddingTop(5).Text("Comprobante Autorizado").FontSize(8).Italic();
                column.Item().Text("Esta Administración Federal no se responsabiliza por los datos ingresados en el detalle de la operación").FontSize(6).Italic();
            });

            row.ConstantItem(100).AlignRight().Column(c => 
            {
                 // QR Code - Placeholder until QuestPDF syntax is confirmed
                 // c.Item().Height(80).Width(80).Element(e => e.QrCode(afipUrl));
                 c.Item().AlignCenter().Text("QR CODE").Bold().FontSize(8);
                 c.Item().AlignCenter().Text("AFIP").Bold().FontSize(8);
            });
        });
    }

    private string GetInvoiceLetter(int type)
    {
        return type switch
        {
            1 => "A", // Factura A
            6 => "B", // Factura B
            11 => "C", // Factura C (Monotributo)
            _ => "?"
        };
    }
    
    private int GetInvoiceCode(int type) => type;

    private object GenerateAfipQrData(Invoice invoice, AfipSettings settings)
    {
        // AFIP QR JSON Structure v1
        long cuit = settings.Cuit;
        long.TryParse(invoice.CAE, out long cae);
        
        return new
        {
            ver = 1,
            fecha = invoice.CreatedAt.ToString("yyyy-MM-dd"),
            cuit = cuit,
            ptoVta = invoice.PuntoDeVenta,
            tipoCmp = invoice.TipoComprobante,
            nroCmp = invoice.NumeroComprobante,
            importe = invoice.ImporteTotal,
            moneda = "PES",
            ctz = 1,
            tipoDocRec = 80, // CUIT (Update dynamically if needed, 96 for DNI, 99 for Cons Final)
            nroDocRec = 0, // Should be Payer CUIT/DNI. 
            tipoCodAut = "E",
            codAut = cae
        };
    }
}
