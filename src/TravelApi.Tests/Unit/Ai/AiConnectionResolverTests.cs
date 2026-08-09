using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.Ai;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using Xunit;

namespace TravelApi.Tests.Unit.Ai;

/// <summary>
/// Quien manda cuando hay configuracion en los dos lados (M-29, adenda firmada a ADR-016 del
/// 2026-08-07): <b>lo que cargo el dueño en la pantalla le gana a lo que dejo el tecnico en el
/// servidor</b>. El servidor es el respaldo.
/// </summary>
public class AiConnectionResolverTests
{
    private static AiSettings StoredGroq(ISensitiveDataProtector protector, string apiKey = "gsk_de_la_pantalla") => new()
    {
        Provider = AiProviderKey.Groq,
        BaseUrl = "https://api.groq.com/openai/v1",
        Model = "llama-3.3-70b-versatile",
        EncryptedApiKey = protector.ProtectString(apiKey),
        ApiKeyPrefix = "gsk_",
    };

    [Fact]
    public async Task LoCargadoEnLaPantalla_LeGanaAlServidor()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        db.AiSettings.Add(StoredGroq(protector));
        await db.SaveChangesAsync();

        var resolver = AiTestDoubles.BuildResolver(
            db,
            protector,
            AiTestDoubles.EnvironmentOptions("https://api.openai.com/v1", "sk-del-servidor", "gpt-4o-mini"));

        var resolution = await resolver.ResolveAsync(CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.Equal(AiConfigurationSource.Database, resolution!.Source);
        Assert.Equal("gsk_de_la_pantalla", resolution.Options.ApiKey);
        Assert.Equal("https://api.groq.com/openai/v1", resolution.Options.BaseUrl);
    }

    [Fact]
    public async Task SinNadaEnLaPantalla_UsaElRespaldoDelServidor()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var resolver = AiTestDoubles.BuildResolver(
            db,
            AiTestDoubles.BuildRealProtector(),
            AiTestDoubles.EnvironmentOptions("https://api.groq.com/openai/v1", "gsk_del_tecnico", "llama-3.3-70b-versatile"));

        var resolution = await resolver.ResolveAsync(CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.Equal(AiConfigurationSource.Environment, resolution!.Source);
        Assert.Equal("gsk_del_tecnico", resolution.Options.ApiKey);
    }

    [Fact]
    public async Task ConfiguracionAMedias_NoSeUsa_YManadaElRespaldo()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        // Fila guardada SIN clave: no alcanza para hablar con nadie. Mezclar esta direccion con la
        // clave del servidor daria una combinacion que no funciona, asi que se descarta entera.
        db.AiSettings.Add(new AiSettings
        {
            Provider = AiProviderKey.Groq,
            BaseUrl = "https://api.groq.com/openai/v1",
            Model = "llama-3.3-70b-versatile",
        });
        await db.SaveChangesAsync();

        var resolver = AiTestDoubles.BuildResolver(
            db,
            protector,
            AiTestDoubles.EnvironmentOptions("https://api.openai.com/v1", "sk-del-servidor", "gpt-4o-mini"));

        var resolution = await resolver.ResolveAsync(CancellationToken.None);

        Assert.NotNull(resolution);
        Assert.Equal(AiConfigurationSource.Environment, resolution!.Source);
        Assert.Equal("https://api.openai.com/v1", resolution.Options.BaseUrl);
    }

    [Fact]
    public async Task SinNadaEnNingunLado_NoHayIa_YEsoNoEsUnError()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var resolver = AiTestDoubles.BuildResolver(
            db, AiTestDoubles.BuildRealProtector(), AiTestDoubles.EmptyEnvironmentOptions());

        Assert.Null(await resolver.ResolveAsync(CancellationToken.None));
        Assert.False(await resolver.IsUsableAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClaveDeEjemploSinReemplazar_NoCuentaComoConfiguracion()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var resolver = AiTestDoubles.BuildResolver(
            db,
            AiTestDoubles.BuildRealProtector(),
            AiTestDoubles.EnvironmentOptions(
                "https://api.groq.com/openai/v1", "CHANGE_THIS_AI_API_KEY", "llama-3.3-70b-versatile"));

        // Es el error de instalacion mas comun (copiar el archivo de ejemplo y no reemplazar nada).
        // Dejarlo pasar da un rechazo del proveedor confuso en vez de "no hay IA configurada".
        Assert.Null(await resolver.ResolveAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ConIaConfigurada_LaRespuestaEsQueSePuedeUsar()
    {
        await using var db = AiTestDoubles.BuildDbContext();
        var protector = AiTestDoubles.BuildRealProtector();
        db.AiSettings.Add(StoredGroq(protector));
        await db.SaveChangesAsync();

        var resolver = AiTestDoubles.BuildResolver(db, protector, AiTestDoubles.EmptyEnvironmentOptions());

        Assert.True(await resolver.IsUsableAsync(CancellationToken.None));
    }
}
