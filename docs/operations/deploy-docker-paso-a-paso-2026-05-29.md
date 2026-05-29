# 🐳 Deploy con Docker — paso a paso (2026-05-29)

> Para Gaston. Nada de esto prende nada nuevo: dejás todo listo y el sistema sigue facturando en pesos como hoy. Las 4 actualizaciones de base **solo agregan** (no borran). Las llaves nuevas vienen **apagadas**.

## ⚠️ Regla de oro
**NUNCA** uses `docker compose down -v`. Esa `-v` borra la base, los archivos subidos (MinIO) y la sesión de WhatsApp. Para reiniciar, usá `docker compose restart` o el `deploy.sh`. Listo.

## Tus contenedores (nombres reales)
`travel_db` (base Postgres), `travel_migrate` (aplica migraciones), `travel_api`, `travel_worker`, `travel_web`, `travel_whatsapp_bot`, `travel_rabbitmq`, `travel_minio`.

---

## PASO 0 — Entrar al servidor
```bash
ssh usuario@tu-servidor
cd /ruta/a/MagnaTravel-Cloud
```
(Reemplazá la ruta por la real donde está clonado el repo en tu servidor.)

## PASO 1 — Backup de la base (red de seguridad)
```bash
bash scripts/ops/backup-db.sh
```
Hace una foto completa de la base (`pg_dump` dentro de `travel_db`) en `backups/postgres/manual/`. Anotá el nombre del archivo que te imprime.

## PASO 2 — Traer el código de hoy
```bash
git pull
git log -1 --oneline
```
El último commit tiene que ser uno de hoy.

## PASO 3 — Deploy (construye + migra + levanta, todo seguro)
```bash
bash scripts/ops/deploy.sh
```
Este script ya hace todo en el orden correcto:
1. Chequea que el `.env` esté bien.
2. Reconstruye las imágenes con el código nuevo (incluido `migrate`).
3. Levanta base + cola + almacenamiento.
4. Corre `travel_migrate` y **espera** a que aplique las 4 migraciones. Si una falla, **para** y te muestra el error (no levanta la app a medias).
5. Levanta API + worker + web + whatsapp + backup.
6. Verifica que todo quedó sano.

**No usa `down -v`** → tus datos quedan intactos.

**Si las migraciones fallan:**
```bash
docker logs --tail=120 travel_migrate
```
Copiame ese error y lo vemos.

## PASO 4 — Correr los tests en el servidor
```bash
bash scripts/ops/run-tests-fc13.sh
```
Tiene que dar **`Exit code: 0`** (verde). Si no, no prendas ninguna llave y pasame el `test-results-fc13.log`.

## PASO 5 — Verificar que levantó bien
```bash
bash scripts/ops/check-prod.sh
docker compose ps
```
Tenés que ver los contenedores en `healthy`/`running`. Para mirar logs de uno:
```bash
docker compose logs --tail=200 api
```
(cambiá `api` por `worker`, `web`, etc.)

**Acá termina el deploy de hoy.** El sistema corre igual que ayer. Frená hasta tener homologación de ARCA + OK del contador.

---

## PASO 6 — Prender las llaves (MÁS ADELANTE, NO HOY)
> Solo después de homologar en ARCA + OK del contador. Prender esto cambia el comportamiento fiscal real.

### 6.1 Backup primero
```bash
bash scripts/ops/backup-db.sh
```
### 6.2 Entrar a la base y prender
```bash
docker compose exec db psql -U traveluser -d travel
```
(usuario `traveluser` y base `travel` salen del `.env`; si los cambiaste, ajustá.)

Ya adentro (prompt `travel=#`), primero mirás cómo están:
```sql
SELECT "EnableMultiCurrencyInvoicing", "EnablePartialCreditNoteRealEmission" FROM "OperationalFinanceSettings";
```
Y cuando tengas el OK, prendés lo que quieras:
```sql
UPDATE "OperationalFinanceSettings" SET "EnableMultiCurrencyInvoicing" = true;        -- facturar en dólares
UPDATE "OperationalFinanceSettings" SET "EnablePartialCreditNoteRealEmission" = true; -- nota de crédito parcial real
```
Salís con `\q`.
### 6.3 Reiniciar API y worker
```bash
docker compose restart api worker
```
(reinicia solo esos dos; no toca la base ni los archivos. Unos segundos de corte.)
### 6.4 Verificar
```bash
bash scripts/ops/check-prod.sh
```

Para **apagar** una llave: el mismo `UPDATE ... = false;` + `docker compose restart api worker`.

---

## Si algo sale mal
- Migración: `docker logs --tail=120 travel_migrate` → pasámelo.
- Restaurar la base al backup: `scripts/ops/restore-db.sh` (avisame antes y te confirmo el uso exacto).
- **Nunca** `down -v`.
