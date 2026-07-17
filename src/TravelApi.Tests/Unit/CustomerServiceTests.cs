using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit
{
    public class CustomerServiceTests
    {
        private readonly DbContextOptions<AppDbContext> _dbOptions;

        public CustomerServiceTests()
        {
            _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options;
        }

        [Fact]
        public async Task CreateCustomerAsync_ShouldAddCustomerToDatabase()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            var service = new CustomerService(context, new FinancePositionService(context));
            var customer = new Customer { FullName = "John Doe", Email = "john@example.com" };

            // Act
            var result = await service.CreateCustomerAsync(customer, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            Assert.True(result.Id > 0);
            Assert.True(result.IsActive);
            Assert.Equal(1, await context.Customers.CountAsync());
        }

        [Fact]
        public async Task GetCustomersAsync_ShouldFilterByActiveStatus()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            context.Customers.Add(new Customer { FullName = "Active", IsActive = true });
            context.Customers.Add(new Customer { FullName = "Inactive", IsActive = false });
            await context.SaveChangesAsync();

            var service = new CustomerService(context, new FinancePositionService(context));

            // Act
            var activeOnly = await service.GetCustomersAsync(new TravelApi.Application.DTOs.CustomerListQuery { IncludeInactive = false }, CancellationToken.None);
            var all = await service.GetCustomersAsync(new TravelApi.Application.DTOs.CustomerListQuery { IncludeInactive = true }, CancellationToken.None);

            // Assert
            Assert.Single(activeOnly.Items);
            Assert.Equal(2, all.TotalCount);
        }

        [Fact]
        public async Task UpdateCustomerAsync_ShouldModifyExistingCustomer()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            var customer = new Customer { Id = 1, FullName = "Old Name", IsActive = true };
            context.Customers.Add(customer);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = new CustomerService(context, new FinancePositionService(context));
            var updatedCustomer = new Customer { Id = 1, FullName = "New Name", IsActive = true };

            // Act
            var result = await service.UpdateCustomerAsync(1, updatedCustomer, CancellationToken.None);

            // Assert
            Assert.Equal("New Name", result.FullName);
            var dbCustomer = await context.Customers.FindAsync(1);
            Assert.NotNull(dbCustomer);
            Assert.Equal("New Name", dbCustomer.FullName);
        }

        // ================================================================
        // Regla del dueño (2026-07-17): "el CUIT es una identidad; la condicion fiscal es un
        // dato de HOY". CODE-06 se separa en dos ejes: el eje TaxId sigue bloqueado con factura
        // viva (igual que antes); el eje TaxCondition/TaxConditionId se permite editar SIEMPRE,
        // con auditoria.
        // ================================================================

        private static async Task<Customer> SeedCustomerWithLiveInvoiceAsync(AppDbContext context, int customerId)
        {
            var customer = new Customer { Id = customerId, FullName = "Cliente con factura", TaxId = null, TaxCondition = "Consumidor Final" };
            context.Customers.Add(customer);
            context.Reservas.Add(new Reserva
            {
                Id = customerId,
                NumeroReserva = $"F-2026-{customerId:D4}",
                Name = "Reserva facturada",
                Status = EstadoReserva.Confirmed,
                PayerId = customerId
            });
            await context.SaveChangesAsync();

            context.Invoices.Add(new Invoice
            {
                Id = customerId,
                ReservaId = customerId,
                CAE = "012345",
                AnnulmentStatus = AnnulmentStatus.None,
                TipoComprobante = 6, // Factura B
                ImporteTotal = 100m,
                ImporteNeto = 82.64m,
                ImporteIva = 17.36m
            });
            await context.SaveChangesAsync();
            return customer;
        }

        /// <summary>
        /// Cambiar SOLO la condicion fiscal (null -> valor, el caso real que Gaston destapo el 2026-07-17) NO
        /// pasa por el guard CODE-06 aunque el cliente tenga una factura con CAE viva: la condicion es un dato
        /// de HOY. Queda auditada (accion CustomerTaxConditionChanged) con el viejo -&gt; nuevo valor.
        /// </summary>
        [Fact]
        public async Task UpdateCustomerAsync_ChangingOnlyTaxCondition_WithLiveInvoice_AllowsAndAudits()
        {
            using var context = new AppDbContext(_dbOptions);
            var customer = await SeedCustomerWithLiveInvoiceAsync(context, customerId: 50);
            context.ChangeTracker.Clear();

            var audit = new Mock<IAuditService>();
            var service = new CustomerService(context, new FinancePositionService(context), audit.Object);

            var incoming = new Customer
            {
                Id = customer.Id,
                FullName = customer.FullName,
                TaxId = customer.TaxId, // SIN CAMBIOS
                TaxCondition = "Responsable Inscripto", // Consumidor Final -> RI: el caso real de hoy
                TaxConditionId = 1,
                IsActive = true
            };

            var result = await service.UpdateCustomerAsync(customer.Id, incoming, CancellationToken.None);

            Assert.Equal("Responsable Inscripto", result.TaxCondition);
            Assert.Equal(1, result.TaxConditionId);

            audit.Verify(a => a.StageBusinessEvent(
                AuditActions.CustomerTaxConditionChanged,
                "Customer",
                customer.Id.ToString(),
                It.Is<string>(d => d.Contains("Consumidor Final") && d.Contains("Responsable Inscripto")),
                It.IsAny<string>(),
                It.IsAny<string>()), Times.Once);
        }

        /// <summary>Cambiar el CUIT con factura viva sigue bloqueado, con el mensaje nuevo (sin mencionar "condicion").</summary>
        [Fact]
        public async Task UpdateCustomerAsync_ChangingTaxId_WithLiveInvoice_Blocks()
        {
            using var context = new AppDbContext(_dbOptions);
            var customer = await SeedCustomerWithLiveInvoiceAsync(context, customerId: 51);
            context.ChangeTracker.Clear();

            var service = new CustomerService(context, new FinancePositionService(context));

            var incoming = new Customer
            {
                Id = customer.Id,
                FullName = customer.FullName,
                TaxId = "20-11111111-1", // CAMBIA el CUIT
                TaxCondition = customer.TaxCondition,
                IsActive = true
            };

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.UpdateCustomerAsync(customer.Id, incoming, CancellationToken.None));

            Assert.Contains("CUIT", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("condicion", ex.Message, StringComparison.OrdinalIgnoreCase);

            // La factura viva no se ve afectada: el CUIT en base sigue siendo el original.
            var dbCustomer = await context.Customers.FindAsync(customer.Id);
            Assert.Null(dbCustomer!.TaxId);
        }

        /// <summary>Sin facturas vivas, cambiar el CUIT sigue permitido (comportamiento preexistente).</summary>
        [Fact]
        public async Task UpdateCustomerAsync_ChangingTaxId_WithoutInvoices_Allows()
        {
            using var context = new AppDbContext(_dbOptions);
            var customer = new Customer { Id = 52, FullName = "Cliente sin facturas", TaxId = null, TaxCondition = "Consumidor Final" };
            context.Customers.Add(customer);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = new CustomerService(context, new FinancePositionService(context));

            var incoming = new Customer
            {
                Id = customer.Id,
                FullName = customer.FullName,
                TaxId = "20-22222222-2",
                TaxCondition = customer.TaxCondition,
                IsActive = true
            };

            var result = await service.UpdateCustomerAsync(customer.Id, incoming, CancellationToken.None);

            Assert.Equal("20-22222222-2", result.TaxId);
        }

        /// <summary>
        /// N2(a): con FACTURA VIVA, si el PUT trae los DOS ejes a la vez (CUIT nuevo + condicion nueva), el
        /// bloqueo del CUIT es TOTAL — no se persiste el CUIT nuevo NI la condicion nueva, y tampoco se audita
        /// el cambio de condicion (aunque esa parte sola hubiera sido valida). Evita el mensaje contradictorio
        /// "no se puede cambiar" + "pero la condicion sí se guardó".
        /// </summary>
        [Fact]
        public async Task UpdateCustomerAsync_ChangingTaxIdAndTaxCondition_WithLiveInvoice_BlocksBothAndDoesNotAudit()
        {
            using var context = new AppDbContext(_dbOptions);
            var customer = await SeedCustomerWithLiveInvoiceAsync(context, customerId: 53);
            context.ChangeTracker.Clear();

            var audit = new Mock<IAuditService>();
            var service = new CustomerService(context, new FinancePositionService(context), audit.Object);

            var incoming = new Customer
            {
                Id = customer.Id,
                FullName = customer.FullName,
                TaxId = "20-33333333-3", // CAMBIA el CUIT
                TaxCondition = "Responsable Inscripto", // Y TAMBIEN cambia la condicion
                TaxConditionId = 1,
                IsActive = true
            };

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.UpdateCustomerAsync(customer.Id, incoming, CancellationToken.None));

            // Ni el CUIT ni la condicion quedaron persistidos: el bloqueo del eje CUIT frena TODO el PUT.
            var dbCustomer = await context.Customers.FindAsync(customer.Id);
            Assert.Null(dbCustomer!.TaxId);
            Assert.Equal("Consumidor Final", dbCustomer.TaxCondition);

            audit.Verify(a => a.StageBusinessEvent(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        /// <summary>
        /// N2(b): cambiar SOLO el CUIT (sin tocar la condicion) audita CustomerTaxIdChanged pero NUNCA dispara
        /// CustomerTaxConditionChanged de rebote — los dos ejes son independientes.
        /// </summary>
        [Fact]
        public async Task UpdateCustomerAsync_ChangingOnlyTaxId_DoesNotAuditTaxConditionChange()
        {
            using var context = new AppDbContext(_dbOptions);
            var customer = new Customer { Id = 54, FullName = "Cliente sin facturas", TaxId = "20-10000000-1", TaxCondition = "Consumidor Final" };
            context.Customers.Add(customer);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var audit = new Mock<IAuditService>();
            var service = new CustomerService(context, new FinancePositionService(context), audit.Object);

            var incoming = new Customer
            {
                Id = customer.Id,
                FullName = customer.FullName,
                TaxId = "20-99999999-9", // SOLO cambia el CUIT
                TaxCondition = customer.TaxCondition,
                TaxConditionId = customer.TaxConditionId,
                IsActive = true
            };

            await service.UpdateCustomerAsync(customer.Id, incoming, CancellationToken.None);

            audit.Verify(a => a.StageBusinessEvent(
                AuditActions.CustomerTaxIdChanged,
                "Customer", customer.Id.ToString(),
                It.Is<string>(d => d.Contains("20-10000000-1") && d.Contains("20-99999999-9")),
                It.IsAny<string>(), It.IsAny<string>()), Times.Once);

            audit.Verify(a => a.StageBusinessEvent(
                AuditActions.CustomerTaxConditionChanged,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        /// <summary>
        /// N2(c): PUT que OMITE TaxCondition (como manda hoy CustomerFormModal.jsx — solo taxConditionId, nunca
        /// el string) sobre un cliente Responsable Inscripto CON factura viva: la condicion se PRESERVA
        /// (no se pisa con vacio/default) y NO se audita (no hubo cambio real).
        /// </summary>
        [Fact]
        public async Task UpdateCustomerAsync_OmittingTaxCondition_WithLiveInvoice_PreservesAndDoesNotAudit()
        {
            using var context = new AppDbContext(_dbOptions);
            var customer = await SeedCustomerWithLiveInvoiceAsync(context, customerId: 55);
            customer.TaxCondition = "Responsable Inscripto";
            customer.TaxConditionId = 1;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var audit = new Mock<IAuditService>();
            var service = new CustomerService(context, new FinancePositionService(context), audit.Object);

            // Simula el PUT real de hoy: viaja taxConditionId (el mismo valor, redundante) pero taxCondition
            // (el string) llega null porque el form nunca lo manda.
            var incoming = new Customer
            {
                Id = customer.Id,
                FullName = "Nombre editado", // el usuario tocó otro campo cualquiera
                TaxId = customer.TaxId,
                TaxCondition = null,
                TaxConditionId = 1,
                IsActive = true
            };

            var result = await service.UpdateCustomerAsync(customer.Id, incoming, CancellationToken.None);

            Assert.Equal("Responsable Inscripto", result.TaxCondition);

            audit.Verify(a => a.StageBusinessEvent(
                AuditActions.CustomerTaxConditionChanged,
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
                Times.Never);
        }

        /// <summary>
        /// ADR-022 §4.9 (fix S1-bis): la pestaña Pagos de la cuenta del cliente NO debe mostrar el Payment
        /// puente del saldo a favor (respaldo interno, AffectsCash=false, monto negativo, Notes con GUID). Antes
        /// solo se excluia en la lista por reserva (PaymentService); aca se cubre la lista por cliente.
        /// </summary>
        [Fact]
        public async Task GetCustomerAccountPaymentsAsync_ShouldExcludeOverpaymentBridge_AndKeepRealPayment()
        {
            // Arrange: un cliente con una reserva, un cobro REAL y el puente de sobrepago atado a ese cobro.
            using var context = new AppDbContext(_dbOptions);

            var customer = new Customer { Id = 7, FullName = "Cliente Sobrepago", IsActive = true };
            var reserva = new Reserva { Id = 1, NumeroReserva = "F-1", Name = "Reserva 1", Status = EstadoReserva.Confirmed, PayerId = customer.Id };

            var realPayment = new Payment
            {
                Id = 100,
                ReservaId = reserva.Id,
                Amount = 150m,
                Currency = "ARS",
                PaidAt = DateTime.UtcNow,
                Status = "Paid",
                Method = "Transfer",
                AffectsCash = true
            };

            // Puente de sobrepago: Method=SaldoAFavor, AffectsCash=false, OriginalPaymentId=cobro real, monto
            // negativo y Notes con GUID (lo que NO debe filtrarse al cliente).
            var overpaymentBridge = new Payment
            {
                Id = 101,
                ReservaId = reserva.Id,
                Amount = -50m,
                Currency = "ARS",
                PaidAt = DateTime.UtcNow,
                Status = "Paid",
                Method = OverpaymentCreditCleanup.BridgeMethod,
                AffectsCash = false,
                OriginalPaymentId = realPayment.Id,
                Notes = $"Sobrepago trasladado a saldo a favor del cliente (cobro {Guid.NewGuid()})."
            };

            context.Customers.Add(customer);
            context.Reservas.Add(reserva);
            context.Payments.AddRange(realPayment, overpaymentBridge);
            await context.SaveChangesAsync();

            var service = new CustomerService(context, new FinancePositionService(context));

            // Act
            var result = await service.GetCustomerAccountPaymentsAsync(customer.Id, new PagedQuery(), CancellationToken.None);

            // Assert: solo aparece el cobro real; el puente (y su GUID) quedan fuera.
            Assert.Single(result.Items);
            Assert.Equal(150m, result.Items.First().Amount);
            Assert.DoesNotContain(result.Items, item => item.Method == OverpaymentCreditCleanup.BridgeMethod);
        }

        /// <summary>
        /// Multimoneda (regla dura del producto): cada cobro debe viajar con SU moneda real para que el
        /// front pueda agrupar y llevar saldo corriente por moneda sin mezclar ARS con USD. Verifica que
        /// el DTO toma la moneda directamente de <c>Payment.Currency</c>, sin forzar pesos.
        /// </summary>
        [Fact]
        public async Task GetCustomerAccountPaymentsAsync_ShouldCarryEachPaymentCurrency()
        {
            // Arrange: misma reserva, un cobro en ARS y otro en USD.
            using var context = new AppDbContext(_dbOptions);

            var customer = new Customer { Id = 9, FullName = "Cliente Multimoneda", IsActive = true };
            var reserva = new Reserva { Id = 2, NumeroReserva = "F-2", Name = "Reserva 2", Status = EstadoReserva.Confirmed, PayerId = customer.Id };

            var pesosPayment = new Payment
            {
                Id = 200,
                ReservaId = reserva.Id,
                Amount = 100m,
                Currency = "ARS",
                PaidAt = DateTime.UtcNow.AddMinutes(-5),
                Status = "Paid",
                Method = "Transfer",
                AffectsCash = true
            };

            var dollarPayment = new Payment
            {
                Id = 201,
                ReservaId = reserva.Id,
                Amount = 50m,
                Currency = "USD",
                PaidAt = DateTime.UtcNow,
                Status = "Paid",
                Method = "Transfer",
                AffectsCash = true
            };

            context.Customers.Add(customer);
            context.Reservas.Add(reserva);
            context.Payments.AddRange(pesosPayment, dollarPayment);
            await context.SaveChangesAsync();

            var service = new CustomerService(context, new FinancePositionService(context));

            // Act
            var result = await service.GetCustomerAccountPaymentsAsync(customer.Id, new PagedQuery(), CancellationToken.None);

            // Assert: cada fila conserva su moneda; ninguna queda vacia.
            Assert.Equal(2, result.Items.Count);
            var arsItem = result.Items.Single(item => item.Amount == 100m);
            var usdItem = result.Items.Single(item => item.Amount == 50m);
            Assert.Equal("ARS", arsItem.Currency);
            Assert.Equal("USD", usdItem.Currency);
        }

        /// <summary>
        /// ADR-021 (pagar en otra moneda): un cobro CRUZADO entro en USD pero se imputo a deuda en ARS. El
        /// extracto del cliente agrupa y lleva saldo corriente por la moneda IMPUTADA, no por la de caja. Verifica
        /// que el DTO expone ImputedCurrency="ARS" + ImputedAmount=el equivalente, conservando Currency="USD" +
        /// Amount=la caja real, para que el saldo por moneda reconcilie con lo que el cliente debe.
        /// </summary>
        [Fact]
        public async Task GetCustomerAccountPaymentsAsync_CrossCurrencyPayment_ShouldExposeImputedCurrencyAndAmount()
        {
            // Arrange: cobro de 50 USD que cancelo 60.000 ARS de deuda (TC 1.200).
            using var context = new AppDbContext(_dbOptions);

            var customer = new Customer { Id = 13, FullName = "Cliente Cruzado", IsActive = true };
            var reserva = new Reserva { Id = 4, NumeroReserva = "F-4", Name = "Reserva 4", Status = EstadoReserva.Confirmed, PayerId = customer.Id };

            var crossCurrencyPayment = new Payment
            {
                Id = 400,
                ReservaId = reserva.Id,
                Amount = 50m,
                Currency = "USD",            // caja: entro en dolares
                ImputedCurrency = "ARS",     // pero se imputo a deuda en pesos
                ImputedAmount = 60000m,      // equivalente que bajo del saldo ARS
                ExchangeRate = 1200m,
                PaidAt = DateTime.UtcNow,
                Status = "Paid",
                Method = "Transfer",
                AffectsCash = true
            };

            context.Customers.Add(customer);
            context.Reservas.Add(reserva);
            context.Payments.Add(crossCurrencyPayment);
            await context.SaveChangesAsync();

            var service = new CustomerService(context, new FinancePositionService(context));

            // Act
            var result = await service.GetCustomerAccountPaymentsAsync(customer.Id, new PagedQuery(), CancellationToken.None);

            // Assert: la caja queda en USD/50; la imputacion (lo que agrupa el extracto) en ARS/60.000.
            var item = Assert.Single(result.Items);
            Assert.Equal("USD", item.Currency);
            Assert.Equal(50m, item.Amount);
            Assert.Equal("ARS", item.ImputedCurrency);
            Assert.Equal(60000m, item.ImputedAmount);
        }

        /// <summary>
        /// Cobro NO cruzado (caso normal): entro y se imputo en la misma moneda. Con ImputedCurrency/ImputedAmount
        /// en null, el DTO debe caer al fallback (?? Currency / ?? Amount) y exponer imputado == caja. Asi el
        /// extracto agrupa estos cobros sin tratamiento especial.
        /// </summary>
        [Fact]
        public async Task GetCustomerAccountPaymentsAsync_SameCurrencyPayment_ShouldFallbackImputedToCash()
        {
            // Arrange: cobro de 100 ARS sin imputacion cruzada (campos imputados en null).
            using var context = new AppDbContext(_dbOptions);

            var customer = new Customer { Id = 15, FullName = "Cliente Simple", IsActive = true };
            var reserva = new Reserva { Id = 5, NumeroReserva = "F-5", Name = "Reserva 5", Status = EstadoReserva.Confirmed, PayerId = customer.Id };

            var simplePayment = new Payment
            {
                Id = 500,
                ReservaId = reserva.Id,
                Amount = 100m,
                Currency = "ARS",
                ImputedCurrency = null, // no cruzado: se imputa a su propia moneda
                ImputedAmount = null,
                PaidAt = DateTime.UtcNow,
                Status = "Paid",
                Method = "Transfer",
                AffectsCash = true
            };

            context.Customers.Add(customer);
            context.Reservas.Add(reserva);
            context.Payments.Add(simplePayment);
            await context.SaveChangesAsync();

            var service = new CustomerService(context, new FinancePositionService(context));

            // Act
            var result = await service.GetCustomerAccountPaymentsAsync(customer.Id, new PagedQuery(), CancellationToken.None);

            // Assert: imputado == caja (mismo codigo ISO y mismo monto).
            var item = Assert.Single(result.Items);
            Assert.Equal("ARS", item.Currency);
            Assert.Equal(100m, item.Amount);
            Assert.Equal("ARS", item.ImputedCurrency);
            Assert.Equal(100m, item.ImputedAmount);
        }

        /// <summary>
        /// Multimoneda: el comprobante guarda la moneda en codigo de ARCA (<c>MonId</c>: "PES"/"DOL"), pero
        /// el DTO debe exponerla en ISO ("ARS"/"USD") para que el front la agrupe junto a los cobros y NO
        /// filtre el codigo interno de ARCA. Verifica el mapeo PES->ARS y DOL->USD.
        /// </summary>
        [Fact]
        public async Task GetCustomerAccountInvoicesAsync_ShouldExposeIsoCurrencyFromMonId()
        {
            // Arrange: una reserva con una factura en pesos (PES) y otra en dolares (DOL).
            using var context = new AppDbContext(_dbOptions);

            var customer = new Customer { Id = 11, FullName = "Cliente Facturas", IsActive = true };
            var reserva = new Reserva { Id = 3, NumeroReserva = "F-3", Name = "Reserva 3", Status = EstadoReserva.Confirmed, PayerId = customer.Id };

            var pesosInvoice = new Invoice
            {
                Id = 300,
                ReservaId = reserva.Id,
                TipoComprobante = 6, // Factura B
                PuntoDeVenta = 1,
                NumeroComprobante = 1001,
                ImporteTotal = 100m,
                MonId = "PES",
                CreatedAt = DateTime.UtcNow.AddMinutes(-5)
            };

            var dollarInvoice = new Invoice
            {
                Id = 301,
                ReservaId = reserva.Id,
                TipoComprobante = 6, // Factura B
                PuntoDeVenta = 1,
                NumeroComprobante = 1002,
                ImporteTotal = 50m,
                MonId = "DOL",
                CreatedAt = DateTime.UtcNow
            };

            context.Customers.Add(customer);
            context.Reservas.Add(reserva);
            context.Invoices.AddRange(pesosInvoice, dollarInvoice);
            await context.SaveChangesAsync();

            var service = new CustomerService(context, new FinancePositionService(context));

            // Act
            var result = await service.GetCustomerAccountInvoicesAsync(customer.Id, new PagedQuery(), CancellationToken.None);

            // Assert: cada comprobante expone ISO, nunca el codigo ARCA crudo.
            Assert.Equal(2, result.Items.Count);
            var arsInvoice = result.Items.Single(item => item.NumeroComprobante == 1001);
            var usdInvoice = result.Items.Single(item => item.NumeroComprobante == 1002);
            Assert.Equal("ARS", arsInvoice.Currency);
            Assert.Equal("USD", usdInvoice.Currency);
        }

        /// <summary>
        /// Tanda 6 (C4): la fila de reserva de la cuenta del cliente ahora expone la plata REAL por moneda
        /// (PorMoneda/EsMultimoneda) leyendo ReservaMoneyByCurrency, para que el front deje de mostrar "ARS"
        /// hardcodeado. Aca una reserva multimoneda (ARS + USD) debe traer ambas lineas, marcada como multimoneda.
        /// </summary>
        [Fact]
        public async Task GetCustomerAccountReservasAsync_ShouldExposeMoneyByCurrency()
        {
            // Arrange: un cliente con una reserva multimoneda y sus filas de plata materializadas.
            using var context = new AppDbContext(_dbOptions);
            var customer = new Customer { Id = 1, FullName = "Multi", IsActive = true };
            context.Customers.Add(customer);
            var reserva = new Reserva { Id = 10, NumeroReserva = "F-10", Name = "Reserva 10", Status = EstadoReserva.Confirmed, PayerId = customer.Id };
            context.Reservas.Add(reserva);
            context.ReservaMoneyByCurrency.AddRange(
                new ReservaMoneyByCurrency { ReservaId = 10, Currency = "ARS", TotalSale = 1000m, TotalPaid = 400m, Balance = 600m },
                new ReservaMoneyByCurrency { ReservaId = 10, Currency = "USD", TotalSale = 500m, TotalPaid = 500m, Balance = 0m });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = new CustomerService(context, new FinancePositionService(context));

            // Act
            var result = await service.GetCustomerAccountReservasAsync(customer.Id, new PagedQuery(), CancellationToken.None);

            // Assert
            var row = Assert.Single(result.Items);
            Assert.True(row.EsMultimoneda);
            Assert.Equal(2, row.PorMoneda.Count);

            var ars = row.PorMoneda.Single(line => line.Currency == "ARS");
            Assert.Equal(1000m, ars.TotalSale);
            Assert.Equal(400m, ars.Paid);
            Assert.Equal(600m, ars.Balance);

            var usd = row.PorMoneda.Single(line => line.Currency == "USD");
            Assert.Equal(500m, usd.TotalSale);
            Assert.Equal(0m, usd.Balance);
        }

        /// <summary>
        /// Tanda 6 (C4): una reserva SIN filas de plata materializadas (nueva o legacy sin backfill) queda con
        /// PorMoneda vacio; el front cae al escalar. El endpoint no debe romperse ni inventar lineas.
        /// </summary>
        [Fact]
        public async Task GetCustomerAccountReservasAsync_NoMoneyRows_LeavesPorMonedaEmpty()
        {
            using var context = new AppDbContext(_dbOptions);
            var customer = new Customer { Id = 1, FullName = "Sin plata", IsActive = true };
            context.Customers.Add(customer);
            context.Reservas.Add(new Reserva { Id = 11, NumeroReserva = "F-11", Name = "Reserva 11", Status = EstadoReserva.Budget, PayerId = customer.Id, TotalSale = 0m, Balance = 0m, TotalPaid = 0m });
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var service = new CustomerService(context, new FinancePositionService(context));

            var result = await service.GetCustomerAccountReservasAsync(customer.Id, new PagedQuery(), CancellationToken.None);

            var row = Assert.Single(result.Items);
            Assert.Empty(row.PorMoneda);
            Assert.False(row.EsMultimoneda);
        }
    }
}
