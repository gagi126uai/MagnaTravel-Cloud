# FC1.3 Fase 2 — Checklist de deploy

> Fecha: 2026-05-28
> Estado: **F2.2 lista para deploy** (101/101 tests verde en VPS, flag apagado por default).
> Para vos cuando vayas a deployar — segui el orden, no salteés pasos.

---

## Lo que vas a deployar (3 migraciones, en este orden)

1. **`Fase2_M0`** (F2.0) — Agrega 5 settings de configuración y un flag (incluido el "interruptor" `EnablePartialCreditNoteRealEmission`, que arranca **apagado**).
2. **`Fase2_M1`** (F2.1) — Crea las columnas de la "ficha de montos" de la NC parcial (FiscalLiquidation_*) + dos controles de cuadre. **Incluye un backfill que llena los casos viejos.**
3. **`Fase2_M1b`** (F2.2 Etapa 0) — Crea la tabla `ArcaIdempotencyKeys` (anti-doble-emisión) + columnas de moneda en Invoice (MonId/MonCotiz, hoy inertes con default "PES"/1).

> Las migraciones SOLO crean estructura y, en M1, hacen el backfill defensivo. **No emiten nada a la AFIP** todavía. El flag apagado lo garantiza.

---

## PASO 1 — Prevalidación ANTES de aplicar las migraciones (NO SE PUEDE SALTEAR)

La migración `Fase2_M1` tiene un control interno: si encuentra datos viejos rotos, **aborta sola** (mejor que dejar columnas a medias). Para no descubrir el problema en medio del deploy, corré el script de chequeo **contra una copia/dump de la base** antes de tocar nada en producción.

### A. Contra dump de **staging**

```sql
-- Conectate al dump/réplica de staging (psql, pgAdmin, DBeaver, lo que uses)
\i tools/sql/fase2-m1-prevalidation-metadata.sql
```

- **0 filas** → seguir adelante.
- **>0 filas** → **PARÁ**. Revisá fila por fila con el id que da el script y corregí ANTES de continuar. Repetí el script hasta que dé 0.

### B. Contra dump de **producción**

Repetí lo mismo contra un dump de producción. Mismo criterio (count=0). Anotá el resultado en la grilla de abajo.

### C. Registro (completá al correrlo)

| Ambiente | Fecha corrida | Count | IDs (si > 0) | Notas |
|---|---|---|---|---|
| Staging | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ |
| Producción | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ |

Recién con **count = 0 en los dos** podés pasar al PASO 2.

---

## PASO 2 — Aplicar las 3 migraciones en el VPS

Antes de aplicar, verificá UNA cosa de infraestructura:

### Pre-check: versión de Postgres

```bash
ssh tu-vps
psql -U tu_usuario -d tu_db -c "SELECT version();"
```

- **Postgres ≥ 11**: todo bien, las columnas con DEFAULT se agregan al instante.
- **Postgres < 11**: avisame antes de seguir — la migración `M1b` agrega columnas a `Invoices` con NOT NULL DEFAULT y en versiones viejas eso reescribe toda la tabla (puede tardar y bloquear). En Postgres 11+ es instantáneo.

### Aplicar las migraciones

Las migraciones **no corren solas en el arranque** (lo confirma el log: *"Database migrations skipped on startup. Run `dotnet TravelApi.dll --migrate-only` before deploy."*). Hay que correrlas a mano:

```bash
# En el VPS, en el directorio del proyecto:
cd /home/MagnaTravel-Cloud
git pull   # asegurate de estar en main al día
dotnet TravelApi.dll --migrate-only
```

Eso aplica **en orden** las pendientes: M0 → M1 → M1b. Verificá los logs:
- `Fase2_M0` debería pasar instantáneo (solo agrega columnas a settings).
- `Fase2_M1` corre el **backfill defensivo**. Si el script de prevalidación dio 0, esto pasa limpio y loguea `FC1.3.F2.1 paso 5.A OK` + un `NOTICE` con cuántas filas backfilleó.
- `Fase2_M1b` agrega `ArcaIdempotencyKeys` + columnas MonId/MonCotiz.

### Si algo falla

