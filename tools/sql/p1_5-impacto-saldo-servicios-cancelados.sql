-- ============================================================================
-- P1.5 — IMPACTO: reservas cuyo SALDO se corrige al unificar el calculo
-- ============================================================================
-- SOLO LECTURA. No modifica nada. Corrise en el VPS (Postgres) para ver, en los
-- datos reales, que reservas tienen servicios CANCELADOS cuyo importe puede estar
-- inflando el saldo guardado.
--
-- NOTA DE NOMBRES (herencia del nombre viejo "TravelFile"): la tabla de reservas
-- se llama "TravelFiles" (PK "Id", numero "FileNumber"), y la FK a la reserva en
-- las tablas de servicio se llama "TravelFileId" (no "ReservaId").
--
-- Contexto: antes de P1.5, el saldo de la reserva se recalculaba de 3 formas
-- distintas segun la accion (tocar servicio / registrar pago / facturar). Dos de
-- esas formas sumaban PLANO, contando los servicios Cancelados que no deberian
-- contar. Tras P1.5 las 3 usan el mismo calculo (los Cancelados NO cuentan), asi
-- que el saldo de estas reservas se CORRIGE (baja) la proxima vez que se recalcule
-- (o con un backfill que recalcule todas de una).
--
-- "Cancelado" = estado que contiene 'cancel' (hotel/transfer/paquete/asistencia)
-- o codigo de vuelo IATA UN/UC/HX/NO.
--
-- ALCANCE: cubre los 5 tipos de servicio reales (hotel, vuelo, transfer, paquete,
-- asistencia). El "servicio generico" viejo (legacy, marginal y en via de retiro)
-- NO se incluye.
--
-- OJO (honestidad): que el saldo guardado HOY incluya o no el importe cancelado
-- depende de cual fue la ultima accion que lo recalculo. Por eso "VentaCorrectaAprox"
-- es una estimacion; el numero exacto queda fijo recien tras recalcular.
-- ============================================================================

WITH cancelados AS (
    SELECT "TravelFileId" AS file_id, "SalePrice" AS sale FROM "HotelBookings"      WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "TravelFileId", "SalePrice" FROM "TransferBookings"   WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "TravelFileId", "SalePrice" FROM "PackageBookings"    WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "TravelFileId", "SalePrice" FROM "AssistanceBookings" WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "TravelFileId", "SalePrice" FROM "FlightSegments"     WHERE UPPER("Status") IN ('UN', 'UC', 'HX', 'NO')
)
SELECT
    r."FileNumber"                      AS "NumeroReserva",
    r."TotalSale"                       AS "VentaGuardadaHoy",
    r."Balance"                         AS "SaldoGuardadoHoy",
    SUM(c.sale)                         AS "ImporteCanceladoQuePuedeEstarContando",
    r."TotalSale" - SUM(c.sale)         AS "VentaCorrectaAprox"
FROM cancelados c
JOIN "TravelFiles" r ON r."Id" = c.file_id
GROUP BY r."Id", r."FileNumber", r."TotalSale", r."Balance"
HAVING SUM(c.sale) > 0
ORDER BY SUM(c.sale) DESC;

-- Resumen rapido (descomentar para correr aparte):
-- WITH cancelados AS (
--     SELECT "TravelFileId" AS file_id, "SalePrice" AS sale FROM "HotelBookings"      WHERE LOWER("Status") LIKE '%cancel%'
--     UNION ALL SELECT "TravelFileId", "SalePrice" FROM "TransferBookings"   WHERE LOWER("Status") LIKE '%cancel%'
--     UNION ALL SELECT "TravelFileId", "SalePrice" FROM "PackageBookings"    WHERE LOWER("Status") LIKE '%cancel%'
--     UNION ALL SELECT "TravelFileId", "SalePrice" FROM "AssistanceBookings" WHERE LOWER("Status") LIKE '%cancel%'
--     UNION ALL SELECT "TravelFileId", "SalePrice" FROM "FlightSegments"     WHERE UPPER("Status") IN ('UN','UC','HX','NO')
-- )
-- SELECT COUNT(DISTINCT file_id) AS reservas_afectadas, SUM(sale) AS importe_total FROM cancelados WHERE sale > 0;
