using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.F2.5 (multimoneda, 2026-05-28): tests UNIT del FORMATO con que la moneda y la
/// cotizacion de la <see cref="Invoice"/> se serializan en el XML SOAP de
/// <c>AfipService.ProcessInvoiceJob</c> (nodos &lt;MonId&gt; / &lt;MonCotiz&gt;).
///
/// <para><b>Por que un test de formato y no de end-to-end</b>: el envelope SOAP completo se
/// arma DENTRO de <c>ProcessInvoiceJob</c>, que ANTES llama a <c>EnsureAuth</c> (necesita un
/// certificado X.509 real + firma CMS) y a <c>GetNextVoucherNumber</c> (otra llamada SOAP a
/// ARCA). Reproducir todo eso en un test exigiria un certificado de prueba y respuestas ARCA
/// hilvanadas — fragil y propenso a falsos rojos. Lo que F2.5 cambia es la construccion del
/// fragmento &lt;MonId&gt;/&lt;MonCotiz&gt;. Este test blinda EXACTAMENTE esa construccion.</para>
///
/// <para><b>Fix M-2 (revision 2026-05-28)</b>: antes este test tenia su PROPIA copia de las dos
/// lineas del SOAP y assertaba contra esa copia — un test tautologico que no protegia contra
/// regresion real (si el codigo de produccion cambiaba, el test seguia verde). Ahora llama al
/// metodo REAL extraido <see cref="AfipService.BuildMonedaSoapFragment"/>, que es el MISMO que
/// usa <c>ProcessInvoiceJob</c> en produccion. Fuente unica: si alguien cambia el formato, este
/// test se pone rojo (en vez de un comprobante rebotado por ARCA en produccion).</para>
/// </summary>
public class AfipServiceMonedaSoapFormatTests
{
    /// <summary>
    /// Fc12NormalInvoice_StillEmitsWithPesos (regresion FC1.2, BYTE-IDENTIDAD): una factura en
    /// pesos (PES/1) serializa EXACTAMENTE como el hardcoded historico "&lt;MonCotiz&gt;1&lt;/MonCotiz&gt;".
    /// Esto es el de-riesgo de homologacion: el path comun FC1.2 (que no esta gateado por el flag)
    /// no cambia un solo byte. Si esto se rompiera, toda la facturacion existente estaria en riesgo.
    /// </summary>
    [Fact]
    public void Fc12NormalInvoice_StillEmitsWithPesos()
    {
        Assert.Equal(
            "<MonId>PES</MonId><MonCotiz>1</MonCotiz>",
            AfipService.BuildMonedaSoapFragment("PES", 1m));
    }

    /// <summary>
    /// Fc12Annulment_StillEmitsWithPesos (regresion NC total FC1.2): la NC total tambien nace
    /// con los defaults PES/1, asi que serializa igual que la factura normal — byte-identico.
    /// </summary>
    [Fact]
    public void Fc12Annulment_StillEmitsWithPesos()
    {
        Assert.Equal(
            "<MonId>PES</MonId><MonCotiz>1</MonCotiz>",
            AfipService.BuildMonedaSoapFragment("PES", 1m));
    }

    /// <summary>
    /// PartialCreditNoteUsd_EmitsWithDolarAndSnapshotRate: una NC parcial en USD (MonId="DOL")
    /// con TC del snapshot (1234.56). Para moneda extranjera se usa el formato de 6 decimales.
    /// </summary>
    [Fact]
    public void PartialCreditNoteUsd_EmitsWithDolarAndSnapshotRate()
    {
        Assert.Equal(
            "<MonId>DOL</MonId><MonCotiz>1234.560000</MonCotiz>",
            AfipService.BuildMonedaSoapFragment("DOL", 1234.56m));
    }

    /// <summary>
    /// PartialCreditNoteArs_EmitsWithPesoAndOne: una NC parcial en ARS usa el mapeo de pesos
    /// (PES/1), byte-identico a cualquier comprobante en pesos.
    /// </summary>
    [Fact]
    public void PartialCreditNoteArs_EmitsWithPesoAndOne()
    {
        Assert.Equal(
            "<MonId>PES</MonId><MonCotiz>1</MonCotiz>",
            AfipService.BuildMonedaSoapFragment("PES", 1m));
    }

    /// <summary>
    /// Pes_UsesNoDecimals_ForByteIdentity: confirma que para PES NO se usa el formato de 6
    /// decimales aunque la cotizacion no sea exactamente 1 (caso defensivo). El "0.######"
    /// recorta ceros: 1 -> "1". Esto garantiza la byte-identidad del path de pesos.
    /// </summary>
    [Fact]
    public void Pes_UsesNoDecimals_ForByteIdentity()
    {
        Assert.Equal(
            "<MonId>PES</MonId><MonCotiz>1</MonCotiz>",
            AfipService.BuildMonedaSoapFragment("PES", 1.000000m));
    }

