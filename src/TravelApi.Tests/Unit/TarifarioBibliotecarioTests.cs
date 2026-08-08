using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// El BIBLIOTECARIO del tarifario (spec firmada 2026-08-07, §6 / M-16, M-17, M-24 + Q3=B).
///
/// <para>Lo que se protege acá, en criollo: el mismo hotel que quedó cargado tres veces (una por
/// habitación, porque nuestro propio formulario le pegaba la habitación al nombre) vuelve a ser UN hotel
/// con tres habitaciones — <b>y si el sistema se equivocó, se puede deshacer y no se perdió nada</b>.</para>
/// </summary>
public class TarifarioBibliotecarioTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static CatalogLibrarianService CreateLibrarian(AppDbContext context, string userId = "gaston")
    {
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new List<Claim> { new(ClaimTypes.NameIdentifier, userId) }, "Test"))
            }
        };
        return new CatalogLibrarianService(context, NullLogger<CatalogLibrarianService>.Instance, accessor);
    }

    private static Rate HotelRate(int id, string name, string city, bool active = true) => new()
    {
        Id = id,
        ServiceType = "Hotel",
        ProductName = name,
        HotelName = name,
        City = city,
        MealPlan = "Desayuno",
        NetCost = 100m,
        SalePrice = 160m,
        Currency = "USD",
        PriceUnit = "noche_habitacion",
        IsActive = active,
        CreatedAt = DateTime.UtcNow.AddDays(-60),
        SearchName = TextNormalizer.NormalizeForCatalog(name)
    };

    private static RateSupplierSale Sale(
        int rateId, int supplierId, decimal netCost, DateTime soldAt,
        string variantKey = "", string variantLabel = "")
        => new()
        {
            RateId = rateId,
            SupplierId = supplierId,
            LastSoldAt = soldAt,
            LastNetCost = netCost,
            LastTax = 0m,
            LastSalePrice = netCost + 20m,
            LastCurrency = "USD",
            LastPriceUnit = CatalogPriceUnits.NocheHabitacion,
            SalesCount = 1,
            VariantKey = variantKey,
            VariantLabel = variantLabel
        };

    // =====================================================================================
    // Separar la habitación que quedó metida en el nombre
    // =====================================================================================

    [Theory]
    [InlineData("Sheraton Iguazú - Doble Superior", "Sheraton Iguazú", "Doble Superior con desayuno")]
    [InlineData("Maitei Posadas - Triple", "Maitei Posadas", "Triple con desayuno")]
    [InlineData("Hotel Costa - Suite Presidencial", "Hotel Costa", "Suite Presidencial con desayuno")]
    public void ElNombreConHabitacionAdentro_SeSepara(string fullName, string expectedName, string expectedRoom)
    {
        var parsed = CatalogProductNameParser.ParseHotelName(fullName, "Desayuno");

        Assert.True(parsed.HadVariantInsideTheName);
        Assert.Equal(expectedName, parsed.CleanName);
        Assert.Equal(expectedRoom, parsed.VariantLabel);
    }

    /// <summary>
    /// En la duda NO se rompe nada: un hotel que de verdad se llama "Costa - Playa Grande" no tiene una
    /// habitación adentro del nombre, y el bibliotecario lo deja en paz.
    /// </summary>
    [Theory]
    [InlineData("Hotel Costa - Playa Grande")]
    [InlineData("Sheraton Iguazú")]
    [InlineData("Complejo Los Alamos - Cabañas")]
    public void UnNombreQueNoTieneHabitacionAdentro_NoSeToca(string fullName)
    {
        var parsed = CatalogProductNameParser.ParseHotelName(fullName, "Desayuno");

        Assert.False(parsed.HadVariantInsideTheName);
        Assert.Equal(fullName, parsed.CleanName);
    }

    // =====================================================================================
    // La bandeja
    // =====================================================================================

    [Fact]
    public async Task Bandeja_AgrupaElProductoLimpioArribaYLosParecidosAbajo()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.AddRange(
            HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú"),
            HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú"),
            HotelRate(3, "sheraton iguazu", "puerto iguazu"));
        context.RateSupplierSales.AddRange(
            Sale(1, 1, 48m, DateTime.UtcNow.AddDays(-5)),
            Sale(1, 1, 50m, DateTime.UtcNow.AddDays(-4), "triple|desayuno|", "Triple con desayuno"),
            Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();

        var tray = await CreateLibrarian(context).GetDuplicateGroupsAsync(CancellationToken.None);

        var group = Assert.Single(tray.Groups);
        Assert.Equal("Sheraton Iguazú", group.SurvivorName);
        Assert.Equal(2, group.SurvivorPriceCount);
        Assert.Equal(2, group.Candidates.Count);

        // La única aclaración permitida: qué habitación se va a rescatar (V14).
        var conHabitacion = group.Candidates.Single(c => c.Name.Contains("Doble Superior"));
        Assert.Equal("Doble Superior con desayuno", conHabitacion.VariantLabelToRescue);

        var soloEscrituraDistinta = group.Candidates.Single(c => c.Name == "sheraton iguazu");
        Assert.Null(soloEscrituraDistinta.VariantLabelToRescue);
    }

    [Fact]
    public async Task Bandeja_LoQueAlguienMarcoComoDistinto_NoVuelveAAparecer()
    {
        await using var context = CreateContext();
        var uno = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var otro = HotelRate(2, "sheraton iguazu", "puerto iguazu");
        context.Rates.AddRange(uno, otro);
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        await librarian.MarkAsNotDuplicatesAsync(new NotDuplicatesRequest
        {
            FirstPublicId = uno.PublicId,
            SecondPublicId = otro.PublicId
        }, CancellationToken.None);

        var tray = await librarian.GetDuplicateGroupsAsync(CancellationToken.None);
        Assert.Empty(tray.Groups);

        // Y tocarlo dos veces no rompe nada.
        await librarian.MarkAsNotDuplicatesAsync(new NotDuplicatesRequest
        {
            FirstPublicId = otro.PublicId,
            SecondPublicId = uno.PublicId
        }, CancellationToken.None);
        Assert.Equal(1, await context.CatalogNotDuplicatePairs.CountAsync());
    }

    // =====================================================================================
    // Unir: nada se borra, la habitación se rescata, y se puede deshacer
    // =====================================================================================

    [Fact]
    public async Task Unir_MudaLosPreciosYRescataLaHabitacionDelNombre()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.Add(Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();

        var result = await CreateLibrarian(context).MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        Assert.Equal(1, result.MovedPrices);
        Assert.Equal("Doble Superior con desayuno", result.VariantLabelRescued);

        // El precio quedó colgando del hotel que se quedó, ya con su habitación.
        var sale = Assert.Single(await context.RateSupplierSales.AsNoTracking().ToListAsync());
        Assert.Equal(survivor.Id, sale.RateId);
        Assert.Equal("Doble Superior con desayuno", sale.VariantLabel);

        // NADA SE BORRA: el absorbido sigue existiendo, apagado y apuntando al que quedó.
        var absorbedAfter = await context.Rates.AsNoTracking().SingleAsync(rate => rate.Id == absorbed.Id);
        Assert.False(absorbedAfter.IsActive);
        Assert.Equal(survivor.Id, absorbedAfter.MergedIntoRateId);
        Assert.NotNull(absorbedAfter.MergedAt);
    }

    [Fact]
    public async Task Unir_SiLosDosTenianLaMismaHabitacion_QuedaElPrecioMasNuevoYLaOtraNoSeBorra()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Maitei Posadas", "Posadas");
        var absorbed = HotelRate(2, "maitei posadas", "posadas");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.AddRange(
            Sale(1, 1, 40m, DateTime.UtcNow.AddDays(-20), "doble|desayuno|", "Doble con desayuno"),
            Sale(2, 1, 52m, DateTime.UtcNow.AddDays(-2), "doble|desayuno|", "Doble con desayuno"));
        await context.SaveChangesAsync();

        await CreateLibrarian(context).MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        // Se ve UNA sola, con el precio más nuevo...
        var visible = await context.RateSupplierSales.AsNoTracking()
            .Where(sale => sale.AbsorbedByTidyUpActionId == null).ToListAsync();
        var winner = Assert.Single(visible);
        Assert.Equal(52m, winner.LastNetCost);
        // ...y el contador de ventas NO se infla (la otra fila sigue existiendo con el suyo).
        Assert.Equal(1, winner.SalesCount);

        // NADA SE BORRA: la que perdio sigue en la base, escondida (es la del producto absorbido).
        Assert.Equal(2, await context.RateSupplierSales.CountAsync());
        var hidden = await context.RateSupplierSales.AsNoTracking()
            .SingleAsync(sale => sale.AbsorbedByTidyUpActionId != null);
        Assert.Equal(52m, hidden.LastNetCost);

        // Y el importe VIEJO del que quedo (40) no se perdio: quedo fotografiado para poder deshacer.
        var overwritten = await context.CatalogTidyUpSaleChanges.AsNoTracking()
            .SingleAsync(change => change.Kind == "Pisada");
        Assert.Equal(40m, overwritten.PreviousNetCost);
    }

    /// <summary>
    /// El caso que hacía perder plata: al unir, la fila del que quedó fue PISADA por una más nueva.
    /// Deshacer tiene que devolver los importes viejos EXACTOS, no dejar los nuevos.
    /// </summary>
    [Fact]
    public async Task Deshacer_DespuesDeUnChoque_DevuelveTodoExacto()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Maitei Posadas", "Posadas");
        var absorbed = HotelRate(2, "maitei posadas", "posadas");
        context.Rates.AddRange(survivor, absorbed);
        var vieja = Sale(1, 1, 40m, DateTime.UtcNow.AddDays(-20), "doble|desayuno|", "Doble con desayuno");
        var nueva = Sale(2, 1, 52m, DateTime.UtcNow.AddDays(-2), "doble|desayuno|", "Doble con desayuno");
        context.RateSupplierSales.AddRange(vieja, nueva);
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        await librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None);

        var sales = await context.RateSupplierSales.AsNoTracking().OrderBy(sale => sale.Id).ToListAsync();
        Assert.Equal(2, sales.Count);

        // Cada una volvió a su producto, con SU importe y SU contador.
        var delQueQuedo = sales.Single(sale => sale.RateId == survivor.Id);
        Assert.Equal(40m, delQueQuedo.LastNetCost);
        Assert.Equal(1, delQueQuedo.SalesCount);
        Assert.Null(delQueQuedo.AbsorbedByTidyUpActionId);

        var delAbsorbido = sales.Single(sale => sale.RateId == absorbed.Id);
        Assert.Equal(52m, delAbsorbido.LastNetCost);
        Assert.Null(delAbsorbido.AbsorbedByTidyUpActionId); // volvió a mostrarse
    }

    /// <summary>
    /// Deshacer TARDE es peligroso: si después de la unión entró una venta nueva sobre una fila movida,
    /// devolverla se la llevaría al producto equivocado. Se rechaza con una frase que se entiende.
    /// </summary>
    [Fact]
    public async Task Deshacer_DespuesDeUnaVentaNueva_SeRechaza()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.Add(Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        // Una venta nueva pisa la fila que se había movido.
        var moved = await context.RateSupplierSales.SingleAsync();
        moved.LastSoldAt = DateTime.UtcNow.AddMinutes(5);
        moved.LastNetCost = 60m;
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<CatalogTidyUpNotReversibleException>(() =>
            librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None));
        Assert.Equal("Después de esto hubo ventas nuevas; ya no se puede deshacer solo.", error.Message);

        // Y la bandeja lo dice, con el botón apagado y el motivo al lado.
        var log = await librarian.GetTidyUpLogAsync(CancellationToken.None);
        var line = Assert.Single(log.Actions);
        Assert.False(line.CanUndo);
        Assert.Equal("Después de esto hubo ventas nuevas; ya no se puede deshacer solo.", line.UndoBlockedReason);
    }

    /// <summary>
    /// El casillero de destino ocupado: después de unir, alguien vendió el producto viejo y el sistema
    /// aprendió un precio ahí. Devolver la fila a ese mismo casillero chocaría contra "una sola fila por
    /// producto + operador + habitación". Se rechaza con una frase que se entiende.
    /// </summary>
    [Fact]
    public async Task Deshacer_ConElCasilleroDeDestinoOcupado_SeRechaza()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Maitei Posadas", "Posadas");
        var absorbed = HotelRate(2, "maitei posadas", "posadas");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.Add(
            Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3), "doble|desayuno|", "Doble con desayuno"));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        // Una venta nueva del producto VIEJO deja un precio en el casillero que la fila movida tendría que
        // recuperar. Nace con fecha anterior a la unión para que el freno que dispare sea el del casillero.
        context.RateSupplierSales.Add(
            Sale(2, 1, 60m, DateTime.UtcNow.AddDays(-4), "doble|desayuno|", "Doble con desayuno"));
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<CatalogTidyUpNotReversibleException>(() =>
            librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None));
        Assert.Equal(
            "Ese producto ya tiene otro precio para esa habitación; este movimiento ya no se puede deshacer solo.",
            error.Message);

        // Y no quedó nada a medio deshacer: el producto absorbido sigue apagado.
        Assert.False((await context.Rates.AsNoTracking().SingleAsync(r => r.Id == absorbed.Id)).IsActive);
    }

    /// <summary>
    /// Uniones ENCADENADAS (A→B, después B→C) deshechas fuera de orden: la fila ya no está donde la dejó la
    /// primera unión, así que devolverla ahí reubicaría el precio MAL y en silencio. Se frena.
    /// </summary>
    [Fact]
    public async Task Deshacer_UnionesEncadenadasFueraDeOrden_SeRechaza()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var a = HotelRate(1, "Maitei Posadas", "Posadas");
        var b = HotelRate(2, "maitei posadas", "posadas");
        var c = HotelRate(3, "Maitei  Posadas", "Posadas");
        context.Rates.AddRange(a, b, c);
        context.RateSupplierSales.Add(
            Sale(1, 1, 48m, DateTime.UtcNow.AddDays(-10), "doble|desayuno|", "Doble con desayuno"));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        // A se une a B (el precio de A se muda a B)...
        var primera = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = b.PublicId,
            AbsorbedPublicId = a.PublicId
        }, CancellationToken.None);

        // ...y después B se une a C (ese mismo precio se vuelve a mudar, ahora a C).
        await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = c.PublicId,
            AbsorbedPublicId = b.PublicId
        }, CancellationToken.None);

        var error = await Assert.ThrowsAsync<CatalogTidyUpNotReversibleException>(() =>
            librarian.UndoTidyUpActionAsync(primera.TidyUpActionPublicId, CancellationToken.None));
        Assert.Equal(
            "Después de esto se ordenaron esos mismos precios otra vez. Deshacé primero el movimiento más nuevo.",
            error.Message);

        // El precio sigue donde lo dejó la última unión: no se reubicó a medias.
        Assert.Equal(c.Id, (await context.RateSupplierSales.AsNoTracking().SingleAsync()).RateId);

        // Y la bandeja lo dice, con el botón apagado y el motivo al lado.
        var log = await librarian.GetTidyUpLogAsync(CancellationToken.None);
        var linea = log.Actions.Single(action => action.PublicId == primera.TidyUpActionPublicId);
        Assert.False(linea.CanUndo);
        Assert.Equal(
            "Después de esto se ordenaron esos mismos precios otra vez. Deshacé primero el movimiento más nuevo.",
            linea.UndoBlockedReason);
    }

    /// <summary>
    /// Unir → Deshacer → volver a unir, con un precio cargado A MANO de por medio. La fila que el primer
    /// Deshacer dejó escondida no puede trabar la segunda unión (contra Postgres, antes reventaba con el
    /// choque del índice único; acá se verifica la regla de negocio: UNA sola fila visible por casillero).
    /// </summary>
    [Fact]
    public async Task Unir_DeshacerYVolverAUnir_ConPrecioCargadoAMano_NoDuplicaNiTraba()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        absorbed.SupplierId = 1;
        absorbed.NetCost = 0m;
        absorbed.SalePrice = 88m;   // precio cargado a mano, sin ninguna venta
        context.Rates.AddRange(survivor, absorbed);
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var request = new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        };

        var primera = await librarian.MergeProductsAsync(request, CancellationToken.None);
        await librarian.UndoTidyUpActionAsync(primera.TidyUpActionPublicId, CancellationToken.None);

        // Segunda unión del MISMO par: la fila escondida por el Deshacer no puede estorbar.
        await librarian.MergeProductsAsync(request, CancellationToken.None);

        var sales = await context.RateSupplierSales.AsNoTracking().ToListAsync();
        var visibles = sales.Where(sale => sale.AbsorbedByTidyUpActionId == null).ToList();

        // UNA sola visible en el casillero (producto que quedó + operador + habitación)...
        var visible = Assert.Single(visibles);
        Assert.Equal(survivor.Id, visible.RateId);
        Assert.Equal(88m, visible.LastSalePrice);
        // ...y la escondida sigue existiendo (nada se borra).
        Assert.Contains(sales, sale => sale.AbsorbedByTidyUpActionId != null);
    }

    [Fact]
    public async Task Unir_UnProductoQueYaFueAbsorbido_SeRechaza()
    {
        await using var context = CreateContext();
        var survivor = HotelRate(1, "Maitei Posadas", "Posadas");
        var absorbed = HotelRate(2, "maitei posadas", "posadas", active: false);
        absorbed.MergedIntoRateId = survivor.Id;
        absorbed.MergedAt = DateTime.UtcNow.AddDays(-1);
        var tercero = HotelRate(3, "Maitei  Posadas", "Posadas");
        context.Rates.AddRange(survivor, absorbed, tercero);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<RateValidationException>(() =>
            CreateLibrarian(context).MergeProductsAsync(new MergeProductsRequest
            {
                SurvivorPublicId = tercero.PublicId,
                AbsorbedPublicId = absorbed.PublicId
            }, CancellationToken.None));

        Assert.Equal("Ese producto ya no está en la lista: puede que alguien lo haya unido antes.", error.Message);
    }

    [Fact]
    public async Task Unir_ConUnSobrevivienteQueYaSeUnioAOtro_SeRechaza()
    {
        await using var context = CreateContext();
        var jefe = HotelRate(1, "Maitei Posadas", "Posadas");
        var yaUnido = HotelRate(2, "maitei posadas", "posadas");
        yaUnido.MergedIntoRateId = jefe.Id;
        var otro = HotelRate(3, "Maitei  Posadas", "Posadas");
        context.Rates.AddRange(jefe, yaUnido, otro);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<RateValidationException>(() =>
            CreateLibrarian(context).MergeProductsAsync(new MergeProductsRequest
            {
                SurvivorPublicId = yaUnido.PublicId,
                AbsorbedPublicId = otro.PublicId
            }, CancellationToken.None));

        Assert.Equal("Ese producto ya se había unido a otro. Elegí el que quedó y probá de nuevo.", error.Message);
    }

    [Fact]
    public async Task Unir_DobleSubmit_NoHaceDosUniones()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.Add(Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var request = new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        };

        var first = await librarian.MergeProductsAsync(request, CancellationToken.None);
        // El segundo clic llega cuando el producto ya está absorbido: devuelve la MISMA unión.
        var second = await librarian.MergeProductsAsync(request, CancellationToken.None);

        Assert.Equal(first.TidyUpActionPublicId, second.TidyUpActionPublicId);
        Assert.Equal(1, await context.CatalogTidyUpActions.CountAsync());
    }

    /// <summary>
    /// El precio que estaba cargado A MANO en el producto absorbido no puede desaparecer de la vista solo
    /// porque el producto se unió (V16=A).
    /// </summary>
    [Fact]
    public async Task Unir_MudaTambienElPrecioCargadoAMano()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        absorbed.SupplierId = 1;
        absorbed.NetCost = 0m;
        absorbed.SalePrice = 88m;   // precio cargado a mano, sin ninguna venta
        context.Rates.AddRange(survivor, absorbed);
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        var mudado = Assert.Single(await context.RateSupplierSales.AsNoTracking().ToListAsync());
        Assert.Equal(survivor.Id, mudado.RateId);
        Assert.Equal(88m, mudado.LastSalePrice);
        Assert.True(mudado.FromManualLoad);            // no miente: no es una venta
        Assert.Equal(0, mudado.SalesCount);
        Assert.Equal("Doble Superior con desayuno", mudado.VariantLabel);

        // Y al deshacer se esconde (el precio original nunca se tocó: sigue en su producto).
        await librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None);
        var afterUndo = Assert.Single(await context.RateSupplierSales.AsNoTracking().ToListAsync());
        Assert.NotNull(afterUndo.AbsorbedByTidyUpActionId);
        Assert.Equal(88m, (await context.Rates.AsNoTracking().SingleAsync(r => r.Id == absorbed.Id)).SalePrice);
    }

    /// <summary>Aunque el criterio sea automático, SIEMPRE queda quién apretó el botón que lo disparó.</summary>
    [Fact]
    public async Task PasadaAutomatica_GuardaQuienLaDisparo()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.AddRange(
            HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú"),
            HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú"));
        await context.SaveChangesAsync();

        await CreateLibrarian(context, userId: "gaston").TidyUpAsync(CancellationToken.None);

        var action = Assert.Single(await context.CatalogTidyUpActions.AsNoTracking().ToListAsync());
        Assert.True(action.DecidedByTheSystem);          // el criterio fue automático...
        Assert.Equal("gaston", action.PerformedByUserId); // ...pero se sabe quién lo disparó
    }


    [Fact]
    public async Task Unir_DejaRastroConSuDeshacer()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.Add(Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        var log = await librarian.GetTidyUpLogAsync(CancellationToken.None);
        var line = Assert.Single(log.Actions);
        Assert.Equal("Sheraton Iguazú - Doble Superior → Sheraton Iguazú", line.Summary);
        Assert.Equal("la habitación quedó como \"Doble Superior con desayuno\"", line.Detail);
        Assert.True(line.CanUndo);
        Assert.False(line.DecidedByTheSystem); // lo confirmó una persona en la bandeja
    }

    [Fact]
    public async Task Deshacer_DevuelveElProductoYSusPrecios()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú");
        var absorbed = HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú");
        context.Rates.AddRange(survivor, absorbed);
        context.RateSupplierSales.Add(Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        await librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None);

        var absorbedAfter = await context.Rates.AsNoTracking().SingleAsync(rate => rate.Id == absorbed.Id);
        Assert.True(absorbedAfter.IsActive);
        Assert.Null(absorbedAfter.MergedIntoRateId);
        Assert.Equal("Sheraton Iguazú - Doble Superior", absorbedAfter.HotelName);

        var sale = Assert.Single(await context.RateSupplierSales.AsNoTracking().ToListAsync());
        Assert.Equal(absorbed.Id, sale.RateId);          // el precio volvió a su dueño
        Assert.Equal(string.Empty, sale.VariantKey);      // y sin la habitación que se le había puesto

        // La línea del rastro NO se borra: queda marcada como deshecha.
        var log = await librarian.GetTidyUpLogAsync(CancellationToken.None);
        Assert.False(Assert.Single(log.Actions).CanUndo);
    }

    [Fact]
    public async Task Deshacer_DosVeces_NoRompeNada()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        var survivor = HotelRate(1, "Maitei Posadas", "Posadas");
        var absorbed = HotelRate(2, "maitei posadas", "posadas");
        context.Rates.AddRange(survivor, absorbed);
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var merge = await librarian.MergeProductsAsync(new MergeProductsRequest
        {
            SurvivorPublicId = survivor.PublicId,
            AbsorbedPublicId = absorbed.PublicId
        }, CancellationToken.None);

        await librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None);
        await librarian.UndoTidyUpActionAsync(merge.TidyUpActionPublicId, CancellationToken.None);

        Assert.True((await context.Rates.AsNoTracking().SingleAsync(r => r.Id == absorbed.Id)).IsActive);
    }

    [Fact]
    public async Task Unir_ProductosDeDistintoTipo_SeRechaza()
    {
        await using var context = CreateContext();
        var hotel = HotelRate(1, "Maitei Posadas", "Posadas");
        var vuelo = new Rate
        {
            Id = 2, ServiceType = "Aereo", ProductName = "Maitei Posadas", City = "Posadas",
            IsActive = true, Currency = "USD",
            SearchName = TextNormalizer.NormalizeForCatalog("Maitei Posadas")
        };
        context.Rates.AddRange(hotel, vuelo);
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<RateValidationException>(() =>
            CreateLibrarian(context).MergeProductsAsync(new MergeProductsRequest
            {
                SurvivorPublicId = hotel.PublicId,
                AbsorbedPublicId = vuelo.PublicId
            }, CancellationToken.None));

        Assert.Equal("Esos dos productos no son del mismo tipo, no se pueden unir.", error.Message);
    }

    // =====================================================================================
    // La pasada automática (Q3=B): une los "casi seguros" y deja el resto para revisar
    // =====================================================================================

    [Fact]
    public async Task PasadaAutomatica_UneLoQueNuestroFormularioPartioYDejaElRestoParaRevisar()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.AddRange(
            HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú"),
            HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú"),
            HotelRate(3, "Sheraton Iguazú - Triple", "Puerto Iguazú"),
            // Este NO lo puede decidir solo: es otra escritura, no un sufijo puesto por el formulario.
            HotelRate(4, "sheraton iguazu", "puerto iguazu"));
        context.RateSupplierSales.AddRange(
            Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)),
            Sale(3, 1, 70m, DateTime.UtcNow.AddDays(-2)));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var run = await librarian.TidyUpAsync(CancellationToken.None);

        Assert.Equal(2, run.MergedProducts);
        Assert.Equal(2, run.VariantsRescued);

        // Los dos precios quedaron en el hotel limpio, cada uno con su habitación.
        var sales = await context.RateSupplierSales.AsNoTracking().ToListAsync();
        Assert.All(sales, sale => Assert.Equal(1, sale.RateId));
        Assert.Contains(sales, sale => sale.VariantLabel == "Doble Superior con desayuno");
        Assert.Contains(sales, sale => sale.VariantLabel == "Triple con desayuno");

        // El de escritura distinta sigue esperando que una persona decida.
        var tray = await librarian.GetDuplicateGroupsAsync(CancellationToken.None);
        var group = Assert.Single(tray.Groups);
        Assert.Equal("sheraton iguazu", Assert.Single(group.Candidates).Name);
        Assert.Equal(2, tray.TidiedUpThisWeek);
    }

    [Fact]
    public async Task PasadaAutomatica_CorrerlaDosVeces_NoHaceNadaLaSegunda()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.AddRange(
            HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú"),
            HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú"));
        context.RateSupplierSales.Add(Sale(2, 1, 55m, DateTime.UtcNow.AddDays(-3)));
        await context.SaveChangesAsync();
        var librarian = CreateLibrarian(context);

        var first = await librarian.TidyUpAsync(CancellationToken.None);
        var second = await librarian.TidyUpAsync(CancellationToken.None);

        Assert.Equal(1, first.MergedProducts);
        Assert.Equal(0, second.MergedProducts);
        Assert.Equal(1, await context.CatalogTidyUpActions.CountAsync());
    }

    /// <summary>El producto absorbido deja de listarse en el Tarifario (pero sigue existiendo en la base).</summary>
    [Fact]
    public async Task DespuesDeUnir_ElAbsorbidoDejaDeListarse()
    {
        await using var context = CreateContext();
        context.Suppliers.Add(new Supplier { Id = 1, Name = "Ola" });
        context.Rates.AddRange(
            HotelRate(1, "Sheraton Iguazú", "Puerto Iguazú"),
            HotelRate(2, "Sheraton Iguazú - Doble Superior", "Puerto Iguazú"));
        await context.SaveChangesAsync();

        await CreateLibrarian(context).TidyUpAsync(CancellationToken.None);

        Assert.Equal(2, await context.Rates.CountAsync());                       // los dos siguen en la base
        Assert.Equal(1, await context.Rates.CountAsync(rate => rate.IsActive));  // pero se lista uno solo
    }
}
