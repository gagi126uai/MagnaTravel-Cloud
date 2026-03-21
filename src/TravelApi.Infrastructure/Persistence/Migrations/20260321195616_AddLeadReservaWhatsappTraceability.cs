using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadReservaWhatsappTraceability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceLeadId",
                table: "TravelFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SourceQuoteId",
                table: "TravelFiles",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhatsAppPhoneOverride",
                table: "TravelFiles",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LeadId",
                table: "Quotes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WhatsAppDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReservaId = table.Column<int>(type: "integer", nullable: false),
                    CustomerId = table.Column<int>(type: "integer", nullable: true),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Direction = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    MessageText = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AttachmentName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    BotMessageId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SentBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PreparedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WhatsAppDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WhatsAppDeliveries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_WhatsAppDeliveries_TravelFiles_ReservaId",
                        column: x => x.ReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_SourceLeadId",
                table: "TravelFiles",
                column: "SourceLeadId");

            migrationBuilder.CreateIndex(
                name: "IX_TravelFiles_SourceQuoteId",
                table: "TravelFiles",
                column: "SourceQuoteId");

            migrationBuilder.CreateIndex(
                name: "IX_Quotes_LeadId",
                table: "Quotes",
                column: "LeadId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppDeliveries_CustomerId",
                table: "WhatsAppDeliveries",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_WhatsAppDeliveries_ReservaId",
                table: "WhatsAppDeliveries",
                column: "ReservaId");

            migrationBuilder.AddForeignKey(
                name: "FK_Quotes_Leads_LeadId",
                table: "Quotes",
                column: "LeadId",
                principalTable: "Leads",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TravelFiles_Leads_SourceLeadId",
                table: "TravelFiles",
                column: "SourceLeadId",
                principalTable: "Leads",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_TravelFiles_Quotes_SourceQuoteId",
                table: "TravelFiles",
                column: "SourceQuoteId",
                principalTable: "Quotes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Quotes_Leads_LeadId",
                table: "Quotes");

            migrationBuilder.DropForeignKey(
                name: "FK_TravelFiles_Leads_SourceLeadId",
                table: "TravelFiles");

            migrationBuilder.DropForeignKey(
                name: "FK_TravelFiles_Quotes_SourceQuoteId",
                table: "TravelFiles");

            migrationBuilder.DropTable(
                name: "WhatsAppDeliveries");

            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_SourceLeadId",
                table: "TravelFiles");

            migrationBuilder.DropIndex(
                name: "IX_TravelFiles_SourceQuoteId",
                table: "TravelFiles");

            migrationBuilder.DropIndex(
                name: "IX_Quotes_LeadId",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "SourceLeadId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "SourceQuoteId",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "WhatsAppPhoneOverride",
                table: "TravelFiles");

            migrationBuilder.DropColumn(
                name: "LeadId",
                table: "Quotes");
        }
    }
}
