using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    public partial class Adr041_M2_AddSupplierCreditLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierCreditEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false, defaultValue: "ARS"),
                    SourceSupplierPaymentId = table.Column<int>(type: "integer", nullable: true),
                    SourceOperatorRefundReceivedId = table.Column<int>(type: "integer", nullable: true),
                    CreditedAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    RemainingBalance = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    IsFullyConsumed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    CreatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCreditEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierCreditEntries_SupplierPayments_SourceSupplierPaymen~",
                        column: x => x.SourceSupplierPaymentId,
                        principalTable: "SupplierPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCreditEntries_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SupplierCreditApplications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    SupplierCreditEntryId = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    TargetReservaId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    ReversesApplicationId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    CreatedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReversalReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierCreditApplications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierCreditApplications_SupplierCreditApplications_Rever~",
                        column: x => x.ReversesApplicationId,
                        principalTable: "SupplierCreditApplications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCreditApplications_SupplierCreditEntries_SupplierCr~",
                        column: x => x.SupplierCreditEntryId,
                        principalTable: "SupplierCreditEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SupplierCreditApplications_TravelFiles_TargetReservaId",
                        column: x => x.TargetReservaId,
                        principalTable: "TravelFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditApplications_PublicId",
                table: "SupplierCreditApplications",
                column: "PublicId",
                unique: true);

            // M3 (review): UNICO PARCIAL. A lo sumo UNA contra-fila (Reversed) puede apuntar a una misma
            // aplicacion -> red dura contra la doble-reversa por carrera (Postgres rechaza la segunda con 23505).
            // Parcial (WHERE NOT NULL) para no chocar entre las Applied (ReversesApplicationId = NULL).
            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditApplications_ReversesApplicationId",
                table: "SupplierCreditApplications",
                column: "ReversesApplicationId",
                unique: true,
                filter: "\"ReversesApplicationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditApplications_SupplierCreditEntryId",
                table: "SupplierCreditApplications",
                column: "SupplierCreditEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditApplications_TargetReservaId",
                table: "SupplierCreditApplications",
                column: "TargetReservaId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditEntries_PublicId",
                table: "SupplierCreditEntries",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditEntries_SourceSupplierPaymentId",
                table: "SupplierCreditEntries",
                column: "SourceSupplierPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditEntries_SupplierId_Currency",
                table: "SupplierCreditEntries",
                columns: new[] { "SupplierId", "Currency" });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierCreditEntries_SupplierId_IsFullyConsumed",
                table: "SupplierCreditEntries",
                columns: new[] { "SupplierId", "IsFullyConsumed" });

            // ADR-041 TANDA 3: saldo a favor del operador NUNCA queda negativo ni supera el monto inicial. Es la
            // red dura bajo concurrencia (dos applies paralelos): si ambos pasan la validacion en memoria pero al
            // persistir uno drenaria de mas, Postgres rechaza con 23514. Espejo de
            // chk_ClientCreditEntries_remaining_non_negative del lado cliente.
            migrationBuilder.Sql("""
                ALTER TABLE "SupplierCreditEntries"
                  DROP CONSTRAINT IF EXISTS chk_SupplierCreditEntries_remaining_non_negative;
                ALTER TABLE "SupplierCreditEntries"
                  ADD CONSTRAINT chk_SupplierCreditEntries_remaining_non_negative
                  CHECK ("RemainingBalance" >= 0 AND "RemainingBalance" <= "CreditedAmount");
                """);

            // El monto de una aplicacion/reversa es SIEMPRE positivo (el signo economico lo da Kind).
            migrationBuilder.Sql("""
                ALTER TABLE "SupplierCreditApplications"
                  DROP CONSTRAINT IF EXISTS chk_SupplierCreditApplications_amount_positive;
                ALTER TABLE "SupplierCreditApplications"
                  ADD CONSTRAINT chk_SupplierCreditApplications_amount_positive
                  CHECK ("Amount" > 0);
                """);

            // ===== BACKFILL IDEMPOTENTE =====
            // Por cada SupplierBalanceByCurrency con saldo NEGATIVO (sobrepago al operador), materializamos ese
            // sobrepago como un SupplierCreditEntry con CreditedAmount = RemainingBalance = -Balance. Asi el pool
            // arranca cumpliendo el invariante (Σ RemainingBalance == max(0,-Balance), sin aplicaciones todavia) y
            // el sobrepago historico ya presente en el balance derivado queda consumible desde el dia uno.
            //
            // IDEMPOTENTE: solo inserta si TODAVIA NO existe ningun entry para ese (proveedor, moneda). Si la
            // migracion se re-corriera, no duplica. SourceSupplierPaymentId queda NULL (es consolidacion historica,
            // no nace de un pago puntual). PublicId via gen_random_uuid() (igual que el resto del repo).
            migrationBuilder.Sql("""
                INSERT INTO "SupplierCreditEntries"
                    ("PublicId", "SupplierId", "Currency", "SourceSupplierPaymentId", "SourceOperatorRefundReceivedId",
                     "CreditedAmount", "RemainingBalance", "IsFullyConsumed", "CreatedAt", "CreatedByUserId", "CreatedByUserName")
                SELECT
                    gen_random_uuid(),
                    b."SupplierId",
                    b."Currency",
                    NULL,
                    NULL,
                    -b."Balance",
                    -b."Balance",
                    false,
                    now(),
                    NULL,
                    NULL
                FROM "SupplierBalanceByCurrency" b
                WHERE b."Balance" < 0
                  AND NOT EXISTS (
                      SELECT 1 FROM "SupplierCreditEntries" e
                      WHERE e."SupplierId" = b."SupplierId" AND e."Currency" = b."Currency"
                  );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierCreditApplications");

            migrationBuilder.DropTable(
                name: "SupplierCreditEntries");
        }
    }
}
