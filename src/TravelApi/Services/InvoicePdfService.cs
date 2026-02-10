using QRCoder;
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

    public byte[] GenerateInvoicePdf(Invoice invoice, TravelFile travelFile, AfipSettings afipSettings, AgencySettings agencySettings)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(header => ComposeHeader(header, invoice, travelFile, afipSettings, agencySettings));
                page.Content().Element(content => ComposeContent(content, invoice, travelFile));
                page.Footer().Element(footer => ComposeFooter(footer, invoice, afipSettings));
            });
        });

        return document.GeneratePdf();
    }

     void ComposeHeader(IContainer container, Invoice invoice, TravelFile travelFile, AfipSettings settings, AgencySettings agencySettings)
    {
        var titleStyle = TextStyle.Default.FontSize(20).SemiBold().FontColor(Colors.Blue.Medium);
        var subTitleStyle = TextStyle.Default.FontSize(10).SemiBold().FontColor(Colors.Grey.Darken2);
        var addressStyle = TextStyle.Default.FontSize(9).FontColor(Colors.Grey.Darken1);
        
        // Priority: Fantasy Name (AgencyName) > Legal Name
        var mainTitle = !string.IsNullOrEmpty(agencySettings.AgencyName) ? agencySettings.AgencyName : agencySettings.LegalName;
        var legalName = agencySettings.LegalName;
        
        container.Row(row =>
        {
            // Left: Agency Identity
            row.RelativeItem().Column(column =>
            {
                // Main Title (Fantasy Name)
                column.Item().Text(mainTitle).Style(titleStyle);
                
                // Legal Name (if different)
                if (!string.IsNullOrEmpty(legalName) && legalName != mainTitle)
                {
                    column.Item().PaddingTop(2).Text(legalName).Style(subTitleStyle);
                }
                
                // Address & contact
                column.Item().PaddingTop(5).Text($"Domicilio: {agencySettings.Address ?? "-"}").Style(addressStyle);
                column.Item().Text($"Condición IVA: {agencySettings.TaxCondition ?? "Responsable Inscripto"}").Style(addressStyle);
                
                if (!string.IsNullOrEmpty(agencySettings.Phone))
                    column.Item().Text($"Tel: {agencySettings.Phone}").Style(addressStyle);
                if (!string.IsNullOrEmpty(agencySettings.Email))
                    column.Item().Text($"Email: {agencySettings.Email}").Style(addressStyle);
            });

            // Center: Letter Box (Standard AFIP Style)
            row.ConstantItem(60).PaddingHorizontal(5).Column(column =>
            {
                 var letter = GetInvoiceLetter(invoice.TipoComprobante);
                 var letterCode = GetInvoiceCode(invoice.TipoComprobante);

                 column.Item()
                    .Border(1)
                    .BorderColor(Colors.Black)
                    .Background(Colors.Grey.Lighten4)
                    .AlignCenter()
                    .Padding(5)
                    .Text(letter)
                    .FontSize(24)
                    .Bold();
                 
                 column.Item().PaddingTop(2).AlignCenter().Text($"COD. {letterCode:00}").FontSize(7).Bold();
            });

            // Right: Invoice Details
            row.RelativeItem().AlignRight().Column(column =>
            {
                column.Item().Text("FACTURA").FontSize(22).Bold().FontColor(Colors.Black);
                
                column.Item().PaddingTop(10).Row(r => 
                {
                    r.RelativeItem().AlignRight().Text("Punto de Venta:").Bold();
                    r.ConstantItem(5).Text("");
                    r.ConstantItem(40).AlignRight().Text($"{invoice.PuntoDeVenta:0000}").Bold();
                });
                
                column.Item().Row(r => 
                {
                    r.RelativeItem().AlignRight().Text("Comp. Nro:").Bold();
                    r.ConstantItem(5).Text("");
                    r.ConstantItem(60).AlignRight().Text($"{invoice.NumeroComprobante:00000000}").Bold();
                });

                column.Item().PaddingTop(5).Text($"Fecha de Emisión: {invoice.CreatedAt:dd/MM/yyyy}");
                column.Item().Text($"CUIT: {settings.Cuit}");
                column.Item().Text($"Ingresos Brutos: {settings.Cuit}"); // Assume IIBB same as CUIT
                
                if (agencySettings.ActivityStartDate.HasValue)
                    column.Item().Text($"Inicio de Actividades: {agencySettings.ActivityStartDate:dd/MM/yyyy}");
            });
        });
        
        // Add a separator line below the header
        container.PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
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
        var json = JsonSerializer.Serialize(qrData);
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var afipUrl = "https://www.afip.gob.ar/fe/qr/?p=" + base64;

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
                try
                {
                    var qrBytes = GenerateQrCodeBytes(afipUrl);
                    c.Item().Height(80).Width(80).Image(qrBytes);
                }
                catch
                {
                    c.Item().AlignCenter().Text("QR ERROR").Bold().FontSize(8).FontColor(Colors.Red.Medium);
                }
                c.Item().AlignCenter().Text("AFIP").Bold().FontSize(8);
            });
        });
    }

    private byte[] GenerateQrCodeBytes(string text)
    {
        using var qrGenerator = new QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        using var qrCode = new PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(20);
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
