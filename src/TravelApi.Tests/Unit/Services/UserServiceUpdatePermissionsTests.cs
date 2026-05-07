using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit.Services;

/// <summary>
/// B1.15 Fase 1 — UserService.UpdatePermissionsForRoleAsync.
/// Verifica que ademas de actualizar las RolePermissions:
///  1. Revoca los refresh tokens activos de TODOS los usuarios con ese rol.
///  2. Invalida el cache de permisos para cada uno de esos usuarios.
/// </summary>
public class UserServiceUpdatePermissionsTests : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly AppDbContext _dbContext;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;

    public UserServiceUpdatePermissionsTests()
    {
        var services = new ServiceCollection();
        var dbName = "UserServicePermsTests-" + Guid.NewGuid();

        services.AddLogging();
        services.AddDbContext<AppDbContext>(options =>
        {
            options.UseInMemoryDatabase(dbName);
            options.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning));
        });

        // DataProtection requerido por AddDefaultTokenProviders -> DataProtectorTokenProvider.
        services.AddDataProtection();

        services.AddIdentityCore<ApplicationUser>(options =>
        {
            // Relajar para tests (no tienen email/password reales en la mayoria).
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
            options.Password.RequiredLength = 6;
            options.User.RequireUniqueEmail = false;
        })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        _services = services.BuildServiceProvider();
        _dbContext = _services.GetRequiredService<AppDbContext>();
        _userManager = _services.GetRequiredService<UserManager<ApplicationUser>>();
        _roleManager = _services.GetRequiredService<RoleManager<IdentityRole>>();
    }

    public void Dispose()
    {
        _services.Dispose();
    }

    private async Task<ApplicationUser> CreateUserWithRoleAsync(string email, string role, string id)
    {
        if (!await _roleManager.RoleExistsAsync(role))
        {
            await _roleManager.CreateAsync(new IdentityRole(role));
        }

        var user = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            FullName = email,
            IsActive = true,
        };
        var create = await _userManager.CreateAsync(user, "Pass123!");
        Assert.True(create.Succeeded, string.Join(", ", create.Errors.Select(e => e.Description)));

        var addRole = await _userManager.AddToRoleAsync(user, role);
        Assert.True(addRole.Succeeded);

        return user;
    }

    private async Task SeedRefreshTokenAsync(string userId, bool active = true)
    {
        _dbContext.RefreshTokens.Add(new RefreshToken
        {
            UserId = userId,
            TokenHash = Guid.NewGuid().ToString("N"),
            ExpiresAt = active ? DateTime.UtcNow.AddDays(7) : DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow,
            RevokedAt = null,
        });
        await _dbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task UpdatePermissionsForRole_revokes_tokens_and_invalidates_cache_for_users_in_role()
    {
        var resolverMock = new Mock<IUserPermissionResolver>();
        resolverMock.Setup(r => r.Invalidate(It.IsAny<string>()));

        var alice = await CreateUserWithRoleAsync("alice@test.local", "Vendedor", "alice-id");
        var bob = await CreateUserWithRoleAsync("bob@test.local", "Vendedor", "bob-id");
        // Charlie tiene OTRO rol; sus tokens NO deben tocarse.
        var charlie = await CreateUserWithRoleAsync("charlie@test.local", "Admin", "charlie-id");

        await SeedRefreshTokenAsync(alice.Id);
        await SeedRefreshTokenAsync(bob.Id);
        await SeedRefreshTokenAsync(charlie.Id);

        var sut = new UserService(_userManager, _roleManager, _dbContext, resolverMock.Object);

        var result = await sut.UpdatePermissionsForRoleAsync(
            "Vendedor",
            new[] { Permissions.ReservasView, Permissions.ReservasCancel });

        Assert.True(result.Succeeded);

        // Tokens de alice y bob deben estar revocados.
        var aliceToken = await _dbContext.RefreshTokens.SingleAsync(t => t.UserId == alice.Id);
        var bobToken = await _dbContext.RefreshTokens.SingleAsync(t => t.UserId == bob.Id);
        var charlieToken = await _dbContext.RefreshTokens.SingleAsync(t => t.UserId == charlie.Id);

        Assert.NotNull(aliceToken.RevokedAt);
        Assert.NotNull(bobToken.RevokedAt);
        Assert.Null(charlieToken.RevokedAt);

        // Cache invalidado para alice y bob, NO para charlie.
        resolverMock.Verify(r => r.Invalidate(alice.Id), Times.Once);
        resolverMock.Verify(r => r.Invalidate(bob.Id), Times.Once);
        resolverMock.Verify(r => r.Invalidate(charlie.Id), Times.Never);

        // Permisos del rol guardados.
        var perms = await _dbContext.RolePermissions
            .Where(rp => rp.RoleName == "Vendedor")
            .Select(rp => rp.Permission)
            .ToListAsync();
        Assert.Equal(2, perms.Count);
        Assert.Contains(Permissions.ReservasView, perms);
        Assert.Contains(Permissions.ReservasCancel, perms);
    }

    [Fact]
    public async Task UpdatePermissionsForRole_unknown_role_returns_failure()
    {
        var resolverMock = new Mock<IUserPermissionResolver>(MockBehavior.Strict);
        var sut = new UserService(_userManager, _roleManager, _dbContext, resolverMock.Object);

        var result = await sut.UpdatePermissionsForRoleAsync("RolInexistente", Array.Empty<string>());

        Assert.False(result.Succeeded);
        // Si el rol no existe, NO debe tocar resolver ni tokens.
        resolverMock.Verify(r => r.Invalidate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task UpdatePermissionsForRole_filters_invalid_permissions()
    {
        var resolverMock = new Mock<IUserPermissionResolver>();
        var alice = await CreateUserWithRoleAsync("alice@test.local", "Vendedor", "alice-id");

        var sut = new UserService(_userManager, _roleManager, _dbContext, resolverMock.Object);

        var result = await sut.UpdatePermissionsForRoleAsync(
            "Vendedor",
            new[] { Permissions.ReservasView, "permiso.inventado" });

        Assert.True(result.Succeeded);

        var perms = await _dbContext.RolePermissions
            .Where(rp => rp.RoleName == "Vendedor")
            .Select(rp => rp.Permission)
            .ToListAsync();
        Assert.Single(perms);
        Assert.Equal(Permissions.ReservasView, perms.Single());
    }
}
