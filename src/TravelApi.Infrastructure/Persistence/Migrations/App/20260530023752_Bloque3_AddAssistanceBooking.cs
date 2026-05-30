using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Bloque3_AddAssistanceBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AssistanceBookings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    TravelFileId = table.Column<int>(type: "integer", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    RateId = table.Column<int>(type: "integer", nullable: true),
                    PolicyNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    PlanType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CoverageLimit = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CoverageZone = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Adults = table.Column<int>(type: "integer", nullable: false),
                    Children = table.Column<int>(type: "integer", nullable: false),
                    ConfirmationNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    NetCost = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    SalePrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Commission = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    NonRefundableConceptsJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssistanceBookings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssistanceBookings_Rates_RateId",
                        column: x => x.RateId,
                        principalTable: "Rates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AssistanceBookings_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AssistanceBookings_TravelFiles_TravelFileId",
                        column: x => x.TravelFileId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssistanceBookings_PublicId",
                table: "AssistanceBookings",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssistanceBookings_RateId",
                table: "AssistanceBookings",
                column: "RateId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistanceBookings_SupplierId",
                table: "AssistanceBookings",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_AssistanceBookings_TravelFileId",
                table: "AssistanceBookings",
                column: "TravelFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssistanceBookings");
        }
    }
}
