using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// ADR-024 §12: tests fiscales del receptor (DocTipo + DocNro + CondicionIVAReceptorId). Blindan el
/// MISMO codigo puro que corre en produccion (AfipService.ProcessInvoiceJob llama a ArcaReceptorResolver).
/// No tocan ARCA: solo verifican la decision de los tres numeros fiscales segun la spec.
/// </summary>
public class ArcaReceptorResolverTests
{
    // CUIT valido (DV calculado por modulo 11): 20-12345678-6.
    private const string CuitValido = "20-12345678-6";
    private const long CuitValidoNumero = 20123456786L;
    // Mismo CUIT con DV alterado -> invalido.
    private const string CuitDvInvalido = "20-12345678-5";

    // ============================================================
    // DocTipo
    // ============================================================

    [Fact] // ADR-024 §12: DocTipo_CuitValido_Mapea80
    public void ResolveDocument_CuitValido_Mapea80()
    {
        var result = ArcaReceptorResolver.ResolveDocument(CuitValido, documentType: null, documentNumber: null);

        Assert.Equal(ArcaReceptorResolver.DocTipoCuit, result.DocTipo); // 80
        Assert.Equal(CuitValidoNumero, result.DocNro);
        Assert.False(result.RequiresFiscalData);
    }

    [Fact] // ADR-024 §12: DocTipo_CuitDvInvalido_CaeAFallback
    public void ResolveDocument_CuitDvInvalido_CaeAFallback99()
    {
        var result = ArcaReceptorResolver.ResolveDocument(CuitDvInvalido, documentType: null, documentNumber: null);

        Assert.NotEqual(ArcaReceptorResolver.DocTipoCuit, result.DocTipo); // nunca 80 con DV malo
        Assert.Equal(ArcaReceptorResolver.DocTipoConsumidorFinalSinIdentificar, result.DocTipo); // 99
        Assert.Equal(0, result.DocNro);
        Assert.True(result.RequiresFiscalData); // el caller puede bloquear por tope de monto
    }

    [Fact] // ADR-024 §12: DocTipo_Pasaporte_NoSaleComoDni
    public void ResolveDocument_PasaporteAlfanumerico_NoSaleComoDni()
    {
        var result = ArcaReceptorResolver.ResolveDocument(
            taxId: null, documentType: "Pasaporte", documentNumber: "AB123456");

        // Lo critico: NUNCA 96 (DNI argentino falso). Alfanumerico -> consumidor final sin identificar.
        Assert.NotEqual(ArcaReceptorResolver.DocTipoDni, result.DocTipo);
        Assert.Equal(ArcaReceptorResolver.DocTipoConsumidorFinalSinIdentificar, result.DocTipo); // 99
        Assert.Equal(0, result.DocNro);
        Assert.True(result.RequiresFiscalData);
    }

    [Theory] // ADR-024 §12: DocTipo_PasaporteVariantes_Normaliza
    [InlineData("PASAPORTE")]
    [InlineData("pasaporte")]
    [InlineData("Pasaporte")]
    [InlineData(" Pasaporte ")]
    public void ResolveDocument_PasaporteVariantes_MismoTipo(string documentType)
    {
        var result = ArcaReceptorResolver.ResolveDocument(
            taxId: null, documentType: documentType, documentNumber: "AB123456");

        // Todas las variantes normalizan al mismo resultado (alfanumerico -> 99).
        Assert.Equal(ArcaReceptorResolver.DocTipoConsumidorFinalSinIdentificar, result.DocTipo);
    }

    [Fact]
    public void ResolveDocument_PasaporteNumerico_ConservaTipo94()
    {
        // Un pasaporte con numero puramente numerico SI entra como long -> conserva DocTipo 94.
        var result = ArcaReceptorResolver.ResolveDocument(
            taxId: null, documentType: "Pasaporte", documentNumber: "123456789");

        Assert.Equal(ArcaReceptorResolver.DocTipoPasaporte, result.DocTipo); // 94
        Assert.Equal(123456789L, result.DocNro);
    }

