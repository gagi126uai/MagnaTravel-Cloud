using System.Net;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Regression suite for endpoints that used to rely only on [Authorize].
/// A hidden UI control is not an authorization boundary: these requests model
/// direct API calls made with Postman/curl and an otherwise valid session.
/// </summary>
public sealed class SecurityPermissionRegressionTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SecurityPermissionRegressionTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData("/api/messages/recipients")]
    [InlineData("/api/whatsapp/conversations")]
    [InlineData("/api/countries")]
    [InlineData("/api/destinations/00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/quotes")]
    [InlineData("/api/rates")]
    public async Task SensitiveReads_WithoutModulePermission_Return403(string path)
    {
        var (userId, role) = await SeedUserAsync();
        using var client = CreateClient(userId, role);

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/countries", Permissions.PaquetesView)]
    [InlineData("/api/destinations", Permissions.PaquetesView)]
    [InlineData("/api/messages/simple", Permissions.MessagesView)]
    [InlineData("/api/quotes/00000000-0000-0000-0000-000000000001", Permissions.CrmView)]
    public async Task SensitiveWrites_WithReadOnlyPermission_Return403(string path, string readPermission)
    {
        var (userId, role) = await SeedUserAsync(readPermission);
        using var client = CreateClient(userId, role);
        using var body = new StringContent("{}", Encoding.UTF8, "application/json");

        var response = path.StartsWith("/api/quotes/", StringComparison.Ordinal)
            ? await client.PutAsync(path, body)
            : await client.PostAsync(path, body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<(string UserId, string Role)> SeedUserAsync(params string[] permissions)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var role = "SecurityRegression-" + suffix;
        var userId = "security-regression-" + suffix;

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await roleManager.CreateAsync(new IdentityRole(role));
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = userId + "@test.local",
            Email = userId + "@test.local",
            FullName = "Security regression user",
            IsActive = true
        };
        await userManager.CreateAsync(user, "Test1234!Aa");
        await userManager.AddToRoleAsync(user, role);

        foreach (var permission in permissions)
        {
            db.RolePermissions.Add(new RolePermission { RoleName = role, Permission = permission });
        }

        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        return (userId, role);
    }

    private HttpClient CreateClient(string userId, string role)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, role);
        return client;
    }
}
