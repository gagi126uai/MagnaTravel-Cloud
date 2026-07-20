using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Tanda 1 del plan de remediacion "contrato pantalla-motor" (2026-07-18): los endpoints
/// AddSupplierPayment y UpdateSupplierPayment de <c>SuppliersController</c> ANTES atrapaban
/// <c>ArgumentException</c>/<c>InvalidOperationException</c> y devolvian SIEMPRE el mismo cartel
/// generico ("No se pudo registrar/actualizar el pago al proveedor."), aunque <c>SupplierService</c>
/// ya habia calculado el motivo REAL (moneda equivocada, reserva sin servicios del proveedor, etc).
/// El front mostraba un error inutil que no ayudaba a nadie a corregir la carga.
///
/// <para>Estos tests demuestran que el body de error ahora trae el <b>texto exacto</b> del negocio
/// (no alcanza con "es 400"): si el mensaje se vuelve a tapar con un generico, estos tests rompen.</para>
///
/// <para>No se cubren los 7 mensajes documentados en el plan: se eligieron los que se arman con seeds
/// simples, mas el caso puntual del cargo del operador (Post-review, 2026-07-18): ese mensaje ANTES
/// llevaba el sufijo tecnico "(Parameter 'request')" de <c>ArgumentException</c>, que es justo el tipo
/// de leak que <c>SupplierPaymentValidationException</c> vino a cerrar.</para>
/// </summary>
public class SupplierPaymentErrorMessagePropagationTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SupplierPaymentErrorMessagePropagationTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ---------- helpers de seed ----------

    /// <summary>Crea un proveedor "vacio" (sin servicios, sin pagos) para arrancar cada test limpio.</summary>
    private async Task<Supplier> AddSupplierAsync(string namePrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var supplier = new Supplier { Name = namePrefix + " " + Guid.NewGuid().ToString("N")[..6], IsActive = true };
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier;
    }

    /// <summary>Reserva firme minima (NumeroReserva/Name/Status son los unicos campos obligatorios).</summary>
    private async Task<Reserva> AddReservaAsync(string numeroPrefix)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reserva = new Reserva
        {
            NumeroReserva = numeroPrefix + "-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva " + numeroPrefix,
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();
        return reserva;
    }

    /// <summary>
    /// Hotel CONFIRMADO (cuenta para la deuda con el proveedor, WorkflowStatusHelper.CountsForSupplierDebtByType)
    /// de este proveedor en esta reserva, con el costo/moneda que pida cada test.
    /// </summary>
    private async Task<HotelBooking> AddConfirmedHotelAsync(int supplierId, int reservaId, decimal netCost, string? currency)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var hotel = new HotelBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            HotelName = "Hotel de prueba",
            City = "Ciudad",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2,
            Status = "Confirmado",
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency
        };
        db.HotelBookings.Add(hotel);
        await db.SaveChangesAsync();
        return hotel;
    }

    /// <summary>Pago ya existente al proveedor (registrado directo en la BD), para los tests de UpdateSupplierPayment.</summary>
    private async Task<(Guid PublicId, int Id)> AddExistingSupplierPaymentAsync(int supplierId, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var payment = new SupplierPayment
        {
            SupplierId = supplierId,
            Amount = amount,
            Method = "Transfer",
            PaidAt = DateTime.UtcNow
        };
        db.SupplierPayments.Add(payment);
        await db.SaveChangesAsync();
        return (payment.PublicId, payment.Id);
    }

    /// <summary>
    /// Cargo del operador (ADR-044 T2/T3b) de ESTE proveedor, con el <c>CollectionMode</c> que pida el test.
    /// Es el arbol minimo que exige la tabla hija: Cliente + Reserva + Factura + BookingCancellation (padre) +
    /// BookingCancellationLine (hija, dueña del proveedor) + el cargo en si. Se usa para los tests de
    /// "liquidar cargo del operador" de AddSupplierPayment (SettlesOperatorChargePublicId).
    /// </summary>
    private async Task<BookingCancellationLineOperatorCharge> AddOperatorChargeAsync(
        int supplierId, decimal amount, string currency, PenaltyCollectionMode collectionMode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var customer = new Customer { FullName = "Cliente cargo operador", IsActive = true };
        db.Customers.Add(customer);
        var reserva = new Reserva
        {
            NumeroReserva = "F-CARGO-" + Guid.NewGuid().ToString("N")[..6],
            Name = "Reserva cargo operador",
            Status = EstadoReserva.Confirmed
        };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        var invoice = new Invoice
        {
            TipoComprobante = 11,
            PuntoDeVenta = 1,
            NumeroComprobante = 1,
            CAE = "cae-cargo-" + Guid.NewGuid().ToString("N")[..6],
            Resultado = "A",
            MonId = "PES",
            ImporteTotal = 100_000m,
            ReservaId = reserva.Id,
            AnnulmentStatus = AnnulmentStatus.None,
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplierId,
            OriginatingInvoiceId = invoice.Id,
            Reason = "Cancelacion para probar el mensaje del cargo del operador",
            Status = BookingCancellationStatus.AwaitingOperatorRefund,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Manual, FetchedAt = DateTime.UtcNow },
        };
        db.BookingCancellations.Add(bc);
        await db.SaveChangesAsync();

        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplierId,
            ServiceTable = CancellableServiceTable.Hotel,
            ServiceId = 1,
            Scope = BookingCancellationLineScope.Full,
            Currency = currency,
            RefundCap = 0m,
            PenaltyStatus = PenaltyStatus.Confirmed,
        };
        db.BookingCancellationLines.Add(line);
        await db.SaveChangesAsync();

        var charge = new BookingCancellationLineOperatorCharge
        {
            BookingCancellationLineId = line.Id,
            Kind = OperatorChargeKind.AdministrativeFee,
            CollectionMode = collectionMode,
            Amount = amount,
            Currency = currency,
            ConfirmedByUserId = "u1",
        };
        db.BookingCancellationLineOperatorCharges.Add(charge);
        await db.SaveChangesAsync();

        return charge;
    }

    /// <summary>Lee <c>message</c> del body de error y lo compara EXACTO con el texto de negocio esperado.</summary>
    private static async Task AssertExactErrorMessageAsync(HttpResponseMessage response, string expectedMessage)
    {
        var payload = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.TryGetProperty("message", out var messageElement),
            $"El body deberia traer 'message'. Body real: {payload}");
        Assert.Equal(expectedMessage, messageElement.GetString());
    }

    // ================================================================================================
    // POST /api/suppliers/{id}/payments (AddSupplierPayment)
    // ================================================================================================

    [Fact]
    public async Task POST_AddSupplierPayment_CurrencyDoesNotMatchService_Returns400WithRealMessage()
    {
        // El hotel esta cotizado en USD; se intenta pagarlo con un pago SIN moneda declarada (default ARS).
        // SupplierService.EnsureServicePaymentCurrencyMatchesService rechaza el mismatch con InvalidOperationException.
        var supplier = await AddSupplierAsync("Mayorista moneda");
        var reserva = await AddReservaAsync("F-MSG5");
        var hotel = await AddConfirmedHotelAsync(supplier.Id, reserva.Id, netCost: 300m, currency: "USD");

        var request = new SupplierPaymentRequest(
            Amount: 300m,
            Method: "Transfer",
            Reference: null,
            Notes: null,
            ReservaId: reserva.PublicId.ToString(),
            ServicioReservaId: null,
            IsAdvanceToAccount: false,
            ServiceRecordKind: ServicePaymentRecordKinds.Hotel,
            ServicePublicId: hotel.PublicId.ToString());

        var client = _factory.CreateClient(); // sin headers: default Admin (bypass de permisos).
        var response = await client.PostAsJsonAsync($"/api/suppliers/{supplier.PublicId}/payments", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertExactErrorMessageAsync(response, "La moneda del pago no coincide con la del costo del servicio.");
    }

    [Fact]
    public async Task POST_AddSupplierPayment_ReservaHasNoServicesFromThisSupplier_Returns400WithRealMessage()
    {
        // La reserva existe pero NO tiene ningun servicio de ESTE proveedor: no hay nada que imputar.
        // SupplierService rechaza con InvalidOperationException ANTES de tocar ninguna moneda/tope.
        var supplier = await AddSupplierAsync("Mayorista sin servicios");
        var reserva = await AddReservaAsync("F-MSG3");

        var request = new SupplierPaymentRequest(
            Amount: 100m,
            Method: "Transfer",
            Reference: null,
            Notes: null,
            ReservaId: reserva.PublicId.ToString(),
            ServicioReservaId: null);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/suppliers/{supplier.PublicId}/payments", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertExactErrorMessageAsync(response, "La reserva no tiene servicios de este proveedor para imputar el pago.");
    }

    [Fact]
    public async Task POST_AddSupplierPayment_SettlesChargeThatIsNotFacturadaAparte_Returns400WithoutParameterSuffix()
    {
        // (2026-07-18) Este mensaje ANTES salia de un ArgumentException(message, nameof(request)): el
        // segundo argumento le pegaba el sufijo tecnico "(Parameter 'request')" al Message completo, un
        // leak que el vendedor veia tal cual. Ahora sale de SupplierPaymentValidationException (sin
        // ParamName), asi que el body NO debe traer ese sufijo.
        var supplier = await AddSupplierAsync("Mayorista cargo retenido");
        // CollectionMode Retenida (no FacturadaAparte): un pago no puede liquidarlo.
        var charge = await AddOperatorChargeAsync(
            supplier.Id, amount: 500m, currency: "ARS", collectionMode: PenaltyCollectionMode.Retenida);

        var request = new SupplierPaymentRequest(
            Amount: 500m,
            Method: "Transfer",
            Reference: null,
            Notes: null,
            ReservaId: null,
            ServicioReservaId: null,
            IsAdvanceToAccount: true,
            SettlesOperatorChargePublicId: charge.PublicId);

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync($"/api/suppliers/{supplier.PublicId}/payments", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Parameter", body);
        await AssertExactErrorMessageAsync(
            response, "Este pago solo puede liquidar un cargo del operador facturado aparte.");
    }

    // ================================================================================================
    // PUT /api/suppliers/{id}/payments/{paymentId} (UpdateSupplierPayment)
    // ================================================================================================

    [Fact]
    public async Task PUT_UpdateSupplierPayment_ReservaAndAdvanceToAccountTogether_Returns400WithRealMessage()
    {
        // Imputar a una reserva Y marcar "anticipo a cuenta" a la vez son caminos opuestos y mutuamente
        // excluyentes. Este chequeo es lo PRIMERO que valida ResolveSupplierPaymentImputationAsync, asi
        // que no hace falta que la reserva tenga servicios de este proveedor para llegar a el.
        var supplier = await AddSupplierAsync("Mayorista anticipo");
        var reserva = await AddReservaAsync("F-MSG1");
        var existingPayment = await AddExistingSupplierPaymentAsync(supplier.Id, amount: 500m);

        var request = new SupplierPaymentRequest(
            Amount: 500m,
            Method: "Transfer",
            Reference: null,
            Notes: null,
            ReservaId: reserva.PublicId.ToString(),
            ServicioReservaId: null,
            IsAdvanceToAccount: true);

        var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/api/suppliers/{supplier.PublicId}/payments/{existingPayment.PublicId}", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertExactErrorMessageAsync(
            response, "Un pago no puede imputarse a una reserva y marcarse como anticipo a cuenta a la vez.");
    }

    [Fact]
    public async Task PUT_UpdateSupplierPayment_CurrencyDoesNotMatchAnyDebtInReserva_Returns400WithRealMessage()
    {
        // La reserva SI tiene deuda de este proveedor, pero solo en ARS. Editar el pago para imputarlo
        // en USD no coincide con ninguna moneda de esa deuda: SupplierService rechaza con
        // InvalidOperationException (distinto mensaje del "reserva sin servicios" de arriba).
        var supplier = await AddSupplierAsync("Mayorista USD sobre ARS");
        var reserva = await AddReservaAsync("F-MSG4");
        await AddConfirmedHotelAsync(supplier.Id, reserva.Id, netCost: 1000m, currency: null); // ARS
        var existingPayment = await AddExistingSupplierPaymentAsync(supplier.Id, amount: 100m);

        var request = new SupplierPaymentRequest(
            Amount: 100m,
            Method: "Transfer",
            Reference: null,
            Notes: null,
            ReservaId: reserva.PublicId.ToString(),
            ServicioReservaId: null,
            Currency: "USD");

        var client = _factory.CreateClient();
        var response = await client.PutAsJsonAsync(
            $"/api/suppliers/{supplier.PublicId}/payments/{existingPayment.PublicId}", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertExactErrorMessageAsync(
            response, "El pago no coincide con ninguna moneda de la deuda de este proveedor en la reserva.");
    }
}
