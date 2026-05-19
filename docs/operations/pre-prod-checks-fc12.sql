-- ============================================================================
-- Pre-prod checks FC1.2 (modulo cancelacion de reservas)
-- ============================================================================
--
-- Que es este archivo
-- -------------------
-- Antes de prender el feature flag `EnableNewCancellationFlow=true` en la
-- agencia de produccion, hay que verificar tres cosas. Este SQL las chequea
-- todas en una sola corrida.
--
-- Como usarlo
-- -----------
-- 1. Conectate a la BD productiva (psql, pgAdmin, DBeaver, etc.).
-- 2. Ejecuta este archivo entero.
-- 3. Revisa los resultados de cada CHECK.
-- 4. Si algun CHECK devuelve filas con problemas, mira la seccion "Como
--    arreglar" abajo de cada uno.
-- 5. NO actives el feature flag hasta que los 3 checks devuelvan OK.
--
-- ============================================================================
-- CHECK 1 — ResponsibleUserId nulos en TravelFiles activos (BR-V2-02)
-- ============================================================================
--
-- Que verifica:
-- Que ninguna reserva activa quede sin vendedor asignado. El modulo de
-- cancelacion usa este campo para validar ownership (que un vendedor solo
-- pueda cancelar sus propias reservas).
--
-- Resultado esperado: count = 0.
--
-- Si count > 0: ver "Como arreglar" abajo.
-- ----------------------------------------------------------------------------

SELECT
    COUNT(*) AS travelfiles_sin_responsable
FROM "TravelFiles"
WHERE "Status" NOT IN ('Closed', 'Cancelled', 'Archived')
  AND "ResponsibleUserId" IS NULL;

-- Si encontraste >0, lista los TravelFiles afectados para revisarlos:
--
-- SELECT "Id", "Reference", "Status", "CreatedAt", "PayerId"
-- FROM "TravelFiles"
-- WHERE "Status" NOT IN ('Closed', 'Cancelled', 'Archived')
--   AND "ResponsibleUserId" IS NULL
-- ORDER BY "CreatedAt" DESC
-- LIMIT 50;
--
-- Como arreglar (Caso B.1 — backfill):
-- Asignar un responsable real a cada TravelFile huerfano. Usar el comando
-- de B1.15: `users.set-responsible --travelfile <id> --user <userId>`.
-- O via UI admin si esta disponible.
--
-- Como arreglar (Caso B.2 — restriccion soft):
-- Si la lista es muy larga y no se puede backfillear ya, configurar en
-- ApprovalPolicies que solo Admin/Colaborador puedan operar TravelFiles
-- sin responsable. Esto evita que Vendedor toque cosas que no son suyas.

-- ============================================================================
-- CHECK 2 — TaxCondition no Unknown en Customers
-- ============================================================================
--
-- Que verifica:
-- Que ningun cliente tenga condicion fiscal en un valor que el normalizador
-- no reconoce. Si el normalizador devuelve `Unknown`, la matriz fiscal de
-- la cancelacion no puede decidir que tipo de NC emitir.
--
-- Resultado esperado: lista de valores distintos que YA EXISTEN en la BD.
-- Cruzar manualmente contra TaxConditionNormalizer (codigo) para ver si
-- todos esos valores tienen un mapeo conocido.
-- ----------------------------------------------------------------------------

SELECT DISTINCT "TaxCondition"
FROM "Customers"
WHERE "TaxCondition" IS NOT NULL
ORDER BY "TaxCondition";

-- ============================================================================
-- CHECK 3 — TaxCondition no Unknown en Suppliers
-- ============================================================================

SELECT DISTINCT "TaxCondition"
FROM "Suppliers"
WHERE "TaxCondition" IS NOT NULL
ORDER BY "TaxCondition";

-- ============================================================================
-- CHECK 4 — TaxCondition no Unknown en AgencySettings
-- ============================================================================

SELECT DISTINCT "TaxCondition"
FROM "AgencySettings"
WHERE "TaxCondition" IS NOT NULL
ORDER BY "TaxCondition";

-- ============================================================================
-- Como cruzar Check 2/3/4 contra el normalizador
-- ============================================================================
--
-- Los valores que SI reconoce el normalizador hoy (ver TaxConditionNormalizer.cs):
--
-- - "Monotributo"
-- - "Monotributista"
-- - "Responsable Inscripto" (variantes con/sin acento, con/sin guion)
-- - "IVA_RESP_INSCRIPTO"
-- - "Consumidor Final"
-- - "ConsumidorFinal"
-- - "Exento"
-- - "IVA_EXENTO"
-- - "No Categorizado" (mapea a Unknown — REVISAR cliente por cliente)
--
-- Si en algun CHECK encontras un valor que NO esta en esta lista, el
-- normalizador lo va a tratar como Unknown y la cancelacion va a rechazar
-- el flujo. Tenes 2 opciones:
--
-- A. Actualizar el dato en BD a un valor canonico:
--    UPDATE "Customers" SET "TaxCondition" = 'Monotributo' WHERE "TaxCondition" = 'monotributo';
--    (ajustar segun el valor real encontrado).
--
-- B. Agregar el valor nuevo al TaxConditionNormalizer:
--    Si es un valor legitimo que se usa hoy en la agencia, actualizar el
--    helper en `src/TravelApi.Infrastructure/Helpers/TaxConditionNormalizer.cs`
--    para que lo reconozca. Hacer un PR separado.

-- ============================================================================
-- CHECK 5 — Feature flag `EnableNewCancellationFlow` esta apagado
-- ============================================================================
--
-- Que verifica:
-- Que el feature flag NO este prendido aun. Hasta que se complete OPS-FISCAL-001
-- (signoff contador), el flag tiene que seguir en false.
--
-- Resultado esperado: EnableNewCancellationFlow = false.
-- ----------------------------------------------------------------------------

SELECT
    "Key",
    "Value",
    "UpdatedAt"
FROM "OperationalFinanceSettings"
WHERE "Key" IN (
    'EnableNewCancellationFlow',
    'Ley25345ThresholdAmount',
    'PhysicalRefundAlertThreshold',
    'OperatorRefundTimeoutDays'
)
ORDER BY "Key";

-- ============================================================================
-- Resumen para deploy
-- ============================================================================
--
-- Antes de prender EnableNewCancellationFlow=true en prod, los 4 checks deben
-- estar OK:
--
-- [ ] CHECK 1: travelfiles_sin_responsable = 0 (o plan B.2 aplicado).
-- [ ] CHECK 2/3/4: todos los valores TaxCondition mapean en el normalizador.
-- [ ] CHECK 5: EnableNewCancellationFlow = false (precondicion: aun no prendido).
--
-- Ademas (verificacion humana, no SQL):
-- [ ] Signoff OPS-FISCAL-001 firmado por contador + arca-tax-expert
--     (referencia: docs/architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md).
-- [ ] Backup completo de la BD reciente (ultima hora).
-- [ ] Rollback plan documentado (volver a EnableNewCancellationFlow=false
--     si algo rompe).
--
-- Adjuntar resultados de este SQL al ticket de deploy para auditoria.
