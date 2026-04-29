using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class FixHistoricalUUIDsInNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var sql = @"
                UPDATE ""Vouchers"" SET ""CreatedByUserName"" = u.""FullName"" FROM ""AspNetUsers"" u WHERE ""Vouchers"".""CreatedByUserId"" = u.""Id"" AND length(""Vouchers"".""CreatedByUserName"") = 36;
                UPDATE ""Vouchers"" SET ""IssuedByUserName"" = u.""FullName"" FROM ""AspNetUsers"" u WHERE ""Vouchers"".""IssuedByUserId"" = u.""Id"" AND length(""Vouchers"".""IssuedByUserName"") = 36;
                UPDATE ""Vouchers"" SET ""AuthorizedBySuperiorUserName"" = u.""FullName"" FROM ""AspNetUsers"" u WHERE ""Vouchers"".""AuthorizedBySuperiorUserId"" = u.""Id"" AND length(""Vouchers"".""AuthorizedBySuperiorUserName"") = 36;
                UPDATE ""AuditLogs"" SET ""UserName"" = u.""FullName"" FROM ""AspNetUsers"" u WHERE ""AuditLogs"".""UserId"" = u.""Id"" AND length(""AuditLogs"".""UserName"") = 36;
                UPDATE ""Invoices"" SET ""ForcedByUserName"" = u.""FullName"" FROM ""AspNetUsers"" u WHERE ""Invoices"".""ForcedByUserId"" = u.""Id"" AND length(""Invoices"".""ForcedByUserName"") = 36;
                UPDATE ""MessageDeliveries"" SET ""SentByUserName"" = u.""FullName"" FROM ""AspNetUsers"" u WHERE ""MessageDeliveries"".""SentByUserId"" = u.""Id"" AND length(""MessageDeliveries"".""SentByUserName"") = 36;
            ";
            migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
