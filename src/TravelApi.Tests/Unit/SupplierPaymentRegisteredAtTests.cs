using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// H17 (barrido E2E 2026-07-25): espejo de <c>PaymentServiceRegistrationTests</c> para el eje del
/// PROVEEDOR. <c>SupplierPayment.CreatedAt</c> ya existia (UTC real al crear la fila, nunca se pisa
/// despues); antes simplemente no viajaba en <see cref="SupplierPaymentDto"/>. Este test blinda que
/// <c>RegisteredAt</c> sale de esa columna y NO de <c>PaidAt</c> (la fecha de negocio, que puede ser
/// vieja si se carga un pago atrasado al operador).
/// </summary>
public class SupplierPaymentRegisteredAtTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    [Fact]
    public async Task GetSupplierAccountPaymentsAsync_ExponeRegisteredAt_DistintoDeLaFechaDeNegocio()
    {
        await using var context = CreateContext();

        var supplier = new Supplier { Name = "Operador Test SRL" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        // Fecha de negocio vieja (pago atrasado que se carga hoy) vs. la hora real de creacion de la
        // fila (CreatedAt, que la entity ya default-ea a DateTime.UtcNow al construirse).
        var fechaDeNegocioVieja = DateTime.SpecifyKind(new DateTime(2020, 1, 1), DateTimeKind.Utc);
        var antesDeGuardar = DateTime.UtcNow;

        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            Amount = 500m,
            Currency = "ARS",
            Method = "Transfer",
            PaidAt = fechaDeNegocioVieja,
        });
        await context.SaveChangesAsync();

        var despuesDeGuardar = DateTime.UtcNow;

        var service = new SupplierService(context);
        var page = await service.GetSupplierAccountPaymentsAsync(
            supplier.Id, new SupplierAccountPaymentsQuery { Page = 1, PageSize = 25 }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(fechaDeNegocioVieja, item.PaidAt);
        Assert.InRange(item.RegisteredAt, antesDeGuardar.AddSeconds(-1), despuesDeGuardar.AddSeconds(1));
        Assert.NotEqual(item.PaidAt, item.RegisteredAt);
    }
}
