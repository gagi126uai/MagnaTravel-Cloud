using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using TravelApi.Application.Contracts.Auth;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Options;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

public class AuthService : IAuthService
{
    private static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);
    private const string GenericAuthFailureMessage = "No se pudo iniciar sesion con las credenciales provistas.";

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<AuthService> _logger;
    private readonly AppDbContext _dbContext;

    public AuthService(
        UserManager<ApplicationUser> userManager,
        IOptions<JwtOptions> jwtOptions,
        ILogger<AuthService> logger,
        AppDbContext dbContext)
    {
        _userManager = userManager;
        _jwtOptions = jwtOptions.Value;
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<AuthTokensResult> RegisterAsync(RegisterRequest request, string? ipAddress = null, string? userAgent = null)
    {
        var isFirstUser = !await _userManager.Users.AnyAsync();
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

        var defaultRole = isFirstUser ? "Admin" : "Colaborador";
        var roleResult = await _userManager.AddToRoleAsync(user, defaultRole);
        if (!roleResult.Succeeded)
        {
            _logger.LogWarning("Failed role assignment for {Email}", request.Email);
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
        var storedToken = await _dbContext.RefreshTokens
            .Include(token => token.User)
            .FirstOrDefaultAsync(token => token.TokenHash == tokenHash);

        if (storedToken is null)
        {
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        if (storedToken.IsRevoked)
        {
            await RevokeAllRefreshTokensAsync(storedToken.UserId);
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        if (storedToken.IsExpired || storedToken.User is null || !storedToken.User.IsActive)
        {
            storedToken.RevokedAt = DateTime.UtcNow;
            await _dbContext.SaveChangesAsync();
            throw new UnauthorizedAccessException(GenericAuthFailureMessage);
        }

        var replacementToken = await IssueSessionAsync(storedToken.User, ipAddress, userAgent, storedToken.IsPersistent);
        storedToken.RevokedAt = DateTime.UtcNow;
        storedToken.ReplacedByTokenHash = ComputeTokenHash(replacementToken.RefreshToken);
        await _dbContext.SaveChangesAsync();

        return replacementToken;
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

        storedToken.RevokedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();
    }

    public async Task RevokeAllRefreshTokensAsync(string userId)
    {
        var activeTokens = await _dbContext.RefreshTokens
            .Where(token => token.UserId == userId && token.RevokedAt == null && token.ExpiresAt > DateTime.UtcNow)
            .ToListAsync();

        if (activeTokens.Count == 0)
        {
            return;
        }

        foreach (var token in activeTokens)
        {
            token.RevokedAt = DateTime.UtcNow;
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
        var accessTokenExpiresAt = DateTime.UtcNow.Add(AccessTokenLifetime);
        var refreshTokenExpiresAt = DateTime.UtcNow.Add(RefreshTokenLifetime);
        var accessToken = await CreateAccessTokenAsync(user, AccessTokenLifetime);
        var refreshToken = CreateRandomToken();
        var refreshTokenHash = ComputeTokenHash(refreshToken);
        var csrfToken = CreateRandomToken();

        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = refreshTokenHash,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = refreshTokenExpiresAt,
            CreatedByIp = ipAddress,
            UserAgent = userAgent?.Length > 512 ? userAgent[..512] : userAgent,
            IsPersistent = isPersistent
        });

        await _dbContext.SaveChangesAsync();

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
            new(ClaimTypes.Name, user.FullName)
        };

        var roles = await _userManager.GetRolesAsync(user);
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtOptions.Issuer,
            audience: _jwtOptions.Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime),
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
