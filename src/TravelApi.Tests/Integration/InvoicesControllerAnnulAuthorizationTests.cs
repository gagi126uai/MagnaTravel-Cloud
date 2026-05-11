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
/// B1.15 Fase 0 (smoke 2026-05-10): tests E2E del gating del endpoint
/// POST /api/invoices/{id}/annul.
///
/// Antes: el Vendedor NO tenia <c>cobranzas.invoice_annul</c> en DefaultVendedor,
/// recibia Forbidden al intentar anular su propia factura mal cargada.
///
/// Ahora: el Vendedor SI tiene el permiso (Permissions.cs + migracion
/// SeedVendedorInvoiceAnnul). El ownership sigue protegiendo: el Vendedor solo
/// puede anular SUS facturas (las que pertenecen a reservas cuya ResponsibleUser
/// soy yo). La auditoria fiscal queda intacta (AnnulledByUser*, AnnulmentReason).
///
/// Cubre:
///  - Vendedor sobre factura ajena → 403 (ownership rechaza).
///  - Admin → bypass (no 403).
///
/// El caso "Vendedor sobre factura propia → no 403" no se incluye porque
/// invoca EnqueueAnnulmentAsync que toca el flow completo (Hangfire). El test
/// del 403 con factura ajena ya valida que el gating del permiso pasa para
/// el Vendedor — si pasara el permiso pero faltara ownership, el resultado
/// seria distinto.
/// </summary>
public class InvoicesControllerAnnulAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public InvoicesControllerAnnulAuthorizationTests(CustomWebApplicationFactory factory)
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
                FullName = "Vendedor Annul Test",
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
        // Permiso necesario para llegar al chequeo de ownership.
        await EnsurePermAsync(Permissions.CobranzasInvoiceAnnul);

        // Reserva ajena (responsable distinto al Vendedor).
        var ajenaPublicId = Guid.NewGuid();
        var ajena = new Reserva
        {
            PublicId = ajenaPublicId,
            Name = "Reserva ajena " + ajenaPublicId.ToString("N")[..6],
            NumeroReserva = "F-INV-OTH-" + ajenaPublicId.ToString("N")[..6],
            ResponsibleUserId = "owner-other",
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.Add(ajena);
        await db.SaveChangesAsync();

        // Factura aprobada en reserva ajena — para tener algo concreto que el
        // Vendedor intente anular.
        var invoicePublicId = Guid.NewGuid();
        var invoice = new Invoice
        {
            PublicId = invoicePublicId,
            ReservaId = ajena.Id,
            TipoComprobante = 6,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            Resultado = "A",
            CAE = "12345678901234",
            VencimientoCAE = DateTime.UtcNow.AddDays(10),
            ImporteTotal = 1000m,
            ImporteNeto = 826.45m,
            ImporteIva = 173.55m
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new SeedData(
            VendedorId: vendedorId,
            OtherInvoicePublicId: invoice.PublicId);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    [Fact]
    public async Task POST_AnnulInvoice_VendedorOnAjenaInvoice_Returns403()
    {
        var vendedorId = "vend-inv-aj-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, vendedorId);

        var body = new StringContent("{ \"Reason\": \"Test\" }", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync($"/api/invoices/{seed.OtherInvoicePublicId}/annul", body);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task POST_AnnulInvoice_AdminDefault_NotForbidden()
    {
        var vendedorId = "vend-inv-admin-" + Guid.NewGuid().ToString("N")[..8];
        var seed = await SeedAsync(vendedorId, vendedorId + "@test.local");

        var client = _factory.CreateClient();
        // Sin headers: default Admin (TestAuthHandler).
        var body = new StringContent("{ \"Reason\": \"Test\" }", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync($"/api/invoices/{seed.OtherInvoicePublicId}/annul", body);
        Assert.NotEqual(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    private sealed record SeedData(
        string VendedorId,
        Guid OtherInvoicePublicId);
}
