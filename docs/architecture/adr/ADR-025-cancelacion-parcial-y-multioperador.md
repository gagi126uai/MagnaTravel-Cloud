# ADR-025 — Cancelación parcial (un servicio) y cancelación de reservas multi-operador

- **Status**: Decisiones de negocio APROBADAS por Gastón (2026-06-12); pasa a diseño técnico fino (`software-architect`) y luego construcción. Requiere `software-architect-reviewer` + firma del contador matriculado en los puntos fiscales marcados (Q-F1/Q-F2, plazo NC/ND) antes de PRENDER la emisión automática.

### Decisiones de Gastón selladas (2026-06-12)
1. **Cancelar un servicio NO crea estado nuevo de reserva** (Q-N1): el servicio queda cancelado/tachado + contador en el header ("1 de 3 servicios cancelado"); la reserva mantiene su estado vivo (no se inventa "parcialmente cancelada"). Coherente con ADR-020.
2. **Multi-operador → reembolso ÚNICO agregado** (Q-N3): lo que se recupera de cada operador se acumula como saldo a favor del cliente por moneda (`ClientCreditEntry`); un retiro cuando el cliente lo pide.
3. **NC de servicio limpio (sin penalidad) arranca en REVISIÓN MANUAL** (Q-F2): el sistema arma todo pero la emisión de la NC parcial la hace el operador a mano; se automatiza cuando el contador matriculado firme Q-F2 + homologación ARCA. NO se descongela el flag de NC parcial neto.
4. **Diferencia de cambio (factura USD / cobro ARS) ENTRA EN EL ALCANCE** (Q-N/F dólares): el diseño técnico debe incluir el asiento contable de la diferencia de cambio al cancelar. **Requiere `accounting-expert-argentina` para el asiento + validación del contador matriculado** (el criterio "se factura" ya está firmado; falta el asiento).
5. (Recordatorio, ya firmado el 2026-06-01) Penalidad del operador (pass-through) → solo NC total, sin ND; cargo propio de la agencia → ND propia. El sistema debe saber/clasificar, por penalidad, de quién es la plata.
6. (Pendiente firma contador, NO bloquea diseño) Q-F1: ¿una ND que junta cargos propios de varios operadores, o una ND por cargo? Default conservador hasta la firma: **una por cargo**.
- **Date**: 2026-06-12.
- **Author(s)**: travel-agency-accountant-argentina (análisis integrado). Modelo de datos heredado de `software-architect` (ADR-015).
- **Decisor de producto**: Gastón (único usuario; producto en desarrollo, sin clientes reales).
- **Reglas duras del dueño aplicadas**: **NO feature flags nuevos** (regla 2026-06-07: "esto es un producto", todo sale directo; rollback = `git revert` + migración `Down`). **No estimar tiempos** (este ADR dice QUÉ y en qué ORDEN, nunca cuánto tarda).

## Relación con ADRs previos

- **Supersede el alcance de [ADR-015](ADR-015-multi-operator-and-partial-cancellation.md) Fases 2 y 3** (multi-operador y parcial), que quedó EN PAUSA el 2026-06-02. ADR-015 **Fase 1** (inferencia de operador desde las 5 tablas tipadas + bloqueo INV-152) **se conserva tal cual** y es la base sobre la que este ADR construye. El modelo de datos central de ADR-015 (**BC-padre + líneas hijas**, §2) se adopta sin cambios; este ADR lo **completa** con las decisiones fiscales que ADR-015 dejó abiertas (§9 Q-F1/Q-F2) y lo reconcilia con el criterio del contador matriculado del 2026-06-01.
- **[ADR-002](ADR-002-cancellation-refund.md)**: refund del operador, estado `PendingOperatorRefund`, crédito diferido al cliente, deducción como "menor reembolso". Sigue vigente; este ADR lo baja a nivel **línea**.
- **[ADR-013](ADR-013-debit-note-on-cancellation-penalty.md) / [ADR-014](ADR-014-deferred-penalty-confirmation-debit-note.md)**: ND por penalidad **propia** de la agencia, `PenaltyStatus` Estimated/Confirmed. Se reusa a nivel **línea** (cada operador puede generar una penalidad propia distinta).
- **[ADR-009](ADR-009-partial-credit-note.md) / [ADR-010](ADR-010-partial-credit-note-receipt-reconciliation-inbox.md)**: motor de NC parcial real (`EnablePartialCreditNoteRealEmission`, `FiscalLiquidationCalculator`, `PartialCreditNoteIvaCalculator`, `EnqueuePartialCreditNoteAsync`, `ArcaIdempotencyKeys`). **DECISIÓN CENTRAL DE ESTE ADR (§4.3): el flag de NC parcial neto NO se descongela.** Se reusa la infra de *emisión* de NC, no la *fórmula de neteo*.
- **[ADR-020](ADR-020-ciclo-de-vida-reserva.md)**: estados de la reserva, `ConfirmedSale` vs `TotalSale` (saldo exigible por servicio confirmado), `Status` por servicio tipado. Base del estado resultante (§4.5).
- **[ADR-021](ADR-021-multimoneda-por-reserva.md)**: `ReservaMoneyByCurrency`, `SupplierBalanceByCurrency`, saldo por moneda. Base del impacto en plata por servicio (§4.4).
- **[ADR-022](ADR-022-circuito-erp-caja.md)**: libro de caja `CashLedgerEntry` (asiento inmutable por moneda, anular = contra-asiento), `ClientCreditEntry` (saldo a favor por moneda), `OperatorRefundReceived`, `ClientCreditWithdrawal`. Base del impacto en caja (§4.6).
- **[ADR-024](ADR-024-arca-datos-reales-spec-fiscal.md)**: datos fiscales reales / spec ARCA.

---

## 1. Contexto

### 1.1 Hechos verificados en el repo (2026-06-12, leídos)

- **`BookingCancellationService` cancela la RESERVA ENTERA y bloquea multi-operador.** `InferSingleSupplierIdAsync` (`BookingCancellationService.cs:3159-3188`): si hay 0 operadores tira error; si hay >1 tira `BusinessInvariantViolationException` **INV-152** ("la cancelación de reservas con varios operadores todavía no está disponible. Gestionala manualmente"). Con 1 operador se autorresuelve. La inferencia recorre las 6 fuentes (genérica + 5 tipadas) con dedupe por `SupplierId` (`GetDistinctSupplierIdsAsync:3209+`). **Esto es ADR-015 Fase 1, ya construido.**
- **No existe cancelación de un servicio suelto.** La cancelación setea el estado de la reserva completa a `Cancelled` / `PendingOperatorRefund` (`:1078`, `:1065` zona). No hay concepto de "servicio cancelado, resto vivo".
- **NC total funciona.** Al anular se emite NC del mismo tipo; tipos NC válidos en el código: 3 (NC A), 8 (NC B), 13 (NC C) (`:1045-1046`). El BC ancla `OriginatingInvoiceId` (una factura de venta al cliente) y `CreditNoteInvoiceId` (una NC).
- **NC parcial neto está CONGELADA** detrás de `EnablePartialCreditNoteRealEmission` (OFF en prod). Toda la infra existe: `FiscalLiquidationCalculator` (fórmula de neteo), `PartialCreditNoteIvaCalculator` (prorrateo IVA multi-alícuota), `EnqueuePartialCreditNoteAsync`, `ArcaIdempotencyKeys`, bandeja de reconciliación (ADR-010).
- **Motor de ND existe** en facturación general (`IsDebitNote`, tipos ARCA 2/7/12/52) pero **NO está conectado al flujo de cancelación** (`BookingCancellationService` nunca emite ND hoy). ADR-013 lo diseñó detrás de `EnableCancellationDebitNote` (OFF).
- **Plata por servicio ya existe**: cada servicio tipado tiene `SalePrice`/costo; `ConfirmedSale` vs `TotalSale` (ADR-020); `ReservaMoneyByCurrency` (ADR-021); libro de caja `CashLedgerEntry` + `ClientCreditEntry` por moneda (ADR-022).
- **Emisor**: Monotributo, Factura C con CAE real. RI es futuro.

### 1.2 Signoff del contador matriculado (2026-06-01) — ANCLA FISCAL DURA

Verificado en memoria `contador-matriculado-nc-total-mas-nd.md`. Lo que **pisa** cualquier diseño de NC parcial:

- **Penalidad en cancelación turismo = NC por el TOTAL de la factura + Nota de Débito (ND) por la penalidad.** Replica la cadena del operador. Ejemplo: pagado $100.000, penalidad $30.000 → NC $100.000 + ND $30.000, neto facturado $30.000. **NO** "NC parcial de $70.000 dejando viva la factura por $30.000".
- **Pass-through del operador = SOLO NC total, sin ND propia.** Si la penalidad se la queda el OPERADOR, la agencia minorista NO emite ND (no es su contraprestación). **ND propia SOLO cuando la penalidad es ingreso de la agencia** (cargo de gestión propio). Esto resuelve el riesgo fiscal central.
- **No emitir comprobante fiscal sobre penalidad ESTIMADA** (`PenaltyStatus` Estimated/Confirmed). La ND sale por el monto CONFIRMADO por el operador, con fecha real de emisión. Plazo: 15 días corridos desde la confirmación (RG 4540; discrepancia hábiles vs corridos → firma matriculado).
- **Tipificación de conceptos** (`CancellationConceptKind`): `OperatorPenaltyPassThrough` (NO ND) / `AgencyManagementFee` / `AgencyCancellationFee` (ND propia gravada) / `RealInsurancePremium` (revisión) / etc.

### 1.3 El problema de negocio

Dos huecos operativos reales:

1. **Cancelar un servicio dentro de un file, dejando el resto vivo** (ej. el cliente baja el traslado pero mantiene hotel + aéreo). Hoy imposible: la cancelación es todo-o-nada.
2. **Cancelar un file con varios operadores** (paquete dinámico: hotel de un operador, aéreo de otro, cada uno con su penalidad y su política de refund). Hoy bloqueado por INV-152.

Ambos comparten la misma causa raíz que ya diagnosticó ADR-015: **el modelo asume 1 reserva = 1 operador = 1 evento fiscal total.** La solución estructural también es la misma: bajar la multiplicidad (operadores, servicios) a una capa de **líneas** debajo de un único BC, preservando el ancla fiscal "1 factura de venta → 1 NC".

