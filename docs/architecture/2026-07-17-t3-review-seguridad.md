# T3 (eje de facturación gana "FullyReturned") — Review de seguridad / riesgo de datos

Fecha: 2026-07-17. Alcance: `git diff` sin commitear (6 archivos). Solo lectura/derivación, sin migración.
Diseño de referencia: `docs/architecture/2026-07-17-modelo-estados-derivados-DISENO-implementacion.md` §T3.

## Veredicto: OK — SIN BLOQUEANTES

El nuevo valor `FullyReturned` es puramente de **rótulo/visualización**. No habilita ni bloquea
ninguna acción de negocio, y no puede mentir sobre un comprobante rechazado/anulado por ARCA.

---

## Riesgo (1) — ¿el valor nuevo HABILITA acciones indebidas? → NO

- El eje derivado (string `InvoicingStatus`: `NotInvoiced`/`PartiallyInvoiced`/`FullyInvoiced`/`FullyReturned`)
  **no tiene NINGÚN consumidor backend que lo lea para decidir**. Grep de `.InvoicingStatus` y
  `ReservaInvoicingStatus.<valor>` en `src/**/*.cs` (excluyendo tests) devuelve solo:
  - 2 defaults de DTO (`ReservaDto.cs:460`, `ReservaListDto.cs:82`),
  - 2 escrituras (`ReservaService.cs:2405` lista, `ReservaService.cs:2557` detalle).
  Cero lecturas en gates, jobs o candados. Es un campo de salida write-only para los chips del front.
- El gate real de emisión de factura (`InvoiceService.CreateAsync` → `ReservaCapabilityPolicy.For(...).CanInvoiceSale`,
  `InvoiceService.cs:372-379`) decide por **`reserva.Status` + `reserva.Balance`**, NO por el eje derivado.
  El array `ActiveInvoicingStatuses` (`InvoiceService.cs:68`) es una **colisión de nombres**: son los
  estados operativos `{Confirmed, Traveling, Closed}` (`ReservaCapabilityPolicy.InvoiceableStatuses`),
  no el eje T3.
- La capacidad de **re-facturar** una reserva `FullyReturned` la gobierna el cuadre `Disponible`
  (= `vendido − FacturadoNeto`). Una reserva `FullyReturned` tiene `FacturadoNeto ~ 0`, luego
  `Disponible = vendido`. Ese valor era **idéntico ANTES del cambio** (cuando caía en `NotInvoiced`):
  el diff solo agrega `BrutoEmitido` como entrada del rótulo; no toca `FacturadoNeto`, `Disponible`
  ni `Excedido`. Ningún gate cambia de comportamiento: `NotInvoiced` y `FullyReturned` se comportan
  igual frente a toda acción backend. No hay re-facturación ni re-emisión nueva habilitada.
- `BrutoEmitido` (campo nuevo del record) solo lo consume `ReservaInvoicingStatus.Derive` para el
  rótulo (`ReservaService.cs:2405,2557`). No alimenta ningún gate.

## Riesgo (2) — ¿BrutoEmitido puede clasificar mal un comprobante rechazado/anulado por ARCA y mentir el rótulo? → NO

- `BrutoEmitido` suma **solo comprobantes con CAE firme** (`Resultado == "A"`) que sean Factura o ND:
  - Detalle: `Calculate` suma `line.IsLive && IsInvoiceOrDebitNote(...)` y `IsLive` =
    `CountsInNetBilled(i.Resultado)` (`ReservaService.cs:2546`).
  - Lista: la query filtra `invoice.Resultado == "A"` y suma los no-NC (`ReservaService.cs:2381,2392-2394`).
  - `CountsInNetBilled(resultado) => resultado == "A"` (`ReservaInvoicingCuadreCalculator.cs:156-157`):
    misma semántica "vivo" en ambos caminos.
- Un comprobante **RECHAZADO por ARCA** (`Resultado="R"`, PENDING o null) queda **excluido del bruto**
  en ambos caminos → `bruto=0` → `NotInvoiced`. No miente "Facturada y devuelta".
- Un comprobante **ANULADO pero con CAE aprobado** (`Resultado="A"`, luego neteado por su NC) SÍ suma
  en el bruto → `FullyReturned`. Esto es correcto: hay rastro fiscal real e irreversible (CAE emitido +
  su NC). Es exactamente la mentira #2 que T3 corrige. Cubierto por
  `BrutoEmitido_FacturaAnuladaMasNC_NoBajaConLaNC` y `FacturaTotalDevueltaPorNC_..._EsFullyReturned`.

## No-bloqueantes / observaciones

- **Divergencia potencial de tipos-NC lista vs detalle (pre-existente, no regresión de T3):** la lista
  usa el array hardcodeado `creditNoteTipos = {3,8,13,53}` (`ReservaService.cs:2369`) mientras el detalle
  usa `InvoiceComprobanteHelpers.Categorize`. Ya era así para `FacturadoNeto`; `BrutoEmitido` replica el
  mismo split. Si ambos conjuntos de tipos-NC se desincronizaran, lista y detalle podrían mostrar chips
  distintos para la misma reserva. Recomendación: unificar el criterio de tipos-NC (o test que fije que
  `{3,8,13,53}` == categorías NC del helper). No bloquea.
- **v1 escalar:** el eje deriva de `TotalSale`/`FacturadoNeto` escalares (sin desglose por moneda). Una
  reserva multi-moneda con NC total en una moneda y factura viva en otra podría, en teoría, cuadrar a
  neto~0 escalar; es una limitación conocida y declarada del carril (decisión H4), no introducida por T3.

## No verificado

- No corrí la app real ni la suite (unit/integración Postgres del CI). El razonamiento es por lectura de
  código y del diff. Los tests nuevos (`ReservaInvoicingStatusTests`, `ReservaInvoicingCuadreCalculatorTests`)
  cubren neto~0/bruto>0, neto negativo, ND, no-vivo y NC parcial, pero **su verde en CI queda por confirmar**.
