using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// Decide con que datos se habla con la inteligencia artificial AHORA (M-29 + M-30).
///
/// <para><b>La precedencia, en una linea</b>: manda lo que el dueño cargo en la pantalla; las
/// variables del servidor (<c>Ai__*</c>) son el respaldo para cuando no hay nada cargado; si no hay
/// ninguna de las dos, no hay IA y el sistema anda igual, sin las ayudas.</para>
///
/// <para><b>Por que "completa o nada"</b>: la configuracion de la base se usa solo si tiene las tres
/// cosas (direccion, modelo y clave). No se mezcla media de la base con media del servidor, porque
/// una clave de un proveedor con la direccion de otro no funciona y daria un error incomprensible.</para>
///
/// <para><b>Sin cache a proposito (M-30)</b>: cada llamada relee la fila. Es una lectura de una fila
/// antes de una llamada por internet de cientos de milisegundos, asi que no se nota; y a cambio no
/// existe la posibilidad de seguir usando una clave vieja despues de guardar una nueva, que es
/// exactamente el bug que dejo la leccion del cache de AfipSettings.</para>
/// </summary>
public sealed class AiConnectionResolver : IAiConnectionResolver
{
    private readonly AppDbContext _dbContext;
    private readonly ISensitiveDataProtector _sensitiveDataProtector;
    private readonly AiConnectionOptions _environmentOptions;
    private readonly ILogger<AiConnectionResolver> _logger;

    public AiConnectionResolver(
        AppDbContext dbContext,
        ISensitiveDataProtector sensitiveDataProtector,
        AiConnectionOptions environmentOptions,
        ILogger<AiConnectionResolver> logger)
    {
        _dbContext = dbContext;
        _sensitiveDataProtector = sensitiveDataProtector;
        _environmentOptions = environmentOptions;
        _logger = logger;
    }

    public async Task<AiConnectionResolution?> ResolveAsync(CancellationToken cancellationToken)
    {
        var stored = await LoadStoredSettingsAsync(cancellationToken);
        var fromDatabase = BuildFromDatabase(stored);
        if (fromDatabase != null)
        {
            return new AiConnectionResolution(fromDatabase, AiConfigurationSource.Database);
        }

        var fromEnvironment = BuildFromEnvironment();
        if (fromEnvironment != null)
        {
            return new AiConnectionResolution(fromEnvironment, AiConfigurationSource.Environment);
        }

        return null;
    }

    public async Task<bool> IsUsableAsync(CancellationToken cancellationToken)
    {
        var resolution = await ResolveAsync(cancellationToken);
        return resolution != null;
    }

    /// <summary>
    /// Lee la fila guardada, sin crearla y sin engancharla al rastreo de cambios (esto es solo
    /// lectura). Tolera que la tabla todavia no exista: en una instalacion cuyo esquema no se
    /// actualizo, la IA simplemente cae al respaldo del servidor en vez de romper toda la app.
    /// </summary>
    private async Task<AiSettings?> LoadStoredSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _dbContext.AiSettings
                .AsNoTracking()
                .OrderBy(settings => settings.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _logger.LogWarning(
                "Configuracion de IA: la tabla todavia no existe en esta base. Se usa el respaldo del servidor.");
            return null;
        }
    }

    private AiConnectionOptions? BuildFromDatabase(AiSettings? stored)
    {
        if (stored == null || !stored.IsComplete())
        {
            return null;
        }

        string? apiKey;
        try
        {
            apiKey = _sensitiveDataProtector.UnprotectString(stored.EncryptedApiKey);
        }
        catch (Exception ex)
        {
            // Falta la llave de cifrado del servidor, o el texto cifrado esta corrupto. NO se
            // propaga: la IA es una ayuda, no puede tumbar una operacion. Se avisa por log (sin la
            // clave, obviamente) y se cae al respaldo.
            _logger.LogError(ex,
                "Configuracion de IA: no se pudo descifrar la clave guardada. Se usa el respaldo del servidor.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        return new AiConnectionOptions
        {
            BaseUrl = stored.BaseUrl,
            ApiKey = apiKey,
            Model = stored.Model,
            // Los tiempos y topes siguen siendo del servidor: son ajustes tecnicos que el dueño de
            // una agencia no tiene por que decidir (P-15: no se le pregunta lo que no entiende).
            TimeoutSeconds = _environmentOptions.TimeoutSeconds,
            MaxTokens = _environmentOptions.MaxTokens,
            MaxRetries = _environmentOptions.MaxRetries,
        };
    }

    private AiConnectionOptions? BuildFromEnvironment()
    {
        if (!IsUsableConnection(_environmentOptions))
        {
            return null;
        }

        return _environmentOptions;
    }

    /// <summary>
    /// Una conexion sirve si tiene las tres cosas y la clave no es el texto de ejemplo del archivo
    /// de configuracion sin reemplazar (<c>CHANGE_THIS_*</c>), que es el error de instalacion mas
    /// comun y da un 401 confuso si se deja pasar.
    /// </summary>
    public static bool IsUsableConnection(AiConnectionOptions options) =>
        !string.IsNullOrWhiteSpace(options.BaseUrl)
        && !string.IsNullOrWhiteSpace(options.Model)
        && !string.IsNullOrWhiteSpace(options.ApiKey)
        && !options.ApiKey.Contains("CHANGE_THIS", StringComparison.OrdinalIgnoreCase);
}
