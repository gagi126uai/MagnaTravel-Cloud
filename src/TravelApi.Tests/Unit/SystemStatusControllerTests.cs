using System;
using Microsoft.AspNetCore.Mvc;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Rediseño de la pantalla de resguardos (2026-07-30, firmado, §7 punto 2): <c>GET /api/system/status</c> es el
/// único endpoint que el modo mantenimiento deja pasar, así que es POR ACÁ por donde la pantalla de espera se
/// entera del paso en curso. Se cubre el mapeo (código + texto en criollo) y, sobre todo, que un código
/// desconocido no se filtre crudo a la pantalla (T-5).
/// </summary>
public class SystemStatusControllerTests
{
    /// <summary>Estado de mantenimiento fijo, armado por cada test: alcanza para probar el mapeo del controlador.</summary>
    private sealed class StubMaintenanceModeService : IMaintenanceModeService
    {
        public bool IsActive { get; init; }
        public string? Reason { get; init; }
        public DateTime? SinceUtc { get; init; }
        public string? CurrentStep { get; init; }

        public bool TryActivate(string reason) => throw new NotSupportedException();
        public void SetStep(string step) => throw new NotSupportedException();
        public void Touch() => throw new NotSupportedException();
        public void SuppressAutoExpiry(string reason) => throw new NotSupportedException();
        public void Deactivate() => throw new NotSupportedException();
    }

    private static SystemStatusResponse Get(IMaintenanceModeService maintenanceMode)
    {
        var result = new SystemStatusController(maintenanceMode).Get();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        return Assert.IsType<SystemStatusResponse>(ok.Value);
    }

    [Fact]
    public void SinMantenimiento_NoHayPasoNiTexto()
    {
        var response = Get(new StubMaintenanceModeService { IsActive = false });

        Assert.False(response.EnMantenimiento);
        Assert.Null(response.Paso);
        Assert.Null(response.PasoTexto);
    }

    [Theory]
    [InlineData(RestoreProgressSteps.Datos, "Trayendo los datos de la copia elegida")]
    [InlineData(RestoreProgressSteps.Resguardo, "Guardamos una copia de cómo está el sistema ahora")]
    [InlineData(RestoreProgressSteps.Actualizacion, "Poniendo el sistema al día")]
    public void ConUnPasoEnCurso_ViajaElCodigoYElTextoFirmado(string paso, string textoEsperado)
    {
        var response = Get(new StubMaintenanceModeService
        {
            IsActive = true,
            Reason = "Restauración total del sistema en curso.",
            SinceUtc = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc),
            CurrentStep = paso,
        });

        Assert.True(response.EnMantenimiento);
        Assert.Equal(paso, response.Paso);
        Assert.Equal(textoEsperado, response.PasoTexto);
    }

    /// <summary>
    /// T-5: si el archivo de estado trajera un código que este código no conoce (por ejemplo, escrito por una
    /// versión distinta del sistema durante un deploy), NO se manda: mejor sin paso que un valor interno crudo
    /// en la pantalla.
    /// </summary>
    [Fact]
    public void ConUnPasoDesconocido_NoSeMandaNadaEnVezDeMandarElValorCrudo()
    {
        var response = Get(new StubMaintenanceModeService
        {
            IsActive = true,
            Reason = "Restauración total del sistema en curso.",
            CurrentStep = "paso_interno_raro",
        });

        Assert.Null(response.Paso);
        Assert.Null(response.PasoTexto);
    }
}
