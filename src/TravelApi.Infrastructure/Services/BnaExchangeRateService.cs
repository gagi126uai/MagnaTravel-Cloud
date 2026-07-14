using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class BnaExchangeRateService : IBnaExchangeRateService
{
    private const string CacheKey = "dashboard:bna-usd-seller";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Uri SourceUri = new("https://www.bna.com.ar/personas");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BnaExchangeRateService> _logger;

    public BnaExchangeRateService(
        IHttpClientFactory httpClientFactory,
        AppDbContext dbContext,
        IMemoryCache cache,
        ILogger<BnaExchangeRateService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _dbContext = dbContext;
        _cache = cache;
        _logger = logger;
    }

    public async Task<BnaUsdSellerRateDto?> GetUsdSellerRateAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey, out BnaUsdSellerRateDto? cachedSnapshot) &&
            cachedSnapshot != null &&
            DateTime.UtcNow - cachedSnapshot.FetchedAt < CacheTtl)
        {
            return cachedSnapshot with { IsStale = false };
        }

        try
        {
            var freshSnapshot = await FetchSnapshotAsync(cancellationToken);
            await SavePersistedSnapshotAsync(freshSnapshot, cancellationToken);
            _cache.Set(CacheKey, freshSnapshot, TimeSpan.FromHours(12));
            return freshSnapshot;
        }
        catch (Exception ex)
        {
            if (_cache.TryGetValue(CacheKey, out BnaUsdSellerRateDto? fallbackSnapshot) && fallbackSnapshot != null)
            {
                _logger.LogWarning(
                    "Banco Nacion no devolvio una cotizacion valida. Se usa la ultima cotizacion en memoria de {FetchedAt:u}. Motivo: {Message}",
                    fallbackSnapshot.FetchedAt,
                    ex.Message);

                return fallbackSnapshot with { IsStale = true };
            }

            var persistedSnapshot = await LoadPersistedSnapshotAsync(cancellationToken);
            if (persistedSnapshot != null)
            {
                _cache.Set(CacheKey, persistedSnapshot, TimeSpan.FromHours(12));
                _logger.LogWarning(
                    "Banco Nacion no devolvio una cotizacion valida. Se usa la ultima cotizacion persistida de {FetchedAt:u}. Motivo: {Message}",
                    persistedSnapshot.FetchedAt,
                    ex.Message);

                return persistedSnapshot with { IsStale = true };
            }

            _logger.LogWarning(ex, "No se pudo obtener la cotizacion oficial del dolar vendedor BNA y no existe respaldo persistido.");
            return null;
        }
    }

    private async Task<BnaUsdSellerRateDto> FetchSnapshotAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        using var request = new HttpRequestMessage(HttpMethod.Get, SourceUri);
        request.Headers.UserAgent.ParseAdd("MagnaTravel/1.0");

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri ?? throw new InvalidOperationException("Banco Nacion no devolvio una URI final valida.");
        if (!string.Equals(finalUri.Host, SourceUri.Host, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Banco Nacion respondio desde un origen no permitido.");
        }

        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        if (IsOfficialTemporaryOutagePage(html))
        {
            throw new InvalidOperationException("Banco Nacion respondio con una pagina de indisponibilidad temporal.");
        }

        return ParseSnapshot(html) with
        {
            Source = SourceUri.AbsoluteUri,
            IsStale = false,
            FetchedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Lectura local del ultimo snapshot persistido, sin tocar la red. Reusa <see cref="LoadPersistedSnapshotAsync"/>
    /// (que ya marca IsStale=true). El dashboard la usa como fallback rapido cuando el fetch en vivo no responde a
    /// tiempo, asi la pantalla nunca queda esperando a Banco Nacion.
    /// </summary>
    public Task<BnaUsdSellerRateDto?> GetPersistedUsdSellerRateAsync(CancellationToken cancellationToken)
    {
        return LoadPersistedSnapshotAsync(cancellationToken);
    }

    /// <summary>
    /// ADR-044 Fix B (2026-07-13): ventana de dias hacia atras que aceptamos entre la fecha pedida y la fecha del
    /// unico snapshot guardado. 5 dias cubre findes largos/feriados (el BNA no cotiza esos dias): si el operador
    /// cobro un domingo, el ultimo BNA util es el del viernes anterior. Mas alla de la ventana, el snapshot no
    /// representa la fecha pedida y devolvemos "sin dato" (el modal cae a carga manual).
    /// </summary>
    private const int RateForDateWindowDays = 5;

    /// <summary>Formatos posibles del <c>PublishedDate</c> que scrapea el parser (d/M/yyyy con o sin cero a la izquierda).</summary>
    private static readonly string[] PublishedDateFormats =
        { "d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy", "dd/M/yyyy" };

    /// <inheritdoc />
    public async Task<BnaRateForDateDto?> GetPersistedUsdSellerRateForDateAsync(
        DateOnly requestedDate, CancellationToken cancellationToken)
    {
        // SOLO lectura del snapshot ya guardado: NO se llama a Banco Nacion en vivo (es una consulta historica).
        var snapshot = await _dbContext.BnaExchangeRateSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == BnaExchangeRateSnapshot.SingletonId, cancellationToken);

        if (snapshot is null)
            return null; // nunca se persistio una cotizacion.

        // La fecha guardada viene como texto scrapeado ("dd/MM/yyyy"). Si no se puede parsear, no arriesgamos un
        // dato incierto: sin sugerencia (el modal permite cargar el TC a mano).
        if (!DateOnly.TryParseExact(
                snapshot.PublishedDate, PublishedDateFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var publishedDate))
            return null;

        // El snapshot solo sirve como TC de la fecha pedida si es de ESA fecha o anterior (nunca un dato posterior
        // que el operador no pudo haber visto) y no mas viejo que la ventana. Como la tabla es un SINGLETON, esto
        // es lo unico que podemos ofrecer con honestidad.
        if (publishedDate > requestedDate)
            return null;
        if (requestedDate.DayNumber - publishedDate.DayNumber > RateForDateWindowDays)
            return null;

        // Cotizacion no confiable (0/negativa): sin sugerencia.
        if (snapshot.UsdSeller <= 0m)
            return null;

        return new BnaRateForDateDto(Rate: snapshot.UsdSeller, RateDate: publishedDate);
    }

    private async Task<BnaUsdSellerRateDto?> LoadPersistedSnapshotAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _dbContext.BnaExchangeRateSnapshots
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == BnaExchangeRateSnapshot.SingletonId, cancellationToken);

        if (snapshot == null)
        {
            return null;
        }

        return new BnaUsdSellerRateDto(
            Value: snapshot.UsdSeller,
            EuroValue: snapshot.EuroSeller,
            RealValue: snapshot.RealSeller,
            PublishedDate: snapshot.PublishedDate,
            PublishedTime: snapshot.PublishedTime,
            Source: snapshot.Source,
            IsStale: true,
            FetchedAt: snapshot.FetchedAt);
    }

    private async Task SavePersistedSnapshotAsync(BnaUsdSellerRateDto snapshot, CancellationToken cancellationToken)
    {
        await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "BnaExchangeRateSnapshots"
                ("Id", "UsdSeller", "EuroSeller", "RealSeller", "PublishedDate", "PublishedTime", "Source", "FetchedAt")
            VALUES
                ({BnaExchangeRateSnapshot.SingletonId}, {snapshot.Value}, {snapshot.EuroValue}, {snapshot.RealValue}, {snapshot.PublishedDate}, {snapshot.PublishedTime}, {snapshot.Source}, {snapshot.FetchedAt})
            ON CONFLICT ("Id") DO UPDATE SET
                "UsdSeller" = EXCLUDED."UsdSeller",
                "EuroSeller" = EXCLUDED."EuroSeller",
                "RealSeller" = EXCLUDED."RealSeller",
                "PublishedDate" = EXCLUDED."PublishedDate",
                "PublishedTime" = EXCLUDED."PublishedTime",
                "Source" = EXCLUDED."Source",
                "FetchedAt" = EXCLUDED."FetchedAt";
            """, cancellationToken);
    }

    private static BnaUsdSellerRateDto ParseSnapshot(string html)
    {
        var normalizedText = NormalizeHtmlToSearchableText(html);

        if (normalizedText.Contains("PAGINA NO DISPONIBLE", StringComparison.Ordinal) ||
            normalizedText.Contains("NO SE PUEDE ACCEDER AL SITIO DEL BANCO DE LA NACION ARGENTINA", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Banco Nacion respondio con una pagina de indisponibilidad temporal.");
        }

        var billetesScope = normalizedText;
        var divisasIndex = billetesScope.IndexOf("COTIZACION DIVISAS", StringComparison.Ordinal);
        if (divisasIndex > 0)
        {
            billetesScope = billetesScope[..divisasIndex];
        }

        var dateMatch = Regex.Match(billetesScope, @"\b(?<date>\d{1,2}/\d{1,2}/\d{4})\b");
        var timeMatch = Regex.Match(billetesScope, @"HORA ACTUALIZACION:\s*(?<time>\d{1,2}:\d{2})");
        var usdMatch = Regex.Match(
            billetesScope,
            @"DOLAR\s+U\.?S\.?A\.?\s+(?<buy>[0-9.,]+)\s+(?<sell>[0-9.,]+)",
            RegexOptions.Singleline);

        if (!dateMatch.Success)
        {
            dateMatch = Regex.Match(normalizedText, @"\b(?<date>\d{1,2}/\d{1,2}/\d{4})\b");
        }

        if (!timeMatch.Success)
        {
            timeMatch = Regex.Match(normalizedText, @"HORA ACTUALIZACION:\s*(?<time>\d{1,2}:\d{2})");
        }

        if (!usdMatch.Success)
        {
            usdMatch = Regex.Match(
                normalizedText,
                @"DOLAR\s+U\.?S\.?A\.?\s+(?<buy>[0-9.,]+)\s+(?<sell>[0-9.,]+)",
                RegexOptions.Singleline);
        }

        if (!dateMatch.Success || !timeMatch.Success || !usdMatch.Success)
        {
            var preview = normalizedText.Length > 320 ? normalizedText[..320] : normalizedText;
            throw new InvalidOperationException($"No se pudo parsear la cotizacion de Banco Nacion. Preview: {preview}");
        }

        var euroMatch = Regex.Match(
            billetesScope,
            @"EURO\s+(?<buy>[0-9.,]+)\s+(?<sell>[0-9.,]+)",
            RegexOptions.Singleline);

        if (!euroMatch.Success)
        {
            euroMatch = Regex.Match(
                normalizedText,
                @"EURO\s+(?<buy>[0-9.,]+)\s+(?<sell>[0-9.,]+)",
                RegexOptions.Singleline);
        }

        var realMatch = Regex.Match(
            billetesScope,
            @"REAL\s+\*?\s*(?<buy>[0-9.,]+)\s+(?<sell>[0-9.,]+)",
            RegexOptions.Singleline);

        if (!realMatch.Success)
        {
            realMatch = Regex.Match(
                normalizedText,
                @"REAL\s+\*?\s*(?<buy>[0-9.,]+)\s+(?<sell>[0-9.,]+)",
                RegexOptions.Singleline);
        }

        if (!euroMatch.Success || !realMatch.Success)
        {
            var preview = normalizedText.Length > 320 ? normalizedText[..320] : normalizedText;
            throw new InvalidOperationException($"No se pudieron parsear euro y real de Banco Nacion. Preview: {preview}");
        }

        return new BnaUsdSellerRateDto(
            Value: ParsePesoAmount(usdMatch.Groups["sell"].Value),
            EuroValue: ParsePesoAmount(euroMatch.Groups["sell"].Value),
            RealValue: ParsePesoAmount(realMatch.Groups["sell"].Value),
            PublishedDate: dateMatch.Groups["date"].Value,
            PublishedTime: timeMatch.Groups["time"].Value,
            Source: SourceUri.AbsoluteUri,
            IsStale: false,
            FetchedAt: DateTime.UtcNow);
    }

    private static string NormalizeHtmlToSearchableText(string html)
    {
        var decoded = WebUtility.HtmlDecode(html ?? string.Empty)
            .Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);

        decoded = Regex.Replace(decoded, @"<script\b[^>]*>.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        decoded = Regex.Replace(decoded, @"<style\b[^>]*>.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        decoded = Regex.Replace(decoded, @"<[^>]+>", " ");

        var decomposed = decoded.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(character));
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static decimal ParsePesoAmount(string rawValue)
    {
        if (decimal.TryParse(rawValue, NumberStyles.Number, CultureInfo.GetCultureInfo("es-AR"), out var parsed))
        {
            return parsed;
        }

        if (decimal.TryParse(rawValue.Replace(".", "").Replace(",", "."), NumberStyles.Number, CultureInfo.InvariantCulture, out parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException("No se pudo interpretar el valor de la cotizacion BNA.");
    }

    private static bool IsOfficialTemporaryOutagePage(string html)
    {
        var normalizedText = NormalizeHtmlToSearchableText(html);
        return normalizedText.Contains("PAGINA NO DISPONIBLE", StringComparison.Ordinal) ||
               normalizedText.Contains("NO SE PUEDE ACCEDER AL SITIO DEL BANCO DE LA NACION ARGENTINA", StringComparison.Ordinal);
    }
}
