-- ============================================================================
-- P1.5 — IMPACTO: reservas cuyo SALDO se corrige al unificar el calculo
-- ============================================================================
-- SOLO LECTURA. No modifica nada. Corrise en el VPS (Postgres) para ver, en los
-- datos reales, que reservas tienen servicios CANCELADOS cuyo importe puede estar
-- inflando el saldo guardado.
--
-- Contexto: antes de P1.5, el saldo de la reserva se recalculaba de 3 formas
-- distintas segun la accion (tocar servicio / registrar pago / facturar). Dos de
-- esas formas sumaban PLANO, contando los servicios Cancelados que no deberian
-- contar. Tras P1.5 las 3 usan el mismo calculo (los Cancelados NO cuentan), asi
-- que el saldo de estas reservas se CORRIGE (baja) la proxima vez que se recalcule
-- (o con un backfill que recalcule todas de una).
--
-- "Cancelado" = estado generico que contiene 'cancel' (hotel/transfer/paquete/
-- asistencia/servicio) o codigo de vuelo IATA UN/UC/HX/NO.
--
-- OJO (honestidad): que el saldo guardado HOY incluya o no el importe cancelado
-- depende de cual fue la ultima accion que lo recalculo. Por eso "VentaCorrectaAprox"
-- es una estimacion del piso; el numero exacto queda fijo recien tras recalcular.
-- ============================================================================

WITH cancelados AS (
    SELECT "ReservaId", "SalePrice" FROM "HotelBookings"      WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "ReservaId", "SalePrice" FROM "TransferBookings"   WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "ReservaId", "SalePrice" FROM "PackageBookings"    WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "ReservaId", "SalePrice" FROM "AssistanceBookings" WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "ReservaId", "SalePrice" FROM "Servicios"          WHERE LOWER("Status") LIKE '%cancel%'
    UNION ALL
    SELECT "ReservaId", "SalePrice" FROM "FlightSegments"     WHERE UPPER("Status") IN ('UN', 'UC', 'HX', 'NO')
)
SELECT
    r."NumeroReserva",
    r."TotalSale"                         AS "VentaGuardadaHoy",
    r."Balance"                           AS "SaldoGuardadoHoy",
    SUM(c."SalePrice")                    AS "ImporteCanceladoQuePuedeEstarContando",
    r."TotalSale" - SUM(c."SalePrice")    AS "VentaCorrectaAprox"
FROM cancelados c
JOIN "Reservas" r ON r."Id" = c."ReservaId"
GROUP BY r."Id", r."NumeroReserva", r."TotalSale", r."Balance"
HAVING SUM(c."SalePrice") > 0
ORDER BY SUM(c."SalePrice") DESC;

-- Resumen rapido: cuantas reservas y cuanto importe total afectado.
-- (Descomentar para correr aparte.)
-- WITH cancelados AS ( ...mismo UNION ALL de arriba... )
-- SELECT COUNT(DISTINCT "ReservaId") AS reservas_afectadas, SUM("SalePrice") AS importe_total
-- FROM cancelados WHERE "SalePrice" > 0;
