using System.Net;
using System.Net.Http.Json;
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

namespace TravelApi.Tests.Cancellation.Http;

/// <summary>
/// FC1.2.4 v3 (2026-05-18): tests E2E sobre <c>OperatorRefundsController</c>.
///
/// <para>
/// Cubre el gating de permission (<c>caja.edit</c> / <c>caja.view</c>) y los
/// codigos HTTP de las branches principales del controller. El flujo de
/// negocio completo (Allocate -> Void -> Reassociate con BCs reales) lo
/// cubre <c>CancellationFlowE2ETests</c> (FC1.2.7).
/// </para>
/// </summary>
public class OperatorRefundsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public OperatorRefundsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Sembra setting + cashier (Vendedor con permisos caja). Devuelve los ids
    /// + un Supplier listo para que el cashier pueda registrar refunds.
    /// </summary>
    private async Task<TestSeed> SeedAsync(string suffix)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();

        var settings = await settingsService.GetEntityAsync(CancellationToken.None);
        settings.EnableNewCancellationFlow = true;
        await db.SaveChangesAsync();

        if (!await roleMgr.RoleExistsAsync("Cashier"))
            await roleMgr.CreateAsync(new IdentityRole("Cashier"));

        // Cashier con permisos minimos para registrar refunds.
        foreach (var perm in new[] { Permissions.CajaEdit, Permissions.CajaView })
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Cashier" && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "Cashier", Permission = perm });
            }
        }
        await db.SaveChangesAsync();

        var cashierId = "cashier-" + suffix;
        if (await userMgr.FindByIdAsync(cashierId) is null)
        {
            var user = new ApplicationUser
            {
                Id = cashierId,
                UserName = cashierId + "@t.local",
                Email = cashierId + "@t.local",
                FullName = "Cashier " + suffix,
                IsActive = true,
            };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Cashier");
        }

        var supplier = new Supplier
        {
            Name = "Operador " + suffix,
            IsActive = true,
            TaxCondition = "IVA_RESP_INSCRIPTO",
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new TestSeed(cashierId, supplier.PublicId);
    }

    private static void SetCashierHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Cashier");
    }

    // =========================================================================
    // POST /api/operator-refunds
    // =========================================================================

    [Fact]
    public async Task POST_RecordReceived_WithoutPermission_Returns403()
    {
        await SeedAsync(Guid.NewGuid().ToString("N")[..6]);

        // User sin caja.edit -> 403.
        using (var scope = _factory.Services.CreateScope())
        {
            var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
            if (!await roleMgr.RoleExistsAsync("Mirador"))
                await roleMgr.CreateAsync(new IdentityRole("Mirador"));
            var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var userId = "noperm-or-" + Guid.NewGuid().ToString("N")[..6];
            var user = new ApplicationUser { Id = userId, UserName = userId + "@t", Email = userId + "@t", FullName = "x", IsActive = true };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Mirador");
            scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
            client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Mirador");

            var payload = new RecordOperatorRefundRequest(
                SupplierPublicId: Guid.NewGuid(),
                ReceivedAmount: 1000m,
                Currency: "ARS",
                ReceivedAt: DateTime.UtcNow,
                Method: "Transfer",
                Reference: null,
                Notes: null);

            var resp = await client.PostAsJsonAsync("/api/operator-refunds", payload);
            Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        }
    }

    [Fact]
    public async Task POST_RecordReceived_WithCashierPermission_Returns201()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetCashierHeaders(client, seed.CashierId);

        var payload = new RecordOperatorRefundRequest(
            SupplierPublicId: seed.SupplierPublicId,
            ReceivedAmount: 1500m,
            Currency: "ARS",
            ReceivedAt: DateTime.UtcNow,
            Method: "Transfer",
            Reference: "TRF-001",
            Notes: "Reembolso por cancelacion ref. ticket-123");

        var resp = await client.PostAsJsonAsync("/api/operator-refunds", payload);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<OperatorRefundReceivedDto>();
        Assert.NotNull(dto);
        Assert.Equal(1500m, dto!.ReceivedAmount);
        Assert.Equal("ARS", dto.Currency);
        Assert.Equal(0m, dto.AllocatedAmount);
    }

    [Fact]
    public async Task POST_RecordReceived_SupplierNotFound_Returns404()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetCashierHeaders(client, seed.CashierId);

        var payload = new RecordOperatorRefundRequest(
            SupplierPublicId: Guid.NewGuid(), // no existe
            ReceivedAmount: 500m,
            Currency: "ARS",
            ReceivedAt: DateTime.UtcNow,
            Method: "Transfer",
            Reference: null,
            Notes: null);

        var resp = await client.PostAsJsonAsync("/api/operator-refunds", payload);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task POST_RecordReceived_FeatureFlagOff_Returns409()
    {
        // Asegurar flag OFF (cualquier test previo pudo haberlo prendido).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var settingsSvc = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();
            var settings = await settingsSvc.GetEntityAsync(CancellationToken.None);
            settings.EnableNewCancellationFlow = false;
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient(); // Admin default -> pasa permiso.
        var payload = new RecordOperatorRefundRequest(
            SupplierPublicId: Guid.NewGuid(),
            ReceivedAmount: 100m,
            Currency: "ARS",
            ReceivedAt: DateTime.UtcNow,
            Method: "Transfer",
            Reference: null,
            Notes: null);

        var resp = await client.PostAsJsonAsync("/api/operator-refunds", payload);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
    }

    // =========================================================================
    // GET /api/operator-refunds/{publicId}
    // =========================================================================

    [Fact]
    public async Task GET_Refund_NotFound_Returns404()
    {
        await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/operator-refunds/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // =========================================================================
    // POST /api/operator-refunds/{publicId}/allocations
    // =========================================================================

    [Fact]
    public async Task POST_Allocation_RefundNotFound_Returns404()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetCashierHeaders(client, seed.CashierId);

        var payload = new AllocateRefundRequest(
            BookingCancellationPublicId: Guid.NewGuid(),
            GrossAmount: 100m,
            Deductions: new List<DeductionLineRequest>());

        var resp = await client.PostAsJsonAsync($"/api/operator-refunds/{Guid.NewGuid()}/allocations", payload);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // =========================================================================
    // DELETE /api/operator-refunds/allocations/{allocationPublicId}
    // =========================================================================

    [Fact]
    public async Task DELETE_Allocation_AllocationNotFound_Returns404()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetCashierHeaders(client, seed.CashierId);

        var payload = new VoidAllocationRequest(Reason: "Anulada por error de carga del operador.");
        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/operator-refunds/allocations/{Guid.NewGuid()}")
        {
            Content = JsonContent.Create(payload),
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // =========================================================================
    // PATCH /api/operator-refunds/allocations/{allocationPublicId}/reassociate
    // =========================================================================

    [Fact]
    public async Task PATCH_Reassociate_AllocationNotFound_Returns404()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetCashierHeaders(client, seed.CashierId);

        var payload = new ReassociateAllocationRequest(
            NewBookingCancellationPublicId: Guid.NewGuid(),
            Reason: "Imputacion incorrecta — mover a la BC correcta.");

        var resp = await client.PatchAsJsonAsync(
            $"/api/operator-refunds/allocations/{Guid.NewGuid()}/reassociate", payload);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    private record TestSeed(string CashierId, Guid SupplierPublicId);
}
