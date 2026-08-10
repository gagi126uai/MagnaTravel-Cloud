using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Buscador del catálogo, parte "palabra por palabra" (2026-08-10): tests de
/// <see cref="CatalogSearchTokens"/>, el helper PURO que decide qué palabras se buscan y si cada una
/// aparece en el producto.
///
/// <para>Lo importante que blindan: (1) las palabras de relleno no cuentan; (2) la cuenta de parecido
/// es la MISMA que hace pg_trgm en la base (si se desincronizaran, un producto podría entrar por SQL y
/// quedar descartado en memoria sin ninguna lógica visible); (3) un error de tipeo se perdona solo en
/// palabras largas.</para>
/// </summary>
public class CatalogSearchTokensTests
{
    // ============================= partir la búsqueda en palabras =============================

    [Fact]
    public void Tokenize_SacaLasPalabrasDeRelleno()
    {
        var tokens = CatalogSearchTokens.Tokenize("hotel de las cataratas");

        Assert.Equal(new[] { "hotel", "cataratas" }, tokens);
    }

    [Fact]
    public void Tokenize_DescartaLasPalabrasDeUnaSolaLetra()
    {
        var tokens = CatalogSearchTokens.Tokenize("h maitei");

        Assert.Equal(new[] { "maitei" }, tokens);
    }

    [Fact]
    public void Tokenize_NoRepitePalabras()
    {
        var tokens = CatalogSearchTokens.Tokenize("sheraton sheraton buenos aires");

        Assert.Equal(new[] { "sheraton", "buenos", "aires" }, tokens);
    }

    [Fact]
    public void Tokenize_CortaEnElTopeDeSeisPalabras()
    {
        var tokens = CatalogSearchTokens.Tokenize("uno dos tres cuatro cinco seis siete ocho");

        Assert.Equal(CatalogSearchTokens.MaxTokens, tokens.Count);
        Assert.DoesNotContain("siete", tokens);
    }

    [Fact]
    public void Tokenize_SoloPalabrasDeRelleno_DevuelveVacio()
    {
        Assert.Empty(CatalogSearchTokens.Tokenize("de la"));
        Assert.Empty(CatalogSearchTokens.Tokenize(null));
    }

    // ============================= la cuenta de parecido (igual que pg_trgm) =============================

    [Fact]
    public void TrigramSimilarity_TextosIdenticos_DaUno()
    {
        Assert.Equal(1d, CatalogSearchTokens.TrigramSimilarity("sheraton", "sheraton"), precision: 6);
    }

    [Fact]
    public void TrigramSimilarity_NadaQueVer_DaCero()
    {
        Assert.Equal(0d, CatalogSearchTokens.TrigramSimilarity("maitei", "xyz"), precision: 6);
    }

    /// <summary>
    /// Valor calculado a mano con la fórmula de pg_trgm: "sheratom" y "sheraton" comparten 7 de los
    /// 11 pedacitos de 3 letras que hay entre las dos -> 7/11 = 0.636. Si alguien cambia el armado de
    /// trigramas (el relleno de espacios, por ejemplo), este número se mueve y el test avisa.
    /// </summary>
    [Fact]
    public void TrigramSimilarity_ErrorDeTipeo_DaElMismoNumeroQuePostgres()
    {
        var similarity = CatalogSearchTokens.TrigramSimilarity("sheratom", "sheraton");

        Assert.Equal(7d / 11d, similarity, precision: 6);
    }

    /// <summary>
    /// La medida del texto ENTERO es justamente la que fallaba antes: "sheraton" contra el nombre
    /// largo del hotel da un parecido bajísimo, aunque para una persona sea obviamente el mismo hotel.
    /// Este test documenta POR QUE hubo que buscar palabra por palabra.
    /// </summary>
    [Fact]
    public void TrigramSimilarity_NombreLargo_DiluyeElParecidoDelTextoEntero()
    {
        var similarity = CatalogSearchTokens.TrigramSimilarity(
            "sheraton", "sheraton buenos aires hotel convention center");

        Assert.True(similarity < 0.4d, $"Se esperaba un parecido bajo del texto entero, dio {similarity}");
    }

    // ============================= ¿aparece esta palabra en el producto? =============================

    [Fact]
    public void TokenMatches_PedazoDelTexto_Encuentra()
    {
        Assert.True(CatalogSearchTokens.TokenMatches(
            "sheraton", "sheraton buenos aires hotel convention center"));
    }

    [Fact]
    public void TokenMatches_ConErrorDeTipeo_EncuentraIgual()
    {
        Assert.True(CatalogSearchTokens.TokenMatches(
            "sheratom", "sheraton buenos aires hotel convention center"));
    }

    /// <summary>
    /// Con palabras cortas NO se perdona el tipeo: "bue" se parece demasiado a cualquier cosa y
    /// llenaría la lista de productos que no tienen nada que ver.
    /// </summary>
    [Fact]
    public void TokenMatches_PalabraCortaConTipeo_NoEncuentra()
    {
        Assert.False(CatalogSearchTokens.TokenMatches("bua", "buenos aires"));
    }

    [Fact]
    public void TokenMatches_TextoVacio_NoEncuentra()
    {
        Assert.False(CatalogSearchTokens.TokenMatches("maitei", ""));
        Assert.False(CatalogSearchTokens.TokenMatches("", "maitei posadas"));
    }

    // ============================= cuántas palabras cubre un producto =============================

    [Fact]
    public void MatchedTokenCount_CuentaCadaPalabraUnaSolaVez_AunqueEsteEnVariosTextos()
    {
        var tokens = CatalogSearchTokens.Tokenize("sheraton ola");
        var haystacks = new[] { "sheraton buenos aires", "buenos aires", "ola mayorista", "hotel" };

        Assert.Equal(2, CatalogSearchTokens.MatchedTokenCount(tokens, haystacks));
    }

    [Fact]
    public void MatchedTokenCount_PalabraQueNoEstaEnNingunLado_NoCuenta()
    {
        var tokens = CatalogSearchTokens.Tokenize("sheraton mendoza");
        var haystacks = new[] { "sheraton buenos aires", "buenos aires", "hotel" };

        Assert.Equal(1, CatalogSearchTokens.MatchedTokenCount(tokens, haystacks));
    }

    /// <summary>
    /// La cuenta "tal cual" es la que separa la coincidencia impecable de la que necesitó perdonar un
    /// tipeo — el buscador les pone puntajes distintos a propósito.
    /// </summary>
    [Fact]
    public void ExactMatchedTokenCount_NoPerdonaElTipeo()
    {
        var tokens = CatalogSearchTokens.Tokenize("sheratom");
        var haystacks = new[] { "sheraton buenos aires" };

        Assert.Equal(1, CatalogSearchTokens.MatchedTokenCount(tokens, haystacks));
        Assert.Equal(0, CatalogSearchTokens.ExactMatchedTokenCount(tokens, haystacks));
    }
}
