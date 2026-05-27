using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TravelApi.Infrastructure.Persistence.Migrations.App
{
    /// <inheritdoc />
    /// <summary>
    /// FC1.3 Fase 2 (plan tactico Fase 2 §FC1.3.F2.1, 2026-05-26): persiste el
    /// detalle COMPLETO de la liquidacion fiscal de la NC parcial en 10 columnas
    /// dedicadas (owned VO <c>FiscalLiquidation</c> con prefijo
    /// <c>FiscalLiquidation_</c>), mas dos CHECK constraints y el backfill de los
    /// BCs Fase 1 que ya tienen el detalle en <c>ApprovalRequest.Metadata</c> JSON.
    ///
    /// <para><b>Por que existe (RH-002)</b>: en Fase 1 los montos de la liquidacion
    /// vivian solo como JSON en el Metadata del approval y solo cuando habia manual
    /// review. Fase 2 los promueve a columnas para reporting/reconciliacion via SQL
    /// y para poder validar la suma con un CHECK constraint.</para>
    ///
    /// <para><b>Pre-requisito CRITICO (RH-001)</b>: ANTES de aplicar esta migracion
    /// hay que correr <c>tools/sql/fase2-m1-prevalidation-metadata.sql</c> contra
    /// dump staging Y prod y verificar count == 0. El paso 5.A de abajo repite ese
    /// pre-check dentro de la migracion: si aparece un <c>ApprovalRequest</c> tipo 11
    /// con Metadata invalido o claves criticas faltantes, la migracion ABORTA con
    /// <c>RAISE EXCEPTION</c> en vez de dejar columnas NULL silenciosamente.</para>
    ///
    /// <para><b>Nullability (desviacion documentada del plan §F2.1 paso 4)</b>: el
    /// plan pedia <c>FiscalLiquidation_Currency</c> NOT NULL con default 'ARS'. Pero
    /// el VO <c>FiscalLiquidation</c> es una owned navigation OPCIONAL (nullable):
    /// cuando un BC no tiene liquidacion (la mayoria de los BCs Fase 1 rechazados o
    /// del path FC1.2 puro), TODAS sus columnas <c>FiscalLiquidation_*</c> quedan
    /// NULL — incluido Currency. Forzar Currency NOT NULL romperia esos BCs. Por eso
    /// Currency queda <b>nullable con default 'ARS'</b>: respeta la intencion del plan
    /// (default disponible para que el backfill nunca falle por currency faltante,
    /// reforzado por el <c>COALESCE(..., 'ARS')</c> del paso 5.B) sin violar la
    /// nullability que EF exige para owned opcionales. El <c>defaultValue: "ARS"</c>
    /// lo genero EF a partir del <c>HasDefaultValue("ARS")</c> de AppDbContext.</para>
    ///
    /// <para><b>Rollback</b>: <c>Down()</c> dropea los CHECK constraints y las 10
    /// columnas. Es seguro <b>solo mientras el doble-write siga escribiendo el
    /// Metadata JSON</b> (invariante RH-002, plan §T6.4): el reverse vuelve la fuente
    /// de verdad al JSON. Si una version intermedia dejara de escribir el JSON, el
    /// reverse perderia data sin forma de recuperarla.</para>
    /// </summary>
    public partial class Fase2_M1 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ============================================================
            // 1) 10 columnas del owned VO FiscalLiquidation. Todas nullable
            //    (owned navigation opcional). Currency con default 'ARS'.
            //    (Bloque AddColumn generado por EF a partir del modelo.)
            // ============================================================

            migrationBuilder.AddColumn<decimal>(
                name: "FiscalLiquidation_AmountToRefundCustomer",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiscalLiquidation_CancellationAmount",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FiscalLiquidation_ComputedAt",
                table: "BookingCancellations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalLiquidation_ComputedByUserId",
                table: "BookingCancellations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalLiquidation_ComputedByUserName",
                table: "BookingCancellations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiscalLiquidation_Currency",
                table: "BookingCancellations",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                defaultValue: "ARS");

            migrationBuilder.AddColumn<decimal>(
                name: "FiscalLiquidation_FinalNetInvoiced",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiscalLiquidation_FiscalAmountToCredit",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiscalLiquidation_NonRefundableItemsAmount",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiscalLiquidation_OperatorPenaltyAmount",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FiscalLiquidation_OriginalInvoiceAmount",
                table: "BookingCancellations",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            // ============================================================
            // 2) CHECK constraint de suma (INV-FC1.3-005 promovido a SQL).
            //    Sintaxis PostgreSQL: identificadores con comillas dobles.
            //    El interceptor BusinessInvariantInterceptor mapea SqlState='23514'
            //    (CHECK violation) a BusinessInvariantViolationException -> HTTP 409.
            //
            //    Tolerancia 0.01 (un centavo) para absorber redondeos. La suma de
            //    componentes (fiscal + no reintegrables + penalidad operador) debe
            //    igualar el total de la factura original.
            //
            //    I1 fix: el guard es FiscalAmountToCredit IS NULL => si el VO no existe
            //    (todas las columnas NULL), el CHECK no valida. PERO si el VO existe
            //    (FiscalAmountToCredit no-null) y ALGUN OTRO componente fuera NULL, la
            //    suma daria NULL y "NULL <= 0.01" => UNKNOWN => el CHECK pasaria sin
            //    validar. Por eso envolvemos cada componente con COALESCE(..., 0): un
            //    NULL parcial cuenta como 0 y el CHECK valida de verdad (un VO con
            //    FiscalAmountToCredit seteado pero Penalty NULL ahora dispara el 23514).
            // ============================================================
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_sum;
                ALTER TABLE "BookingCancellations"
                  ADD CONSTRAINT chk_BookingCancellations_fiscalliquidation_sum
                  CHECK (
                    "FiscalLiquidation_FiscalAmountToCredit" IS NULL
                    OR ABS(
                         COALESCE("FiscalLiquidation_FiscalAmountToCredit", 0)
                         + COALESCE("FiscalLiquidation_NonRefundableItemsAmount", 0)
                         + COALESCE("FiscalLiquidation_OperatorPenaltyAmount", 0)
                         - COALESCE("FiscalLiquidation_OriginalInvoiceAmount", 0)
                       ) <= 0.01
                  );
                """);

            // ============================================================
            // 3) CHECK constraint de consistencia de timestamp.
            //    Si FiscalLiquidation_ComputedAt no es null, debe coincidir EXACTAMENTE
            //    con LiquidationComputedAt (columna summary de Fase 1). Sin tolerancia.
            //    RH3-003 (round 4): el backfill paso 5.B lee bc."LiquidationComputedAt"
            //    directamente (no del JSON), eliminando divergencia por serializacion.
            // ============================================================
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_consistency;
                ALTER TABLE "BookingCancellations"
                  ADD CONSTRAINT chk_BookingCancellations_fiscalliquidation_consistency
                  CHECK (
                    "FiscalLiquidation_ComputedAt" IS NULL
                    OR "LiquidationComputedAt" = "FiscalLiquidation_ComputedAt"
                  );
                """);

            // ============================================================
            // 4) Backfill en 3 pasos atomicos (cierra RH-001).
            // ============================================================

            // Paso 5.A — pre-check defensivo. Si hay ApprovalRequests tipo 11 con
            // Metadata invalido o claves criticas faltantes, ABORTA toda la migracion
            // con mensaje claro en vez de dejar columnas NULL silenciosamente.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                  v_problematic_count int;
                BEGIN
                  SELECT COUNT(*) INTO v_problematic_count
                  FROM "ApprovalRequests" ar
                  WHERE ar."RequestType" = 11
                    AND (
                      ar."Metadata" IS NULL
                      OR length(trim(ar."Metadata")) = 0
                      OR jsonb_typeof(ar."Metadata"::jsonb) IS DISTINCT FROM 'object'
                      OR NOT (ar."Metadata"::jsonb ? 'originalInvoiceAmount')
                      OR NOT (ar."Metadata"::jsonb ? 'fiscalAmountToCredit')
                      OR NOT (ar."Metadata"::jsonb ? 'currency')
                    );

                  IF v_problematic_count > 0 THEN
                    RAISE EXCEPTION 'FC1.3.F2.1 backfill ABORTED: % ApprovalRequests tipo 11 con Metadata invalido o claves criticas faltantes. Correr tools/sql/fase2-m1-prevalidation-metadata.sql para identificar filas y limpiarlas ANTES de re-aplicar la migracion.', v_problematic_count;
                  END IF;

                  RAISE NOTICE 'FC1.3.F2.1 paso 5.A OK: 0 filas problematicas en ApprovalRequests tipo 11.';
                END $$;
                """);

            // Paso 5.B — UPDATE idempotente acotado a filas seguras.
            // El subselect repite los mismos filtros del pre-check para no leer filas
            // problematicas. El WHERE final exige columna fiscal todavia NULL para que
            // re-correr la migracion no pise valores ya backfilleados (idempotente).
            //
            // RH3-003: FiscalLiquidation_ComputedAt = bc."LiquidationComputedAt" (columna
            // summary), NO el computedAt del JSON, para que el CHECK de consistencia
            // (igualdad exacta) no rebote por diferencia de serializacion de fechas.
            //
            // B-FISC-1 (decision Gaston opcion A): EXCLUIMOS los BCs CommissionOnly del
            // backfill. En esos casos Fase 1 escribio en el JSON fiscalAmountToCredit=0
            // con originalInvoiceAmount>0 (terna 0+0+penalty). Poblar las columnas con
            // esos numeros haria que el CHECK de suma aborte TODA la migracion (23514).
            // Los reconocemos por computedCase (Case5_CommissionOnlyPartial /
            // Case6_CommissionOnlyFull, que es liquidation.Case.ToString() del calculator
            // en STEP 0). Sus columnas FiscalLiquidation_* quedan NULL — coherente con
            // ConfirmAsync, que tampoco persiste el VO en CommissionOnly. El detalle vive
            // igual en el JSON Metadata (fuente de verdad para el reverse).
            migrationBuilder.Sql("""
                UPDATE "BookingCancellations" bc
                SET
                  "FiscalLiquidation_OriginalInvoiceAmount" = (m.meta->>'originalInvoiceAmount')::numeric,
                  "FiscalLiquidation_CancellationAmount"    = (m.meta->>'cancellationAmount')::numeric,
                  "FiscalLiquidation_OperatorPenaltyAmount" = (m.meta->>'operatorPenaltyAmount')::numeric,
                  "FiscalLiquidation_NonRefundableItemsAmount" = (m.meta->>'nonRefundableItemsAmount')::numeric,
                  "FiscalLiquidation_FiscalAmountToCredit"  = (m.meta->>'fiscalAmountToCredit')::numeric,
                  "FiscalLiquidation_AmountToRefundCustomer"= (m.meta->>'amountToRefundCustomer')::numeric,
                  "FiscalLiquidation_FinalNetInvoiced"      = (m.meta->>'finalNetInvoiced')::numeric,
                  -- I3 fix: NULLIF(trim(...), '') colapsa currency vacio o solo-espacios
                  -- a NULL para que el COALESCE caiga al default 'ARS'. Un COALESCE
                  -- pelado no salva el string vacio ("" no es NULL).
                  "FiscalLiquidation_Currency"              = COALESCE(NULLIF(trim(m.meta->>'currency'), ''), 'ARS'),
                  "FiscalLiquidation_ComputedAt"            = bc."LiquidationComputedAt",
                  "FiscalLiquidation_ComputedByUserId"      = m.meta->>'computedByUserId',
                  "FiscalLiquidation_ComputedByUserName"    = m.meta->>'computedByUserName'
                FROM (
                  SELECT ar."Id" as id, ar."Metadata"::jsonb as meta
                  FROM "ApprovalRequests" ar
                  WHERE ar."RequestType" = 11
                    AND ar."Metadata" IS NOT NULL
                    AND jsonb_typeof(ar."Metadata"::jsonb) = 'object'
                    AND ar."Metadata"::jsonb ? 'originalInvoiceAmount'
                    AND ar."Metadata"::jsonb ? 'fiscalAmountToCredit'
                    AND ar."Metadata"::jsonb ? 'currency'
                    -- B-FISC-1: excluir CommissionOnly (computedCase Case5/Case6). Su terna
                    -- 0+0+penalty violaria el CHECK de suma. IS DISTINCT FROM trata el NULL
                    -- como "distinto" (un JSON sin computedAt no se excluye por error).
                    AND (ar."Metadata"::jsonb->>'computedCase') IS DISTINCT FROM 'Case5_CommissionOnlyPartial'
                    AND (ar."Metadata"::jsonb->>'computedCase') IS DISTINCT FROM 'Case6_CommissionOnlyFull'
                ) m
                WHERE bc."PartialCreditNoteApprovalRequestId" = m.id
                  AND bc."FiscalLiquidation_FiscalAmountToCredit" IS NULL;
                """);

            // Paso 5.C — count + RAISE NOTICE final (observabilidad del backfill).
            // backfilled = filas con columna fiscal ya poblada.
            // orphan_skipped = BCs con FK al approval pero sin backfill. Los BCs
            // rechazados tienen FK nulled (Fase 1 OnRejectedAsync) y no entran aca,
            // asi que en condiciones normales esto deberia dar 0. Lo contamos igual
            // para detectar inconsistencias post-backfill.
            migrationBuilder.Sql("""
                DO $$
                DECLARE
                  v_backfilled int;
                  v_orphan int;
                BEGIN
                  SELECT COUNT(*) INTO v_backfilled
                  FROM "BookingCancellations"
                  WHERE "FiscalLiquidation_FiscalAmountToCredit" IS NOT NULL;

                  SELECT COUNT(*) INTO v_orphan
                  FROM "BookingCancellations" bc
                  WHERE bc."PartialCreditNoteApprovalRequestId" IS NOT NULL
                    AND bc."FiscalLiquidation_FiscalAmountToCredit" IS NULL;

                  RAISE NOTICE 'FC1.3.F2.1 backfill done. backfilled=% orphan_skipped=%', v_backfilled, v_orphan;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop CHECK constraints ANTES de dropear columnas para que Postgres no
            // se queje por dependencias.
            migrationBuilder.Sql("""
                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_sum;

                ALTER TABLE "BookingCancellations"
                  DROP CONSTRAINT IF EXISTS chk_BookingCancellations_fiscalliquidation_consistency;
                """);

            // Drop de las 10 columnas. El reverse es seguro SOLO mientras el
            // doble-write siga escribiendo el Metadata JSON (RH-002, plan §T6.4):
            // la fuente de verdad vuelve al JSON.
            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_AmountToRefundCustomer",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_CancellationAmount",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_ComputedAt",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_ComputedByUserId",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_ComputedByUserName",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_Currency",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_FinalNetInvoiced",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_FiscalAmountToCredit",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_NonRefundableItemsAmount",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_OperatorPenaltyAmount",
                table: "BookingCancellations");

            migrationBuilder.DropColumn(
                name: "FiscalLiquidation_OriginalInvoiceAmount",
                table: "BookingCancellations");
        }
    }
}
