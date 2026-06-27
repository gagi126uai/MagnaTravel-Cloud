using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-022 Tanda 2 (capas 5-6): fix P1 (la deuda del proveedor se recalcula tambien desde el servicio
/// GENERICO), P4 (imputacion del pago a proveedor a una reserva o anticipo "a cuenta") y T3 (bloqueo duro de
/// categorias manuales que duplican una puerta propia).
///
/// <para><b>Nota InMemory</b>: el provider InMemory no aplica CHECK constraints ni el indice unico parcial.
/// Estos tests verifican el COMPORTAMIENTO de los servicios (que numero queda en la cuenta del proveedor,
/// que pagos se aceptan/rechazan, que categorias se bloquean). La paridad de cuenta se prueba comparando el
/// resultado de ReservaService contra el de SupplierService sobre el mismo dato.</para>
/// </summary>
public class Adr022Tanda2Tests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // ====================================================================================
    // Harness: ReservaService con un caller que SI ve costos (el request manda, como Admin/cobranzas).
    // ====================================================================================

    private static ReservaService BuildReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "tester";
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = BuildResolver(userId, Permissions.CobranzasSeeCost);

        return new ReservaService(
            context,
            Mock.Of<IMapper>(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    // ====================================================================================
    // Seeds
    // ====================================================================================

    private static async Task<(Supplier supplier, Reserva reserva)> SeedSupplierAndReservaAsync(
        AppDbContext context, string currency = Monedas.ARS)
    {
        var supplier = new Supplier { Id = 1, Name = "Operador Test", CurrentBalance = 0m };
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (supplier, reserva);
    }

    /// <summary>Servicio generico CONFIRMADO de un proveedor (genera deuda). Status="Confirmado" cuenta.</summary>
    private static ServicioReserva NewConfirmedGenericService(
        int id, int reservaId, int supplierId, decimal netCost, string currency = Monedas.ARS) => new()
    {
        Id = id,
        ReservaId = reservaId,
        SupplierId = supplierId,
        ServiceType = "Excursion",
        ProductType = "Excursion",
        Description = "Excursion",
        ConfirmationNumber = "OK",
        Status = "Confirmado",
        Currency = currency,
        DepartureDate = DateTime.UtcNow.AddDays(10),
        SalePrice = netCost + 50m,
        NetCost = netCost,
        Commission = 50m,
        CreatedAt = DateTime.UtcNow,
    };

    private static AddServiceRequest BuildGenericRequest(
        string? supplierPublicId, decimal netCost = 100m, decimal salePrice = 160m) => new(
        ServiceType: "Excursion",
        SupplierId: supplierPublicId,
        Description: "Excursion",
        ConfirmationNumber: "OK",
        DepartureDate: DateTime.UtcNow.AddDays(10),
        ReturnDate: null,
        SalePrice: salePrice,
        NetCost: netCost);

    // Lee el saldo persistido del proveedor (escalar + tabla hija por moneda).
    private static async Task<(decimal Scalar, decimal ArsBalance)> ReadSupplierBalanceAsync(
        AppDbContext context, int supplierId)
    {
        var supplier = await context.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplierId);
        var ars = await context.SupplierBalanceByCurrency.AsNoTracking()
            .Where(r => r.SupplierId == supplierId && r.Currency == Monedas.ARS)
            .Select(r => (decimal?)r.Balance)
            .FirstOrDefaultAsync() ?? 0m;
        return (supplier.CurrentBalance, ars);
    }

    // ====================================================================================
    // P1 — el servicio generico recalcula la deuda del proveedor
    // ====================================================================================

    [Fact]
    public async Task AddGenericService_WithSupplier_RecalculatesSupplierDebt()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        // Ya existe un servicio confirmado por 300 PERO el escalar del proveedor quedo STALE en 0 (es el bug
        // P1: nadie lo recalculo). Al agregar OTRO servicio generico del mismo proveedor, ReservaService debe
        // disparar el recalculo y dejar la deuda al dia.
        context.Servicios.Add(NewConfirmedGenericService(id: 50, reserva.Id, supplier.Id, netCost: 300m));
        await context.SaveChangesAsync();

        var service = BuildReservaService(context);
        await service.AddServiceAsync(
            reserva.Id, BuildGenericRequest(supplier.PublicId.ToString(), netCost: 100m), CancellationToken.None);

        // El nuevo servicio nace "Solicitado" (no cuenta aun); pero el recalculo levanta el confirmado de 300.
        var (scalar, ars) = await ReadSupplierBalanceAsync(context, supplier.Id);
        Assert.Equal(300m, scalar);
        Assert.Equal(300m, ars);
    }

    [Fact]
    public async Task RemoveGenericService_WithSupplier_RecalculatesSupplierDebt()
    {
        // Un servicio CONFIRMADO no se puede BORRAR (se cancela). El flujo de borrado real es sobre un
        // servicio "Solicitado" (todavia sin confirmar = sin deuda). Borrarlo dispara el recalculo del
        // proveedor, que debe dejar la deuda correcta (la del OTRO servicio confirmado), no corromperla.
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        // Confirmado por 250 (deuda real) + escalar STALE en 0 (bug P1).
        context.Servicios.Add(NewConfirmedGenericService(id: 60, reserva.Id, supplier.Id, netCost: 250m));
        // Solicitado por 999 del mismo proveedor (no cuenta como deuda; es el que vamos a borrar).
        var solicitado = new ServicioReserva
        {
            Id = 61, ReservaId = reserva.Id, SupplierId = supplier.Id,
            ServiceType = "Excursion", ProductType = "Excursion", Description = "Solic", ConfirmationNumber = "PENDIENTE",
            Status = "Solicitado", Currency = Monedas.ARS, DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = 1100m, NetCost = 999m, Commission = 101m, CreatedAt = DateTime.UtcNow,
        };
        context.Servicios.Add(solicitado);
        await context.SaveChangesAsync();

        var service = BuildReservaService(context);
        await service.RemoveServiceAsync(solicitado.Id, CancellationToken.None);

        // Tras borrar el Solicitado, la deuda queda en 250 (la del confirmado) — el recalculo corrio y dio
        // el numero correcto, no quedo stale en 0 ni sumo el Solicitado borrado.
        var (scalar, ars) = await ReadSupplierBalanceAsync(context, supplier.Id);
        Assert.Equal(250m, scalar);
        Assert.Equal(250m, ars);
    }

    [Fact]
    public async Task UpdateGenericService_ChangeSupplier_RecalculatesOldAndNewSupplier()
    {
        await using var context = CreateContext();
        var (oldSupplier, reserva) = await SeedSupplierAndReservaAsync(context);
        var newSupplier = new Supplier { Id = 2, Name = "Operador Nuevo", CurrentBalance = 0m };
        context.Suppliers.Add(newSupplier);

        // Servicio confirmado del proveedor VIEJO por 400, deuda al dia.
        var confirmed = NewConfirmedGenericService(id: 70, reserva.Id, oldSupplier.Id, netCost: 400m);
        context.Servicios.Add(confirmed);
        oldSupplier.CurrentBalance = 400m;
        context.SupplierBalanceByCurrency.Add(new SupplierBalanceByCurrency
        {
            SupplierId = oldSupplier.Id, Currency = Monedas.ARS, ConfirmedPurchases = 400m, TotalPaid = 0m, Balance = 400m,
        });
        await context.SaveChangesAsync();

        var service = BuildReservaService(context);
        // Editamos el servicio reasignandolo al proveedor NUEVO (request lleva su publicId).
        var request = new AddServiceRequest(
            ServiceType: "Excursion",
            SupplierId: newSupplier.PublicId.ToString(),
            Description: "Excursion",
            ConfirmationNumber: "OK",
            DepartureDate: DateTime.UtcNow.AddDays(10),
            ReturnDate: null,
            SalePrice: 450m,
            NetCost: 400m);
        await service.UpdateServiceAsync(confirmed.Id, request, CancellationToken.None);

        // El proveedor VIEJO ya no tiene el servicio -> deuda 0. El NUEVO lo gana -> deuda 400.
        var (oldScalar, _) = await ReadSupplierBalanceAsync(context, oldSupplier.Id);
        var (newScalar, _) = await ReadSupplierBalanceAsync(context, newSupplier.Id);
        Assert.Equal(0m, oldScalar);
        Assert.Equal(400m, newScalar);
    }

    [Fact]
    public async Task GenericService_Recalc_MatchesSupplierServiceExactly()
    {
        // Paridad: el numero que deja ReservaService debe ser IDENTICO al que daria SupplierService sobre el
        // mismo dato. Tras la operacion de ReservaService, correr SupplierService.UpdateBalanceAsync no debe
        // cambiar nada (ya esta sincronizado).
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        context.Servicios.Add(NewConfirmedGenericService(id: 80, reserva.Id, supplier.Id, netCost: 333m));
        await context.SaveChangesAsync();

        var reservaService = BuildReservaService(context);
        await reservaService.AddServiceAsync(
            reserva.Id, BuildGenericRequest(supplier.PublicId.ToString(), netCost: 50m), CancellationToken.None);

        var (scalarAfterReserva, arsAfterReserva) = await ReadSupplierBalanceAsync(context, supplier.Id);

        // Ahora SupplierService recalcula: si ReservaService dejo el numero correcto, no cambia.
        var supplierService = new SupplierService(context);
        await supplierService.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var (scalarAfterSupplier, arsAfterSupplier) = await ReadSupplierBalanceAsync(context, supplier.Id);
        Assert.Equal(scalarAfterReserva, scalarAfterSupplier);
        Assert.Equal(arsAfterReserva, arsAfterSupplier);
        Assert.Equal(333m, scalarAfterSupplier); // confirmado de 333; el nuevo (Solicitado) no cuenta
    }

    [Fact]
    public void SupplierService_And_Persister_ShareSameValidReservationStatuses()
    {
        // fix #4: paridad ESTRUCTURAL (no de un escenario sembrado). Los dos consumidores de la lista de
        // estados "vivos" del proveedor (SupplierService y SupplierDebtPersister) deben usar EXACTAMENTE el
        // mismo conjunto, o la deuda saldria distinta segun el camino. Ahora ambos referencian la fuente unica
        // en Domain (SupplierDebtCalculator.ValidReservationStatuses); este test lo candamos por reflexion.
        var fromSupplierService = (string[])typeof(SupplierService)
            .GetField("ValidReservationStatuses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var fromPersister = (string[])typeof(TravelApi.Infrastructure.Reservations.SupplierDebtPersister)
            .GetField("ValidReservationStatuses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        // Igualdad de secuencia (mismo contenido y orden) contra la fuente unica de Domain.
        Assert.Equal(SupplierDebtCalculator.ValidReservationStatuses, fromSupplierService);
        Assert.Equal(SupplierDebtCalculator.ValidReservationStatuses, fromPersister);
        Assert.Equal(fromSupplierService, fromPersister);
    }

    [Fact]
    public async Task AddGenericService_WithoutSupplier_DoesNotTouchAnySupplier()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        supplier.CurrentBalance = 999m; // valor centinela: no debe tocarse (servicio sin proveedor)
        await context.SaveChangesAsync();

        var service = BuildReservaService(context);
        await service.AddServiceAsync(reserva.Id, BuildGenericRequest(supplierPublicId: null), CancellationToken.None);

        var supplierAfter = await context.Suppliers.AsNoTracking().SingleAsync(s => s.Id == supplier.Id);
        Assert.Equal(999m, supplierAfter.CurrentBalance);
    }

    // ====================================================================================
    // P4 — imputacion del pago a proveedor
    // ====================================================================================

    private static SupplierPaymentRequest PaymentImputedToReserva(
        decimal amount, string reservaPublicId, string currency = Monedas.ARS) =>
        new(Amount: amount, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reservaPublicId, ServicioReservaId: null, IsAdvanceToAccount: false, Currency: currency);

    [Fact]
    public async Task AddSupplierPayment_ImputedToReserva_WithinDebt_Succeeds()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        // Deuda en la reserva: un servicio confirmado de 500 ARS.
        context.Servicios.Add(NewConfirmedGenericService(id: 90, reserva.Id, supplier.Id, netCost: 500m));
        await context.SaveChangesAsync();
        await new SupplierService(context).UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var service = new SupplierService(context);
        var publicId = await service.AddSupplierPaymentAsync(
            supplier.Id, PaymentImputedToReserva(300m, reserva.PublicId.ToString()), CancellationToken.None);

        var payment = await context.SupplierPayments.SingleAsync();
        Assert.Equal(reserva.Id, payment.ReservaId);
        Assert.Equal(300m, payment.Amount);
        Assert.NotEqual(Guid.Empty, publicId);
    }

    [Fact]
    public async Task AddSupplierPayment_ImputedToReserva_ExceedsReservaDebt_Accepted_ExcessIsCredit()
    {
        // (2026-06-26, decision del dueño) Pagar a un operador imputado a una reserva POR ENCIMA de la deuda de
        // esa reserva ahora SE ACEPTA: el excedente queda como saldo a favor con el operador en esa moneda.
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        context.Servicios.Add(NewConfirmedGenericService(id: 91, reserva.Id, supplier.Id, netCost: 200m)); // ARS
        await context.SaveChangesAsync();
        await new SupplierService(context).UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var service = new SupplierService(context);
        // 1000 > deuda de la reserva (200 ARS): antes se rechazaba; ahora se acepta y el excedente (800) es credito.
        var publicId = await service.AddSupplierPaymentAsync(
            supplier.Id, PaymentImputedToReserva(1000m, reserva.PublicId.ToString()), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, publicId);
        Assert.Equal(1, await context.SupplierPayments.CountAsync());

        // La posicion ARS del operador queda como saldo a favor (-800): el excedente NO se pierde ni cambia de
        // moneda. Se lee de la tabla materializada (no enmascarada por see_cost).
        var ars = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.ARS);
        Assert.Equal(200m, ars.ConfirmedPurchases);
        Assert.Equal(1000m, ars.TotalPaid);
        Assert.Equal(-800m, ars.Balance); // 200 - 1000 = -800 saldo a favor
    }

    [Fact]
    public async Task AddSupplierPayment_ImputedToReserva_WrongCurrency_Rejected()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        // La deuda de la reserva esta en ARS; intentar imputar un pago USD a esa reserva no tiene deuda USD.
        context.Servicios.Add(NewConfirmedGenericService(id: 93, reserva.Id, supplier.Id, netCost: 500m, currency: Monedas.ARS));
        // Otro servicio USD en OTRA reserva para que el proveedor tenga deuda global USD (asi no frena el global).
        var usdReserva = new Reserva { Id = 3, NumeroReserva = "F-2026-0003", Name = "USD", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(usdReserva);
        context.Servicios.Add(NewConfirmedGenericService(id: 94, usdReserva.Id, supplier.Id, netCost: 1000m, currency: Monedas.USD));
        await context.SaveChangesAsync();
        await new SupplierService(context).UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var service = new SupplierService(context);
        // Pago USD (no cruzado) imputado a la reserva que solo debe ARS -> no hay deuda USD en esa reserva -> rechazo.
        var request = new SupplierPaymentRequest(
            Amount: 100m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null,
            IsAdvanceToAccount: false, Currency: Monedas.USD);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None));
        Assert.Equal(0, await context.SupplierPayments.CountAsync());
    }

    [Fact]
    public async Task AddSupplierPayment_ImputedToReserva_WithoutServicesOfThatSupplier_Rejected()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        // El proveedor tiene deuda GLOBAL (en otra reserva) pero NINGUN servicio en la reserva imputada.
        var otherReserva = new Reserva { Id = 2, NumeroReserva = "F-2026-0002", Name = "Otra", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(otherReserva);
        context.Servicios.Add(NewConfirmedGenericService(id: 95, otherReserva.Id, supplier.Id, netCost: 800m));
        await context.SaveChangesAsync();
        await new SupplierService(context).UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var service = new SupplierService(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddSupplierPaymentAsync(
                supplier.Id, PaymentImputedToReserva(100m, reserva.PublicId.ToString()), CancellationToken.None));
        Assert.Equal(0, await context.SupplierPayments.CountAsync());
    }

    [Fact]
    public async Task AddSupplierPayment_AdvanceToAccount_NoReserva_Succeeds()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        // Deuda global del proveedor para que el tope general permita el anticipo.
        context.Servicios.Add(NewConfirmedGenericService(id: 96, reserva.Id, supplier.Id, netCost: 700m));
        await context.SaveChangesAsync();
        await new SupplierService(context).UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var service = new SupplierService(context);
        var request = new SupplierPaymentRequest(
            Amount: 400m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true);

        var publicId = await service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None);

        var payment = await context.SupplierPayments.SingleAsync();
        Assert.Null(payment.ReservaId); // a cuenta = sin reserva
        Assert.Equal(400m, payment.Amount);
        Assert.NotEqual(Guid.Empty, publicId);
    }

    [Fact]
    public async Task AddSupplierPayment_ReservaAndAdvanceFlagTogether_Rejected()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);
        context.Servicios.Add(NewConfirmedGenericService(id: 97, reserva.Id, supplier.Id, netCost: 500m));
        await context.SaveChangesAsync();
        await new SupplierService(context).UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var service = new SupplierService(context);
        var request = new SupplierPaymentRequest(
            Amount: 100m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: true);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None));
        Assert.Equal(0, await context.SupplierPayments.CountAsync());
    }

    // ====================================================================================
    // T3 — bloqueo duro de categorias manuales que duplican una puerta propia
    // ====================================================================================

    private static TreasuryService BuildTreasuryService(AppDbContext context)
        => new(context, new EntityReferenceResolver(context));

    private static UpsertManualCashMovementRequest ManualRequest(string category) => new()
    {
        Direction = CashMovementDirections.Expense,
        Amount = 100m,
        OccurredAt = DateTime.UtcNow,
        Method = "Cash",
        Category = category,
        Description = "Movimiento manual",
    };

    [Theory]
    [InlineData("Cobro cliente")]
    [InlineData("Cobranza")]
    [InlineData("cobro")]
    [InlineData("Pago Proveedor")]
    [InlineData("pago a proveedor")]
    [InlineData("Pago Operador")]
    public async Task CreateManualMovement_BlockedCategory_Rejected(string category)
    {
        await using var context = CreateContext();
        var service = BuildTreasuryService(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateManualMovementAsync(ManualRequest(category), "tester", CancellationToken.None));

        Assert.Equal(0, await context.ManualCashMovements.CountAsync());
        Assert.Equal(0, await context.CashLedgerEntries.CountAsync());
    }

    [Theory]
    [InlineData("Gastos de oficina")]
    [InlineData("Ajuste de caja")]
    [InlineData("Combustible")]
    public async Task CreateManualMovement_FreeCategory_Succeeds(string category)
    {
        await using var context = CreateContext();
        var service = BuildTreasuryService(context);

        var dto = await service.CreateManualMovementAsync(ManualRequest(category), "tester", CancellationToken.None);

        Assert.Equal(category, dto.Category);
        Assert.Equal(1, await context.ManualCashMovements.CountAsync());
        // El asiento de caja del gasto manual tambien se crea (capa 2).
        Assert.Equal(1, await context.CashLedgerEntries.CountAsync());
    }

    [Fact]
    public async Task CreateManualMovement_WithCurrency_StoresAndAssentsInThatCurrency()
    {
        await using var context = CreateContext();
        var service = BuildTreasuryService(context);

        var request = ManualRequest("Gastos de oficina");
        request.Currency = Monedas.USD;

        await service.CreateManualMovementAsync(request, "tester", CancellationToken.None);

        var manual = await context.ManualCashMovements.SingleAsync();
        Assert.Equal(Monedas.USD, manual.Currency);
        var ledger = await context.CashLedgerEntries.SingleAsync();
        Assert.Equal(Monedas.USD, ledger.Currency); // T2: el asiento toma la moneda del manual
    }

    [Fact]
    public void CategoryRules_IsBlocked_NormalizesAccentsAndSpaces()
    {
        // Variantes de tipeo que igual deben bloquearse (sin acentos, mayusculas, espacios extra).
        Assert.True(ManualCashMovementCategoryRules.IsBlocked("  Pago  Proveedor  "));
        Assert.True(ManualCashMovementCategoryRules.IsBlocked("COBRANZA"));
        // Libres: no bloquean.
        Assert.False(ManualCashMovementCategoryRules.IsBlocked("Gastos varios"));
        Assert.False(ManualCashMovementCategoryRules.IsBlocked(null));
        Assert.False(ManualCashMovementCategoryRules.IsBlocked(""));
    }
}
