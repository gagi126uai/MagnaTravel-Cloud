using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Solapa "Datos" de la ficha del cliente (spec UX §7, 2026-07-17): la pantalla NO calcula nada, solo lee dos
/// veredictos que ahora manda <c>CustomerAccountOverviewDto</c> (GET /api/customers/{id}/account):
///
///   - <c>HasPendingTaxData</c>: la MISMA formula que usa el motor de anulaciones para bloquear con INV-118
///     (<c>CustomerTaxConditionCatalog.ResolveCanonical</c>: texto primero, código AFIP como respaldo).
///   - <c>TaxIdLocked</c>: el MISMO veredicto de <c>MutationGuards.GetCustomerTaxIdMutationBlockReasonAsync</c>
///     (CODE-06 — factura viva referenciando al cliente). Ojo: desde el 2026-07-17 este candado es SOLO del
///     CUIT, la condición fiscal se edita siempre.
///
/// También cubre que el DTO de un solo cliente (<c>GetCustomerAsync</c>, ya usado por la ficha) trae los
/// campos que la solapa necesita mostrar/editar (taxId, taxConditionId, documentType/documentNumber, email,
/// phone, address, isActive) — no hizo falta agregar nada ahí porque ya estaban.
/// </summary>
public class CustomerAccountTaxDataOverviewTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static CustomerService CreateService(AppDbContext context)
        => new CustomerService(context, new FinancePositionService(context));

    // ================= HasPendingTaxData =================

    [Fact]
    public async Task HasPendingTaxData_TextAndCodeBothEmpty_IsTrue()
    {
        // Caso real de la investigacion 2026-07-17: un cliente legacy sin condicion fiscal cargada por
        // ningun lado. TaxCondition vacio a proposito (el default de alta es "Consumidor Final"; este test
        // simula el dato roto, no el alta nueva).
        await using var context = CreateContext();
        var customer = new Customer
        {
            FullName = "Cliente sin condicion fiscal",
            TaxCondition = string.Empty,
            TaxConditionId = null,
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var overview = await CreateService(context)
            .GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.True(overview.HasPendingTaxData);
    }

    [Fact]
    public async Task HasPendingTaxData_ValidTextCondition_IsFalse()
    {
        await using var context = CreateContext();
        var customer = new Customer
        {
            FullName = "Cliente con texto valido",
            TaxCondition = "Responsable Inscripto",
            TaxConditionId = null,
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var overview = await CreateService(context)
            .GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.False(overview.HasPendingTaxData);
    }

    [Fact]
    public async Task HasPendingTaxData_TextRotoPeroCodigoValido_CaeAlCodigo_IsFalse()
    {
        // Red de seguridad del catalogo (2026-07-17): texto vacio + codigo AFIP valido (6 = Monotributo)
        // resuelve por el codigo, NO queda pendiente. Es el mismo fallback que ya usa la anulacion.
        await using var context = CreateContext();
        var customer = new Customer
        {
            FullName = "Cliente con texto roto pero codigo bueno",
            TaxCondition = string.Empty,
            TaxConditionId = 6, // Monotributo
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var overview = await CreateService(context)
            .GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.False(overview.HasPendingTaxData);
    }

    [Fact]
    public async Task HasPendingTaxData_DefaultConsumidorFinal_IsFalse()
    {
        // El caso de alta normal (default "Consumidor Final" del entity): casi siempre false, tal como
        // anticipa el enunciado de la tarea.
        await using var context = CreateContext();
        var customer = new Customer { FullName = "Cliente recien creado" };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var overview = await CreateService(context)
            .GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.False(overview.HasPendingTaxData);
    }

    // ================= TaxIdLocked =================

    [Fact]
    public async Task TaxIdLocked_ClienteConFacturaViva_IsTrue()
    {
        await using var context = CreateContext();
        var customer = new Customer { FullName = "Cliente con factura viva" };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-2026-9001",
            Name = "Reserva con CAE",
            Status = EstadoReserva.Confirmed,
            PayerId = customer.Id,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.Invoices.Add(new Invoice
        {
            ReservaId = reserva.Id,
            CAE = "012345",
            AnnulmentStatus = AnnulmentStatus.None,
            TipoComprobante = 6, // Factura B, no es NC
            ImporteTotal = 100m,
            ImporteNeto = 82.64m,
            ImporteIva = 17.36m,
        });
        await context.SaveChangesAsync();

        var overview = await CreateService(context)
            .GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.True(overview.TaxIdLocked);
    }

    [Fact]
    public async Task TaxIdLocked_ClienteSinFacturas_IsFalse()
    {
        await using var context = CreateContext();
        var customer = new Customer { FullName = "Cliente sin facturas" };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var overview = await CreateService(context)
            .GetCustomerAccountOverviewAsync(customer.Id, CancellationToken.None);

        Assert.False(overview.TaxIdLocked);
    }

    // ================= Campos que la solapa "Datos" necesita (item 2 de la tarea) =================

    [Fact]
    public async Task GetCustomerAsync_TraeLosCamposQueLaSolapaDatosNecesita()
    {
        await using var context = CreateContext();
        var customer = new Customer
        {
            FullName = "Cliente completo",
            Email = "cliente@ejemplo.com",
            Phone = "1122334455",
            TaxId = "20304050607",
            TaxConditionId = 1, // Responsable Inscripto
            DocumentType = "DNI",
            DocumentNumber = "30405060",
            Address = "Av. Siempre Viva 742",
            IsActive = true,
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var dto = await CreateService(context).GetCustomerAsync(customer.Id, CancellationToken.None);

        Assert.Equal("cliente@ejemplo.com", dto.Email);
        Assert.Equal("1122334455", dto.Phone);
        Assert.Equal("20304050607", dto.TaxId);
        Assert.Equal(1, dto.TaxConditionId);
        Assert.Equal("DNI", dto.DocumentType);
        Assert.Equal("30405060", dto.DocumentNumber);
        Assert.Equal("Av. Siempre Viva 742", dto.Address);
        Assert.True(dto.IsActive);
    }
}