    // ---------------------------------------------------------------------------------------
    // FC1.3.F2.5 (multimoneda) — CanMisMonExt ("Cancela en Misma Moneda Extranjera",
    // RG ARCA 5616/2024). El nodo solo se emite para moneda extranjera; en pesos NO se emite
    // (string vacio) para que el envelope quede byte-identico al historico.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// CanMisMonExt_Pesos_EmitsNothing (regresion BYTE-IDENTIDAD): un comprobante en pesos
    /// (PES) NO debe emitir el nodo &lt;CanMisMonExt&gt;. El helper devuelve string vacio, asi
    /// el envelope de la facturacion en pesos ya homologada no cambia un solo byte.
    /// </summary>
    [Fact]
    public void CanMisMonExt_Pesos_EmitsNothing()
    {
        Assert.Equal(
            string.Empty,
            AfipService.BuildCanMisMonExtFragment("PES"));
    }

    /// <summary>
    /// CanMisMonExt_Dolar_EmitsN: un comprobante en dolares (DOL) emite &lt;CanMisMonExt&gt;N&lt;/CanMisMonExt&gt;.
    /// MVP: valor fijo "N" porque la agencia factura en USD pero cobra en pesos.
    /// </summary>
    [Fact]
    public void CanMisMonExt_Dolar_EmitsN()
    {
        Assert.Equal(
            "<CanMisMonExt>N</CanMisMonExt>",
            AfipService.BuildCanMisMonExtFragment("DOL"));
    }

    /// <summary>
    /// CanMisMonExt_PesosLowercase_EmitsNothing: la comparacion de "PES" es case-insensitive
    /// (OrdinalIgnoreCase), igual que BuildMonedaSoapFragment. "pes" en minuscula tambien
    /// debe tratarse como pesos y no emitir el nodo.
    /// </summary>
    [Fact]
    public void CanMisMonExt_PesosLowercase_EmitsNothing()
    {
        Assert.Equal(
            string.Empty,
            AfipService.BuildCanMisMonExtFragment("pes"));
    }

    // ---------------------------------------------------------------------------------------
    // Bug critico fiscal (barrido PROD 2026-07-22, hallazgo #1): CbteFch/FchVtoPago se armaban
    // con DateTime.Now (hora del SERVIDOR, UTC en el contenedor de produccion). Una factura
    // emitida ~22hs Argentina salia fechada un dia despues ("23/07" en vez de "22/07"). Estos
    // tests blindan AfipService.BuildComprobanteFechas, el metodo REAL que arma <CbteFch> y
    // <FchVtoPago> en el envelope FECAESolicitar (FC/NC/ND comparten el mismo codigo).
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// CasoDelBugReal: un instante UTC de madrugada (23/07 01:30 UTC) que en Argentina todavia
    /// es la noche del dia anterior (22/07 22:30). CbteFch tiene que salir "20260722", NO
    /// "20260723" — este es exactamente el caso que produjo el hallazgo de PROD.
    /// </summary>
    [Fact]
    public void BuildComprobanteFechas_InstanteDeMadrugadaUtc_CbteFchUsaElDiaArgentino()
    {
        var instanteUtc = new DateTime(2026, 07, 23, 1, 30, 0, DateTimeKind.Utc);

        var (cbteFchDate, cbteFch, _) = AfipService.BuildComprobanteFechas(instanteUtc);

        Assert.Equal("20260722", cbteFch);
        // CbteFchDate (fix B1: lo que se persiste en Invoice.CbteFchArgentina) tiene que ser el
        // MISMO dia que el string, sin componente horario.
        Assert.Equal(new DateTime(2026, 07, 22), cbteFchDate);
    }

    /// <summary>
    /// FchVtoPago (vencimiento de pago) es CbteFch + 10 dias, calculado sobre el MISMO dia
    /// argentino que CbteFch — no sobre el dia UTC del servidor.
    /// </summary>
    [Fact]
    public void BuildComprobanteFechas_InstanteDeMadrugadaUtc_FchVtoPagoSumaDiezDiasAlDiaArgentino()
    {
        var instanteUtc = new DateTime(2026, 07, 23, 1, 30, 0, DateTimeKind.Utc);

        var (_, _, fchVtoPago) = AfipService.BuildComprobanteFechas(instanteUtc);

        // 22/07/2026 + 10 dias = 01/08/2026.
        Assert.Equal("20260801", fchVtoPago);
    }

    /// <summary>
    /// Caso sin cruce de medianoche: un instante de la tarde UTC (22/07 15:00 UTC = 22/07 12:00
    /// ART) da el mismo dia calendario en ambos husos. Confirma que el fix no rompe el caso
    /// comun (la inmensa mayoria de las facturas se emiten en horario habil).
    /// </summary>
    [Fact]
    public void BuildComprobanteFechas_InstanteDeLaTardeUtc_MismoDiaEnAmbosHusos()
    {
        var instanteUtc = new DateTime(2026, 07, 22, 15, 0, 0, DateTimeKind.Utc);

        var (cbteFchDate, cbteFch, fchVtoPago) = AfipService.BuildComprobanteFechas(instanteUtc);

        Assert.Equal(new DateTime(2026, 07, 22), cbteFchDate);
        Assert.Equal("20260722", cbteFch);
        Assert.Equal("20260801", fchVtoPago);
    }
}
