using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// FC1.2.0 v3 (2026-05-17): tests del helper <see cref="TaxConditionNormalizer"/>.
/// Cubre la matriz documentada en el plan + edge cases (null/empty/case
/// insensitive/garbage) + round-trip storage.
/// </summary>
public class TaxConditionNormalizerTests
{
    // ============== Normalize ==============

    [Theory]
    // Variantes Monotributista.
    [InlineData("MONOTRIBUTISTA", TaxConditionCanonical.Monotributista)]
    [InlineData("Monotributo", TaxConditionCanonical.Monotributista)]
    [InlineData("monotributo", TaxConditionCanonical.Monotributista)]
    [InlineData("MONOTRIBUTO", TaxConditionCanonical.Monotributista)]
    [InlineData("MonoTRIBUTISTA", TaxConditionCanonical.Monotributista)]

    // Variantes Responsable Inscripto.
    [InlineData("IVA_RESP_INSCRIPTO", TaxConditionCanonical.ResponsableInscripto)]
    [InlineData("Responsable Inscripto", TaxConditionCanonical.ResponsableInscripto)]
    [InlineData("RESPONSABLE INSCRIPTO", TaxConditionCanonical.ResponsableInscripto)]
    [InlineData("iva_resp_inscripto", TaxConditionCanonical.ResponsableInscripto)]

    // Variantes Exento.
    [InlineData("IVA_EXENTO", TaxConditionCanonical.Exento)]
    [InlineData("Exento", TaxConditionCanonical.Exento)]
    [InlineData("EXENTO", TaxConditionCanonical.Exento)]
    [InlineData("iva exento", TaxConditionCanonical.Exento)]

    // Variantes Consumidor Final.
    [InlineData("Consumidor Final", TaxConditionCanonical.ConsumidorFinal)]
    [InlineData("CONSUMIDOR_FINAL", TaxConditionCanonical.ConsumidorFinal)]
    [InlineData("consumidor final", TaxConditionCanonical.ConsumidorFinal)]

    // Variantes Extranjero (preparado para FC4).
    [InlineData("Extranjero", TaxConditionCanonical.Extranjero)]
    [InlineData("foreign", TaxConditionCanonical.Extranjero)]
    public void Normalize_variante_conocida_retorna_canonical(string raw, TaxConditionCanonical expected)
    {
        var actual = TaxConditionNormalizer.Normalize(raw);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Normalize_null_o_whitespace_retorna_unknown(string? raw)
    {
        var actual = TaxConditionNormalizer.Normalize(raw);
        Assert.Equal(TaxConditionCanonical.Unknown, actual);
    }

    [Theory]
    [InlineData("ABC123")]
    [InlineData("Sin Categoria")]
    [InlineData("Monotri")]            // Substring incompleto: no debe matchear.
    [InlineData("IVA Inscripto")]      // Sin "RESP": no debe matchear el bucket RI.
    [InlineData("Consumidor")]         // Sin "Final".
    [InlineData("123")]
    public void Normalize_garbage_retorna_unknown(string raw)
    {
        var actual = TaxConditionNormalizer.Normalize(raw);
        Assert.Equal(TaxConditionCanonical.Unknown, actual);
    }

    [Fact]
    public void Normalize_con_espacios_alrededor_se_trimea()
    {
        Assert.Equal(TaxConditionCanonical.Monotributista, TaxConditionNormalizer.Normalize("  Monotributo  "));
        Assert.Equal(TaxConditionCanonical.ResponsableInscripto, TaxConditionNormalizer.Normalize("\tResponsable Inscripto\n"));
    }

    // ============== MR-03: variantes con tildes ==============
    //
    // Hay registros legacy cargados desde Word/Excel donde el campo TaxCondition
    // viene con tildes. Antes del MR-03, esos casos caian en Unknown y T0
    // fallaba sin razon de negocio. Ahora deben mapear al mismo canonical que
    // la version sin tilde.

    [Fact]
    public void Normalize_con_tilde_monotributista_retorna_monotributista()
    {
        var actual = TaxConditionNormalizer.Normalize("Monotríbutista");
        Assert.Equal(TaxConditionCanonical.Monotributista, actual);
    }

    [Fact]
    public void Normalize_con_tilde_responsable_inscripto_retorna_responsable_inscripto()
    {
        var actual = TaxConditionNormalizer.Normalize("Responsablé Inscripto");
        Assert.Equal(TaxConditionCanonical.ResponsableInscripto, actual);
    }

    [Fact]
    public void Normalize_con_tilde_y_mayusculas_mixtas_retorna_canonical()
    {
        var actual = TaxConditionNormalizer.Normalize("MoNotríBUTISTA");
        Assert.Equal(TaxConditionCanonical.Monotributista, actual);
    }

    [Theory]
    [InlineData("Exénto")]     // tilde sobre la 'e' del medio
    [InlineData("Éxento")]     // tilde sobre la 'E' mayuscula inicial
    [InlineData("éXENTO")]     // tilde sobre la 'e' minuscula inicial, resto mayuscula
    public void Normalize_con_tilde_exento_retorna_exento(string raw)
    {
        var actual = TaxConditionNormalizer.Normalize(raw);
        Assert.Equal(TaxConditionCanonical.Exento, actual);
    }

    // ============== ToStorageString ==============

    [Theory]
    [InlineData(TaxConditionCanonical.Monotributista, "MONOTRIBUTISTA")]
    [InlineData(TaxConditionCanonical.ResponsableInscripto, "RESPONSABLE_INSCRIPTO")]
    [InlineData(TaxConditionCanonical.Exento, "EXENTO")]
    [InlineData(TaxConditionCanonical.ConsumidorFinal, "CONSUMIDOR_FINAL")]
    [InlineData(TaxConditionCanonical.Extranjero, "EXTRANJERO")]
    [InlineData(TaxConditionCanonical.Unknown, "UNKNOWN")]
    public void ToStorageString_devuelve_formato_canonical(TaxConditionCanonical canonical, string expected)
    {
        var actual = TaxConditionNormalizer.ToStorageString(canonical);
        Assert.Equal(expected, actual);
    }

    // ============== Round-trip ==============

    [Theory]
    [InlineData(TaxConditionCanonical.Monotributista)]
    [InlineData(TaxConditionCanonical.ResponsableInscripto)]
    [InlineData(TaxConditionCanonical.Exento)]
    [InlineData(TaxConditionCanonical.ConsumidorFinal)]
    [InlineData(TaxConditionCanonical.Extranjero)]
    public void RoundTrip_canonical_to_storage_to_canonical_es_estable(TaxConditionCanonical original)
    {
        var storage = TaxConditionNormalizer.ToStorageString(original);
        var recovered = TaxConditionNormalizer.Normalize(storage);
        Assert.Equal(original, recovered);
    }

    [Fact]
    public void RoundTrip_unknown_se_mapea_a_unknown_no_a_si_mismo_estricto()
    {
        // Decision didactica: Unknown -> "UNKNOWN" -> Unknown.
        // Si el contrato cambiara a "no persistir Unknown", este test detecta
        // el cambio de comportamiento.
        var storage = TaxConditionNormalizer.ToStorageString(TaxConditionCanonical.Unknown);
        Assert.Equal("UNKNOWN", storage);
        Assert.Equal(TaxConditionCanonical.Unknown, TaxConditionNormalizer.Normalize(storage));
    }
}
