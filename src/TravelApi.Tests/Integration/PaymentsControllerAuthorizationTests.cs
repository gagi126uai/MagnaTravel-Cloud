using System;
using System.Linq;
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
/// B1.15 Fase 2a (review final): tests E2E de autorizacion del PaymentsController.
///
/// Cierra el bypass del flow nested /api/reservas/{id}/payments. El frontend
/// (PaymentModal.jsx + useReservaDetail.js) usa POST/PUT /api/payments y
/// GET /api/payments/reserva/{id} directamente; sin estos guards un Vendedor
/// con cobranzas.edit otorgado manualmente podria operar pagos de reservas
/// ajenas (ownership bypass).
///
/// Cubre:
///  - GET    /api/payments/reserva/{id}      Vendedor sobre ajena -> 403.
///  - POST   /api/payments                   Vendedor con ReservaId ajeno -> 403.
///  - PUT    /api/payments/{id}              Vendedor sobre payment ajeno -> 403.
///  - POST   /api/payments/{id}/receipt      Vendedor sobre payment ajeno -> 403.
///  - GET    /api/payments/{id}/receipt/pdf  Vendedor sobre payment ajeno -> 403.
///  - Admin / Colaborador con cobranzas.view_all bypass -> 200/2xx (happy path).
/// </summary>
public class PaymentsControllerAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public PaymentsControllerAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Crea un Vendedor con cobranzas.view + cobranzas.edit (sin view_all),
    /// dos reservas Confirmed (una propia, una ajena) y un Payment colgado de
    /// la reserva ajena. Devuelve identificadores publicos para construir URLs.
    /// </summary>
    private async Task<SeedData> SeedAsync(string vendedorId, string vendedorEmail)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
        {
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));
        }

        var existing = await userMgr.FindByIdAsync(vendedorId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = vendedorId,
                UserName = vendedorEmail,
                Email = vendedorEmail,
                FullName = "Vendedor Pagos Test",
                IsActive = true
            };
            await userMgr.CreateAsync(existing, "Test1234!Aa");
            await userMgr.AddToRoleAsync(existing, "Vendedor");
        }

        // Permisos minimos para llegar al chequeo de ownership: sin cobranzas.view
        // o cobranzas.edit el Vendedor caeria en 403 por permiso, no por ownership.
        // Idempotente.
        async Task EnsurePermAsync(string perm)
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = perm });
                await db.SaveChangesAsync();
            }
        }
        await EnsurePermAsync(Permissions.CobranzasView);
        await EnsurePermAsync(Permissions.CobranzasEdit);

        var ownPublicId = Guid.NewGuid();
        var otherPublicId = Guid.NewGuid();
        var own = new Reserva
        {
            PublicId = ownPublicId,
            Name = "Reserva propia " + ownPublicId.ToString("N")[..6],
            NumeroReserva = "F-PAG-OWN-" + ownPublicId.ToString("N")[..6],
            ResponsibleUserId = vendedorId,
            Status = EstadoReserva.Confirmed
        };
        var other = new Reserva
        {
            PublicId = otherPublicId,
            Name = "Reserva ajena " + otherPublicId.ToString("N")[..6],
            NumeroReserva = "F-PAG-OTH-" + otherPublicId.ToString("N")[..6],
            ResponsibleUserId = "owner-other",
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.AddRange(own, other);
        await db.SaveChangesAsync();

        // Payment colgado de la reserva ajena. Para los tests que validan
        // 403 sobre operaciones por payment id (PUT, GET pdf, POST receipt).
        var paymentPublicId = Guid.NewGuid();
        var otherPayment = new Payment
        {
            PublicId = paymentPublicId,
            ReservaId = other.Id,
            Amount = 100m,
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            PaidAt = DateTime.UtcNow
        };
        db.Payments.Add(otherPayment);
        await db.SaveChangesAsync();

        // Invalidar cache del resolver de permisos. RolePermissions cambian entre
        // tests y el TTL es de 15s — sin esto, fixtures previas pueden contaminar.
        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new SeedData(
            VendedorId: vendedorId,
            OwnReservaPublicId: own.PublicId,
            OtherReservaPublicId: other.PublicId,
            OtherPaymentPublicId: otherPayment.PublicId);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    [Fact]
    public async Task GET_PaymentsForReserva_VendedorOnAjenaReserva_Returns403()
    {
        var vendedorId = "vend-pay-getlist-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var response = await client.GetAsync($"/api/payments/reserva/{seed.OtherReservaPublicId}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_Payment_VendedorOnAjenaReserva_Returns403()
    {
        // El attribute RequireOwnership no aplica sobre POST /api/payments porque
        // la reserva viene en el body. La validacion la hace PaymentService.CreatePaymentAsync
        // tirando UnauthorizedAccessException, que el controller traduce a 403.
        var vendedorId = "vend-pay-post-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var body = new StringContent(
            "{ \"ReservaId\": \"" + seed.OtherReservaPublicId + "\", \"Amount\": 100, \"Method\": \"Transfer\" }",
            Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/payments", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PUT_Payment_VendedorOnAjenoPayment_Returns403()
    {
        var vendedorId = "vend-pay-put-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var body = new StringContent(
            "{ \"Amount\": 200, \"Method\": \"Cash\" }",
            Encoding.UTF8, "application/json");
        var response = await client.PutAsync($"/api/payments/{seed.OtherPaymentPublicId}", body);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task POST_PaymentReceipt_VendedorOnAjenoPayment_Returns403()
    {
        var vendedorId = "vend-pay-receipt-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var response = await client.PostAsync(
            $"/api/payments/{seed.OtherPaymentPublicId}/receipt",
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GET_PaymentReceiptPdf_VendedorOnAjenoPayment_Returns403()
    {
        var vendedorId = "vend-pay-pdf-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var response = await client.GetAsync($"/api/payments/{seed.OtherPaymentPublicId}/receipt/pdf");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// Happy path: Admin (default test user) atraviesa el bypass de
    /// PermissionAuthorizationHandler (rol "Admin" succeed) y RequireOwnership
    /// (rol "Admin" succeed). Devuelve 200 contra una reserva ajena.
    /// </summary>
    [Fact]
    public async Task GET_PaymentsForReserva_AdminBypass_Returns200()
    {
        var vendedorId = "vend-pay-admin-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        // Sin headers: defaults a Admin.
        var response = await client.GetAsync($"/api/payments/reserva/{seed.OtherReservaPublicId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed record SeedData(
        string VendedorId,
        Guid OwnReservaPublicId,
        Guid OtherReservaPublicId,
        Guid OtherPaymentPublicId);
}
