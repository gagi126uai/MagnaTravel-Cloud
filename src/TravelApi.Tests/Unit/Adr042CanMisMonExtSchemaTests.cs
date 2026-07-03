using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-042 §3.3.3 (2026-07-01): valida ESTATICAMENTE, contra el XSD del WSFEv1 (bajado del WSDL de
/// homologacion, embebido como recurso), que el nodo <c>CanMisMonExt</c> va en el ORDEN correcto dentro de
/// <c>FECAEDetRequest</c> (despues de MonId/MonCotiz, antes de CondicionIVAReceptorId). Un nodo fuera de
/// orden hace REBOTAR el comprobante en ARCA; este test lo detecta en CI, no en un rebote en prod.
/// </summary>
public class Adr042CanMisMonExtSchemaTests
{
    private const string Fev1Ns = "http://ar.gov.afip.dif.FEV1/";
    private const string XsdNs = "http://www.w3.org/2001/XMLSchema";

    private static XmlSchemaSet LoadWsfev1SchemaSet()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("TravelApi.Tests.Resources.wsfev1.xsd")
            ?? throw new InvalidOperationException("No se encontro el recurso embebido TravelApi.Tests.Resources.wsfev1.xsd");
        using var reader = XmlReader.Create(stream);

        var set = new XmlSchemaSet();
        set.Add(Fev1Ns, reader);

        // Wrapper: declara un elemento raiz del tipo FEDetRequest (el contenido de FECAEDetRequest) para poder
        // validar un fragmento suelto contra la SECUENCIA del tipo. Mismo targetNamespace -> se fusiona.
        const string wrapper =
            "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:tns=\"http://ar.gov.afip.dif.FEV1/\" " +
            "targetNamespace=\"http://ar.gov.afip.dif.FEV1/\" elementFormDefault=\"qualified\">" +
            "<xs:element name=\"TestFECAEDetRequest\" type=\"tns:FEDetRequest\" />" +
            "</xs:schema>";
        using var wrapperReader = XmlReader.Create(new StringReader(wrapper));
        set.Add(Fev1Ns, wrapperReader);

        set.Compile();
        return set;
    }

    /// <summary>
    /// Construye un FECAEDetRequest (todos los nodos requeridos + moneda) con CanMisMonExt en la posicion
    /// <paramref name="canMisMonExtBeforeMonId"/>: false = orden del envelope (despues de MonCotiz); true =
    /// fuera de orden (antes de MonId) para el caso negativo.
    /// </summary>
    private static XDocument BuildDetRequest(bool canMisMonExtBeforeMonId)
    {
        XNamespace ns = Fev1Ns;

        // Nodos en el ORDEN del envelope real de AfipService (ver ProcessInvoiceJob).
        var children = new System.Collections.Generic.List<XElement>
        {
            new(ns + "Concepto", 2),
            new(ns + "DocTipo", 80),
            new(ns + "DocNro", 20111111112),
            new(ns + "CbteDesde", 1),
            new(ns + "CbteHasta", 1),
            new(ns + "CbteFch", "20260702"),
            new(ns + "ImpTotal", "100.00"),
            new(ns + "ImpTotConc", "0"),
            new(ns + "ImpNeto", "100.00"),
            new(ns + "ImpOpEx", "0"),
            new(ns + "ImpTrib", "0.00"),
            new(ns + "ImpIVA", "0.00"),
            new(ns + "FchServDesde", "20260702"),
            new(ns + "FchServHasta", "20260702"),
            new(ns + "FchVtoPago", "20260712"),
        };

        if (canMisMonExtBeforeMonId)
        {
            // Caso NEGATIVO: CanMisMonExt ANTES de MonId (fuera de la secuencia del XSD).
            children.Add(new XElement(ns + "CanMisMonExt", "N"));
            children.Add(new XElement(ns + "MonId", "DOL"));
            children.Add(new XElement(ns + "MonCotiz", "1000.000000"));
        }
        else
        {
            // Caso del envelope real: MonId, MonCotiz, CanMisMonExt (en ese orden).
            children.Add(new XElement(ns + "MonId", "DOL"));
            children.Add(new XElement(ns + "MonCotiz", "1000.000000"));
            children.Add(new XElement(ns + "CanMisMonExt", "N"));
        }

        children.Add(new XElement(ns + "CondicionIVAReceptorId", 1));

        return new XDocument(new XElement(ns + "TestFECAEDetRequest", children));
    }

    [Fact]
    public void Envelope_con_CanMisMonExt_despues_de_MonCotiz_valida_contra_el_XSD()
    {
        var set = LoadWsfev1SchemaSet();
        var doc = BuildDetRequest(canMisMonExtBeforeMonId: false);

        var errors = new System.Collections.Generic.List<string>();
        doc.Validate(set, (_, e) => errors.Add(e.Message));

        Assert.True(errors.Count == 0,
            "El envelope con CanMisMonExt despues de MonId/MonCotiz deberia ser valido. Errores: "
            + string.Join(" | ", errors));
    }

    [Fact]
    public void Envelope_con_CanMisMonExt_fuera_de_orden_es_rechazado_por_el_XSD()
    {
        var set = LoadWsfev1SchemaSet();
        var doc = BuildDetRequest(canMisMonExtBeforeMonId: true);

        var errors = new System.Collections.Generic.List<string>();
        doc.Validate(set, (_, e) => errors.Add(e.Message));

        // Guard de regresion: si alguien reordena el envelope y pone CanMisMonExt en el lugar equivocado,
        // el XSD lo rechaza (como haria ARCA). Este test confirma que el XSD DETECTA el desorden.
        Assert.True(errors.Count > 0,
            "CanMisMonExt fuera de orden deberia ser rechazado por el XSD (como lo rechazaria ARCA).");
    }

    [Fact]
    public void El_XSD_declara_CanMisMonExt_entre_MonCotiz_y_CondicionIVAReceptorId()
    {
        // Documenta/bloquea el hecho del esquema: dentro de FEDetRequest, la secuencia es
        // ... MonId, MonCotiz, CanMisMonExt, CondicionIVAReceptorId ...
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("TravelApi.Tests.Resources.wsfev1.xsd")!;
        var xsd = XDocument.Load(stream);
        XNamespace xs = XsdNs;

        var feDetRequest = xsd.Descendants(xs + "complexType")
            .First(ct => (string?)ct.Attribute("name") == "FEDetRequest");

        var elementNames = feDetRequest
            .Descendants(xs + "element")
            .Select(e => (string?)e.Attribute("name"))
            .Where(n => n != null)
            .ToList();

        int monCotiz = elementNames.IndexOf("MonCotiz");
        int canMisMonExt = elementNames.IndexOf("CanMisMonExt");
        int condicionIva = elementNames.IndexOf("CondicionIVAReceptorId");

        Assert.True(monCotiz >= 0 && canMisMonExt >= 0 && condicionIva >= 0);
        Assert.True(monCotiz < canMisMonExt, "CanMisMonExt debe ir DESPUES de MonCotiz en el XSD.");
        Assert.True(canMisMonExt < condicionIva, "CanMisMonExt debe ir ANTES de CondicionIVAReceptorId en el XSD.");
    }
}
