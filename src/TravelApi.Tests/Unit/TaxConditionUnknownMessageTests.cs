using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bug reportado 2026-07-16: el mensaje de INV-118 ("no pudimos determinar la condición fiscal")
/// juntaba a las tres fichas posibles (agencia, operador, cliente) SIN decir cual estaba incompleta,
/// asi que el agente de viajes no sabia adonde ir a completarla. <see cref="BookingCancellationService.BuildTaxConditionUnknownMessage"/>
/// arma un mensaje que nombra EXPLICITAMENTE la o las fichas faltantes.
///
/// <para>El metodo es <c>internal static</c> (puro, sin DB) y se testea directo gracias a
/// <c>InternalsVisibleTo("TravelApi.Tests")</c> ya configurado en <c>TravelApi.Infrastructure.csproj</c>
/// (mismo patron que <see cref="BookingCancellationServiceHelpersTests"/>).</para>
/// </summary>
public class TaxConditionUnknownMessageTests
{
    [Fact]
    public void OnlyAgencyUnknown_NamesAgencyFicha()
    {
        var message = BookingCancellationService.BuildTaxConditionUnknownMessage(
            agencyCanonical: TaxConditionCanonical.Unknown,
            supplierCanonical: TaxConditionCanonical.ResponsableInscripto,
            customerCanonical: TaxConditionCanonical.ConsumidorFinal,
            supplierName: "Turismo Andina");

        Assert.Contains("ficha de la agencia", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operador", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cliente", message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlySupplierUnknown_NamesSupplierByCommercialName()
    {
        var message = BookingCancellationService.BuildTaxConditionUnknownMessage(
            agencyCanonical: TaxConditionCanonical.Monotributista,
            supplierCanonical: TaxConditionCanonical.Unknown,
            customerCanonical: TaxConditionCanonical.ConsumidorFinal,
            supplierName: "Turismo Andina");

        // Nombra al operador POR SU NOMBRE COMERCIAL, no un id ni un generico "el operador".
        Assert.Contains("ficha del operador Turismo Andina", message);
        Assert.DoesNotContain("agencia", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cliente", message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OnlySupplierUnknown_WithoutName_FallsBackToGenericLabel()
    {
        // Si por algun motivo el operador no vino cargado en el contexto, el mensaje sigue siendo
        // accionable (menciona la ficha del operador) aunque no tenga el nombre a mano.
        var message = BookingCancellationService.BuildTaxConditionUnknownMessage(
            agencyCanonical: TaxConditionCanonical.Monotributista,
            supplierCanonical: TaxConditionCanonical.Unknown,
            customerCanonical: TaxConditionCanonical.ConsumidorFinal,
            supplierName: null);

        Assert.Contains("ficha del operador", message);
    }

    [Fact]
    public void OnlyCustomerUnknown_NamesCustomerFicha()
    {
        var message = BookingCancellationService.BuildTaxConditionUnknownMessage(
            agencyCanonical: TaxConditionCanonical.Monotributista,
            supplierCanonical: TaxConditionCanonical.ResponsableInscripto,
            customerCanonical: TaxConditionCanonical.Unknown,
            supplierName: "Turismo Andina");

        Assert.Contains("ficha del cliente", message);
        Assert.DoesNotContain("agencia", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("operador", message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AllThreeUnknown_NamesAllThreeFichas()
    {
        var message = BookingCancellationService.BuildTaxConditionUnknownMessage(
            agencyCanonical: TaxConditionCanonical.Unknown,
            supplierCanonical: TaxConditionCanonical.Unknown,
            customerCanonical: TaxConditionCanonical.Unknown,
            supplierName: "Turismo Andina");

        Assert.Contains("ficha de la agencia", message, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ficha del operador Turismo Andina", message);
        Assert.Contains("ficha del cliente", message);
    }

    [Fact]
    public void Message_NeverLeaksEnumNamesOrJargon()
    {
        // Mismo estandar que el gate de exposicion de datos (CancellationErrorMessageLeakUnitTests):
        // el mensaje NO debe mostrar el nombre del enum TaxConditionCanonical ni jerga tecnica.
        var message = BookingCancellationService.BuildTaxConditionUnknownMessage(
            agencyCanonical: TaxConditionCanonical.Unknown,
            supplierCanonical: TaxConditionCanonical.Unknown,
            customerCanonical: TaxConditionCanonical.Unknown,
            supplierName: "Turismo Andina");

        Assert.DoesNotContain("TaxConditionCanonical", message);
        Assert.DoesNotContain("Unknown", message);
    }
}
