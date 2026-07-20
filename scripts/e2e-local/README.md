# E2E local con la app real (armado 2026-07-19, Tanda 1 contrato pantalla-motor)

Receta para levantar TODO local y correr el paseo E2E de pagos a proveedor
(13 chequeos, incluye el cartel con el mensaje real del motor). La cadena de
migraciones NO arranca desde base vacía (ver "Deuda técnica" abajo), por eso
el esquema se genera desde el modelo actual.

## Levantar el entorno (en orden)

1. **Postgres local** (si el contenedor ya existe: `docker start travel_db_local`):
   ```
   docker run -d --name travel_db_local -e POSTGRES_DB=travel -e POSTGRES_USER=traveluser \
     -e POSTGRES_PASSWORD=travelpass -p 5432:5432 postgres:16
   ```
2. **Esquema desde el modelo** (solo la primera vez, base vacía):
   ```
   cd src/TravelApi && dotnet ef dbcontext script -o schema.sql
   # aplicar schema.sql con psql (sacarle el BOM), luego poblar __EFMigrationsHistory
   # con TODOS los IDs de src/TravelApi.Infrastructure/Persistence/Migrations y .../Migrations/App
   # (ver docs/explicaciones/2026-07-19-*.md para los comandos exactos)
   ```
3. **Llaves que PROD tiene prendidas y el default no**:
   ```
   UPDATE "OperationalFinanceSettings" SET "EnableCatalogFindOrCreate"=true, "EnableMultiCurrencyInvoicing"=true;
   ```
   Sin `EnableCatalogFindOrCreate` la moneda del servicio SE IGNORA (camino legacy) y todo nace en pesos.
4. **API**: `cd src/TravelApi && ASPNETCORE_URLS=http://localhost:60663 dotnet run --no-launch-profile`
5. **Front same-origin** (obligatorio: cookies Secure + CSRF no viajan cross-origin con curl/fetch):
   ```
   cd src/TravelWeb && npx vite --config ../../scripts/e2e-local/vite.e2e.config.mjs
   ```
   (el config proxya `/api` → 60663, misma topología que nginx en PROD)
6. **Paseo**: `npm i playwright-core` donde corras el script y `node e2e-t1.js`
   (primer usuario registrado = Admin: e2e@magnatravel.local / E2eLocal2026!).

## Deuda técnica encontrada armando esto (2026-07-19)

- **Migraciones rotas desde cero**: la migración inicial crea la tabla `Reservas`
  pero el resto de la cadena referencia `TravelFiles` (el renombre se hizo a mano
  en la base, nunca hubo migración de renombre). Una base vacía NO puede migrar.
  En el VPS nunca se notó porque esa base ya existía.
- Las capturas `4-reserva-elegida.png` y `6-cartel-mensaje-real.png` son la
  evidencia del E2E del 2026-07-19 (multimoneda andando + cartel con mensaje real).
