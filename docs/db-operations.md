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

## Objetivos operativos de esta fase
- `RPO`: 24h
- `RTO`: restore probado en menos de 60 minutos

## Checklist de cierre
- Backup full diario ejecutando sin error.
- Restore exitoso en `travel_shadow`.
- API levantando y consultando datos desde una base restaurada.
- `whatsapp_auth` respaldado y verificado.
- Documentar fecha, operador, dump usado y duracion real de cada restore drill.
