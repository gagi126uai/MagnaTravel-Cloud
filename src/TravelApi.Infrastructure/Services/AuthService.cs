using System.IdentityModel.Tokens.Jwt;
using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using TravelApi.Application.Contracts.Auth;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Options;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class AuthService : IAuthService
{
    private static readonly SemaphoreSlim NonRelationalRegistrationLock = new(1, 1);
    private static readonly SemaphoreSlim NonRelationalRefreshLock = new(1, 1);
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);
    private const string GenericAuthFailureMessage = "No se pudo iniciar sesion con las credenciales provistas.";

    // Hallazgo 2026-08-06 (revision de seguridad, bloqueante B2): con varias pestañas
    // compartiendo la MISMA cookie de refresh, una ráfaga de reconexion (por ejemplo, el
    // reinicio del contenedor "api" en cada deploy) puede hacer que DOS pestañas manden el
    // mismo refresh token casi al mismo tiempo. La PRIMERA lo rota (crea uno nuevo, marca el
    // viejo como revocado). La SEGUNDA, milisegundos despues, llega con el MISMO token viejo
    // -que ya esta marcado como revocado- y el codigo de deteccion de robo (mas abajo,
    // RefreshCoreAsync) lo trata como un intento de REUSO MALICIOSO: revoca TODA la cadena
    // de sesion del usuario, incluida la que la primera pestaña recien recibio. Resultado:
    // el dueño (o cualquier usuario con dos pestañas) queda deslogueado por una carrera
    // legitima, no por un robo real.
    //
    // FIX (ventana de gracia de rotacion, patron estandar de la industria — Auth0 lo llama
    // "reuse interval", Okta "grace period"): si el reuso llega DENTRO de una ventana chica
    // despues de la rotacion original, no es robo — es la segunda pestaña llegando tarde.
    // En ese caso le devolvemos LA MISMA respuesta (mismo access+refresh) que ya se le dio a
    // la primera pestaña, sin revocar nada. Pasada la ventana (o si no encontramos la
    // respuesta en cache, por ejemplo tras un reinicio del proceso), seguimos tratando el
    // reuso como robo real, exactamente como antes: la deteccion de seguridad sigue viva.
    //
    // La gracia esta ATADA AL CLIENTE (hallazgo B-N1 de la revision de seguridad del 2026-08-06,
    // regla T-10: lo sensible se verifica del lado del servidor): no alcanza con que el token
    // coincida, tambien tienen que coincidir la IP y el user-agent del pedido con los de la
    // rotacion original. Dos pestañas del MISMO navegador comparten ambos datos, asi que la
    // carrera legitima sigue resolviendose igual; en cambio un token robado y replayado desde
    // OTRA maquina dentro de esos 15 segundos ya no recibe la sesion viva del dueño: cae al
    // camino de robo de siempre (se revoca la cadena entera) y queda registrado en el log.
    //
    // La respuesta original vive en memoria (IMemoryCache), NUNCA en la base: guardar un
    // refresh token en texto plano en Postgres (aunque sea unos segundos) es justamente lo
    // que la tabla RefreshTokens evita a proposito (solo persiste el HASH). Vivir en memoria
    // tiene un residual conocido: si "api" corre en mas de una instancia (hoy corre en una
    // sola, ver docker-compose.yml), una carrera entre pestañas podria caer en dos procesos
    // distintos y no encontrar la cache -> se trataria como robo (falla del lado seguro, no
    // inseguro). Documentado para revisar si el dia de mañana se escala "api" a mas de un
    // replica.
    internal static readonly TimeSpan RefreshRotationGraceWindow = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshRotationCacheTtl = RefreshRotationGraceWindow + TimeSpan.FromSeconds(15);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<AuthService> _logger;
    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _refreshRotationCache;
    private readonly TimeProvider _timeProvider;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        IOptions<JwtOptions> jwtOptions,
        ILogger<AuthService> logger,
        AppDbContext dbContext,
        IMemoryCache refreshRotationCache)
        : this(userManager, jwtOptions, logger, dbContext, refreshRotationCache, TimeProvider.System)
    {
    }

    /// <summary>
    /// Constructor interno con <see cref="TimeProvider"/> inyectable: los tests de la ventana
    /// de gracia de rotacion necesitan simular "pasaron 20 segundos" sin un delay real.
    /// </summary>
    internal AuthService(
        UserManager<ApplicationUser> userManager,
        IOptions<JwtOptions> jwtOptions,
        ILogger<AuthService> logger,
        AppDbContext dbContext,
        IMemoryCache refreshRotationCache,
        TimeProvider timeProvider)
    {
        _userManager = userManager;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
        _dbContext = dbContext;
        _refreshRotationCache = refreshRotationCache;
        _timeProvider = timeProvider;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public async Task<AuthTokensResult> RegisterAsync(RegisterRequest request, string? ipAddress = null, string? userAgent = null)
    {
        if (!_dbContext.Database.IsRelational())
        {
            await NonRelationalRegistrationLock.WaitAsync();
            try
            {
                return await RegisterFirstUserCoreAsync(request, ipAddress, userAgent);
            }
            finally
            {
                NonRelationalRegistrationLock.Release();
            }
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            var response = await RegisterFirstUserCoreAsync(request, ipAddress, userAgent);
            await transaction.CommitAsync();
            return response;
        });
    }

    private async Task<AuthTokensResult> RegisterFirstUserCoreAsync(
        RegisterRequest request,
        string? ipAddress,
        string? userAgent)
    {
        if (await _userManager.Users.AnyAsync())
        {
            throw new InvalidOperationException("El registro publico esta deshabilitado.");
        }

        var user = new ApplicationUser
        {
            UserName = request.Email,
            Email = request.Email,
            FullName = request.FullName,
            IsActive = true
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(", ", result.Errors.Select(e => e.Description)));
        }

        var roleResult = await _userManager.AddToRoleAsync(user, "Admin");
        if (!roleResult.Succeeded)
        {
            throw new InvalidOperationException("No se pudo completar la configuracion del primer usuario.");
        }

        return await IssueSessionAsync(user, ipAddress, userAgent, isPersistent: false);
    }

    public async Task<AuthTokensResult> LoginAsync(LoginRequest request, string? ipAddress = null, string? userAgent = null)
    {
        var normalizedEmail = request.Email.Trim();
        var user = await _userManager.FindByEmailAsync(normalizedEmail);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        if (await _userManager.IsLockedOutAsync(user))
        {
            _logger.LogWarning("Login blocked by lockout for user {UserId}", user.Id);
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        var isValid = await _userManager.CheckPasswordAsync(user, request.Password);
        if (!isValid)
        {
            await _userManager.AccessFailedAsync(user);
            _logger.LogWarning("Invalid login attempt for user {UserId}", user.Id);
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        await _userManager.ResetAccessFailedCountAsync(user);
        return await IssueSessionAsync(user, ipAddress, userAgent, request.RememberMe);
    }

    public async Task<AuthTokensResult> RefreshAsync(string refreshToken, string? ipAddress = null, string? userAgent = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        var tokenHash = ComputeTokenHash(refreshToken);

        if (!_dbContext.Database.IsRelational())
        {
            await NonRelationalRefreshLock.WaitAsync();
            try
            {
                return await RefreshCoreAsync(tokenHash, ipAddress, userAgent, lockPostgresRow: false);
            }
            finally
            {
                NonRelationalRefreshLock.Release();
            }
        }

        var strategy = _dbContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var response = await RefreshCoreAsync(
                    tokenHash,
                    ipAddress,
                    userAgent,
                    lockPostgresRow: _dbContext.Database.IsNpgsql());
                await transaction.CommitAsync();
                return response;
            }
            catch (UnauthorizedAccessException)
            {
                // Reutilizacion o expiracion pueden haber revocado tokens. Esos cambios
                // defensivos deben persistir aunque la respuesta HTTP termine en 401.
                await transaction.CommitAsync();
                throw;
            }
        });
    }

    private async Task<AuthTokensResult> RefreshCoreAsync(
        string tokenHash,
        string? ipAddress,
        string? userAgent,
        bool lockPostgresRow)
    {
        var storedToken = lockPostgresRow
            ? await _dbContext.RefreshTokens
                .FromSqlInterpolated($"SELECT * FROM \"RefreshTokens\" WHERE \"TokenHash\" = {tokenHash} FOR UPDATE")
                .SingleOrDefaultAsync()
            : await _dbContext.RefreshTokens.SingleOrDefaultAsync(token => token.TokenHash == tokenHash);

        if (storedToken is null)
        {
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        if (storedToken.IsRevoked)
        {
            // Ventana de gracia de rotacion (ver comentario largo junto a RefreshRotationGraceWindow,
            // arriba del todo de la clase): si este mismo token YA fue rotado hace poquito por OTRA
            // pestaña, no es un robo — es una carrera legitima. Le devolvemos la MISMA respuesta que
            // ya se le entrego a la primera pestaña, sin tocar nada mas.
            //
            // OJO: "otra pestaña" tiene que ser del MISMO cliente. Si el pedido llega con otra IP
            // u otro navegador, no es una carrera: es el mismo token apareciendo en otra maquina.
            if (IsWithinRefreshRotationGraceWindow(storedToken.RevokedAt) &&
                _refreshRotationCache.TryGetValue(tokenHash, out RefreshRotationReplay? gracefulReplay) &&
                gracefulReplay is not null)
            {
                if (IsSameClientAsOriginalRotation(gracefulReplay, ipAddress, userAgent))
                {
                    // Warning (no Information) a proposito: es un evento raro y queremos poder
                    // confirmar en los logs de produccion que la ventana de gracia se activo de
                    // verdad en los deploys, sin tener que subir el nivel de log.
                    _logger.LogWarning(
                        "Ventana de gracia de rotacion ACTIVADA para el usuario {UserId}: mismo cliente (misma IP y mismo dispositivo) reuso el refresh token recien rotado. Se repite la sesion ya emitida en vez de tratarlo como robo.",
                        storedToken.UserId);
                    return gracefulReplay.Tokens;
                }

                _logger.LogWarning(
                    "Ventana de gracia de rotacion RECHAZADA para el usuario {UserId}: el refresh token recien rotado se reuso desde un cliente distinto (IP {ClientIpAddress}). Se trata como robo y se revoca la cadena entera.",
                    storedToken.UserId,
                    ipAddress ?? "desconocida");
            }

            await RevokeAllRefreshTokensAsync(storedToken.UserId);
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        var user = await _userManager.FindByIdAsync(storedToken.UserId);
        if (storedToken.IsExpired || user is null || !user.IsActive)
        {
            storedToken.RevokedAt = UtcNow;
            await _dbContext.SaveChangesAsync();
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        // La fila original permanece bloqueada hasta el commit. Un segundo refresh con
        // el mismo token espera y luego observa RevokedAt, en vez de emitir otra sesion.
        var replacementToken = await IssueSessionAsync(user, ipAddress, userAgent, storedToken.IsPersistent);
        storedToken.RevokedAt = UtcNow;
        storedToken.ReplacedByTokenHash = ComputeTokenHash(replacementToken.RefreshToken);
        await _dbContext.SaveChangesAsync();

        // Se guarda DESPUES del SaveChanges exitoso: si algo de arriba falla, no queremos dejar en
        // cache una respuesta "fantasma" que nunca quedo persistida como la rotacion oficial.
        // Junto con la respuesta se guarda A QUIEN se le entrego (IP + user-agent), que es lo que
        // despues habilita -o no- el replay de la ventana de gracia.
        _refreshRotationCache.Set(
            tokenHash,
            new RefreshRotationReplay(replacementToken, ipAddress, userAgent),
            RefreshRotationCacheTtl);

        return replacementToken;
    }

    /// <summary>
    /// Foto de una rotacion recien hecha: la respuesta que se entrego y el cliente que la pidio.
    /// Vive solo unos segundos en memoria para poder repetirsela a la segunda pestaña del MISMO
    /// cliente (ver la ventana de gracia arriba del todo de la clase).
    /// </summary>
    private sealed record RefreshRotationReplay(AuthTokensResult Tokens, string? IpAddress, string? UserAgent);

    /// <summary>
    /// El replay de la ventana de gracia solo vale para el mismo cliente: dos pestañas del mismo
    /// navegador comparten IP y user-agent; un token robado usado desde otra maquina, no.
    /// Comparacion exacta y a prueba de nulos: si un dato falta de un lado, tiene que faltar del
    /// otro tambien (si no, se cae del lado seguro y el reuso se trata como robo).
    /// </summary>
    private static bool IsSameClientAsOriginalRotation(
        RefreshRotationReplay originalRotation,
        string? ipAddress,
        string? userAgent)
    {
        var sameIpAddress = string.Equals(originalRotation.IpAddress, ipAddress, StringComparison.Ordinal);
        var sameUserAgent = string.Equals(originalRotation.UserAgent, userAgent, StringComparison.Ordinal);
        return sameIpAddress && sameUserAgent;
    }

    // "hace poquito" = dentro de RefreshRotationGraceWindow. Si el token viejo nunca tuvo
    // RevokedAt (no deberia pasar, IsRevoked ya lo garantiza) lo tratamos como AFUERA de la
    // ventana -> cae al camino de robo real, del lado seguro.
    private bool IsWithinRefreshRotationGraceWindow(DateTime? revokedAt)
    {
        if (revokedAt is null)
        {
            return false;
        }

        return UtcNow - revokedAt.Value <= RefreshRotationGraceWindow;
    }

    public async Task<CurrentUserResponse?> GetCurrentUserAsync(string userId)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
        {
            return null;
        }

        return await BuildCurrentUserAsync(user);
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return;
        }

        var tokenHash = ComputeTokenHash(refreshToken);
        var storedToken = await _dbContext.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == tokenHash);
        if (storedToken is null || storedToken.IsRevoked)
        {
            return;
        }

        storedToken.RevokedAt = UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    public async Task RevokeAllRefreshTokensAsync(string userId)
    {
        var now = UtcNow;
        var activeTokens = await _dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > now)
            .ToListAsync();

        if (activeTokens.Count == 0)
        {
            return;
        }

        foreach (var token in activeTokens)
        {
            token.RevokedAt = now;
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<string> CreateHangfireTokenAsync(string userId, TimeSpan? lifetime = null)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
        {
            throw new UnauthorizedAccessException("Usuario no valido.");
        }

        return await CreateAccessTokenAsync(user, lifetime ?? TimeSpan.FromMinutes(5));
    }

    public async Task<UserServiceResult> ChangePasswordAsync(string userId, ChangePasswordRequest request)
    {
        var user = await _userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return new UserServiceResult(false, new[] { "Usuario no encontrado." });
        }

        var result = await _userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);
        if (result.Succeeded)
        {
            await RevokeAllRefreshTokensAsync(userId);
        }

        return new UserServiceResult(result.Succeeded, result.Errors.Select(e => e.Description));
    }

    private async Task<AuthTokensResult> IssueSessionAsync(ApplicationUser user, string? ipAddress, string? userAgent, bool isPersistent)
    {
        var currentUser = await BuildCurrentUserAsync(user);
        var accessTokenExpiresAt = UtcNow.Add(AccessTokenLifetime);
        var refreshTokenExpiresAt = UtcNow.Add(RefreshTokenLifetime);
        var accessToken = await CreateAccessTokenAsync(user, AccessTokenLifetime);
        var refreshToken = CreateRandomToken();
        var refreshTokenHash = ComputeTokenHash(refreshToken);
        var csrfToken = CreateRandomToken();

        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshTokenHash,
            CreatedAt = UtcNow,
            ExpiresAt = refreshTokenExpiresAt,
            CreatedByIp = ipAddress,
            UserAgent = userAgent?.Length > 512 ? userAgent[..512] : userAgent,
            IsPersistent = isPersistent
        });

        try
        {
            await _dbContext.SaveChangesAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            _logger.LogWarning(ex, "RefreshTokens table was missing during session issuance. Bootstrapping schema and retrying.");
            _dbContext.ChangeTracker.Clear();
            await RefreshTokenSchemaBootstrapper.EnsureAsync(_dbContext);
            await RefreshTokenSchemaBootstrapper.MarkRefreshTokenMigrationAsAppliedAsync(_dbContext);

            _dbContext.RefreshTokens.Add(new RefreshToken
            {
                UserId = user.Id,
                TokenHash = refreshTokenHash,
                CreatedAt = UtcNow,
                ExpiresAt = refreshTokenExpiresAt,
                CreatedByIp = ipAddress,
                UserAgent = userAgent?.Length > 512 ? userAgent[..512] : userAgent,
                IsPersistent = isPersistent
            });

            await _dbContext.SaveChangesAsync();
        }

        return new AuthTokensResult(
            accessToken,
            refreshToken,
            csrfToken,
            accessTokenExpiresAt,
            refreshTokenExpiresAt,
            currentUser,
            isPersistent);
    }

    private async Task<CurrentUserResponse> BuildCurrentUserAsync(ApplicationUser user)
    {
        var roles = (await _userManager.GetRolesAsync(user)).OrderBy(role => role).ToArray();
        return new CurrentUserResponse(
            user.Id,
            user.Email ?? string.Empty,
            user.FullName,
            roles,
            roles.Contains("Admin", StringComparer.OrdinalIgnoreCase));
    }

    private async Task<string> CreateAccessTokenAsync(ApplicationUser user, TimeSpan lifetime)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email ?? string.Empty),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.FullName) ? (user.Email ?? "Usuario") : user.FullName)
        };

        var roles = await _userManager.GetRolesAsync(user);
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: UtcNow.Add(lifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateRandomToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
    }

    private static string ComputeTokenHash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
