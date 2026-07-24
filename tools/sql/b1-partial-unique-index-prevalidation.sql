-- =============================================================================
-- B1 (2026-06-03) - Prevalidacion de la migracion
--   20260603032534_B1_AddBookingCancellationPartialUniqueIndexes
--
-- QUE HACE LA MIGRACION: convierte los indices UNIQUE TOTALES de
--   "BookingCancellations" sobre ("ReservaId") y ("OriginatingInvoiceId") en
--   indices UNIQUE PARCIALES con filtro  "Status" <> 6  (6 = Aborted).
--
-- POR QUE PREVALIDAR: relajar un UNIQUE total a parcial nunca falla por datos,
--   PERO la creacion del nuevo indice parcial SI falla si ya existen DOS filas
--   "vivas" (Status <> 6) con el mismo ReservaId u OriginatingInvoiceId. En una
--   base sana esto no deberia pasar (el UNIQUE total previo lo impedia), pero lo
--   verificamos por las dudas antes de tocar prod.
--
-- COMO USAR: correr este script en la base de PROD/STAGING ANTES de aplicar la
--   migracion. Las DOS consultas de control deben devolver 0 filas. Si alguna
--   devuelve filas, hay duplicados que hay que sanear (abortar los muertos)
--   ANTES de migrar.
-- =============================================================================

-- Control 1: duplicados vivos por ReservaId.
-- Esperado: 0 filas.
SELECT "ReservaId", count(*) AS vivos
FROM "BookingCancellations"
WHERE "Status" <> 6
GROUP BY "ReservaId"
HAVING count(*) > 1;

-- Control 2: duplicados vivos por OriginatingInvoiceId.
-- Esperado: 0 filas.
--
-- NOTA (obra "anular sin factura", 2026-07-23): OriginatingInvoiceId pasó a ser NULLABLE (una cancelación
-- puede quedar SIN factura de venta que la ancle, ver BookingCancellation.OriginatingInvoiceId). El índice
-- único real (IX_BookingCancellations_OriginatingInvoiceId) ya excluye los NULL con su propio filtro parcial
-- ("OriginatingInvoiceId" IS NOT NULL AND "Status" NOT IN (4, 6)) — en SQL, NULL <> NULL, así que Postgres
-- NUNCA los trata como duplicados entre sí. Pero GROUP BY sí agrupa todos los NULL juntos como un solo grupo:
-- sin este WHERE, dos o más BC sin factura (perfectamente válidos) dispararían un FALSO POSITIVO acá. El
-- WHERE "OriginatingInvoiceId" IS NOT NULL alinea este control con lo que el índice real permite.
SELECT "OriginatingInvoiceId", count(*) AS vivos
FROM "BookingCancellations"
WHERE "Status" <> 6
  AND "OriginatingInvoiceId" IS NOT NULL
GROUP BY "OriginatingInvoiceId"
HAVING count(*) > 1;

-- -----------------------------------------------------------------------------
-- Si algun control devolvio filas: inspeccionar los BCs en conflicto antes de
-- decidir cual abortar. NO ejecutar ningun UPDATE a ciegas: revisar el estado
-- fiscal (CreditNoteInvoiceId) de cada fila. Solo es seguro abortar un BC que NO
-- dejo nota de credito viva (misma regla que DraftAsync).
--
--   SELECT "Id", "PublicId", "ReservaId", "OriginatingInvoiceId", "Status",
--          "CreditNoteInvoiceId", "DraftedAt"
--   FROM "BookingCancellations"
--   WHERE "ReservaId" IN ( <ids del control 1> )
--      OR "OriginatingInvoiceId" IN ( <ids del control 2> )
--   ORDER BY "ReservaId", "Id";
-- -----------------------------------------------------------------------------
