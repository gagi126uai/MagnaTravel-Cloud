# Review backend — Tanda 3 modelo de estados: eje de facturación gana "Facturada y devuelta" (FullyReturned)

Fecha: 2026-07-17
Revisor: backend-dotnet-reviewer
Alcance: diff sin commitear (working tree) contra HEAD `f74ce84`.
Spec: `docs/architecture/2026-07-17-modelo-estados-derivados-DISENO-implementacion.md` §T3 (regla 5, INV-048-07).

---

## 1. Veredicto

**Approved with comments (Aprobado con comentarios).**

La lógica de dominio es correcta y coincide byte a byte con la spec §T3
(`neto ≈ 0 && bruto > 0 → FullyReturned`; `neto ≈ 0 && bruto ≈ 0 → NotInvoiced`).
El árbol de decisión viejo (parcial/total/excedido) queda intacto, el epsilon no
cambió y todos los callers del record posicional compilan. No encontré ningún
riesgo de corrupción de datos, de plata, ni de contrato roto.

Lo que impide un "Approved" liso: **dos verdades potenciales entre el detalle y el
listado** que NO están cubiertas por ningún test, y una construcción LINQ nueva
(`.Where().Sum()` dentro del group-by) que **no está verificada contra Postgres**.
La lógica es correcta por inspección; el riesgo es de traducción SQL y de
alineación, y por el historial de "verde en unit / roto en prod" hay que cerrarlo
antes del deploy conjunto con T4.

---

## 2. Hechos verificados (por inspección de código; NO corrí build ni tests)

1. **BrutoEmitido en el dominio** (`ReservaInvoicingCuadreCalculator.cs:76-79,193-195`):
   suma sólo `IsLive && (Invoice | DebitNote)`. La NC nunca participa (ni suma ni
   resta). Misma puerta de "vivo" que el neto: `IsLive = CountsInNetBilled(Resultado)
   = Resultado == "A"` (`:156-157`, usado en detalle `:2546`).
2. **"Vivas" excluye lo correcto**: `Resultado == "A"` cuenta el CAE aprobado
   AUNQUE esté anulado (Succeeded), y EXCLUYE rechazado/pendiente/null. Es lo que
   queremos: una factura **rechazada por ARCA** no infla el bruto (no dispararía
   FullyReturned falso); una factura **anulada con su NC** sí cuenta en bruto y por
   eso muestra "Facturada y devuelta" en vez de "Sin facturar". Correcto contra el
   criterio de `Categorize` (`InvoiceComprobanteHelpers.cs:61-68`: Invoice
   {1,6,11,51}, DebitNote {2,7,12,52}, CreditNote {3,8,13,53}).
3. **Árbol de decisión intacto** (`ReservaInvoicingStatus.cs:80-89`): sólo se
   bifurcó la primera rama (`neto <= epsilon`); las ramas total (`neto >= vendido -
   epsilon`) y parcial quedaron idénticas, en el mismo orden. El caso excedido/
   over-invoicing sigue cayendo en FullyInvoiced (decisión H1, sin cuarto valor).
4. **Epsilon**: `const decimal epsilon = 0.005m` sin cambios; coincide en valor con
   `ReservaCollectionStatus.Epsilon = 0.005m` (`ReservaCollectionStatus.cs:65`).
   Misma tolerancia de centavo que antes. (Es una constante DUPLICADA en dos
   archivos, no compartida — ver Mejora M4; pre-existente.)
5. **Listado gatea igual el estado**: `FillInvoicingStatusForListAsync` filtra
   `invoice.Resultado == "A"` (`ReservaService.cs:2381`) ANTES de agrupar, para neto
   y bruto por igual. La exclusión por ESTADO de comprobante está perfectamente
   alineada con el detalle.
6. **N+1**: no hay N+1. El bruto se calcula en la MISMA query agrupada que el neto
   (`:2374-2395`, un solo `ToListAsync`), volcada a un diccionario en memoria. El
   `foreach` sobre `items` sólo hace lookups en el diccionario. Correcto.
7. **Compilación / callers del record posicional**: el campo nuevo `BrutoEmitido`
   se agregó AL FINAL (`:45`). Único constructor posicional (`:86-92`) actualizado.
   Los otros consumidores leen por nombre (`.FacturadoNeto`, `.Excedido`,
   `.Exceso`) — no rompen. Ambos callers de `Derive` (detalle `:2557`, listado
   `:2405`) pasan a 3 args. El deconstruct nuevo `var (facturadoNeto, brutoEmitido)`
   sobre el value-tuple del diccionario (`:2397-2404`) es válido. Compila por
   inspección (build NO ejecutado — ver No verificado).
