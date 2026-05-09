using System;
using System.Net;
using System.Net.Http;
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
/// B1.15 Fase 0' (CODE-09 / CODE-10 / INV-2): tests E2E del SuppliersController.
///
/// Cubre:
///  - Admin bypass: opera sobre cualquier endpoint del controller (200 OK).
///  - SupplierPayment delete: soft-delete (IsDeleted=true, DeletedAt no nulo)
///    en lugar de hard-delete (no se puede restaurar la auditoria).
///
/// Nota sobre el factory compartido: los tests "rol Vendedor sin permiso"
/// estan cubiertos por <c>PermissionAuthorizationHandlerTests</c> a nivel
/// unitario — el factory comparte BD InMemory entre tests de la misma clase
/// y otorgar/revocar permisos al rol Vendedor introduciria contaminacion.
/// </summary>
public class SuppliersControllerAuthorizationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SuppliersControllerAuthorizationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private async Task<SeedData> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var supplier = new Supplier
        {
            Name = "Operador test " + Guid.NewGuid().ToString("N")[..6],
            IsActive = true
        };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var supplierPayment = new SupplierPayment
        {
            SupplierId = supplier.Id,
            Amount = 100m,
            Method = "Transfer",
            PaidAt = DateTime.UtcNow
        };
        db.SupplierPayments.Add(supplierPayment);
        await db.SaveChangesAsync();

        return new SeedData(
            SupplierPublicId: supplier.PublicId,
            SupplierPaymentPublicId: supplierPayment.PublicId,
            SupplierPaymentId: supplierPayment.Id);
    }

    [Fact]
    public async Task GET_Suppliers_AdminBypass_Returns200()
    {
        await SeedAsync();

        var client = _factory.CreateClient();
        // Sin headers: defaults a Admin.
        var resp = await client.GetAsync("/api/suppliers");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task GET_SupplierPayments_AdminBypass_Returns200()
    {
        var seed = await SeedAsync();

        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/suppliers/{seed.SupplierPublicId}/payments");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task DELETE_SupplierPayment_AdminBypass_SoftDeletesAndPersistsAuditTrail()
    {
        // B1.15 Fase 0' (CODE-10/INV-2): el delete es soft. La fila persiste con
        // IsDeleted=true y DeletedAt no nulo, en lugar de borrarse.
        var seed = await SeedAsync();

        var client = _factory.CreateClient();
        // Admin bypass para el permiso de tesoreria.
        var resp = await client.DeleteAsync($"/api/suppliers/{seed.SupplierPublicId}/payments/{seed.SupplierPaymentPublicId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Con query filter aplicado, el pago "no existe" (filtrado).
        var liveCount = await db.SupplierPayments.CountAsync(p => p.Id == seed.SupplierPaymentId);
        Assert.Equal(0, liveCount);

        // Con IgnoreQueryFilters, el pago existe pero soft-deleted.
        var soft = await db.SupplierPayments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstAsync(p => p.Id == seed.SupplierPaymentId);
        Assert.True(soft.IsDeleted);
        Assert.NotNull(soft.DeletedAt);
    }

    private sealed record SeedData(
        Guid SupplierPublicId,
        Guid SupplierPaymentPublicId,
        int SupplierPaymentId);
}
