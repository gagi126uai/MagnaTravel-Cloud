-- SUPERSEDIDO (2026-07-21): este saneo ahora viaja como migracion EF
-- (20260721054235_BackfillMonedaVaciaServiciosLegacy) y se aplica solo con el deploy.
-- Se conserva como referencia del conteo/validacion previa.

-- ============================================================================================
-- Backfill: normalizar moneda vacia/NULL a 'ARS' en las 6 tablas de servicios de reserva.
-- Fecha: 2026-07-21
-- Autor: investigacion "reserva sana no aparece en la cuenta corriente del proveedor" (Gaston).
--
-- CONTEXTO IMPORTANTE (leer antes de correr):
--   Este script NO es el arreglo del bug reportado por Gaston (F-2026-1051 no aparece en la
--   Cuenta corriente del proveedor 5 / no se puede elegir para pagarle). Se investigo esa
--   hipotesis con un test de integracion contra Postgres real
--   (src/TravelApi.Tests/Cancellation/Integration/SupplierAccountEmptyCurrencyIntegrationTests.cs)
--   reproduciendo EXACTAMENTE el escenario de PROD (una reserva vieja con hotel "Confirmado" sin
--   moneda + una reserva sana con hotel "Confirmado" en USD, mismo proveedor, mismo
--   InvoicingMode) y los TRES caminos que arma SupplierService (extracto, deuda por reserva,
--   resumen/overview) YA agrupan y muestran la reserva USD correctamente, SIN mezclarla con el
--   bloque de la moneda vacia. La causa del reporte de Gaston esta en OTRO lado (no verificado
--   todavia; ver el documento de la sesion).
--
--   Igual conviene correr este backfill como HIGIENE DE DATOS: hoy CUATRO servicios de hotel del
--   proveedor 5 (F-2026-1004, F-2026-1007, F-2026-1013, RES-00014) tienen Currency NULL o vacio.
--   El codigo de la cuenta del proveedor los normaliza a ARS EN MEMORIA en cada lectura
--   (Monedas.Normalizar, TravelApi.Domain/Entities/Monedas.cs), pero cualquier reporte o query SQL
--   cruda que NO pase por ese helper (exports, dashboards ad-hoc, futuras features) leeria una fila
--   con moneda "" o NULL y podria agruparla mal. Dejar el dato explicito en la base es mas seguro
--   que depender de que TODO el codigo futuro recuerde normalizar.
--
-- QUE HACE:
--   UPDATE de Currency NULL o '' (string vacio) -> 'ARS' en las 6 tablas de servicios. SOLO toca
--   filas con moneda vacia/NULL; nunca pisa una moneda ya cargada (ni ARS ni USD ni ninguna otra).
--
-- COMO CORRERLO (a mano, por el canal de siempre — el deploy NO ejecuta este archivo):
--   1) Correr el bloque de CONTEO (paso 0) primero y guardar el resultado.
--   2) Correr los 6 UPDATE dentro de una transaccion (BEGIN/COMMIT ya incluido abajo).
--   3) Volver a correr el bloque de CONTEO: debe dar 0 filas en las 6 tablas.
--   4) Si algo se ve raro, ROLLBACK antes del COMMIT (no hay backup automatico de este script).
--
-- QUE NO HACE:
--   No toca la logica de ALTA de servicios (como se guarda la moneda al crear un servicio nuevo).
--   Eso es un problema aparte: si sigue naciendo un servicio nuevo sin moneda, este backfill NO lo
--   va a volver a normalizar (es un UPDATE de una sola vez, no un trigger).
-- ============================================================================================


-- ---------------------------------------------------------------------------
-- PASO 0 (correr ANTES del UPDATE): conteo de filas afectadas por tabla.
-- Guardar este resultado para poder comparar "antes" contra "despues".
-- ---------------------------------------------------------------------------
SELECT 'HotelBookings' AS tabla, COUNT(*) AS filas_con_moneda_vacia
FROM "HotelBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'FlightSegments', COUNT(*)
FROM "FlightSegments"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'TransferBookings', COUNT(*)
FROM "TransferBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'PackageBookings', COUNT(*)
FROM "PackageBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'AssistanceBookings', COUNT(*)
FROM "AssistanceBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'Servicios', COUNT(*)
FROM "Servicios"
WHERE "Currency" IS NULL OR "Currency" = '';


-- ---------------------------------------------------------------------------
-- PASO 1: backfill dentro de una transaccion. Revisar el conteo del PASO 0
-- antes de hacer COMMIT; si algun numero sorprende, ROLLBACK y avisar.
-- ---------------------------------------------------------------------------
BEGIN;

UPDATE "HotelBookings"
SET "Currency" = 'ARS'
WHERE "Currency" IS NULL OR "Currency" = '';

UPDATE "FlightSegments"
SET "Currency" = 'ARS'
WHERE "Currency" IS NULL OR "Currency" = '';

UPDATE "TransferBookings"
SET "Currency" = 'ARS'
WHERE "Currency" IS NULL OR "Currency" = '';

UPDATE "PackageBookings"
SET "Currency" = 'ARS'
WHERE "Currency" IS NULL OR "Currency" = '';

UPDATE "AssistanceBookings"
SET "Currency" = 'ARS'
WHERE "Currency" IS NULL OR "Currency" = '';

UPDATE "Servicios"
SET "Currency" = 'ARS'
WHERE "Currency" IS NULL OR "Currency" = '';

-- Revisar el resultado de estos UPDATE (filas afectadas por cada sentencia, en la salida de la
-- consola de psql/pgAdmin) contra el conteo del PASO 0 antes de decidir COMMIT o ROLLBACK.

COMMIT;
-- Si algo salio mal, reemplazar el COMMIT de arriba por ROLLBACK y volver a correr el PASO 0.


-- ---------------------------------------------------------------------------
-- PASO 2 (correr DESPUES del COMMIT): debe dar 0 filas en las 6 tablas.
-- ---------------------------------------------------------------------------
SELECT 'HotelBookings' AS tabla, COUNT(*) AS filas_con_moneda_vacia_restantes
FROM "HotelBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'FlightSegments', COUNT(*)
FROM "FlightSegments"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'TransferBookings', COUNT(*)
FROM "TransferBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'PackageBookings', COUNT(*)
FROM "PackageBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'AssistanceBookings', COUNT(*)
FROM "AssistanceBookings"
WHERE "Currency" IS NULL OR "Currency" = ''
UNION ALL
SELECT 'Servicios', COUNT(*)
FROM "Servicios"
WHERE "Currency" IS NULL OR "Currency" = '';
