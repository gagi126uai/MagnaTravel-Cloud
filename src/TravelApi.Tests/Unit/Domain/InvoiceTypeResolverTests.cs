using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// Fix fiscal emisor RI->Monotributista (2026-06-13): tests de la matriz emisor x receptor de
/// <see cref="InvoiceTypeResolver"/> (decision A/B/C de la factura de venta) + la leyenda
/// obligatoria de la Ley 27.618.
///
/// <para>Son tests UNIT puros (sin DB, sin ARCA): el resolver es estatico y solo depende de
/// <c>TaxConditionNormalizer</c>. La matriz fue verificada contra ARCA (RG 5003/2021, Ley 27.618)
/// y confirmada por el dueno + contador.</para>
/// </summary>
public class InvoiceTypeResolverTests
{
    // Codigos ARCA: 1 = Factura A, 6 = Factura B, 11 = Factura C.

    [Theory]
    // --- Emisor Responsable Inscripto ---
    [InlineData("Responsable Inscripto", "Responsable Inscripto", 1)] // RI -> RI = A
    [InlineData("Responsable Inscripto", "Monotributo", 1)]           // RI -> Mono = A (FIX del bug; antes daba B)
    [InlineData("Responsable Inscripto", "Consumidor Final", 6)]      // RI -> CF = B
    [InlineData("Responsable Inscripto", "Exento", 6)]                // RI -> Exento = B
    [InlineData("Responsable Inscripto", "Extranjero", 6)]            // RI -> Extranjero = B
    // --- Emisor Monotributo: siempre C, sin importar el receptor ---
    [InlineData("Monotributo", "Responsable Inscripto", 11)]
    [InlineData("Monotributo", "Monotributo", 11)]
    [InlineData("Monotributo", "Consumidor Final", 11)]
    // --- Emisor Exento: siempre C ---
    [InlineData("Exento", "Responsable Inscripto", 11)]
    [InlineData("Exento", "Consumidor Final", 11)]
    public void ResolveSaleInvoiceType_matriz_emisor_receptor(
        string emisor, string receptor, int expectedTipo)
    {
        var actual = InvoiceTypeResolver.ResolveSaleInvoiceType(emisor, receptor);
        Assert.Equal(expectedTipo, actual);
    }

    /// <summary>
    /// EL FIX: emisor RI a receptor Monotributo da Factura A (1). Era el bug a corregir
    /// (antes daba B). Test dedicado para que la regresion sea obvia si alguien lo revierte.
    /// </summary>
    [Fact]
    public void ResolveSaleInvoiceType_RI_a_Monotributo_es_FacturaA()
    {
        var tipo = InvoiceTypeResolver.ResolveSaleInvoiceType("Responsable Inscripto", "Monotributo");
        Assert.Equal(InvoiceTypeResolver.FacturaA, tipo);
    }

    /// <summary>
    /// Robustez: variantes de texto del EMISOR no degradan la letra en silencio. Un emisor
    /// "MONOTRIBUTISTA"/"monotributo"/con tilde sigue dando C (no cae al default B).
    /// </summary>
    [Theory]
    [InlineData("MONOTRIBUTISTA")]
    [InlineData("monotributo")]
    [InlineData("Monotríbutista")]
    public void ResolveSaleInvoiceType_emisor_monotributo_variantes_siguen_dando_C(string emisorVariante)
    {
        var tipo = InvoiceTypeResolver.ResolveSaleInvoiceType(emisorVariante, "Consumidor Final");
        Assert.Equal(InvoiceTypeResolver.FacturaC, tipo);
    }

    /// <summary>
    /// Robustez: variantes de texto del RECEPTOR Monotributo no degradan a B. Emisor RI +
    /// receptor "MONOTRIBUTISTA"/con tilde sigue dando A (el fix no se rompe por el formato).
    /// </summary>
    [Theory]
    [InlineData("MONOTRIBUTISTA")]
    [InlineData("monotributo")]
    [InlineData("Monotríbutista")]
    public void ResolveSaleInvoiceType_RI_a_receptor_monotributo_variantes_siguen_dando_A(string receptorVariante)
    {
        var tipo = InvoiceTypeResolver.ResolveSaleInvoiceType("Responsable Inscripto", receptorVariante);
        Assert.Equal(InvoiceTypeResolver.FacturaA, tipo);
    }

