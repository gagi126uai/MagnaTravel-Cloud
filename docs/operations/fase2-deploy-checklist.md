# FC1.3 Fase 2 — Checklist de deploy (Docker)

> Fecha: 2026-05-28
> Estado: **F2.2 lista para deploy** (101/101 tests verde en VPS, flag apagado por default).
> Para vos cuando vayas a deployar — segui el orden, no salteés pasos.
>
> **Contexto: usás Docker.** Postgres corre en el contenedor `travel_db` (postgres:16),
> las migraciones las aplica el contenedor `travel_migrate` (one-shot job), y el deploy
> completo lo dispara `scripts/ops/deploy.sh`. Todos los comandos de abajo se ejecutan
> desde el directorio del repo en el VPS.

---

## Antes de empezar

Conectate al VPS y andá al directorio del repo (donde está `docker-compose.yml`).

```bash
ssh tu-vps
cd ~/MagnaTravel-Cloud      # o donde tengas el repo en el VPS
git pull                     # asegurate de tener al menos commit 0b4455d
docker compose ps            # confirmá que travel_db esta Up (healthy)
```

Si `travel_db` no está prendido: `docker compose up -d db` y esperá unos segundos.

---

## PASO 1 — Prevalidación SQL (chequeo previo, NO modifica nada)

El script `tools/sql/fase2-m1-prevalidation-metadata.sql` es **solo lectura** (un `SELECT`
que detecta si hay datos viejos rotos que harían abortar la migración M1). No modifica
nada de la base. **Es seguro correrlo directo contra producción.**

### Comando

```bash
docker compose exec -T db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" < tools/sql/fase2-m1-prevalidation-metadata.sql
```

Qué hace cada parte:

- `docker compose exec -T db` → entra al contenedor `db` sin abrir terminal interactiva (`-T` es necesario para pipear archivos).
- `psql -U $POSTGRES_USER -d $POSTGRES_DB` → se conecta como el usuario de tu `.env` a la base.
- `< tools/sql/...` → le pasa el script por la entrada estándar.

### Cómo leer el resultado

- **Sin filas** (o `(0 rows)` al final) → todo limpio, pasá al PASO 2.
- **Una o más filas con `id` y `razon`** → **PARÁ ACÁ**. Cada fila es un pedido de aprobación viejo con datos rotos. Pasame el listado completo antes de seguir.

### Tabla de registro (completá al correrlo)

| Fecha corrida | Count de filas | IDs (si > 0) | Notas |
|---|---|---|---|
| _(pendiente)_ | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ |

---

## PASO 2 — Aplicar las 3 migraciones

### Opción A (recomendada): deploy completo con tu script

`scripts/ops/deploy.sh` hace TODO el flujo correcto: build de las imágenes, levanta `db`
si no está, corre el contenedor `migrate` (aplica todas las migraciones pendientes con
`dotnet TravelApi.dll --migrate-only`), espera a que termine, **chequea el código de
salida** del contenedor y aborta si falla, y recién después levanta `api` y `worker` con
el código nuevo.

```bash
bash scripts/ops/deploy.sh
```

Si la migración falla, el script aborta y te muestra los últimos 80 renglones del log
de `travel_migrate`. La transacción se revierte sola — la base queda como antes.

### Opción B (quirúrgica): solo correr las migraciones sin reiniciar api/worker

Si querés aplicar las migraciones **sin redeploy completo** (raro, pero válido para
probar las migraciones aisladas primero):

```bash
# Rebuild solo la imagen del migrate (toma el código nuevo del git pull):
docker compose build migrate

# Correr el migrate (one-shot job):
docker compose up -d --force-recreate --no-deps migrate

# Esperar a que termine y leer el código de salida:
docker wait travel_migrate

# Ver el log:
docker logs travel_migrate
```

Después de esto, si querés tomar también el código nuevo de api/worker:

```bash
docker compose build api worker
docker compose up -d --force-recreate --no-deps api worker
```

### Cómo leer el log de `travel_migrate`

- **OK**: vas a ver mensajes tipo `Applied migration: Fase2_M0_AddFc13Phase2Settings`,
  `Applied migration: Fase2_M1` (con NOTICEs `FC1.3.F2.1 paso 5.A OK` y `backfill done.
  backfilled=N orphan_skipped=0`), `Applied migration: Fase2_M1b_AddArcaIdempotencyKeysAndInvoiceCurrency`,
  y al final `Done.`
- **Aborto**: vas a ver `RAISE EXCEPTION` con `FC1.3.F2.1 backfill ABORTED: N
  ApprovalRequests tipo 11 con Metadata invalido...`. Significa que el PASO 1 dejó pasar
  algo. Pegame el log y lo vemos. La base no se modificó.

> **Recomendación**: usá Opción A (`bash scripts/ops/deploy.sh`). Tiene los chequeos
> armados, es lo que ya está probado.

---

## PASO 3 — Verificación post-migración

```bash
# Estado de todos los contenedores. Esperás "Up" y "(healthy)" en api, worker, db.
docker compose ps

# El check automatico que ya tenes:
bash scripts/ops/check-prod.sh
```

Verificá que las 3 migraciones quedaron registradas:

