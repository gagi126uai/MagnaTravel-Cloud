using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// Pieza C "tarifario que se llena solo" (2026-05-30): tests del helper
/// <see cref="TextNormalizer"/>, que prepara texto libre para compararlo sin
/// que acentos, mayusculas o espacios de mas cuenten como diferencia.
///
/// Estos tests corren con InMemory/sin base: <see cref="TextNormalizer"/> es
/// codigo puro de C#, no toca Postgres. La parte difusa (pg_trgm) NO se testea
/// aca porque depende de funciones nativas de Postgres; eso se valida en el VPS.
/// </summary>
public class TextNormalizerTests
{
    [Theory]
    // Acentos: el caso central del feature ("Sheratón" tiene que matchear "sheraton").
    [InlineData("Sheratón", "sheraton")]
    [InlineData("BARILOCHE", "bariloche")]
    [InlineData("Cancún", "cancun")]
    [InlineData("Áéíóú", "aeiou")]
    public void NormalizeForMatch_saca_acentos_y_pasa_a_minuscula(string raw, string expected)
    {
        var actual = TextNormalizer.NormalizeForMatch(raw);
        Assert.Equal(expected, actual);
    }

    [Theory]
    // Mayusculas mezcladas se vuelven todo minuscula.
    [InlineData("HOTEL Plaza", "hotel plaza")]
    [InlineData("hotel plaza", "hotel plaza")]
    [InlineData("HoTeL PlAzA", "hotel plaza")]
    public void NormalizeForMatch_es_case_insensitive(string raw, string expected)
    {
        var actual = TextNormalizer.NormalizeForMatch(raw);
        Assert.Equal(expected, actual);
    }

    [Theory]
    // Espacios multiples (incluyendo tabs) se colapsan a uno solo y se trimean los bordes.
    [InlineData("  buenos   aires  ", "buenos aires")]
    [InlineData("buenos\taires", "buenos aires")]
    [InlineData("buenos \t  aires", "buenos aires")]
    [InlineData("hotel  plaza   san  martin", "hotel plaza san martin")]
    public void NormalizeForMatch_colapsa_espacios_multiples(string raw, string expected)
    {
        var actual = TextNormalizer.NormalizeForMatch(raw);
        Assert.Equal(expected, actual);
    }

    [Theory]
    // Null, vacio o solo-espacios devuelven "" (nunca null), asi el caller compara sin chequear null.
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("  \t  ")]
    public void NormalizeForMatch_null_o_whitespace_devuelve_vacio(string? raw)
    {
        var actual = TextNormalizer.NormalizeForMatch(raw);
        Assert.Equal(string.Empty, actual);
    }

    [Fact]
    public void NormalizeForMatch_combina_acentos_mayusculas_y_espacios()
    {
        // Caso realista: dos cargas distintas del mismo hotel tienen que dar igual.
        var ladoUsuario = TextNormalizer.NormalizeForMatch("  Sheratón   BUENOS Aires ");
        var ladoBase = TextNormalizer.NormalizeForMatch("Sheraton Buenos Aires");

        Assert.Equal("sheraton buenos aires", ladoUsuario);
        Assert.Equal(ladoBase, ladoUsuario);
    }

    [Fact]
    public void NormalizeForMatch_dos_textos_realmente_distintos_no_coinciden()
    {
        // Guardamos que la normalizacion NO aplasta de mas: hoteles distintos
        // siguen siendo distintos despues de normalizar.
        var a = TextNormalizer.NormalizeForMatch("Hotel Plaza");
        var b = TextNormalizer.NormalizeForMatch("Hotel Libertador");

        Assert.NotEqual(a, b);
    }
}
