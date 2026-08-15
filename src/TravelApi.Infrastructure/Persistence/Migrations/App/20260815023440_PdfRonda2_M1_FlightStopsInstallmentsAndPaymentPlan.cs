using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class PdfRonda2_M1_FlightStopsInstallmentsAndPaymentPlan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "InstallmentAmount",
                table: "TransferBookings",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstallmentsCount",
                table: "TransferBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InstallmentAmount",
                table: "Reservations",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstallmentsCount",
                table: "Reservations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InstallmentAmount",
                table: "PackageBookings",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstallmentsCount",
                table: "PackageBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InstallmentAmount",
                table: "FlightSegments",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstallmentsCount",
                table: "FlightSegments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutboundStopPlace",
                table: "FlightSegments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutboundStopWait",
                table: "FlightSegments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OutboundStopsCount",
                table: "FlightSegments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnStopPlace",
                table: "FlightSegments",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnStopWait",
                table: "FlightSegments",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReturnStopsCount",
                table: "FlightSegments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "InstallmentAmount",
                table: "AssistanceBookings",
                type: "numeric(12,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InstallmentsCount",
                table: "AssistanceBookings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BudgetPaymentPlanInstallments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    DueText = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BudgetPaymentPlanInstallments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BudgetPaymentPlanInstallments_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BudgetPaymentPlanInstallments_ReservaId_Position",
                table: "BudgetPaymentPlanInstallments",
                columns: new[] { "ReservaId", "Position" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BudgetPaymentPlanInstallments");

            migrationBuilder.DropColumn(
                name: "InstallmentAmount",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "InstallmentsCount",
                table: "TransferBookings");

            migrationBuilder.DropColumn(
                name: "InstallmentAmount",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "InstallmentsCount",
                table: "Reservations");

            migrationBuilder.DropColumn(
                name: "InstallmentAmount",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "InstallmentsCount",
                table: "PackageBookings");

            migrationBuilder.DropColumn(
                name: "InstallmentAmount",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "InstallmentsCount",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OutboundStopPlace",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OutboundStopWait",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "OutboundStopsCount",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "ReturnStopPlace",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "ReturnStopWait",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "ReturnStopsCount",
                table: "FlightSegments");

            migrationBuilder.DropColumn(
                name: "InstallmentAmount",
                table: "AssistanceBookings");

            migrationBuilder.DropColumn(
                name: "InstallmentsCount",
                table: "AssistanceBookings");
        }
    }
}
