using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// PARTE 1 de la obra "gate ámbito" (decisión firmada del dueño, 2026-08-05): tests PUROS de
/// <see cref="PassportAlertScopeGate"/> (sin DB, sin ReservaService). Cubre la matriz completa firmada:
/// Internacional / Nacional / SinDefinir / mixto / sin servicios.
/// </summary>
public class PassportAlertScopeGateTests
{
    [Fact]
    public void SinServiciosEnLaReserva_QuedaAbierto_ComportamientoHistoricoConservador()
    {
        var abierto = PassportAlertScopeGate.IsOpen(
            reservaHasAnyServiceWithScope: false,
            reservaHasInternationalService: false,
            reservaHasUndefinedScopeService: false);

        Assert.True(abierto);
    }

    [Fact]
    public void TodoNacionalConAmbitoDefinido_SeApaga()
    {
        var abierto = PassportAlertScopeGate.IsOpen(
            reservaHasAnyServiceWithScope: true,
            reservaHasInternationalService: false,
            reservaHasUndefinedScopeService: false);

        Assert.False(abierto);
    }

    [Fact]
    public void HayAlMenosUnServicioInternacional_QuedaAbierto()
    {
        var abierto = PassportAlertScopeGate.IsOpen(
            reservaHasAnyServiceWithScope: true,
            reservaHasInternationalService: true,
            reservaHasUndefinedScopeService: false);

        Assert.True(abierto);
    }

    [Fact]
    public void HayServicioSinDefinir_AunqueElRestoSeaNacional_QuedaAbierto_ReglaConservadora()
    {
        // Falta de dato NUNCA apaga un aviso que hoy existe (decision firmada).
        var abierto = PassportAlertScopeGate.IsOpen(
            reservaHasAnyServiceWithScope: true,
            reservaHasInternationalService: false,
            reservaHasUndefinedScopeService: true);

        Assert.True(abierto);
    }

    [Fact]
    public void MixtoNacionalEInternacional_QuedaAbierto()
    {
        var abierto = PassportAlertScopeGate.IsOpen(
            reservaHasAnyServiceWithScope: true,
            reservaHasInternationalService: true,
            reservaHasUndefinedScopeService: false);

        Assert.True(abierto);
    }

    [Fact]
    public void MixtoNacionalEInternacionalYSinDefinir_QuedaAbierto()
    {
        var abierto = PassportAlertScopeGate.IsOpen(
            reservaHasAnyServiceWithScope: true,
            reservaHasInternationalService: true,
            reservaHasUndefinedScopeService: true);

        Assert.True(abierto);
    }
}
