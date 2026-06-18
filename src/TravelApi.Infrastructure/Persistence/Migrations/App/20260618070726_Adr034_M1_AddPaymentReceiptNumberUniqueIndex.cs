using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// Bug concurrencia/numeracion (2026-06-18): el numero de recibo interno se generaba con
    /// CountAsync()+1, calculo NO atomico. Bajo concurrencia (dos cobros emitiendo recibo a la vez,
    /// o un doble-click) dos emisiones leen el mismo Count y producen el MISMO numero correlativo
    /// -> recibos con numero duplicado. La numeracion correlativa es justo lo que el resto del sistema
    /// preserva (por eso se prohibe reemitir recibos Voided).
    ///
    /// Este indice UNIQUE en "PaymentReceipts"."ReceiptNumber" es la garantia REAL de no-duplicados:
    /// si dos inserts compiten por el mismo numero, Postgres rechaza al segundo (23505) y
    /// PaymentService.CreateReceiptWithCorrelativeNumberAsync lo atrapa, recomputa el numero y reintenta.
    ///
    /// Por que la COLUMNA SOLA y no un compuesto (anio, secuencia): el formato es "RCP-{anio}-{n:6}" donde
    /// n es el conteo GLOBAL de recibos + 1 (NO se reinicia por anio). El anio del prefijo es solo etiqueta;
    /// n nunca se repite aunque cambie el anio. Por eso la columna completa ya es la clave de unicidad correcta.
    ///
    /// Por que NO es un indice filtrado: los recibos Voided se PRESERVAN con su numero (la entidad no tiene
    /// soft-delete; la fila Voided se conserva para mantener la correlativa fiscal). El numero debe seguir
    /// siendo unico aunque el recibo este anulado, asi que el indice cubre TODAS las filas.
    ///
    /// RIESGO DE DEPLOY (el dueno debe chequear ANTES de aplicar): si la base ya contiene numeros de recibo
    /// DUPLICADOS (generados por este mismo bug antes del fix), la creacion del indice UNIQUE FALLA con
    /// "could not create unique index ... duplicate key". SQL de chequeo previo (debe devolver 0 filas):
    ///
    ///   SELECT "ReceiptNumber", COUNT(*)
    ///   FROM "PaymentReceipts"
    ///   GROUP BY "ReceiptNumber"
    ///   HAVING COUNT(*) > 1;
    ///
    /// Si devuelve filas, hay que renumerar/anular los duplicados manualmente (decision operativa+fiscal)
    /// ANTES de correr esta migracion. No se renumera automaticamente aca: tocar numeros de recibo ya
    /// emitidos es una accion fiscal que exige intervencion humana, no un side-effect de migracion.
    ///
    /// IF NOT EXISTS / IF EXISTS: re-runnable y rollback seguro, consistente con las demas migraciones de indices.
    /// </summary>
    public partial class Adr034_M1_AddPaymentReceiptNumberUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentReceipts_ReceiptNumber"
                    ON "PaymentReceipts" ("ReceiptNumber");
            """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_PaymentReceipts_ReceiptNumber";
            """);
        }
    }
}
