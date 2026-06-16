#!/usr/bin/env bash
# ADR-032 (2026-06-15) — verificacion READ-ONLY previa al deploy. NO es migracion: no toca datos.
#
# Reporta los cobros VIVOS (IsDeleted=false) que quedan en reservas fuera de la lista de estados
# cobrables (ActiveCollectionStatuses = InManagement, Confirmed, Traveling, ToSettle). Con ADR-032,
# en esas reservas ya no se puede cobrar/editar/borrar a mano un cobro; la correccion va por ANULAR
# (POST /api/payments/{id}/annul). Este script dimensiona cuantos cobros existentes caen en ese caso.
#
# Separa en tres grupos:
#   (a) cobros vivos REALES (no puente) en CUALQUIER estado no cobrable;
#   (b) cobros vivos REALES en estados TERMINALES (los que necesitarian anulacion: Cancelled, Closed,
#       PendingOperatorRefund, Lost). Subconjunto de (a) que es el caso operativo a corregir;
#   (c) PUENTES vivos (saldo a favor: AffectsCash=false) en estados no cobrables — NO son cobros del
#       usuario, los maneja el sistema; se listan aparte para no confundirlos con (a)/(b).
#
# Nombres de tabla/columna (verificados en AppDbContext, no asumidos):
#   - Reserva  -> tabla "TravelFiles", columna estado "Status".
#   - Payment  -> tabla "Payments", FK a reserva = "TravelFileId", soft-delete = "IsDeleted".
#   - Puentes  -> Payment.Method IN ('SaldoAFavor','SaldoAFavorAplicado') con "AffectsCash" = false.
#
# Uso:
#   ./scripts/ops/adr032-check-payments-noncollectable.sh
#
# Variables esperadas: POSTGRES_USER, POSTGRES_DB (desde .env del VPS). Corre dentro del container db.

set -euo pipefail

cd "$(dirname "$0")/../.."

if [ -f .env ]; then
  # shellcheck disable=SC1091
  set -a; . ./.env; set +a
fi

: "${POSTGRES_USER:?POSTGRES_USER no definido}"
: "${POSTGRES_DB:?POSTGRES_DB no definido}"

# Lista canonica de estados cobrables (debe coincidir con EstadoReserva.ActiveCollectionStatuses).
# Si cambia en el dominio, actualizar aca tambien.
QUERY=$(cat <<'SQL'
WITH collectable AS (
  SELECT unnest(ARRAY['InManagement','Confirmed','Traveling','ToSettle']) AS status
),
bridge AS (
  -- Puentes de saldo a favor: no son cobros del usuario.
  SELECT p."Id"
    FROM "Payments" p
   WHERE p."AffectsCash" = false
     AND p."Method" IN ('SaldoAFavor','SaldoAFavorAplicado')
)
-- (a) Cobros REALES vivos en cualquier estado NO cobrable.
SELECT
  '(a) cobros reales vivos en estado NO cobrable' AS grupo,
  COUNT(*) AS cantidad
  FROM "Payments" p
  JOIN "TravelFiles" r ON r."Id" = p."TravelFileId"
 WHERE p."IsDeleted" = false
   AND p."Id" NOT IN (SELECT "Id" FROM bridge)
   AND r."Status" NOT IN (SELECT status FROM collectable)

UNION ALL

-- (b) Subconjunto: cobros REALES vivos en estados TERMINALES (necesitan anulacion).
SELECT
  '(b) cobros reales vivos en estado TERMINAL (a anular)',
  COUNT(*)
  FROM "Payments" p
  JOIN "TravelFiles" r ON r."Id" = p."TravelFileId"
 WHERE p."IsDeleted" = false
   AND p."Id" NOT IN (SELECT "Id" FROM bridge)
   AND r."Status" IN ('Cancelled','Closed','PendingOperatorRefund','Lost')

UNION ALL

-- (c) Puentes vivos en estado NO cobrable (informativo: los maneja el sistema).
SELECT
  '(c) puentes saldo-a-favor vivos en estado NO cobrable',
  COUNT(*)
  FROM "Payments" p
  JOIN "TravelFiles" r ON r."Id" = p."TravelFileId"
 WHERE p."IsDeleted" = false
   AND p."Id" IN (SELECT "Id" FROM bridge)
   AND r."Status" NOT IN (SELECT status FROM collectable);
SQL
)

# Detalle por estado de los cobros REALES vivos en estado no cobrable (para entender la distribucion).
DETAIL=$(cat <<'SQL'
SELECT
  r."Status" AS estado,
  COUNT(*) AS cobros_reales_vivos
  FROM "Payments" p
  JOIN "TravelFiles" r ON r."Id" = p."TravelFileId"
 WHERE p."IsDeleted" = false
   AND NOT (p."AffectsCash" = false AND p."Method" IN ('SaldoAFavor','SaldoAFavorAplicado'))
   AND r."Status" NOT IN ('InManagement','Confirmed','Traveling','ToSettle')
 GROUP BY r."Status"
 ORDER BY cobros_reales_vivos DESC;
SQL
)

echo "ADR-032 — verificacion READ-ONLY de cobros en reservas no cobrables (DB '$POSTGRES_DB')."
echo

echo "== Resumen =="
docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -F '|' -c "$QUERY" | column -t -s '|'

echo
echo "== Detalle por estado (cobros reales vivos no cobrables) =="
docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c "$DETAIL"

echo
echo "Lectura: si (b) > 0, esos cobros se corrigen con POST /api/payments/{id}/annul (anular con rastro)."
echo "Este script NO modifica datos. NO se requiere migracion para ADR-032."
