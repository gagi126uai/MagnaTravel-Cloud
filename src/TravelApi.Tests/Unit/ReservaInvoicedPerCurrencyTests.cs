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
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-037 / cuadre POR MONEDA (2026-06-22): el DTO de la reserva expone, en cada linea de
/// <c>PorMoneda</c>, cuanto se FACTURO NETO y cuanto FALTA FACTURAR en ESA moneda (sin mezclar
/// ARS con USD, a diferencia de los escalares <c>FacturadoNeto</c>/<c>DisponibleParaFacturar</c>).
///
/// <para>Estos tests recorren el path real (<c>ReservaService.GetReservaByIdAsync</c>) con facturas
/// sembradas a mano, para verificar el cableado y los bordes que no se ven en el calculator puro:
/// mono-moneda coincide con el escalar; multimoneda no mezcla; una moneda con venta y sin factura
/// queda facturado 0 / falta = venta; una NC resta solo en su moneda.</para>
///
/// <para>El calculo puro vive en <c>ReservaInvoicingCuadreCalculatorTests</c>; aca probamos la
/// INTEGRACION (mapeo MonId->ISO, agrupacion por linea de PorMoneda, criterio TotalSale para la falta).</para>
/// </summary>
public class ReservaInvoicedPerCurrencyTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static ReservaService CreateService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        const string userId = "tester";
        // Admin: ve costos (no nos importa el masking aca; los campos de facturacion no se enmascaran de todos modos).
        var accessor = BuildHttpContextAccessor(userId, "Admin");
        var resolver = BuildResolver(userId, Permissions.CobranzasSeeCost);

        return new ReservaService(
            context,
            new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper(),
            settings.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            resolver,
            accessor);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
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

    /// <summary>Servicio generico RESUELTO ("Confirmado") que aporta venta a la moneda dada.</summary>
    private static ServicioReserva ConfirmedService(int id, int reservaId, string currency, decimal salePrice)
        => new()
        {
            Id = id,
            ReservaId = reservaId,
            ServiceType = "Excursion",
            ProductType = "Excursion",
            Description = "Servicio test",
            Status = "Confirmado",
            Currency = currency,
            DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = salePrice,
            NetCost = 0m,
            CreatedAt = DateTime.UtcNow
        };

    /// <summary>Factura/ND/NC VIVA (Resultado "A", no anulada) en la moneda ARCA dada ("PES"/"DOL").</summary>
    private static Invoice LiveInvoice(int reservaId, int tipoComprobante, decimal importe, string monIdArca, int numero)
        => new()
        {
            ReservaId = reservaId,
            TipoComprobante = tipoComprobante,
            ImporteTotal = importe,
            MonId = monIdArca,
            Resultado = "A",
            AnnulmentStatus = AnnulmentStatus.None,
            NumeroComprobante = numero,
            CreatedAt = DateTime.UtcNow,
        };

    private static ReservaMoneyLineDto LineOf(ReservaDto dto, string currency)
        => dto.PorMoneda.Single(line => line.Currency == currency);

    // ============================================================================================

    [Fact]
    public async Task MonoMoneda_FacturadoPorMoneda_CoincideConElEscalar()
    {
        await using var context = CreateContext();
        // El escalar DisponibleParaFacturar usa Reserva.TotalSale (columna persistida, que el flujo real
        // mantiene en sincronia con los servicios). Lo seteamos a la misma venta para que el invariante
        // mono-moneda (escalar == linea) sea honesto: la linea usa la venta CALCULADA de la moneda.
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed, TotalSale = 100_000m };
        context.Reservas.Add(reserva);
        context.Servicios.Add(ConfirmedService(10, reserva.Id, "ARS", salePrice: 100_000m));
        // Factura 80k en pesos (MonId "PES").
        context.Invoices.Add(LiveInvoice(reserva.Id, tipoComprobante: 11, importe: 80_000m, monIdArca: "PES", numero: 1));
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var ars = LineOf(dto, "ARS");
        Assert.Equal(80_000m, ars.FacturadoNeto);
        // Falta facturar por moneda = TotalSale de la moneda - facturado (mismo criterio que el escalar).
        Assert.Equal(100_000m - 80_000m, ars.DisponibleParaFacturar);
        // Invariante mono-moneda: la unica linea coincide con el escalar.
        Assert.Equal(dto.FacturadoNeto, ars.FacturadoNeto);
        Assert.Equal(dto.DisponibleParaFacturar, ars.DisponibleParaFacturar);
    }

    [Fact]
    public async Task Multimoneda_FacturaARS_y_VentaUSDsinFactura_NoMezcla()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        context.Servicios.AddRange(
            ConfirmedService(10, reserva.Id, "ARS", salePrice: 100_000m),
            ConfirmedService(11, reserva.Id, "USD", salePrice: 500m));
        // Solo se facturo en pesos (60k); el USD no tiene factura.
        context.Invoices.Add(LiveInvoice(reserva.Id, tipoComprobante: 11, importe: 60_000m, monIdArca: "PES", numero: 1));
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var ars = LineOf(dto, "ARS");
        Assert.Equal(60_000m, ars.FacturadoNeto);
        Assert.Equal(100_000m - 60_000m, ars.DisponibleParaFacturar);

        // USD: vendido pero NADA facturado -> facturado 0, falta = toda la venta USD.
        var usd = LineOf(dto, "USD");
        Assert.Equal(0m, usd.FacturadoNeto);
        Assert.Equal(500m, usd.DisponibleParaFacturar);
    }

    [Fact]
    public async Task Multimoneda_NotaDeCredito_RestaSoloEnSuMoneda()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        context.Servicios.AddRange(
            ConfirmedService(10, reserva.Id, "ARS", salePrice: 100_000m),
            ConfirmedService(11, reserva.Id, "USD", salePrice: 1_000m));
        context.Invoices.AddRange(
            LiveInvoice(reserva.Id, tipoComprobante: 1, importe: 100_000m, monIdArca: "PES", numero: 1), // Factura A ARS
            LiveInvoice(reserva.Id, tipoComprobante: 1, importe: 1_000m, monIdArca: "DOL", numero: 2),    // Factura A USD
            LiveInvoice(reserva.Id, tipoComprobante: 3, importe: 400m, monIdArca: "DOL", numero: 3));     // NC A USD
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var ars = LineOf(dto, "ARS");
        Assert.Equal(100_000m, ars.FacturadoNeto); // la NC en USD NO toca el ARS
        Assert.Equal(0m, ars.DisponibleParaFacturar);

        var usd = LineOf(dto, "USD");
        Assert.Equal(600m, usd.FacturadoNeto); // 1000 - 400
        Assert.Equal(1_000m - 600m, usd.DisponibleParaFacturar);
    }

    [Fact]
    public async Task VentaSinNingunaFactura_FaltaFacturarIgualAVenta()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        context.Servicios.Add(ConfirmedService(10, reserva.Id, "ARS", salePrice: 70_000m));
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var ars = LineOf(dto, "ARS");
        Assert.Equal(0m, ars.FacturadoNeto);
        Assert.Equal(70_000m, ars.DisponibleParaFacturar);
    }

    [Fact]
    public async Task FacturaAnuladaConSuNotaDeCredito_LaAnulacionSeCuentaUnaVez()
    {
        // FIX doble conteo (por moneda): una factura anulada (Succeeded) SIGUE sumando, y su Nota de Credito
        // resta. Antes la factura Succeeded se excluia del lado que suma Y la NC restaba -> la anulacion se
        // contaba DOS veces y el facturado neto se iba a negativo. Escenario realista de anulacion total +
        // refacturacion: factura 90k anulada + NC 90k + factura nueva 40k = facturado neto 40k.
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-1", Name = "R1", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        context.Servicios.Add(ConfirmedService(10, reserva.Id, "ARS", salePrice: 90_000m));

        // Factura original anulada (Succeeded): sigue contando como facturado.
        var anulada = LiveInvoice(reserva.Id, tipoComprobante: 11, importe: 90_000m, monIdArca: "PES", numero: 1);
        anulada.AnnulmentStatus = AnnulmentStatus.Succeeded;
        context.Invoices.Add(anulada);
        // NC C (tipo 13) por el total: resta los 90k anulados.
        context.Invoices.Add(LiveInvoice(reserva.Id, tipoComprobante: 13, importe: 90_000m, monIdArca: "PES", numero: 2));
        // Refacturacion parcial nueva de 40k.
        context.Invoices.Add(LiveInvoice(reserva.Id, tipoComprobante: 11, importe: 40_000m, monIdArca: "PES", numero: 3));
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var ars = LineOf(dto, "ARS");
        Assert.Equal(40_000m, ars.FacturadoNeto); // 90k (anulada, suma) - 90k (NC) + 40k (nueva)
        Assert.Equal(90_000m - 40_000m, ars.DisponibleParaFacturar);
    }

    [Fact]
    public async Task AnulacionTotal_FacturadoNetoCero()
    {
        // Anulacion TOTAL sin refacturar: factura 80k anulada (Succeeded) + NC 80k = facturado neto 0
        // (antes daba -80k porque la factura dejaba de sumar y la NC restaba).
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-2", Name = "R2", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        context.Servicios.Add(ConfirmedService(10, reserva.Id, "ARS", salePrice: 80_000m));

        var anulada = LiveInvoice(reserva.Id, tipoComprobante: 11, importe: 80_000m, monIdArca: "PES", numero: 1);
        anulada.AnnulmentStatus = AnnulmentStatus.Succeeded;
        context.Invoices.Add(anulada);
        context.Invoices.Add(LiveInvoice(reserva.Id, tipoComprobante: 13, importe: 80_000m, monIdArca: "PES", numero: 2));
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var ars = LineOf(dto, "ARS");
        Assert.Equal(0m, ars.FacturadoNeto);
        Assert.Equal(80_000m, ars.DisponibleParaFacturar); // queda todo por refacturar
    }

    [Fact]
    public async Task NotaDeCreditoParcial_RestaSoloLoAcreditado()
    {
        // NC PARCIAL: factura 100k viva + NC 30k = facturado neto 70k (antes daba -30k: la factura NO estaba
        // anulada pero el bug aparece igual cuando la factura cae en Succeeded; aca verificamos el caso parcial
        // canonico donde la factura sigue viva y la NC parcial solo resta lo acreditado).
        await using var context = CreateContext();
        var reserva = new Reserva { Id = 1, NumeroReserva = "R-3", Name = "R3", Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        context.Servicios.Add(ConfirmedService(10, reserva.Id, "ARS", salePrice: 100_000m));

        context.Invoices.Add(LiveInvoice(reserva.Id, tipoComprobante: 11, importe: 100_000m, monIdArca: "PES", numero: 1));
        context.Invoices.Add(LiveInvoice(reserva.Id, tipoComprobante: 13, importe: 30_000m, monIdArca: "PES", numero: 2));
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetReservaByIdAsync(reserva.PublicId.ToString(), CancellationToken.None);

        var ars = LineOf(dto, "ARS");
        Assert.Equal(70_000m, ars.FacturadoNeto);
        Assert.Equal(30_000m, ars.DisponibleParaFacturar);
    }
}
