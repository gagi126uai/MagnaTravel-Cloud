using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// ADR-044 T2 Addendum, Decision B1/B3 (2026-07-10): agrega <c>RetainedDeductionAmount</c> a
    /// <c>BookingCancellationLine</c> — el eje CAJA/RefundCap (SUM de cargos <c>Kind != Withholding AND
    /// CollectionMode == Retenida</c>), columna FISICA separada de <c>PenaltyAmount</c> (eje CLIENTE, la que
    /// alimenta la Nota de Debito). Antes de esta tanda existia UN solo agregado (<c>PenaltyAmount</c>) que
    /// mezclaba ambos ejes; separarlos es lo que permite que <c>Withholding</c> (credito fiscal) y
    /// <c>FacturadaAparte</c> (deuda AP, no retencion) NUNCA bajen el reembolso esperado del operador.
    ///
    /// <para><b>BACKFILL (obligatorio, no opcional)</b>: para toda linea con <c>PenaltyStatus = Confirmed</c>
    /// (1) ANTES de esta tanda, <c>RetainedDeductionAmount = PenaltyAmount</c>. Es EXACTO para el historico:
    /// antes de T2 el UNICO camino para confirmar una multa era un cargo administrativo retenido (no existia
    /// <c>Tax</c>/<c>Withholding</c>/<c>FacturadaAparte</c>), asi que los dos agregados coinciden sin
    /// aproximacion. Las lineas <c>Estimated</c>/<c>Waived</c> (sin <c>PenaltyAmount</c>) quedan en el default
    /// 0 de la columna (no hubo multa retenida que registrar).</para>
    ///
    /// <para><b>Por que en esta misma migracion y no en el service</b>: el backfill es SQL crudo (no logica de
    /// dominio) — un simple <c>UPDATE ... SET = </c> sobre un criterio de estado ya persistido, exactamente el
    /// patron que ya usa el repo para backfills de columnas nuevas (ver <c>Adr041_M2</c>). Es idempotente: una
    /// segunda corrida vuelve a poner el mismo valor (no hay condicion de "ya corrido" que salte, pero el
    /// resultado es identico, asi que no hace falta guardarla).</para>
    ///
    /// <para><b>Consulta de validacion (solo lectura, para correr antes de aplicar)</b> — cuenta las lineas que
    /// el backfill va a tocar y previsualiza el valor que van a recibir:
    /// <code>
    /// SELECT COUNT(*) AS lineas_a_backfillear,
    ///        SUM("PenaltyAmount") AS suma_retaineddeduction_resultante
    /// FROM "BookingCancellationLines"
    /// WHERE "PenaltyStatus" = 1 AND "PenaltyAmount" IS NOT NULL;
    ///
    /// -- Verificacion POST-migracion (debe dar 0 filas = ninguna confirmada quedo sin su eje caja):
    /// SELECT "Id", "PenaltyAmount", "RetainedDeductionAmount"
    /// FROM "BookingCancellationLines"
    /// WHERE "PenaltyStatus" = 1 AND "PenaltyAmount" IS NOT NULL
    ///   AND "RetainedDeductionAmount" IS DISTINCT FROM "PenaltyAmount";
    /// </code></para>
    /// </summary>
    public partial class Adr044_M_T2c_AddRetainedDeductionAmountToLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "RetainedDeductionAmount",
                table: "BookingCancellationLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // ───────────────────────────────────────────────────────────────────────────────────────────
            // BACKFILL — lineas confirmadas ANTES de esta tanda: RetainedDeductionAmount = PenaltyAmount.
            // PenaltyStatus = 1 (Confirmed). Exacto para el historico (ver el XML-doc de la clase).
            // ───────────────────────────────────────────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                UPDATE "BookingCancellationLines"
                SET "RetainedDeductionAmount" = "PenaltyAmount"
                WHERE "PenaltyStatus" = 1
                  AND "PenaltyAmount" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetainedDeductionAmount",
                table: "BookingCancellationLines");
        }
    }
}