    /// <summary>
    /// Variantes del emisor RI ("IVA_RESP_INSCRIPTO", con tilde) tampoco degradan: RI->RI = A.
    /// </summary>
    [Theory]
    [InlineData("IVA_RESP_INSCRIPTO")]
    [InlineData("Responsablé Inscripto")]
    public void ResolveSaleInvoiceType_emisor_RI_variantes_a_RI_siguen_dando_A(string emisorVariante)
    {
        var tipo = InvoiceTypeResolver.ResolveSaleInvoiceType(emisorVariante, "Responsable Inscripto");
        Assert.Equal(InvoiceTypeResolver.FacturaA, tipo);
    }

    /// <summary>
    /// Emisor desconocido / null (dato corrupto): default conservador B (6). OJO: esto NO es igual
    /// al comportamiento historico (el inline viejo daba C/11 para emisor != "Responsable Inscripto").
    /// B es un default defensivo: no discrimina IVA, asi que no habilita credito fiscal indebido.
    /// La diferencia con el historico solo aplicaria con dato corrupto, caso que hoy no ocurre.
    /// </summary>
    [Theory]
    [InlineData(null, "Responsable Inscripto")]
    [InlineData("", "Consumidor Final")]
    [InlineData("Cualquier Cosa", "Monotributo")]
    public void ResolveSaleInvoiceType_emisor_desconocido_default_B(string? emisor, string receptor)
    {
        var tipo = InvoiceTypeResolver.ResolveSaleInvoiceType(emisor, receptor);
        Assert.Equal(InvoiceTypeResolver.FacturaB, tipo);
    }

    // ============== Leyenda Ley 27.618 ==============

    [Fact]
    public void RequiresMonotributistaLegend_RI_a_Monotributo_es_true()
    {
        Assert.True(InvoiceTypeResolver.RequiresMonotributistaLegend("Responsable Inscripto", "Monotributo"));
    }

    [Theory]
    [InlineData("Responsable Inscripto", "Responsable Inscripto")] // RI->RI (A) no lleva leyenda
    [InlineData("Responsable Inscripto", "Consumidor Final")]      // RI->CF (B) no lleva
    [InlineData("Monotributo", "Monotributo")]                     // Mono emisor (C) no lleva
    [InlineData("Exento", "Monotributo")]                          // Exento emisor (C) no lleva
    public void RequiresMonotributistaLegend_otros_casos_es_false(string emisor, string receptor)
    {
        Assert.False(InvoiceTypeResolver.RequiresMonotributistaLegend(emisor, receptor));
    }

    [Theory]
    // Las variantes de texto tampoco hacen perder la leyenda obligatoria.
    [InlineData("IVA_RESP_INSCRIPTO", "MONOTRIBUTISTA")]
    [InlineData("Responsablé Inscripto", "Monotríbutista")]
    public void RequiresMonotributistaLegend_robusto_a_variantes(string emisor, string receptor)
    {
        Assert.True(InvoiceTypeResolver.RequiresMonotributistaLegend(emisor, receptor));
    }

    /// <summary>
    /// El texto de la leyenda es el literal EXACTO que exige la norma (RG 5003/Ley 27.618).
    /// Si alguien lo edita por error, este test lo detecta.
    /// </summary>
    [Fact]
    public void LeyendaFacturaAMonotributista_texto_exacto_de_la_norma()
    {
        Assert.Equal(
            "El crédito fiscal discriminado en el presente comprobante sólo podrá ser computado a efectos del Régimen de Sostenimiento e Inclusión Fiscal para Pequeños Contribuyentes de la Ley 27.618.",
            InvoiceTypeResolver.LeyendaFacturaAMonotributista);
    }
}
