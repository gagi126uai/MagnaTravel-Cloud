# VPS Robustness Stage

Este documento define la etapa recomendada para escalar MagnaTravel en un VPS robusto sin saltar todavia a Kubernetes ni microservicios completos. El objetivo es que el producto sea operable, recuperable y medible antes de agregar mas complejidad.

## Objetivo

- API disponible 99% mensual.
- `p95` menor a 800 ms en endpoints principales.
- Restore probado en menos de 60 minutos.
- Perdida maxima aceptada: ultimo backup programado.

## Deploy

Usar siempre:

```bash
bash scripts/ops/deploy.sh
```

El deploy valida secretos, construye imagenes, levanta infraestructura, ejecuta `migrate` una sola vez, inicia `api`, `worker`, `web`, `reservas-service`, `whatsapp-bot` y `postgres-backup`, y termina con `check-prod`.

No reemplazar ese flujo por `docker compose up -d --build` en produccion. Si se levanta manualmente, correr primero `docker compose run --rm migrate`; si no, `reservas-service` puede quedar sin tablas nuevas como `Vouchers`, `VoucherPassengerAssignments`, `VoucherAuditEntries` o `MessageDeliveries`.

En produccion la API queda con:

- `Database__ApplyMigrationsOnStartup=false`
- `Hangfire__ServerEnabled=false`
- `Hangfire__SchedulerEnabled=false`

El worker queda con:

- `Hangfire__ServerEnabled=true`
- `Hangfire__SchedulerEnabled=true`

## Backups y restore

Backups automaticos:

- `postgres-backup` guarda como maximo un dump diario por fecha en `backups/postgres/daily`.
- Los domingos copia ese dump diario a `backups/postgres/weekly` si todavia no existe copia semanal de esa fecha.
- Retencion por `.env`: `BACKUP_DAILY_RETENTION_DAYS` y `BACKUP_WEEKLY_RETENTION_DAYS`.

Backups manuales:

```bash
bash scripts/ops/backup-db.sh
bash scripts/ops/backup-volumes.sh
```

Restore seguro:

```bash
bash scripts/ops/restore-db.sh --backup backups/postgres/daily/travel-YYYYMMDD-HHMMSS.dump
```

Sin restore probado, el backup no cuenta. Registrar fecha, dump, duracion, resultado y responsable.

## Observabilidad

Endpoints de salud:

- `/health/live`
- `/health/ready`

Metricas internas protegidas:

```bash
docker compose exec -T api curl -H "X-Metrics-Token: $METRICS_TOKEN" http://127.0.0.1:8080/internal/metrics
```

Metricas expuestas en formato Prometheus:

- requests totales por metodo, ruta y status.
- histograma de duracion HTTP.
- requests activos.
- uptime del proceso.

Alertas minimas a configurar en el VPS o proveedor:

- errores HTTP 5xx.
- contenedores caidos o unhealthy.
- jobs fallidos en Hangfire.
- uso de disco alto.
- base de datos no disponible.
- backups vencidos o inexistentes.
- `p95` por encima de 800 ms.

## Base de datos

PostgreSQL queda configurado con slow query logging:

- `POSTGRES_SLOW_QUERY_MS`, default 500 ms.
- logs por stdout/stderr del contenedor, visibles con `docker compose logs -f db`.

La migracion `AddOperationalIndexes` agrega indices para reservas, pagos, pasajeros, clientes, vouchers, auditoria, mensajes, adjuntos y entregas WhatsApp.

Tareas operativas mensuales:

- revisar queries lentas.
- validar crecimiento de tablas calientes.
- ejecutar restore drill.
- revisar necesidad de `VACUUM/ANALYZE` manual si hay bloat.
- evaluar PgBouncer si aparecen muchas conexiones concurrentes.

## Estado compartido

- Archivos: MinIO, no filesystem local de API.
- Sesion WhatsApp: volumen `whatsapp_auth`.
- Logs: stdout + `logs/`.
- No guardar datos criticos en memoria local.

Siguiente fase razonable:

- Redis para cache distribuida y SignalR backplane.
- PgBouncer si hay presion de conexiones.
- balanceador + multi-instancia de API.

## WhatsApp

El bot debe mantenerse como singleton por numero/sesion. No replicar `whatsapp-bot` sin estrategia explicita por numero.

Persistencia:

- `WHATSAPP_AUTH_PATH=/app/.wwebjs_auth`
- volumen Docker: `whatsapp_auth:/app/.wwebjs_auth`

Recuperacion:

- si el contenedor se reinicia, monta `whatsapp_auth` y recupera la sesion.
- si el volumen se elimina o WhatsApp invalida la sesion, sera necesario escanear QR de nuevo.
- respaldar `whatsapp_auth` con `bash scripts/ops/backup-volumes.sh`.

## Smoke Load Test

Prueba basica:

```bash
bash scripts/ops/load-test-smoke.sh
```

Con rutas autenticadas:

```bash
ACCESS_TOKEN="<jwt>" LOAD_TEST_PATHS="/api/reservas,/api/messages/recipients" REQUESTS=50 CONCURRENCY=8 bash scripts/ops/load-test-smoke.sh
```

Esto no reemplaza k6/Locust/Artillery; es una prueba humo para detectar regresiones obvias despues de deploy.
