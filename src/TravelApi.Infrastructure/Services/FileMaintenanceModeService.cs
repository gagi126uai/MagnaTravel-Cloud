using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Obra "Restaurar TOTAL" (2026-07-28, firmada por el dueño) + hardening de la revisión de seguridad/
/// funcional del mismo día: implementación real de <see cref="IMaintenanceModeService"/>. Vive como
/// <b>singleton</b> (ver el registro en <c>Program.cs</c>) — pero OJO: hay DOS procesos .NET distintos
/// corriendo este mismo código (<c>api</c> y <c>worker</c>, ver <c>docker-compose.yml</c>), cada uno con su
/// PROPIA instancia singleton. Por eso el archivo en disco no es solo un respaldo para un reinicio: es el
/// ÚNICO canal por el que un proceso se entera de lo que activó/desactivó el OTRO.
///
/// <para><b>Hallazgo B-10 (revisión funcional 2026-07-28, "el worker sigue escribiendo durante el restore")</b>:
/// antes, el archivo se leía UNA sola vez, en el constructor — el <c>worker</c> (que corre los jobs de
/// Hangfire y usa su PROPIA instancia de este servicio) jamás se enteraba de un mantenimiento activado
/// DESPUÉS de que arrancó, y sus jobs seguían escribiendo en la base mientras <c>pg_restore</c> la reemplazaba
/// (sus conexiones nuevas además podían trabar los <c>DROP TABLE</c> del <c>--clean</c>). Ahora, cada lectura
/// de <see cref="IsActive"/>/<see cref="Reason"/>/<see cref="SinceUtc"/> vuelve a leer el archivo si pasó más
/// de <see cref="RefreshInterval"/> desde la última lectura — un caché CORTO, no una lectura por request (eso
/// sería I/O de disco en cada job de Hangfire).</para>
///
/// <para><b>Hallazgo B-11a (revisión funcional 2026-07-28, "el sistema queda tapiado sin salida", GRAVÍSIMO)</b>:
/// si el proceso muere entre activar y desactivar el mantenimiento, el archivo queda con <c>active: true</c>
/// para siempre (fail-closed a propósito) — pero eso significa que <c>/api/auth/login</c> también devuelve
/// 503 (ver <c>MaintenanceModeMiddleware</c>), así que NADIE puede autenticarse para arreglar nada, ni
/// siquiera un Admin. Como red de seguridad de ÚLTIMO recurso: si el mantenimiento sigue activo pasado
/// <see cref="_maxMaintenanceDuration"/>, se auto-desactiva con un log CRÍTICO.</para>
///
/// <para><b>Hallazgo B-N2 (ronda de hardening, 2026-07-28, "los timeouts no cierran entre sí")</b>: antes, el
/// reloj de <see cref="_maxMaintenanceDuration"/> arrancaba en <see cref="TryActivate"/> (el PRIMER paso, antes
/// del chequeo de esquema + candado fiscal + backup previo + <c>pg_restore</c>) — la suma de esos pasos podía
/// superar el presupuesto MIENTRAS una restauración legítima todavía estaba en curso. El fix tiene DOS partes:
/// (1) <see cref="Touch"/> renueva <see cref="SinceUtc"/> justo antes de arrancar el <c>pg_restore</c> real,
/// así el presupuesto de <see cref="_maxMaintenanceDuration"/> mide desde AHÍ (acotado por el timeout propio
/// del <c>pg_restore</c>), no desde el arranque de toda la operación — los pasos ANTERIORES tienen sus PROPIOS
/// timeouts (<c>Wipe:SchemaCheckTimeoutMinutes</c>, <c>Wipe:PgDumpTimeoutMinutes</c>,
/// <c>Wipe:MinioCopyTimeoutMinutes</c>), así que tampoco pueden colgarse indefinidamente sosteniendo el
/// candado. (2) <see cref="SuppressAutoExpiry"/> EXIME a una sesión de la auto-expiración cuando el desenlace
/// queda incierto — auto-desactivar un mantenimiento cuyo <c>pg_restore</c> quizás sigue corriendo sería
/// repetir exactamente el error que el hallazgo B1 vino a corregir. Ver
/// <c>SystemDataRestoreService.ExecuteTotalRestoreAsync</c> para dónde se llama cada uno.</para>
/// </summary>
public sealed class FileMaintenanceModeService : IMaintenanceModeService
{
    /// <summary>
    /// Default pensado para el contenedor de producción: <c>./logs:/app/logs</c> ya es un volumen persistente
    /// montado en <c>docker-compose.yml</c> para AMBOS servicios (<c>api</c> Y <c>worker</c>, mismo path de
    /// host) — por eso sirve como canal compartido entre los dos procesos, no solo como respaldo ante reinicio.
    /// </summary>
    internal const string DefaultStateFilePath = "/app/logs/maintenance-mode-state.json";

