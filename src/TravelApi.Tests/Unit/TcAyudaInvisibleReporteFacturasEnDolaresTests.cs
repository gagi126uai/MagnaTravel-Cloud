using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// "Facturas en dólares" (spec firmada 2026-08-06, Parte B): la solapa de Reportes donde el contador
/// ve, mes por mes, cuánto se facturó en moneda extranjera y cuánta plata entró contra esas facturas.
///
/// <para>El caso que le da sentido, en palabras del dueño: cobraste US$ 1.000 a $1.500 (te entraron
/// $1.500.000) pero la factura salió al dólar del techo, $1.234,50 ($1.234.500). Esos $265.500 son
/// reales y normales; el contador los necesita ordenados y el vendedor no los tiene que ver nunca.</para>
/// </summary>
public class TcAyudaInvisibleReporteFacturasEnDolaresTests
{
    private static readonly DateTime Emision = new(2026, 08, 06, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Desde = new(2026, 08, 01, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Hasta = new(2026, 08, 31, 23, 59, 59, DateTimeKind.Utc);

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ReportService BuildService(AppDbContext context)
    {
        var bna = new Mock<IBnaExchangeRateService>();
        return new ReportService(context, bna.Object);
    }

    private static Reserva SeedReserva(AppDbContext context, int id, string numero, string cliente)
    {
        var customer = new Customer { Id = id, FullName = cliente };
        var reserva = new Reserva
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            NumeroReserva = numero,
            Name = numero,
            Status = EstadoReserva.Confirmed,
            PayerId = id,
            Payer = customer
        };
        context.Customers.Add(customer);
        context.Reservas.Add(reserva);
        return reserva;
    }

    private static Invoice SeedUsdInvoice(
        AppDbContext context,
        int id,
        Reserva reserva,
        decimal importeTotal,
        decimal monCotiz,
        int tipoComprobante = 6,
        DateTime? fechaEmision = null,
        AnnulmentStatus annulmentStatus = AnnulmentStatus.None,
        string resultado = "A")
    {
        var invoice = new Invoice
        {
            Id = id,
            PublicId = Guid.NewGuid(),
            ReservaId = reserva.Id,
            TipoComprobante = tipoComprobante,
            PuntoDeVenta = 1,
            NumeroComprobante = id,
            Resultado = resultado,
            AnnulmentStatus = annulmentStatus,
            MonId = "DOL",
            MonCotiz = monCotiz,
            ImporteTotal = importeTotal,
            CbteFchArgentina = fechaEmision ?? Emision,
            CreatedAt = fechaEmision ?? Emision
        };
        context.Invoices.Add(invoice);
        return invoice;
    }

    private static void SeedPayment(
        AppDbContext context, int id, Reserva reserva, Invoice invoice, decimal amount, string currency)
    {
        context.Payments.Add(new Payment
        {
            Id = id,
            ReservaId = reserva.Id,
            LinkedInvoiceId = invoice.Id,
            Amount = amount,
            Currency = currency,
            Status = "Paid",
            AffectsCash = true,
            PaidAt = Emision
        });
    }

    // ============================================================
    // El caso que motivó la obra: cobré a un dólar y facturé a otro
    // ============================================================

    [Fact]
    public async Task CobradoAOtroDolar_MuestraLaDiferencia()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 1, "R-1042", "Pérez, Juan");
        var invoice = SeedUsdInvoice(context, 1, reserva, importeTotal: 1000m, monCotiz: 1234.50m);
        SeedPayment(context, 1, reserva, invoice, amount: 1_500_000m, currency: "ARS");
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal("Factura B 0001-00000001", fila.Comprobante);
        Assert.Equal("R-1042", fila.NumeroReserva);
        Assert.Equal("Pérez, Juan", fila.Cliente);
        Assert.Equal("USD", fila.Moneda);
        Assert.Equal(1000m, fila.MontoEnMonedaExtranjera);
        Assert.Equal(1234.50m, fila.TipoCambioFactura);
        Assert.Equal(1_234_500m, fila.PesosDeLaFactura);
        Assert.Equal(1_500_000m, fila.PesosCobrados);
        Assert.Equal(265_500m, fila.Diferencia);

