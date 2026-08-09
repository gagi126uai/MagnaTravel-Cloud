using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Ai;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Ai;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Piezas compartidas por los tests de la configuracion de IA: base en memoria, el cifrador REAL
/// (no un remedo: queremos comprobar que lo guardado no se puede leer de un vistazo), un probador
/// de mentira que no toca internet, y un revisor de direcciones con resolucion de nombres falsa
/// (para no depender de que la maquina que corre la suite tenga salida a internet).
/// </summary>
internal static class AiTestDoubles
{
    public static AppDbContext BuildDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>
    /// El cifrador de verdad, con una llave de prueba. Sirve para verificar que lo que queda en la
    /// base NO es la clave en claro y que se puede volver a leer solo con la llave del servidor.
    /// </summary>
    public static ISensitiveDataProtector BuildRealProtector()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:EncryptionKey"] = "llave-de-prueba-para-los-tests-de-ia-32+",
            })
            .Build();

        var environment = new Mock<IHostEnvironment>().Object;
        return new SensitiveDataProtector(
            configuration,
            environment,
            NullLogger<SensitiveDataProtector>.Instance);
    }

    /// <summary>
    /// El mismo cifrador, pero como queda en un servidor al que le falta la llave de cifrado
    /// (<c>Security__EncryptionKey</c>) y que NO es de desarrollo: ahi cifrar tiene que fallar, no
    /// guardar la clave en claro por las dudas.
    /// </summary>
    public static ISensitiveDataProtector BuildProtectorWithoutServerKey()
    {
        var configuration = new ConfigurationBuilder().Build();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(env => env.EnvironmentName).Returns("Production");

        return new SensitiveDataProtector(
            configuration,
            environment.Object,
            NullLogger<SensitiveDataProtector>.Instance);
    }

    /// <summary>
    /// Revisor de direcciones con nombres resueltos a mano. Los nombres que no esten en el mapa se
    /// resuelven a una direccion publica, asi los tests que no hablan de seguridad no se traban.
    /// </summary>
    public static AiEndpointGuard BuildGuard(Dictionary<string, string>? hostToAddress = null)
    {
        var map = hostToAddress ?? new Dictionary<string, string>();

        return new AiEndpointGuard((host, _) =>
        {
            var address = map.TryGetValue(host, out var mapped) ? mapped : "93.184.216.34";
            return Task.FromResult(new[] { IPAddress.Parse(address) });
        });
    }

    public static AiConnectionOptions EmptyEnvironmentOptions() => new();

    public static AiConnectionOptions EnvironmentOptions(string baseUrl, string apiKey, string model) => new()
    {
        BaseUrl = baseUrl,
        ApiKey = apiKey,
        Model = model,
    };

    public static AiConnectionResolver BuildResolver(
        AppDbContext db,
        ISensitiveDataProtector protector,
        AiConnectionOptions environmentOptions) =>
        new(db, protector, environmentOptions, NullLogger<AiConnectionResolver>.Instance);

    public static AiSettingsService BuildSettingsService(
        AppDbContext db,
        ISensitiveDataProtector protector,
        IAiConnectionTester tester,
        AiConnectionOptions environmentOptions,
        AiEndpointGuard? guard = null)
    {
        var resolver = BuildResolver(db, protector, environmentOptions);
        return new AiSettingsService(
            db,
            protector,
            resolver,
            tester,
            guard ?? BuildGuard(),
            environmentOptions,
            NullLogger<AiSettingsService>.Instance);
    }
}

/// <summary>
/// Probador de mentira: no sale a internet. Guarda con que datos lo llamaron (para verificar, por
/// ejemplo, que uso la clave guardada cuando el pedido no traia una) y devuelve el codigo pactado.
/// </summary>
internal sealed class FakeAiConnectionTester : IAiConnectionTester
{
    private readonly string _resultCode;

    public AiConnectionProbe? LastProbe { get; private set; }
    public int CallCount { get; private set; }

    public FakeAiConnectionTester(string resultCode = AiConnectionTestCodes.Ok)
    {
        _resultCode = resultCode;
    }

    public Task<AiConnectionTestResultDto> TestAsync(AiConnectionProbe probe, CancellationToken cancellationToken)
    {
        CallCount++;
        LastProbe = probe;
        return Task.FromResult(new AiConnectionTestResultDto
        {
            ResultCode = _resultCode,
            ElapsedMilliseconds = 42,
        });
    }
}
