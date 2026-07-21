using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Tanda P2 "circuito proveedor" (2026-07-22): <c>GetSupplierRegisteredRefundsAsync</c>, el listado
/// "reembolsos ya registrados" de un operador (una fila por <c>OperatorRefundAllocation</c>). La pantalla lo
/// usa para ofrecer "Deshacer" y "Corregir reserva" sobre un reembolso puntual. Tests UNIT con EF InMemory
/// (sin Docker): cubren que aparecen vivas Y deshechas, que no se mezclan operadores, el orden (mas nuevas
/// primero) y el enmascarado de montos por <c>cobranzas.see_cost</c>.
/// </summary>
public class OperatorRefundRegisteredReadModelTests
{
    private static AppDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"registered-refunds-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IHttpContextAccessor AdminAccessor()
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Role, "Admin") }, authenticationType: "test")),
        };
        return new HttpContextAccessor { HttpContext = http };
    }

    /// <summary>
    /// Siembra: un operador con DOS reembolsos ya registrados (uno vivo, uno deshecho) sobre dos reservas
    /// anuladas distintas, mas un tercer reembolso de OTRO operador (no debe aparecer en la consulta del
    /// primero). Devuelve los ids de los dos operadores para que cada test filtre a gusto.
    /// </summary>
    private static async Task<(int SupplierAId, int SupplierBId)> SeedAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Familia Garcia", IsActive = true };
        var supplierA = new Supplier { Name = "Operador A", IsActive = true };
        var supplierB = new Supplier { Name = "Operador B", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplierA);
        ctx.Suppliers.Add(supplierB);
        await ctx.SaveChangesAsync();

        async Task<BookingCancellation> AddBcAsync(int supplierId, string numeroReserva)
        {
            var reserva = new Reserva
            {
                NumeroReserva = numeroReserva, Name = numeroReserva,
                PayerId = customer.Id, Status = EstadoReserva.Cancelled,
            };
            ctx.Reservas.Add(reserva);
            var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
            ctx.Invoices.Add(invoice);
            await ctx.SaveChangesAsync();

            var bc = new BookingCancellation
            {
                ReservaId = reserva.Id,
                CustomerId = customer.Id,
                SupplierId = supplierId,
                OriginatingInvoiceId = invoice.Id,
                Status = BookingCancellationStatus.Closed,
                Reason = "rm-registered",
                DraftedByUserId = "vendedor-1",
            };
            ctx.BookingCancellations.Add(bc);
            await ctx.SaveChangesAsync();
            return bc;
        }

        var bcLive = await AddBcAsync(supplierA.Id, "R-VIVA");
        var bcVoided = await AddBcAsync(supplierA.Id, "R-DESHECHA");
        var bcOtherSupplier = await AddBcAsync(supplierB.Id, "R-OTRO-OPERADOR");

        var refundA = new OperatorRefundReceived
        {
            SupplierId = supplierA.Id,
            ReceivedAmount = 900m,
            AllocatedAmount = 900m,
            Currency = "USD",
            Method = "Transfer",
            ReceivedByUserId = "cajero-1",
            ReceivedByUserName = "Cajero Uno",
        };
        var refundB = new OperatorRefundReceived
        {
            SupplierId = supplierB.Id,
            ReceivedAmount = 500m,
            AllocatedAmount = 500m,
            Currency = "ARS",
            Method = "Transfer",
            ReceivedByUserId = "cajero-1",
            ReceivedByUserName = "Cajero Uno",
        };
        ctx.OperatorRefundReceived.Add(refundA);
        ctx.OperatorRefundReceived.Add(refundB);
        await ctx.SaveChangesAsync();

        // Imputacion VIVA: se cargo mas tarde (fecha mas nueva) -> debe salir primero con el orden por defecto.
        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refundA.Id,
            BookingCancellationId = bcLive.Id,
            GrossAmount = 400m,
            NetAmount = 400m,
            IsVoided = false,
            CreatedAt = new DateTime(2026, 7, 20, 10, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = "cajero-1",
        });

        // Imputacion DESHECHA: se cargo antes -> debe seguir apareciendo (tachada), no desaparecer.
        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refundA.Id,
            BookingCancellationId = bcVoided.Id,
            GrossAmount = 500m,
            NetAmount = 500m,
            IsVoided = true,
            VoidedAt = new DateTime(2026, 7, 21, 9, 0, 0, DateTimeKind.Utc),
            VoidedByUserId = "cajero-2",
            VoidedReason = "Monto mal tipeado, correspondia otro importe",
            CreatedAt = new DateTime(2026, 7, 19, 10, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = "cajero-1",
        });

        // Imputacion de OTRO operador (control negativo).
        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refundB.Id,
            BookingCancellationId = bcOtherSupplier.Id,
            GrossAmount = 200m,
            NetAmount = 200m,
            IsVoided = false,
            CreatedAt = new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = "cajero-1",
        });

        await ctx.SaveChangesAsync();
        return (supplierA.Id, supplierB.Id);
    }

    [Fact]
    public async Task DevuelveVivaYDeshecha_ConLosCamposQueNecesitaLaPantalla_YNoMezclaOperadores()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedAsync(ctx);
        var service = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);

        var page = await service.GetSupplierRegisteredRefundsAsync(
            supplierAId, new OperatorRefundRegisteredQuery(), CancellationToken.None);

        // Solo las 2 imputaciones de A; la de B queda afuera.
        Assert.Equal(2, page.TotalCount);
        Assert.DoesNotContain(page.Items, i => i.NumeroReserva == "R-OTRO-OPERADOR");

        var live = page.Items.Single(i => i.NumeroReserva == "R-VIVA");
        Assert.False(live.IsVoided);
        Assert.Null(live.VoidedAt);
        Assert.Null(live.VoidedReason);
        Assert.Equal("Familia Garcia", live.ClienteNombre);
        // SeedAsync siembra un unico cliente ("Familia Garcia") duenio de las 3 reservas: lo recuperamos
        // de la base para comparar el PublicId real, sin hardcodear un Guid en el test.
        var seededCustomer = await ctx.Customers.SingleAsync(c => c.FullName == "Familia Garcia");
        Assert.Equal(seededCustomer.PublicId, live.ClientePublicId);
        Assert.Equal("USD", live.Currency);
        Assert.Equal(400m, live.NetAmount);
        Assert.False(live.AmountsMasked);
        Assert.NotEqual(Guid.Empty, live.ReservaPublicId);
        Assert.NotEqual(Guid.Empty, live.RefundReceivedPublicId);

        var voided = page.Items.Single(i => i.NumeroReserva == "R-DESHECHA");
        Assert.True(voided.IsVoided);
        Assert.NotNull(voided.VoidedAt);
        Assert.Equal("Monto mal tipeado, correspondia otro importe", voided.VoidedReason);
        // La deshecha SIGUE trayendo su monto (la pantalla la muestra tachada, no la esconde ni la vacia).
        Assert.Equal(500m, voided.NetAmount);
    }

    [Fact]
    public async Task Orden_PorDefecto_MasNuevasPrimero_SinEsconderLasDeshechas()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedAsync(ctx);
        var service = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);

        var page = await service.GetSupplierRegisteredRefundsAsync(
            supplierAId, new OperatorRefundRegisteredQuery(), CancellationToken.None);

        // R-VIVA se cargo el 20/07, R-DESHECHA el 19/07 -> con "mas nuevas primero" la viva va arriba,
        // aunque la deshecha sea mas vieja: el paginado no la manda al final ni la esconde.
        Assert.Equal(new[] { "R-VIVA", "R-DESHECHA" }, page.Items.Select(i => i.NumeroReserva));
    }

    /// <summary>
    /// Gate de exposicion de datos (2026-07-22): antes del fix, el reasociado guardaba el motivo del void con
    /// el prefijo en ingles "Reassociate: ". Esas filas quedaron ASI escritas en la base (dato legacy); el
    /// read-model tiene que sanearlas al leer, no solo las nuevas que ya se escriben con el prefijo correcto.
    /// </summary>
    [Fact]
    public async Task FilaDeshechaConPrefijoLegacyEnIngles_SeSaneaAEspanolAlLeer()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedAsync(ctx);

        // Sembramos a mano una allocation deshecha con el prefijo VIEJO, como si viniera de antes del fix
        // (OperatorRefundService.ReassociateAllocationAsync ya no escribe este texto, pero la fila historica
        // sigue en la base).
        var customer = new Customer { FullName = "Cliente Legacy", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();
        var reserva = new Reserva
        {
            NumeroReserva = "R-LEGACY-PREFIX", Name = "R-LEGACY-PREFIX",
            PayerId = customer.Id, Status = EstadoReserva.Cancelled,
        };
        ctx.Reservas.Add(reserva);
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        ctx.Invoices.Add(invoice);
        await ctx.SaveChangesAsync();
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplierAId,
            OriginatingInvoiceId = invoice.Id,
            Status = BookingCancellationStatus.Closed,
            Reason = "rm-legacy-prefix",
            DraftedByUserId = "vendedor-1",
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplierAId,
            ReceivedAmount = 300m,
            AllocatedAmount = 0m,
            Currency = "ars", // moneda legacy en minusculas -> debe normalizarse a "ARS" al leer
            Method = "Transfer",
            ReceivedByUserId = "cajero-1",
            ReceivedByUserName = "Cajero Uno",
        };
        ctx.OperatorRefundReceived.Add(refund);
        await ctx.SaveChangesAsync();

        ctx.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id,
            BookingCancellationId = bc.Id,
            GrossAmount = 300m,
            NetAmount = 300m,
            IsVoided = true,
            VoidedAt = new DateTime(2026, 7, 15, 9, 0, 0, DateTimeKind.Utc),
            VoidedByUserId = "cajero-2",
            VoidedReason = "Reassociate: me equivoqué de reserva", // prefijo VIEJO sembrado a mano
            CreatedAt = new DateTime(2026, 7, 14, 10, 0, 0, DateTimeKind.Utc),
            CreatedByUserId = "cajero-1",
        });
        await ctx.SaveChangesAsync();

        var service = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);
        var page = await service.GetSupplierRegisteredRefundsAsync(
            supplierAId, new OperatorRefundRegisteredQuery(), CancellationToken.None);

        var row = page.Items.Single(i => i.NumeroReserva == "R-LEGACY-PREFIX");
        Assert.DoesNotContain("Reassociate", row.VoidedReason);
        Assert.Contains("Corrección de reserva", row.VoidedReason);
        Assert.Equal("Corrección de reserva: me equivoqué de reserva", row.VoidedReason);
        // De paso, la moneda legacy en minusculas tambien queda normalizada.
        Assert.Equal("ARS", row.Currency);
    }

    [Fact]
    public async Task SinPermisoDeVerCostos_EnmascaraElMontoPeroNoElRestoDeLaFila()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedAsync(ctx);
        // Sin httpContextAccessor -> CostMasking falla cerrado -> montos enmascarados (mismo criterio que
        // el read-model de pendientes).
        var service = new OperatorRefundReadModelService(ctx, httpContextAccessor: null, permissionResolver: null);

        var page = await service.GetSupplierRegisteredRefundsAsync(
            supplierAId, new OperatorRefundRegisteredQuery(), CancellationToken.None);

        Assert.All(page.Items, item =>
        {
            Assert.True(item.AmountsMasked);
            Assert.Equal(0m, item.NetAmount);
            // Lo que NO es costo sigue viajando entero (reserva, cliente, estado deshecho).
            Assert.NotEmpty(item.NumeroReserva);
            Assert.NotEmpty(item.ClienteNombre);
        });
    }

    [Fact]
    public async Task OperadorSinReembolsosRegistrados_DevuelvePaginaVacia()
    {
        await using var ctx = NewDbContext();
        await SeedAsync(ctx);
        var service = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);

        // Un operador que existe pero nunca tuvo un reembolso registrado.
        var supplierSinMovimientos = new Supplier { Name = "Operador sin movimientos", IsActive = true };
        ctx.Suppliers.Add(supplierSinMovimientos);
        await ctx.SaveChangesAsync();

        var page = await service.GetSupplierRegisteredRefundsAsync(
            supplierSinMovimientos.Id, new OperatorRefundRegisteredQuery(), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task OperadorInexistente_DevuelvePaginaVacia_SinExplotar()
    {
        await using var ctx = NewDbContext();
        await SeedAsync(ctx);
        var service = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);

        // Mismo criterio que GetSupplierPendingRefundsAsync: el read-model no valida existencia del
        // proveedor (eso lo hace el controller via EntityReferenceResolver -> 404). Un id que nunca existio
        // simplemente no matchea ninguna allocation.
        var page = await service.GetSupplierRegisteredRefundsAsync(
            supplierId: 987654, new OperatorRefundRegisteredQuery(), CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Paginado_RespetaPageSize()
    {
        await using var ctx = NewDbContext();
        var (supplierAId, _) = await SeedAsync(ctx);
        var service = new OperatorRefundReadModelService(ctx, AdminAccessor(), permissionResolver: null);

        var page = await service.GetSupplierRegisteredRefundsAsync(
            supplierAId, new OperatorRefundRegisteredQuery { Page = 1, PageSize = 25 }, CancellationToken.None);

        Assert.Equal(1, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(1, page.TotalPages);
        Assert.False(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
    }
}