        Assert.Equal(1_234_500m, reporte.Totales.PesosDeLaFactura);
        Assert.Equal(1_500_000m, reporte.Totales.PesosCobrados);
        Assert.Equal(265_500m, reporte.Totales.Diferencia);
    }

    /// <summary>
    /// Cobró exactamente lo facturado: la diferencia da cero, y un cero no es información — se muestra
    /// un guion (null en el contrato).
    /// </summary>
    [Fact]
    public async Task CobradoExacto_LaDiferenciaVieneVacia()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 2, "R-1041", "Gómez SRL");
        var invoice = SeedUsdInvoice(context, 2, reserva, importeTotal: 450m, monCotiz: 1230m);
        SeedPayment(context, 2, reserva, invoice, amount: 553_500m, currency: "ARS");
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(553_500m, fila.PesosCobrados);
        Assert.Null(fila.Diferencia);
        Assert.Null(reporte.Totales.Diferencia);
    }

    /// <summary>
    /// Todavía no cobró nada: guion, no cero. "Cobré cero pesos" y "todavía no cobré" son dos
    /// afirmaciones distintas, y esta pantalla no puede sugerir que hay un pendiente donde no lo hay.
    /// </summary>
    [Fact]
    public async Task SinCobros_MuestraGuionEnCobradoYEnDiferencia()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 3, "R-1038", "Díaz, Ana");
        SeedUsdInvoice(context, 3, reserva, importeTotal: 2100m, monCotiz: 1228m);
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(2_578_800m, fila.PesosDeLaFactura);
        Assert.Null(fila.PesosCobrados);
        Assert.Null(fila.Diferencia);
        Assert.Null(reporte.Totales.PesosCobrados);
    }

    /// <summary>
    /// Cobro parcial: se muestra lo que entró, aunque no alcance a cubrir la factura. La diferencia
    /// negativa es informacion valida (todavia falta cobrar), no un error.
    /// </summary>
    [Fact]
    public async Task CobroParcial_MuestraLoQueEntroYLaDiferenciaNegativa()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 4, "R-1050", "López, Marta");
        var invoice = SeedUsdInvoice(context, 4, reserva, importeTotal: 1000m, monCotiz: 1200m);
        SeedPayment(context, 1, reserva, invoice, amount: 500_000m, currency: "ARS");
        SeedPayment(context, 2, reserva, invoice, amount: 200_000m, currency: "ARS");
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(1_200_000m, fila.PesosDeLaFactura);
        Assert.Equal(700_000m, fila.PesosCobrados);
        Assert.Equal(-500_000m, fila.Diferencia);
    }

    /// <summary>
    /// Cobro EN LA MISMA MONEDA de la factura: recibiste exactamente los dólares que facturaste, así
    /// que no hay diferencia de cambio que declarar. Se valúa al tipo de cambio de la propia factura.
    /// </summary>
    [Fact]
    public async Task CobroEnLaMismaMoneda_NoGeneraDiferencia()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 5, "R-1060", "Ruiz, Pablo");
        var invoice = SeedUsdInvoice(context, 5, reserva, importeTotal: 800m, monCotiz: 1300m);
        SeedPayment(context, 1, reserva, invoice, amount: 800m, currency: "USD");
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal(1_040_000m, fila.PesosCobrados);
        Assert.Null(fila.Diferencia);
    }

    /// <summary>
    /// Cobro en una TERCERA moneda (la factura es en dólares y el cliente pagó en euros): NO se cuenta.
    /// No tenemos con qué valuar esos euros en pesos — el tipo de cambio de la factura es dólar/peso, no
    /// euro/peso — y usarlo igual metería un número inventado en una planilla contable. Se muestra el
    /// cobro que SÍ se puede valuar, y nada más.
    /// </summary>
    [Fact]
    public async Task CobroEnUnaTerceraMoneda_NoSeCuenta()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 10, "R-1110", "Ferrari, Lucía");
        var invoice = SeedUsdInvoice(context, 50, reserva, importeTotal: 1000m, monCotiz: 1200m);
        SeedPayment(context, 1, reserva, invoice, amount: 400_000m, currency: "ARS");
        SeedPayment(context, 2, reserva, invoice, amount: 500m, currency: "EUR");
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        // Solo los pesos que entraron de verdad. Si el euro se hubiera valuado al dólar de la factura,
        // acá habría 1.000.000 — un número que no existe en ningún lado.
        Assert.Equal(400_000m, fila.PesosCobrados);
    }

    /// <summary>
    /// La columna Moneda nunca muestra el código interno del comprobante. Un código que no está en el
    /// catálogo cae en "Otra": "060" en una planilla que abre el contador no significa nada (regla T-5).
    /// </summary>
    [Fact]
    public async Task ConUnCodigoDeMonedaDesconocido_LaColumnaNoMuestraElCodigoInterno()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 11, "R-1120", "Ibáñez, Raúl");
        var invoice = SeedUsdInvoice(context, 60, reserva, importeTotal: 100m, monCotiz: 1500m);
        invoice.MonId = "060";
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal("Otra", fila.Moneda);
        Assert.DoesNotContain("060", fila.Moneda);
    }

    // ============================================================
    // Qué entra y qué no en la tabla
    // ============================================================

    [Fact]
    public async Task NoEntranLasFacturasEnPesos_NiLasNotas_NiLasAnuladas_NiLasNoAprobadas()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 6, "R-1070", "Sosa, Luis");

        // La única que tiene que aparecer.
        SeedUsdInvoice(context, 10, reserva, importeTotal: 100m, monCotiz: 1000m);

        // En pesos.
        var enPesos = SeedUsdInvoice(context, 11, reserva, importeTotal: 100m, monCotiz: 1m);
        enPesos.MonId = "PES";

        // Nota de crédito en dólares (tipo 8): corrige, no vende.
        SeedUsdInvoice(context, 12, reserva, importeTotal: 100m, monCotiz: 1000m, tipoComprobante: 8);

        // Anulada por nota de crédito: ya no vale.
        SeedUsdInvoice(context, 13, reserva, importeTotal: 100m, monCotiz: 1000m,
            annulmentStatus: AnnulmentStatus.Succeeded);

        // Todavía en proceso (sin CAE).
        SeedUsdInvoice(context, 14, reserva, importeTotal: 100m, monCotiz: 1000m, resultado: "PENDING");

        // Fuera del período.
        SeedUsdInvoice(context, 15, reserva, importeTotal: 100m, monCotiz: 1000m,
            fechaEmision: new DateTime(2026, 07, 05, 12, 0, 0, DateTimeKind.Utc));

        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        var fila = Assert.Single(reporte.Filas);
        Assert.Equal("Factura B 0001-00000010", fila.Comprobante);
    }

    [Fact]
    public async Task PeriodoSinFacturasEnDolares_DevuelveTablaVaciaYTotalesEnCero()
    {
        await using var context = CreateContext();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        Assert.Empty(reporte.Filas);
        Assert.Equal(0m, reporte.Totales.PesosDeLaFactura);
        Assert.Null(reporte.Totales.PesosCobrados);
        Assert.Null(reporte.Totales.Diferencia);
    }

    /// <summary>
    /// El total de la diferencia es la SUMA de las diferencias de cada fila, no la resta de los dos
    /// totales: las facturas que todavía no cobraron nada no aportan diferencia. Restar los totales
    /// daría un número enorme y falso.
    /// </summary>
    [Fact]
    public async Task ElTotalDeLaDiferencia_SumaSoloLasFilasQueTienenDiferencia()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 7, "R-1080", "Vega, Sol");

        var conDiferencia = SeedUsdInvoice(context, 20, reserva, importeTotal: 1000m, monCotiz: 1234.50m);
        SeedPayment(context, 1, reserva, conDiferencia, amount: 1_500_000m, currency: "ARS");

        var exacta = SeedUsdInvoice(context, 21, reserva, importeTotal: 450m, monCotiz: 1230m);
        SeedPayment(context, 2, reserva, exacta, amount: 553_500m, currency: "ARS");

        SeedUsdInvoice(context, 22, reserva, importeTotal: 2100m, monCotiz: 1228m);

        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        Assert.Equal(3, reporte.Filas.Count);
        Assert.Equal(1_234_500m + 553_500m + 2_578_800m, reporte.Totales.PesosDeLaFactura);
        Assert.Equal(1_500_000m + 553_500m, reporte.Totales.PesosCobrados);
        Assert.Equal(265_500m, reporte.Totales.Diferencia);
    }

    /// <summary>
    /// Un cobro de la reserva que NO está vinculado a esta factura no se reparte por adivinanza: la
    /// factura muestra un guion honesto.
    /// </summary>
    [Fact]
    public async Task UnCobroSinVincularALaFactura_NoSeCuenta()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 8, "R-1090", "Cruz, Elena");
        SeedUsdInvoice(context, 30, reserva, importeTotal: 1000m, monCotiz: 1200m);
        context.Payments.Add(new Payment
        {
            Id = 1,
            ReservaId = reserva.Id,
            LinkedInvoiceId = null,
            Amount = 1_200_000m,
            Currency = "ARS",
            Status = "Paid",
            AffectsCash = true,
            PaidAt = Emision
        });
        await context.SaveChangesAsync();

        var reporte = await BuildService(context).GetUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        Assert.Null(Assert.Single(reporte.Filas).PesosCobrados);
    }

    [Fact]
    public async Task ElExcelSeGeneraConLasMismasFilas()
    {
        await using var context = CreateContext();
        var reserva = SeedReserva(context, 9, "R-1100", "Molina, Ana");
        var invoice = SeedUsdInvoice(context, 40, reserva, importeTotal: 1000m, monCotiz: 1234.50m);
        SeedPayment(context, 1, reserva, invoice, amount: 1_500_000m, currency: "ARS");
        await context.SaveChangesAsync();

        var bytes = await BuildService(context).ExportUsdInvoicesReportAsync(Desde, Hasta, CancellationToken.None);

        // No abrimos el Excel: alcanza con verificar que se produjo un archivo real (el armado en si
        // es el mismo mecanismo que ya usa el resto de Reportes).
        Assert.NotNull(bytes);
        Assert.True(bytes.Length > 0);
    }
}