---

## 2. Decisión central de modelado (heredada de ADR-015 §2)

> **La NC al cliente es ÚNICA por factura de venta. La multiplicidad (operadores, servicios cancelados) vive en líneas hijas DEBAJO del BC, no en múltiples BC por reserva.**

Se mantiene **un único `BookingCancellation` por reserva** (INV-081 / UNIQUE intacto, cero migración destructiva) como aggregate root del evento, y se agregan **líneas** `BookingCancellationLine` — una por servicio cancelado. Cada línea lleva: `SupplierId`, referencia al servicio `(ServiceTable enum, ServiceId)`, `Scope` (Full/Partial-del-file), `LineSaleAmount`, `Currency`, `PenaltyAmount`/`PenaltyStatus`/`ConceptKind` (por operador), `RefundCap`/`ReceivedRefundAmount`.

**Por qué BC-padre + líneas y no N BC por reserva** (resumen; detalle en ADR-015 §2.1 y §8): preserva el ancla fiscal (el cliente le compró a la AGENCIA, no a los operadores → la NC es de la agencia, una sola sobre su factura); preserva INV-081 sin migración destructiva; modela parcial y multi-operador en la misma estructura; reusa ADR-002/009/013/014 bajándolos de "por BC" a "por línea" de forma aditiva.

**Anclas que NO se mueven:**
- **Fiscal hacia el cliente = nivel EVENTO** (factura única → NC única; ND única que puede sumar penalidades propias de varias líneas, sujeto a Q-F1).
- **Operador / refund / penalidad / pago a proveedor = nivel LÍNEA** (cada operador su circuito).

---

## 3. Diseño por casos — Caso A: cancelar UN servicio (resto del file vivo)

### 3.1 Qué pasa con el saldo del cliente

- La venta del servicio cancelado (`LineSaleAmount`, en su `Currency`) **sale** del saldo exigible de la reserva. El cálculo de saldo (`ReservaMoneyByCurrency` / `ConfirmedSale`, ADR-020/021) debe **excluir** los servicios marcados como cancelados, menos la penalidad retenida que sí queda a cargo del cliente.
- `Hecho verificado:` el saldo de la reserva se deriva de servicios + pagos (ADR-021/022); excluir un servicio cancelado es restar su `SalePrice`. **`Riesgo contable:`** hay que verificar en código el punto exacto donde el cálculo de saldo lee los servicios y agregar el filtro "no cancelado" — no inspeccionado a nivel implementación en este ADR (supuesto declarado).

### 3.2 Qué pasa con la deuda / pago al operador de ESE servicio

Depende de cuánto se le pagó al operador del servicio cancelado (circuito ADR-002 a nivel línea):
- **No se le pagó nada** → se cancela la deuda de esa línea contra el operador (`SupplierBalanceByCurrency` baja). Sin refund.
- **Se pagó (total o anticipo)** → la línea entra en `RefundCap` = lo pagado al operador, menos lo que el operador retenga como penalidad. Estado de la línea `PendingOperatorRefund` hasta que el refund efectivo entre (ADR-002 T2/T3). El refund se imputa a la **línea** cuyo `SupplierId` coincide (INV-126 reformulado a nivel línea).

### 3.3 Qué comprobante fiscal corresponde — DECISIÓN

`Hecho verificado (ancla matriculado §1.2):` cancelar un servicio = la porción de la factura de venta que pierde causa fiscal. El criterio del matriculado es **NC total + nueva factura por lo que queda** o, equivalente operativamente y preferido por el sistema, **NC parcial de emisión** que acredita la porción cancelada — PERO **sin la fórmula de neteo de penalidad** (la penalidad NO reduce la NC; la penalidad va por ND si es ingreso de la agencia, o queda dentro del pass-through del operador sin ND).

**Resolución de la tensión NC parcial vs NC total + nueva factura:**

| Situación de la línea cancelada | Comprobante | Por qué |
|---|---|---|
| Sin penalidad (devolución limpia de un servicio) | **NC parcial** por `LineSaleAmount` de ese servicio (reusa infra de emisión ADR-009: `EnqueuePartialCreditNoteAsync` + IVA prorrateado), asociada a la factura original (`CbtesAsoc`) | La factura sigue viva por los servicios no cancelados. No hay penalidad → no hay neteo → no contradice al matriculado. |
| Con penalidad **pass-through del operador** | **NC total + nueva factura** por los servicios que quedan vivos, SIN ND | Criterio matriculado R1: pass-through = solo NC total. La penalidad la factura el operador, no la agencia. |
| Con penalidad **propia de la agencia** (`AgencyManagementFee`/`AgencyCancellationFee`) | **NC parcial** por la porción cancelada **+ ND** por la penalidad propia (motor ADR-013, `PenaltyStatus=Confirmed`) | Matriculado: ND propia solo si la penalidad es ingreso de la agencia. La NC no se netea por la penalidad. |
| Factura A / RI / multimoneda compleja / tributos provinciales | **Revisión manual obligatoria** (clasificador ADR-009 reciclado) | Caso 8 del criterio NC: siempre a revisión. |

> **`Necesita confirmación profesional:` esta matriz por-servicio (NC parcial limpia para servicio sin penalidad) extiende el criterio del matriculado, que firmó el caso de cancelación TOTAL. Falta su firma para: (a) que la NC parcial de un servicio dentro de una factura mixta sea aceptable; (b) que la factura siga viva por el remanente sin reemisión. Esto es ADR-015 Q-F2.**

### 3.4 Estado resultante del file — DECISIÓN

- **Recomendación**: NO mover toda la reserva a `Cancelled`. El **servicio** se marca cancelado (cada tabla tipada ya tiene su `Status`, ADR-020); la reserva permanece en su estado vivo (Confirmada, En viaje, etc.) por el resto.
- Si la línea cancelada tiene refund pendiente del operador, **esa línea** queda en `PendingOperatorRefund`; la reserva NO entra en `PendingOperatorRefund` global (eso es para cancelación total).
- **`Necesita confirmación de Gastón (Q-N1):`** ¿querés ver un estado visible "parcialmente cancelada" en la reserva, o alcanza con que el servicio aparezca tachado/cancelado y la reserva siga como está? (Recomendación: el servicio cancelado + un contador "1 de 3 servicios cancelado" en el header; sin estado nuevo de reserva, para no chocar con ADR-020.)

---

## 4. Diseño por casos — Caso B: cancelar un file con VARIOS operadores

### 4.1 Cómo se cancela

Un único BC con **N líneas, una por servicio/operador**. Cada línea corre su propio sub-circuito ADR-002 (refund/penalidad/condición del operador). Esto reemplaza el bloqueo INV-152: ya no hay que elegir "un solo operador"; se cancelan todos los servicios afectados como líneas del mismo evento.

- **Refund por operador**: cada línea tiene su `RefundCap` (lo pagado a ESE operador menos su penalidad) y su `ReceivedRefundAmount`. INV-126 se reformula: el refund se imputa a la línea con el `SupplierId` que coincide.
- **Penalidad por operador**: cada línea con su `PenaltyAmount`/`PenaltyStatus`/`ConceptKind`. Operador A puede ser pass-through, operador B puede tener cargo propio de la agencia.

### 4.2 La parte fiscal (NC) — DECISIÓN

`Hecho verificado (ADR-015 §2 + matriculado):` **la NC es UNA SOLA sobre la factura de venta al cliente**, no por tramo/operador. El cliente le compró a la agencia; la dimensión "operador" es de pago a proveedores, no de la relación fiscal agencia↔cliente. Emitir N NC por operador sobre la misma factura es riesgo fiscal alto (NC fragmentada sin criterio) — descartado (ADR-015 Alt. D).

- **Cancelación total multi-operador** → **NC total** (como hoy). El detalle por operador vive en las líneas, invisible para ARCA.
- **ND**: cada línea con penalidad **propia de la agencia** contribuye. **`Necesita confirmación profesional (Q-F1):` ¿una ND única que suma penalidades propias de VARIOS operadores es correcta, o cada cargo propio necesita su propia ND?** ADR-013 hoy asume 1 penalidad por BC. Recomendación conservadora hasta la firma: **una ND por cada concepto/penalidad propia confirmada** (más comprobantes pero trazabilidad limpia operador-por-operador), no una ND agregada.

### 4.3 NO se descongela el flag de NC parcial neto — DECISIÓN CENTRAL

`EnablePartialCreditNoteRealEmission` esconde dos cosas distintas:
1. **La FÓRMULA de neteo** (`FiscalLiquidationCalculator`: NC = total − no reintegrables − penalidad, dejando viva la factura por la penalidad). → **NO se usa.** Contradice al matriculado (penalidad va por ND, no resta de la NC).
2. **La MAQUINARIA de emisión** de una NC por menos del total (`EnqueuePartialCreditNoteAsync`, `PartialCreditNoteIvaCalculator` prorrateando IVA multi-alícuota, `ArcaIdempotencyKeys`, bandeja ADR-010). → **SÍ se reusa**, para emitir la NC parcial *limpia* del Caso A (sin penalidad) y para la porción cancelada cuando hay ND propia.

**Qué falta para emitir NC parcial bien** (bloqueantes heredados de `fc13-f25-multimoneda-nc-parcial.md`, siguen abiertos):
1. Firma del contador a la matriz de §3.3 (NC parcial por servicio en factura mixta).
2. Homologación ARCA: NC con CAE real por menos del total + multi-alícuota cuadrada al centavo + `MonCotiz` 6 decimales aceptado.
3. **`Riesgo fiscal ALTO:`** fallback `MonCotiz=1` para facturas USD legacy sin snapshot (`BookingCancellationService ~2163-2165`). Una NC parcial sobre factura USD legacy saldría con `MonCotiz=1` (error fiscal grave). Debe ir a Failed/manual, no a 1.
4. Verificar que `ExchangeRateAtOriginalInvoice` del snapshot == `MonCotiz` mandado a ARCA en la factura original.

### 4.4 Impacto en plata por moneda (ADR-021)

