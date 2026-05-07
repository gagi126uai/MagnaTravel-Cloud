using System.Collections.Concurrent;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using TravelApi.Application.Interfaces;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Authorization;

/// <summary>
/// B1.15 Fase 1: implementacion default de <see cref="IUserPermissionResolver"/>.
/// Cache TTL 15s; permisos fiscales no toleran stale > 15s — review B1.15.
/// </summary>
public sealed class UserPermissionResolver : IUserPermissionResolver
{
    private const string CacheKeyPrefix = "user-permissions:";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(15);

    private readonly UserManager<ApplicationUser> _userManager;
    private readonly AppDbContext _dbContext;
    private readonly IMemoryCache _cache;

    // Tracker de keys activas para soportar InvalidateAll sin Reflection.
    // Es por instancia del resolver — el resolver es scoped y el cache es process-wide,
    // pero solo necesitamos invalidar lo que este resolver tracked. Para invalidaciones
    // broad (deploy, seed) usar el ciclo natural del TTL de 15s.
    private static readonly ConcurrentDictionary<string, byte> KnownUserKeys = new();

    public UserPermissionResolver(
        UserManager<ApplicationUser> userManager,
        AppDbContext dbContext,
        IMemoryCache cache)
    {
        _userManager = userManager;
        _dbContext = dbContext;
        _cache = cache;
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return EmptySet;
        }

        var key = CacheKeyPrefix + userId;
        if (_cache.TryGetValue<IReadOnlySet<string>>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var user = await _userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
        {
            // Cachear vacio para evitar hammering en flujos no autorizados.
            _cache.Set(key, EmptySet, CacheTtl);
            KnownUserKeys.TryAdd(userId, 0);
            return EmptySet;
        }

        var roles = await _userManager.GetRolesAsync(user);
        if (roles.Count == 0)
        {
            _cache.Set(key, EmptySet, CacheTtl);
            KnownUserKeys.TryAdd(userId, 0);
            return EmptySet;
        }

        var perms = await _dbContext.RolePermissions
            .AsNoTracking()
            .Where(rp => roles.Contains(rp.RoleName))
            .Select(rp => rp.Permission)
            .Distinct()
            .ToListAsync(cancellationToken);

        IReadOnlySet<string> set = new HashSet<string>(perms, StringComparer.Ordinal);
        _cache.Set(key, set, CacheTtl);
        KnownUserKeys.TryAdd(userId, 0);
        return set;
    }

    public void Invalidate(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        _cache.Remove(CacheKeyPrefix + userId);
        KnownUserKeys.TryRemove(userId, out _);
    }

    public void InvalidateAll()
    {
        foreach (var userId in KnownUserKeys.Keys)
        {
            _cache.Remove(CacheKeyPrefix + userId);
        }
        KnownUserKeys.Clear();
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);
}
