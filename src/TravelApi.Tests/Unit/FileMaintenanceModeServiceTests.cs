using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.DTOs;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño) + hardening de seguridad/funcional del mismo día:
/// cubre <see cref="FileMaintenanceModeService"/> — el flag en memoria, la persistencia en disco (para
/// sobrevivir un reinicio del proceso o avisarle a OTRO proceso .NET que comparte el archivo — hallazgo B-10),
/// la activación ATÓMICA (hallazgo B4) y la auto-expiración (hallazgo B-11a).
/// </summary>
public class FileMaintenanceModeServiceTests : IDisposable
{
    private readonly string _tempFilePath;

    public FileMaintenanceModeServiceTests()
    {
        _tempFilePath = Path.Combine(Path.GetTempPath(), $"maintenance-mode-test-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        if (File.Exists(_tempFilePath))
        {
            File.Delete(_tempFilePath);
        }
    }

    /// <summary>Fake mínimo de <see cref="TimeProvider"/> (sin traer un paquete nuevo): permite simular "pasaron 31 minutos" sin un Thread.Sleep real.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start)
        {
            _now = start;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    private FileMaintenanceModeService NewService(TimeProvider? timeProvider = null, double? maxDurationMinutes = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Maintenance:StateFilePath"] = _tempFilePath,
        };
        if (maxDurationMinutes is not null)
        {
            settings["Maintenance:MaxDurationMinutes"] = maxDurationMinutes.Value.ToString();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return new FileMaintenanceModeService(configuration, NullLogger<FileMaintenanceModeService>.Instance, timeProvider ?? TimeProvider.System);
    }

    [Fact]
    public void SinArchivoPrevio_ArrancaInactivo()
    {
        var service = NewService();

        Assert.False(service.IsActive);
        Assert.Null(service.Reason);
        Assert.Null(service.SinceUtc);
    }

    [Fact]
    public void TryActivate_PrendeElFlagYGuardaElMotivoYLaFechaYDevuelveTrue()
    {
        var service = NewService();

        var activated = service.TryActivate("Restauración total del sistema en curso.");

        Assert.True(activated);
        Assert.True(service.IsActive);
        Assert.Equal("Restauración total del sistema en curso.", service.Reason);
        Assert.NotNull(service.SinceUtc);
    }

    [Fact]
    public void TryActivate_SiYaEstabaActivo_NoPisaNadaYDevuelveFalse()
    {
        // Hallazgo B4 de seguridad: "dos restauraciones a la vez se pisan". TryActivate tiene que ser el
        // candado atomico que resuelve la carrera - la SEGUNDA llamada nunca debe pisar el motivo/fecha de la
        // primera.
        var service = NewService();
        service.TryActivate("primer motivo");

        var segundoIntento = service.TryActivate("segundo motivo, no deberia aplicar");

        Assert.False(segundoIntento);
        Assert.Equal("primer motivo", service.Reason);
    }

    [Fact]
    public void Deactivate_ApagaElFlagYLimpiaMotivoYFecha()
    {
        var service = NewService();
        service.TryActivate("motivo de prueba");

        service.Deactivate();

        Assert.False(service.IsActive);
        Assert.Null(service.Reason);
        Assert.Null(service.SinceUtc);
    }

    [Fact]
    public void TryActivate_PersisteEnDiscoYUnaInstanciaNuevaLoLeeAlArrancar()
    {
        // Simula un reinicio del PROCESO a mitad de una restauracion: se activa el mantenimiento con una
        // instancia, y una instancia NUEVA (como si el proceso se hubiera reiniciado) tiene que seguir viendo
        // el mantenimiento activo al arrancar - leyendo el archivo, no la memoria de la instancia anterior.
        var firstInstance = NewService();
        firstInstance.TryActivate("Restauración total del sistema en curso.");

        var secondInstanceAfterRestart = NewService();

        Assert.True(secondInstanceAfterRestart.IsActive);
        Assert.Equal("Restauración total del sistema en curso.", secondInstanceAfterRestart.Reason);
    }

    [Fact]
    public void Deactivate_PersisteEnDiscoYUnaInstanciaNuevaLoLeeInactivo()
    {
        var firstInstance = NewService();
        firstInstance.TryActivate("motivo");
        firstInstance.Deactivate();

        var secondInstance = NewService();

        Assert.False(secondInstance.IsActive);
    }

    [Fact]
    public void ArchivoCorrupto_ArrancaSinMantenimientoEnVezDeRomperse()
    {
        // Fail-open a proposito SOLO para este caso puntual (ver el comentario XML de LoadStateFromDiskOrDefault):
        // un archivo roto no debe dejar el sistema entero bloqueado para siempre sin forma de arreglarlo.
        File.WriteAllText(_tempFilePath, "{ esto no es json valido");

        var service = NewService();

        Assert.False(service.IsActive);
    }

    [Fact]
    public void OtroProcesoActivaElArchivo_UnaInstanciaExistenteLoDetectaSinReiniciar()
    {
        // Hallazgo B-10 (revision funcional): simula api y worker como DOS instancias separadas compartiendo
        // el mismo archivo. Antes de este fix, la instancia del "worker" solo leia el archivo en su propio
        // constructor y JAMAS se enteraba de un mantenimiento activado despues por el proceso "api".
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var workerInstance = NewService(timeProvider); // arranca ANTES de que "api" active nada.
        Assert.False(workerInstance.IsActive);

        var apiInstance = NewService(timeProvider);
        apiInstance.TryActivate("Restauración total del sistema en curso.");

        // Sin avanzar el tiempo, el cache corto de "workerInstance" todavia no vencio - sigue viendo el
        // estado viejo (inactivo). Esto es el trade-off documentado: hasta 2s de demora como mucho.
        Assert.False(workerInstance.IsActive);

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        Assert.True(workerInstance.IsActive);
        Assert.Equal("Restauración total del sistema en curso.", workerInstance.Reason);
    }

    [Fact]
    public void MantenimientoActivoMasDeLoPermitido_SeAutoDesactivaSolo()
    {
        // Hallazgo B-11a (revision funcional, GRAVISIMO): si el proceso muere a mitad de una restauracion, el
        // archivo queda con "active: true" para siempre - y CASI todo /api/** (incluido el login) devuelve
        // 503. Como red de seguridad de ULTIMO recurso, pasado "Maintenance:MaxDurationMinutes" el propio
        // servicio se auto-desactiva.
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = NewService(timeProvider, maxDurationMinutes: 30);
        service.TryActivate("Restauración total del sistema en curso.");
        Assert.True(service.IsActive);

        timeProvider.Advance(TimeSpan.FromMinutes(31));

        Assert.False(service.IsActive);
    }

    [Fact]
    public void MantenimientoActivoDentroDelLimite_NoSeAutoDesactivaTodavia()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var service = NewService(timeProvider, maxDurationMinutes: 30);
        service.TryActivate("Restauración total del sistema en curso.");

        timeProvider.Advance(TimeSpan.FromMinutes(10));

        Assert.True(service.IsActive);
    }

    // ============================================================================================
    // Paso en curso (rediseño de la pantalla de resguardos, 2026-07-30, §7 punto 2)
    // ============================================================================================

    [Fact]
    public void SetStep_PublicaElPasoYLoPersisteParaQueOtraInstanciaLoLea()
    {
        var service = NewService();
        service.TryActivate("Restauración total del sistema en curso.");

        // Recién activado no hay paso: los publica quien corre la operación, a medida que ocurren.
        Assert.Null(service.CurrentStep);

        service.SetStep(RestoreProgressSteps.Datos);
        Assert.Equal(RestoreProgressSteps.Datos, service.CurrentStep);

        service.SetStep(RestoreProgressSteps.Actualizacion);
        Assert.Equal(RestoreProgressSteps.Actualizacion, service.CurrentStep);

        // Mismo canal que ya usa el resto del estado: el archivo, así lo ve otro proceso (o este después de un reinicio).
        var otraInstancia = NewService();
        Assert.Equal(RestoreProgressSteps.Actualizacion, otraInstancia.CurrentStep);
    }

    [Fact]
    public void SetStep_SinMantenimientoActivo_NoPublicaNada()
    {
        var service = NewService();

        service.SetStep(RestoreProgressSteps.Datos);

        Assert.Null(service.CurrentStep);
    }

    [Fact]
    public void Deactivate_LimpiaElPaso()
    {
        var service = NewService();
        service.TryActivate("Restauración total del sistema en curso.");
        service.SetStep(RestoreProgressSteps.Actualizacion);

        service.Deactivate();

        Assert.Null(service.CurrentStep);
    }

    [Fact]
    public void SuppressAutoExpiry_BorraElPaso_PorqueYaNoHayNadaAvanzando()
    {
        // Doble fallo (ADR-052 D4.5): el sistema queda esperando intervención humana. Dejar publicado el
        // último paso haría que la pantalla siguiera diciendo "poniendo el sistema al día" para siempre.
        var service = NewService();
        service.TryActivate("Restauración total del sistema en curso.");
        service.SetStep(RestoreProgressSteps.Actualizacion);

        service.SuppressAutoExpiry("Requiere intervención manual.");

        Assert.Null(service.CurrentStep);
        Assert.True(service.IsActive);
    }

    [Fact]
    public void ArchivoDeLaFormaVIEJA_SinElCampoDelPaso_SeLeeIgualSinRomperse()
    {
        // Trampa de framework: el campo "Step" se agregó al final del record DESPUÉS de que ya había archivos
        // escritos con la forma vieja. System.Text.Json le pasa el valor por defecto a los parámetros que no
        // encuentra en el JSON, así que un archivo viejo se lee perfecto y queda sin paso.
        File.WriteAllText(
            _tempFilePath,
            """{"Active":true,"Reason":"Restauración total del sistema en curso.","SinceUtc":"2026-07-30T00:00:00Z","RequiresManualClear":true}""");

        var service = NewService();

        Assert.True(service.IsActive);
        Assert.Null(service.CurrentStep);
    }
}
