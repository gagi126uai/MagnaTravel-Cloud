using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using Xunit;

namespace TravelApi.Tests.Integration
{
    public class RepositoryTests
    {
        private readonly DbContextOptions<AppDbContext> _dbOptions;

        public RepositoryTests()
        {
            _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
        }

        [Fact]
        public async Task GenericRepository_AddAsync_ShouldPersistEntity()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            var repository = new Repository<Supplier>(context);
            var supplier = new Supplier { Name = "Test Supplier", IsActive = true };

            // Act
            await repository.AddAsync(supplier);
            await context.SaveChangesAsync();

            // Assert
            Assert.True(supplier.Id > 0);
            var dbSupplier = await context.Suppliers.FindAsync(supplier.Id);
            Assert.NotNull(dbSupplier);
            Assert.Equal("Test Supplier", dbSupplier.Name);
        }

        [Fact]
        public async Task GenericRepository_GetAllAsync_ShouldReturnAllEntities()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            context.Suppliers.AddRange(new[]
            {
                new Supplier { Name = "S1", IsActive = true },
                new Supplier { Name = "S2", IsActive = true }
            });
            await context.SaveChangesAsync();

            var repository = new Repository<Supplier>(context);

            // Act
            var results = await repository.ListAllAsync();

            // Assert
            Assert.Equal(2, results.Count());
        }

        [Fact]
        public async Task GenericRepository_DeleteAsync_ShouldRemoveEntity()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            var supplier = new Supplier { Id = 1, Name = "To Delete", IsActive = true };
            context.Suppliers.Add(supplier);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var repository = new Repository<Supplier>(context);

            // Act
            var entity = await repository.GetByIdAsync(1);
            Assert.NotNull(entity);
            await repository.DeleteAsync(entity);
            await context.SaveChangesAsync();

            // Assert
            var dbSupplier = await context.Suppliers.FindAsync(1);
            Assert.Null(dbSupplier);
        }
    }
}
