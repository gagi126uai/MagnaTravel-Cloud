# FC1.3 Fase 2 — Pre-validacion del backfill de FiscalLiquidation (F2.1.0)

> Estado: **PENDIENTE de correr + signoff de Gaston**
> Plan de referencia: `docs/architecture/plan-tactico-fc1-3-fase2.md` §FC1.3.F2.1.0 (lineas 323-377)
> Script: `tools/sql/fase2-m1-prevalidation-metadata.sql`

## Para que sirve (en criollo)

Antes de aplicar la migracion `Fase2_M1`, tenemos que asegurarnos de que **todos los
pedidos de aprobacion de NC parcial** (los `ApprovalRequest` tipo 11) tengan bien
guardada la "ficha" con los numeros del calculo (el `Metadata` JSON).

Es como mudarte de casa: antes de meter las cajas en el camion, revisas que ninguna
este rota o sin etiqueta. Si una caja esta rota, la arreglas ANTES de subirla, no
cuando ya estas en la ruta.

La migracion `Fase2_M1` copia esos numeros del JSON a columnas nuevas (backfill). Si
una "ficha" esta vacia, no es un objeto JSON valido, o le falta un dato critico
(`originalInvoiceAmount`, `fiscalAmountToCredit`, `currency`), la
migracion **se aborta sola** (paso 5.A, `RAISE EXCEPTION`) en vez de dejar columnas a
medio llenar. Este script detecta esas fichas rotas **antes** de correr la migracion,
asi no nos enteramos del problema en medio del deploy.

> Nota (I2): `computedAt` **NO** es una clave critica. El backfill toma el timestamp
> de la columna `BookingCancellations.LiquidationComputedAt`, no del JSON. Por eso ni
> la migracion ni este script lo chequean — hacerlo daria falsos positivos (frenaria
> un deploy que la migracion habria corrido sin problemas).

## Como se corre

1. Conectarse a un **dump / replica de STAGING** (psql, pgAdmin, DBeaver, etc.).
2. Ejecutar el archivo `tools/sql/fase2-m1-prevalidation-metadata.sql` entero.
3. Leer el resultado:
   - **0 filas** => OK, se puede aplicar `Fase2_M1` en staging.
   - **>0 filas** => REVISAR CASO A CASO. NO aplicar la migracion todavia.
4. Repetir **todo** contra un **dump de PRODUCCION**.
5. Completar la tabla de resultados de abajo (fecha + count + IDs si los hubo).
6. Pedir signoff explicito de Gaston: "count = 0, podemos avanzar".

## Como interpretar el output

El script devuelve, por cada fila problematica, su `id` (Id del `ApprovalRequest`) y
la `razon`:

| razon | Que significa | Como se arregla |
| --- | --- | --- |
| `METADATA_VACIO` | El `Metadata` esta null o vacio. | Reconstruir el JSON desde el `AuditLog` del submit, o descartar si el BC fue abortado. |
| `METADATA_NO_OBJETO` | El `Metadata` no es un objeto JSON (es un array, un string suelto, etc.). | Corregir el JSON a un objeto valido. |
| `FALTA_originalInvoiceAmount` | Falta la clave del monto original. | Rellenar la clave desde el AuditLog o el Invoice origen. |
| `FALTA_fiscalAmountToCredit` | Falta el monto fiscal a acreditar. | Idem. |
| `FALTA_currency` | Falta la moneda. | Idem (default seguro: `ARS`). |

> Nota: las claves `cancellationAmount`, `operatorPenaltyAmount`, `computedAt`, etc.
> NO son criticas; la migracion las puede dejar NULL (o tomarlas de otra fuente) sin
> romper el CHECK de suma. En particular `computedAt` se toma de
> `BookingCancellations.LiquidationComputedAt`, no del JSON.

## Resultado esperado

**Cero filas.** El serializer de Fase 1 (`SubmitForReviewAsync`) escribe
`schemaVersion=1` con todas las claves. Si aparece algo, casi seguro es un BC editado a
mano o un dato de test que llego a prod — investigar antes de avanzar.

## Tabla de resultados (completar al correr)

### Staging

| Fecha corrida | Count filas problematicas | IDs (si > 0) | Quien corrio | Notas |
| --- | --- | --- | --- | --- |
| _(pendiente)_ | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ |

### Produccion

| Fecha corrida | Count filas problematicas | IDs (si > 0) | Quien corrio | Notas |
| --- | --- | --- | --- | --- |
| _(pendiente)_ | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ | _(pendiente)_ |

## Signoff

- [ ] Staging: count = 0 verificado.
- [ ] Produccion: count = 0 verificado.
- [ ] Signoff de Gaston: "count = 0, podemos avanzar a aplicar Fase2_M1".

> Si cualquiera de los counts fue > 0, abrir una tarea por cada fila para limpiarla a
> mano ANTES de aplicar la migracion, y volver a correr el script hasta que de 0.
