using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// B1.15 Fase B'' (2026-05-11): tests E2E del CRUD de policies + gating.
///
/// Cubre:
///  - GET /api/approval-policies como Admin → 200 con seeds.
///  - GET sin permiso approvals.policies → 403.
///  - PUT /api/approval-policies/{requestType} como Admin → 200 con valor nuevo.
///  - PUT con RequestType invalido → 400.
///  - PUT con expirationDays fuera de rango → 400.
/// </summary>
public class ApprovalPoliciesControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApprovalPoliciesControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task SeedDefaultPoliciesAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Tests usan InMemory, no corren la migracion SQL. Seedeamos manualmente.
        if (!await db.ApprovalPolicies.AnyAsync())
        {
            db.ApprovalPolicies.AddRange(
                new ApprovalPolicy { RequestType = "InvoiceAnnulment", RequiresApproval = true },
                new ApprovalPolicy { RequestType = "ReservationCancellationWithPayment", RequiresApproval = true },
                new ApprovalPolicy { RequestType = "DiscountAboveThreshold", RequiresApproval = true },
                new ApprovalPolicy { RequestType = "FrozenEntityMutation", RequiresApproval = true },
                new ApprovalPolicy { RequestType = "PaymentDeadlineOverride", RequiresApproval = false },
                new ApprovalPolicy { RequestType = "ReservationTransfer", RequiresApproval = false });
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task GET_AllPolicies_Admin_Returns200()
    {
        await SeedDefaultPoliciesAsync();
        var client = _factory.CreateClient(); // Default Admin.

        var resp = await client.GetAsync("/api/approval-policies");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var policies = await resp.Content.ReadFromJsonAsync<ApprovalPolicyDto[]>();
        Assert.NotNull(policies);
        Assert.True(policies!.Length >= 6);
    }

    [Fact]
    public async Task GET_AllPolicies_VendedorWithoutPerm_Returns403()
    {
        await SeedDefaultPoliciesAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleMgr.RoleExistsAsync("Vendedor"))
                await roleMgr.CreateAsync(new IdentityRole("Vendedor"));
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var userId = "vend-policy-" + Guid.NewGuid().ToString("N")[..6];
            var user = new ApplicationUser { Id = userId, UserName = userId + "@test.local", Email = userId + "@test.local", FullName = "v", IsActive = true };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Vendedor");
            scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
            var resp = await client.GetAsync("/api/approval-policies");
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    [Fact]
    public async Task PUT_Policy_Admin_UpdatesValue()
    {
        await SeedDefaultPoliciesAsync();
        var client = _factory.CreateClient();
        var payload = new UpdateApprovalPolicyPayload(
            RequiresApproval: false,
            ExpirationDaysOverride: 3,
            CooldownHoursOverride: 2,
            Notes: "Test override");

        var resp = await client.PutAsJsonAsync("/api/approval-policies/InvoiceAnnulment", payload);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<ApprovalPolicyDto>();
        Assert.NotNull(dto);
        Assert.Equal("InvoiceAnnulment", dto!.RequestType);
        Assert.False(dto.RequiresApproval);
        Assert.Equal(3, dto.ExpirationDaysOverride);
        Assert.Equal(2, dto.CooldownHoursOverride);
        Assert.Equal("Test override", dto.Notes);
    }

    [Fact]
    public async Task PUT_Policy_InvalidRequestType_Returns400()
    {
        await SeedDefaultPoliciesAsync();
        var client = _factory.CreateClient();
        var payload = new UpdateApprovalPolicyPayload(true, null, null, null);

        var resp = await client.PutAsJsonAsync("/api/approval-policies/NoExiste", payload);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task PUT_Policy_OutOfRangeExpiration_Returns400()
    {
        await SeedDefaultPoliciesAsync();
        var client = _factory.CreateClient();
        var payload = new UpdateApprovalPolicyPayload(true, 999, null, null);

        var resp = await client.PutAsJsonAsync("/api/approval-policies/InvoiceAnnulment", payload);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
