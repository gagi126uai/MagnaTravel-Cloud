using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-042 B3 + refuerzo data-exposure (2026-07-02): tests del saneador del motivo de error de ARCA que va al
/// front (<see cref="BookingCancellationService.SanitizeArcaErrorForUser"/>). Es BLOCKLIST a proposito: los
/// rechazos de AFIP en texto plano (aprobados en H2) pasan tal cual; el ruido tecnico de ARCA/SOAP/.NET/EF se
/// reemplaza por un copy generico. Estos tests blindan que el ruido .NET/EF SIN tokens tecnicos clasicos
/// (ObjectReference/ValueCannotBeNull/duplicate-key/stack) tambien se filtre, y que el texto AFIP legitimo no.
/// </summary>
public class Adr042ArcaErrorSanitizationTests
{
    private const string Generic = "AFIP rechazó la nota de crédito. Revisá los datos fiscales de la factura o reintentá.";

    [Theory]
    // XML / SOAP fault de ARCA.
    [InlineData("<soap:Fault><faultstring>Server was unable to process request.</faultstring></soap:Fault>")]
    [InlineData("<Errors><Err Code=\"600\">Token invalido</Err></Errors>")]
    // Mensajes .NET/EF SIN tokens tecnicos clasicos (los nuevos del refuerzo).
    [InlineData("Error técnico: Object reference not set to an instance of an object.")]
    [InlineData("Value cannot be null. (Parameter 'invoice')")]
    [InlineData("duplicate key value violates unique constraint \"IX_Invoices_CAE\"")]
    [InlineData("System.NullReferenceException: Object reference not set")]
    // Stack trace.
    [InlineData("at TravelApi.Infrastructure.Services.AfipService.Post() in D:\\repo\\AfipService.cs:line 42")]
    [InlineData("Se produjo un error. Ver detalle.cs:line 10")]
    // URL / JSON.
    [InlineData("Ver https://servicios1.afip.gov.ar/error")]
    [InlineData("{\"error\":\"bad request\"}")]
    public void Tecnico_seReemplazaPorGenerico(string raw)
    {
        Assert.Equal(Generic, BookingCancellationService.SanitizeArcaErrorForUser(raw));
    }

    [Theory]
    // Motivos de AFIP en texto PLANO legitimo (info util para el vendedor, aprobados en H2): pasan tal cual.
    [InlineData("CUIT del emisor sin habilitación")]
    [InlineData("El comprobante ya fue registrado con esos datos")]
    [InlineData("La fecha del comprobante no puede ser mayor a la fecha actual")]
    [InlineData("Punto de venta no autorizado para el tipo de comprobante")]
    public void TextoAfipPlano_pasaTalCual(string raw)
    {
        Assert.Equal(raw, BookingCancellationService.SanitizeArcaErrorForUser(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NuloOVacio_devuelveNull(string? raw)
    {
        Assert.Null(BookingCancellationService.SanitizeArcaErrorForUser(raw));
    }

    [Fact]
    public void TextoLargo_seAcota()
    {
        // Un motivo AFIP legitimo pero largo se acota (no dumps). No es "tecnico" -> no cae en el generico.
        var raw = new string('A', 500);
        var result = BookingCancellationService.SanitizeArcaErrorForUser(raw);
        Assert.NotNull(result);
        Assert.True(result!.Length <= 300);
    }
}