    [Fact] // ADR-024 §12: DocTipo_DniNumerico_Mapea96
    public void ResolveDocument_DniNumerico_Mapea96()
    {
        var result = ArcaReceptorResolver.ResolveDocument(
            taxId: null, documentType: "DNI", documentNumber: "30123456");

        Assert.Equal(ArcaReceptorResolver.DocTipoDni, result.DocTipo); // 96
        Assert.Equal(30123456L, result.DocNro);
        Assert.False(result.RequiresFiscalData);
    }

    [Fact] // ADR-024 §12: DocTipo_SinTipoConNumero_DefaultDni (ASUNCION confirmar con contador)
    public void ResolveDocument_SinTipoConNumero_DefaultDni()
    {
        var result = ArcaReceptorResolver.ResolveDocument(
            taxId: null, documentType: null, documentNumber: "30123456");

        Assert.Equal(ArcaReceptorResolver.DocTipoDni, result.DocTipo); // 96 (regla C)
        Assert.Equal(30123456L, result.DocNro);
    }

    [Fact] // ADR-024 §12: DocTipo_SinDato_ConsumidorFinal
    public void ResolveDocument_SinDato_ConsumidorFinal()
    {
        var result = ArcaReceptorResolver.ResolveDocument(taxId: null, documentType: null, documentNumber: null);

        Assert.Equal(ArcaReceptorResolver.DocTipoConsumidorFinalSinIdentificar, result.DocTipo); // 99
        Assert.Equal(0, result.DocNro);
        Assert.False(result.RequiresFiscalData); // sin dato no es un error, es venta a consumidor final
    }

    [Fact] // ADR-024 §12: DocTipo_DocumentTypeDesconocido_Fallback99
    public void ResolveDocument_DocumentTypeDesconocido_Fallback99()
    {
        var result = ArcaReceptorResolver.ResolveDocument(
            taxId: null, documentType: "Carnet", documentNumber: "12345");

        Assert.Equal(ArcaReceptorResolver.DocTipoConsumidorFinalSinIdentificar, result.DocTipo); // 99
        Assert.Equal(0, result.DocNro);
        Assert.True(result.RequiresFiscalData);
    }

    [Fact]
    public void ResolveDocument_CuitGanaSobreOtroDocumento()
    {
        // ADR-024 §3.4 regla A: si hay CUIT valido, gana aunque haya un pasaporte cargado.
        var result = ArcaReceptorResolver.ResolveDocument(
            taxId: CuitValido, documentType: "Pasaporte", documentNumber: "AB999");

        Assert.Equal(ArcaReceptorResolver.DocTipoCuit, result.DocTipo); // 80
        Assert.Equal(CuitValidoNumero, result.DocNro);
    }

    // ============================================================
    // CondicionIVAReceptorId
    // ============================================================

    [Fact] // ADR-024 §12: CondicionIva_TaxConditionId1_Emite1
    public void ResolveCondicionIva_TaxConditionId1_Emite1()
    {
        var result = ArcaReceptorResolver.ResolveCondicionIva(
            taxConditionId: 1, taxConditionText: null, docTipo: ArcaReceptorResolver.DocTipoCuit);

        Assert.Equal(ArcaReceptorResolver.CondicionIvaResponsableInscripto, result); // 1, no 5
    }

    [Fact] // ADR-024 §12: CondicionIva_TaxConditionId6_Emite6
    public void ResolveCondicionIva_TaxConditionId6_Emite6()
    {
        var result = ArcaReceptorResolver.ResolveCondicionIva(
            taxConditionId: 6, taxConditionText: null, docTipo: ArcaReceptorResolver.DocTipoCuit);

        Assert.Equal(ArcaReceptorResolver.CondicionIvaMonotributo, result); // 6
    }

    [Fact] // ADR-024 §12: CondicionIva_SnapshotViejoSinId_ParseaTexto
    public void ResolveCondicionIva_SnapshotViejoSinId_ParseaTexto()
    {
        var result = ArcaReceptorResolver.ResolveCondicionIva(
            taxConditionId: null, taxConditionText: "Responsable Inscripto", docTipo: ArcaReceptorResolver.DocTipoCuit);

        Assert.Equal(ArcaReceptorResolver.CondicionIvaResponsableInscripto, result); // 1 (parseo texto)
    }

