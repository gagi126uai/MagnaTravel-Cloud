using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
    private readonly ServiceProvider _services;

    public AuthServiceConcurrencyTests()
    {
        var services = new ServiceCollection();
        var dbName = $"AuthConcurrency-{Guid.NewGuid()}";

        services.AddLogging();
        services.AddDataProtection();
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

    [Fact]
    public async Task RefreshAsync_ConcurrentReuse_IssuesOnlyOneSessionAndRevokesItsChain()
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

        Assert.Single(attempts.Where(attempt => attempt.Response is not null));
        Assert.Single(attempts.Where(attempt => attempt.Error is UnauthorizedAccessException));

        await using var verificationScope = _services.CreateAsyncScope();
        var db = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tokens = await db.RefreshTokens.ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.All(tokens, token => Assert.True(token.IsRevoked));
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
}
