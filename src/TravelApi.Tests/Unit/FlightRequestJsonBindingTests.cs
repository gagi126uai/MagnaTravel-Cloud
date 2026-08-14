using System.Text.Json;
using System.Text.Json.Serialization;
using TravelApi.Application.DTOs;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "PDF completo" (2026-08-13/14), fix de review del frontend: deja FIJADA en el repo la prueba de
/// que el binder JSON real de la API entiende <c>TimeOnly</c> con el formato corto que manda un
/// <c>&lt;input type="time"&gt;</c> del navegador ("08:30", sin segundos) — antes esto se probó a mano en
/// una consola descartable y no quedó evidencia verificable.
///
/// <para><b>Por qué las MISMAS opciones que la API</b>: <c>Program.cs</c> registra
/// <c>AddControllers().AddJsonOptions(...)</c>. El framework arranca esas opciones desde
/// <c>JsonSerializerDefaults.Web</c> (camelCase + case-insensitive — comportamiento estándar de
/// ASP.NET Core, no algo que el proyecto configure a mano) y Program.cs solo le suma
/// <c>ReferenceHandler.IgnoreCycles</c>. Reconstruir esas mismas dos líneas acá es lo que hace que este
/// test represente de verdad lo que le pasa a un POST real, no una suposición.</para>
/// </summary>
public class FlightRequestJsonBindingTests
{
    /// <summary>Espejo exacto de como Program.cs arma JsonSerializerOptions para los controllers (ver el XML-doc de la clase).</summary>
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    [Fact]
    public void CreateFlightRequest_ShortTimeFormat_HHmm_ParsesAllFourOutboundReturnFields()
    {
        // "HH:mm" es EXACTAMENTE lo que manda un <input type="time"> del navegador (sin segundos) — el
        // formato real que va a viajar desde FlightInlineForm.jsx cuando el frontend cablee estos campos.
        const string json = """
            {
              "supplierId": "1",
              "departureTime": "2027-02-10T00:00:00",
              "outboundDepartureTime": "08:30",
              "outboundArrivalTime": "11:45",
              "returnDepartureTime": "19:00",
              "returnArrivalTime": "23:10",
              "netCost": 0,
              "salePrice": 100,
              "commission": 0,
              "tax": 0
            }
            """;

        var request = JsonSerializer.Deserialize<CreateFlightRequest>(json, ApiJsonOptions);

        Assert.NotNull(request);
        Assert.Equal(new TimeOnly(8, 30), request!.OutboundDepartureTime);
        Assert.Equal(new TimeOnly(11, 45), request.OutboundArrivalTime);
        Assert.Equal(new TimeOnly(19, 0), request.ReturnDepartureTime);
        Assert.Equal(new TimeOnly(23, 10), request.ReturnArrivalTime);
    }

    [Fact]
    public void CreateFlightRequest_LongTimeFormat_HHmmss_ParsesAllFourOutboundReturnFields()
    {
        // "HH:mm:ss" es el formato que el propio backend produce en un round-trip (ver
        // QuoteBudgetPdfRulesTests/el DTO de lectura) -- confirma que ACEPTAR el formato corto no le
        // rompió la compatibilidad con el formato largo que ya se usaba.
        const string json = """
            {
              "supplierId": "1",
              "departureTime": "2027-02-10T00:00:00",
              "outboundDepartureTime": "08:30:00",
              "outboundArrivalTime": "11:45:00",
              "returnDepartureTime": "19:00:00",
              "returnArrivalTime": "23:10:00",
              "netCost": 0,
              "salePrice": 100,
              "commission": 0,
              "tax": 0
            }
            """;

        var request = JsonSerializer.Deserialize<CreateFlightRequest>(json, ApiJsonOptions);

        Assert.NotNull(request);
        Assert.Equal(new TimeOnly(8, 30), request!.OutboundDepartureTime);
        Assert.Equal(new TimeOnly(11, 45), request.OutboundArrivalTime);
        Assert.Equal(new TimeOnly(19, 0), request.ReturnDepartureTime);
        Assert.Equal(new TimeOnly(23, 10), request.ReturnArrivalTime);
    }

    [Fact]
    public void CreateFlightRequest_MissingTimeOnlyFields_DeserializesAsNull_NeverThrows()
    {
        // Caso normal (mayoría de las cargas hoy): el vendedor no anotó horarios -- el request no manda
        // esos 4 campos y el binder los deja en null (son opcionales), no explota.
        const string json = """
            {
              "supplierId": "1",
              "departureTime": "2027-02-10T00:00:00",
              "netCost": 0,
              "salePrice": 100,
              "commission": 0,
              "tax": 0
            }
            """;

        var request = JsonSerializer.Deserialize<CreateFlightRequest>(json, ApiJsonOptions);

        Assert.NotNull(request);
        Assert.Null(request!.OutboundDepartureTime);
        Assert.Null(request.OutboundArrivalTime);
        Assert.Null(request.ReturnDepartureTime);
        Assert.Null(request.ReturnArrivalTime);
    }
}
