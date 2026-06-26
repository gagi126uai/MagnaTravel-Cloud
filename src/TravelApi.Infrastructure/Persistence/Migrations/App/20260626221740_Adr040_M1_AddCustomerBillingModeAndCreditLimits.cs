using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr040_M1_AddCustomerBillingModeAndCreditLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ADR-040: las filas EXISTENTes de settings deben quedar en FRENA (true), no en avisar. El default
            // en C# es true (BlockTravelWhenCreditExceeded), pero EF no lo traduce a default SQL; por eso forzamos
            // defaultValue:true aca para el backfill de la fila singleton ya creada. Es la posicion segura del
            // dueño: pasarse del limite al viajar FRENA, salvo que la agencia elija avisar.
            migrationBuilder.AddColumn<bool>(
                name: "BlockTravelWhenCreditExceeded",
                table: "OperationalFinanceSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "DefaultCustomerBillingMode",
                table: "OperationalFinanceSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "BillingMode",
                table: "Customers",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentTermsDays",
                table: "Customers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "CustomerCreditLimitByCurrency",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CustomerId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS"),
                    Limit = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerCreditLimitByCurrency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerCreditLimitByCurrency_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerCreditLimitByCurrency_CustomerId_Currency",
                table: "CustomerCreditLimitByCurrency",
                columns: new[] { "CustomerId", "Currency" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerCreditLimitByCurrency");

            migrationBuilder.DropColumn(
                name: "BlockTravelWhenCreditExceeded",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "DefaultCustomerBillingMode",
                table: "OperationalFinanceSettings");

            migrationBuilder.DropColumn(
                name: "BillingMode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "PaymentTermsDays",
                table: "Customers");
        }
    }
}
