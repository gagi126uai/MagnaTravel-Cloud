using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// (2026-07-17, fix de raiz del bug real "edito la condicion fiscal del cliente y no hace nada")
/// Tests del catalogo <see cref="CustomerTaxConditionCatalog"/>: la traduccion codigo AFIP &lt;-&gt; texto
/// de <c>Customer.TaxConditionId</c>/<c>Customer.TaxCondition</c>, y la funcion
/// <see cref="CustomerTaxConditionCatalog.ResolveIncoming"/> que <c>CustomerService</c> usa para decidir
/// que persistir en un Create/Update.
/// </summary>
public class CustomerTaxConditionCatalogTests
{
    // ============== TryGetLabel ==============

    [Theory]
    [InlineData(1, "Responsable Inscripto")]
    [InlineData(4, "Exento")]
    [InlineData(5, "Consumidor Final")]
    [InlineData(6, "Monotributo")]
    public void TryGetLabel_KnownCode_ReturnsLabel(int code, string expectedLabel)
    {
        Assert.Equal(expectedLabel, CustomerTaxConditionCatalog.TryGetLabel(code));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void TryGetLabel_UnknownCode_ReturnsNull(int code)
    {
        Assert.Null(CustomerTaxConditionCatalog.TryGetLabel(code));
    }

    // ============== TryGetIdFromLabel ==============

    [Theory]
    [InlineData("Responsable Inscripto", 1)]
    [InlineData("RESPONSABLE_INSCRIPTO", 1)] // formato de storage (Supplier/FiscalSnapshot)
    [InlineData("Exento", 4)]
    [InlineData("Consumidor Final", 5)]
    [InlineData("Monotributo", 6)]
    [InlineData("MONOTRIBUTISTA", 6)]
    [InlineData("  monotributo  ", 6)] // TaxConditionNormalizer trimea y es case-insensitive
    public void TryGetIdFromLabel_KnownText_ReturnsId(string text, int expectedId)
    {
        Assert.Equal(expectedId, CustomerTaxConditionCatalog.TryGetIdFromLabel(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("texto que no existe")]
    [InlineData("Extranjero")] // el catalogo de 4 no cubre Extranjero (no tiene codigo AFIP en este subset)
    public void TryGetIdFromLabel_UnknownOrEmptyText_ReturnsNull(string? text)
    {
        Assert.Null(CustomerTaxConditionCatalog.TryGetIdFromLabel(text));
    }

    // ============== ResolveIncoming ==============

    [Fact]
    public void ResolveIncoming_IdProvided_TextAlwaysDerivedFromCatalog_IgnoresIncomingText()
    {
        // El caso REAL del bug: vino el codigo (el desplegable de la ficha) y NADA de texto (el
        // formulario nunca lo manda). El texto tiene que salir del catalogo, no quedar vacio/viejo.
        var (id, text) = CustomerTaxConditionCatalog.ResolveIncoming(
            incomingId: 1, incomingText: null, existingId: null, existingText: "Consumidor Final");

        Assert.Equal(1, id);
        Assert.Equal("Responsable Inscripto", text);
    }

    [Fact]
    public void ResolveIncoming_IdProvided_EvenIfIncomingTextSaysSomethingElse_CatalogWins()
    {
        // Defensa: si por algun motivo llegara un texto que NO coincide con el codigo, el codigo
        // manda siempre (single source of truth) — nunca puede persistir un par incoherente.
        var (id, text) = CustomerTaxConditionCatalog.ResolveIncoming(
            incomingId: 5, incomingText: "Responsable Inscripto", existingId: 1, existingText: "Responsable Inscripto");

        Assert.Equal(5, id);
        Assert.Equal("Consumidor Final", text);
    }

    [Fact]
    public void ResolveIncoming_NoId_TextDiffersFromExisting_DerivesInverseId()
    {
        // Caller legacy que solo manda texto (nunca el codigo): si el texto es una novedad real
        // (distinto al que ya habia guardado), se deriva el codigo inverso.
        var (id, text) = CustomerTaxConditionCatalog.ResolveIncoming(
            incomingId: null, incomingText: "Exento", existingId: null, existingText: "Consumidor Final");

        Assert.Equal(4, id);
        Assert.Equal("Exento", text);
    }

    [Fact]
    public void ResolveIncoming_NoId_TextSameAsExisting_PreservesExistingId_NoSpuriousChange()
    {
        // Este es el caso que protege a los callers que "echan" el mismo texto de vuelta al editar un
        // campo no relacionado (ej. telefono): NO debe inventarse un codigo nuevo solo porque el texto
        // vino en el payload — si no cambio nada, no hay nada que derivar.
        var (id, text) = CustomerTaxConditionCatalog.ResolveIncoming(
            incomingId: null, incomingText: "Consumidor Final", existingId: null, existingText: "Consumidor Final");

        Assert.Null(id);
        Assert.Equal("Consumidor Final", text);
    }

    [Fact]
    public void ResolveIncoming_NothingProvided_PreservesExistingIdAndText()
    {
        var (id, text) = CustomerTaxConditionCatalog.ResolveIncoming(
            incomingId: null, incomingText: null, existingId: 6, existingText: "Monotributo");

        Assert.Equal(6, id);
        Assert.Equal("Monotributo", text);
    }

    [Fact]
    public void ResolveIncoming_OmittedText_IdSameAsExisting_PreservesLabel_NoSpuriousAudit()
    {
        // Espejo del PUT real del formulario del cliente sobre un cliente YA coherente: manda el mismo
        // Id que ya tenia, texto ausente. No debe "cambiar" nada (ni el Id ni el texto).
        var (id, text) = CustomerTaxConditionCatalog.ResolveIncoming(
            incomingId: 1, incomingText: null, existingId: 1, existingText: "Responsable Inscripto");

        Assert.Equal(1, id);
        Assert.Equal("Responsable Inscripto", text);
    }

    [Fact]
    public void ResolveIncoming_UnknownId_FallsBackToTextRule()
    {
        // Defensivo: un codigo fuera de la tabla de 4 (no deberia pasar desde el dropdown) no inventa
        // un texto — se degrada a la regla del texto (que en este caso tampoco aporta nada nuevo, asi
        // que preserva lo que ya habia).
        var (id, text) = CustomerTaxConditionCatalog.ResolveIncoming(
            incomingId: 999, incomingText: null, existingId: 1, existingText: "Responsable Inscripto");

        Assert.Equal(1, id);
        Assert.Equal("Responsable Inscripto", text);
    }
}
