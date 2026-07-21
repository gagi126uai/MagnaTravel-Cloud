using QRCoder;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;

using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class InvoicePdfService : IInvoicePdfService
{
    public InvoicePdfService()
    {
        // License configuration (Community is free for individuals/small companies)
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GenerateInvoicePdf(Invoice invoice, Reserva reserva, AfipSettings afipSettings, AgencySettings agencySettings)
    {
        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1, Unit.Centimetre);
                page.PageColor(Colors.White);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Element(header => ComposeHeader(header, invoice, reserva, afipSettings, agencySettings));
                page.Content().Element(content => ComposeContent(content, invoice, reserva));
                page.Footer().Element(footer => ComposeFooter(footer, invoice, afipSettings));
            });
        });

        return document.GeneratePdf();
    }

     void ComposeHeader(IContainer container, Invoice invoice, Reserva reserva, AfipSettings settings, AgencySettings agencySettings)
    {
        var titleStyle = TextStyle.Default.FontSize(20).SemiBold().FontColor(Colors.Blue.Medium);
        var subTitleStyle = TextStyle.Default.FontSize(10).SemiBold().FontColor(Colors.Grey.Darken2);
        var addressStyle = TextStyle.Default.FontSize(9).FontColor(Colors.Grey.Darken1);
        
        // Priority: Fantasy Name (AgencyName) > Legal Name
        // Try to use Snapshot first
        if (!string.IsNullOrEmpty(invoice.AgencySnapshot))
        {
            try 
            {
                var snapshot = JsonSerializer.Deserialize<AgencySettings>(invoice.AgencySnapshot);
                if (snapshot != null) agencySettings = snapshot;
            }
            catch {}
        }

        var mainTitle = !string.IsNullOrEmpty(agencySettings.AgencyName) ? agencySettings.AgencyName : agencySettings.LegalName;
        var legalName = agencySettings.LegalName;
        
        container.Column(mainColumn =>
        {
            mainColumn.Item().Row(row =>
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
                    var title = GetInvoiceTitle(invoice.TipoComprobante);
                    column.Item().Text(title).FontSize(22).Bold().FontColor(Colors.Black);
                    
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
            mainColumn.Item().PaddingTop(10).LineHorizontal(1).LineColor(Colors.Grey.Lighten2);
        });
    }

    void ComposeContent(IContainer container, Invoice invoice, Reserva reserva)
    {
        // Snapshot Logic for Customer
        var customerName = reserva.Payer?.FullName ?? "Consumidor Final";
        var customerAddress = reserva.Payer?.Address ?? "-";
        var customerTaxCondition = reserva.Payer?.TaxCondition ?? "Consumidor Final";
        var customerDoc = reserva.Payer?.TaxId ?? reserva.Payer?.DocumentNumber ?? "-";
        
        if (!string.IsNullOrEmpty(invoice.CustomerSnapshot))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<Customer>(invoice.CustomerSnapshot);
                if (snapshot != null)
                {
                    customerName = snapshot.FullName;
                    customerAddress = snapshot.Address ?? "-";
                    customerTaxCondition = snapshot.TaxCondition;
                    customerDoc = snapshot.TaxId ?? snapshot.DocumentNumber ?? "-";
                }
            }
            catch {}
        }

        container.PaddingVertical(10).Column(column =>
        {
            // Customer Section
            column.Item().Border(1).BorderColor(Colors.Grey.Lighten2).Padding(5).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text($"Cliente: {customerName}").Bold();
                    c.Item().Text($"Domicilio: {customerAddress}");
                    c.Item().Text($"Condición IVA: {customerTaxCondition}");
                });

                row.RelativeItem().AlignRight().Column(c =>
                {
                    c.Item().Text($"CUIT/DNI: {customerDoc}");
                    c.Item().Text("Condición de Venta: Contado");
                    if (invoice.OriginalInvoiceId.HasValue)
                    {
                        c.Item().Text($"Ref. Orig.: {invoice.OriginalInvoice?.NumeroComprobante:00000000}").Bold();
                    }
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

                // Headers
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

                // Items Loop
                var index = 1;
                foreach (var item in invoice.Items)
                {
                    table.Cell().Element(CellStyle).Text(index.ToString());
                    table.Cell().Element(CellStyle).Text(item.Description);
                    table.Cell().Element(CellStyle).AlignRight().Text(item.Quantity.ToString("0.##"));
                    table.Cell().Element(CellStyle).AlignRight().Text($"$ {CurrencyDisplayFormat.Amount(item.UnitPrice)}");
                    table.Cell().Element(CellStyle).AlignRight().Text($"$ {CurrencyDisplayFormat.Amount(item.Total)}");
                    index++;
                }

                // Fallback for legacy invoices without items
                if (!invoice.Items.Any())
                {
                    table.Cell().Element(CellStyle).Text("1");
                    table.Cell().Element(CellStyle).Text($"Servicios Turísticos - Res {reserva.NumeroReserva}");
                    table.Cell().Element(CellStyle).AlignRight().Text("1");
                    var isA = invoice.TipoComprobante == 1 || invoice.TipoComprobante == 2 || invoice.TipoComprobante == 3;
                    var unitPrice = isA ? invoice.ImporteNeto : invoice.ImporteTotal;
                    table.Cell().Element(CellStyle).AlignRight().Text($"$ {CurrencyDisplayFormat.Amount(unitPrice)}");
                    table.Cell().Element(CellStyle).AlignRight().Text($"$ {CurrencyDisplayFormat.Amount(unitPrice)}");
                }

                static IContainer CellStyle(IContainer container) => 
                    container.BorderBottom(1).BorderColor(Colors.Grey.Lighten2).PaddingVertical(5);
            });

            // Totals Section
            column.Item().PaddingTop(10).AlignRight().Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("Subtotal Neto:").AlignRight();
                    r.ConstantItem(100).Text($"$ {CurrencyDisplayFormat.Amount(invoice.ImporteNeto)}").AlignRight();
                });

                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("IVA:").AlignRight();
                    r.ConstantItem(100).Text($"$ {CurrencyDisplayFormat.Amount(invoice.ImporteIva)}").AlignRight();
                });

                foreach(var t in invoice.Tributes)
                {
                    c.Item().Row(r =>
                    {
                        r.RelativeItem().Text($"{t.Description}:").AlignRight();
                        r.ConstantItem(100).Text($"$ {CurrencyDisplayFormat.Amount(t.Importe)}").AlignRight();
                    });
                }

                c.Item().PaddingTop(5).Row(r =>
                {
                    r.RelativeItem().Text("TOTAL:").Bold().FontSize(14).AlignRight();
                    r.ConstantItem(100).Text($"$ {CurrencyDisplayFormat.Amount(invoice.ImporteTotal)}").Bold().FontSize(14).AlignRight();
                });

                // ADR-012 MVP (facturar en USD, RG ARCA 5616/2024): cuando la factura es en
                // moneda extranjera tenemos que MOSTRAR la moneda, el tipo de cambio y el
                // equivalente en pesos. Sin esto, una factura en dolares "se ve igual" que una
                // en pesos = comprobante fiscalmente incompleto.
                //
                // Por que con Monotributo se factura en USD con la MISMA Factura C: el tipo de
                // comprobante (A/B/C) lo fija la condicion fiscal, NO la moneda. Lo unico que
                // cambia es que el cuerpo debe exhibir TC + conversion a pesos.
                //
                // Para facturas en pesos (MonId == "PES") este bloque NO se ejecuta, asi que el
                // PDF queda byte-identico a hoy.
                ComposeForeignCurrencyDetails(c, invoice);
            });

            // Leyenda fiscal obligatoria al pie del cuerpo del comprobante. Hoy el unico uso es la
            // leyenda de la Ley 27.618 / RG 5003 que lleva una Factura A emitida por un Responsable
            // Inscripto a un Monotributista. Es un requisito del comprobante IMPRESO (NO un dato que
            // ARCA reciba), por eso vive en el PDF y no en el envelope WSFEv1.
            ComposeFiscalLegend(column, invoice);
        });
    }

    /// <summary>
    /// Imprime la leyenda fiscal (Invoice.FiscalLegend) al pie del cuerpo del comprobante, tal cual
    /// fue persistida y sin truncar. Hoy aplica a la Factura A de RI a Monotributista (Ley 27.618 /
    /// RG 5003), que la norma exige en el comprobante impreso. Cuando FiscalLegend es null/vacio no
    /// agrega nada, asi el layout de las demas facturas queda intacto.
    /// </summary>
    private void ComposeFiscalLegend(ColumnDescriptor column, Invoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.FiscalLegend))
            return;

        var legendStyle = TextStyle.Default.FontSize(8).Italic().FontColor(Colors.Grey.Darken3);

        column.Item().PaddingTop(12).Column(legendBlock =>
        {
            legendBlock.Item().PaddingBottom(3).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);
            // Texto EXACTO de la norma: no se trunca ni se reformatea.
            legendBlock.Item().Text(invoice.FiscalLegend).Style(legendStyle);
        });
    }

    /// <summary>
    /// Muestra moneda, tipo de cambio y equivalente en pesos cuando la factura es en moneda
    /// extranjera (MonId distinto de "PES"). Existe para que el comprobante en dolares sea
    /// fiscalmente legible (RG ARCA 5616/2024): el cliente tiene que ver con que TC se valuo y
    /// cuanto representa en pesos. Para facturas en pesos no agrega nada (retorna sin escribir).
    /// </summary>
    private void ComposeForeignCurrencyDetails(ColumnDescriptor column, Invoice invoice)
    {
        // Regla fiscal: solo las facturas en moneda extranjera exhiben el bloque de conversion.
        // Las facturas en pesos (MonId == "PES") salen exactamente como hasta hoy.
        if (string.Equals(invoice.MonId, "PES", StringComparison.OrdinalIgnoreCase))
            return;

        // Cultura es-AR para que los montos usen el formato local (punto de miles, coma decimal).
        var argentinaCulture = CultureInfo.GetCultureInfo("es-AR");

        // Etiqueta legible de la moneda. Hoy el MVP solo habilita dolar ("DOL" en el catalogo
        // de ARCA), pero dejamos un fallback generico para no romper si manana se agrega otra.
        var currencyLabel = GetCurrencyLabel(invoice.MonId);

        // Equivalente en pesos = total en la moneda de la factura * cotizacion del comprobante.
        // No re-calculamos nada fiscal: el ImporteTotal ya esta en la moneda de la factura y el
        // MonCotiz es el TC que se mando a ARCA. Solo lo multiplicamos para mostrarlo.
        var totalInPesos = invoice.ImporteTotal * invoice.MonCotiz;

        var labelStyle = TextStyle.Default.FontSize(9).FontColor(Colors.Grey.Darken2);

        column.Item().PaddingTop(8).Column(currencyBlock =>
        {
            // Separador visual para diferenciar el bloque de moneda extranjera del total.
            currencyBlock.Item().PaddingBottom(3).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten2);

            currencyBlock.Item().Text($"Moneda: {currencyLabel}").Style(labelStyle);

            // TC del comprobante. "por unidad" = cuantos pesos vale una unidad de la moneda.
            currencyBlock.Item().Text(
                $"Tipo de cambio: $ {invoice.MonCotiz.ToString("N2", argentinaCulture)} por {GetCurrencyShortCode(invoice.MonId)}")
                .Style(labelStyle);

            currencyBlock.Item().Text(
                $"Equivalente en pesos: $ {totalInPesos.ToString("N2", argentinaCulture)}")
                .Style(labelStyle);
        });
    }

    /// <summary>
    /// Traduce el codigo de moneda de ARCA a una etiqueta legible para el cliente. Hoy el MVP
    /// solo factura en dolares; el resto cae a un fallback que muestra el codigo crudo para no
    /// ocultar informacion si en el futuro se habilita otra moneda.
    /// </summary>
    private string GetCurrencyLabel(string monId)
    {
        return monId?.ToUpperInvariant() switch
        {
            "DOL" => "Dolar estadounidense (USD)",
            "PES" => "Pesos argentinos (ARS)",
            _ => monId ?? "-"
        };
    }

    /// <summary>
    /// Devuelve el codigo corto/comercial de la moneda para mostrar junto al tipo de cambio
    /// (ej. "USD"). El catalogo de ARCA usa "DOL"; comercialmente se conoce como "USD".
    /// </summary>
    private string GetCurrencyShortCode(string monId)
    {
        return monId?.ToUpperInvariant() switch
        {
            "DOL" => "USD",
            "PES" => "ARS",
            _ => monId ?? "-"
        };
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
            2 => "A", // Nota Debito A
            3 => "A", // Nota Credito A
            6 => "B", // Factura B
            7 => "B", // Nota Debito B
            8 => "B", // Nota Credito B
            11 => "C", // Factura C
            12 => "C", // Nota Debito C
            13 => "C", // Nota Credito C
            51 => "M", // Factura M
            52 => "M", // Nota Debito M
            53 => "M", // Nota Credito M
            _ => "?"
        };
    }

    private string GetInvoiceTitle(int type)
    {
        return type switch
        {
            1 or 6 or 11 or 51 => "FACTURA",
            2 or 7 or 12 or 52 => "NOTA DE DÉBITO",
            3 or 8 or 13 or 53 => "NOTA DE CRÉDITO",
            _ => "COMPROBANTE"
        };
    }
    
    private int GetInvoiceCode(int type) => type;

    private object GenerateAfipQrData(Invoice invoice, AfipSettings settings)
    {
        // AFIP QR JSON Structure v1
        long cuit = settings.Cuit;
        long.TryParse(invoice.CAE, out long cae);

        // Determine Receiver Doc Type from Snapshot or guessing
        int tipoDocRec = 99; // Consumidor Final
        long nroDocRec = 0;

        if (!string.IsNullOrEmpty(invoice.CustomerSnapshot))
        {
             try 
             {
                 var snap = JsonSerializer.Deserialize<Customer>(invoice.CustomerSnapshot);
                 if (snap != null)
                 {
                     if (!string.IsNullOrEmpty(snap.TaxId))
                     {
                         var clean = snap.TaxId.Replace("-", "").Replace(".", "").Trim();
                         if (long.TryParse(clean, out long val)) { tipoDocRec = 80; nroDocRec = val; }
                     }
                     else if (!string.IsNullOrEmpty(snap.DocumentNumber))
                     {
                         if (long.TryParse(snap.DocumentNumber, out long val)) { tipoDocRec = 96; nroDocRec = val; }
                     }
                 }
             }
             catch {}
        }
        
        return new
        {
            ver = 1,
            fecha = invoice.CreatedAt.ToString("yyyy-MM-dd"),
            cuit = cuit,
            ptoVta = invoice.PuntoDeVenta,
            tipoCmp = invoice.TipoComprobante,
            nroCmp = invoice.NumeroComprobante,
            importe = invoice.ImporteTotal,
            // ADR-012 MVP (facturar en USD, RG ARCA 5616/2024): el QR de AFIP debe reflejar la
            // moneda REAL del comprobante, no un "PES" fijo. Antes esto estaba hardcoded en
            // "PES"/1, asi que una factura en dolares mostraba un QR mentiroso (decia pesos).
            //
            // invoice.MonId ya viene en el formato del catalogo de ARCA ("PES" / "DOL"), porque
            // InvoiceService normaliza ISO->ARCA antes de persistir. Para facturas en pesos
            // sigue siendo MonId="PES" y MonCotiz=1, asi que el QR queda byte-identico a hoy.
            moneda = invoice.MonId,
            ctz = invoice.MonCotiz,
            tipoDocRec = tipoDocRec,
            nroDocRec = nroDocRec, 
            tipoCodAut = "E",
            codAut = cae
        };
    }
}
