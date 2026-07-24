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

/// <summary>
/// Bug critico fiscal (barrido PROD 2026-07-22/23): el PDF mostraba "Fecha de Emisión" e
/// imprimia el campo "fecha" del QR de ARCA con una fecha que podia NO coincidir con el
/// <c>CbteFch</c> real registrado en ARCA. Hubo DOS hallazgos:
///
/// <list type="bullet">
/// <item>Hallazgo #1 (primera vuelta): <c>invoice.CreatedAt</c> CRUDO (hora UTC del servidor,
/// sin convertir a hora argentina).</item>
/// <item>Hallazgo B1 (revision del reviewer, segunda vuelta — el critico): el camino de
/// recuperacion anti-doble-CAE seteaba <c>IssuedAt</c> con el <c>CbteFch</c> de ARCA parseado
/// como fecha-a-medianoche (Kind=Unspecified) y re-etiquetado Utc. Pasar ESO por
/// <c>ArgentinaTime.ToArgentinaTime</c> (pensado para instantes reales) le restaba 3 horas a una
/// medianoche y corria la fecha un dia para atras — DETERMINISTICO, no un caso borde.</item>
/// </list>
///
/// El fix B1 agrego <see cref="Invoice.CbteFchArgentina"/>: el dia EXACTO que se mando/registro
/// en ARCA, sin pasar nunca por <c>ArgentinaTime</c>. Estos tests blindan
/// <see cref="InvoicePdfService.GetEmissionDateArgentina"/>, que ahora prioriza ese campo y solo
/// cae al calculo historico (con su riesgo residual conocido) para facturas sin la columna.
/// </summary>
public class InvoicePdfEmissionDateArgentinaTests
{
    // ---------------------------------------------------------------------------------------
    // Camino NUEVO (fix B1): CbteFchArgentina seteado. Es el camino que usan TODAS las facturas
    // emitidas/recuperadas DESPUES de este fix, tanto por el POST normal como por recovery.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Regresion B1 EXACTA: CbteFchArgentina con la MISMA forma que produce el camino de
    /// recuperacion anti-doble-CAE (fecha-a-medianoche, Kind=Utc por la relabel obligatoria de
    /// Postgres). Con el codigo VIEJO (que hacia ArgentinaTime.ToArgentinaTime sobre esto) este
    /// test daria 21/07, un dia ANTES del real. Con el fix, CbteFchArgentina se devuelve TAL CUAL
    /// (es una fecha pura, no un instante) y da 22/07 — el dia que ARCA realmente registro.
    /// </summary>
    [Fact]
    public void GetEmissionDateArgentina_ConCbteFchArgentinaFormaMedianocheUtc_DevuelveElDiaExactoSinCorrerlo()
    {
        var invoice = new Invoice
        {
            // Forma exacta que sale de ParseVoucherDetailExtras + SpecifyKind(Utc) en el camino
            // de recovery (AfipService.cs, bloque arcaMatchesOurInvoice).
            CbteFchArgentina = new System.DateTime(2026, 07, 22, 0, 0, 0, System.DateTimeKind.Utc),
            // IssuedAt con OTRO valor a proposito: si el fix no priorizara CbteFchArgentina,
            // este test seguiria en verde por casualidad. Poniendolo distinto garantiza que el
            // assert solo pasa si CbteFchArgentina gano de verdad.
            IssuedAt = new System.DateTime(2026, 07, 20, 5, 0, 0, System.DateTimeKind.Utc),
        };

        var resultado = InvoicePdfService.GetEmissionDateArgentina(invoice);

        Assert.Equal(new System.DateTime(2026, 07, 22), resultado);
    }

    /// <summary>
    /// Consistencia PDF/QR/ARCA en el camino NORMAL (POST directo, sin recovery): el
    /// CbteFchDate que <c>AfipService.BuildComprobanteFechas</c> calcula (y que se manda como
    /// &lt;CbteFch&gt; en el envelope) tiene que ser BYTE A BYTE el mismo valor que
    /// <c>GetEmissionDateArgentina</c> devuelve una vez que se persiste en CbteFchArgentina. Si
    /// alguna de las dos capas cambiara su formula por separado, este test se rompe.
    /// </summary>
    [Fact]
    public void GetEmissionDateArgentina_CaminoNormal_CoincideConElCbteFchQueSeMandoAArca()
    {
        // 23/07 01:30 UTC = 22/07 22:30 ART (el caso del bug original: cruce de medianoche).
        var instanteUtc = new System.DateTime(2026, 07, 23, 1, 30, 0, System.DateTimeKind.Utc);
        var (cbteFchDate, cbteFch, _) = TravelApi.Infrastructure.Services.AfipService.BuildComprobanteFechas(instanteUtc);

        // Simula lo que ProcessInvoiceJob hace en el bloque de exito: persistir cbteFchDate tal
        // cual (Kind=Utc por la relabel obligatoria) en Invoice.CbteFchArgentina.
        var invoice = new Invoice
        {
            CbteFchArgentina = System.DateTime.SpecifyKind(cbteFchDate, System.DateTimeKind.Utc),
        };

        var fechaMostradaEnPdfYQr = InvoicePdfService.GetEmissionDateArgentina(invoice);

        Assert.Equal(cbteFch, fechaMostradaEnPdfYQr.ToString("yyyyMMdd"));
    }