```bash
docker compose exec db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c '
  SELECT "MigrationId" FROM "__EFMigrationsHistory"
  WHERE "MigrationId" LIKE %Fase2%
  ORDER BY "MigrationId";
'
```

Tenés que ver 3 filas (M0, M1, M1b). Si no aparecen las 3, algo se aplicó a medias —
avisame.

---

## PASO 4 — ANTES de prender el flag (signoff fiscal + homologación)

Hasta acá las migraciones están aplicadas pero **el flag sigue apagado**, así que
el sistema no le manda nada a la AFIP (hace fallback al flujo FC1.2 viejo, como hasta
ahora). Antes de prender el flag, dos cosas:

### A. Preguntas al contador

Pasarle las 3 preguntas y guardar las respuestas por escrito (mail, WhatsApp, lo que sea
trazable):

1. **¿En una NC parcial de turismo, el campo "Concepto" del comprobante AFIP siempre va
   como "Servicios" (código 2), o puede variar según el caso?** Hoy el sistema lo manda
   fijo en "Servicios".
2. **¿Las "fechas de servicio" de la NC parcial deben heredarse del comprobante original
   (período facturado) o sirve usar la fecha del día?** Hoy usa la fecha del día.
3. **(Confirmación por escrito)** El "monto" de cada línea de la NC parcial se interpreta
   como **bruto con IVA incluido** (decisión 28/05). Pedí confirmación por escrito.

Si las respuestas 1 y 2 son las que asumimos hoy, podés pasar al B sin tocar código. Si
alguna es distinta, parar y avisar antes de prender el flag.

### B. Probar contra AFIP homologación

Probar al menos **un caso con multi-alícuota** (factura con ítems 10.5% + 21%) contra el
ambiente de homologación de AFIP. Cancelar parcialmente, emitir NC, confirmar que vuelve
**CAE aprobado** y los totales cuadran.

Si vuelve aprobado: listo. Si vuelve rechazado por "cuadre de totales": **NO prendas en
prod**, pegame el código de error.

### Tabla de registro

| Pregunta contador | Respuesta (fecha) | Necesita ajuste? |
|---|---|---|
| Concepto siempre Servicios? | _(pendiente)_ | _(pendiente)_ |
| Fechas heredan del original? | _(pendiente)_ | _(pendiente)_ |
| Confirma BRUTO por escrito? | _(pendiente)_ | _(pendiente)_ |
| Homologación multi-alícuota OK? | _(pendiente)_ | _(pendiente)_ |

---

## PASO 5 — Prender el flag

Una vez confirmados A y B:

```bash
docker compose exec db psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -c '
  UPDATE "OperationalFinanceSettings"
  SET "EnablePartialCreditNoteRealEmission" = true;
'
```

Te tiene que devolver `UPDATE 1`.

Reiniciá `api` y `worker` para que tomen el flag fresco (aunque se relee dinámico, mejor
asegurar):

```bash
docker compose restart api worker
```

---

## PASO 6 — Después de prender (auditoría)

1. **Mirá los primeros 5-10 comprobantes** emitidos en producción: que el CAE vuelva OK y
   los totales cuadren con lo que el cliente ve.
2. **Reportes con "IVA por ítem" de NCs parciales**: con la decisión BRUTO, el IVA por
   ítem sale **más chico** que antes (se extrae del bruto, no se suma al neto). Si tenés
   reportes que dividen `ImporteIva / Total` esperando ~0,21, ahora va a dar ~0,17.
   **La facturación normal NO se ve afectada**, solo las NCs parciales nuevas.
3. Si alguna NC quedó a medias (POST viajó pero el sistema se cayó antes de actualizar
   internamente), usá `ForceArcaConfirmation` desde el panel admin para resolverla a mano.

---

## Si algo se rompe

| Síntoma | Qué hacer |
|---|---|
| `travel_migrate` aborta con `RAISE EXCEPTION` | Pegame el mensaje completo. La base no se modificó. |
| `docker compose ps` muestra `api` o `worker` en `unhealthy` | `docker logs travel_api --tail 100` (o `travel_worker`) y pegame los últimos renglones. |
| AFIP rechaza la primera NC parcial real | Apagá el flag (UPDATE ... SET ... = false), reiniciá `api worker`, pegame el código de error AFIP. El sistema vuelve a fallback FC1.2. |
| Sospechás que algo quedó mal | Tenés backups diarios en `./backups/postgres/daily/`. Restaurar: `bash scripts/ops/restore-db.sh <archivo-dump>`. |

---

## Resumen mental

```
HOY (antes del deploy):
  - Codigo nuevo en main, no aplicado en VPS.
  - 3 migraciones pendientes (M0, M1, M1b).
  - Flag apagado por default.

DESPUES DEL DEPLOY (paso 2 completado):
  - 3 migraciones aplicadas en la base.
  - api y worker corriendo con el codigo nuevo.
  - Flag SIGUE APAGADO -> sistema usa fallback FC1.2 (NC TOTAL), no emite NC parcial real.
  - Cero riesgo fiscal hasta ahora.

DESPUES DE PRENDER EL FLAG (paso 5):
  - Cuando se apruebe una cancelacion con NC parcial, el sistema le manda la NC
    parcial real a la AFIP (con idempotencia + recuperacion automatica si algo se cuelga).
```
