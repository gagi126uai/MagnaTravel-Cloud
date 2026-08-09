using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Ai;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// La pantalla "Configuracion → Inteligencia artificial" del lado del motor (spec firmada
/// 2026-08-07 §15, dependencias M-28 a M-33).
///
/// <para><b>Las tres reglas que no se negocian</b>:</para>
/// <list type="number">
///   <item>La clave <b>entra y no sale</b>: se guarda cifrada y de ella solo se devuelven los
///   primeros 4 caracteres. Ningun metodo de esta clase devuelve la clave completa.</item>
///   <item>Lo cargado en la pantalla <b>le gana</b> a las variables del servidor; el servidor es el
///   respaldo (M-29).</item>
///   <item>Probar <b>no guarda</b> la configuracion. Lo unico que queda registrado de una prueba es
///   como le fue y cuando, que es lo que alimenta la foto de estado de arriba.</item>
/// </list>
/// </summary>
public sealed class AiSettingsService : IAiSettingsService
{
    private readonly AppDbContext _dbContext;
    private readonly ISensitiveDataProtector _sensitiveDataProtector;
    private readonly IAiConnectionResolver _connectionResolver;
    private readonly IAiConnectionTester _connectionTester;
    private readonly AiEndpointGuard _endpointGuard;
    private readonly AiConnectionOptions _environmentOptions;
    private readonly ILogger<AiSettingsService> _logger;

    /// <summary>Cuantos caracteres de la clave se muestran. Cuatro: alcanzan para reconocerla.</summary>
    private const int ApiKeyPrefixLength = 4;

    /// <summary>
    /// Id de la unica fila de configuracion. Es fijo para que dos guardados simultaneos no puedan
    /// crear dos configuraciones distintas conviviendo en la misma instalacion.
    /// </summary>
    private const int SingleRowId = 1;

    public AiSettingsService(
        AppDbContext dbContext,
        ISensitiveDataProtector sensitiveDataProtector,
        IAiConnectionResolver connectionResolver,
        IAiConnectionTester connectionTester,
        AiEndpointGuard endpointGuard,
        AiConnectionOptions environmentOptions,
        ILogger<AiSettingsService> logger)
    {
        _dbContext = dbContext;
        _sensitiveDataProtector = sensitiveDataProtector;
        _connectionResolver = connectionResolver;
        _connectionTester = connectionTester;
        _endpointGuard = endpointGuard;
        _environmentOptions = environmentOptions;
        _logger = logger;
    }

    // ============================================================
    // LEER
    // ============================================================

