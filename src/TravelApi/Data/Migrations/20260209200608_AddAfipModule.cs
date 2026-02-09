using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAfipModule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AfipSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Cuit = table.Column<long>(type: "bigint", nullable: false),
                    PuntoDeVenta = table.Column<int>(type: "integer", nullable: false),
                    IsProduction = table.Column<bool>(type: "boolean", nullable: false),
                    CertificatePath = table.Column<string>(type: "text", nullable: true),
                    CertificatePassword = table.Column<string>(type: "text", nullable: true),
                    Token = table.Column<string>(type: "text", nullable: true),
                    Sign = table.Column<string>(type: "text", nullable: true),
                    TokenExpiration = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AfipSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TipoComprobante = table.Column<int>(type: "integer", nullable: false),
                    PuntoDeVenta = table.Column<int>(type: "integer", nullable: false),
                    NumeroComprobante = table.Column<long>(type: "bigint", nullable: false),
                    CAE = table.Column<string>(type: "text", nullable: true),
                    VencimientoCAE = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Resultado = table.Column<string>(type: "text", nullable: true),
                    Observaciones = table.Column<string>(type: "text", nullable: true),
                    ImporteTotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ImporteNeto = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ImporteIva = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TravelFileId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoices_TravelFiles_TravelFileId",
                        column: x => x.TravelFileId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TravelFileId",
                table: "Invoices",
                column: "TravelFileId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AfipSettings");

            migrationBuilder.DropTable(
                name: "Invoices");
        }
    }
}