    /// <summary>
    /// Consistencia PDF/QR/ARCA en el camino de RECOVERY (idempotencia anti-doble-CAE): el valor
    /// que ARCA devuelve como CbteFch (ya parseado como fecha-a-medianoche) es EXACTAMENTE lo que
    /// termina en CbteFchArgentina — sin transformaciones intermedias que puedan divergir. Este
    /// es el escenario puntual que motivo el hallazgo B1.
    /// </summary>
    [Fact]
    public void GetEmissionDateArgentina_CaminoRecovery_CoincideConElCbteFchQueArcaTieneRegistrado()
    {
        // Forma que produce ParseVoucherDetailExtras al parsear <CbteFch>"20260722" de la
        // respuesta de ARCA: DateTime.TryParseExact con DateTimeStyles.None -> Kind=Unspecified.
        var cbteFchDeArca = System.DateTime.ParseExact("20260722", "yyyyMMdd", null);

        // Simula lo que el bloque de recovery de AfipService hace: SpecifyKind(Utc) sobre el
        // mismo valor crudo, sin pasarlo por ArgentinaTime.
        var invoice = new Invoice
        {
            CbteFchArgentina = System.DateTime.SpecifyKind(cbteFchDeArca, System.DateTimeKind.Utc),
        };

        var fechaMostradaEnPdfYQr = InvoicePdfService.GetEmissionDateArgentina(invoice);

        Assert.Equal("20260722", fechaMostradaEnPdfYQr.ToString("yyyyMMdd"));
    }

    // ---------------------------------------------------------------------------------------
    // Camino FALLBACK (facturas emitidas ANTES de esta columna, CbteFchArgentina = null, sin
    // backfill posible). Mismo calculo historico de siempre.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Fallback con columna null (factura vieja) + IssuedAt de un instante REAL (no una
    /// medianoche fabricada): reproduce el caso comun del camino normal historico. Confirma que
    /// el fallback sigue funcionando igual que antes del fix B1 para este tipo de dato.
    /// </summary>
    [Fact]
    public void GetEmissionDateArgentina_SinCbteFchArgentina_FacturaVieja_CaeAlCalculoHistoricoConIssuedAtReal()
    {
        var invoice = new Invoice
        {
            CbteFchArgentina = null,
            CreatedAt = new System.DateTime(2026, 07, 20, 10, 0, 0, System.DateTimeKind.Utc),
            IssuedAt = new System.DateTime(2026, 07, 22, 15, 0, 0, System.DateTimeKind.Utc),
        };

        var resultado = InvoicePdfService.GetEmissionDateArgentina(invoice);

        Assert.Equal(new System.DateTime(2026, 07, 22), resultado.Date);
    }

    /// <summary>
    /// Fallback con columna null (factura vieja) + SIN IssuedAt (nunca llego a tener CAE
    /// confirmado, PENDING): cae a CreatedAt para no explotar.
    /// </summary>
    [Fact]
    public void GetEmissionDateArgentina_SinCbteFchArgentinaNiIssuedAt_CaeACreatedAt()
    {
        var invoice = new Invoice
        {
            CbteFchArgentina = null,
            CreatedAt = new System.DateTime(2026, 07, 22, 23, 0, 0, System.DateTimeKind.Utc),
            IssuedAt = null,
        };

        var resultado = InvoicePdfService.GetEmissionDateArgentina(invoice);

        Assert.Equal(new System.DateTime(2026, 07, 22), resultado.Date);
    }

    /// <summary>
    /// Riesgo RESIDUAL documentado (honestidad del rastro, NO es un test de "esto esta bien"):
    /// una factura VIEJA (sin CbteFchArgentina) cuyo IssuedAt fue seteado por el camino de
    /// recovery ANTES de este fix — es decir, ya tiene la forma fecha-a-medianoche contaminada.
    /// El fallback sigue mostrando el dia corrido (21/07 en vez de 22/07) porque no hay forma de
    /// distinguir "una medianoche real" de "una fecha-a-medianoche disfrazada de instante" sin
    /// volver a consultar ARCA. Este test PINEA el limite conocido para que no se lo confunda con
    /// un bug nuevo si alguien lo redescubre en produccion sobre datos historicos.
    /// </summary>
    [Fact]
    public void GetEmissionDateArgentina_FacturaViejaConIssuedAtContaminadoPorRecoveryPrevioAlFix_MantieneElRiesgoResidualConocido()
    {
        var invoice = new Invoice
        {
            CbteFchArgentina = null,
            IssuedAt = new System.DateTime(2026, 07, 22, 0, 0, 0, System.DateTimeKind.Utc),
        };

        var resultado = InvoicePdfService.GetEmissionDateArgentina(invoice);

        // Riesgo conocido: NO es 22/07 (el dia real que ARCA registro), sino 21/07 -- documentado
        // en Invoice.CbteFchArgentina y en el brief del fix B1. Migracion aditiva sin backfill.
        Assert.Equal(new System.DateTime(2026, 07, 21), resultado.Date);
    }
}
