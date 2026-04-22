using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace TravelApi.Infrastructure.Persistence;

public class ReservationsDbContextFactory : IDesignTimeDbContextFactory<ReservationsDbContext>
{
    public ReservationsDbContext CreateDbContext(string[] args)
    {
        var basePath = Path.Combine(Directory.GetCurrentDirectory(), "../TravelReservations.Api");
        var configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var builder = new DbContextOptionsBuilder<ReservationsDbContext>();
        var connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Host=localhost;Database=travel;Username=traveluser;Password=TravelPass123!;Include Error Detail=true;";

        builder.UseNpgsql(connectionString, b => b.MigrationsAssembly("TravelApi.Infrastructure"));

        return new ReservationsDbContext(builder.Options);
    }
}
