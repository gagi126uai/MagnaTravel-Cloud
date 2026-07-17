using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <summary>
    /// (2026-07-17, fix de raiz del bug "edito la condicion fiscal del cliente y no hace nada")
    /// REPARACION DE DATOS LEGACY — sincroniza <c>Customers."TaxCondition"</c> (el texto que lee
    /// <c>BookingCancellationService.ResolveServerSideTaxIdentity</c>) con
    /// <c>Customers."TaxConditionId"</c> (el codigo AFIP que carga el dropdown de la ficha) para los
    /// clientes que quedaron desalineados.
    ///
    /// <para><b>Que problema arregla</b>: hasta hoy, <c>CustomerService.UpdateCustomerAsync</c> actualizaba
    /// el CODIGO de la condicion fiscal pero preservaba el TEXTO viejo cuando el request no lo mandaba —
    /// y el formulario de la ficha del cliente (<c>CustomerFormModal.jsx</c>) NUNCA manda el texto, solo el
    /// codigo. Resultado: un vendedor editaba la condicion fiscal en la ficha, la pantalla mostraba el
    /// cambio guardado (el codigo), pero la devolucion seguia bloqueada pidiendo "completa la condicion
    /// fiscal" porque el motor de cancelaciones solo lee el texto. El codigo de la aplicacion ya se arreglo
    /// (ver <c>CustomerTaxConditionCatalog.ResolveIncoming</c>); esta migracion es el backfill UNA VEZ de
    /// los clientes que ya quedaron con el texto viejo antes del fix.</para>
    ///
    /// <para><b>Que hace</b>: para cada cliente con <c>TaxConditionId</c> en el catalogo conocido (1 =
    /// Responsable Inscripto, 4 = Exento, 5 = Consumidor Final, 6 = Monotributo — mismos codigos que
    /// <c>CustomerTaxConditionCatalog</c>, ADR-024 CondicionIVAReceptorId), sobreescribe <c>TaxCondition</c>
    /// con el texto que le corresponde a ESE codigo. Un cliente con <c>TaxConditionId</c> nulo, o con un
    /// codigo fuera de esos 4, NO se toca (no hay de donde derivar el texto con certeza).</para>
    ///
    /// <para><b>Por que es SEGURA</b>:
    /// <list type="bullet">
    /// <item>NO cambia el esquema (0 columnas/indices).</item>
    /// <item>Backup previo (paso 0) en <c>_repair_20260717_customer_taxcondition_backup</c>, idempotente
    /// via <c>CREATE TABLE IF NOT EXISTS ... AS</c> (una segunda corrida no vuelve a pisar la foto
    /// original).</item>
    /// <item>El <c>UPDATE</c> es idempotente: el <c>WHERE</c> con <c>IS DISTINCT FROM</c> excluye las filas
    /// que ya coinciden con el texto derivado. Correrla dos veces no vuelve a tocar nada.</item>
    /// <item>Nunca inventa una condicion fiscal nueva: solo alinea el texto al codigo que YA estaba
    /// cargado. No cambia ningun <c>TaxConditionId</c>.</item>
    /// </list></para>
    ///
    /// <para><b>Dimensionamiento validado contra produccion ANTES de este deploy</b> (SELECT de solo
    /// lectura, sin tocar datos):
    /// <code>
    /// SELECT COUNT(*) FROM "Customers"
    /// WHERE "TaxConditionId" IN (1, 4, 5, 6)
    ///   AND "TaxCondition" IS DISTINCT FROM (
    ///     CASE "TaxConditionId"
    ///       WHEN 1 THEN 'Responsable Inscripto'
    ///       WHEN 4 THEN 'Exento'
    ///       WHEN 5 THEN 'Consumidor Final'
    ///       WHEN 6 THEN 'Monotributo'
    ///     END
    ///   );
    /// </code>
    /// </para>
    /// </summary>
    public partial class RepairCustomerTaxConditionTextFromId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // PASO 0 — backup del estado previo (solo las filas que esta migracion va a tocar).
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""_repair_20260717_customer_taxcondition_backup"" AS
                SELECT ""Id"", ""TaxConditionId"", ""TaxCondition""
                FROM ""Customers""
                WHERE ""TaxConditionId"" IN (1, 4, 5, 6)
                  AND ""TaxCondition"" IS DISTINCT FROM (
                    CASE ""TaxConditionId""
                      WHEN 1 THEN 'Responsable Inscripto'
                      WHEN 4 THEN 'Exento'
                      WHEN 5 THEN 'Consumidor Final'
                      WHEN 6 THEN 'Monotributo'
                    END
                  );
            ");

            // PASO 1 — sincroniza el texto con el codigo. Mismo mapeo que
            // CustomerTaxConditionCatalog.TryGetLabel (fuente unica en C#).
            migrationBuilder.Sql(@"
                UPDATE ""Customers""
                SET ""TaxCondition"" = CASE ""TaxConditionId""
                      WHEN 1 THEN 'Responsable Inscripto'
                      WHEN 4 THEN 'Exento'
                      WHEN 5 THEN 'Consumidor Final'
                      WHEN 6 THEN 'Monotributo'
                    END
                WHERE ""TaxConditionId"" IN (1, 4, 5, 6)
                  AND ""TaxCondition"" IS DISTINCT FROM (
                    CASE ""TaxConditionId""
                      WHEN 1 THEN 'Responsable Inscripto'
                      WHEN 4 THEN 'Exento'
                      WHEN 5 THEN 'Consumidor Final'
                      WHEN 6 THEN 'Monotributo'
                    END
                  );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op deliberado: no hay esquema que revertir, y "deshacer" volveria a dejar el texto
            // desalineado del codigo a proposito (el bug original). Si hiciera falta auditar el valor
            // previo, esta en "_repair_20260717_customer_taxcondition_backup" (no se dropea aca).
        }
    }
}
