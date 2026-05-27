using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Fase2_M1b_AddArcaIdempotencyKeysAndInvoiceCurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "MonCotiz",
                table: "Invoices",
                type: "numeric(18,6)",
                precision: 18,
                scale: 6,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<string>(
                name: "MonId",
                table: "Invoices",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "PES");

            migrationBuilder.CreateTable(
                name: "ArcaIdempotencyKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    JobId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSeenNumeroBeforePost = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArcaIdempotencyKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArcaIdempotencyKeys_Key",
                table: "ArcaIdempotencyKeys",
                column: "Key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArcaIdempotencyKeys");

            migrationBuilder.DropColumn(
                name: "MonCotiz",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "MonId",
                table: "Invoices");
        }
    }
}
