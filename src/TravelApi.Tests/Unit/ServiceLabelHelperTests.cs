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

    // ============================================================
    // H11 (barrido E2E 2026-07-25): la palabra del prefijo puede aparecer en
    // CUALQUIER parte del nombre, no solo al principio (ej. "PI0724 Hotel B",
    // codigo de prueba adelante del nombre real del hotel).
    // ============================================================

    [Fact]
    public void WithPrefix_NombreContieneElPrefijoEnElMedio_NoLoDuplica()
    {
        // "PI0724 Hotel B" no ARRANCA con "Hotel", pero ya lo tiene adentro: antes esto daba
        // "Hotel PI0724 Hotel B" (hallazgo H11).
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "PI0724 Hotel B", "sin nombre");

        Assert.Equal("PI0724 Hotel B", resultado);
    }

    [Fact]
    public void WithPrefix_NombreContieneElPrefijoEnElMedioEnOtroCasing_NoLoDuplica()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "PI0724 hotel B", "sin nombre");

        Assert.Equal("PI0724 hotel B", resultado);
    }

    [Fact]
    public void WithPrefix_PlanDeAsistenciaContieneElPrefijoEnElMedio_NoLoDuplica()
    {
        var resultado = ServiceLabelHelper.WithPrefix("Asistencia", "Grupo Asistencia Premium", "seguro");

        Assert.Equal("Grupo Asistencia Premium", resultado);
    }

    [Fact]
    public void WithPrefix_NombreContieneElPrefijoSoloComoPartePalabraDistinta_SiLoAntepone()
    {
        // "Hoteleria" NO es la palabra "Hotel" (limite de palabra \b): el nombre no la tiene de
        // verdad, asi que el prefijo se antepone normal.
        var resultado = ServiceLabelHelper.WithPrefix("Hotel", "Hoteleria Especial", "sin nombre");

        Assert.Equal("Hotel Hoteleria Especial", resultado);
    }
}