8. **DTO**: `ReservaDto.InvoicingStatus` sigue siendo `string`, default
   `NotInvoiced`. El valor nuevo llega sin mapear al front (esperado por T4 — no es
   hallazgo, per instrucción).

---

## 3. Blocking issues

Ninguno que pueda **probar** como defecto. Ver la sección 4: los dos ítems de más
peso son riesgos no verificables desde acá (traducción SQL Postgres y alineación
detalle-vs-listado sin test). Por el mandato de "verificado de verdad" los dejo
como condición de deploy, no como aprobación ciega.

---

## 4. Non-blocking improvements (ordenadas por riesgo)

### M1 (la más importante) — `.Where().Sum()` en el group-by NO está verificado contra Postgres
`ReservaService.cs:2392-2394`:
```csharp
BrutoEmitido = byReserva
    .Where(invoice => !creditNoteTipos.Contains(invoice.TipoComprobante))
    .Sum(invoice => invoice.ImporteTotal)
```
El hermano `FacturadoNeto` (`:2386-2389`) usa la forma **ternario-adentro-del-Sum**
(`Sum(i => cond ? -x : x)`), que ya está probada en Postgres. El bruto estrena
`.Where(...).Sum(...)` sobre el `IGrouping`. EF Core 8.0.11 + Npgsql 8.0.11
*probablemente* lo traducen (`SUM(...) FILTER (WHERE ...)`), pero **no lo verifiqué
corriendo Postgres** y los unit tests nuevos son InMemory/puros — no tocan esta
query. Si no traduce, el endpoint del LISTADO de reservas revienta (500) en prod:
exactamente el patrón "verde en unit, roto en Postgres" que ya nos mordió.
**Fix recomendado (elimina el riesgo y espeja al hermano):**
```csharp
BrutoEmitido = byReserva.Sum(invoice =>
    creditNoteTipos.Contains(invoice.TipoComprobante) ? 0m : invoice.ImporteTotal)
```
Si se deja como está: **debe salir verde en el CI de integración Postgres antes del
deploy** (no alcanza con la suite unit local).

### M2 — Dos verdades en tipos DESCONOCIDOS (dato sucio) entre detalle y listado
- Dominio (`SignedNetAmount` / `IsInvoiceOrDebitNote`): un `TipoComprobante` fuera
  de los conjuntos conocidos → `Unknown` → **no** cuenta ni en neto ni en bruto (0).
- Listado inline: neto usa `creditNoteTipos.Contains(...) ? -x : x` y bruto usa
  `!creditNoteTipos.Contains(...)` → un tipo desconocido cuenta como **factura**
  (suma en neto y en bruto).

Divergencia SÓLO con tipos que no están en {1,2,3,6,7,8,11,12,13,51,52,53} y con
`Resultado == "A"` — o sea, dato realmente corrupto (un CAE aprobado siempre tiene
tipo conocido). Además la divergencia del NETO **ya existía** antes de T3; el bruto
nuevo sólo hereda la misma convención del listado (es internamente coherente dentro
del listado). Riesgo práctico bajo, pero es la clase de desalineo que el diseño
quiere erradicar. Recomendación: si se quiere estrictez, alinear el listado a usar
la lista blanca de tipos conocidos (Invoice ∪ DebitNote) para el bruto, en vez de
"todo lo que no es NC". Documentar la decisión si se acepta el tradeoff.

### M3 — El helper del dominio y el inline SQL duplican la lista de tipos
`creditNoteTipos = {3,8,13,53}` está inline en `ReservaService.cs:2369` (EF no
traduce `Categorize`, correcto). Pero es la 4.ta copia del mismo conjunto. Si
mañana ARCA agrega un tipo, hay que tocar N lugares. No es de esta tanda; sólo dejo
la nota de deuda.

### M4 — Epsilon 0.005 duplicado en dos archivos
`ReservaInvoicingStatus.cs` y `ReservaCollectionStatus.cs` declaran cada uno su
`0.005m`. Hoy coinciden (verificado). Un cambio futuro en uno solo los desincroniza
en silencio. Considerar una constante compartida. Pre-existente, no de esta tanda.

---

## 5. Security / data risks

Sin hallazgos. El cambio es puramente derivado (no persiste columna, no muta
estado, no toca autorización ni ownership). No hay exposición de datos nuevos: el
único valor nuevo es un string de estado ("FullyReturned") que T4 pintará. No se
loguea nada sensible. No hay migración.

---

## 6. Missing tests