    public async Task<AiSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await LoadSettingsAsync(tracked: false, cancellationToken);
        var resolution = await _connectionResolver.ResolveAsync(cancellationToken);
        return BuildDto(stored, resolution);
    }

    public AiProviderPresetsResponse GetProviderPresets()
    {
        var providers = AiProviderPresets.All
            .Select(preset => new AiProviderPresetDto
            {
                Code = preset.Code,
                DisplayName = preset.DisplayName,
                Tagline = preset.Tagline,
                BaseUrl = preset.BaseUrl,
                Model = preset.Model,
                IsRecommended = preset.IsRecommended,
                RequiresManualEndpoint = preset.RequiresManualEndpoint,
            })
            .ToList();

        return new AiProviderPresetsResponse { Providers = providers };
    }

    // ============================================================
    // GUARDAR
    // ============================================================

    public async Task<AiSettingsDto> UpdateAsync(
        UpdateAiSettingsRequest request,
        string? updatedByUserId,
        string? updatedByUserName,
        CancellationToken cancellationToken)
    {
        var preset = AiProviderPresets.FindByCode(request.ProviderCode)
            ?? throw new ValidationException("Elegí una de las opciones de la lista.");

        var baseUrl = FirstNonEmpty(request.BaseUrl, preset.BaseUrl);
        var model = FirstNonEmpty(request.Model, preset.Model);

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            // Solo puede pasar con "Otra": los demas traen valores recomendados.
            throw new ValidationException("Completá la dirección y el modelo para poder guardar.");
        }

        // Misma revision de direccion que usa el probador: la configuracion guardada tampoco puede
        // terminar apuntando a la red interna del servidor (si no, el candado del probador seria
        // inutil: alcanzaria con guardar la direccion interna y esperar a que la use el sistema).
        //
        // OJO: "no se pudo resolver el nombre" NO bloquea el guardado. Ese veredicto tambien aparece
        // cuando el servidor se quedo sin resolucion de nombres por un rato, y dejar al dueño sin
        // poder guardar por un problema pasajero de red seria peor que guardar una direccion que
        // quizas no exista (si no existe, la prueba de conexion se lo va a decir).
        var addressVerdict = await _endpointGuard.CheckAsync(baseUrl, cancellationToken);
        if (addressVerdict is AiEndpointVerdict.Malformed or AiEndpointVerdict.PrivateOrInternal)
        {
            throw new ValidationException(
                "Esa dirección no sirve. Tiene que ser una dirección de internet que empiece con https.");
        }

        var settings = await LoadSettingsAsync(tracked: true, cancellationToken);
        var isNewRow = settings == null;
        settings ??= new AiSettings();

        var hadStoredKey = settings.HasStoredApiKey();
        var providerChanged = settings.Provider != preset.Provider;
        var newApiKey = SanitizeApiKey(request.ApiKey);

        if (string.IsNullOrEmpty(newApiKey))
        {
            // No vino clave nueva: se conserva la guardada. Pero si no habia ninguna, o si cambio el
            // proveedor (la clave vieja es de OTRO proveedor y no va a funcionar), hay que pedirla.
            if (!hadStoredKey || providerChanged)
            {
                // Con "Otra" no hay nombre de proveedor que nombrar ("Pegá la clave de Otra" no se
                // entiende), asi que ahi el pedido va sin nombre.
                //
                // Va con CODIGO (T-13): la pantalla necesita reconocer ESTE rechazo para dejar el
                // foco en el casillero de la clave, y antes lo reconocia mirando como empezaba la
                // frase. El texto en criollo NO cambia; lo que se agrega es el codigo al lado.
                throw new CodedValidationException(
                    ValidationCodes.AiApiKeyMissing,
                    preset.RequiresManualEndpoint
                        ? "Pegá la clave para poder usarla."
                        : $"Pegá la clave de {preset.DisplayName} para poder usarla.");
            }
        }
        else
        {
            settings.EncryptedApiKey = ProtectApiKeyOrFail(newApiKey);
            settings.ApiKeyPrefix = BuildPrefix(newApiKey);
            // La clave cambio: lo que decia la ultima prueba ya no vale para esta configuracion.
            settings.LastTestOutcome = null;
            settings.LastTestAt = null;
        }

        if (providerChanged
            || !string.Equals(settings.BaseUrl, baseUrl, StringComparison.Ordinal)
            || !string.Equals(settings.Model, model, StringComparison.Ordinal))
        {
            // Cambio a que proveedor / modelo se le habla: la prueba anterior tampoco describe a
            // esta configuracion. Se limpia para que la foto de arriba no mienta.
            settings.LastTestOutcome = null;
            settings.LastTestAt = null;
        }

        settings.Provider = preset.Provider;
        settings.BaseUrl = baseUrl.Trim();
        settings.Model = model.Trim();
        settings.UpdatedByUserId = updatedByUserId;
        settings.UpdatedByUserName = updatedByUserName;
        settings.UpdatedAt = DateTime.UtcNow;

        if (isNewRow)
        {
            // Id FIJO en 1: esta configuracion es UNA sola por instalacion. Sin esto, dos
            // administradores guardando en el mismo instante crearian DOS filas y despues el sistema
            // usaria la primera por Id — es decir, uno de los dos cambios quedaria aplicado a medias
            // y sin que nadie se entere. Con el Id fijo, el segundo choca contra la clave primaria y
            // falla ruidosamente en vez de dejar dos configuraciones conviviendo.
            settings.Id = SingleRowId;
            _dbContext.AiSettings.Add(settings);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);

        // Log sin un solo dato sensible: quien y con cual proveedor. La clave no aparece nunca.
        _logger.LogInformation(
            "Configuracion de IA actualizada por {UserId} (proveedor {Provider}).",
            updatedByUserId ?? "desconocido",
            preset.Code);

        // M-30: no hay cache que invalidar porque el resolver relee siempre la fila autoritativa.
        // La foto que devolvemos se arma volviendo a resolver, asi el que guardo ve el estado REAL
        // resultante (incluido el caso "guarde algo incompleto y sigue mandando el respaldo").
        var resolution = await _connectionResolver.ResolveAsync(cancellationToken);
        return BuildDto(settings, resolution);
    }

    /// <summary>
    /// Cifra la clave, o corta el guardado con un mensaje en criollo.
    ///
    /// <para><b>Cuando puede fallar</b>: si al servidor le falta la llave de cifrado
    /// (<c>Security__EncryptionKey</c>). Guardar la clave en claro "para salir del paso" NO es una
    /// opcion, y un error tecnico crudo tampoco puede llegar a la pantalla (regla P-17).</para>
    /// </summary>
    private string? ProtectApiKeyOrFail(string apiKey)
    {
        try
        {
            return _sensitiveDataProtector.ProtectString(apiKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Configuracion de IA: no se pudo cifrar la clave. Revisar Security__EncryptionKey en el servidor.");
            throw new ValidationException(
                "No se pudo guardar la clave de forma segura. Escribinos antes de volver a intentar.");
        }
    }

    // ============================================================
    // PROBAR
    // ============================================================

    public async Task<AiConnectionTestResultDto> TestConnectionAsync(
        TestAiConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(tracked: true, cancellationToken);

        var preset = AiProviderPresets.FindByCode(request.ProviderCode)
            ?? (settings != null ? AiProviderPresets.FindByProvider(settings.Provider) : AiProviderPresets.Default);

        var baseUrl = FirstNonEmpty(request.BaseUrl, settings?.BaseUrl, preset.BaseUrl);
        var model = FirstNonEmpty(request.Model, settings?.Model, preset.Model);
        var key = ResolveProbeApiKey(SanitizeApiKey(request.ApiKey), settings, preset, baseUrl);

        if (string.IsNullOrEmpty(key.Value))
        {
            // No hay clave para esta prueba, o la unica clave que tenemos guardada NO le corresponde
            // a la direccion que se esta probando (ver ResolveProbeApiKey). Se contesta igual que si
            // la clave no sirviera, SIN llamar a nadie: es lo mismo para el usuario y cierra la
            // puerta a usar este boton para averiguar la clave ajena.
            return new AiConnectionTestResultDto
            {
                ResultCode = AiConnectionTestCodes.InvalidKey,
                ElapsedMilliseconds = 0,
            };
        }

        var probe = new AiConnectionProbe(baseUrl, key.Value, model);
        var result = await _connectionTester.TestAsync(probe, cancellationToken);

        // La foto de estado describe a la configuracion GUARDADA. Si lo que se probo fue otra cosa
        // (una prueba antes de guardar, con otra direccion o modelo), guardar ese resultado haria
        // que el semaforo de arriba mienta. Ver ProbedTheStoredConfiguration.
        if (ProbedTheStoredConfiguration(settings, key, preset, baseUrl, model))
        {
            await RecordTestOutcomeAsync(settings!, result.ResultCode, cancellationToken);
        }

        return result;
    }

    /// <summary>La clave con la que se va a probar, y de donde salio.</summary>
    private readonly record struct ProbeKey(string? Value, AiConfigurationSource Source);

    /// <summary>
    /// Que clave se usa para probar.
    ///
    /// <list type="number">
    ///   <item>La que vino en el pedido (aunque todavia no este guardada): es la gracia de probar
    ///   antes de romper lo que funcionaba.</item>
    ///   <item>Si el pedido no trae ninguna, la GUARDADA — pero <b>solo si la prueba es contra el
    ///   mismo proveedor y la misma direccion de esa clave</b>.</item>
    ///   <item>Si no, la que dejo el tecnico en el servidor, con la misma condicion de direccion.</item>
    /// </list>
    ///
    /// <para><b>Por que esa condicion (agujero real que cierra)</b>: sin ella, un Admin podia elegir
    /// "Otra", poner la direccion de un servidor propio, dejar el campo de la clave vacio y apretar
    /// "Probar conexion" — y el sistema le mandaba la clave del proveedor de la agencia a ese
    /// servidor. Es decir, el boton servia para sacarle la clave al sistema. Ahora la clave guardada
    /// solo se reusa contra la direccion a la que pertenece.</para>
    /// </summary>
    private ProbeKey ResolveProbeApiKey(
        string? requestApiKey,
        AiSettings? settings,
        AiProviderPreset preset,
        string? baseUrl)
    {
        if (!string.IsNullOrWhiteSpace(requestApiKey))
        {
            return new ProbeKey(requestApiKey, AiConfigurationSource.None);
        }

        var storedBelongsToThisProbe =
            settings != null
            && settings.HasStoredApiKey()
            && settings.Provider == preset.Provider
            && SameAddress(settings.BaseUrl, baseUrl);

        if (storedBelongsToThisProbe)
        {
            try
            {
                var stored = _sensitiveDataProtector.UnprotectString(settings!.EncryptedApiKey);
                if (!string.IsNullOrWhiteSpace(stored))
                {
                    return new ProbeKey(SanitizeApiKey(stored), AiConfigurationSource.Database);
                }
            }
            catch (Exception ex)
            {
                // Sin llave de cifrado del servidor no se puede leer lo guardado. Se avisa por log
                // (sin la clave) y se sigue: quizas el respaldo del servidor sirve.
                _logger.LogError(ex, "Prueba de conexion de IA: no se pudo leer la clave guardada.");
            }
        }

        var environmentBelongsToThisProbe =
            !string.IsNullOrWhiteSpace(_environmentOptions.ApiKey)
            && SameAddress(_environmentOptions.BaseUrl, baseUrl);

        return environmentBelongsToThisProbe
            ? new ProbeKey(SanitizeApiKey(_environmentOptions.ApiKey), AiConfigurationSource.Environment)
            : new ProbeKey(null, AiConfigurationSource.None);
    }

    /// <summary>
    /// ¿Lo que se probo es EXACTAMENTE la configuracion guardada? Hacen falta las cuatro cosas: la
    /// clave usada tiene que ser la guardada, y proveedor, direccion y modelo tienen que coincidir
    /// con la fila. Solo en ese caso el resultado describe lo que la pantalla muestra arriba.
    /// </summary>
    private static bool ProbedTheStoredConfiguration(
        AiSettings? settings,
        ProbeKey key,
        AiProviderPreset preset,
        string? baseUrl,
        string? model)
    {
        return settings != null
            && key.Source == AiConfigurationSource.Database
            && settings.Provider == preset.Provider
            && SameAddress(settings.BaseUrl, baseUrl)
            && string.Equals(settings.Model?.Trim(), model?.Trim(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Guarda COMO le fue a la prueba y cuando. Es lo unico que una prueba escribe: ni la direccion,
    /// ni el modelo, ni la clave que se probaron (probar no guarda la configuracion, §15.9). Nunca
    /// crea la fila: si no hay configuracion guardada, no hay foto de estado que actualizar.
    /// </summary>
    private async Task RecordTestOutcomeAsync(
        AiSettings settings,
        string resultCode,
        CancellationToken cancellationToken)
    {
        settings.LastTestOutcome = MapToOutcome(resultCode);
        settings.LastTestAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    // ============================================================
    // Armado de la foto (lo unico que sale por la API)
    // ============================================================

    private AiSettingsDto BuildDto(AiSettings? settings, AiConnectionResolution? resolution)
    {
        var source = resolution?.Source ?? AiConfigurationSource.None;
        var preset = ResolveDisplayPreset(settings, resolution);

        var dto = new AiSettingsDto
        {
            ProviderCode = preset.Code,
            ProviderDisplayName = preset.DisplayName,
            BaseUrl = FirstNonEmpty(settings?.BaseUrl, resolution?.Options.BaseUrl, preset.BaseUrl) ?? string.Empty,
            Model = FirstNonEmpty(settings?.Model, resolution?.Options.Model, preset.Model) ?? string.Empty,
            HasApiKey = source != AiConfigurationSource.None,
            ApiKeySource = source switch
            {
                AiConfigurationSource.Database => AiApiKeySources.Saved,
                AiConfigurationSource.Environment => AiApiKeySources.Server,
                _ => AiApiKeySources.None,
            },
            // Del respaldo del servidor NO se muestra prefijo: esa clave no la cargo el dueño, no
            // tiene por que reconocerla, y no hay motivo para exponer ni un pedazo.
            ApiKeyPrefix = source == AiConfigurationSource.Database ? settings?.ApiKeyPrefix : null,
            LastTestCode = MapToCode(settings?.LastTestOutcome),
            LastTestAt = settings?.LastTestAt,
            UpdatedByUserName = settings?.UpdatedByUserName,
            UpdatedAt = settings?.UpdatedAt,
        };

        dto.StatusCode = ResolveStatusCode(source, settings?.LastTestOutcome);
        return dto;
    }

    /// <summary>
    /// La foto de arriba, en una linea (§15.5): sin configurar / funcionando / configurada pero la
    /// ultima prueba no anduvo. El ambar aparece SOLO si la ultima prueba guardada fallo.
    /// </summary>
    private static string ResolveStatusCode(AiConfigurationSource source, AiConnectionTestOutcome? lastTestOutcome)
    {
        if (source == AiConfigurationSource.None)
        {
            return AiSettingsStatusCodes.NotConfigured;
        }

        if (lastTestOutcome.HasValue && lastTestOutcome.Value != AiConnectionTestOutcome.Ok)
        {
            return AiSettingsStatusCodes.LastTestFailed;
        }

        return AiSettingsStatusCodes.Working;
    }

    /// <summary>
    /// Con que nombre se muestra el proveedor. Si lo eligio el dueño, ese. Si la configuracion la
    /// dejo el tecnico por el servidor, se reconoce por la direccion (asi la linea dice "Funcionando
    /// con Groq" en vez de un generico), y si no coincide con ninguno conocido, queda "Otra".
    /// </summary>
    private static AiProviderPreset ResolveDisplayPreset(AiSettings? settings, AiConnectionResolution? resolution)
    {
        if (settings != null && !string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return AiProviderPresets.FindByProvider(settings.Provider);
        }

        if (resolution != null && resolution.Source == AiConfigurationSource.Environment)
        {
            var matched = AiProviderPresets.All.FirstOrDefault(candidate =>
                !string.IsNullOrEmpty(candidate.BaseUrl)
                && resolution.Options.BaseUrl.StartsWith(candidate.BaseUrl, StringComparison.OrdinalIgnoreCase));

            return matched ?? AiProviderPresets.FindByProvider(AiProviderKey.Other);
        }

        return settings != null
            ? AiProviderPresets.FindByProvider(settings.Provider)
            : AiProviderPresets.Default;
    }

    // ============================================================
    // Ayudas chicas
    // ============================================================

    private async Task<AiSettings?> LoadSettingsAsync(bool tracked, CancellationToken cancellationToken)
    {
        var query = _dbContext.AiSettings.AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query
            .OrderBy(settings => settings.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Los primeros 4 caracteres de la clave, que es lo unico mostrable.
    ///
    /// <para>Si la clave tuviera 4 caracteres o menos, NO se guarda ningun prefijo: mostrar "los
    /// primeros 4" de una clave de 4 seria mostrar la clave entera.</para>
    /// </summary>
    private static string? BuildPrefix(string apiKey)
    {
        var trimmed = apiKey.Trim();
        return trimmed.Length <= ApiKeyPrefixLength ? null : trimmed[..ApiKeyPrefixLength];
    }

    /// <summary>
    /// Limpia la clave antes de usarla. La regla vive en <see cref="AiApiKeySanitizer"/> porque la
    /// necesitan tambien el probador de conexion y el que habla con el modelo.
    /// </summary>
    private static string? SanitizeApiKey(string? apiKey) => AiApiKeySanitizer.Sanitize(apiKey);

    /// <summary>
    /// ¿Son la misma direccion? Se comparan sin distinguir mayusculas y sin la barra final, que es
    /// la diferencia mas comun al copiar y pegar y no cambia a donde se apunta.
    /// </summary>
    private static bool SameAddress(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            left.Trim().TrimEnd('/'),
            right.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return null;
    }

    private static AiConnectionTestOutcome MapToOutcome(string resultCode) => resultCode switch
    {
        AiConnectionTestCodes.Ok => AiConnectionTestOutcome.Ok,
        AiConnectionTestCodes.InvalidKey => AiConnectionTestOutcome.InvalidKey,
        AiConnectionTestCodes.InvalidAddress => AiConnectionTestOutcome.InvalidAddress,
        AiConnectionTestCodes.ModelNotFound => AiConnectionTestOutcome.ModelNotFound,
        _ => AiConnectionTestOutcome.NoResponse,
    };

    private static string? MapToCode(AiConnectionTestOutcome? outcome) => outcome switch
    {
        AiConnectionTestOutcome.Ok => AiConnectionTestCodes.Ok,
        AiConnectionTestOutcome.InvalidKey => AiConnectionTestCodes.InvalidKey,
        AiConnectionTestOutcome.InvalidAddress => AiConnectionTestCodes.InvalidAddress,
        AiConnectionTestOutcome.ModelNotFound => AiConnectionTestCodes.ModelNotFound,
        AiConnectionTestOutcome.NoResponse => AiConnectionTestCodes.NoResponse,
        _ => null,
    };
}
