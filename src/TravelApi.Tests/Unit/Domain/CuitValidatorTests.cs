using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// Hallazgo H2 (barrido E2E 2026-07-25): el gate que bloquea un CUIT mal tipeado en el alta/edicion
/// de cliente. <see cref="CuitValidator"/> reusa el MISMO algoritmo de digito verificador que ya
/// blindan los tests de <c>ArcaReceptorResolverTests</c> (mismo CUIT de ejemplo, DV modulo 11
/// calculado a mano: 20-12345678-6).
/// </summary>
public class CuitValidatorTests
{
    private const string CuitValidoConGuiones = "20-12345678-6";
    private const string CuitValidoSinFormato = "20123456786";
    private const string CuitDvInvalido = "20-12345678-5";

    [Fact]
    public void IsValidOrEmpty_CuitConDigitoVerificadorCorrecto_True()
    {
        Assert.True(CuitValidator.IsValidOrEmpty(CuitValidoConGuiones));
    }

    [Fact]
    public void IsValidOrEmpty_MismoCuitSinGuionesNiPuntos_True()
    {
        // El vendedor puede tipear el CUIT con o sin guiones; ambos formatos deben validar igual.
        Assert.True(CuitValidator.IsValidOrEmpty(CuitValidoSinFormato));
    }

    [Fact]
    public void IsValidOrEmpty_DigitoVerificadorIncorrecto_False()
    {
        // Mismo numero base que el CUIT valido, pero con el ultimo digito alterado (typo tipico).
        Assert.False(CuitValidator.IsValidOrEmpty(CuitDvInvalido));
    }

    [Theory]
    [InlineData("123")] // muy corto
    [InlineData("201234567860")] // muy largo
    [InlineData("20-ABCDEFGH-6")] // no son todos digitos
    public void IsValidOrEmpty_FormatoMalFormado_False(string cuitMalformado)
    {
        Assert.False(CuitValidator.IsValidOrEmpty(cuitMalformado));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsValidOrEmpty_SinCuitCargado_True(string? sinCuit)
    {
        // No exigimos que el cliente TENGA CUIT (puede ser una persona con solo DNI). El gate solo
        // bloquea un CUIT PRESENTE pero mal tipeado, nunca la ausencia de CUIT.
        Assert.True(CuitValidator.IsValidOrEmpty(sinCuit));
    }
}
