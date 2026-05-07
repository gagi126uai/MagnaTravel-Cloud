# MagnaTravel-Cloud

## Deploy con Docker en VPS

Este repo queda preparado para levantar todo con Docker Compose:

- `db`: PostgreSQL
- `migrate`: paso unico de migraciones EF antes de publicar la API
- `api`: ASP.NET Core para trafico HTTP
- `worker`: ASP.NET Core + Hangfire dedicado a jobs recurrentes/background
- `postgres-backup`: backup diario con `pg_dump -Fc` y retencion configurable
- `web`: frontend Vite servido por Nginx
- `whatsapp-bot`: bot de WhatsApp con sesion persistida en volumen
- `minio`: almacenamiento de archivos
- `rabbitmq`: mensajeria interna

### Variables

1. Crear `.env` a partir de [.env.example](./.env.example).
2. Completar como minimo:

- `POSTGRES_PASSWORD`
- `JWT_KEY`
- `SECURITY_ENCRYPTION_KEY`
- `WHATSAPP_WEBHOOK_SECRET`
- `METRICS_TOKEN`
- `RABBITMQ_PASSWORD`
- `MINIO_ROOT_USER`
- `MINIO_ROOT_PASSWORD`
- `WEB_ORIGIN`

`VITE_API_URL` puede quedar vacio. En ese caso el frontend usa mismo origen y Nginx proxya `/api`, `/hubs` y `/hangfire` al backend.

### Levantar o actualizar en VPS

```bash
git pull
bash scripts/ops/deploy.sh
```

Si se usa `docker compose up -d --build` manualmente, las migraciones se aplican automaticamente: `api` y `worker` declaran `depends_on: migrate (service_completed_successfully)`, asi que `up` espera a que el job `migrate` termine con exit 0 antes de levantar los servicios.

```bash
docker compose up -d --build
```

Para forzar la migracion fuera del flujo de `up` (ej. solo aplicar SQL sin reiniciar):

```bash
docker compose run --rm migrate
```

### Que hace automaticamente

- El contenedor `api` espera a que PostgreSQL este saludable.
- Las migraciones EF se ejecutan una sola vez con el servicio `migrate`; la API no migra la base en cada arranque de produccion.
- `worker` ejecuta Hangfire y agenda jobs recurrentes; `api` queda dedicada al trafico HTTP.
- `postgres-backup` genera un backup diario por fecha en `backups/postgres/daily` y copia semanal en `backups/postgres/weekly` los domingos.
- El frontend espera al backend saludable antes de iniciar.
- El bot espera al backend saludable antes de iniciar.
- Los healthchecks quedan definidos para `db`, `api`, `worker`, `web`, `minio` y `whatsapp-bot`.

### Primer arranque del bot

El volumen `whatsapp_auth` persiste la sesion de WhatsApp. Si el bot no tiene sesion iniciada, hay que vincularlo una vez y luego la sesion queda guardada.

La autenticacion de `whatsapp-web.js` se guarda en `LocalAuth` usando `WHATSAPP_AUTH_PATH=/app/.wwebjs_auth`. En Docker Compose ese path esta montado en el volumen persistente `whatsapp_auth:/app/.wwebjs_auth`, por lo que no queda dentro del filesystem temporal del contenedor.

Al reiniciar Docker, el contenedor vuelve a montar `whatsapp_auth`, el bot limpia locks temporales y recupera la sesion existente desde `/app/.wwebjs_auth`. Solo deberia volver a pedir QR si el volumen fue eliminado, si WhatsApp invalido la sesion o si se ejecuto logout del bot.

### Comandos operativos

```bash
bash scripts/ops/deploy.sh
bash scripts/ops/check-prod.sh
bash scripts/ops/backup-db.sh
bash scripts/ops/restore-db.sh --backup backups/postgres/daily/travel-YYYYMMDD-HHMMSS.dump
bash scripts/ops/backup-volumes.sh
bash scripts/ops/docker-disk-usage.sh
bash scripts/ops/docker-cleanup.sh
bash scripts/ops/load-test-smoke.sh
```

Metricas internas protegidas:

```bash
docker compose exec -T api curl -H "X-Metrics-Token: $METRICS_TOKEN" http://127.0.0.1:8080/internal/metrics
```

Mas detalle: [docs/operations/vps-robustness.md](docs/operations/vps-robustness.md) y [docs/db-operations.md](docs/db-operations.md).

### Comandos utiles

```bash
docker compose ps
docker compose logs -f api
docker compose logs -f worker
docker compose logs -f whatsapp-bot
docker compose logs -f web
docker compose logs -f db
docker compose logs -f postgres-backup
```

### Control de espacio Docker

Ver uso real de Docker:

```bash
bash scripts/ops/docker-disk-usage.sh
```

Limpiar en modo simulacion:

```bash
bash scripts/ops/docker-cleanup.sh
```

Ejecutar limpieza segura de objetos no usados, sin tocar volumenes:

```bash
bash scripts/ops/docker-cleanup.sh --execute
```

Los logs de contenedores quedan rotados por Compose con `DOCKER_LOG_MAX_SIZE` y `DOCKER_LOG_MAX_FILE`.

Si Vouchers o Mensajes devuelven 500, revisar primero:

```bash
docker compose run --rm migrate
docker compose logs --tail=200 api
docker compose exec -T api curl -fsS http://127.0.0.1:8080/health/ready
```
