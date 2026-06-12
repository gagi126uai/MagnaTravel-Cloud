using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
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
    }
}
