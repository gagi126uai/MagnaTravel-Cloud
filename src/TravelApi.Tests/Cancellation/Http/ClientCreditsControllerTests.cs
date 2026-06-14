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
/// FC1.2.4 v3 (2026-05-18): tests E2E sobre <c>ClientCreditsController</c>.
///
/// <para>
/// Cubre el gating de permission (<c>reservas.view</c> / <c>cobranzas.edit</c>),
/// ownership (BC y ClientCreditEntry derivados de la Reserva) y dos codigos
/// de error principales (INV-094 Ley 25.345 explicito del service, KeptAsCredit
/// con Amount!=0 -> 400). El flujo end-to-end con allocation + saldo lo cubre
/// <c>CancellationFlowE2ETests</c>.
/// </para>
/// </summary>
public class ClientCreditsControllerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public ClientCreditsControllerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Sembra setting con flag on + un Vendedor con permisos basicos + una
    /// reserva con BC y ClientCreditEntry asociado, listo para que los tests
    /// HTTP puedan apuntar a publicIds reales.
    /// </summary>
    private async Task<TestSeed> SeedAsync(string suffix, decimal initialBalance = 5000m)
    {
        using var scope = _factory.Services.CreateScope();
        var userMgr = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleMgr = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var settingsService = scope.ServiceProvider.GetRequiredService<IOperationalFinanceSettingsService>();

        var settings = await settingsService.GetEntityAsync(CancellationToken.None);
        settings.EnableNewCancellationFlow = true;
        await db.SaveChangesAsync();

        if (!await roleMgr.RoleExistsAsync("Vendedor"))
            await roleMgr.CreateAsync(new IdentityRole("Vendedor"));

        foreach (var perm in new[] { Permissions.ReservasView, Permissions.CobranzasEdit })
        {
            if (!await db.RolePermissions.AnyAsync(rp => rp.RoleName == "Vendedor" && rp.Permission == perm))
            {
                db.RolePermissions.Add(new RolePermission { RoleName = "Vendedor", Permission = perm });
            }
        }
        await db.SaveChangesAsync();

        var vendedorId = "vend-cred-" + suffix;
        if (await userMgr.FindByIdAsync(vendedorId) is null)
        {
            var user = new ApplicationUser
            {
                Id = vendedorId,
                UserName = vendedorId + "@t.local",
                Email = vendedorId + "@t.local",
                FullName = "Vendedor Credit " + suffix,
                IsActive = true,
            };
            await userMgr.CreateAsync(user, "Test1234!Aa");
            await userMgr.AddToRoleAsync(user, "Vendedor");
        }

        // Seed minimo: customer + supplier + reserva propia + reserva ajena +
        // invoice + BC propio + BC ajeno + entry propio + entry ajeno.
        var customer = new Customer { FullName = "Cliente " + suffix, TaxCondition = "Consumidor Final", IsActive = true };
        var supplier = new Supplier { Name = "Sup " + suffix, IsActive = true, TaxCondition = "IVA_RESP_INSCRIPTO" };
        db.Customers.Add(customer);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();

        var seedIds = await SeedBcAndEntryAsync(
            db, customer.Id, supplier.Id, vendedorId, "owner-other", suffix, initialBalance);

        scope.ServiceProvider.GetRequiredService<IUserPermissionResolver>().InvalidateAll();

        return new TestSeed(
            vendedorId,
            seedIds.EntryOwnPid,
            seedIds.EntryAjenoPid,
            seedIds.BcOwnPid,
            seedIds.OwnReservaPid,
            seedIds.AjenaReservaPid);
    }

    /// <summary>
    /// Helper: crea un BC propio + un BC ajeno + un entry asociado a cada uno.
    /// Retorna los PublicIds de los entries, del BC propio (que tambien sirve para testear el endpoint
    /// nested GET /booking-cancellations/{bc}/credit-entries) y de las DOS reservas (propia/ajena), que
    /// los tests de FC4-I1 usan como destino al aplicar saldo a favor.
    ///
    /// <para>FC4 (fix I1): ambas reservas llevan un servicio confirmado en ARS de 1000 para que tengan deuda
    /// exigible en esa moneda. Asi un saldo a favor (que nace en ARS) puede aplicarse sin chocar contra la
    /// validacion de "no hay deuda en esa moneda" (INV-095), y el unico filtro que separa el caso 403 del 201
    /// es el OWNERSHIP de la reserva destino (lo que I1 verifica).</para>
    /// </summary>
    private static async Task<(Guid EntryOwnPid, Guid EntryAjenoPid, Guid BcOwnPid, Guid OwnReservaPid, Guid AjenaReservaPid)> SeedBcAndEntryAsync(
        AppDbContext db, int customerId, int supplierId,
        string ownerUserId, string otherUserId, string suffix, decimal initialBalance)
    {
        var ownRes = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Own " + suffix,
            NumeroReserva = "CRED-OWN-" + suffix,
            ResponsibleUserId = ownerUserId,
            PayerId = customerId,
            Status = EstadoReserva.Confirmed,
        };
        var ajenaRes = new Reserva
        {
            PublicId = Guid.NewGuid(),
            Name = "Otr " + suffix,
            NumeroReserva = "CRED-OTR-" + suffix,
            ResponsibleUserId = otherUserId,
            PayerId = customerId,
            Status = EstadoReserva.Confirmed,
        };
        db.Reservas.AddRange(ownRes, ajenaRes);
        await db.SaveChangesAsync();

        // FC4 (fix I1): un servicio confirmado en ARS por reserva => deuda exigible en ARS de 1000 cada una.
        foreach (var res in new[] { ownRes, ajenaRes })
        {
            db.Servicios.Add(new ServicioReserva
            {
                ReservaId = res.Id,
                ServiceType = "Hotel",
                ProductType = "Hotel",
                Description = "Hotel destino " + suffix,
                ConfirmationNumber = "OK-" + res.NumeroReserva,
                Status = "Confirmado",
                Currency = "ARS",
                DepartureDate = DateTime.UtcNow.AddDays(20),
                SalePrice = 1000m,
                NetCost = 0m,
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();

        // Invoice cada una.
        var invOwn = new Invoice
        {
            ReservaId = ownRes.Id, TipoComprobante = 6, PuntoDeVenta = 1, NumeroComprobante = 1,
            ImporteTotal = 1000m, Resultado = "A", CAE = "1",
            VencimientoCAE = DateTime.UtcNow.AddDays(10), CreatedAt = DateTime.UtcNow,
        };
        var invAjena = new Invoice
        {
            ReservaId = ajenaRes.Id, TipoComprobante = 6, PuntoDeVenta = 1, NumeroComprobante = 2,
            ImporteTotal = 1000m, Resultado = "A", CAE = "2",
            VencimientoCAE = DateTime.UtcNow.AddDays(10), CreatedAt = DateTime.UtcNow,
        };
        db.Invoices.AddRange(invOwn, invAjena);
        await db.SaveChangesAsync();

        var bcOwn = new BookingCancellation
        {
            PublicId = Guid.NewGuid(),
            CustomerId = customerId, SupplierId = supplierId, ReservaId = ownRes.Id,
            OriginatingInvoiceId = invOwn.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion propia",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = ownerUserId,
            AmountPaidAtCancellation = 1000m,
            EstimatedRefundAmount = initialBalance,
            ReceivedRefundAmount = initialBalance,
        };
        var bcAjeno = new BookingCancellation
        {
            PublicId = Guid.NewGuid(),
            CustomerId = customerId, SupplierId = supplierId, ReservaId = ajenaRes.Id,
            OriginatingInvoiceId = invAjena.Id,
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Cancelacion ajena",
            DraftedAt = DateTime.UtcNow,
            DraftedByUserId = otherUserId,
            AmountPaidAtCancellation = 1000m,
            EstimatedRefundAmount = initialBalance,
            ReceivedRefundAmount = initialBalance,
        };
        db.BookingCancellations.AddRange(bcOwn, bcAjeno);
        await db.SaveChangesAsync();

        // Refund + allocation por BC para poder generar el entry (FK a allocation).
        var refund = new OperatorRefundReceived
        {
            PublicId = Guid.NewGuid(),
            SupplierId = supplierId, ReceivedAmount = initialBalance * 2, AllocatedAmount = initialBalance * 2,
            Currency = "ARS", Method = "Transfer", ReceivedAt = DateTime.UtcNow,
            ReceivedByUserId = "system", ReceivedByUserName = "system",
            ExchangeRateAtReceipt = 1m,
        };
        db.OperatorRefundReceived.Add(refund);
        await db.SaveChangesAsync();

        var allocOwn = new OperatorRefundAllocation
        {
            PublicId = Guid.NewGuid(),
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = bcOwn.Id,
            GrossAmount = initialBalance, NetAmount = initialBalance,
            CreatedAt = DateTime.UtcNow, CreatedByUserId = "system",
        };
        var allocAjeno = new OperatorRefundAllocation
        {
            PublicId = Guid.NewGuid(),
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = bcAjeno.Id,
            GrossAmount = initialBalance, NetAmount = initialBalance,
            CreatedAt = DateTime.UtcNow, CreatedByUserId = "system",
        };
        db.OperatorRefundAllocations.AddRange(allocOwn, allocAjeno);
        await db.SaveChangesAsync();

        var entryOwn = new ClientCreditEntry
        {
            PublicId = Guid.NewGuid(),
            BookingCancellationId = bcOwn.Id, CustomerId = customerId,
            OperatorRefundAllocationId = allocOwn.Id,
            CreditedAmount = initialBalance, RemainingBalance = initialBalance,
            IsFullyConsumed = false, CreatedAt = DateTime.UtcNow,
        };
        var entryAjeno = new ClientCreditEntry
        {
            PublicId = Guid.NewGuid(),
            BookingCancellationId = bcAjeno.Id, CustomerId = customerId,
            OperatorRefundAllocationId = allocAjeno.Id,
            CreditedAmount = initialBalance, RemainingBalance = initialBalance,
            IsFullyConsumed = false, CreatedAt = DateTime.UtcNow,
        };
        db.ClientCreditEntries.AddRange(entryOwn, entryAjeno);
        await db.SaveChangesAsync();

        return (entryOwn.PublicId, entryAjeno.PublicId, bcOwn.PublicId, ownRes.PublicId, ajenaRes.PublicId);
    }

    private static void SetVendedorHeaders(HttpClient client, string userId)
    {
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserIdHeader);
        client.DefaultRequestHeaders.Remove(TestAuthHandler.TestUserRolesHeader);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserIdHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserRolesHeader, "Vendedor");
    }

    // =========================================================================
    // GET /api/booking-cancellations/{bcPublicId}/credit-entries
    // =========================================================================

    [Fact]
    public async Task GET_EntriesByBc_Vendedor_OnOwnBC_Returns200()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.GetAsync($"/api/booking-cancellations/{seed.BcOwnPublicId}/credit-entries");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var entries = await resp.Content.ReadFromJsonAsync<List<ClientCreditEntryDto>>();
        Assert.NotNull(entries);
        Assert.Single(entries!);
        Assert.Equal(seed.EntryOwnPublicId, entries![0].PublicId);
    }

    // =========================================================================
    // GET /api/client-credit-entries/{publicId}
    // =========================================================================

    [Fact]
    public async Task GET_Entry_Vendedor_OnAjenoEntry_Returns403_Ownership()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.GetAsync($"/api/client-credit-entries/{seed.EntryAjenoPublicId}");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task GET_Entry_Vendedor_OnOwnEntry_Returns200()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var resp = await client.GetAsync($"/api/client-credit-entries/{seed.EntryOwnPublicId}");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<ClientCreditEntryDto>();
        Assert.NotNull(dto);
        Assert.Equal(seed.EntryOwnPublicId, dto!.PublicId);
    }

    // =========================================================================
    // POST /api/client-credit-entries/{publicId}/withdrawals
    // =========================================================================

    [Fact]
    public async Task POST_Withdraw_Vendedor_OnAjenoEntry_Returns403_Ownership()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var payload = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.PhysicalCash,
            Amount: 100m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: null,
            ApprovalRequestPublicId: null,
            Reference: null);

        var resp = await client.PostAsJsonAsync(
            $"/api/client-credit-entries/{seed.EntryAjenoPublicId}/withdrawals", payload);
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Withdraw_PhysicalCash_AboveLey25345_Returns409()
    {
        // Seed con saldo grande para poder pedir un withdraw > tope ley.
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6], initialBalance: 2_000_000m);
        var client = _factory.CreateClient(); // Admin -> bypass ownership.

        // Ley25345ThresholdAmount default = 1.000.000. Pidamos 1.500.000.
        var payload = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.PhysicalCash,
            Amount: 1_500_000m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: null,
            ApprovalRequestPublicId: null,
            Reference: null);

        var resp = await client.PostAsJsonAsync(
            $"/api/client-credit-entries/{seed.EntryOwnPublicId}/withdrawals", payload);

        // BusinessInvariantViolationException -> GlobalExceptionHandler -> 409
        // con invariantCode "INV-094" en el body.
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("INV-094", body);
    }

    [Fact]
    public async Task POST_Withdraw_KeptAsCredit_WithAmount_Returns400()
    {
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();

        var payload = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.KeptAsCredit,
            Amount: 500m, // invalido — KeptAsCredit exige 0
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: null,
            ApprovalRequestPublicId: null,
            Reference: null);

        var resp = await client.PostAsJsonAsync(
            $"/api/client-credit-entries/{seed.EntryOwnPublicId}/withdrawals", payload);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Withdraw_NotFound_Returns404()
    {
        await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();

        var payload = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.KeptAsCredit,
            Amount: 0m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: null,
            ApprovalRequestPublicId: null,
            Reference: null);

        // Admin bypassa ownership filter pero el entry no existe -> service tira
        // KeyNotFoundException -> 404.
        var resp = await client.PostAsJsonAsync(
            $"/api/client-credit-entries/{Guid.NewGuid()}/withdrawals", payload);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    // =========================================================================
    // FC4 fix I1: ownership de la reserva DESTINO al aplicar saldo a favor.
    // El bolsillo origen ya lo cubre RequireOwnership(ClientCreditEntry); estos tests cubren que la reserva
    // destino (que viaja en el body) tambien respete la frontera por-reserva de Cobranzas.
    // =========================================================================

    [Fact]
    public async Task POST_Withdraw_Applied_Vendedor_ToAjenaReserva_SameCustomer_Returns403()
    {
        // El Vendedor es dueño del bolsillo (entryOwn) y de su propia reserva, pero la reserva DESTINO es del
        // MISMO cliente y a cargo de OTRO vendedor. Aunque INV-093 (mismo cliente) pasaria, el ownership de la
        // reserva destino NO: debe rechazar con 403 (igual que el alta de pago normal en PaymentsController).
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var payload = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: 500m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: seed.AjenaReservaPublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

        var resp = await client.PostAsJsonAsync(
            $"/api/client-credit-entries/{seed.EntryOwnPublicId}/withdrawals", payload);

        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Withdraw_Applied_Vendedor_ToOwnReserva_Returns201()
    {
        // Misma operacion pero la reserva destino es propia del Vendedor: debe permitir (201 Created).
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient();
        SetVendedorHeaders(client, seed.VendedorId);

        var payload = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: 500m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: seed.OwnReservaPublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

        var resp = await client.PostAsJsonAsync(
            $"/api/client-credit-entries/{seed.EntryOwnPublicId}/withdrawals", payload);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    [Fact]
    public async Task POST_Withdraw_Applied_Admin_ToAjenaReserva_Returns201()
    {
        // Admin ve todas las cobranzas (bypass de ownership): puede aplicar a la reserva del otro vendedor
        // siempre que sea del mismo cliente. Verifica que la guarda I1 NO sea un bloqueo absoluto, sino el
        // mismo scope view_all que el resto de Cobranzas.
        var seed = await SeedAsync(Guid.NewGuid().ToString("N")[..6]);
        var client = _factory.CreateClient(); // Admin por default

        var payload = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: 500m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: seed.AjenaReservaPublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

        var resp = await client.PostAsJsonAsync(
            $"/api/client-credit-entries/{seed.EntryOwnPublicId}/withdrawals", payload);

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
    }

    private record TestSeed(
        string VendedorId,
        Guid EntryOwnPublicId,
        Guid EntryAjenoPublicId,
        Guid BcOwnPublicId,
        Guid OwnReservaPublicId,
        Guid AjenaReservaPublicId);
}
