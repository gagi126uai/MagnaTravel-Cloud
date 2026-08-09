using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// El boton "Probar conexion" del lado del motor (M-31). Manda el saludo mas barato posible al
/// proveedor y traduce TODO lo que pueda pasar a uno de cinco codigos + cuanto tardo.
///
/// <para><b>Regla dura (P-17 + gate de exposicion)</b>: de aca sale un codigo y un numero de
/// milisegundos. Nunca el texto del proveedor, nunca el numero de error HTTP, nunca el nombre de
/// una clase o de un servicio. Lo tecnico va al log del servidor, que lo lee un tecnico.</para>
///
/// <para><b>Y antes de conectar, se revisa la direccion</b> (ver <see cref="AiEndpointGuard"/>): si
/// no es una direccion publica de internet en https, ni se intenta. Sin eso, este boton seria una
/// sonda para espiar la red interna del servidor.</para>
/// </summary>
public sealed class AiConnectionTester : IAiConnectionTester
{
    private readonly HttpClient _httpClient;
    private readonly AiEndpointGuard _endpointGuard;
    private readonly ILogger<AiConnectionTester> _logger;

    /// <summary>
    /// Cuanto se espera antes de dar por muerto al proveedor. Corto a proposito: es una prueba
    /// interactiva, el usuario esta mirando la pantalla.
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(12);

    public AiConnectionTester(
        HttpClient httpClient,
        AiEndpointGuard endpointGuard,
        ILogger<AiConnectionTester> logger)
    {
        _httpClient = httpClient;
        _endpointGuard = endpointGuard;
        _logger = logger;
    }

    public async Task<AiConnectionTestResultDto> TestAsync(
        AiConnectionProbe probe,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        var addressVerdict = await _endpointGuard.CheckAsync(probe.BaseUrl, cancellationToken);
        if (addressVerdict != AiEndpointVerdict.Ok)
        {
            // "No resuelve" y "apunta a la red interna" se le cuentan igual al usuario: la
            // direccion no sirve. Distinguirlos en pantalla no lo ayuda y le confirmaria a un
            // curioso que ese nombre interno existe.
            _logger.LogWarning(
                "Prueba de conexion de IA rechazada por la direccion. Veredicto interno: {Verdict}.",
                addressVerdict);
            return Result(AiConnectionTestCodes.InvalidAddress, stopwatch);
        }

        if (string.IsNullOrWhiteSpace(probe.ApiKey))
        {
            // Sin clave no hay nada que probar, y mandar el pedido igual solo gastaria un viaje
            // para recibir el mismo 401.
            return Result(AiConnectionTestCodes.InvalidKey, stopwatch);
        }

        if (string.IsNullOrWhiteSpace(probe.Model))
        {
            return Result(AiConnectionTestCodes.ModelNotFound, stopwatch);
        }

        try
        {
            var code = await ProbeProviderAsync(probe, cancellationToken);
            return Result(code, stopwatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // El que pidio la prueba se fue (cerro la pantalla). No es un resultado: se propaga.
            throw;
        }
        catch (TaskCanceledException)
        {
            // Sin cancelacion del llamador, esto es el reloj: el proveedor no contesto a tiempo.
            return Result(AiConnectionTestCodes.NoResponse, stopwatch);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Prueba de conexion de IA: falla de red. Motivo interno: {Message}", ex.Message);
            return Result(AiConnectionTestCodes.NoResponse, stopwatch);
        }
        catch (UriFormatException)
        {
            return Result(AiConnectionTestCodes.InvalidAddress, stopwatch);
        }
        catch (FormatException ex)
        {
            // Armar la cabecera con la clave puede fallar si la clave tiene caracteres que no
            // entran en una cabecera HTTP. Se atrapa ACA a proposito: si se escapara, el mensaje de
            // la excepcion (que puede contener la clave) terminaria en el manejador global.
            // OJO: va DESPUES de UriFormatException, que hereda de esta y significa otra cosa.
            _logger.LogWarning("Prueba de conexion de IA: la clave no se pudo usar. Motivo interno: {Type}", ex.GetType().Name);
            return Result(AiConnectionTestCodes.InvalidKey, stopwatch);
        }
    }

    /// <summary>
    /// El saludo minimo: un mensaje de una palabra y un tope de respuesta de 1, que es lo mas
    /// barato que se le puede pedir a cualquier proveedor compatible con OpenAI.
    /// </summary>
    private async Task<string> ProbeProviderAsync(AiConnectionProbe probe, CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"{probe.BaseUrl!.Trim().TrimEnd('/')}/chat/completions");

        var payload = JsonSerializer.Serialize(new
        {
            model = probe.Model,
            messages = new[] { new { role = "user", content = "ping" } },
            max_tokens = 1,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        // La clave viaja en la cabecera. OJO: esta cabecera NUNCA se loguea (el cliente tipado esta
        // configurado para tachar "Authorization" en los logs de HttpClient). Se le sacan saltos de
        // linea y espacios: una cabecera con un salto adentro es un pedido invalido.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            AiApiKeySanitizer.Sanitize(probe.ApiKey));

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(ProbeTimeout);

        using var response = await _httpClient.SendAsync(request, timeoutSource.Token);

        if (response.IsSuccessStatusCode)
        {
            return AiConnectionTestCodes.Ok;
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized
            || response.StatusCode == HttpStatusCode.Forbidden)
        {
            return AiConnectionTestCodes.InvalidKey;
        }

        // Modelo inexistente: la mayoria contesta 404, y varios contestan 400 nombrando el modelo
        // en el cuerpo. Miramos el cuerpo SOLO para decidir el codigo; no sale de este metodo.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return AiConnectionTestCodes.ModelNotFound;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest
            && await MentionsModelAsync(response, timeoutSource.Token))
        {
            return AiConnectionTestCodes.ModelNotFound;
        }

        _logger.LogWarning(
            "Prueba de conexion de IA: el proveedor respondio HTTP {StatusCode}.",
            (int)response.StatusCode);
        return AiConnectionTestCodes.NoResponse;
    }

    /// <summary>
    /// ¿El proveedor se quejo del modelo? Se lee el cuerpo (acotado) solo para elegir el codigo
    /// correcto. El texto leido no se devuelve ni se loguea: puede traer datos del proveedor.
    /// </summary>
    private static async Task<bool> MentionsModelAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrEmpty(body))
            {
                return false;
            }

            var head = body.Length > 2000 ? body[..2000] : body;
            return head.Contains("model", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // Si ni el cuerpo se puede leer, no vale la pena insistir: queda "no responde".
            return false;
        }
    }

    private static AiConnectionTestResultDto Result(string code, Stopwatch stopwatch)
    {
        stopwatch.Stop();
        return new AiConnectionTestResultDto
        {
            ResultCode = code,
            ElapsedMilliseconds = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue),
        };
    }
}
