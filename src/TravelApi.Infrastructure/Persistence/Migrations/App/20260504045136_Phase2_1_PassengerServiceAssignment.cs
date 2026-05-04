using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Phase2_1_PassengerServiceAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PassengerServiceAssignments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    PassengerId = table.Column<int>(type: "integer", nullable: false),
                    ServiceType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ServiceId = table.Column<int>(type: "integer", nullable: false),
                    RoomNumber = table.Column<int>(type: "integer", nullable: true),
                    SeatNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PassengerServiceAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PassengerServiceAssignments_Passengers_PassengerId",
                        column: x => x.PassengerId,
                        principalTable: "Passengers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PassengerServiceAssignments_PassengerId_ServiceType_Service~",
                table: "PassengerServiceAssignments",
                columns: new[] { "PassengerId", "ServiceType", "ServiceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PassengerServiceAssignments_PublicId",
                table: "PassengerServiceAssignments",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PassengerServiceAssignments_ServiceType_ServiceId",
                table: "PassengerServiceAssignments",
                columns: new[] { "ServiceType", "ServiceId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PassengerServiceAssignments");
        }
    }
}
