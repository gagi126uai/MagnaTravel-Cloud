using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Hallazgo #8 (barrido T5, 2026-07-24): "Hotel Hotel Prueba" en la factura sugerida — el nombre real de
/// un hotel suele arrancar con "Hotel " (ej. "Hotel Sheraton"), y anteponer el prefijo sin mirar el
/// nombre duplicaba la palabra. Estos tests blindan <see cref="ServiceLabelHelper.WithPrefix"/>, la
/// UNICA fuente que reemplazo la logica copiada a mano en 7 lugares (InvoiceSuggestedItemsBuilder,
/// BookingService, ReservaService).
/// </summary>
public class ServiceLabelHelperTests
{
    [Fact]
    public void WithPrefix_NombreSinElPrefijo_AnteponeElPrefijoNormal()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "Sheraton", "sin nombre");

        Assert.Equal("Hotel Sheraton", resultado);
    }

    [Fact]
    public void WithPrefix_NombreYaArrancaConElPrefijo_NoLoDuplica()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "Hotel Sheraton", "sin nombre");

        Assert.Equal("Hotel Sheraton", resultado);
    }

    [Fact]
    public void WithPrefix_NombreArrancaConElPrefijoEnOtroCasing_NoLoDuplica()
    {
        // case-insensitive a proposito: el nombre cargado por el vendedor puede venir con
        // mayuscula/minuscula distinta a la del prefijo ("hotel sheraton", "HOTEL Sheraton", etc.).
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "hotel Sheraton", "sin nombre");

        Assert.Equal("hotel Sheraton", resultado);
    }

    [Fact]
    public void WithPrefix_NombreVacio_UsaElFallback()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", null, "sin nombre");

        Assert.Equal("Hotel sin nombre", resultado);
    }

    [Fact]
    public void WithPrefix_NombreEnBlanco_UsaElFallback()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "   ", "sin nombre");

        Assert.Equal("Hotel sin nombre", resultado);
    }

    [Fact]
    public void WithPrefix_NombreVacioYFallbackVacio_DevuelveSoloElPrefijo()
    {
        // Caso de BookingService (PendingServiceChange.ServiceDescription): fallback vacio para que,
        // sin nombre cargado, el resultado quede en solo "Hotel" (sin "Hotel sin nombre").
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", null, string.Empty);

        Assert.Equal("Hotel", resultado);
    }

    [Fact]
    public void WithPrefix_NombreVacioYFallbackConFraseCompleta_ElFallbackNoQuedaDuplicado()
    {
        // Caso de la Asistencia (BookingService.cs, PendingServiceChange): fallback "al viajero" arma
        // "Asistencia al viajero" tal cual el texto historico, sin duplicar el prefijo.
        var resultado = ServiceLabelHelper.WithPrefix("Asistencia", null, "al viajero");

        Assert.Equal("Asistencia al viajero", resultado);
    }

    [Fact]
    public void WithPrefix_PlanDeAsistenciaYaArrancaConElPrefijo_NoLoDuplica()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Asistencia", "Asistencia Premium", "seguro");

        Assert.Equal("Asistencia Premium", resultado);
    }

    [Fact]
    public void WithPrefix_RecortaEspaciosAlPrincipioYFinalDelNombre()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "  Sheraton  ", "sin nombre");

        Assert.Equal("Hotel Sheraton", resultado);
    }
}