1. **(recomendado fuerte) Test de integración del LISTADO** para el camino
   FullyReturned. Hoy los 8 tests nuevos son puros (`Derive` + `Calculate`) — NO
   ejercitan `FillInvoicingStatusForListAsync` ni su proyección SQL. La spec §T3
   pide una caminata E2E ("factura → Facturada total; NC total → Facturada y
   devuelta; asegurar que NO vuelve a Sin facturar"), pero está sin automatizar.
   Un test de integración Postgres que siembre factura + NC total y asegure
   `InvoicingStatus == FullyReturned` en el listado cerraría **a la vez** M1
   (traducción SQL) y la alineación detalle-vs-listado. Este es el test que más
   valor agrega.
2. **(menor) Borde del epsilon del BRUTO**: hay test con bruto 0.004 (→ NotInvoiced)
   y bruto 1000 (→ FullyReturned), pero ninguno justo por encima del umbral (p.ej.
   bruto 0.006, neto 0 → debería ser FullyReturned). Cubre la rama `brutoEmitido >
   epsilon` en su borde.

Sobre "los 15 nuevos": conté 4 tests nuevos en `ReservaInvoicingCuadreCalculatorTests`
(bruto sin comprobantes, factura anulada+NC no baja el bruto, ND suma, no-vivo no
suma) y 3 nuevos en `ReservaInvoicingStatusTests` (FullyReturned neto 0, neto
negativo, neto ~0 por redondeo) + 1 reconvertido (NC parcial sigue Partial). Los
bordes pedidos SÍ están: **neto negativo** (`:96-102`), **redondeo** (`:106-113`),
**parcial con NC** (`:117-124`). La cobertura de la función pura es sólida; lo que
falta es el nivel SQL/listado (ítem 1).

---

## 7. Domain concerns

- La regla 5 quedó bien encapsulada: FullyReturned se activa **sólo** cuando el
  neto quedó en ~0. Una NC PARCIAL (neto > epsilon) sigue en "Facturada en parte"
  aunque el bruto sea mayor — correcto y con test (`:117-124`). No hay riesgo de que
  una anulación parcial "esconda" facturación viva.
- Coherencia con la mentira #2 (INV-048-07): la factura anulada (Succeeded, CAE "A")
  sigue sumando al bruto y su NC no lo baja → nunca vuelve a "Sin facturar". Es
  exactamente lo que pide el ADR-048.
- Limitación escalar v1 se mantiene (el estado se deriva de escalares vendido/neto/
  bruto, no por moneda). Consistente con `CollectionStatus`. No es regresión.

---

## 8. Suggested fixes (concretos)

1. Reescribir `BrutoEmitido` del listado a la forma ternario-adentro-del-Sum
   (M1) para espejar al hermano probado y sacar el riesgo de traducción:
   ```csharp
   BrutoEmitido = byReserva.Sum(invoice =>
       creditNoteTipos.Contains(invoice.TipoComprobante) ? 0m : invoice.ImporteTotal)
   ```
2. Agregar un test de integración Postgres del listado (M6.1) que asegure
   FullyReturned sobre factura+NC total, para blindar M1 y la alineación.
3. (Opcional, estrictez) alinear el bruto del listado a lista blanca de tipos
   conocidos (M2), o documentar el tradeoff de "todo lo no-NC = factura".

---

## 9. Commands that should be run (yo NO los corrí)

```bash
# Compilación + suite unit (confirma que los 3-arg y el record compilan):
dotnet build src/TravelApi.sln -c Release
dotnet test src/TravelApi.Tests/TravelApi.Tests.csproj --filter "FullyQualifiedName~ReservaInvoicing"

# CRÍTICO para M1: la suite de INTEGRACIÓN contra Postgres (la que corre en CI),
# que es la única que ejercita FillInvoicingStatusForListAsync con SQL real.
# Sin esto, la traducción de .Where().Sum() queda SIN verificar.
```

---

## 10. No verificado

- **NO corrí build ni ningún test.** "Compila" y "los tests pasan" son inferencias
  por lectura, no hechos observados. La afirmación de que el árbol compila se basa
  en que encontré y revisé todos los callers del record y de `Derive`.
- **NO verifiqué la traducción de `.Where().Sum()` contra Postgres** (M1). Es el
  riesgo abierto principal. No hay entorno Postgres en esta revisión (DB en VPS
  remoto; TestContainers no arrancan local).
- **NO verifiqué el comportamiento end-to-end** (facturar → NC total → ver el chip)
  en la app real; el front todavía no mapea el valor (T4), así que el camino
  completo no es observable aún.
- La alineación detalle-vs-listado para tipos conocidos la deduje de que ambos usan
  el mismo conjunto {3,8,13,53} y el mismo gate `Resultado == "A"`; NO existe un
  test que lo pruebe sobre datos reales.
