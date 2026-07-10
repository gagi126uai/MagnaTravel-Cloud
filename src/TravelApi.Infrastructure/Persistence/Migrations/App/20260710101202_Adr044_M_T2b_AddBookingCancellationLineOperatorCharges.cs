using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T2 Addendum, Decision B (2026-07-10): tabla hija NUEVA de cargos tipificados del operador por
    /// linea de cancelacion (<c>BookingCancellationLineOperatorCharge</c>). Reemplaza el diseño inicial de UN
    /// campo escalar por linea (rechazado por <c>software-architect-reviewer</c>): un operador Responsable
    /// Inscripto puede aplicar, en la MISMA cancelacion, un cargo administrativo Y una retencion fiscal
    /// SIMULTANEOS (confirmado por el contador, no hipotetico), y esos dos montos NUNCA deben mezclarse en un
    /// solo numero.
    ///
    /// <para><b>Los 2 CHECK de esta migracion</b>:
    /// <list type="bullet">
    /// <item><c>chk_BookingCancellationLineOperatorCharges_documentref_required_when_invoiced</c>: si
    /// <c>CollectionMode = FacturadaAparte</c> (1), <c>DocumentRef</c> es obligatorio (el operador facturo
    /// aparte, tiene que haber un documento del proveedor referenciado).</item>
    /// <item><c>chk_BookingCancellationLineOperatorCharges_amount_positive</c>: el monto de un cargo siempre es
    /// positivo (mismo criterio que <c>SupplierCreditApplications.Amount</c>).</item>
    /// </list>
    /// La coherencia de MONEDA (Decision B2: <c>Currency</c> del cargo == <c>Currency</c> de su
    /// <c>BookingCancellationLine</c>) NO se puede expresar como CHECK SQL en Postgres (cruza tablas): se
    /// valida en el SERVICIO al escribir (mismo punto que crea el cargo). Documentado acá como invariante dura,
    /// no como constraint de base.</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura)</b> — confirma que la tabla nueva existe vacia (no hay
    /// backfill de cargos en esta migracion; el backfill OPCIONAL de cargos legacy vive en T2c) y que los 2
    /// CHECK quedaron activos:
    /// <code>
    /// SELECT COUNT(*) FROM "BookingCancellationLineOperatorCharges"; -- esperado: 0 justo despues de aplicar
    ///
    /// SELECT conname FROM pg_constraint
    /// WHERE conrelid = '"BookingCancellationLineOperatorCharges"'::regclass AND contype = 'c';
    /// -- esperado: las 2 filas de arriba (documentref_required_when_invoiced, amount_positive)
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T2b_AddBookingCancellationLineOperatorCharges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BookingCancellationLineOperatorCharges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PublicId = table.Column<Guid>(type: "uuid", nullable: false),
                    BookingCancellationLineId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CollectionMode = table.Column<int>(type: "integer", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DocumentRef = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ConfirmedByUserId = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    ConfirmedByUserName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ConfirmedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookingCancellationLineOperatorCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookingCancellationLineOperatorCharges_BookingCancellationL~",
                        column: x => x.BookingCancellationLineId,
                        principalTable: "BookingCancellationLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineOperatorCharges_BookingCancellationLineId",
                table: "BookingCancellationLineOperatorCharges",
                column: "BookingCancellationLineId");

            migrationBuilder.CreateIndex(
                name: "IX_BookingCancellationLineOperatorCharges_PublicId",
                table: "BookingCancellationLineOperatorCharges",
                column: "PublicId",
                unique: true);

            // DocumentRef obligatorio cuando el operador factura el cargo APARTE (CollectionMode=FacturadaAparte=1):
            // esa forma de cobro exige el documento del proveedor referenciado. Retenida (0) no lo requiere.
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellationLineOperatorCharges_documentref_required_when_invoiced;
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  ADD CONSTRAINT chk_BookingCancellationLineOperatorCharges_documentref_required_when_invoiced
                  CHECK ("CollectionMode" <> 1 OR "DocumentRef" IS NOT NULL);
                """);

            // El monto de un cargo siempre es positivo (mismo criterio que SupplierCreditApplications.Amount).
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellationLineOperatorCharges_amount_positive;
                ALTER TABLE "BookingCancellationLineOperatorCharges"
                  ADD CONSTRAINT chk_BookingCancellationLineOperatorCharges_amount_positive
                  CHECK ("Amount" > 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookingCancellationLineOperatorCharges");
        }
    }
}