- Si la migración **aborta** con `RAISE EXCEPTION` en `Fase2_M1`: **es buena noticia** que falló antes de dejar cosas a medias. Significa que hay datos viejos rotos que el script standalone no detectó. Pasame el mensaje exacto del error y lo miramos.
- Si falla por otra causa (conexión, permisos, etc.): la transacción se revierte y la base queda como antes. No hay riesgo.

---

## PASO 3 — Antes de PRENDER el "interruptor" de emisión real

⚠️ Hasta que prendas `EnablePartialCreditNoteRealEmission=true`, **no se le manda nada a la AFIP**. Todo el flujo está listo pero "dormido". Antes de prender, falta esto:

### A. Firma del contador (las preguntas concretas)

Pasale al contador estas 2 (o 3) preguntas — son las que el experto fiscal marcó como necesarias antes de habilitar:

1. **Concepto del comprobante en una NC parcial de turismo:** ¿siempre va a ir "Servicios" (código 2), o puede ir "Productos" / "Productos y Servicios" según el caso? Hoy el sistema lo manda fijo en "Servicios".
2. **Fechas de servicio en la NC parcial:** ¿deben ser las del comprobante original (período facturado) o se acepta la fecha del día en que se emite la NC? Hoy el sistema usa la fecha del día.
3. **(Solo informativo)** El "monto" de cada línea de la NC parcial es **bruto (con IVA incluido)** — esa decisión la tomaste vos el 2026-05-28 y el sistema ya está armado así. Que el contador lo confirme por escrito para tener el respaldo fiscal.

### B. Probar contra la AFIP de práctica (homologación)

Antes de prender en producción, probar **al menos un caso multi-línea con dos alícuotas mezcladas** contra el ambiente de homologación de la AFIP, y confirmar que el comprobante vuelve con CAE aprobado. Esto valida que el cuadre cierra en la realidad, no solo en los tests.

### C. Prender el flag

Una vez que tengas (A) y (B), prender el flag (`EnablePartialCreditNoteRealEmission = true`) en la base de producción. Recién ahí el sistema empieza a emitir NC parciales reales.

---

## PASO 4 — Después del primer deploy real (auditoría)

1. **Mirar los primeros 5-10 comprobantes** emitidos en producción: confirmar que el CAE vuelve OK, que los totales cuadran con lo que muestra la factura del cliente, y que el IVA discriminado por alícuota es coherente.
2. **Auditar reportes downstream que usan `InvoiceItem.ImporteIva` de NCs parciales**: con la decisión "bruto", el IVA por ítem de una NC parcial sale **más chico** que antes (extraído del bruto, no sumado al neto). Si tenés algún reporte/PDF/asiento que muestre IVA por ítem, revisalo. **La facturación normal no se ve afectada**.
3. Tener a mano `ForceArcaConfirmation` para el caso raro de "el POST llegó pero el sistema se cayó antes de actualizar" (manualmente resoluble — el experto técnico lo dejó documentado como deuda menor para F2.3).

---

## Resumen ultra-corto (para pegar en un post-it)

1. Correr `tools/sql/fase2-m1-prevalidation-metadata.sql` contra **staging** y **prod**. Tiene que dar 0.
2. `git pull` + `dotnet TravelApi.dll --migrate-only` en el VPS. Aplica M0, M1, M1b en orden.
3. Esperar firma del contador (Concepto, fechas, criterio bruto) + probar contra AFIP homologación con NC multi-línea.
4. Recién ahí prender `EnablePartialCreditNoteRealEmission=true` en la base.

---

## Diagrama mental: en qué estado quedan las cosas

```
HOY (después del deploy de las 3 migraciones, flag apagado):
  - Estructura nueva en la base: ✅
  - Lógica de emisión cargada: ✅
  - Probada contra Postgres real: ✅ (101/101)
  - Emite NC parcial real a AFIP: ❌ (flag apagado)

DESPUÉS DE PRENDER EL FLAG:
  - Cuando se apruebe una cancelación con NC parcial, el sistema le manda la NC real a la AFIP.
  - El flujo está protegido contra duplicados (no manda dos veces aunque se cuelgue).
  - Y se recupera solo si el POST a la AFIP viajó pero el sistema se cayó después.
```

---

> Si algo no encaja durante el deploy, **parar y avisar**. El flag te protege: nada se emite hasta que vos lo prendas.
