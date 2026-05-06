using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Tests.Fixtures;

/// <summary>
/// Factory para tests E2E que levantan el host completo de TravelApi en proceso.
///
/// Decisiones clave (alineadas con TravelApi/Program.cs):
///  - Environment "Testing" -> evita el bloque que valida secrets de production.
///  - InMemory DB con un nombre por instancia (Guid) para aislar cada factoria.
///  - Auth scheme "Test" -> bypass JWT con TestAuthHandler.
///  - Hangfire server y scheduler deshabilitados via env vars (la config se lee
///    en builder.Configuration ANTES de que ConfigureAppConfiguration corra,
///    por eso la unica forma confiable de neutralizar Hangfire es via el env).
///  - HostedServices realtime deshabilitado (LogStreamingService /
///    BotLogMonitorService no aportan valor en tests).
///  - Database:ApplyMigrationsOnStartup=false -> evita MigrateAsync() contra
///    Postgres. InMemory crea tablas on-demand.
///  - Services:Reservations:BaseUrl no se setea -> Program cae en el else y
///    registra los servicios de reservas in-process. Ese es el comportamiento
///    que queremos pinear pre-refactor C17.
///  - Jwt:Key con 32+ chars para no romper la validacion de AddJwtBearer
///    (no afecta los tests porque el scheme default pasa a "Test").
///
/// Las env vars se setean en el static ctor para garantizar que esten antes
/// de cualquier WebApplication.CreateBuilder. Son del proceso de tests; quedan
/// seteadas durante toda la corrida (idempotente).
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = "TravelApiTests-" + Guid.NewGuid();

    static CustomWebApplicationFactory()
    {
        // .NET configuration usa "__" como separador de seccion en env vars.
        // Estas variables se aplican al builder.Configuration.GetValue(...)
        // que se ejecuta DENTRO de Program.cs antes de cualquier hook del
        // IWebHostBuilder.
        Environment.SetEnvironmentVariable("Hangfire__ServerEnabled", "false");
        Environment.SetEnvironmentVariable("Hangfire__SchedulerEnabled", "false");
        Environment.SetEnvironmentVariable("HostedServices__RealtimeEnabled", "false");
        Environment.SetEnvironmentVariable("Database__ApplyMigrationsOnStartup", "false");
        Environment.SetEnvironmentVariable("Jwt__Key", "TEST_KEY_AT_LEAST_32_CHARS_FOR_HS256_SUITE_OK");
        Environment.SetEnvironmentVariable("Jwt__Issuer", "MagnaTravelTest");
        Environment.SetEnvironmentVariable("Jwt__Audience", "MagnaTravelTestAudience");
        Environment.SetEnvironmentVariable("WhatsApp__WebhookSecret", "test-webhook-secret");
        Environment.SetEnvironmentVariable("Metrics__Token", "test-metrics-token");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection",
            "Host=test-db;Database=test;Username=test;Password=test");
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__JobStorageConnection",
            "Host=test-db;Database=test;Username=test;Password=test");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Reemplazar el DbContext registrado con Npgsql por InMemory.
            var descriptor = services.SingleOrDefault(s =>
                s.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options =>
            {
                options.UseInMemoryDatabase(_databaseName);
                options.ConfigureWarnings(w =>
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
            });

            // Forzar el scheme por defecto a "Test" para los tests autenticados.
            // Esto sobreescribe el JwtBearer registrado en Program.cs.
            services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = "Test";
                    options.DefaultChallengeScheme = "Test";
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }
}
