#!/usr/bin/env bash
# Pre-check para la migracion 20260506181208_RestrictSupplierDeleteOnTypedBookings (C24).
#
# Verifica que no haya filas huerfanas en HotelBookings/TransferBookings/PackageBookings/
# FlightSegments apuntando a un SupplierId inexistente. Si hay, AddForeignKey con Restrict
# fallara y la migracion deja la BD a medio aplicar (los DropForeignKey ya se ejecutaron).
#
# Uso:
#   ./scripts/ops/precheck-supplier-restrict.sh
#
# Variables esperadas: POSTGRES_USER, POSTGRES_DB, POSTGRES_PASSWORD (desde .env del VPS).
# El script asume psql disponible en el host o ejecuta dentro del container db.

set -euo pipefail

cd "$(dirname "$0")/../.."

if [ -f .env ]; then
  # shellcheck disable=SC1091
  set -a; . ./.env; set +a
fi

: "${POSTGRES_USER:?POSTGRES_USER no definido}"
: "${POSTGRES_DB:?POSTGRES_DB no definido}"

QUERY=$(cat <<'SQL'
SELECT 'HotelBookings' AS tabla, COUNT(*) AS huerfanos
  FROM "HotelBookings" h LEFT JOIN "Suppliers" s ON s."Id" = h."SupplierId"
  WHERE s."Id" IS NULL
UNION ALL
SELECT 'TransferBookings', COUNT(*)
  FROM "TransferBookings" h LEFT JOIN "Suppliers" s ON s."Id" = h."SupplierId"
  WHERE s."Id" IS NULL
UNION ALL
SELECT 'PackageBookings', COUNT(*)
  FROM "PackageBookings" h LEFT JOIN "Suppliers" s ON s."Id" = h."SupplierId"
  WHERE s."Id" IS NULL
UNION ALL
SELECT 'FlightSegments', COUNT(*)
  FROM "FlightSegments" h LEFT JOIN "Suppliers" s ON s."Id" = h."SupplierId"
  WHERE s."Id" IS NULL;
SQL
)

echo "Ejecutando pre-check contra DB '$POSTGRES_DB'..."
RESULT=$(docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At -F '|' -c "$QUERY")

echo "$RESULT" | column -t -s '|'

ORPHANS=$(echo "$RESULT" | awk -F '|' '{ s += $2 } END { print s }')

if [ "${ORPHANS:-0}" != "0" ]; then
  echo
  echo "ERROR: hay $ORPHANS filas huerfanas. NO aplicar la migracion."
  echo "Limpiar antes (sanear FK rotas) y repetir el pre-check."
  exit 1
fi

echo
echo "OK: no hay huerfanos. Migracion 20260506181208_RestrictSupplierDeleteOnTypedBookings es segura."
