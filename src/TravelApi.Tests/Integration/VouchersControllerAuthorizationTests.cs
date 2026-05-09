using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
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
/// B1.15 Fase 0' (CODE-08): tests E2E del gating del VouchersController.
///
/// Antes el controller era solo [Authorize] — cualquier autenticado podia
/// listar/generar/anular vouchers de cualquier reserva. Ahora cada endpoint
/// tiene RequirePermission + RequireOwnership.
///
/// Nota sobre el factory compartido (CustomWebApplicationFactory IClassFixture):
/// la BD InMemory persiste entre tests de la misma clase. Por eso los tests no
/// asumen "rol Vendedor sin permiso X" — eso introduciria contaminacion entre
/// tests cuando otros tests del mismo run otorgan ese permiso al rol. En
/// cambio, otorgamos los permisos minimos al rol Vendedor y validamos el
/// ownership (rechazo cuando opera sobre voucher/reserva ajena).
///
/// Cubre:
///  - GET   /api/reservas/{id}/vouchers           Vendedor sobre reserva ajena -> 403.
///  - POST  /api/vouchers/{id}/revoke             Vendedor sobre voucher ajeno -> 403.
///  - Admin -> bypass (200/2xx contra cualquier reserva o voucher).
///  - Permiso sin rol Admin -> los tests de "sin permiso" estan en
///    <c>PermissionAuthorizationHandlerTests</c> a nivel unitario.
/// </summary>
public class VouchersControllerAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public VouchersControllerAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<SeedData> SeedAsync(string vendedorId, string vendedorEmail)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        var existing = await userMgr.FindByIdAsync(vendedorId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = vendedorId,
                UserName = vendedorEmail,
                Email = vendedorEmail,
                FullName = "Vendedor Voucher Test",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, "Vendedor");
        }

        async Task EnsurePermAsync(string perm)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = perm });
                await db.SaveChangesAsync();
            }
        }
        // Permisos minimos para llegar al chequeo de ownership.
        await EnsurePermAsync(Permissions.VouchersGenerate);
        await EnsurePermAsync(Permissions.VouchersIssue);
        await EnsurePermAsync(Permissions.VouchersRevoke);

        var ownPublicId = Guid.NewGuid();
        var otherPublicId = Guid.NewGuid();
        var own = new Reserva
        {
            PublicId = ownPublicId,
            Name = "Reserva propia " + ownPublicId.ToString("N")[..6],
            NumeroReserva = "F-VCH-OWN-" + ownPublicId.ToString("N")[..6],
            ResponsibleUserId = vendedorId,
            Status = EstadoReserva.Confirmed
        };
        var other = new Reserva
        {
            PublicId = otherPublicId,
            Name = "Reserva ajena " + otherPublicId.ToString("N")[..6],
            NumeroReserva = "F-VCH-OTH-" + otherPublicId.ToString("N")[..6],
            ResponsibleUserId = "owner-other",
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.AddRange(own, other);
        await db.SaveChangesAsync();

        var voucherPublicId = Guid.NewGuid();
        var voucher = new Voucher
        {
            PublicId = voucherPublicId,
            ReservaId = other.Id,
            FileName = "v-" + voucherPublicId.ToString("N")[..6] + ".pdf",
            Status = VoucherStatuses.Issued,
            Source = VoucherSources.Generated
        };
        db.Vouchers.Add(voucher);
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new SeedData(
            VendedorId: vendedorId,
            OwnReservaPublicId: own.PublicId,
            OtherReservaPublicId: other.PublicId,
            OtherVoucherPublicId: voucher.PublicId);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    [Fact]
    public async Task GET_Vouchers_VendedorOnAjenaReserva_Returns403()
    {
        var vendedorId = "vend-vch-aj-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var resp = await client.GetAsync($"/api/reservas/{seed.OtherReservaPublicId}/vouchers");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task POST_RevokeVoucher_VendedorOnAjenoVoucher_Returns403()
    {
        var vendedorId = "vend-vch-rev-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var body = new StringContent("{ \"Reason\": \"Test\" }", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync($"/api/vouchers/{seed.OtherVoucherPublicId}/revoke", body);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task POST_IssueVoucher_VendedorOnAjenoVoucher_Returns403()
    {
        var vendedorId = "vend-vch-iss-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var body = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync($"/api/vouchers/{seed.OtherVoucherPublicId}/issue", body);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Vouchers_AdminBypass_Returns200()
    {
        var vendedorId = "vend-vch-admin-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        // Sin headers: defaults a Admin.
        var resp = await client.GetAsync($"/api/reservas/{seed.OtherReservaPublicId}/vouchers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private sealed record SeedData(
        string VendedorId,
        Guid OwnReservaPublicId,
        Guid OtherReservaPublicId,
        Guid OtherVoucherPublicId);
}
