using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppBotConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WhatsAppBotConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WelcomeMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AskInterestMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AskDatesMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AskTravelersMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ThanksMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    AgentRequestMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DuplicateMessage = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppBotConfigs", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WhatsAppBotConfigs");
        }
    }
}
