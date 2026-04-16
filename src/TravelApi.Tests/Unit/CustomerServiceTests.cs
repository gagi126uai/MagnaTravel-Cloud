using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
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
            var service = new CustomerService(context);
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

            var service = new CustomerService(context);

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

            var service = new CustomerService(context);
            var updatedCustomer = new Customer { Id = 1, FullName = "New Name", IsActive = true };

            // Act
            var result = await service.UpdateCustomerAsync(1, updatedCustomer, CancellationToken.None);

            // Assert
            Assert.Equal("New Name", result.FullName);
            var dbCustomer = await context.Customers.FindAsync(1);
            Assert.NotNull(dbCustomer);
            Assert.Equal("New Name", dbCustomer.FullName);
        }
    }
}
