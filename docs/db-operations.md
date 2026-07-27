# DB Operations

## Backup
- Servicio automatico: `postgres-backup`
- Script manual VPS/Linux: `scripts/ops/backup-db.sh`
- Script manual Windows/PowerShell: `scripts/db/backup-postgres.ps1`
- Target por defecto: Docker container `travel_db`
- Formato: `pg_dump -Fc`
- Retencion automatica configurable:
  - `BACKUP_DAILY_RETENTION_DAYS`, default 14
  - `BACKUP_WEEKLY_RETENTION_DAYS`, default 56

Ejemplo:

```bash
bash scripts/ops/backup-db.sh
```

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\db\backup-postgres.ps1
```

Los dumps quedan bajo `backups/postgres`. Esa carpeta esta ignorada por Git y debe respaldarse fuera del VPS.

## Backup de volumenes

- Script: `scripts/ops/backup-volumes.sh`
- Volumenes incluidos por defecto:
  - `minio_data`
  - `whatsapp_auth`
  - `rabbitmq_data`
- `pgdata` no se respalda en caliente por defecto. Para PostgreSQL, la fuente confiable es `pg_dump`. Si se necesita snapshot de `pgdata`, detener la DB o usar snapshot consistente del proveedor.

Ejemplo:

```bash
bash scripts/ops/backup-volumes.sh
```

## Restore Drill
- Script VPS/Linux: `scripts/ops/restore-db.sh`
- Script Windows/PowerShell: `scripts/db/restore-postgres-shadow.ps1`
- Objetivo: restaurar sobre una base sombra (`travel_shadow`) sin tocar la productiva.

Ejemplo:

```bash
bash scripts/ops/restore-db.sh --backup backups/postgres/daily/travel-20260429-010000.dump
```

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\db\restore-postgres-shadow.ps1 -BackupFile .\backups\postgres\daily\travel-20260325-010000.dump
```

Restore destructivo sobre primaria solo con confirmacion explicita:

```bash
CONFIRM_RESTORE_PRIMARY=YES bash scripts/ops/restore-db.sh --backup backups/postgres/daily/travel-20260429-010000.dump --target primary
```

## Restaurar un backup de "Empezar de cero"

Obra "Empezar de cero" (2026-07-27): antes de un borrado masivo de datos, la propia API genera un backup
COMPLETO (Postgres + MinIO) — ver `SystemDataWipeService`/`PgDumpAndMinioWipeBackupPort` en el backend. La
restauración es 100% manual por procedimiento de servidor (NO hay botón en la app a propósito: restaurar es
tan destructivo como borrar).

- **Dump de Postgres**: queda en `./backups/postgres/wipe/wipe-<yyyyMMdd-HHmmss>.dump` (mismo formato
  `pg_dump -Fc` que el resto de los backups de este documento). **Contiene TODOS los datos de la base** —
  como el resto de los dumps de este documento, respaldarlo TAMBIÉN fuera del VPS (esa carpeta está ignorada
  por Git y no sobrevive un problema de disco/servidor).
- **Objetos de MinIO**: quedan COPIADOS (fix bloqueante de revisión, 2026-07-27 — antes decía "movidos"; los
  originales los borra la API recién DESPUÉS de confirmar el borrado en Postgres) dentro del MISMO bucket,
  bajo el prefijo `wipe-backup-<yyyyMMdd-HHmmss>/`. Copiar el prefijo de vuelta a la raíz del bucket restaura
  los archivos adjuntos (vouchers, comprobantes subidos, etc.) exactamente donde estaban.

### Paso 1 — restaurar Postgres a la base sombra (SIN tocar la productiva)

```bash
bash scripts/ops/restore-db.sh --backup backups/postgres/wipe/wipe-20260727-153000.dump --target shadow
```

Verificar contra `travel_shadow` (conteos de tablas, usuarios, settings) antes de seguir.

### Paso 2 — restaurar Postgres a la primaria (SOLO tras verificar el paso 1)

```bash
CONFIRM_RESTORE_PRIMARY=YES bash scripts/ops/restore-db.sh --backup backups/postgres/wipe/wipe-20260727-153000.dump --target primary
```

Esto detiene `api`/`worker` durante el restore (ver `restore-db.sh`). **NO arrancarlos todavía** — recién se
levantan en el Paso 5, después de devolver los archivos de MinIO (si se levanta la API antes, puede servir
respuestas con datos de Postgres ya restaurados pero adjuntos de MinIO todavía en el prefijo de backup).

### Paso 3 — copiar el prefijo de MinIO de vuelta a la raíz del bucket

El contenedor `minio` trae `mc` (MinIO Client) embebido (el propio healthcheck del `docker-compose.yml` lo
usa: `mc ready local`). Las credenciales se expanden DENTRO del contenedor (que ya las tiene como variables
de entorno propias, definidas en `docker-compose.yml`) — nunca en el shell del host, para no dejarlas en el
historial de bash ni en `docker inspect` del proceso:

```bash
# 1) Configurar un alias local apuntando al propio servidor MinIO (una sola vez por sesión). Las comillas
#    simples son a propósito: MINIO_ROOT_USER/MINIO_ROOT_PASSWORD se expanden ADENTRO del contenedor, no acá.
docker exec travel_minio sh -c 'mc alias set local http://127.0.0.1:9000 "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD"'

# 2) Listar el contenido del prefijo de backup para confirmar que es el correcto ANTES de copiar nada.
docker exec travel_minio mc ls --recursive local/reservations/wipe-backup-20260727-153000/

# 3) COPIAR (no mover) el prefijo de vuelta a la raíz del bucket. El backup queda intacto en su prefijo -
#    recién se limpia (paso opcional 3b) DESPUÉS de verificar que todo abre bien (Paso 4).
docker exec travel_minio mc cp --recursive local/reservations/wipe-backup-20260727-153000/ local/reservations/

# 3b) (opcional, solo tras verificar el Paso 4) limpiar el prefijo de backup ya restaurado.
docker exec travel_minio mc rm --recursive --force local/reservations/wipe-backup-20260727-153000/
```

⚠️ **Verificación obligatoria antes del paso 3**: el nombre exacto del prefijo (detalle del `AuditLog` con
`Action = SystemDataWiped`, campo `backupMinioPrefijo`) — copiar el prefijo equivocado puede mezclar archivos
de un backup distinto. Como el comando apunta a un prefijo ESPECÍFICO (no a la raíz del bucket), los
`wipe-backup-*` de corridas anteriores NO se tocan ni "vuelven" con este paso — solo se restaura el prefijo
que se nombra explícitamente. La sintaxis exacta del binario `mc` embebido no está verificada contra un
contenedor real en esta revisión: `infrastructure-devops-docker` debe confirmarla antes de un incidente real.

### Paso 4 — verificar

- Login funciona con un usuario existente (usuarios/roles NUNCA se tocan en el wipe).
- El `AuditLog` con `Action = SystemDataWiped` sigue visible (la auditoría tampoco se toca).
- Una reserva/factura restaurada abre su adjunto/voucher desde MinIO sin 404.

### Paso 5 — arrancar los servicios de nuevo

Recién ACA, con Postgres Y MinIO restaurados y verificados:

```bash
docker compose up -d api worker
```

## Objetivos operativos de esta fase
- `RPO`: 24h
- `RTO`: restore probado en menos de 60 minutos

## Checklist de cierre
- Backup full diario ejecutando sin error.
- Restore exitoso en `travel_shadow`.
- API levantando y consultando datos desde una base restaurada.
- `whatsapp_auth` respaldado y verificado.
- Documentar fecha, operador, dump usado y duracion real de cada restore drill.
