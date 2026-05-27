-- ============================================================================
-- FC1.3 Fase 2 — Pre-validacion del backfill de FiscalLiquidation (F2.1.0)
-- ============================================================================
--
-- Que es este archivo
-- -------------------
-- Antes de aplicar la migracion Fase2_M1 (que crea las 10 columnas
-- FiscalLiquidation_* y backfillea los BCs Fase 1), hay que verificar que TODOS
-- los ApprovalRequests tipo 11 (PartialCreditNoteApproval) tengan un Metadata
-- JSON valido con las claves criticas. Si alguna fila esta rota, la migracion
-- ABORTA (su paso 5.A repite este mismo chequeo y tira RAISE EXCEPTION).
--
-- Este script es STANDALONE: NO es una migracion EF y NO se ejecuta como parte
-- del deploy automatico. Se corre a mano contra un dump de la base.
--
-- Como usarlo
-- -----------
-- 1. Conectate a un dump/replica de STAGING (psql, pgAdmin, DBeaver, etc.).
-- 2. Ejecuta este archivo entero.
-- 3. Mira el resultado:
--      - 0 filas devueltas  => OK, se puede avanzar a aplicar Fase2_M1 en staging.
--      - >0 filas devueltas => REVISAR CASO A CASO antes de avanzar (ver abajo).
-- 4. Repetir TODO el proceso contra un dump de PRODUCCION.
-- 5. Documentar el resultado (fecha + count + IDs) en
--    docs/operations/fase2-m1-prevalidation.md y pedir signoff de Gaston.
--
-- Que chequea (5 cosas)
-- ---------------------
--   1. Metadata vacio o null.
--   2. Metadata que no es un objeto JSON valido (un array, un string suelto,
--      etc.) — eso haria fallar el cast a ::jsonb del backfill.
--   3..5. Claves criticas faltantes: originalInvoiceAmount, fiscalAmountToCredit,
--      currency.
--
-- Las demas claves (cancellationAmount, operatorPenaltyAmount, etc.) NO son
-- criticas: la migracion las puede dejar NULL sin romper el CHECK de suma
-- (las columnas numericas son nullable).
--
-- I2 fix: computedAt NO es clave critica. El backfill (paso 5.B de la migracion)
-- toma el ComputedAt de la columna "BookingCancellations"."LiquidationComputedAt",
-- NO del JSON. Un Metadata sin computedAt NO rompe la migracion. Esta
-- prevalidacion debe chequear EXACTAMENTE las mismas 3 claves que el pre-check
-- de la migracion (paso 5.A): originalInvoiceAmount, fiscalAmountToCredit,
-- currency. Si chequeara computedAt seria mas estricta que la migracion =>
-- falso positivo (frenaria un deploy que la migracion habria corrido OK).
--
-- Resultado esperado en dumps recientes
-- -------------------------------------
-- Cero filas. El serializer de Fase 1 (SubmitForReviewAsync) escribe
-- schemaVersion=1 con todas las claves. Si aparece algo, suele ser un BC
-- editado a mano o un dato de test que se escapo a prod — investigar.
--
-- Si aparecen filas problematicas (>0)
-- ------------------------------------
-- NO aplicar la migracion todavia. Por cada fila listada:
--   - Abrir el ApprovalRequest por su Id.
--   - Decidir si se corrige el Metadata (rellenar las claves faltantes desde el
--     AuditLog del submit) o si la fila se descarta (BC abortado/rechazado que
--     dejo un approval huerfano).
--   - Recien con count = 0 se avanza.
-- ----------------------------------------------------------------------------

WITH ar11 AS (
    -- Aliaseamos ar."Id" (columna citada, case "Id") a `id` (sin comillas, que
    -- Postgres pliega a minuscula). Asi la columna de salida del CTE se llama `id`
    -- y los CTEs siguientes que la referencian sin comillas (`SELECT id`, el
    -- `ORDER BY id` final) resuelven OK. Sin el alias, esos usos pedian una columna
    -- `id` que no existia (la salida se llamaba `Id`) => "column id does not exist"
    -- y el script abortaba en la 1ra corrida contra el dump (gate RH-001 pre-deploy).
    -- "Metadata" se deja citado a proposito: lo referenciamos citado mas abajo.
    SELECT ar."Id" AS id, ar."Metadata"
    FROM "ApprovalRequests" ar
    WHERE ar."RequestType" = 11
),
problematic AS (
    SELECT
        id,
        CASE
            WHEN "Metadata" IS NULL OR length(trim("Metadata")) = 0 THEN 'METADATA_VACIO'
            WHEN jsonb_typeof("Metadata"::jsonb) IS DISTINCT FROM 'object' THEN 'METADATA_NO_OBJETO'
            WHEN NOT ("Metadata"::jsonb ? 'originalInvoiceAmount') THEN 'FALTA_originalInvoiceAmount'
            WHEN NOT ("Metadata"::jsonb ? 'fiscalAmountToCredit') THEN 'FALTA_fiscalAmountToCredit'
            WHEN NOT ("Metadata"::jsonb ? 'currency') THEN 'FALTA_currency'
            -- I2 fix: computedAt NO se chequea: el backfill lo toma de la columna
            -- LiquidationComputedAt, no del JSON. Chequearlo daria falsos positivos.
            ELSE NULL
        END AS razon
    FROM ar11
)
SELECT id, razon
FROM problematic
WHERE razon IS NOT NULL
ORDER BY id;