`SupplierBalanceByCurrency` baja por cada línea cancelada en SU moneda. `ReservaMoneyByCurrency` baja la venta cancelada en su moneda. Multi-operador con monedas distintas (hotel USD + aéreo ARS) se maneja porque la plata ya es **por moneda** y las líneas llevan `Currency`. **`Riesgo:`** la NC al cliente es UNA en la moneda de la factura de venta; si la factura es ARS pero un operador era USD, la diferencia es de costo/refund (proveedor), no de la NC al cliente. Diferencia de cambio → §5.

### 4.5 Estado resultante

- **Total multi-operador** → reserva a `Cancelled` (o `PendingOperatorRefund` si hay refunds en vuelo de alguna línea). Igual que hoy pero las líneas trackean cada operador.
- **Parcial multi-operador** (cancelo el servicio del operador A, dejo vivo el de B) → §3.4: servicio cancelado, reserva viva.

### 4.6 Caja (ADR-022)

Cada refund efectivo del operador = `OperatorRefundReceived` → asiento `CashLedgerEntry` (ingreso de caja, por moneda) en la misma transacción (ADR-002 §2.3.2, no se rompe). El reintegro al cliente: o `ClientCreditEntry` (saldo a favor, diferido) o `ClientCreditWithdrawal` (devolución física → asiento de egreso). Por línea/operador, agregado al cliente.

- **`Necesita confirmación de Gastón (Q-N3):`** en multi-operador, ¿el cliente recibe **un** reembolso agregado (la agencia junta lo que recupera de cada operador y reintegra el total) o reembolsos separados a medida que cada operador devuelve? (Recomendación: agregado al saldo a favor del cliente por moneda; un retiro cuando el cliente lo pide. Es lo más simple y lo que ya modela `ClientCreditEntry`.)

---

## 5. Casos borde (integrados)

| Caso | Tratamiento |
|---|---|
| **Cliente NO pagó nada** | No hay refund al cliente. NC igual se emite (anula la obligación fiscal de la venta cancelada). Baja saldo exigible. ND solo si hay cargo propio confirmado. |
| **Cliente pagó parte** | NC por la porción cancelada. Lo cobrado que excede lo que queda exigible → saldo a favor del cliente (`ClientCreditEntry`, ADR-022) por moneda. NO caduca automáticamente (política cerrada). |
| **Cliente pagó todo** | NC + reintegro (saldo a favor o retiro físico). Menos penalidades retenidas. |
| **Servicio impago al operador** | Sin refund de esa línea; baja la deuda al operador. |
| **Servicio con anticipo al operador** | `RefundCap` de la línea = anticipo − penalidad del operador. Resto del circuito ADR-002. |
| **Servicio ya pagado total al operador** | `RefundCap` = pagado − penalidad. `PendingOperatorRefund` de la línea hasta el refund efectivo. |
| **Multimoneda (factura USD, cobró ARS)** | NC en la moneda de la factura original (USD) con el TC congelado del snapshot (no live). Diferencia entre TC de cobro (ARS) y TC de NC → **diferencia de cambio**. `Riesgo contable (heredado):` no verifiqué que exista código que genere el asiento de diferencia de cambio T0→T3; el `FiscalSnapshot` guarda los 3 TC pero no vi quién arma el asiento. **`Necesita confirmación profesional:` la diferencia de cambio SE FACTURA (firmado por matriculado 2026-06-01) — falta el asiento contable.** |
| **Penalidad estimada, operador confirma después** | No emitir ND sobre estimado (ADR-014). ND por el monto confirmado, fecha real, plazo desde confirmación. |

---

## 6. Tipificación de penalidades (reusa ADR-013, sin inventar)

Cada línea lleva `ConceptKind` con el enum ya definido por el matriculado (`CancellationConceptKind`):
- `OperatorPenaltyPassThrough` → la retiene el operador. **NO ND propia.** Solo NC total / menos refund al cliente.
- `AgencyManagementFee` / `AgencyCancellationFee` → ingreso de la agencia. **ND propia gravada** (Monotributo: ND C sin IVA discriminado; RI: ND A con 21% → futuro).
- `RealInsurancePremium` → revisión.

Y la tipificación de la deducción del operador (qué descuenta antes de devolver), que NO debe confundirse:
- `administrativeFee` / `tax` → reduce el crédito al cliente.
- **`withholding`** (retención fiscal del operador: IIBB/IVA/Ganancias) → **crédito fiscal a favor de la agencia. NO reduce el crédito al cliente.** Confundirlo con `administrativeFee` = error fiscal serio.

**Comisión del vendedor**: política tope cero — la deducción del operador puede llevar la comisión de la línea cancelada a CERO pero **nunca a negativo**; el exceso es pérdida de la agencia, nunca cargo al vendedor.

---

## 7. Modelo de datos y migraciones

- Tabla nueva `BookingCancellationLines` (FK a `BookingCancellations`, `xmin` concurrency, CHECK constraints). **Aditiva** (`Adr025_M1`), no toca tablas existentes ni INV-081.
- Backfill: 1 línea por BC histórico (el BC mono-operador = 1 línea con `bc.SupplierId`). Script SQL idempotente, **prevalidado contra dump** (convención dura del repo: probar migraciones con SQL crudo contra Postgres real, no InMemory — lección ADR-020 M2).
- Referencia al servicio: `(ServiceTable enum, ServiceId int)` (ADR-015 §6.3 opción a). Sin polimorfismo EF.
- **NO feature flags nuevos** (regla del dueño). El comportamiento sale directo. La migración aditiva no rompe el path mono-operador actual (que sigue funcionando con 1 línea).
- `bc.SupplierId` queda como denormalización del operador "principal" (compat) hasta decidir deprecarlo (Q-T1, no bloquea).

---

## 8. Invariantes

- **INV-081** (1 BC por reserva): intacto.
- **INV-126** reformulado: refund.SupplierId ∈ {lines.SupplierId}, imputado a la línea correcta con su `RefundCap`.
- **INV-152** (bloqueo multi-operador): se **levanta** al construir este ADR (ya no se gestiona a mano).
- **Nuevo INV (NC fiscal única)**: una sola NC y/o una factura viva por reserva; nunca N NC por operador sobre la misma factura.
- **Nuevo INV (no neteo)**: la penalidad NUNCA reduce el monto de la NC; va por ND (si es propia) o por menor refund (si es pass-through). Refleja el criterio matriculado.
- **Comisión vendedor** ≥ 0 siempre.

---

## 9. Riesgos integrados

- **`Riesgo fiscal ALTO`**: emitir NC parcial real (Caso A sin penalidad) sin la firma del matriculado a la matriz §3.3 y sin homologación ARCA. **Mitigación**: hasta la firma, el Caso A "servicio sin penalidad" rutea a **revisión manual** (no emite NC automática); el operador la emite a mano con NC total + factura por el resto.
- **`Riesgo fiscal ALTO`**: fallback `MonCotiz=1` en facturas USD legacy (§4.3.3). Debe ir a manual, no a 1.
- **`Riesgo fiscal medio`**: ND que suma penalidades de varios operadores (Q-F1 abierto). Mitigación: una ND por concepto hasta la firma.
- **`Riesgo contable`**: asiento de diferencia de cambio T0→T3 no verificado en código (§5).
- **`Riesgo contable`**: el filtro "excluir servicio cancelado" del cálculo de saldo no inspeccionado a nivel implementación (§3.1).
- **Seguridad/datos**: la clasificación de penalidad por línea y el levantamiento de INV-152 tocan cancelaciones/refunds/comprobantes → requiere `security-data-risk-reviewer` (4-eyes para overrides, no confiar en frontend para pertenencia del operador a la reserva).
- **Coordinación**: interactúa con ADR-020 (estados) y ADR-022 (libro de caja). Secuenciar.

---

## 10. Qué descongelar / qué falta (resumen)

- **Descongelar**: la **maquinaria de emisión** de NC parcial (no la fórmula de neteo) para el Caso A sin penalidad. Requiere firma matriculado + homologación ARCA + fix del `MonCotiz=1` legacy.
- **Mantener congelado**: `FiscalLiquidationCalculator` fórmula de neteo (contradice al matriculado).
- **Conectar**: motor de ND (ADR-013) al flujo de cancelación, a nivel línea, solo para cargo propio confirmado.
- **Levantar**: INV-152 (bloqueo multi-operador), reemplazado por líneas.
- **Construir**: tabla `BookingCancellationLines` + backfill + reformulación INV-126.

---

## Resumen para Gastón (decisiones a aprobar)

Gastón: hoy el sistema **no te deja cancelar un solo servicio** (es todo o nada) ni **una reserva con varios operadores** (te dice "gestionala a mano"). Esto es el plan para arreglar las dos cosas. Antes de que se programe nada, necesito que decidas estos puntos. Te dejo mi recomendación en cada uno.

**Cómo lo pienso, en criollo:** una reserva tiene una sola factura tuya al cliente (el cliente te compró a VOS, no a los hoteles). Entonces la nota de crédito siempre es UNA, tuya, aunque adentro haya tres operadores. Cada operador, por debajo, tiene su propia plata (qué le pagaste, qué penalidad te cobra, qué te devuelve). Eso lo manejo en "renglones" internos, que vos no ves como facturas separadas.

**1. Cancelar un solo servicio: ¿la reserva cambia de estado?**
Cuando cancelás (por ejemplo) el traslado pero dejás vivo el hotel y el aéreo: ¿querés ver la reserva marcada como "parcialmente cancelada", o alcanza con que el traslado aparezca tachado/cancelado y la reserva siga como está (Confirmada, etc.)?
**Mi recomendación:** que el servicio quede cancelado y en el encabezado diga algo tipo "1 de 3 servicios cancelado", sin inventar un estado nuevo de reserva (para no pelearme con el rediseño de estados que ya hicimos).

**2. Varios operadores: ¿un reembolso o varios al cliente?**
Si cancelás una reserva con hotel de un operador y aéreo de otro, y los dos te devuelven plata en momentos distintos: ¿le devolvés al cliente UN total cuando ya juntaste todo, o le vas devolviendo a medida que cada operador te devuelve?
**Mi recomendación:** que se le acumule como saldo a favor (por moneda) y se le devuelva de una cuando él lo pida. Es lo que ya tenés armado y lo más simple.

