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
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// DISEÑO 1 (Estado de Cuenta) — cobertura END-TO-END del armado del extracto en ReservaService: el filtro
/// de comprobantes/cobros VIVOS, la traduccion de moneda ARCA->ISO, el cobro cruzado, la inclusion de los
/// cobros puente (AffectsCash=false) y el invariante "ClosingBalance por moneda == PorMoneda[moneda].Balance".
///
/// <para>Usa InMemory porque la logica de filtros (vivo/anulado/borrado) y de Includes vive en el service,
/// no en el builder puro (ese se prueba aparte en ReservaAccountStatementBuilderTests).</para>
/// </summary>
public class ReservaServiceAccountStatementTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceAccountStatementTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static IHttpContextAccessor BuildContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);
        return mock.Object;
    }

    private ReservaService BuildService(AppDbContext context)
    {
        var accessor = BuildContextAccessor("admin-1", "Admin");
        var resolver = BuildResolver("admin-1");
        return new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(),
                   NullLogger<ReservaService>.Instance, resolver, accessor);
    }

    private static Invoice LiveInvoice(int id, int tipo, decimal total, DateTime date, int pv = 1, long nro = 100, string monId = "PES")
        => new()
        {
            Id = id, ReservaId = 1, TipoComprobante = tipo, ImporteTotal = total,
            Resultado = "A", AnnulmentStatus = AnnulmentStatus.None, CAE = "12345",
            PuntoDeVenta = pv, NumeroComprobante = nro, MonId = monId, CreatedAt = date,
        };

    // ===================== Mono-moneda: factura + 2 cobros + NC, cuadra con Balance =====================

    [Fact]
    public async Task MonoCurrency_LedgerOrderedSignsAndClosingBalance()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var d = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // Venta confirmada = 100000 (un servicio resuelto). Facturado = 100000. Cobros = 80000. NC = 0.
        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-1", Name = "R", Status = EstadoReserva.Confirmed });
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Otro", ProductType = "Otro", Description = "s",
            Status = "Confirmado", Currency = "ARS", SalePrice = 100000m, NetCost = 0m,
            DepartureDate = d.AddDays(30), CreatedAt = d,
        });
        ctx.Invoices.Add(LiveInvoice(1, tipo: 1, total: 100000m, date: d.AddDays(1)));   // Factura A
        ctx.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 50000m, Currency = "ARS", Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(2) });
        ctx.Payments.Add(new Payment { Id = 2, ReservaId = 1, Amount = 30000m, Currency = "ARS", Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(3) });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var statement = await service.GetAccountStatementAsync("1", CancellationToken.None);

        var block = Assert.Single(statement.Currencies);
        Assert.Equal("ARS", block.Currency);
        Assert.Equal(3, block.Lines.Count);

        // Orden: Factura (+100k), Cobro (-50k), Cobro (-30k) -> saldo 20000.
        Assert.Equal(AccountStatementLineKinds.Invoice, block.Lines[0].Kind);
        Assert.Equal(100000m, block.Lines[0].RunningBalance);
        Assert.Equal(50000m, block.Lines[1].RunningBalance);
        Assert.Equal(20000m, block.Lines[2].RunningBalance);
        Assert.Equal(20000m, block.ClosingBalance);

        // Invariante: cierre del extracto == Balance por moneda de la reserva (facturado == confirmado aca).
        var reserva = await ctx.Reservas.Include(r => r.Servicios).Include(r => r.Payments).FirstAsync(r => r.Id == 1);
        var money = ReservaMoneyCalculator.Calculate(reserva);
        Assert.Equal(money.PorMoneda["ARS"].Balance, block.ClosingBalance);
    }

    // ===================== INVARIANTE DE DIVERGENCIA: facturacion PARCIAL =====================

    /// <summary>
    /// Fija (documenta y protege) la DIVERGENCIA esperada entre dos saldos que miran cosas distintas cuando
    /// lo facturado NO iguala lo confirmado:
    ///
    /// <list type="bullet">
    /// <item><b>Extracto (ClosingBalance)</b> = facturado neto - cobrado. Es un LIBRO MAYOR: solo refleja los
    /// DOCUMENTOS realmente emitidos (facturas/ND/NC) y los cobros. Si todavia no se facturo todo, el extracto
    /// cierra en lo que los comprobantes vivos dicen, no en lo que se vendio.</item>
    /// <item><b>Reserva (PorMoneda[moneda].Balance)</b> = ConfirmedSale - TotalPaid. Refleja lo CONFIRMADO
    /// (vendido y exigible), independientemente de cuanto se haya facturado.</item>
    /// </list>
    ///
    /// <para>COINCIDEN solo cuando lo facturado == lo confirmado (caso de los demas tests). Con facturacion
    /// PARCIAL DIVERGEN, y esa divergencia es CORRECTA: el extracto es un registro de documentos, el Balance es
    /// el estado de cobranza de la venta. Este test existe para que un "arreglo" futuro que intente forzar la
    /// igualdad (haciendo que el extracto sume ConfirmedSale, o que el Balance mire facturado) rompa aca y se
    /// piense dos veces antes de romper la semantica de libro mayor.</para>
    ///
    /// <para>Escenario: venta confirmada 100.000, factura emitida 60.000, cobro 60.000. Extracto: 60.000 cargo
    /// - 60.000 abono = 0. Reserva: 100.000 - 60.000 = 40.000.</para>
    /// </summary>
    [Fact]
    public async Task PartialInvoicing_StatementClosesAtInvoicedWhileBalanceReflectsConfirmed_DivergenceIsExpected()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var d = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

        // Venta confirmada = 100000 (un servicio resuelto). Pero solo se FACTURO 60000 (factura parcial) y se
        // cobro 60000. El resto (40000) esta confirmado y exigible, pero todavia no facturado.
        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-7", Name = "R", Status = EstadoReserva.Confirmed });
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Otro", ProductType = "Otro", Description = "s",
            Status = "Confirmado", Currency = "ARS", SalePrice = 100000m, NetCost = 0m,
            DepartureDate = d.AddDays(30), CreatedAt = d,
        });
        ctx.Invoices.Add(LiveInvoice(1, tipo: 1, total: 60000m, date: d.AddDays(1)));   // Factura A parcial (60k)
        ctx.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 60000m, Currency = "ARS", Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(2) });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var statement = await service.GetAccountStatementAsync("1", CancellationToken.None);

        // Extracto: 60000 cargo (factura) - 60000 abono (cobro) = 0. Refleja SOLO lo facturado.
        var block = Assert.Single(statement.Currencies);
        Assert.Equal("ARS", block.Currency);
        Assert.Equal(0m, block.ClosingBalance);

        // Reserva: ConfirmedSale(100000) - TotalPaid(60000) = 40000. Refleja lo CONFIRMADO.
        var reserva = await ctx.Reservas.Include(r => r.Servicios).Include(r => r.Payments).FirstAsync(r => r.Id == 1);
        var money = ReservaMoneyCalculator.Calculate(reserva);
        Assert.Equal(40000m, money.PorMoneda["ARS"].Balance);

        // La DIVERGENCIA es el punto: los dos saldos NO coinciden, y eso es esperado y correcto.
        Assert.NotEqual(money.PorMoneda["ARS"].Balance, block.ClosingBalance);
    }

    // ===================== Anulados / borrados NO aparecen =====================

    [Fact]
    public async Task AnnulledInvoiceAndDeletedPayment_DoNotAppear()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var d = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-2", Name = "R", Status = EstadoReserva.Confirmed });
        // Factura VIVA.
        ctx.Invoices.Add(LiveInvoice(1, tipo: 1, total: 100000m, date: d.AddDays(1), nro: 1));
        // Factura ANULADA (AnnulmentStatus = Succeeded): no debe aparecer.
        var annulled = LiveInvoice(2, tipo: 1, total: 999999m, date: d.AddDays(1), nro: 2);
        annulled.AnnulmentStatus = AnnulmentStatus.Succeeded;
        ctx.Invoices.Add(annulled);
        // Factura RECHAZADA (Resultado != "A"): no debe aparecer.
        var rejected = LiveInvoice(3, tipo: 1, total: 888888m, date: d.AddDays(1), nro: 3);
        rejected.Resultado = "R";
        ctx.Invoices.Add(rejected);

        // Cobro vivo + cobro borrado + cobro cancelado.
        ctx.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 40000m, Currency = "ARS", Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(2) });
        ctx.Payments.Add(new Payment { Id = 2, ReservaId = 1, Amount = 11111m, Currency = "ARS", Status = "Paid", AffectsCash = true, IsDeleted = true, PaidAt = d.AddDays(2) });
        ctx.Payments.Add(new Payment { Id = 3, ReservaId = 1, Amount = 22222m, Currency = "ARS", Status = "Cancelled", AffectsCash = true, PaidAt = d.AddDays(2) });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var statement = await service.GetAccountStatementAsync("1", CancellationToken.None);

        var block = Assert.Single(statement.Currencies);
        // Solo 2 lineas vivas: 1 factura + 1 cobro. Saldo = 100000 - 40000 = 60000.
        Assert.Equal(2, block.Lines.Count);
        Assert.Equal(60000m, block.ClosingBalance);
    }

    // ===================== Cobro cruzado USD->ARS cae en bloque ARS por ImputedAmount =====================

    [Fact]
    public async Task CrossCurrencyPayment_LandsInImputedBlock_WithDescription()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var d = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-3", Name = "R", Status = EstadoReserva.Confirmed });
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Otro", ProductType = "Otro", Description = "s",
            Status = "Confirmado", Currency = "ARS", SalePrice = 100000m, NetCost = 0m,
            DepartureDate = d.AddDays(30), CreatedAt = d,
        });
        ctx.Invoices.Add(LiveInvoice(1, tipo: 1, total: 100000m, date: d.AddDays(1)));
        // Cobro CRUZADO: entraron 50 USD imputados a ARS como 50000.
        ctx.Payments.Add(new Payment
        {
            Id = 1, ReservaId = 1, Amount = 50m, Currency = "USD",
            ImputedCurrency = "ARS", ImputedAmount = 50000m,
            Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(2),
        });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var statement = await service.GetAccountStatementAsync("1", CancellationToken.None);

        // Un solo bloque ARS (el cobro cruzado cae en la moneda imputada, no crea bloque USD).
        var block = Assert.Single(statement.Currencies);
        Assert.Equal("ARS", block.Currency);
        // Cierre = 100000 - 50000 = 50000, igual que el Balance ARS de la reserva.
        Assert.Equal(50000m, block.ClosingBalance);

        var reserva = await ctx.Reservas.Include(r => r.Servicios).Include(r => r.Payments).FirstAsync(r => r.Id == 1);
        var money = ReservaMoneyCalculator.Calculate(reserva);
        Assert.Equal(money.PorMoneda["ARS"].Balance, block.ClosingBalance);

        // La descripcion del cobro cruzado aclara la moneda REAL recibida (USD).
        var paymentLine = block.Lines.Single(l => l.Kind == AccountStatementLineKinds.Payment);
        Assert.Contains("USD", paymentLine.Description);
        Assert.Equal(50000m, paymentLine.Credit); // se muestra el monto imputado
    }

    // ===================== Cobro puente (AffectsCash=false) aparece y baja la deuda =====================

    [Fact]
    public async Task BridgePayment_AppearsAsCreditAppliedAndReducesBalance()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var d = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-4", Name = "R", Status = EstadoReserva.Confirmed });
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Otro", ProductType = "Otro", Description = "s",
            Status = "Confirmado", Currency = "ARS", SalePrice = 100000m, NetCost = 0m,
            DepartureDate = d.AddDays(30), CreatedAt = d,
        });
        ctx.Invoices.Add(LiveInvoice(1, tipo: 1, total: 100000m, date: d.AddDays(1)));
        // Cobro real + cobro PUENTE (saldo a favor aplicado, no movio caja).
        ctx.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 60000m, Currency = "ARS", Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(2) });
        ctx.Payments.Add(new Payment { Id = 2, ReservaId = 1, Amount = 40000m, Currency = "ARS", Status = "Paid", AffectsCash = false, PaidAt = d.AddDays(3) });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var statement = await service.GetAccountStatementAsync("1", CancellationToken.None);

        var block = Assert.Single(statement.Currencies);
        // 1 factura + 2 cobros (uno real, uno puente) -> saldo 0.
        Assert.Equal(3, block.Lines.Count);
        Assert.Equal(0m, block.ClosingBalance);

        // El puente aparece con descripcion clara.
        var bridgeLine = block.Lines.Single(l => l.Description == "Saldo a favor aplicado");
        Assert.Equal(40000m, bridgeLine.Credit);

        var reserva = await ctx.Reservas.Include(r => r.Servicios).Include(r => r.Payments).FirstAsync(r => r.Id == 1);
        var money = ReservaMoneyCalculator.Calculate(reserva);
        Assert.Equal(money.PorMoneda["ARS"].Balance, block.ClosingBalance);
    }

    // ===================== NC abona, ND carga (via tipos AFIP reales) =====================

    [Fact]
    public async Task DebitNoteCharges_CreditNoteCredits()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var d = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);

        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-5", Name = "R", Status = EstadoReserva.Confirmed });
        ctx.Invoices.Add(LiveInvoice(1, tipo: 1, total: 100000m, date: d.AddDays(1), nro: 1));   // Factura A -> +100k
        ctx.Invoices.Add(LiveInvoice(2, tipo: 2, total: 5000m, date: d.AddDays(2), nro: 2));      // ND A -> +5k
        ctx.Invoices.Add(LiveInvoice(3, tipo: 3, total: 30000m, date: d.AddDays(3), nro: 3));     // NC A -> -30k
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var statement = await service.GetAccountStatementAsync("1", CancellationToken.None);

        var block = Assert.Single(statement.Currencies);
        Assert.Equal(3, block.Lines.Count);
        Assert.Equal(AccountStatementLineKinds.DebitNote, block.Lines[1].Kind);
        Assert.Equal(5000m, block.Lines[1].Charge);
        Assert.Equal(AccountStatementLineKinds.CreditNote, block.Lines[2].Kind);
        Assert.Equal(30000m, block.Lines[2].Credit);
        // 100k + 5k - 30k = 75k.
        Assert.Equal(75000m, block.ClosingBalance);
    }

    // ===================== Multimoneda: 2 bloques, factura USD cae en bloque USD =====================

    [Fact]
    public async Task MultiCurrency_UsdInvoiceLandsInUsdBlock()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var d = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        ctx.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-6", Name = "R", Status = EstadoReserva.Confirmed });
        ctx.Invoices.Add(LiveInvoice(1, tipo: 1, total: 80000m, date: d.AddDays(1), nro: 1, monId: "PES"));  // ARS
        ctx.Invoices.Add(LiveInvoice(2, tipo: 1, total: 1000m, date: d.AddDays(1), nro: 2, monId: "DOL"));   // USD
        ctx.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 80000m, Currency = "ARS", Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(2) });
        ctx.Payments.Add(new Payment { Id = 2, ReservaId = 1, Amount = 600m, Currency = "USD", Status = "Paid", AffectsCash = true, PaidAt = d.AddDays(2) });
        await ctx.SaveChangesAsync();

        var service = BuildService(ctx);
        var statement = await service.GetAccountStatementAsync("1", CancellationToken.None);

        Assert.Equal(2, statement.Currencies.Count);
        var ars = statement.Currencies.Single(b => b.Currency == "ARS");
        var usd = statement.Currencies.Single(b => b.Currency == "USD");
        Assert.Equal(0m, ars.ClosingBalance);   // 80k - 80k
        Assert.Equal(400m, usd.ClosingBalance);  // 1000 - 600
        // La factura USD se mapeo correctamente desde MonId "DOL".
        Assert.All(usd.Lines, line => Assert.Equal("USD", line.Currency));
    }

    // ===================== Contrato: la respuesta NO contiene costo =====================

    [Fact]
    public void AccountStatementDtos_HaveNoCostFields_ContractGuard()
    {
        var lineProps = typeof(AccountStatementLineDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Cost", lineProps);
        Assert.DoesNotContain("NetCost", lineProps);
        Assert.DoesNotContain("TotalCost", lineProps);
        Assert.DoesNotContain("Margin", lineProps);
        Assert.DoesNotContain("Commission", lineProps);

        var blockProps = typeof(AccountStatementCurrencyBlockDto).GetProperties().Select(p => p.Name).ToList();
        Assert.DoesNotContain("Cost", blockProps);
        Assert.DoesNotContain("Margin", blockProps);
    }

    // ===================== Reserva inexistente: KeyNotFound =====================

    [Fact]
    public async Task UnknownReserva_Throws()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var service = BuildService(ctx);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetAccountStatementAsync("999", CancellationToken.None));
    }
}
