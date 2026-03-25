# DB Operations

## Backup
- Script: `scripts/db/backup-postgres.ps1`
- Default target: Docker container `travel_db`
- Format: `pg_dump -Fc`
- Retention:
  - diarios: 14
  - semanales: 8

Ejemplo:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\db\backup-postgres.ps1
```

## Restore Drill
- Script: `scripts/db/restore-postgres-shadow.ps1`
- Objetivo: restaurar sobre una base sombra (`travel_shadow`) sin tocar la productiva.

Ejemplo:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\db\restore-postgres-shadow.ps1 -BackupFile .\backups\postgres\daily\travel-20260325-010000.dump
```

## Objetivos operativos de esta fase
- `RPO`: 24h
- `RTO`: 4h

## Checklist de cierre
- Backup full diario ejecutando sin error.
- Restore exitoso en `travel_shadow`.
- API levantando y consultando datos desde una base restaurada.
