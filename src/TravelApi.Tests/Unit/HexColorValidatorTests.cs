using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "PDF de presupuesto" (2026-08-11/12): tests del validador del color de la banda del PDF.
/// </summary>
public class HexColorValidatorTests
{
    [Theory]
    [InlineData("#1E40AF")]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidOrEmpty_AcceptsValidHexOrEmpty(string? value)
    {
        Assert.True(HexColorValidator.IsValidOrEmpty(value));
    }

    [Theory]
    [InlineData("1E40AF")] // falta el numeral
    [InlineData("#1E40A")] // le falta un digito
    [InlineData("#1E40AFF")] // le sobra un digito
    [InlineData("#GGGGGG")] // no son digitos hexadecimales
    [InlineData("azul")]
    [InlineData("#123")] // forma corta no soportada
    public void IsValidOrEmpty_RejectsInvalidHex(string value)
    {
        Assert.False(HexColorValidator.IsValidOrEmpty(value));
    }

    [Fact]
    public void InvalidHexColorMessage_IsCriollo()
    {
        Assert.Equal("El color tiene que ser un código hexadecimal válido, por ejemplo #1E40AF.", HexColorValidator.InvalidHexColorMessage);
    }
}
