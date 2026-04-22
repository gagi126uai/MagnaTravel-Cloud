using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace TravelApi.Infrastructure.Services.ReservationsServiceProxy;

public abstract class ReservationsServiceHttpProxyBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    protected readonly HttpClient HttpClient;

    protected ReservationsServiceHttpProxyBase(HttpClient httpClient)
    {
        HttpClient = httpClient;
    }

    protected async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(path, cancellationToken);
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    protected async Task<byte[]> GetBytesAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForResponseAsync(response, cancellationToken);
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    protected async Task<(byte[] Bytes, string ContentType, string FileName)> GetFileAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.GetAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForResponseAsync(response, cancellationToken);
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? "download.bin";

        return (bytes, contentType, fileName.Trim('"'));
    }

    protected async Task<T> PostAsync<TRequest, T>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.PostAsJsonAsync(path, payload, JsonOptions, cancellationToken);
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    protected async Task<T> PutAsync<TRequest, T>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.PutAsJsonAsync(path, payload, JsonOptions, cancellationToken);
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    protected async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.DeleteAsync(path, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForResponseAsync(response, cancellationToken);
        }
    }

    protected async Task<T> PostMultipartAsync<T>(string path, MultipartFormDataContent content, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.PostAsync(path, content, cancellationToken);
        return await ReadResponseAsync<T>(response, cancellationToken);
    }

    protected async Task<JsonDocument> PostForDocumentAsync<TRequest>(string path, TRequest payload, CancellationToken cancellationToken)
    {
        using var response = await HttpClient.PostAsJsonAsync(path, payload, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForResponseAsync(response, cancellationToken);
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    protected static string WithQuery(string path, object query)
    {
        var parameters = new List<KeyValuePair<string, string?>>();
        foreach (var property in query.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var value = property.GetValue(query);
            if (value == null)
            {
                continue;
            }

            if (value is string text)
            {
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parameters.Add(new KeyValuePair<string, string?>(property.Name, text));
                }

                continue;
            }

            if (value is IEnumerable<string> many)
            {
                foreach (var item in many.Where(item => !string.IsNullOrWhiteSpace(item)))
                {
                    parameters.Add(new KeyValuePair<string, string?>(property.Name, item));
                }

                continue;
            }

            parameters.Add(new KeyValuePair<string, string?>(property.Name, ConvertToString(value)));
        }

        return parameters.Count == 0 ? path : QueryHelpers.AddQueryString(path, parameters);
    }

    protected static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            await ThrowForResponseAsync(response, cancellationToken);
        }

        var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
        return result ?? throw new InvalidOperationException($"Reservations service returned an empty payload for {typeof(T).Name}.");
    }

    protected static async Task ThrowForResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var message = string.IsNullOrWhiteSpace(body)
            ? $"Reservations service request failed with status {(int)response.StatusCode}."
            : body;

        throw response.StatusCode switch
        {
            HttpStatusCode.NotFound => new KeyNotFoundException(message),
            HttpStatusCode.BadRequest => new ArgumentException(message),
            HttpStatusCode.Conflict => new InvalidOperationException(message),
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => new UnauthorizedAccessException(message),
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable => new HttpRequestException(message, null, response.StatusCode),
            _ => new HttpRequestException(message, null, response.StatusCode)
        };
    }

    private static string? ConvertToString(object value)
    {
        return value switch
        {
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            Enum enumValue => enumValue.ToString(),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)
        };
    }
}