    [Fact]
    public void ResolveCondicionIva_TextoMonotributo_Emite6()
    {
        var result = ArcaReceptorResolver.ResolveCondicionIva(
            taxConditionId: null, taxConditionText: "Monotributo", docTipo: ArcaReceptorResolver.DocTipoCuit);

        Assert.Equal(ArcaReceptorResolver.CondicionIvaMonotributo, result);
    }

    [Fact] // ADR-024 §12: CondicionIva_CuitSinCondicion_Default5 (ASUNCION confirmar con contador)
    public void ResolveCondicionIva_CuitSinCondicion_Default5()
    {
        var result = ArcaReceptorResolver.ResolveCondicionIva(
            taxConditionId: null, taxConditionText: null, docTipo: ArcaReceptorResolver.DocTipoCuit);

        Assert.Equal(ArcaReceptorResolver.CondicionIvaConsumidorFinal, result); // 5 conservador
    }

    [Fact]
    public void ResolveCondicionIva_PersonaFisicaSinCondicion_Default5()
    {
        // DNI/Pasaporte sin condicion -> Consumidor Final (persona fisica).
        var result = ArcaReceptorResolver.ResolveCondicionIva(
            taxConditionId: null, taxConditionText: null, docTipo: ArcaReceptorResolver.DocTipoDni);

        Assert.Equal(ArcaReceptorResolver.CondicionIvaConsumidorFinal, result);
    }

    [Fact]
    public void ResolveCondicionIva_IdFueraDeTabla_CaeAParseoODerivacion()
    {
        // Un TaxConditionId fuera de la tabla §4.1 (ej. 99) NO se emite tal cual: se ignora y se deriva.
        var result = ArcaReceptorResolver.ResolveCondicionIva(
            taxConditionId: 99, taxConditionText: null, docTipo: ArcaReceptorResolver.DocTipoDni);

        Assert.Equal(ArcaReceptorResolver.CondicionIvaConsumidorFinal, result); // deriva, no emite 99
    }

    [Theory] // ADR-024 §12: CondicionIva_NuncaVacio / CondicionIva_ValidaParaClaseC
    // Cualquier combinacion de entrada produce un codigo que pertenece a la tabla §4.1.
    [InlineData(1, null, 80)]
    [InlineData(6, null, 80)]
    [InlineData(null, "Responsable Inscripto", 80)]
    [InlineData(null, "Exento", 80)]
    [InlineData(null, null, 80)]
    [InlineData(null, null, 96)]
    [InlineData(null, null, 99)]
    [InlineData(999, "texto raro", 99)]
    public void ResolveCondicionIva_SiempreCodigoValido(int? taxConditionId, string? taxConditionText, int docTipo)
    {
        var result = ArcaReceptorResolver.ResolveCondicionIva(taxConditionId, taxConditionText, docTipo);

        Assert.True(ArcaReceptorResolver.IsValidCondicionIvaCode(result),
            $"El codigo {result} no pertenece a la tabla CondicionIVAReceptorId (RG 5616).");
    }

    // ============================================================
    // CUIT DV (modulo 11)
    // ============================================================

    [Fact]
    public void IsValidCuit_DvCorrecto_True()
    {
        Assert.True(ArcaReceptorResolver.IsValidCuit("20123456786"));
    }

    [Fact]
    public void IsValidCuit_DvIncorrecto_False()
    {
        Assert.False(ArcaReceptorResolver.IsValidCuit("20123456785"));
    }

    [Theory]
    [InlineData("2012345678")]   // 10 digitos
    [InlineData("201234567866")] // 12 digitos
    [InlineData("2012345678A")]  // no numerico
    [InlineData("")]
    public void IsValidCuit_FormatoMalo_False(string cuit)
    {
        Assert.False(ArcaReceptorResolver.IsValidCuit(cuit));
    }
}
