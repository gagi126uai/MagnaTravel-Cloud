using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
/// B1.15 Fase D (2026-05-11): tests E2E del workflow annul + approval.
///
/// Cubre los 3 escenarios criticos:
///  1) Vendedor SIN ApprovalRequest aprobado -> 409 con requiresApproval=true.
///  2) Vendedor CON ApprovalRequest aprobado vigente -> 202 Accepted (encola job).
///  3) Admin -> bypass del setting RequireApprovalForInvoiceAnnulment, 202 directo.
///
/// NO testea el ProcessAnnulmentJob (Hangfire) — eso requiere mockear AFIP completo.
/// El consumo del ApprovalRequest (MarkConsumedAsync al Success) se valida via
/// unit test del service en otro archivo si hace falta. Aca solo el gating.
/// </summary>
public class InvoicesControllerAnnulApprovalWorkflowTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public InvoicesControllerAnnulApprovalWorkflowTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<SeedData> SeedAsync(string vendedorSuffix)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        var vendedorId = "vend-ann-apr-" + vendedorSuffix;
        var existing = await userMgr.FindByIdAsync(vendedorId);
        if (existing is null)
        {
            existing = new ApplicationUser
            {
                Id = vendedorId,
                UserName = vendedorId + "@test.local",
                Email = vendedorId + "@test.local",
                FullName = "Vendedor Annul Approval Test",
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
        await EnsurePermAsync(Permissions.CobranzasInvoiceAnnul);
        await EnsurePermAsync(Permissions.ApprovalsRequest);

        // Asegurar setting on (default true, pero por idempotencia entre tests).
        var settings = await db.OperationalFinanceSettings.FirstOrDefaultAsync();
        if (settings is null)
        {
            settings = new OperationalFinanceSettings { RequireApprovalForInvoiceAnnulment = true };
            db.OperationalFinanceSettings.Add(settings);
        }
        else
        {
            settings.RequireApprovalForInvoiceAnnulment = true;
        }
        await db.SaveChangesAsync();

        // Reserva propia del Vendedor (ownership pasa).
        var propiaPublicId = Guid.NewGuid();
        var propia = new Reserva
        {
            PublicId = propiaPublicId,
            Name = "Reserva propia " + propiaPublicId.ToString("N")[..6],
            NumeroReserva = "F-INV-OWN-" + propiaPublicId.ToString("N")[..6],
            ResponsibleUserId = vendedorId,
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.Add(propia);
        await db.SaveChangesAsync();

        var invoicePublicId = Guid.NewGuid();
        var invoice = new Invoice
        {
            PublicId = invoicePublicId,
            ReservaId = propia.Id,
            TipoComprobante = 6,
            PuntoDeVenta = 1,
            NumeroComprobante = 100 + DateTime.UtcNow.Millisecond,
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
            OwnInvoicePublicId: invoice.PublicId,
            OwnInvoiceLegacyId: invoice.Id);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    [Fact]
    public async Task POST_Annul_Vendedor_NoApproval_Returns409WithRequiresApproval()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.PostAsync($"/api/invoices/{seed.OwnInvoicePublicId}/annul", new StringContent(""));
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("requiresApproval").GetBoolean());
        Assert.Equal("InvoiceAnnulment", doc.RootElement.GetProperty("requestType").GetString());
        Assert.Equal("Invoice", doc.RootElement.GetProperty("entityType").GetString());
        Assert.Equal(seed.OwnInvoiceLegacyId, doc.RootElement.GetProperty("entityId").GetInt32());
    }

    [Fact]
    public async Task POST_Annul_Vendedor_WithApprovedRequest_ReturnsAccepted()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);

        // Crear approval Pending → aprobarlo via service (no via endpoint para no
        // depender del cookie/admin client).
        using (var scope = _factory.Services.CreateScope())
        {
            var approvalService = scope.ServiceProvider.GetRequiredService<IApprovalRequestService>();
            var created = await approvalService.CreateAsync(
                new Application.DTOs.CreateApprovalRequestPayload(
                    "InvoiceAnnulment", "Invoice", seed.OwnInvoiceLegacyId,
                    "Test approval - corregir cliente",
                    null),
                seed.VendedorId,
                "Vendedor Test");
            await approvalService.ApproveAsync(created.PublicId, "admin-test", "Admin Test", "OK procedan");
        }

        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.PostAsync($"/api/invoices/{seed.OwnInvoicePublicId}/annul", new StringContent(""));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Annul_Admin_BypassesApprovalRequirement()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..8]);
        // Default headers → Admin (TestAuthHandler).
        var client = _factory.CreateClient();

        var resp = await client.PostAsync($"/api/invoices/{seed.OwnInvoicePublicId}/annul", new StringContent(""));
        Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
    }

    private sealed record SeedData(
        string VendedorId,
        Guid OwnInvoicePublicId,
        int OwnInvoiceLegacyId);
}