    /// <summary>Ver el comentario de clase, hallazgo B-10.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ver el comentario de clase, hallazgo B-11a/B-N2. Expuesto <c>internal</c> (con
    /// <c>InternalsVisibleTo("TravelApi.Tests")</c> ya configurado) para que el test guardián de la invariante
    /// de timeouts (<c>RestoreTotalTimeoutConfigurationTests</c>) lo derive de ACÁ en vez de mantener una copia
    /// paralela del número que se puede desincronizar en silencio.
    /// </summary>
    internal const int DefaultMaxMaintenanceDurationMinutes = 30;

    private readonly object _lock = new();
    private readonly string _stateFilePath;
    private readonly TimeSpan _maxMaintenanceDuration;
    private readonly ILogger<FileMaintenanceModeService> _logger;
    private readonly TimeProvider _timeProvider;
    private MaintenanceModeState _state;
    private DateTime _lastDiskReadUtc;

    public FileMaintenanceModeService(IConfiguration configuration, ILogger<FileMaintenanceModeService> logger)
        : this(configuration, logger, TimeProvider.System)
    {
    }

    /// <summary>
    /// Constructor interno con <see cref="TimeProvider"/> inyectable: los tests de auto-expiración/refresh
    /// necesitan simular "pasaron 31 minutos" sin un <c>Thread.Sleep</c> real de 31 minutos.
    /// </summary>
    internal FileMaintenanceModeService(IConfiguration configuration, ILogger<FileMaintenanceModeService> logger, TimeProvider timeProvider)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _stateFilePath = configuration["Maintenance:StateFilePath"] ?? DefaultStateFilePath;
        _maxMaintenanceDuration = TimeSpan.FromMinutes(
            configuration.GetValue<double?>("Maintenance:MaxDurationMinutes") ?? DefaultMaxMaintenanceDurationMinutes);
        _state = LoadStateFromDiskOrDefault();
        _lastDiskReadUtc = _timeProvider.GetUtcNow().UtcDateTime;
    }

    public bool IsActive
    {
        get { lock (_lock) { RefreshAndEnforceExpiryLocked(); return _state.Active; } }
    }

    public string? Reason
    {
        get { lock (_lock) { RefreshAndEnforceExpiryLocked(); return _state.Reason; } }
    }

    public DateTime? SinceUtc
    {
        get { lock (_lock) { RefreshAndEnforceExpiryLocked(); return _state.SinceUtc; } }
    }

    public string? CurrentStep
    {
        get { lock (_lock) { RefreshAndEnforceExpiryLocked(); return _state.Step; } }
    }

    public bool TryActivate(string reason)
    {
        lock (_lock)
        {
            RefreshAndEnforceExpiryLocked();
            if (_state.Active)
            {
                return false;
            }

            // Step arranca en null: los pasos los publica quien corre la operación, a medida que ocurren.
            _state = new MaintenanceModeState(
                true, reason, _timeProvider.GetUtcNow().UtcDateTime, RequiresManualClear: false, Step: null);
            _lastDiskReadUtc = _timeProvider.GetUtcNow().UtcDateTime;
            PersistToDiskBestEffort();
        }

        _logger.LogWarning("Modo mantenimiento ACTIVADO. Motivo: {Reason}", reason);
        return true;
    }

    public void SetStep(string step)
    {
        lock (_lock)
        {
            RefreshAndEnforceExpiryLocked();
            if (!_state.Active)
            {
                return;
            }

            _state = _state with { Step = step };
            _lastDiskReadUtc = _timeProvider.GetUtcNow().UtcDateTime;
            PersistToDiskBestEffort();
        }

        _logger.LogInformation("Modo mantenimiento: paso en curso = {Paso}.", step);
    }

    public void Touch()
    {
        lock (_lock)
        {
            RefreshAndEnforceExpiryLocked();
            if (!_state.Active)
            {
                return;
            }

            _state = _state with { SinceUtc = _timeProvider.GetUtcNow().UtcDateTime };
            _lastDiskReadUtc = _timeProvider.GetUtcNow().UtcDateTime;
            PersistToDiskBestEffort();
        }
    }

    public void SuppressAutoExpiry(string reason)
    {
        lock (_lock)
        {
            RefreshAndEnforceExpiryLocked();
            if (!_state.Active)
            {
                return;
            }

            // El paso se BORRA a propósito: desde acá ya no hay una restauración avanzando, hay una espera de
            // intervención humana. Dejar el último paso publicado haría que la pantalla siguiera mostrando
            // "poniendo el sistema al día" para siempre, que es justo lo contrario de lo que pasa.
            _state = _state with { Reason = reason, RequiresManualClear = true, Step = null };
            _lastDiskReadUtc = _timeProvider.GetUtcNow().UtcDateTime;
            PersistToDiskBestEffort();
        }

        _logger.LogCritical(
            "Modo mantenimiento marcado como DESENLACE INCIERTO — la auto-expiración queda SUSPENDIDA para esta " +
            "sesión, solo sale con intervención manual (ver docs/db-operations.md). Motivo: {Reason}", reason);
    }

    public void Deactivate()
    {
        lock (_lock)
        {
            _state = MaintenanceModeState.Inactive;
            _lastDiskReadUtc = _timeProvider.GetUtcNow().UtcDateTime;
            PersistToDiskBestEffort();
        }

        _logger.LogInformation("Modo mantenimiento DESACTIVADO.");
    }

    /// <summary>
    /// Se llama con el lock YA tomado, antes de leer o decidir sobre cualquier campo. Dos responsabilidades
    /// (ver el comentario de clase para el detalle de cada hallazgo):
    ///
    /// <list type="number">
    ///   <item><b>B-10</b>: si pasó más de <see cref="RefreshInterval"/> desde la última lectura de disco,
    ///   vuelve a leer el archivo — así este proceso se entera de cambios hechos por OTRO proceso (api ↔
    ///   worker) que comparte el mismo archivo.</item>
    ///   <item><b>B-11a/B-N2(a)</b>: si el mantenimiento está activo, NO está marcado
    ///   <see cref="MaintenanceModeState.RequiresManualClear"/>, y <c>SinceUtc</c> es más viejo que
    ///   <see cref="_maxMaintenanceDuration"/>, se auto-desactiva con un log CRÍTICO. Una sesión marcada
    ///   "requiere intervención manual" (ver <see cref="SuppressAutoExpiry"/>) NUNCA se auto-desactiva sola.</item>
    /// </list>
    /// </summary>
    private void RefreshAndEnforceExpiryLocked()
    {
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        if (now - _lastDiskReadUtc >= RefreshInterval)
        {
            _state = LoadStateFromDiskOrDefault();
            _lastDiskReadUtc = now;
        }

        if (_state is { Active: true, RequiresManualClear: false } && _state.SinceUtc is { } since && now - since >= _maxMaintenanceDuration)
        {
            _logger.LogCritical(
                "Modo mantenimiento llevaba activo mas de {Minutos} minutos (desde {Desde}) - se autodesactiva " +
                "por seguridad. Esto NO deberia pasar en una restauracion exitosa: revisar si el proceso murio a " +
                "mitad de camino o si el restore quedo colgado de verdad.",
                _maxMaintenanceDuration.TotalMinutes, since);
            _state = MaintenanceModeState.Inactive;
            PersistToDiskBestEffort();
        }
    }

    /// <summary>
    /// Si el archivo no existe, no se pudo leer o vino corrupto, se arranca en
    /// <see cref="MaintenanceModeState.Inactive"/> — un archivo roto NUNCA debe dejar el sistema entero
    /// bloqueado para siempre sin que nadie pueda arreglarlo sin entrar al contenedor a mano (a diferencia del
    /// caso "reinicio/otro proceso a mitad de un restore", acá no hay ambigüedad sobre qué pasó: un archivo
    /// que no se puede leer no es evidencia de una restauración interrumpida).
    ///
    /// <para><b>Trampa de framework evitada (hallazgo menor #16, revisión de infra)</b>: si este método
    /// leyera el archivo justo en el instante en que <see cref="PersistToDiskBestEffort"/> de OTRO proceso lo
    /// está reescribiendo, <c>File.ReadAllText</c> podría ver contenido a medio escribir (JSON truncado) y
    /// tirar una excepción de parseo — el <c>catch</c> de acá abajo lo atrapa igual y arranca "sin
    /// mantenimiento" por un instante (falla ABIERTA, ~2s como mucho hasta el próximo refresh). Por eso
    /// <see cref="PersistToDiskBestEffort"/> escribe con archivo temporal + <c>File.Move</c> atómico: la
    /// ventana de "contenido a medio escribir" deja de existir del todo (un <c>File.Move</c> en el mismo
    /// volumen es atómico a nivel de sistema operativo — el lector SIEMPRE ve o el archivo viejo completo o el
    /// nuevo completo, nunca una mezcla).</para>
    /// </summary>
    private MaintenanceModeState LoadStateFromDiskOrDefault()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return MaintenanceModeState.Inactive;
            }

            var json = File.ReadAllText(_stateFilePath);
            var loaded = JsonSerializer.Deserialize<MaintenanceModeState>(json);
            return loaded ?? MaintenanceModeState.Inactive;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "No se pudo leer el archivo de estado de mantenimiento {Path}; se arranca SIN mantenimiento.",
                _stateFilePath);
            return MaintenanceModeState.Inactive;
        }
    }

    /// <summary>
    /// Best-effort a propósito: si el disco falla (permisos, sin espacio), el flag en MEMORIA sigue siendo la
    /// fuente de verdad para ESTE proceso — el middleware lee <see cref="IsActive"/>, no el archivo. Lo único
    /// que se pierde si esto falla es la capacidad de recuperar el estado ante un reinicio del proceso o de
    /// avisarle al OTRO proceso (api ↔ worker) del cambio.
    ///
    /// <para><b>Escritura ATÓMICA (hallazgo menor #16, revisión de infra 2026-07-28)</b>: <c>File.WriteAllText</c>
    /// directo sobre <see cref="_stateFilePath"/> NO es atómico — escribe el contenido nuevo progresivamente
    /// sobre el archivo existente, así que un lector (el OTRO proceso, api↔worker) que justo en ese instante
    /// esté leyendo el archivo podría ver una mezcla de contenido viejo y nuevo (JSON corrupto). Se escribe
    /// primero a un archivo temporal EN EL MISMO DIRECTORIO (mismo volumen, requisito para que el siguiente
    /// paso sea atómico) y se usa <c>File.Move(..., overwrite: true)</c> para reemplazar el archivo final de
    /// un solo golpe — a nivel de sistema operativo, un <c>rename</c> dentro del mismo volumen es una
    /// operación atómica: cualquier lector concurrente ve o el archivo viejo completo, o el nuevo completo,
    /// nunca algo a medio escribir.</para>
    /// </summary>
    private void PersistToDiskBestEffort()
    {
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempFilePath = _stateFilePath + $".tmp-{Guid.NewGuid():N}";
            File.WriteAllText(tempFilePath, JsonSerializer.Serialize(_state));
            File.Move(tempFilePath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo persistir el estado de mantenimiento en {Path}.", _stateFilePath);
        }
    }

    /// <summary>
    /// Estado inmutable que se guarda tal cual en el archivo — un record nuevo reemplaza al anterior en cada
    /// cambio, nunca se muta in-place. <see cref="RequiresManualClear"/> (hallazgo B-N2(a)): distingue una
    /// sesión de mantenimiento "normal" (puede auto-expirar si algo salió mal) de una con desenlace INCIERTO
    /// (nunca se auto-desactiva sola, ver <see cref="SuppressAutoExpiry"/>).
    ///
    /// <para><b>Trampa de framework (rediseño 2026-07-30)</b>: <see cref="Step"/> se agregó al final del record
    /// DESPUÉS de que ya había archivos escritos con la forma vieja. <c>System.Text.Json</c>, cuando deserializa
    /// sobre un constructor con parámetros, a los que no encuentra en el JSON les pasa su valor por defecto —
    /// así que un archivo viejo (sin "Step") se lee perfecto y queda con <c>Step = null</c>. Por eso agregar un
    /// campo NUEVO al final es seguro; sacar o renombrar uno existente NO lo sería.</para>
    /// </summary>
    private sealed record MaintenanceModeState(
        bool Active, string? Reason, DateTime? SinceUtc, bool RequiresManualClear, string? Step)
    {
        public static readonly MaintenanceModeState Inactive = new(false, null, null, false, null);
    }
}
