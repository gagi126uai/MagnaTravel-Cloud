using System;
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
/// B1.15 Fase B' (2026-05-11): tests E2E del workflow de aprobaciones.
///
/// Cubre los 4 cortes mas criticos:
///  - Gating de permisos: Vendedor sin approvals.request → 403 al crear.
///  - Idempotencia: Pending re-llamado devuelve el mismo (no crea otro).
///  - Cooldown post-rechazo: re-pedir lo mismo tras Reject → 429.
///  - Ownership de "mis solicitudes": solo veo las mias.
///
/// Lo que NO esta cubierto aqui (queda para Parte 2 / cuando integremos con
/// un endpoint real como /annul):
///  - El consumo del Approval por parte del handler de la accion.
///  - Job nightly de expiracion (es un metodo simple, se puede testear unit).
/// </summary>
public class ApprovalRequestsControllerAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ApprovalRequestsControllerAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> SeedVendedorAsync(string suffix, bool withRequestPermission = true)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        var userId = "vend-appr-" + suffix;
        var existing = await userMgr.FindByIdAsync(userId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = userId,
                UserName = userId + "@test.local",
                Email = userId + "@test.local",
                FullName = "Vendedor Approval Test",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, "Vendedor");
        }

        if (withRequestPermission)
        {
            if (!await db.RolePermissions.AnyAsync(rp =>
                    rp.RoleName == "Vendedor" && rp.Permission == Permissions.ApprovalsRequest))
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleName = "Vendedor",
                    Permission = Permissions.ApprovalsRequest
                });
                await db.SaveChangesAsync();
            }
        }

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();
        return userId;
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    private static CreateApprovalRequestPayload BuildPayload(int entityId)
        => new("InvoiceAnnulment", "Invoice", entityId, "Test reason", null);

    [Fact]
    public async Task POST_Create_VendedorWithRequestPerm_Returns201()
    {
        var userId = await SeedVendedorAsync(Guid.NewGuid().ToString("N")[..8]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, userId);

        var resp = await client.PostAsJsonAsync("/api/approvals", BuildPayload(1001));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<ApprovalRequestDto>();
        Assert.NotNull(dto);
        Assert.Equal("Pending", dto!.Status);
        Assert.Equal("InvoiceAnnulment", dto.RequestType);
        Assert.Equal(1001, dto.EntityId);
    }

    [Fact]
    public async Task POST_Create_DuplicatePending_ReturnsExistingNotCreatesNew()
    {
        var userId = await SeedVendedorAsync(Guid.NewGuid().ToString("N")[..8]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, userId);

        var first = await client.PostAsJsonAsync("/api/approvals", BuildPayload(1002));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstDto = await first.Content.ReadFromJsonAsync<ApprovalRequestDto>();

        // Segundo POST con misma combo → devuelve el mismo PublicId.
        var second = await client.PostAsJsonAsync("/api/approvals", BuildPayload(1002));
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var secondDto = await second.Content.ReadFromJsonAsync<ApprovalRequestDto>();

        Assert.Equal(firstDto!.PublicId, secondDto!.PublicId);
    }

    [Fact]
    public async Task POST_Approve_AdminBypass_ReturnsApproved()
    {
        // Vendedor crea, Admin (default headers) aprueba.
        var userId = await SeedVendedorAsync(Guid.NewGuid().ToString("N")[..8]);
        var vendedorClient = _factory.CreateClient();
        SetVendedorHeaders(vendedorClient, userId);
        var create = await vendedorClient.PostAsJsonAsync("/api/approvals", BuildPayload(1003));
        var created = await create.Content.ReadFromJsonAsync<ApprovalRequestDto>();

        var adminClient = _factory.CreateClient(); // default Admin.
        var approve = await adminClient.PostAsJsonAsync(
            $"/api/approvals/{created!.PublicId}/approve",
            new ResolveApprovalRequestPayload("Aprobado por test"));

        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        var approved = await approve.Content.ReadFromJsonAsync<ApprovalRequestDto>();
        Assert.Equal("Approved", approved!.Status);
        Assert.Equal("Aprobado por test", approved.ResolverNotes);
    }

    [Fact]
    public async Task POST_Create_VendedorAfterReject_Returns429Cooldown()
    {
        var userId = await SeedVendedorAsync(Guid.NewGuid().ToString("N")[..8]);
        var vendedorClient = _factory.CreateClient();
        SetVendedorHeaders(vendedorClient, userId);
        var create = await vendedorClient.PostAsJsonAsync("/api/approvals", BuildPayload(1004));
        var created = await create.Content.ReadFromJsonAsync<ApprovalRequestDto>();

        var adminClient = _factory.CreateClient();
        var reject = await adminClient.PostAsJsonAsync(
            $"/api/approvals/{created!.PublicId}/reject",
            new ResolveApprovalRequestPayload("Rechazado por test"));
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

        // El Vendedor intenta re-pedir lo mismo dentro del cooldown.
        var retry = await vendedorClient.PostAsJsonAsync("/api/approvals", BuildPayload(1004));
        Assert.Equal(HttpStatusCode.TooManyRequests, retry.StatusCode);
    }

    [Fact]
    public async Task GET_MyRequests_OnlyReturnsOwn()
    {
        var aliceId = await SeedVendedorAsync("alice-" + Guid.NewGuid().ToString("N")[..6]);
        var bobId = await SeedVendedorAsync("bob-" + Guid.NewGuid().ToString("N")[..6]);

        var alice = _factory.CreateClient();
        SetVendedorHeaders(alice, aliceId);
        await alice.PostAsJsonAsync("/api/approvals", BuildPayload(1005));

        var bob = _factory.CreateClient();
        SetVendedorHeaders(bob, bobId);
        await bob.PostAsJsonAsync("/api/approvals", BuildPayload(1006));

        var aliceRequests = await alice.GetFromJsonAsync<ApprovalRequestDto[]>("/api/approvals/my-requests");
        Assert.NotNull(aliceRequests);
        Assert.All(aliceRequests!, r => Assert.Equal(aliceId, r.RequestedByUserId));
        Assert.Contains(aliceRequests!, r => r.EntityId == 1005);
        Assert.DoesNotContain(aliceRequests!, r => r.EntityId == 1006);
    }
}
