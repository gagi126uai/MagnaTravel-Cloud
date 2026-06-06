using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.1 (catalogo find-or-create, 2026-06-05): tests de
/// <see cref="TextNormalizer.NormalizeForCatalog"/>, la funcion AUTORITATIVA que normaliza el nombre
/// del producto tanto para el backfill de la migracion como para la escritura futura de la app.
///
/// <para>Lo que garantizan estos tests: la normalizacion neutraliza los detalles cosmeticos
/// (mayusculas, tildes, espacios de mas, puntuacion repetida) que no deberian partir el mismo producto
/// en dos — PERO no acerca palabras realmente distintas ("Maitey" != "maitei"): eso es trabajo del
/// matching difuso (similarity), no de la igualdad exacta del find-or-create defensivo.</para>
/// </summary>
public class TextNormalizerCatalogTests
{
    [Theory]
    [InlineData("Córdoba", "cordoba")]              // tildes fuera (set español)
    [InlineData("PÓSADAS", "posadas")]              // mayusculas + tilde fuera
    [InlineData("Hotel  Maitei", "hotel maitei")]   // espacios multiples colapsados a uno
    [InlineData("  Hotel Maitei  ", "hotel maitei")] // bordes recortados
    [InlineData("Ñandú", "nandu")]                  // ñ -> n, ú -> u
    public void NormalizeForCatalog_NeutralizesCosmeticDifferences(string raw, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeForCatalog(raw));
    }

    [Theory]
    [InlineData("Hotel--Maitei", "hotel-maitei")]   // guiones repetidos colapsados
    [InlineData("Maitei!!!", "maitei!")]            // signos repetidos colapsados
    [InlineData("San . . Martin", "san . . martin")] // puntuacion separada por espacios NO se colapsa
    public void NormalizeForCatalog_CollapsesRepeatedPunctuation(string raw, string expected)
    {
        Assert.Equal(expected, TextNormalizer.NormalizeForCatalog(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeForCatalog_NullOrBlank_ReturnsEmptyString(string? raw)
    {
        Assert.Equal(string.Empty, TextNormalizer.NormalizeForCatalog(raw));
    }

    /// <summary>
    /// Caso textual del ADR §7 R3: "Maitey" y "maitei" NO son el mismo producto en la igualdad exacta
    /// (difieren en una letra real). El find-or-create defensivo NO los fusiona; el fuzzy del dropdown
    /// los acerca, pero eso es otra capa.
    /// </summary>
    [Fact]
    public void NormalizeForCatalog_DistinctWords_AreNotEqual()
    {
        var maitey = TextNormalizer.NormalizeForCatalog("Maitey");
        var maitei = TextNormalizer.NormalizeForCatalog("maitei");

        Assert.Equal("maitey", maitey);
        Assert.Equal("maitei", maitei);
        Assert.NotEqual(maitey, maitei);
    }

    /// <summary>
    /// Dos formas cosmeticamente distintas del MISMO hotel terminan iguales: es lo que permite que el
    /// anti-duplicados reuse el producto en vez de crear uno nuevo.
    /// </summary>
    [Fact]
    public void NormalizeForCatalog_SameProductDifferentTyping_AreEqual()
    {
        var a = TextNormalizer.NormalizeForCatalog("Hotel  Maitei  Posadas");
        var b = TextNormalizer.NormalizeForCatalog("HOTEL Máitei Pósadas");

        Assert.Equal(a, b);
    }
}