**3. La penalidad: ¿es plata tuya o del operador?** (esto define el comprobante)
Hay dos tipos de penalidad y se tratan distinto:
- **Penalidad del operador** (él se queda la plata): vos hacés solo la nota de crédito, sin nota de débito. No es ingreso tuyo.
- **Cargo tuyo** (vos te quedás un fee por la gestión): ahí sí va una nota de débito tuya.
Esto ya lo firmó tu contador el 1 de junio. **No hay decisión nueva acá, es para que lo tengas presente:** el sistema tiene que preguntar/saber, por cada penalidad, de quién es la plata.

**4. Cancelar un servicio sin penalidad: ¿emitimos nota de crédito automática por ese servicio?**
Si cancelás un servicio limpio (sin penalidad), lo técnicamente prolijo es emitir una nota de crédito SOLO por ese servicio y dejar viva la factura por el resto. **PERO esto necesita la firma de tu contador matriculado** (él firmó la cancelación total, no la de "un pedazo de la factura"). Hasta que firme, mi recomendación es que ese caso vaya a **revisión manual** (lo emitís vos a mano) en vez de que el sistema lo dispare solo. ¿De acuerdo en arrancar con revisión manual y automatizar cuando el contador firme?

**5. Dólares: confirmar el tema diferencia de cambio.**
Si facturaste en dólares y cobraste en pesos, cuando cancelás puede quedar una diferencia de cambio. Tu contador ya dijo que **eso se factura**. Lo que falta del lado del sistema es generar el asiento contable de esa diferencia (hoy no estoy seguro de que exista). ¿Confirmás que querés que esto entre en el alcance, o lo dejamos para después y lo anotás como pendiente con el contador?

**6. Multi-operador: ¿una nota de débito que junta todos los cargos tuyos, o una por cada uno?**
Si en una reserva con tres operadores vos cobrás un cargo propio por cada uno, ¿querés UNA nota de débito que sume los tres, o UNA por cada cargo? **Esto también necesita firma del contador** (es una pregunta fiscal). Mi recomendación hasta que firme: **una por cada cargo** (más papeles pero queda clarísimo de qué operador viene cada uno).

---

### Separación VERIFICADO / ASUNCIÓN-confirmar-contador / RIESGO

**VERIFICADO (código o firma previa del matriculado):**
- INV-152 bloquea multi-operador hoy; no existe cancelación de un servicio suelto; NC total funciona; NC parcial neto congelada; motor de ND existe pero desconectado del flujo de cancelación.
- Criterio matriculado: penalidad = NC total + ND; pass-through del operador = solo NC total sin ND propia; ND propia solo si la penalidad es ingreso de la agencia; no emitir sobre estimado.
- Plata por servicio y por moneda ya existe (ADR-020/021/022).
- Modelo BC-padre + líneas ya diseñado en ADR-015.

**ASUNCIÓN — confirmar con contador matriculado (firma):**
- Q-F2: NC parcial de UN servicio dentro de una factura mixta, dejando viva la factura por el resto (matriz §3.3).
- Q-F1: una ND que suma penalidades propias de varios operadores vs una por cargo.
- La diferencia de cambio se factura (ya firmado) pero falta definir/armar el asiento contable.
- Plazo NC/ND: 15 días corridos vs hábiles (discrepancia RG 4540 / RG 1415).

**RIESGO:**
- Emitir NC parcial real sin homologación ARCA y sin la firma → arrancar en revisión manual.
- Fallback `MonCotiz=1` en facturas USD legacy → debe ir a manual.
- Asiento de diferencia de cambio T0→T3 no verificado en código.
- Filtro "excluir servicio cancelado" del cálculo de saldo no inspeccionado a nivel implementación.

### Interacción necesaria con otros agentes
- **`software-architect` / `software-architect-reviewer`**: diseño detallado de `BookingCancellationLines`, reformulación INV-126, backfill, secuenciado con ADR-020/022.
- **Contador matriculado**: firma de Q-F1, Q-F2, diferencia de cambio, plazo NC/ND.
- **`security-data-risk-reviewer`**: levantamiento de INV-152, clasificación de penalidad por línea, 4-eyes.
- **`travel-agency-domain-expert`**: defaults de fee por las 5 dimensiones (antelación/monto/quién cancela/política operador/decisión vendedor) a nivel línea.

---
---

## Diseño técnico (software-architect, 2026-06-12)

