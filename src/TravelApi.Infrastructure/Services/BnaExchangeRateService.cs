using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class BnaExchangeRateService : IBnaExchangeRateService
{
    private const string CacheKey = "dashboard:bna-usd-seller";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Uri SourceUri = new("https://www.bna.com.ar/personas");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BnaExchangeRateService> _logger;

    public BnaExchangeRateService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<BnaExchangeRateService> logger)
    {
        _httpClientFactory = httpClientFactory;
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
            _cache.Set(CacheKey, freshSnapshot, TimeSpan.FromHours(12));
            return freshSnapshot;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo obtener la cotizacion oficial del dolar vendedor BNA.");

            if (_cache.TryGetValue(CacheKey, out BnaUsdSellerRateDto? fallbackSnapshot) && fallbackSnapshot != null)
            {
                return fallbackSnapshot with { IsStale = true };
            }

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
        return ParseSnapshot(html) with
        {
            Source = SourceUri.AbsoluteUri,
            IsStale = false,
            FetchedAt = DateTime.UtcNow
        };
    }

    private static BnaUsdSellerRateDto ParseSnapshot(string html)
    {
        var normalizedText = NormalizeHtmlToSearchableText(html);
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

        return new BnaUsdSellerRateDto(
            Value: ParsePesoAmount(usdMatch.Groups["sell"].Value),
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
}
