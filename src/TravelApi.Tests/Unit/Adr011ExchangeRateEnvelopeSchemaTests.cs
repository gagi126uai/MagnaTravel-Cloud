using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): valida ESTATICAMENTE, contra el XSD del
/// WSFEv1 embebido como recurso, que el envelope de <c>FEParamGetCotizacion</c> que arma
/// <c>AfipService.GetOfficialExchangeRateAsync</c> respeta la forma que declara el schema
/// (<c>Auth</c>, <c>MonId</c>, <c>FchCotiz</c>, en ese orden). Mismo patron que
/// <see cref="Adr042CanMisMonExtSchemaTests"/>: no le pegamos a ARCA en un test unitario, pero el
/// XSD nos dice si el envelope rebotaria antes de probarlo en homologacion.
/// </summary>
public class Adr011ExchangeRateEnvelopeSchemaTests
{
    private const string Fev1Ns = "http://ar.gov.afip.dif.FEV1/";

    private static XmlSchemaSet LoadWsfev1SchemaSet()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("TravelApi.Tests.Resources.wsfev1.xsd")
            ?? throw new InvalidOperationException("No se encontro el recurso embebido TravelApi.Tests.Resources.wsfev1.xsd");
        using var reader = XmlReader.Create(stream);

        var set = new XmlSchemaSet();
        set.Add(Fev1Ns, reader);
        set.Compile();
        return set;
    }

    /// <summary>
    /// Arma el request de <c>FEParamGetCotizacion</c> en el ORDEN que usa el envelope real de
    /// <c>AfipService.GetOfficialExchangeRateAsync</c>: Auth, MonId, FchCotiz.
    /// </summary>
    private static XDocument BuildRequest(bool omitirAuth = false)
    {
        XNamespace ns = Fev1Ns;

        var children = new List<XElement>();
        if (!omitirAuth)
        {
            children.Add(new XElement(ns + "Auth",
                new XElement(ns + "Token", "token-de-prueba"),
                new XElement(ns + "Sign", "sign-de-prueba"),
                new XElement(ns + "Cuit", 20111111112)));
        }
        children.Add(new XElement(ns + "MonId", "DOL"));
        children.Add(new XElement(ns + "FchCotiz", "20260805"));

        return new XDocument(new XElement(ns + "FEParamGetCotizacion", children));
    }

    [Fact]
    public void Envelope_con_Auth_MonId_FchCotiz_valida_contra_el_XSD()
    {
        var set = LoadWsfev1SchemaSet();
        var doc = BuildRequest();

        var errors = new List<string>();
        doc.Validate(set, (_, e) => errors.Add(e.Message));

        Assert.True(errors.Count == 0,
            "El envelope de FEParamGetCotizacion (Auth, MonId, FchCotiz) deberia ser valido. Errores: "
            + string.Join(" | ", errors));
    }

    [Fact]
    public void El_XSD_declara_FEParamGetCotizacion_con_Auth_MonId_FchCotiz_en_ese_orden()
    {
        // Documenta/bloquea el hecho del esquema: si alguien reordena el envelope en AfipService,
        // este test explica CONTRA QUE orden hay que validar.
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("TravelApi.Tests.Resources.wsfev1.xsd")!;
        var xsd = XDocument.Load(stream);
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";

        var element = xsd.Descendants(xs + "element")
            .First(e => (string?)e.Attribute("name") == "FEParamGetCotizacion");

        var elementNames = element
            .Descendants(xs + "element")
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => n != null)
            .ToList();

        Assert.Equal(new[] { "Auth", "MonId", "FchCotiz" }, elementNames);
    }

    // FIX (detalle #8, revision post-implementacion 2026-08-05): el nombre anterior
    // ("...es_rechazado_por_el_XSD...") decia LO CONTRARIO de lo que el test verifica
    // (Assert.True(errors.Count == 0) = el XSD NO rechaza este caso). Renombrado para que el
    // nombre describa el comportamiento real: el XSD TOLERA la ausencia de Auth (es
    // minOccurs="0"), aunque ARCA en la practica SI la rechazaria por falta de autenticacion.
    [Fact]
    public void Envelope_sin_Auth_esValidoParaElXsd_AunqueArcaLoRechazaríaEnLaPractica()
    {
        // Guard de regresion: Auth es minOccurs="0" en el XSD (por eso NO tira si falta), pero si
        // faltara Auth y el primer nodo fuera MonId, sigue siendo valido igual (la secuencia con
        // minOccurs=0 permite saltearlo). Este test documenta ese matiz para que nadie interprete
        // "compila sin Auth" como "Auth es opcional en la practica" — ARCA rechaza sin Auth aunque
        // el XSD lo tolere.
        var set = LoadWsfev1SchemaSet();
        var doc = BuildRequest(omitirAuth: true);

        var errors = new List<string>();
        doc.Validate(set, (_, e) => errors.Add(e.Message));

        Assert.True(errors.Count == 0,
            "El XSD declara Auth como minOccurs=0: el documento sigue siendo valido sin el, aunque " +
            "ARCA lo rechazaria en la practica por falta de autenticacion.");
    }
}
