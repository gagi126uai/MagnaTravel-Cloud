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

## Restaurar TOTAL desde la app (2026-07-28)

Además del restore manual de más arriba, un Admin puede ejecutar una restauración TOTAL desde la propia
aplicación (`POST /api/admin/danger/restore` con `modo: "total"`): reemplaza toda la base viva por la foto de
un backup, con backup previo automático del estado actual, modo mantenimiento mientras dura (todo `/api/**`
y `/hubs/**` responde 503, salvo `GET /api/system/status`, `POST /api/admin/danger/restore`, `POST
/api/auth/login` y `POST /api/auth/refresh`), y todo dentro de una única transacción de `pg_restore`.

### Requisito OBLIGATORIO antes de usar la restauración total en producción (nginx del HOST)

⚠️ **Verificar esto ANTES del primer uso en producción, no durante un incidente.** `nginx.conf` de este repo
(el nginx que corre DENTRO del contenedor `web`) ya tiene un `location /api/admin/danger/` con
`proxy_read_timeout`/`proxy_send_timeout` largos (2700s). Pero en producción hay **otro nginx corriendo en el
HOST** (Ubuntu, versión 1.24.0 al momento de esta revisión, **fuera de este repo**, configurado en
`/etc/nginx/` del VPS) que hace de reverse-proxy hacia el contenedor `web` — ese nginx tiene su propio
default de 60 segundos y corta la conexión ANTES que cualquier otra cosa, sin importar lo que diga el nginx
del contenedor.

**Paso manual en el VPS** (una sola vez, o cada vez que se reconfigure ese nginx):

```bash
# 1) Verificar el valor ACTUAL configurado para el location del backoffice:
nginx -T | grep -A5 "location.*api"

# 2) Si no aparece "proxy_read_timeout"/"proxy_send_timeout" (o son menores a 2700s), agregar en el
#    location correspondiente al backoffice (ej. /etc/nginx/sites-available/backoffice, dentro del
#    location que proxypasea hacia el contenedor "web"):
#        proxy_read_timeout 2700s;
#        proxy_send_timeout 2700s;

# 3) Validar la sintaxis y recargar:
nginx -t && systemctl reload nginx
```

Sin este paso, una restauración total real se corta a los 60 segundos en el nginx del host — el pedido HTTP
muere, pero (ver el hallazgo B1 más abajo) el `pg_restore` real sigue vivo en el contenedor `api`.

### Runbook: el sistema quedó en mantenimiento y no se sabe si terminó

**El chequeo autoritativo es contra la BASE, nunca contra procesos del sistema operativo.** El `pg_restore`
real corre DENTRO del contenedor **`api`** (no `travel_db`: el Dockerfile de la API instala
`postgresql-client-16` y el puerto lanza ahí el `Process.Start`) — y la imagen `postgres:16` de `travel_db`
ni siquiera trae el binario `ps`, así que `docker exec travel_db ps aux | grep pg_restore` da un resultado
VACÍO SIEMPRE, sea que el restore esté corriendo o no. Un operador que use ese comando y vea "nada corriendo"
puede concluir erróneamente "ya terminó, es seguro reabrir el sistema" y borrar el archivo de estado con la
base todavía a medio reemplazar — el escenario exacto que el modo mantenimiento existe para evitar.

**Comando correcto** (consulta la actividad real contra Postgres, sin importar en qué contenedor corre el
cliente que la generó):

```bash
docker exec travel_db psql -U traveluser -d postgres -c \
  "select pid, state, left(query, 60) as query from pg_stat_activity where datname = 'travel';"
```

(ajustar `traveluser`/`travel` si `POSTGRES_USER`/`POSTGRES_DB` fueron sobreescritos en `.env`). Si aparece una
fila con una consulta larga en curso contra la base `travel`, la restauración TODAVÍA está corriendo — **no
tocar nada**, esperar y volver a consultar. Recién cuando la consulta ya no aparece es seguro asumir que el
`pg_restore` terminó (con éxito o con rollback automático — ver el hallazgo B1: ambos casos son seguros para
reabrir el sistema; lo único inseguro es "no sé si terminó").

### Salida de emergencia si el sistema queda "tapiado"

El modo mantenimiento se auto-desactiva solo si sigue activo pasado `Maintenance:MaxDurationMinutes` (fijado
explícito en `docker-compose.yml`, `api` y `worker`, en 30 minutos) — pensado para el caso en que el PROCESO
muere a mitad de una restauración. **Excepción importante (hallazgo B-N2 de seguridad)**: si el desenlace de
un `pg_restore` quedó incierto (timeout propio agotado) o no se pudo confirmar que AFIP quedó en modo
homologación, el sistema queda marcado "requiere intervención manual" y **la auto-expiración NO aplica** —
nunca se reabre solo, hay que seguir este runbook a mano.

1. **Confirmar con el comando de arriba** que no hay ninguna consulta en curso contra la base `travel`.
2. **Parar el sidecar de backup automático** si todavía sigue activo desde el intento anterior:
   `docker compose stop postgres-backup` (su `pg_dump` diario toma locks que pueden interferir con un
   `pg_restore --clean` — ver el comentario en `docker-compose.yml`, servicio `postgres-backup`).
3. Recién con el paso 1 confirmado, borrar el archivo de estado — **path ABSOLUTO** (puede requerir `sudo`
   según los permisos del volumen montado, ya que el contenedor escribe con su propio usuario interno):

   ```bash
   sudo rm /ruta/al/repo/logs/maintenance-mode-state.json
   ```

   `/ruta/al/repo/logs` es el host-path del volumen `./logs:/app/logs` (mismo volumen que
   `Maintenance:StateFilePath=/app/logs/maintenance-mode-state.json` dentro del contenedor) — **compartido
   entre `api` y `worker`** (por eso el worker se entera de un mantenimiento activado por la API, ver
   `FileMaintenanceModeService`). **No "limpiar" ni vaciar todo ese directorio** — ahí también viven los logs
   de Serilog de ambos procesos; borrar solo el archivo de estado puntual.
4. **NO hace falta reiniciar los contenedores**: `FileMaintenanceModeService` relee el archivo cada ~2
   segundos (caché corto, ver el comentario de esa clase) — a los pocos segundos ambos procesos ven el estado
   inactivo solos. Si de verdad hace falta forzar un reinicio, **ojo**: `docker compose restart api` MATA
   cualquier `pg_restore` que siga corriendo de verdad (corre DENTRO de ese contenedor) — solo reiniciar
   DESPUÉS de haber confirmado con el paso 1 que no hay nada corriendo; si el paso 1 mostró una consulta en
   curso, un reinicio de `api` en este momento la cortaría a la fuerza, dejando la base en un estado
   verdaderamente incierto (peor que esperar).
5. Reactivar el backup automático si se paró en el paso 2: `docker compose start postgres-backup`.

## Objetivos operativos de esta fase
- `RPO`: 24h
- `RTO`: restore probado en menos de 60 minutos

## Checklist de cierre
- Backup full diario ejecutando sin error.
- Restore exitoso en `travel_shadow`.
- API levantando y consultando datos desde una base restaurada.
- `whatsapp_auth` respaldado y verificado.
- Documentar fecha, operador, dump usado y duracion real de cada restore drill.
