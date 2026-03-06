using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;
using AutoMapper;

namespace TravelApi.Tests.Unit
{
    public class TravelFileServiceTests
    {
        private readonly DbContextOptions<AppDbContext> _dbOptions;
        private readonly Mock<IMapper> _mapperMock;

        public TravelFileServiceTests()
        {
            _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
                .Options;
            _mapperMock = new Mock<IMapper>();
        }

        [Fact]
        public async Task CreateFileAsync_ShouldCreateFile_WithCorrectInitialValues()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            var service = new TravelFileService(context, _mapperMock.Object);
            var request = new CreateFileRequest(
                "Test Trip",
                1,
                DateTime.UtcNow.AddDays(10),
                "Testing file creation"
            );

            // Act
            var result = await service.CreateFileAsync(request);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("Test Trip", result.Name);
            Assert.Equal(FileStatus.Budget, result.Status);
            Assert.StartsWith("F-", result.FileNumber);
            Assert.Equal(1, await context.TravelFiles.CountAsync());
        }

        [Fact]
        public async Task UpdateStatusAsync_ShouldUpdateStatus_WhenValid()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            var file = new TravelFile { Id = 1, Name = "Test", Status = FileStatus.Budget };
            context.TravelFiles.Add(file);
            await context.SaveChangesAsync();

            var service = new TravelFileService(context, _mapperMock.Object);

            // Act
            var result = await service.UpdateStatusAsync(1, FileStatus.Reserved);

            // Assert
            Assert.Equal(FileStatus.Reserved, result.Status);
            var dbFile = await context.TravelFiles.FindAsync(1);
            Assert.NotNull(dbFile);
            Assert.Equal(FileStatus.Reserved, dbFile.Status);
        }

        [Fact]
        public async Task UpdateStatusAsync_ShouldThrowException_WhenReturningToBudgetWithPayments()
        {
            // Arrange
            using var context = new AppDbContext(_dbOptions);
            var file = new TravelFile { Id = 1, Name = "Test", Status = FileStatus.Reserved };
            var payment = new Payment { Id = 1, TravelFileId = 1, Amount = 100, Status = "Paid" };
            context.TravelFiles.Add(file);
            context.Payments.Add(payment);
            await context.SaveChangesAsync();

            var service = new TravelFileService(context, _mapperMock.Object);

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(() => 
                service.UpdateStatusAsync(1, FileStatus.Budget));
        }
    }
}
