using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TravelApi.Application.Contracts.Auth;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Options;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit.Services;

public sealed class AuthServiceConcurrencyTests : IAsyncLifetime
{
    // Cliente "dueño de la sesion" (las dos pestañas del mismo navegador comparten estos dos
    // datos) y cliente "ladron" (otra maquina / otro navegador), para los tests de la ventana
    // de gracia atada al cliente.
    private const string OwnerIpAddress = "203.0.113.10";
    private const string OwnerUserAgent = "Mozilla/5.0 (Windows NT 10.0) Chrome/126";
    private const string ThiefIpAddress = "198.51.100.77";
    private const string ThiefUserAgent = "curl/8.5.0";

    private readonly ServiceProvider _services;

    public AuthServiceConcurrencyTests()
    {
        var services = new ServiceCollection();
        var dbName = $"AuthConcurrency-{Guid.NewGuid()}";

        services.AddLogging();
        services.AddDataProtection();
        services.AddMemoryCache();
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseInMemoryDatabase(dbName);
            options.ConfigureWarnings(warnings => warnings.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
        });
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.Password.RequireDigit = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Password.RequiredLength = 8;
            options.User.RequireUniqueEmail = true;
        })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();
        services.AddSingleton<IOptions<JwtOptions>>(Options.Create(new JwtOptions
        {
            Issuer = "tests",
            Audience = "tests",
            Key = "AUTH_TEST_KEY_32_BYTES_MINIMUM_123456789",
        }));
        services.AddScoped<IAuthService, AuthService>();

        _services = services.BuildServiceProvider();
    }

    public async Task InitializeAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var result = await roles.CreateAsync(new IdentityRole("Admin"));
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
    }

    public Task DisposeAsync()
    {
        _services.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Fake mínimo de <see cref="TimeProvider"/> (sin traer un paquete nuevo, mismo patrón
    /// que <c>FileMaintenanceModeServiceTests</c>): permite simular "pasaron 20 segundos" sin un delay
    /// real, para probar la ventana de gracia de rotación de refresh tokens.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start)
        {
            _now = start;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public async Task RegisterAsync_ConcurrentBootstrap_CreatesExactlyOneAdmin()
    {
        var attempts = await Task.WhenAll(
            RegisterCapturingExceptionAsync("uno@example.com"),
            RegisterCapturingExceptionAsync("dos@example.com"));

        Assert.Single(attempts.Where(attempt => attempt.Response is not null));
        Assert.Single(attempts.Where(attempt => attempt.Error is InvalidOperationException));

        await using var verificationScope = _services.CreateAsyncScope();
        var users = verificationScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var allUsers = await users.Users.ToListAsync();
        Assert.Single(allUsers);
        Assert.True(await users.IsInRoleAsync(allUsers[0], "Admin"));
    }

    // Hallazgo 2026-08-06 (revision de seguridad, bloqueante B2 — probablemente LA causa raiz
    // real de "cada deploy desloguea al dueño"): antes de este fix, dos pestañas mandando el
    // MISMO refresh token casi al mismo tiempo terminaban con UNA sesion nueva y la cadena
    // ENTERA revocada (incluida la sesion recien emitida) — la segunda pestaña, sin haber hecho
    // nada malo, tiraba abajo tambien a la primera. Con la ventana de gracia de rotacion, una
    // carrera LEGITIMA (dentro de los 15s) ya no se trata como robo: AMBAS pestañas terminan
    // con una sesion valida, y es LA MISMA (mismo access+refresh) — no dos sesiones distintas.
    [Fact]
    public async Task RefreshAsync_ConcurrentReuseWithinGraceWindow_BothTabsEndUpWithTheSameValidSession()
    {
        AuthTokensResult initial;
        await using (var registrationScope = _services.CreateAsyncScope())
        {
            var auth = registrationScope.ServiceProvider.GetRequiredService<IAuthService>();
            initial = await auth.RegisterAsync(new RegisterRequest(
                "Primera persona", "primera@example.com", "Valid123!"));
        }

        var attempts = await Task.WhenAll(
            RefreshCapturingExceptionAsync(initial.RefreshToken),
            RefreshCapturingExceptionAsync(initial.RefreshToken));

        // Ninguna de las dos pestañas se queda afuera: las dos reciben una respuesta valida.
        Assert.All(attempts, attempt => Assert.Null(attempt.Error));
        Assert.All(attempts, attempt => Assert.NotNull(attempt.Response));

        // Y es LA MISMA sesion (mismo access token y mismo refresh token), no dos sesiones
        // paralelas: la segunda pestaña recibio el replay cacheado de la primera rotacion.
        Assert.Equal(attempts[0].Response!.AccessToken, attempts[1].Response!.AccessToken);
        Assert.Equal(attempts[0].Response!.RefreshToken, attempts[1].Response!.RefreshToken);

        await using var verificationScope = _services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await db.RefreshTokens.ToListAsync();

        // El token original (revocado por la rotacion) + el nuevo, TODAVIA VIVO. Antes del fix
        // esto daba 2 tokens, AMBOS revocados (la cadena entera muerta por una carrera legitima).
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens.Where(token => token.IsRevoked));
        Assert.Single(tokens.Where(token => !token.IsRevoked));
    }

    // Contraparte del test de arriba: la deteccion de robo real SIGUE VIVA. Pasada la ventana
    // de gracia (15s), un reuso del token viejo ya no es "la segunda pestaña llego tarde" —
    // vuelve a tratarse exactamente como antes: reuso = robo, se revoca TODA la cadena.
    [Fact]
    public async Task RefreshAsync_ReuseAfterGraceWindowExpires_IsStillTreatedAsTheftAndRevokesEntireChain()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);

        AuthTokensResult initial;
        await using (var registrationScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(registrationScope, timeProvider);
            initial = await auth.RegisterAsync(new RegisterRequest(
                "Persona tardia", "tardia@example.com", "Valid123!"));
        }

        await using (var firstRefreshScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(firstRefreshScope, timeProvider);
            await auth.RefreshAsync(initial.RefreshToken);
        }

        // Pasa la ventana de gracia entera: el reuso que sigue ya no puede ser "la segunda
        // pestaña llegando un poco tarde", solo puede ser un token robado reapareciendo.
        timeProvider.Advance(AuthService.RefreshRotationGraceWindow + TimeSpan.FromSeconds(1));

        await using (var lateReuseScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(lateReuseScope, timeProvider);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => auth.RefreshAsync(initial.RefreshToken));
        }

        await using var verificationScope = _services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.True(token.IsRevoked));
    }

    // (1) Hallazgo B-N1 (revision de seguridad 2026-08-06, regla T-10): la ventana de gracia
    // ahora esta ATADA AL CLIENTE. Este test fija el caso legitimo explicito: el MISMO cliente
    // (misma IP, mismo user-agent) reusa el token recien rotado dentro de los 15s y recibe
    // exactamente la misma sesion, sin que se revoque nada.
    [Fact]
    public async Task RefreshAsync_ReuseWithinGraceWindowFromSameIpAndUserAgent_ReplaysTheSameSession()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var initial = await RegisterWithTimeProviderAsync(timeProvider, "misma-maquina@example.com");

        AuthTokensResult firstRotation;
        await using (var firstRefreshScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(firstRefreshScope, timeProvider);
            firstRotation = await auth.RefreshAsync(initial.RefreshToken, OwnerIpAddress, OwnerUserAgent);
        }

        // La segunda pestaña llega unos segundos despues, DENTRO de la ventana de gracia.
        timeProvider.Advance(TimeSpan.FromSeconds(3));

        AuthTokensResult secondTab;
        await using (var secondRefreshScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(secondRefreshScope, timeProvider);
            secondTab = await auth.RefreshAsync(initial.RefreshToken, OwnerIpAddress, OwnerUserAgent);
        }

        Assert.Equal(firstRotation.AccessToken, secondTab.AccessToken);
        Assert.Equal(firstRotation.RefreshToken, secondTab.RefreshToken);

        await using var verificationScope = _services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.Single(tokens.Where(token => !token.IsRevoked));
    }

    // (2) Contracara del test de arriba: MISMO token, DENTRO de la ventana de gracia, pero desde
    // OTRA IP. Eso ya no es "la segunda pestaña llegando tarde" (una pestaña no cambia de IP en
    // 3 segundos): es el token apareciendo en otra maquina -> robo -> cadena entera revocada.
    [Fact]
    public async Task RefreshAsync_ReuseWithinGraceWindowFromAnotherIp_IsTreatedAsTheftAndRevokesEntireChain()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var initial = await RegisterWithTimeProviderAsync(timeProvider, "ip-robada@example.com");

        await using (var firstRefreshScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(firstRefreshScope, timeProvider);
            await auth.RefreshAsync(initial.RefreshToken, OwnerIpAddress, OwnerUserAgent);
        }

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        await using (var thiefScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(thiefScope, timeProvider);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.RefreshAsync(initial.RefreshToken, ThiefIpAddress, OwnerUserAgent));
        }

        await using var verificationScope = _services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.True(token.IsRevoked));
    }

    // (3) Igual que (2) pero cambiando el dispositivo en vez de la IP: mismo token, misma red,
    // otro navegador dentro de la ventana. Tampoco recibe el replay.
    [Fact]
    public async Task RefreshAsync_ReuseWithinGraceWindowFromAnotherUserAgent_IsTreatedAsTheftAndRevokesEntireChain()
    {
        var timeProvider = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var initial = await RegisterWithTimeProviderAsync(timeProvider, "dispositivo-robado@example.com");

        await using (var firstRefreshScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(firstRefreshScope, timeProvider);
            await auth.RefreshAsync(initial.RefreshToken, OwnerIpAddress, OwnerUserAgent);
        }

        timeProvider.Advance(TimeSpan.FromSeconds(3));

        await using (var thiefScope = _services.CreateAsyncScope())
        {
            using var auth = BuildAuthServiceWithTimeProvider(thiefScope, timeProvider);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => auth.RefreshAsync(initial.RefreshToken, OwnerIpAddress, ThiefUserAgent));
        }

        await using var verificationScope = _services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.True(token.IsRevoked));
    }

    private async Task<AuthTokensResult> RegisterWithTimeProviderAsync(TimeProvider timeProvider, string email)
    {
        await using var registrationScope = _services.CreateAsyncScope();
        using var auth = BuildAuthServiceWithTimeProvider(registrationScope, timeProvider);
        return await auth.RegisterAsync(
            new RegisterRequest("Persona", email, "Valid123!"),
            OwnerIpAddress,
            OwnerUserAgent);
    }

    private async Task<(AuthTokensResult? Response, Exception? Error)> RegisterCapturingExceptionAsync(string email)
    {
        await using var scope = _services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        try
        {
            return (await auth.RegisterAsync(new RegisterRequest("Persona", email, "Valid123!")), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    private async Task<(AuthTokensResult? Response, Exception? Error)> RefreshCapturingExceptionAsync(string token)
    {
        await using var scope = _services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IAuthService>();
        try
        {
            return (await auth.RefreshAsync(token), null);
        }
        catch (Exception ex)
        {
            return (null, ex);
        }
    }

    // AuthService no implementa IDisposable, pero el helper de abajo devuelve un wrapper que
    // permite usar "using" para hacer explicito el alcance de cada instancia manual (una por
    // "pestaña" simulada), sin depender del contenedor de DI para su ciclo de vida.
    private static ManualAuthService BuildAuthServiceWithTimeProvider(AsyncServiceScope scope, TimeProvider timeProvider)
    {
        var provider = scope.ServiceProvider;
        var auth = new AuthService(
            provider.GetRequiredService<UserManager<ApplicationUser>>(),
            provider.GetRequiredService<IOptions<JwtOptions>>(),
            provider.GetRequiredService<ILogger<AuthService>>(),
            provider.GetRequiredService<AppDbContext>(),
            provider.GetRequiredService<IMemoryCache>(),
            timeProvider);
        return new ManualAuthService(auth);
    }

    // Wrapper trivial (IAuthService no expone el ctor internal, y no queremos exponerlo fuera de
    // pruebas): delega todo a la instancia real construida a mano con el TimeProvider fake.
    private sealed class ManualAuthService : IAuthService, IDisposable
    {
        private readonly AuthService _inner;

        public ManualAuthService(AuthService inner) => _inner = inner;

        public Task<AuthTokensResult> RegisterAsync(RegisterRequest request, string? ipAddress = null, string? userAgent = null)
            => _inner.RegisterAsync(request, ipAddress, userAgent);

        public Task<AuthTokensResult> LoginAsync(LoginRequest request, string? ipAddress = null, string? userAgent = null)
            => _inner.LoginAsync(request, ipAddress, userAgent);

        public Task<AuthTokensResult> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
            => _inner.RefreshAsync(refreshToken, ipAddress, userAgent);

        public Task<CurrentUserResponse?> GetCurrentUserAsync(string userId) => _inner.GetCurrentUserAsync(userId);

        public Task RevokeRefreshTokenAsync(string refreshToken) => _inner.RevokeRefreshTokenAsync(refreshToken);

        public Task RevokeAllRefreshTokensAsync(string userId) => _inner.RevokeAllRefreshTokensAsync(userId);

        public Task<string> CreateHangfireTokenAsync(string userId, TimeSpan? lifetime = null)
            => _inner.CreateHangfireTokenAsync(userId, lifetime);

        public Task<UserServiceResult> ChangePasswordAsync(string userId, ChangePasswordRequest request)
            => _inner.ChangePasswordAsync(userId, request);

        public void Dispose()
        {
            // Nada que liberar: AuthService no tiene recursos propios (el DbContext y el resto
            // los libera el AsyncServiceScope que los creo).
        }
    }
}
