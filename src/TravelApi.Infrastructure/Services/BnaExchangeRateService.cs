using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Services;

public class BnaExchangeRateService : IBnaExchangeRateService
{
    private const string CacheKey = "dashboard:bna-usd-seller";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly Uri SourceUri = new("https://www.bna.com.ar/Personas");

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
        var normalized = WebUtility.HtmlDecode(html);
        normalized = Regex.Replace(normalized, @"\s+", " ");

        var match = Regex.Match(
            normalized,
            @"Cotizaci[oó]n Billetes.*?(\d{1,2}/\d{1,2}/\d{4}).*?D[oó]lar U\.S\.A\s+([0-9.,]+)\s+([0-9.,]+).*?Hora Actualizaci[oó]n:\s*([0-9]{1,2}:[0-9]{2})",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (!match.Success)
        {
            throw new InvalidOperationException("No se pudo parsear la cotizacion de Banco Nacion.");
        }

        return new BnaUsdSellerRateDto(
            Value: ParsePesoAmount(match.Groups[3].Value),
            PublishedDate: match.Groups[1].Value,
            PublishedTime: match.Groups[4].Value,
            Source: SourceUri.AbsoluteUri,
            IsStale: false,
            FetchedAt: DateTime.UtcNow);
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