> **Estado**: PROPUESTO. Pendiente `software-architect-reviewer`. Implementable por `backend-dotnet-senior` salvo lo marcado "detrás de firma del contador" (no se construye la emisión automática; sí el armado/borrador manual). Verificado contra el código real en `main` (HEAD `ea1150c` + working tree con `Adr025_M1` de vencimientos sin commitear).
>
> **Reglas duras aplicadas**: SIN feature flags nuevos (la NC parcial automática NO se prende: arranca en revisión manual, decisión #3). Migraciones aditivas, EF puro, sin SQL crudo (lección M2/`TravelFileId`). No hay estimaciones de tiempo: esto dice QUÉ y en qué ORDEN.

### DT.0 — Correcciones a hechos del ADR de negocio (verificados en código)

Antes del diseño, tres afirmaciones del ADR de negocio quedaron desactualizadas respecto al código y se corrigen acá (no invalidan las decisiones, sí cambian el peso de riesgo):

1. **§3.1 / §9 "el filtro 'excluir servicio cancelado' del cálculo de saldo no está inspeccionado / es un riesgo" → RESUELTO, el filtro YA EXISTE.** `ReservaMoneyCalculator.AddService` (`ReservaMoneyCalculator.cs:121-138`) ignora todo servicio que no esté `quoted` ni `resolved`; `quoted` se deriva de `WorkflowStatusHelper.CountsForQuotedTotal` (`WorkflowStatusHelper.cs:45-48`), que devuelve `false` para `Cancelado`; `resolved` viene de `ServiceResolutionRules.IsResolved`, que excluye explícitamente los cancelados en cada tipo (`ServiceResolutionRules.cs:39-43, 70-74`, comentario "un servicio cancelado sale del saldo, ADR-020"). **Conclusión: para sacar la venta de un servicio del saldo del cliente NO hay que tocar el cálculo de plata; alcanza con poner el `Status` del servicio en cancelado.** El recálculo + sincronización de `ReservaMoneyByCurrency` lo hace `ReservaMoneyPersister.PersistAsync` (`ReservaMoneyPersister.cs:39-71`), que además borra las filas de moneda que dejan de tener presencia (`SyncMoneyByCurrencyRowsAsync:79-120`).

2. **§7 / §6.3 "la próxima migración sería `Adr026_*`" → CONFIRMADO.** `Adr025_M1_ReintroduceOperationalDeadlinesAndPassportExpiry` ya existe en el working tree (timestamp `20260613023119`, vencimientos, sin commitear). La migración de este diseño es **`Adr026_M1`**, NO `Adr025_M1` (el ADR de negocio §7 dice `Adr025_M1`; está tomado).

3. **§4.3.3 / §9 "fallback `MonCotiz=1` para facturas USD legacy en `BookingCancellationService ~2163-2165`" → línea desactualizada Y parcialmente mitigado.** Las líneas 2163-2165 hoy son `WarnIfDebitNoteLate` (nada que ver). El guard real del path de **NC parcial** ya existe: `BookingCancellationService.cs:3644-3648` rutea a manual (LogCritical) cuando `isForeignCurrency && (snapshotExchangeRate <= 0 || == 1)`, y hay un segundo guard de coherencia NC↔origen en `:3743-3748`. El riesgo VIVO está en el path de **NC total**: `:2316-2318` deja `MonId/MonCotiz` en su default PES/1 ("MVP: solo ARS"). Por lo tanto el riesgo `MonCotiz=1` aplica a la NC total sobre factura USD legacy, no a la NC parcial. Lo recogemos en DT.7.

Las entidades tipadas (`HotelBooking`, `FlightSegment`, `TransferBooking`, `PackageBooking`, `AssistanceBooking`) **ya tienen** `Status`, `CancelledAt`, `CancelledByUserId`, `CancelledByUserName`, `ConfirmedAt` desde ADR-020 (`HotelBooking.cs:60,155-164`; `FlightSegment.cs:84,179`). **Marcar un servicio como cancelado NO requiere columnas nuevas en esas tablas.**

### DT.1 — Modelo de datos: `BookingCancellation` (padre) + `BookingCancellationLine` (hijas)

#### DT.1.1 Entidad nueva `BookingCancellationLine`

Tabla nueva `BookingCancellationLines`, aditiva. No toca `BookingCancellations` ni INV-081 (el UNIQUE 1-BC-por-reserva queda intacto). FK a `BookingCancellations` con `OnDelete: Cascade` (las líneas no sobreviven a su BC; el BC nunca se borra físicamente, se anula, así que el cascade no es un riesgo de pérdida fiscal — pero ver DT.8 invariante de inmutabilidad).

```csharp
// src/TravelApi.Domain/Entities/BookingCancellationLine.cs  (NUEVO)
public class BookingCancellationLine : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    // --- Padre (aggregate root del evento fiscal) ---
    public int BookingCancellationId { get; set; }
    public BookingCancellation BookingCancellation { get; set; } = null!;

    // --- Operador de ESTA línea (nivel línea, no evento) ---
    public int SupplierId { get; set; }
    public Supplier Supplier { get; set; } = null!;

    // --- Referencia al servicio cancelado: (tabla, id) (ADR-015 §6.3 opción a) ---
    public CancellableServiceTable ServiceTable { get; set; }   // enum: Generic|Flight|Hotel|Transfer|Package|Assistance
    public int ServiceId { get; set; }                          // Id (int) del servicio en su tabla tipada/genérica

    // --- Alcance y montos (por moneda de la línea) ---
    public BookingCancellationLineScope Scope { get; set; }     // enum: Full | Partial  (Partial = el resto del file sigue vivo)
    [MaxLength(3)] public string Currency { get; set; } = Monedas.ARS;   // moneda del servicio (HotelBooking.Currency etc.)
    public decimal LineSaleAmount { get; set; }                 // SalePrice del servicio cancelado, congelado al draft

    // --- Penalidad por operador (baja ADR-013/014 a nivel línea) ---
    public CancellationConceptKind ConceptKind { get; set; } = CancellationConceptKind.OperatorPenaltyPassThrough;
    public PenaltyStatus PenaltyStatus { get; set; } = PenaltyStatus.Estimated;
    public decimal? PenaltyAmount { get; set; }                 // null mientras no haya penalidad confirmada
    public DateTime? PenaltyConfirmedAt { get; set; }
    public DateTime? OperatorPenaltyConfirmedDate { get; set; } // eje fiscal del plazo (ADR-014), distinta de la del sistema
    [MaxLength(450)] public string? ConceptClassifiedByUserId { get; set; }
    [MaxLength(200)] public string? ConceptClassifiedByUserName { get; set; }
    public DateTime? ConceptClassifiedAt { get; set; }

    // --- ND propia de ESTA línea (solo si ConceptKind = cargo propio; Q-F1 default "una por cargo") ---
    public int? DebitNoteInvoiceId { get; set; }                // guard de idempotencia por línea
    public Invoice? DebitNoteInvoice { get; set; }
    public DebitNoteStatus DebitNoteStatus { get; set; } = DebitNoteStatus.NotApplicable;
    [MaxLength(1000)] public string? DebitNoteArcaErrorMessage { get; set; }

    // --- Refund del operador de ESTA línea (baja ADR-002/INV-126 a nivel línea) ---
    public decimal RefundCap { get; set; }                      // lo pagado a este operador − su penalidad
    public decimal ReceivedRefundAmount { get; set; }           // SUM(allocations de ESTE operador no-voided)
    public BookingCancellationLineRefundStatus RefundStatus { get; set; }  // None|PendingOperatorRefund|Settled

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // xmin concurrency (igual que BookingCancellation): UseXminAsConcurrencyToken() en la config.
}
```

Enums nuevos (en `Domain/Entities`, junto a los de ADR-013):
- `CancellableServiceTable { Generic=0, Flight=1, Hotel=2, Transfer=3, Package=4, Assistance=5 }`.
- `BookingCancellationLineScope { Full=0, Partial=1 }` — `Full` = cancelación total del file (la línea es una de N que cancelan todo); `Partial` = cancelación parcial (servicio cancelado, resto del file vivo).
- `BookingCancellationLineRefundStatus { None=0, PendingOperatorRefund=1, Settled=2 }`.

Se **reusan** los enums de ADR-013/014 ya existentes (`CancellationConceptKind`, `PenaltyStatus`, `DebitNoteStatus`) — no se inventan.

#### DT.1.2 Qué se mantiene a nivel EVENTO (padre `BookingCancellation`)

- `OriginatingInvoiceId`, `CreditNoteInvoiceId` (1 factura → 1 NC): **sin cambios**. Ancla fiscal hacia el cliente.
- `FiscalSnapshot` (owned VO, `BookingCancellation.cs:131`): **sin cambios** (foto fiscal del evento).
- `FiscalLiquidation` (owned VO, `:150`): se mantiene a nivel evento; en cancelación total multi-operador la NC sigue siendo total y la liquidación es del evento. (NO se usa la fórmula de neteo; ver DT.4.)
- Campos ADR-013 de ND (`ConceptKind`, `PenaltyStatus`, `DebitNoteInvoiceId`, snapshot `*AtEvent`, `:294-436`): **se conservan como agregado del evento** para compat y para el caso mono-operador/mono-penalidad (1 línea), pero la **fuente de verdad por operador pasa a la línea**. Decisión de migración Q-T1: `bc.SupplierId` y los campos ND del padre quedan como denormalización del "operador principal" / "penalidad principal" hasta deprecarlos; NO se borran en este diseño (cero migración destructiva).

#### DT.1.3 Backfill (en la misma migración `Adr026_M1`, EF puro)

1 línea por cada BC histórico: `ServiceTable`/`ServiceId` no son recuperables de forma fiable para BCs viejos (el BC histórico no apunta a un servicio puntual, cancela la reserva entera). **Decisión**: el backfill crea **una línea sintética por BC** con `SupplierId = bc.SupplierId`, `Scope = Full`, `Currency = bc.FiscalSnapshot.CurrencyAtEvent` (o ARS si null), `LineSaleAmount = 0` (no se intenta reconstruir; el monto del evento histórico vive en el padre), `ServiceTable = Generic`, `ServiceId = 0` (centinela "línea de backfill, sin servicio puntual"), copiando `ConceptKind`/`PenaltyStatus`/`ReceivedRefundAmount`/`DebitNoteInvoiceId` del padre. Es idempotente (no crear si ya existe línea para el BC). **Prevalidar contra dump Postgres real** antes de migrar (convención dura; lección M2: nada de SQL crudo en la migración, el backfill se escribe en C#/EF dentro del `Up` o en un servicio de backfill invocado post-migrate, como `MultiCurrencyBackfillService`).

`ServiceId = 0` como centinela es seguro porque las líneas nuevas (no-backfill) siempre referencian un servicio real (`ServiceId > 0`); un CHECK puede exigir `ServiceId > 0 OR ServiceTable = Generic` si se quiere endurecer, pero NO bloquear el centinela del backfill.

### DT.2 — Reformulación de invariantes

#### DT.2.1 INV-152 (bloqueo multi-operador) — SE LEVANTA

`InferSingleSupplierIdAsync` (`BookingCancellationService.cs:3159-3188`) hoy tira `INV-152` si hay >1 operador. **Cambio**: se deja de inferir UN operador y se pasa a construir UNA LÍNEA POR OPERADOR/SERVICIO. `GetDistinctSupplierIdsAsync` (`:3209+`, las 6 fuentes con dedupe) se **conserva y se reusa** como insumo, pero ahora alimenta la creación de líneas, no un single-supplier. El método `InferSingleSupplierIdAsync` se mantiene SOLO para el path mono-operador/mono-servicio de compat (1 servicio, 1 operador → 1 línea), o se reemplaza por un `BuildCancellationLinesAsync(reserva, scope, selectedServiceIds, ct)` que devuelve la lista de líneas. **Recomendación**: nuevo método `BuildCancellationLinesAsync`; `InferSingleSupplierIdAsync` queda deprecado pero no se borra hasta cerrar Q-T1.

#### DT.2.2 INV-126 (refund.SupplierId == bc.SupplierId) — SE REFORMULA A NIVEL LÍNEA

`OperatorRefundService.cs:328-333` hoy exige `refund.SupplierId == bc.SupplierId`. **Cambio**: el refund se imputa a la **línea** cuyo `SupplierId` coincide. Nueva forma:

```csharp
// OperatorRefundService, reemplaza el check INV-126 actual
var matchingLine = bc.Lines.SingleOrDefault(l => l.SupplierId == refund.SupplierId);
if (matchingLine == null)
    throw new BusinessInvariantViolationException(
        "El proveedor del reintegro no corresponde a ninguna línea de esta cancelación.",
        invariantCode: "INV-126");
// El cap y el ReceivedRefundAmount se llevan a nivel línea (matchingLine.RefundCap / .ReceivedRefundAmount),
// y el denormalizado bc.ReceivedRefundAmount se mantiene como SUM(lines.ReceivedRefundAmount) para compat.
```

**Riesgo de transición** (declarado): los BCs backfilleados tienen 1 línea con `SupplierId = bc.SupplierId`, así que `SingleOrDefault` la encuentra y el comportamiento mono-operador es byte-equivalente. Verificar que NO haya dos líneas con el mismo `SupplierId` en un BC nuevo (un operador con 2 servicios cancelados en el mismo evento) — en ese caso `SingleOrDefault` tira. **Decisión**: cuando un operador tiene 2+ servicios cancelados, el refund se imputa a la línea por `(SupplierId, ServiceId)` o, si el refund es agregado del operador, a la primera línea de ese operador con `RefundCap` no saturado (regla de imputación a definir con `travel-agency-domain-expert`; default: agregar `RefundCap`/`ReceivedRefundAmount` por operador, no por línea, cuando el operador devuelve un único monto). **Marcar como decisión abierta DT-Q1.**

#### DT.2.3 Invariantes nuevos

- **INV-153 (NC fiscal única)**: una sola NC y/o una factura viva por reserva; nunca N NC por operador sobre la misma factura. Se materializa porque la NC vive en el padre (`CreditNoteInvoiceId`), no en las líneas. Test, no CHECK SQL (es semántico).
- **INV-154 (no neteo)**: la penalidad NUNCA reduce `LineSaleAmount` ni el monto de la NC; va por ND (cargo propio) o por menor refund (pass-through). Refleja el criterio del matriculado. Test + el guard de disyunción dura que YA existe en `OperatorRefundService.cs:343-359` (anti-doble-cobro ADR-013), que se baja a nivel línea.
- **INV-155 (comisión vendedor ≥ 0)**: la deducción del operador puede llevar la comisión de la línea a cero, nunca a negativo. Test.

### DT.3 — Flujo por casos (qué método hace qué)

El flujo de cancelación tiene hoy 2 pasos (`DraftAsync` → `ConfirmAsync`) más el sub-circuito de refund (`OperatorRefundService`). El diseño los extiende SIN romper el path mono-operador actual.

#### DT.3.1 Cancelar 1 servicio (parcial, resto del file vivo)

Nuevo endpoint/método de aplicación (no se mezcla con la cancelación total para no contaminar el path fiscal existente):

1. **`CancelServiceAsync(reservaPublicId, serviceTable, servicePublicId, reason, penaltyClassification, userId, ct)`** (nuevo, en `BookingCancellationService` o en un `PartialServiceCancellationService` que delega):
   - Marca el servicio: setea `Status = Cancelado`, `CancelledAt`, `CancelledByUserId/Name` en la tabla tipada correspondiente (los campos YA existen, ADR-020). **No toca el estado de la reserva** (decisión #1 sellada).
   - Recalcula la plata con `ReservaMoneyPersister.PersistAsync` → la venta del servicio sale del saldo automáticamente (DT.0.1).
   - Sub-circuito operador (DT.3.3): según cuánto se pagó al operador, baja deuda o crea la línea en `PendingOperatorRefund`.
   - Fiscal: **NO emite NC automática** (decisión #3). Arma el borrador/cálculo y lo deja en revisión manual (DT.4). El header de la reserva muestra "N de M servicios cancelado" (dato calculado, no estado nuevo).
2. Casos de penalidad/pago: ver matriz §3.3 del ADR de negocio (sin cambios; la implementación clasifica por `ConceptKind` en la línea).

#### DT.3.2 Cancelar file multi-operador (total, línea por línea)

`DraftAsync` extendido: en vez de `InferSingleSupplierIdAsync`, llama `BuildCancellationLinesAsync` → una línea `Scope=Full` por servicio/operador. La NC sigue siendo total sobre la factura única (path actual, sin cambios fiscales). Cada línea corre su sub-circuito de refund/penalidad. El estado de la reserva va a `Cancelled` (o `PendingOperatorRefund` si alguna línea tiene refund en vuelo), igual que hoy.

#### DT.3.3 Sub-circuito operador por línea (reusa ADR-002)

- **No se pagó nada al operador** → `RefundStatus = None`, baja la deuda de la línea (`SupplierBalanceByCurrency` baja por `SupplierId`+`Currency`). Sin refund.
- **Se pagó (total/anticipo)** → `RefundCap = pagado − penalidad del operador`, `RefundStatus = PendingOperatorRefund`. El refund efectivo entra por `OperatorRefundService.RecordRefundAsync` y se imputa a la línea (INV-126 reformulado, DT.2.2). El asiento de caja (`CashLedgerEntry`, ingreso por moneda) lo hace el path ADR-002/022 EXISTENTE en la misma transacción (`OperatorRefundService`, verificado: hay coherencia de moneda con `FiscalSnapshot` en `:309-318` — esto pasa a validarse contra `line.Currency`, no contra `bc.FiscalSnapshot.CurrencyAtEvent`, en multi-moneda; ver DT.6).

#### DT.3.4 Reembolso agregado al cliente (decisión #2)

Cuando varias líneas devuelven plata, se acumula en `ClientCreditEntry` POR MONEDA (`ClientCreditEntry.cs:53-62`, ya es por moneda y ya soporta origen cancelación con `BookingCancellationId`). Un retiro (`ClientCreditWithdrawal`) cuando el cliente lo pide. **No se requiere entidad nueva**: `ClientCreditEntry` ya modela esto. Para multi-operador, cada `OperatorRefundReceived` genera/incrementa el saldo a favor del cliente en su moneda; el cliente ve UN bolsillo por moneda (agregado), no uno por operador.

### DT.4 — Fiscal: qué se reusa, qué arranca en manual, qué queda detrás de la firma

- **Maquinaria de emisión de NC parcial** (`EnqueuePartialCreditNoteAsync`, `:3825`; `PartialCreditNoteIvaCalculator`; `ArcaIdempotencyKeys`; bandeja ADR-010): **se reusa** SOLO cuando el operador confirme manualmente la NC parcial limpia (Caso A sin penalidad) y SIN netear penalidad. La invocación a `EnqueuePartialCreditNoteAsync` se hace desde el path de revisión manual, no automático.
- **Fórmula de neteo** (`FiscalLiquidationCalculator`: NC = total − no reintegrables − penalidad): **NO se usa** (contradice al matriculado). El flag `EnablePartialCreditNoteRealEmission` **NO se descongela**.
- **Motor de ND** (ADR-013, `IsDebitNote`, tipos ARCA 2/7/12/52): se conecta a nivel línea SOLO para `ConceptKind` de cargo propio confirmado (`PenaltyStatus=Confirmed`). Default Q-F1: una ND por línea/cargo (campo `DebitNoteInvoiceId` en la línea como guard de idempotencia).
- **Caso A sin penalidad → REVISIÓN MANUAL** (decisión #3): el sistema arma el borrador (líneas + montos + clasificación + cálculo de NC parcial propuesto) y lo deja en una bandeja/estado de revisión. El operador emite a mano (NC total + factura por el resto, o NC parcial cuando el contador firme Q-F2). **No se construye el disparo automático.** Sí se construye: el armado del borrador, la clasificación por línea, el cálculo propuesto, y el botón de confirmación manual que reusa `EnqueuePartialCreditNoteAsync`.

**Detrás de firma del contador (NO se construye la emisión automática)**: Q-F2 (NC parcial de un servicio en factura mixta), Q-F1 (ND agregada vs por cargo), plazo NC/ND (15 días corridos vs hábiles), asiento de diferencia de cambio.

### DT.5 — Impacto en plata y caja (verificado)

- **Saldo del cliente** (`ReservaMoneyByCurrency` / `ConfirmedSale`): el servicio cancelado sale solo al setear su `Status` (DT.0.1). Cero código de cálculo nuevo; solo invocar `ReservaMoneyPersister.PersistAsync` tras marcar el servicio.
- **Deuda al operador** (`SupplierBalanceByCurrency`): baja por línea cancelada en su moneda (el cálculo ya es por `SupplierId`+`Currency`; verificar que la baja de deuda lea el `Status` cancelado del servicio — el mismo helper `CountsForSupplierDebt` excluye no-confirmados, `WorkflowStatusHelper.cs:51-54`).
- **Libro de caja** (`CashLedgerEntry`, ADR-022): el refund efectivo del operador y el reintegro al cliente generan asientos por el path ADR-002/022 EXISTENTE, en la misma transacción. NO se toca el motor de caja; solo se baja la imputación a nivel línea.
- **Saldo a favor** (`ClientCreditEntry`): agregado por moneda (DT.3.4).

### DT.6 — Diferencia de cambio (decisión #4) — enganche técnico, asiento a `accounting-expert-argentina`

**Dónde se calcula**: al confirmar la cancelación (T0/emisión de NC) de una reserva con factura en moneda extranjera cobrada en otra moneda. El `FiscalSnapshot` guarda `ExchangeRateAtOriginalInvoice` (`:628`); los pagos guardan su TC de imputación (`Payment.ImputedAmount`/`ImputedCurrency`, usados por `ReservaMoneyCalculator.cs:160-161`). La diferencia de cambio T0→cancelación es: `(monto cancelado en USD × TC_NC) − (lo cobrado imputado a esa porción en ARS)`.

**Enganche técnico que SÍ diseñamos** (el asiento contable fino lo valida `accounting-expert-argentina`):
1. Un punto de cálculo en el path de confirmación de la cancelación que, cuando `FiscalSnapshot.CurrencyAtEvent != ARS` y hubo cobro en ARS, computa el delta de cambio sobre la porción cancelada.
2. Un registro nuevo del delta. **Opciones a validar con `accounting-expert-argentina`**:
   - (a) un `CashLedgerEntry` de tipo "diferencia de cambio" (encaja en el libro inmutable de ADR-022, por moneda);
   - (b) un campo/VO en el evento de cancelación que el reporte contable consuma.
   **Recomendación técnica**: (a) reusa el libro inmutable y no inventa estructura nueva; pero la **cuenta contable, el signo (ganancia/pérdida por diferencia de cambio) y si se factura aparte** los define `accounting-expert-argentina` + firma del matriculado (el criterio "se factura" ya está firmado; falta el asiento).

**Qué necesita explícitamente `accounting-expert-argentina`**: estructura del asiento (cuentas debe/haber), cómo se llama el concepto en el libro de caja, si el delta va a un `CashLedgerEntry` o a un comprobante fiscal aparte, y el tratamiento del signo. **NO construir el asiento sin esa validación**; sí construir el punto de cálculo del delta y dejarlo registrado para revisión.

### DT.7 — Riesgos

- **`Riesgo fiscal ALTO`**: emitir NC parcial real (Caso A) sin firma Q-F2 + homologación ARCA → **mitigado por decisión #3** (arranca en revisión manual, no emite solo).
- **`Riesgo fiscal medio` (corregido respecto al ADR de negocio)**: el fallback `MonId/MonCotiz` PES/1 vive en el path de **NC TOTAL** (`:2316-2318`), no en el parcial (el parcial ya guarda en `:3644-3648`). Una NC total sobre factura USD legacy saldría con `MonCotiz=1`. **Mitigación**: extender el guard de `:3644-3648` (foreign currency + snapshot rate ≤0 o ==1 → manual) al path de NC total, antes de levantar multi-operador para reservas con factura USD legacy.
- **`Riesgo contable`**: asiento de diferencia de cambio (DT.6) → a `accounting-expert-argentina` + matriculado.
- **`Riesgo de transición`**: imputación de refund cuando un operador tiene 2+ servicios cancelados en el mismo evento (DT.2.2, DT-Q1) → cerrar con `travel-agency-domain-expert`.
- **`Seguridad/datos`**: levantar INV-152 + clasificación de penalidad por línea tocan cancelaciones/refunds/comprobantes → `security-data-risk-reviewer` obligatorio. NO confiar en el frontend para validar que el `serviceId`/`supplierId` pertenecen a la reserva (validación server-side, espejo de INV-151 de ADR-015 Fase 1). 4-eyes para la clasificación de penalidad propia (ya existe `cancellations.classify_agency_penalty`).
- **Concurrencia**: `BookingCancellationLine` lleva `xmin` como el padre; cancelar un servicio y editar la reserva en paralelo → 409 controlado.

### DT.8 — Invariantes/tests a cubrir

- Inferencia/construcción de líneas: 1 operador (1 línea Full), N operadores (N líneas Full), 1 servicio parcial (1 línea Partial), dedupe por operador en multi-servicio.
- Saldo: cancelar un servicio saca su venta del saldo (regresión sobre `ReservaMoneyCalculator` — ya hay tests, agregar el caso "servicio pasa a Cancelado → baja `ConfirmedSale`/`TotalSale`").
- INV-126 reformulado: refund imputado a la línea correcta; refund de operador ajeno a la reserva → rechazo; cap por línea respetado.
- INV-153 (NC única), INV-154 (no neteo: penalidad NO baja la NC), INV-155 (comisión ≥ 0).
- Multi-moneda: hotel USD + aéreo ARS cancelados → `ReservaMoneyByCurrency` baja cada uno en su moneda; NC única en la moneda de la factura.
- Caso A sin penalidad → queda en revisión manual, NO emite NC (decisión #3): test de que NO se llama `EnqueuePartialCreditNoteAsync` automáticamente.
- Guard MonCotiz USD legacy en path NC total (DT.7) → rutea a manual, no emite con MonCotiz=1.
- **Migración/backfill contra Postgres real** (no InMemory): `Up`/`Down` de `Adr026_M1`, índice por `BookingCancellationId` y por `SupplierId`, backfill idempotente 1 línea/BC, centinela `ServiceId=0`. Lección M2: nada de SQL crudo en la migración.
- Baseline unit ~1617 verde debe mantenerse; los tests nuevos se suman.

### DT.9 — Reutilización vs nuevo (resumen)

| Pieza | Reuso / Nuevo |
|---|---|
| `BookingCancellation` (padre, NC única, FiscalSnapshot) | **Reuso sin cambios** |
| `BookingCancellationLine` + 3 enums | **Nuevo** (`Adr026_M1`, aditivo) |
| Cálculo de saldo (`ReservaMoneyCalculator`/`Persister`) | **Reuso sin cambios** (ya excluye cancelados) |
| Campos `Status`/`CancelledAt` en tablas tipadas | **Reuso sin cambios** (ADR-020) |
| `OperatorRefundService` (INV-126) | **Refactor**: imputación a línea, no a BC |
| `BookingCancellationService.InferSingleSupplierIdAsync` (INV-152) | **Refactor**: → `BuildCancellationLinesAsync` |
| `CancelServiceAsync` (parcial, marca servicio, no toca estado reserva) | **Nuevo** |
| `EnqueuePartialCreditNoteAsync` + bandeja ADR-010 | **Reuso** (solo desde confirmación manual) |
| `FiscalLiquidationCalculator` (neteo) | **Congelado** (no se usa) |
| Motor de ND (ADR-013) | **Reuso** a nivel línea, cargo propio confirmado |
| `ClientCreditEntry` (saldo a favor por moneda) | **Reuso sin cambios** (agregado al cliente) |
| `CashLedgerEntry` (caja) | **Reuso**: imputación por línea; + concepto "diferencia de cambio" (DT.6, validar con accounting) |

### DT.10 — Secuenciado de migraciones

1. `Adr025_M1_ReintroduceOperationalDeadlinesAndPassportExpiry` (`20260613023119`) — YA en el árbol (vencimientos, sin commitear). **Commitearla/deployarla primero** (es independiente).
2. **`Adr026_M1_AddBookingCancellationLines`** (este diseño): tabla `BookingCancellationLines` + índices (`BookingCancellationId`, `SupplierId`) + CHECK opcional + backfill 1 línea/BC. Aditiva, EF puro, sin SQL crudo. Prevalidar contra dump Postgres real.
3. Orden de deploy: `Adr026_M1` después de `Adr025_M1` para no chocar el orden temporal de EF. No hay dependencia de datos entre ambas (vencimientos vs líneas de cancelación son ortogonales).

### DT.11 — Archivos a tocar (para `backend-dotnet-senior`)

- **Nuevo**: `src/TravelApi.Domain/Entities/BookingCancellationLine.cs` + enums (`CancellableServiceTable`, `BookingCancellationLineScope`, `BookingCancellationLineRefundStatus`).
- **Config EF**: `src/TravelApi.Infrastructure/Persistence/AppDbContext.cs` (DbSet + `Owns/HasMany`, `xmin`, índices, FK cascade, CHECK).
- **Migración**: `src/TravelApi.Infrastructure/Persistence/Migrations/App/<ts>_Adr026_M1_AddBookingCancellationLines.cs` (+ Designer) + backfill (en `Up` EF o servicio tipo `MultiCurrencyBackfillService`).
- **`BookingCancellationService.cs`**: `BuildCancellationLinesAsync` (reemplaza el camino de `InferSingleSupplierIdAsync` para multi-operador); `CancelServiceAsync` (parcial); levantar INV-152 (`:3168-3185`); extender guard MonCotiz al path NC total (`:2316-2318`); armar borrador de NC parcial para revisión manual (no auto-emisión).
- **`OperatorRefundService.cs`**: INV-126 a nivel línea (`:328-333`); bajar disyunción anti-doble-cobro (`:343-359`) a la línea; refund cap/recibido por línea.
- **DTOs**: `CancellationDtos.cs` (exponer líneas en el DTO de respuesta; request de cancelación de servicio).
- **API**: endpoint de cancelación de un servicio (parcial) — coordinar con el endpoint `/status` por servicio que hoy NO existe para servicios genéricos (pendiente menor anotado en memoria 2026-06-08).
- **Frontend**: pasa por el **gate UX obligatorio con Gastón** (regla 2026-06-05) — contador "N de M cancelados" en el header, servicio tachado, bandeja de revisión manual de NC. NO diseñar UI sin sus respuestas; `ux-ui-disenador` primero.
- **Tests**: `src/TravelApi.Tests/Unit/Adr026*` (líneas, INV-126/152/153/154/155, saldo, multimoneda, manual review) + tests de migración contra Postgres real.

---

## Review del diseño técnico (software-architect-reviewer, 2026-06-13)

> **Veredicto: CHANGES REQUIRED.** El diseño es sólido en lo estructural (BC-padre + líneas, levantar INV-152, reformular INV-126 a línea, fiscal en manual) y las afirmaciones "ya existe" que bajan el peso están **verificadas en código** (ver más abajo, casi todas ciertas). Pero hay **2 bloqueantes de plata/correctitud** que el diseño deja como supuesto y que, si se construyen tal cual, dejan un agujero. Se aprueba la dirección; se traba la construcción hasta cerrar B1 y B2.

### Hechos verificados (contra código, no contra el texto del ADR)

- **DT.0.1 (excluir servicio cancelado del saldo del cliente) — CIERTO.** `ReservaMoneyCalculator.AddService` (`ReservaMoneyCalculator.cs:121-138`) corta temprano si `!quoted && !resolved`. Para los 6 tipos: `IsQuoted*` (`:195-211`) usa `CountsForQuotedTotal` (`WorkflowStatusHelper.cs:45-48`, false para `Cancelado`) e `IsResolved` excluye cancelados en cada tipo (`ServiceResolutionRules.cs:39-43, 70-74, 131-135`). Poner `Status=Cancelado` saca el servicio **tanto de TotalSale como de ConfirmedSale**. NO hay que tocar el cálculo de plata del cliente. Confirmado.
- **DT.0.2 (tablas tipadas ya tienen Status/CancelledAt/CancelledByUserId/Name/ConfirmedAt) — CIERTO.** Verificado en `HotelBooking.cs:60,155-164` y `FlightSegment.cs:84,167,179`. Marcar un servicio cancelado no requiere columnas nuevas.
- **DT.0.3 (fallback MonCotiz) — CIERTO y bien corregido.** El guard de NC parcial existe en `BookingCancellationService.cs:3644-3648` (foreign + rate ≤0 o ==1 → manual). El path de NC total deja PES/1 por default en `:2316-2318` ("MVP: solo ARS"). El riesgo vivo es la NC total sobre factura USD legacy, como dice DT.7. Correcto.
- **INV-152 (`:3159-3188`) e INV-126 (`OperatorRefundService.cs:328-333`) y disyunción anti-doble-cobro (`:343-369`) — están donde el diseño dice.** El método `GetDistinctSupplierIdsAsync` (`:3209+`, 6 fuentes con dedupe) es reusable como insumo de `BuildCancellationLinesAsync`. Correcto.
- **Colisión `Adr025_M1` — CONFIRMADA.** Existe `20260613023119_Adr025_M1_ReintroduceOperationalDeadlinesAndPassportExpiry.cs` en el árbol. Nombrar la nueva `Adr026_M1` es correcto (ver M2 sobre el esquema de nombres).
- **`ClientCreditEntry` por moneda + origen cancelación — CIERTO** (`ClientCreditEntry.cs:49-62`). DT.3.4 no necesita entidad nueva.
- **FK línea→BC sin polimorfismo EF — sólido.** El patrón `HasOne(BookingCancellation).HasForeignKey(BookingCancellationId)` ya existe (`AppDbContext.cs:1723-1725, 1790-1792`) y `UseXminAsConcurrencyToken()` es el patrón vigente del módulo (`:1593,1659,1709`). La referencia `(ServiceTable enum, ServiceId int)` SIN navegación EF es correcta: no hay FK rota porque no hay FK declarada al servicio. **Nota**: no hay integridad referencial a nivel BD entre la línea y el servicio (un `ServiceId` puede quedar colgado si el servicio se borra). Aceptable para genéricos borrables, pero ver M3.

### Bloqueantes

- **B1 — Baja de deuda al operador NO es automática al setear `Status=Cancelado`; el diseño no lo construye explícitamente (plata).** `SupplierDebtCalculator` filtra por `CountsForSupplierDebtByType` (`SupplierDebtPersister.cs:99`), así que un servicio cancelado SÍ sale de la deuda **— pero solo si alguien recalcula**. La recalculación NO se dispara sola al escribir el `Status`: hay que invocar `SupplierDebtPersister.PersistAsync` / `RecalculateSupplierDebtAsync` en la misma transacción (patrón verificado en `ReservaService.cs:1957-1968`, que lo llama a mano tras `SaveChanges`). El diseño DT.5 dice "verificar que la baja de deuda lea el Status cancelado" pero **no especifica que `CancelServiceAsync` (DT.3.1) deba llamar al persister del proveedor de esa línea**. Si se construye DT.3.1 sin ese llamado, se cancela el servicio, baja el saldo del cliente, pero **la deuda al operador (`Supplier.CurrentBalance` + `SupplierBalanceByCurrency`) queda stale** — exactamente el bug P1 que ADR-022 §4.10 ya arregló para el path genérico. **Remediación**: DT.3.1 y DT.3.3 deben establecer explícitamente que, tras marcar el servicio cancelado, se llama `SupplierDebtPersister.PersistAsync(db, line.SupplierId, ct)` por cada operador afectado, en la misma transacción y ANTES del SaveChanges del caller (igual que `ReservaService` viejo proveedor + nuevo proveedor). Agregar test: "cancelar un hotel confirmado y pagado → `SupplierBalanceByCurrency` de ese operador baja el NetCost en su moneda".

- **B2 — `SingleOrDefault` de INV-126 reformulado (DT.2.2, `OperatorRefundService.cs:328-333`) deja un bug latente que el propio diseño reconoce pero NO resuelve (DT-Q1).** El código propuesto `bc.Lines.SingleOrDefault(l => l.SupplierId == refund.SupplierId)` **tira `InvalidOperationException` cuando un operador tiene 2+ líneas en el mismo BC** (un operador con hotel + traslado cancelados, caso real de multi-servicio que este ADR justamente habilita). DT.2.2 lo marca como "decisión abierta DT-Q1" y lo difiere a `travel-agency-domain-expert`. **No se puede construir INV-126 con `SingleOrDefault` y dejar la regla de imputación abierta**: o se decide la regla (imputar por `(SupplierId, ServiceId)`, o agregar refund por operador a través de N líneas con cap por operador) antes de codear, o el primer refund de un operador con 2 servicios cancelados explota en runtime con un 500 no controlado (no es ni siquiera un 409 de invariante). **Remediación**: cerrar DT-Q1 ANTES de construir. Recomendación del reviewer: el refund del operador es agregado por operador (un operador devuelve un monto, no "por servicio"), así que el cap y el `ReceivedRefundAmount` deben ser **por operador dentro del BC** (sumar sobre las líneas de ese `SupplierId`), no por línea individual; reemplazar `SingleOrDefault` por `Where(...).ToList()` + imputación contra el cap agregado del operador. Si se prefiere por línea, exige que el refund traiga el `ServiceId`/`ServiceTable` destino. En cualquier caso: NUNCA dejar `SingleOrDefault` con la regla abierta.

### Mejoras mayores

- **M1 — Backfill: el centinela `ServiceId=0` + `LineSaleAmount=0` debe garantizar que NO contamina la baja de deuda ni el saldo.** El backfill (DT.1.3) crea 1 línea sintética por BC con `ServiceId=0`, `LineSaleAmount=0`, `Scope=Full`. Es razonable (el BC histórico ya consumió su efecto sobre saldo/deuda al cancelarse en su día). Pero hay que verificar explícitamente que ninguna lógica nueva (recalcular saldo/deuda al "reprocesar" un BC viejo) re-aplique la línea sintética y duplique/reste de nuevo. Las líneas backfilleadas son **históricas, no deben gatillar recálculo**. Declarar el invariante: "una línea con `ServiceId=0` es de backfill, no participa de recálculos de saldo/deuda futuros". Y confirmar que el backfill corre como servicio C#/EF post-migrate (estilo `MultiCurrencyBackfillService`), NO SQL crudo en el `Up` (lección M2 — el diseño lo dice, pero DT.1.3 ofrece "en el `Up` EF o en un servicio": elegir el servicio, es más testeable contra Postgres).

- **M2 — Esquema de nombres de migración: la numeración ADR↔migración ya es inconsistente; fijar la convención para no acumular más confusión.** Hoy: ADR-025 (este, cancelación) → migración `Adr026_M1`; vencimientos (sin ADR) → migración `Adr025_M1`. Es confuso pero **la colisión está evitada** (los nombres no chocan) y el secuenciado por timestamp es correcto (`Adr026_M1` con `ts > 20260613023119`). Recomendación: mantener `Adr026_M1_AddBookingCancellationLines` (ya elegido) y **documentar en el ADR que la numeración de migración NO sigue la del ADR** (van por orden de construcción, no por número de ADR), o renumerar la de vencimientos a su propio ADR. No bloquea; solo evitar que un tercero asuma `Adr0NN_M1` ↔ ADR-0NN.

- **M3 — Integridad referencial línea→servicio.** Como `(ServiceTable, ServiceId)` no tiene FK, un servicio genérico borrado (los genéricos sí se borran si no están confirmados) deja la línea apuntando a un `ServiceId` inexistente. Para líneas `Partial` esto importa (la UI muestra "servicio cancelado" que ya no existe). Decidir: o se prohíbe borrar un servicio que tiene línea de cancelación (regla de negocio, espejo de borrar-vs-cancelar de ADR-020), o la lectura tolera el dangling. Recomendación: un servicio con `BookingCancellationLine` NO se borra (ya está cancelado, no borrado — coherente con ADR-020).

- **M4 — Diferencia de cambio (DT.6): el "punto de cálculo" se construye pero el destino del registro queda sin definir, y eso bloquea el test.** DT.6 propone calcular el delta y registrarlo como `CashLedgerEntry` "diferencia de cambio" (opción a), difiriendo a `accounting-expert-argentina` la cuenta/signo/si se factura. Correcto NO construir el asiento sin firma. Pero "construir el punto de cálculo y dejarlo registrado para revisión" necesita un **destino concreto y testeable** o no se puede cubrir con test (DT.8 no lista test de diferencia de cambio). Recomendación: el destino interino sea un registro inerte (un campo/VO en el evento de cancelación, opción b de DT.6) que NO impacta caja ni reportes hasta que accounting defina el asiento — así el cálculo es testeable sin comprometer el libro inmutable de ADR-022. Construir el `CashLedgerEntry` de diferencia de cambio (opción a) recién con la firma. Agregar a DT.8 el test del cálculo del delta (input USD facturado / ARS cobrado → delta esperado).

### Cobertura de los caminos al levantar INV-152 (Challenge #5) — OK con una salvedad

Levantar INV-152 expone estos caminos, y el diseño los cubre: (1) `GetDistinctSupplierIdsAsync` se reusa como insumo (correcto); (2) la NC sigue siendo única a nivel evento (`CreditNoteInvoiceId` en el padre, no en líneas) → INV-153; (3) refund por operador → B2 (path a medio hacer, ese es el agujero). La salvedad: el guard `MonCotiz=1` del path NC total (DT.7) debe extenderse ANTES de habilitar multi-operador para reservas con factura USD legacy — el diseño lo dice pero hay que secuenciarlo como precondición dura, no como riesgo "a mitigar después".

### Riesgos de migración / rollback

- Aditiva, EF puro, `Down` = drop tabla (sin pérdida de dato fiscal: el padre BC no se toca). Correcto.
- **Prevalidar contra dump Postgres real** (no InMemory) — el diseño lo exige (lección M2/`TravelFileId`). Mantener. El backfill como servicio C#/EF (M1) es más fácil de prevalidar que un `Up` con loop.

### Seguridad / auditoría

- Validación server-side de pertenencia de `serviceId`/`supplierId` a la reserva (espejo INV-151) — el diseño la pide (DT.7). Obligatoria: no confiar en el frontend para qué servicio/operador se cancela.
- `security-data-risk-reviewer` obligatorio antes de construir (toca cancelaciones/refunds/comprobantes + 4-eyes de clasificación de penalidad). Mantener.

### Resumen de qué cerrar antes de construir

1. **B1**: DT.3.1/DT.3.3 invocan explícitamente `SupplierDebtPersister.PersistAsync` por operador afectado en la misma transacción + test de baja de deuda.
2. **B2**: cerrar DT-Q1 (regla de imputación de refund con operador multi-servicio) y reemplazar `SingleOrDefault` por la regla decidida; nunca dejarlo abierto con `SingleOrDefault`.
3. **M1-M4** recomendadas (no bloquean, mejoran robustez/testabilidad).
4. Secuenciar el guard `MonCotiz=1` del path NC total como precondición de multi-operador con factura USD legacy.

### No verificado (declarado)

- No ejecuté la suite de tests (baseline ~1617 verde es del diseño, no confirmado por el reviewer).
- No verifiqué que `BookingService` (path típico de cambio de Status de tablas tipadas Hotel/Flight) recalcule deuda hoy; verifiqué el path genérico de `ReservaService` (`:1957-1968`, sí recalcula a mano). B1 aplica igual: `CancelServiceAsync` es nuevo y debe llamar al persister sin asumir que el path de Status lo hace.
- No verifiqué la homologación ARCA ni la firma del contador (fuera de alcance de código; correctamente diferidas a manual).

---

## Resolución de los bloqueantes B1/B2 (cierre de diseño, 2026-06-13) — LISTO PARA CONSTRUIR

**B1 — RESUELTO (regla de construcción obligatoria):** `CancelServiceAsync` (y el camino multi-operador) DEBE invocar `SupplierDebtPersister.PersistAsync` para **cada operador afectado** dentro de la MISMA transacción que marca el `Status` del servicio en cancelado, replicando el patrón ya existente del path genérico (`ReservaService.cs:1957-1968`). NO se asume que el cambio de `Status` recalcule la deuda solo. Invariante de test obligatorio: cancelar un servicio confirmado de un operador → la deuda de ESE operador (`SupplierBalanceByCurrency` + escalar) baja por el costo del servicio cancelado, en la misma operación.

**B2 / DT-Q1 — RESUELTO (alineado con la decisión #2 de Gastón):** la imputación del refund con un operador que tiene 2+ servicios cancelados en el mismo evento es **agregada por operador**: un único `RefundCap` por operador (suma de los caps de sus líneas canceladas) y el reembolso recibido se imputa contra ese agregado, acumulándose al saldo a favor del cliente por moneda (`ClientCreditEntry`). En código: reemplazar el `bc.Lines.SingleOrDefault(l => l.SupplierId == refund.SupplierId)` por `bc.Lines.Where(l => l.SupplierId == refund.SupplierId)` y operar sobre el conjunto (suma de caps, distribución/acumulación), **nunca** `SingleOrDefault` (que tira 500 con 2+ líneas). Coherente con "un reembolso agregado al cliente" que ya eligió el dueño.

**M1-M4 y guard `MonCotiz=1`:** se incorporan como tareas de robustez del build (no bloquean el arranque). El path NC total con factura USD legacy sin `MonCotiz` guardado debe rutear a revisión manual igual que el parcial.

**Estado:** diseño técnico CERRADO. Construible por `backend-dotnet-senior` cuando se priorice, con la salvedad de que la **emisión fiscal automática (NC parcial / ND / asiento diferencia de cambio) NO se construye**: queda en revisión manual hasta la firma del contador matriculado (Q-F1/Q-F2/plazo/asiento). Lo construible ahora: modelo `BookingCancellationLine`, levantar INV-152, cancelar servicio individual con baja de deuda (B1), refund agregado por operador (B2), clasificación de penalidad por línea, y el armado/borrador de la NC sin emitir.
