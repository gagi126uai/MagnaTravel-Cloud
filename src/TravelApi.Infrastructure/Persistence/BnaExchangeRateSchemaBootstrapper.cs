using Microsoft.EntityFrameworkCore;

namespace TravelApi.Infrastructure.Persistence;

public static class BnaExchangeRateSchemaBootstrapper
{
    private static readonly string[] Statements =
    {
        @"CREATE TABLE IF NOT EXISTS ""BnaExchangeRateSnapshots"" (
            ""Id"" integer NOT NULL PRIMARY KEY,
            ""UsdSeller"" numeric(18,2) NOT NULL,
            ""EuroSeller"" numeric(18,2) NOT NULL,
            ""RealSeller"" numeric(18,2) NOT NULL,
            ""PublishedDate"" character varying(20) NOT NULL,
            ""PublishedTime"" character varying(10) NOT NULL,
            ""Source"" character varying(500) NOT NULL,
            ""FetchedAt"" timestamp with time zone NOT NULL
        );"
    };

    public static async Task EnsureAsync(AppDbContext dbContext, CancellationToken cancellationToken = default)
    {
        foreach (var statement in Statements)
        {
            await dbContext.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }
}
