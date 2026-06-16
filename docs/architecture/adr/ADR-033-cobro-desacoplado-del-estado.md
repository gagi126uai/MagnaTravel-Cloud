# ADR-033 — Cobro desacoplado del estado operativo (cobrabilidad por deuda real)

## 1. Status

**v2 (2026-06-16) — PARCIALMENTE IMPLEMENTADO (E1–E7, lo que NO necesita contador).** La **dirección** (desacoplar cobro del estado) quedó **aprobada** por `software-architect-reviewer`. Esta v2 corrige el **alcance** y dos regresiones que el reviewer marcó como bloqueantes (B1–B4) + tres "debería" (D1–D3).
No tiene feature flag (regla del dueño 2026-06-07). Reemplaza la regla de cobrabilidad de **ADR-032** (que queda subsumido, ver §10).

> **Estado de implementación (2026-06-16, backend):** ver el detalle por etapa en **§14**. Implementado y con suite unit verde: **E1** (regla `SaleFirmStatuses`/`IsSaleFirmStatus` + `IsCollectable` por deuda real), **E2** (visibilidad AR/cobranza/cliente/dashboard/alertas con `ReceivableDebtStatuses`), **E3** (editar/borrar cobro sin gate de estado, fiscal/puente conservados), **E4** (gate de estado vivo en cancelar servicio + revert `Cancelled→InManagement` con query D2), **E5** (FC4 acepta destino Closed con deuda, conserva INV-096/INV-095), **E6** (visibilidad del crédito atascado), **E7** (estado de cobro derivado por moneda). **PENDIENTE de contador (NO implementado):** E8 (facturación lista positiva / reabrir por factura tardía — B5), E9 (ancla refund-cap sin factura — B7) y las 4 confirmaciones fiscales de la Parte C.

> **Resumen de cambios v1 → v2** al final del documento (§13), en lenguaje simple.

## 2. Context

Auditoría de 5 miradas (arquitectura ×3, contador de turismo, ERP) sobre el ciclo de la reserva. Causa raíz **confirmada por las 5 y verificada en código (HEAD `7603b4e`, re-verificada en v2)**:

> La **cobrabilidad** (registrar un cobro, editar/borrar cobro, aplicar saldo a favor) está atada al **ESTADO OPERATIVO** de la reserva (`EstadoReserva.ActiveCollectionStatuses`), no a la **DEUDA / cuenta por cobrar real**.

Patrón ERP unánime (SAP, Odoo, Dynamics, NetSuite): el cobro va contra la **factura / cuenta por cobrar (AR)**, independiente del estado del documento operativo. El estado operativo describe el viaje; el AR describe la plata. Son ejes distintos.

### Hechos verificados en código (no asumidos) — rutas corregidas en v2

> Corrección de v1: el namespace es **`TravelApi.Domain`** (no `Travel.Domain`). `EnsureCollectable` está en **`Reserva.cs:307`** (v1 decía 298/311); `IsCollectableStatus` en **`Reserva.cs:107`** (v1 decía 90). `FinancePositionService` vive en **`src/TravelApi.Infrastructure/Services/FinancePositionService.cs`** (no en la raíz). Las líneas de abajo están re-verificadas contra el código real.

| # | Hecho | Ubicación (verificada) |
|---|---|---|
| F1 | Cobrar se gatea por estado: `Reserva.EnsureCollectable()` → `EstadoReserva.IsCollectableStatus` → `ActiveCollectionStatuses = {InManagement, Confirmed, Traveling, ToSettle}`. **Closed NO está.** | `TravelApi.Domain/Entities/Reserva.cs:90-119` (`EstadoReserva`), `:298` (`IsCollectable`), `:307` (`EnsureCollectable`) |
| F2 | Editar/borrar cobro se gatea por el **mismo** estado: `Reserva.EnsurePaymentsEditable()` usa `IsCollectable()`. | `Reserva.cs:319-323` |
| F3 | Call-sites de alta de cobro con el gate de estado: `PaymentService.CreatePaymentAsync` (`:522`), `ReservaService.AddPaymentAsync(int)` (`:2498`). | `Infrastructure/Services/PaymentService.cs`, `ReservaService.cs` |
| F4 | FC4 (aplicar saldo a favor) gatea estado del destino: `ClientCreditService.cs:671` (`EstadoReserva.IsCollectableStatus(targetReserva.Status)`). **Pero tira `BusinessInvariantViolationException` con `invariantCode="INV-096"`, NO el `InvalidOperationException` de `EnsureCollectable`** (ver B2). | `ClientCreditService.cs:662-694` |
| F5 | Visibilidad AR/cobranza usa la lista de estados `FinancePositionService.ActiveReceivableStatuses = {InManagement, Confirmed, Traveling, ToSettle}`. Closed NO está → deuda en Closed **invisible** en cuentas por cobrar. **Esta lista `public static` tiene MÁS consumidores que F5 (ver B1).** | `FinancePositionService.cs:24-30` |
| F6 | Alerta "viajó y debe": `AlertService.cs:154-167` arma `urgentTrips` con comparaciones **inline** (`f.Status == InManagement/Confirmed/Traveling`) y `Balance > 0`. **No usa `ActiveCollectionStatuses`.** Closed/ToSettle con deuda no alertan. | `AlertService.cs:150-182` |
| F7 | El saldo puede aparecer **después** del cierre: `RecalculateMoneyAsync/UpdateBalanceAsync` no tiene guard de estado y recalcula `Balance` en cualquier estado, incluido Closed. El cierre limpio exige `Balance <= 0`, pero editar un servicio bajo candado post-cierre deja `Balance > 0`. | `ReservaService.cs` (UpdateBalanceAsync), `BookingService` call-sites |
| F8 | `Closed → Traveling` existe en la matriz de revert (`AllowedRevertTransitions`, `ReservaService.cs:903`) **pero** hay hard-block si hay cualquier factura con CAE (`ReservaService.cs:1030-1032`), no salteable ni por admin. ⇒ Closed + deuda + CAE = sin salida hoy. | `ReservaService.cs:896-904`, `:1030-1032` |
| F9 | `InvoiceService.CreateAsync` usa lista **negativa** `NonInvoiceableStatuses = {Quotation, Budget, InManagement, Lost}` (`InvoiceService.cs:71-77, 322`). Closed/Cancelled/PendingOperatorRefund **no** están → un POST factura una reserva Closed/Cancelled. La UI usa la lista positiva `ActiveInvoicingStatuses` (`:62-67, 241`), pero el server lo acepta. | — |
| F10 | `CancelServiceAsync` NO valida que la reserva esté en estado "vivo": solo corre el candado fiscal CAE/voucher. Deja cancelar servicios en Lost/Cancelled. | `BookingCancellationService.cs:357, 379-382` |
| F11 | **CORREGIDO en v2.** `PendingOperatorRefund` SÍ tiene salida hoy: `BookingCancellationService.OnAllCreditConsumedAsync:1759` escribe `Reserva.Status = Cancelled` **cuando el cliente consume TODO el saldo a favor**. `OperatorRefundService.AllocateAsync` (`:486/515`) NO escribe `Reserva.Status`: crea el `ClientCreditEntry` y deja el BC en `ClientCreditApplied`. El problema real es: **si el crédito NUNCA se consume**, la reserva queda en `PendingOperatorRefund` para siempre. (Ver B1 rediseñado.) | `BookingCancellationService.cs:1756-1764`, `OperatorRefundService.cs:482-523` |
| F12 | `Cancelled` NO está en `AllowedRevertTransitions` (`ReservaService.cs:896-904`): es terminal sin revert, aunque no haya habido NC/crédito/refund (asimétrico con `Lost`, que sí revierte). | `ReservaService.cs:896-904` |
| F13 | **NUEVO en v2 (B1).** `FinancePositionService.ActiveReceivableStatuses` es `public static` y lo consumen 5 lugares con DOS intenciones distintas: AR/deuda **y** "venta firme operativa" (lead ganado). Detalle en B1. | ver B1 |
| F14 | **NUEVO en v2 (D1).** Los métodos legacy de editar/borrar pago (`PaymentService.UpdatePaymentAsync`, `DeletePaymentCoreAsync`; `ReservaService.UpdatePaymentAsync:2550`, `DeletePaymentAsync:2628`) tienen **guard fiscal y de puente PROPIO** (recibo/CAE vía `MutationGuards`/`DeleteGuards`, puentes vía `OverpaymentCreditCleanup`/`AppliedCreditBridge`), independiente del gate de estado. Detalle y consecuencia en D1. | `PaymentService.cs:1200-1238, 1476-1517`, `ReservaService.cs:2540-2592, 2605-2659` |

### El deadlock que motiva todo

Reserva **Closed con deuda real y factura con CAE**: no se puede cobrar (F1), no se puede revertir para reabrir (F8), no aparece en cobranzas ni alertas (F5, F6). **Deuda atrapada e invisible.** La factura con CAE es una cuenta por cobrar legítima que el estado operativo no debería poder bloquear.

## 3. Decision

Desacoplar el **cobro** del **estado operativo**. La cobrabilidad pasa a definirse por **venta firme + deuda real**, no por la lista de estados activos. La factura **no se toca** (candado fiscal intacto). La deuda en estados terminales-pero-con-deuda se vuelve **visible y cobrable**; cuando `Balance` (por moneda) llega a 0 queda realmente saldada.

Introducimos un eje explícito de **cobro derivado del saldo por moneda** (no un nuevo campo persistido; un valor calculado), sin tocar el eje operativo ni el de facturación.

---

## PARTE A — Desacople (la raíz)

### A1. Nueva regla de COBRABILIDAD (basada en deuda real)

Reemplazar la regla de estado por un predicado de **venta firme + saldo positivo**. La fuente única sigue siendo el dominio (`Reserva` / `EstadoReserva`).

**Predicado (recomendado):**

```
EsCobrable(reserva) :=
    VentaFirme(reserva.Status)        // ya pasó por venta — NO pre-venta
    AND reserva.Balance > 0           // hay deuda real exigible
```

Donde **`VentaFirme`** = todo lo que NO es pre-venta ni descartado-sin-venta:

- **Pre-venta (NO firme):** `Quotation`, `Budget`, `Lost`. Nunca cobrables (no hay venta).
- **Firme:** `InManagement`, `Confirmed`, `Traveling`, `ToSettle` **y también `Closed`**.
- **`Cancelled`** y **`PendingOperatorRefund`**: ver A1.1 — se mantienen **fuera** del cobro normal.

Esto introduce una nueva lista en `EstadoReserva`: **`SaleFirmStatuses`** = `{InManagement, Confirmed, Traveling, ToSettle, Closed}`. La diferencia con `ActiveCollectionStatuses` es exactamente **`Closed`** (operativamente terminado, financieramente puede seguir abierto).

> Nota de diseño: hoy `ActiveCollectionStatuses` mezcla dos preguntas: "¿hay venta firme?" y "¿se le puede pedir plata?". Las separamos. `ActiveCollectionStatuses` se conserva SOLO como "estados operativos vivos pre-cierre" para los usos que de verdad quieren eso (no para cobrabilidad).

**Cambios por archivo:**

| Archivo:método | Cambio |
|---|---|
| `Reserva.cs` `EstadoReserva` (`:90`) | Agregar `SaleFirmStatuses` (las 5 firmes) y `IsSaleFirmStatus(string?)`. Conservar `ActiveCollectionStatuses` con su semántica acotada a "operativo vivo". |
| `Reserva.cs` `Reserva.IsCollectable()` (`:298`) | Redefinir: `EstadoReserva.IsSaleFirmStatus(Status) && Balance > 0`. (Antes: solo estado.) |
| `Reserva.cs` `EnsureCollectable()` (`:307`) | Sin cambio de firma. Ahora corta cuando: (a) estado pre-venta/terminal no-firme → mensaje "pasala a En gestión primero"; (b) firme pero `Balance <= 0` → mensaje nuevo "no hay saldo pendiente para cobrar". Dos mensajes distintos para no confundir. |
| `PaymentService.CreatePaymentAsync` `:522` | Sin cambio textual (sigue llamando `reserva.EnsureCollectable()`); cambia el comportamiento por la nueva regla. **PRECONDICIÓN DURA D3**: `Balance` debe estar fresco antes del guard (ver D3). |
| `ReservaService.AddPaymentAsync(int)` `:2498` | Igual: sigue llamando `file.EnsureCollectable()`. Mismo requisito D3. |
| `ClientCreditService.cs:671` (FC4) | **NO se cambia a `IsCollectable()` escalar (ver B2).** Solo se agrega "venta firme" para permitir destino `Closed` con deuda, conservando su `BusinessInvariantViolationException`/`INV-096` y su validación de deuda **per-moneda** (`INV-095`). |

**Por qué `Balance > 0` y no solo "venta firme":** sin el `Balance > 0` se podría "cobrar" una reserva ya saldada (sobrepago a ciegas). El sobrepago legítimo se maneja por el puente a saldo a favor (ADR-022), no abriendo el cobro normal sobre saldo cero. El monto del cobro lo sigue topeando el flujo de pago existente.

> **Nota de coherencia con A5/B4:** el predicado escalar `Balance > 0` de `Reserva.IsCollectable()` es el guard de **dominio** (corta el alta de un cobro sin saldo). La VERDAD por moneda vive en `ReservaMoneyByCurrency` y la usa el flujo de cobro real (qué moneda se puede cobrar). El guard escalar es un piso ("hay ALGO de deuda"); la imputación por moneda la resuelve el flujo de pago como hoy. No reemplaza al cálculo por moneda.

#### A1.1 Qué pasa con `Cancelled` y `PendingOperatorRefund`

**Recomendación: ambos quedan FUERA del cobro normal.** Justificación:

- **`Cancelled`**: la cancelación ya resolvió la plata por su propio circuito (NC / refund / saldo a favor — ADR-002, ADR-009, ADR-015, ADR-025). Cobrarle "deuda" a una reserva cancelada por la vía normal del recibo evadiría ese circuito y podría tapar una NC pendiente (riesgo fiscal, ver Parte C). Si una cancelada quedó con saldo real mal resuelto, el camino correcto es **revert controlado** (Parte B, B2) o el circuito de cancelación, no el recibo. ⇒ `Cancelled` NO es `SaleFirm` para cobro.
- **`PendingOperatorRefund`**: estado **transitorio** esperando que el operador devuelva. La plata del cliente está congelada esperando refund; abrir cobro acá contradice el flujo. ⇒ NO cobrable. (Su problema real es el crédito que nunca se consume — se arregla en B1, no acá.)

Ambos se excluyen por construcción: no están en `SaleFirmStatuses`.

### A2. EDITAR / BORRAR cobro: por inmutabilidad fiscal, no por estado

Hoy `EnsurePaymentsEditable()` bloquea editar/borrar en cualquier estado no-cobrable (incluido un Closed sin nada fiscal). Eso es demasiado: lo que debe bloquear la edición/borrado libre es la **inmutabilidad fiscal del cobro**, no el estado de la reserva.

**Nueva regla de editabilidad (a nivel del PAGO, no de la reserva):**

```
EsEditableLibremente(payment) :=
    NO tiene recibo emitido (ni Voided)                                  // guard fiscal existente
    AND NO está ligado a factura AFIP viva                               // guard fiscal existente
    AND NO es un pago-puente (sobrepago / saldo a favor / cancelación)   // guards de puente existentes
```

El **estado operativo de la reserva sale de la ecuación**. Un cobro sin recibo y sin atadura fiscal se puede corregir esté la reserva donde esté; un cobro con recibo emitido se **anula** (no se edita) esté donde esté.

**Cambios por archivo:**

| Archivo:método | Cambio |
|---|---|
| `Reserva.cs` `EnsurePaymentsEditable()` (`:319`) | **Deprecar como guard de estado.** Ya no debe basarse en `IsCollectable()`. La decisión pasa a los guards fiscales/puente por pago que ya existen. Remover sus usos de gate de estado (ver D1). |
| `PaymentService.cs` `UpdatePaymentAsync` (`:1225` llama `EnsureReservaPaymentsEditableAsync`) y `DeletePaymentCoreAsync` (`:1472`) | Quitar el gate de estado (`EnsureReservaPaymentsEditableAsync`); **conservar** los guards fiscales/puente existentes (`MutationGuards`/`DeleteGuards` recibo/CAE, `OverpaymentCreditCleanup`, `AppliedCreditBridge`). **D1 verificado:** estos guards ya existen y son independientes del estado. |
| `ReservaService.cs:2550` (UpdatePayment legacy), `:2628` (DeletePayment legacy) | Igual: quitar `file.EnsurePaymentsEditable()` de estado; **conservar** los guards fiscales/puente (verificados presentes, ver D1). |
| Anulación | **`AnnulPaymentAsync` (ADR-032, `PaymentService.cs:1363`, `POST /api/payments/{id}/annul`) es la salida canónica** para cobros fiscalmente sellados, en cualquier estado (incluido terminal). La anulación deja rastro (soft-delete + contra-asiento de caja ADR-022). |

> Importante: editar/borrar **libre** se restringe por lo fiscal, pero **anular** (con rastro) debe poder usarse incluso en terminal. La anulación NO depende del estado operativo.

> **PRECONDICIÓN DE CONSTRUCCIÓN (D1):** antes de quitar el gate de estado, está **verificado** (HEAD `7603b4e`) que los 4 métodos legacy de update/delete ya tienen guard fiscal+puente propio (F14). **No queda ningún camino que permita editar/borrar libre un cobro con recibo/CAE/puente.** Si en la construcción se descubre un camino sin guard propio (p.ej. un endpoint nuevo), **el mismo cambio debe agregarle el guard fiscal**, no dejar la edición/borrado libre. No se deprecia el gate de estado sin esta verificación pasada en cada call-site.

### A3. VISIBILIDAD: alinear AR, cobranza y alertas con la deuda real

Para que no exista deuda invisible, los lugares que muestran **deuda cobrable** deben reflejar **venta firme (incl. Closed) + `Balance > 0`**. Pero — corrección B1 — **NO todos los consumidores de `ActiveReceivableStatuses` quieren eso**. Ver B1 para el split de conceptos. Acá se listan SOLO los que pasan a la nueva lista de "deuda cobrable":

| Archivo:método | Cambio |
|---|---|
| `FinancePositionService.cs:44, 75, 109` (AR por moneda, por cliente, ranking) | Filtrar por la nueva `ReceivableDebtStatuses` (= `SaleFirmStatuses`, incluye Closed). Las queries ya filtran `Balance > 0`, así que una Closed saldada (Balance 0) sigue sin aparecer; solo aparece Closed con deuda. |
| `CustomerService.cs:354` (saldo del cliente / `CurrentBalance` + resumen) | Filtrar por `ReceivableDebtStatuses`. El "Saldo Actual" del cliente pasa a incluir su deuda en reservas Closed. |
| `ReportService.cs:574` (dashboard "Saldo Pendiente"), `:619` (detalle receivables), `:667` (Excel) | Filtrar por `ReceivableDebtStatuses`. El dashboard de cobranzas muestra Closed con deuda. |
| `AlertService.cs:154-167` ("viajó y debe") | Agregar bucket `Closed` con `Balance > 0` como **deuda post-viaje** (sin tope de ventana, como ya hace `Traveling` en el caso B). Es código **inline** (no usa la lista): se agrega un tercer OR. Rótulo separado: "terminado y debe" vs "en viaje y debe". |
| `PaymentService.cs:108, 167` (worklist cobranza) | Cambiar el filtro de estado para ofrecer cobrar la Closed con deuda → alinea front con la nueva regla de A1. |

**Fuente única (corregida en B1):** estos consumidores referencian **`ReceivableDebtStatuses`** (concepto "tiene deuda viva cobrable"), que se define como `= SaleFirmStatuses`. NO se reusa `ActiveReceivableStatuses` a ciegas (eso arrastraría el lead-won, ver B1).

### A4. "Finalizada con deuda" como situación financiera VÁLIDA

Definición canónica: una reserva `Closed` es **operativamente terminada** (el viaje pasó) pero puede estar **financieramente abierta** (`Balance > 0`). No es un error ni un estado a "arreglar volviendo atrás".

- Se la puede **cobrar** (A1) → al llegar a `Balance = 0` (por moneda) queda **realmente saldada** (sigue `Closed`, sin cambio de estado).
- Aparece en cobranzas / AR / alertas mientras `Balance > 0` (A3).
- **Deuda que reaparece post-cierre** (factura tardía, ADR-027): **sí debe re-mostrarse** en cobranzas (recomendado). Como A3 deriva de `Balance > 0`, esto sale gratis: si una factura tardía sube `ConfirmedSale`/`Balance`, la Closed vuelve a aparecer automáticamente. No requiere lógica extra.
- **Hard-block de revert con CAE (F8): se mantiene como está (confirmado correcto).** Ya no hace falta reabrir para cobrar — se cobra en Closed. El bloqueo fiscal por CAE sigue protegiendo la historia.

### A5. Estado de cobro derivado del saldo — POR MONEDA (corrección B4)

**Recomendación: estado de cobro DERIVADO del saldo, calculado, NO persistido, y definido POR MONEDA.**

> Corrección B4 sobre v1: v1 definía el estado de cobro sobre `Balance` **escalar**, que es la suma cross-moneda (ARS + USD) y es semánticamente impuro (ver el propio comentario de `FinancePositionService.GetCustomerReceivableScalarAsync:87-95`). La verdad por moneda vive en **`ReservaMoneyByCurrency`** (ADR-021/023) y las queries de AR ya filtran `Balance > 0` **por moneda**. El estado de cobro debe leerse de ahí, no del escalar.

```
EstadoCobro(reserva) :=   // derivado de ReservaMoneyByCurrency (filas por moneda)
    si alguna moneda tiene Balance > 0   -> "Con deuda"
    si no hay deuda y alguna moneda < 0   -> "Saldo a favor"   // sobrepago (ver B5 / Parte C)
    si todas las monedas en 0             -> "Saldado"
```

- "Con deuda" gana sobre "saldo a favor" cuando hay ambas en monedas distintas (una reserva que debe USD y tiene saldo a favor ARS está, antes que nada, "con deuda"). Esto es coherente con la regla de cobrabilidad (si hay alguna moneda con deuda, es cobrable en esa moneda).
- El front que ya muestra el desglose por moneda (ADR-021 Capa 7) puede mostrar el estado por fila además del agregado.

Justificación de "derivado, no persistido": evita un cuarto campo que se desincronice (ya hubo divergencias entre listas, F5/F6). El saldo por moneda ya es la fuente de verdad; el estado de cobro es solo su lectura. Se expone en DTO/API. **NO se introduce columna en DB → sin migración por esto.**

Los **ejes independientes** quedan así, sin tabla nueva:
- **Operativo:** `Reserva.Status` (ciclo de vida ADR-020).
- **Facturación:** existencia de `Invoice` con CAE (ya en datos).
- **Cobro:** derivado de `ReservaMoneyByCurrency` por moneda (A5).

---

## PARTE B — Correcciones de alcance y callejones concretos

### B1. SPLIT de `ActiveReceivableStatuses` en DOS conceptos (corrección bloqueante)

**Problema que v1 no vio:** `FinancePositionService.ActiveReceivableStatuses` es `public static` y la consumen **5 lugares con dos intenciones distintas**. Agregarle `Closed` a ciegas (como decía v1 §A3) tendría un efecto colateral grave: **cerrar una reserva marcaría su LEAD como Ganado.**

**Inventario completo de consumidores (verificado en HEAD `7603b4e`):**

| # | Consumidor | Intención REAL | ¿Debe incluir `Closed`? |
|---|---|---|---|
| C1 | `FinancePositionService.cs:44` `GetAccountsReceivableByCurrencyAsync` | Deuda cobrable global (AR por moneda) | **SÍ** — Closed con deuda es AR legítima |
| C2 | `FinancePositionService.cs:75` `GetCustomerReceivableByCurrencyAsync` | Deuda cobrable del cliente | **SÍ** |
| C3 | `FinancePositionService.cs:109` `GetReceivableScalarByCustomerPublicIdAsync` | Ranking/orden de clientes por deuda | **SÍ** |
| C4 | `FinancePositionService.cs:119` `IsInFirmReceivableStatus(status)` | Helper público — depende de quién lo llame | Ver nota ▼ |
| C5 | `CustomerService.cs:354` saldo del cliente (`CurrentBalance` + resumen TotalSales/Paid/Balance) | Deuda cobrable del cliente | **SÍ** |
| C6 | `ReportService.cs:574` dashboard "Saldo Pendiente" | Deuda cobrable (dashboard) | **SÍ** |
| C7 | `ReportService.cs:619` detalle receivables por cliente+moneda | Deuda cobrable | **SÍ** |
| C8 | `ReportService.cs:667` Excel de receivables | Deuda cobrable | **SÍ** |
| C9 | `ReservaService.cs:2735` `MarkSourceLeadAsWonIfReservaIsFirmAsync` | "¿La venta se concretó?" → marca el LEAD como **Ganado** | **NO** — ver ▼ |

**▼ Por qué C9 NO debe heredar `Closed`:** el lead se marca Ganado cuando el cliente **acepta** (la reserva pasa a en-firme). Una reserva ya **cerrada** que recalcula saldo no es un evento de "venta nueva concretada"; además, el lead-won es **idempotente** (no reabre Perdido, no re-procesa Ganado), así que en la práctica un Closed casi nunca re-dispararía — pero **no queremos atar semánticamente "cerrar/cobrar" a "lead ganado".** Son ejes distintos. Heredar Closed acá sería un acople accidental.

**▼ C4 `IsInFirmReceivableStatus`:** es un helper público genérico. En v2 se **deja apuntando al concepto operativo** (sin Closed) y se audita en construcción que ningún call-site de "deuda cobrable" lo use por error. Si algún consumidor de deuda lo necesita, debe usar el helper del concepto de deuda (abajo), no este.

**Diseño: DOS conceptos separados, una fuente única cada uno (NO un alias ciego):**

1. **`EstadoReserva.SaleFirmStatuses` = `{InManagement, Confirmed, Traveling, ToSettle, Closed}`** — "es una venta firme" (incluye terminada-pero-firme). Base del concepto de deuda.
2. **`EstadoReserva.ActiveCollectionStatuses` = `{InManagement, Confirmed, Traveling, ToSettle}`** (SIN cambios) — "operativo vivo pre-cierre". **Lo sigue usando C9 (lead-won)** y cualquier uso que de verdad signifique "la venta está viva, todavía no cerró".
3. **`FinancePositionService.ReceivableDebtStatuses`** (nuevo, expone `= SaleFirmStatuses`) — "tiene cuenta por cobrar / deuda viva cobrable". **Lo usan C1, C2, C3, C5, C6, C7, C8** (todos los lugares de deuda/AR/cobranza).

**Migración del código:**
- C1, C2, C3, C5, C6, C7, C8 → cambian de `ActiveReceivableStatuses` a `ReceivableDebtStatuses` (= incluye Closed).
- C9 (`MarkSourceLeadAsWonIfReservaIsFirmAsync:2735`) → cambia de `FinancePositionService.ActiveReceivableStatuses` a `EstadoReserva.ActiveCollectionStatuses` (mismo conjunto que hoy: SIN Closed). El comportamiento de C9 **no cambia**; solo se desacopla de la lista de AR para que el split de AR no lo arrastre.
- `FinancePositionService.ActiveReceivableStatuses` (la lista vieja `public static`) → se renombra/reorienta. Recomendación: **renombrarla a `ReceivableDebtStatuses` con el conjunto nuevo (incl. Closed)** y dejar `ActiveCollectionStatuses` (en `EstadoReserva`) como la única fuente del concepto operativo. Eliminar la lista vieja para que nadie la reuse con la semántica equivocada.

> **Invariante de diseño:** "venta firme operativa viva" (lead-won, sin Closed) y "tiene deuda viva cobrable" (AR/cobranza, con Closed) son **conceptos distintos con fuente única cada uno**. Prohibido un alias `ReceivableDebtStatuses = ActiveReceivableStatuses` que mezcle ambos.

### B2. FC4 (saldo a favor) conserva su excepción + código + chequeo per-moneda (corrección bloqueante)

**Problema que v1 introdujo:** v1 §A1 decía "Reemplazar `IsCollectableStatus(targetReserva.Status)` por `targetReserva.IsCollectable()`". Eso es una **regresión**:

1. **Rompe el contrato HTTP.** `ClientCreditService.cs:671-677` (verificado) tira `BusinessInvariantViolationException` con `invariantCode="INV-096"`, que el `GlobalExceptionHandler` propaga al 409 y **el frontend de saldo a favor depende de ese código** (test `AppliedToNonCollectibleReserva_RejectsInv096`). `EnsureCollectable()` tira `InvalidOperationException` genérico → perdería el código y rompería front + test.
2. **El check escalar es más débil.** `IsCollectable()` usa `Balance > 0` escalar (cross-moneda). FC4 ya tiene algo **más fuerte**: valida la deuda **per-moneda** (`INV-095`, `:688-694`): no deja aplicar un saldo a favor USD a una reserva que solo debe ARS. Bajar a escalar abriría justo esa puerta.

**Corrección v2:** FC4 **conserva** su excepción (`BusinessInvariantViolationException`), su código (`INV-096`), y su validación per-moneda (`INV-095`). El **único** cambio es ampliar la pregunta de estado para **permitir destino `Closed` con deuda**:

| Archivo:método | Cambio v2 |
|---|---|
| `ClientCreditService.cs:671` | Reemplazar `EstadoReserva.IsCollectableStatus(targetReserva.Status)` por `EstadoReserva.IsSaleFirmStatus(targetReserva.Status)` (permite Closed firme). **Conservar** la `BusinessInvariantViolationException` con `invariantCode="INV-096"` (NO cambiar a `EnsureCollectable`). |
| `ClientCreditService.cs:688-694` | **Sin cambios.** El chequeo per-moneda (`INV-095`, `targetDebtInCreditCurrency <= 0`) se queda tal cual: ese es el que realmente garantiza "hay deuda en la moneda del saldo a favor". |

Resultado: se puede aplicar saldo a favor a una reserva `Closed` que tiene deuda en esa moneda, sin perder el contrato ni el chequeo fuerte.

### B3. `PendingOperatorRefund` con crédito que NUNCA se consume (rediseño del B1 de v1)

**Corrección de hechos (F11):** v1 afirmaba que `PendingOperatorRefund` "no tiene salida" y proponía escribir `Cancelled` en `OperatorRefundService.AllocateAsync`. **Eso es impreciso y peligroso:**

- **Ya existe la salida:** `BookingCancellationService.OnAllCreditConsumedAsync:1759` escribe `Reserva.Status = Cancelled` cuando el cliente consume **todo** el saldo a favor. (Verificado.)
- **`AllocateAsync` NO devuelve plata:** crea el `ClientCreditEntry` (saldo a favor del cliente) y deja el BC en `ClientCreditApplied` (`:486/515`). Escribir `Cancelled` en el allocate sería **prematuro** (el cliente todavía tiene crédito sin usar) y **duplicaría** la transición de :1759.

**El problema REAL:** si ese crédito **nunca se consume** (el cliente no lo aplica a otra reserva ni se le devuelve), la reserva queda en `PendingOperatorRefund` **para siempre**, y el saldo a favor queda colgado.

**Diseño v2 — visibilidad + salida administrativa del crédito no consumido (no tocar el allocate):**

1. **Visibilidad:** una reserva en `PendingOperatorRefund` con `ClientCreditEntry` de saldo remanente > 0 debe ser **visible** en una vista de "saldos a favor pendientes de usar/devolver" (el bolsillo del cliente por moneda ya existe, ADR-022; acá se trata de **listar las reservas atascadas** en este estado con crédito vivo). No se inventa estado nuevo.
2. **Salida administrativa:** habilitar una acción explícita "**dar por cerrada la cancelación**" cuando el crédito ya no se va a usar — que dispare la **misma** lógica de `OnAllCreditConsumedAsync` (devolución/baja del crédito remanente + transición a `Cancelled`), con autorización y rastro. No se escribe `Reserva.Status` directo: se reusa el camino existente para no saltear gates ni audit.
3. **NO tocar `AllocateAsync`.** Sigue creando el `ClientCreditEntry` y dejando el BC en `ClientCreditApplied`. La transición a `Cancelled` sigue saliendo de :1759 (consumo total) o de la nueva acción administrativa (cierre forzado del crédito).

> El "qué hacer con el crédito remanente" (devolver al cliente vs darlo de baja) tiene aristas fiscales → ver Parte C punto 4. La parte de **visibilidad** avanza sin contador; la **baja/devolución** del crédito remanente necesita criterio del dueño + contador.

### B4. `Cancelled` terminal sin revert (F12) — con QUERY del gate (D2)

Agregar **revert controlado `Cancelled → InManagement` SOLO cuando NO hubo NC / crédito / refund** (simétrico a `Lost`).

- `AllowedRevertTransitions[Cancelled] = { InManagement }` (`ReservaService.cs:896-904`).
- **Gate duro (D2 — query exacta).** Antes de permitir el revert, bloquear si existe cualquier movimiento fiscal/plata de la cancelación. Anclado en `BookingCancellation` de la reserva (que es lo que ata NC + crédito + refund):

```csharp
// Bloquea revert de Cancelled si la cancelación dejó huella fiscal o de plata.
// 1) NC emitida: BookingCancellation.CreditNoteInvoiceId != null  (Domain/Entities/BookingCancellation.cs:50)
// 2) Saldo a favor originado por la cancelación: ClientCreditEntry.BookingCancellationId apunta a una BC de esta reserva
//    (ClientCreditEntry.cs:49). Cubre el caso refund→crédito.
// 3) Refund recibido/imputado: la BC pasó por allocation -> tiene ReceivedRefundAmount > 0
//    o status ClientCreditApplied/Closed (BookingCancellation).
var hasFiscalOrMoneyTrace = await _context.BookingCancellations
    .Where(bc => bc.ReservaId == id)
    .AnyAsync(bc =>
        bc.CreditNoteInvoiceId != null
        || bc.ReceivedRefundAmount > 0
        || _context.ClientCreditEntries.Any(cce => cce.BookingCancellationId == bc.Id),
        ct);
if (hasFiscalOrMoneyTrace)
    throw new InvalidOperationException(
        "Esta cancelación ya generó una nota de crédito, un saldo a favor o un reintegro del operador. " +
        "No se puede revertir sin deshacer ese movimiento por su circuito.");
```

- El **hard-block CAE existente** (`ReservaService.cs:1030-1032`: `Invoices.AnyAsync(i => i.ReservaId == id && !string.IsNullOrEmpty(i.CAE))`) ya cubre el caso de factura viva y corre **antes** que este gate, así que una reserva con factura CAE ni llega acá. El gate de arriba cubre lo específico de la cancelación (NC/crédito/refund) que la factura no captura.
- **A confirmar en construcción:** el nombre exacto de los campos `ReceivedRefundAmount` y de la colección `ClientCreditEntries` en el `DbContext` (verificado `BookingCancellation.CreditNoteInvoiceId:50`, `ClientCreditEntry.BookingCancellationId:49`; el resto se confirma al implementar). El criterio del gate (NC ∨ crédito ∨ refund) es el que importa.

### B5. `InvoiceService.CreateAsync` no bloquea Closed/Cancelled (F9)

Cambiar de lista **negativa** a lista **positiva**.

- `InvoiceService.cs:322`: reemplazar `if (NonInvoiceableStatuses.Contains(reserva.Status)) throw` por `if (!ActiveInvoicingStatuses.Contains(reserva.Status)) throw`.
- Efecto: facturar queda permitido SOLO en `{Confirmed, Traveling, ToSettle}` (lo que la UI ya ofrece). Closed, Cancelled, PendingOperatorRefund, Quotation, Budget, InManagement, Lost quedan excluidos por defecto.
- **Impacto fiscal → contador** (Parte C): hoy una factura tardía sobre una Closed entra por server. Con el cambio, ya no. Hay que decidir si la factura tardía legítima requiere (a) reabrir a `ToSettle` antes de facturar, o (b) una excepción explícita. Recomiendo (a) por trazabilidad. **Decisión D-B5.**
- Quitar `NonInvoiceableStatuses` si queda sin uso.

### B6. `CancelServiceAsync` no valida estado vivo (F10)

Agregar gate de estado al inicio de `BookingCancellationService.CancelServiceAsync` (`:357`), antes del candado fiscal.

- Permitir cancelar servicios solo si la reserva está en estado **operativo vivo** = `EstadoReserva.ActiveCollectionStatuses` (`{InManagement, Confirmed, Traveling, ToSettle}`). Bloquear en `Quotation`/`Budget` (no hay venta), `Lost`, `Cancelled`, `Closed`, `PendingOperatorRefund`.
- `InvalidOperationException` → 409 (igual que el candado fiscal ya hace).

### B7. Cancelación parcial de servicio pagado SIN factura pierde el ancla del refund-cap

Hoy `RecordPartialCancellationLineAsync` (`BookingCancellationService.cs:413`) crea la línea `Scope=Partial` para anclar el refund. Pero cuando el servicio pagado al operador **no tiene factura**, el ancla del refund-cap depende de la factura → se pierde (pérdida de plata silenciosa).

**Ancla alternativa propuesta:** anclar el refund-cap a la **`BookingCancellationLine` de la reserva** (que B existente ya crea), no a la factura. El cap del refund recuperable del operador = monto pagado al operador por ese servicio, registrado en la línea, independiente de si hubo factura del operador. La conciliación del refund (`OperatorRefundService.AllocateAsync`) imputa contra la línea, no contra la factura.

> Esto toca circuito de refund de proveedor (high-risk). **Decisión D-B7** + revisión de `accounting-expert`/`travel-agency-accountant`.

---

## PARTE C — Lo que necesita CONTADOR (acotado por el reviewer)

El reviewer acotó qué de la Parte C **bloquea construcción** (necesita firma ANTES) y qué avanza sin contador.

### C-bloqueante (firma ANTES de construir el ítem correspondiente)

1. **Recibo en período posterior al de la factura** — cobrar una reserva finalizada con recibo emitido en un período **posterior** al de la factura original. ¿Admisible? ¿Implicancias IVA/Ganancias por el desfasaje? (Afecta A1/A3 sobre Closed.)
2. **Cobro vs Nota de Crédito vs incobrable** — cuándo una deuda en Closed/terminal se **cobra** (recibo), cuándo corresponde **NC**, cuándo se da por **incobrable**. (Afecta A1.1 y la regla de que la NC no "tape" deuda cobrable.)
3. **Factura tardía sobre Closed (D-B5)** — si se reabre a `ToSettle` para facturar, validar que el circuito sea fiscalmente correcto. (Bloquea B5.)
4. **Ancla del refund-cap sin factura (D-B7)** — anclar el cap recuperable a la `BookingCancellationLine` en vez de a la factura. (Bloquea B7.) Incluye el tratamiento del **crédito remanente no consumido** de B3 (devolver vs dar de baja).

### Avanza SIN contador

- El **desacople** del cobro respecto del estado (A1/A2 — la mecánica, no el caso fiscal puntual del punto 1).
- La **visibilidad** (A3, A5 por moneda, B1 split de listas, B3 *visibilidad* del crédito atascado).
- Los **gates de estado** B4 (revert) y B6 (cancelar servicio).

> Regla: si un ítem toca un caso fiscal de C-bloqueante, espera firma; lo demás (desacople mecánico, visibilidad, listas, gates de estado) avanza.

---

## 4. Consequences

**Positivas:**
- Desaparece la deuda atrapada/invisible (caso semilla del deadlock).
- Una sola pregunta para cobrar ("¿hay venta firme con saldo?"); los lugares de deuda derivan de **un** concepto (`ReceivableDebtStatuses`) y el lead-won de **otro** (`ActiveCollectionStatuses`) → no más divergencia ni acople accidental.
- El candado fiscal (CAE) queda intacto; ya no hay tentación de reabrir para cobrar.
- Sin tabla nueva ni columna nueva → sin migración de esquema.

**Negativas / costos:**
- Cambia el comportamiento de `EnsureCollectable` → toca el corazón de pagos. Requiere cobertura de tests amplia (§9) y reviewers de seguridad y contador.
- A3 hace aparecer en cobranzas y en el **saldo del cliente** (C5) reservas Closed con deuda que hoy estaban ocultas → el dueño verá deuda "nueva" que en realidad ya existía (es el objetivo, pero hay que comunicarlo).
- B5 endurece la facturación (positivo fiscal) pero puede bloquear un flujo de factura tardía que hoy funciona silenciosamente → D-B5.
- El split B1 toca CustomerService/ReportService/FinancePositionService a la vez (3 archivos, mismo concepto) — debe ir en un solo cambio coherente para no dejar listas mezcladas a mitad.

## 5. Alternatives considered

- **A. Reabrir-para-cobrar (revert Closed→Traveling).** Rechazada: requiere saltear el hard-block CAE (rompe historia fiscal) o no aplica con CAE vivo = no resuelve el caso semilla. Además ensucia el eje operativo para una necesidad de plata.
- **B. Estado de cobro persistido (columna).** Rechazada: agrega un campo que se desincroniza con `Balance` (ya pasó con las listas). El derivado por moneda (A5) da lo mismo sin ese riesgo.
- **C. Solo arreglar visibilidad (A3) sin tocar cobrabilidad (A1).** Rechazada: mostraría la deuda pero seguiría sin poder cobrarse = frustración.
- **D. (v1) Alias `ReceivableDebtStatuses = ActiveReceivableStatuses` + agregar Closed a la lista única.** Rechazada en v2: arrastraría el lead-won (C9) y mezclaría dos conceptos. Por eso el split de B1.
- **E. (v1) FC4 a `IsCollectable()` escalar.** Rechazada en v2: rompe `INV-096` (contrato front + test) y debilita el chequeo per-moneda `INV-095`. Por eso B2.
- **F. (v1) Escribir `Cancelled` en `AllocateAsync`.** Rechazada en v2: prematuro (crédito sin consumir) y duplica la transición de :1759. Por eso B3.

## 6. Migración / rollback

- **Sin migración de esquema** (regla pura + listas derivadas).
- **Backfill de visibilidad = NO requerido**: A3 deriva de `Balance` ya persistido; las Closed con deuda aparecen solas al desplegar.
- **Consulta read-only previa al merge** (no migración): contar reservas `Closed`/`Cancelled` con `Balance != 0` y con pagos vivos, para dimensionar qué se vuelve visible/cobrable y detectar datos sucios antes (Riesgo R1).
- **Rollback:** revertir el commit. Como no hay cambio de datos, el rollback es limpio. (Las reservas Closed con deuda vuelven a ocultarse, no se pierde nada.)

## 7. Testing strategy

- **Unit** `Reserva.IsCollectable()` / `EnsureCollectable`: firme+deuda (cobrable); firme+saldo0 (no, mensaje nuevo); pre-venta (no, mensaje "pasala a En gestión"); Cancelled/PendingOperatorRefund (no); **Closed+deuda (sí — caso nuevo).**
- **Service** los 3 call-sites de alta (`CreatePaymentAsync`, `AddPaymentAsync(int)`, FC4) cobran en Closed con deuda y rechazan en saldo0/terminal. **FC4 conserva `INV-096`** (test `AppliedToNonCollectibleReserva_RejectsInv096` debe seguir verde) **y `INV-095` per-moneda** (saldo a favor USD a reserva que solo debe ARS sigue rechazado).
- **B1 split (regresión):** cerrar una reserva que nació de un lead **NO** marca el lead como Ganado (C9 sigue usando `ActiveCollectionStatuses`); el saldo del cliente (C5) y el dashboard (C6/C7/C8) **sí** incluyen Closed con deuda.
- **D1 (editabilidad):** editar libre un cobro sin recibo en Closed (permitido); editar un cobro con recibo en Closed/Cancelled (bloqueado por guard fiscal, NO por estado); anular (no editar) un cobro con recibo en terminal (permitido vía `AnnulPaymentAsync`).
- **D3 (balance fresco):** el guard de `EnsureCollectable` corre después de `UpdateBalanceAsync` — un cobro que deja la reserva saldada no rechaza por leer un balance viejo, y una reserva recién endeudada se vuelve cobrable.
- **Visibilidad** (A3): Closed con deuda aparece en AR, worklist, dashboard, saldo del cliente y alerta; Closed saldada no.
- **A5 por moneda:** reserva que debe USD y tiene saldo a favor ARS → estado de cobro "Con deuda".
- **B3:** `PendingOperatorRefund` con crédito vivo aparece en la vista de atascados; la acción administrativa de cierre reusa `OnAllCreditConsumedAsync` y transiciona a Cancelled.
- **B4:** revert `Cancelled`→`InManagement` solo sin NC/crédito/refund; bloqueado si los hubo (con la query de D2); bloqueado por CAE antes de llegar al gate.
- **B5:** factura rechazada en Closed/Cancelled; permitida en Confirmed/Traveling/ToSettle.
- **B6:** cancelar servicio rechazado en Lost/Cancelled/Closed.
- **Regresión completa** de la suite de pagos/cancelación (alto acoplamiento).

## 8. Operational risks

- **R1 — Datos sucios:** pagos vivos en estados que hoy no son cobrables; Closed/Cancelled con `Balance != 0`. Mitigación: consulta read-only previa (§6).
- **R2 — Closed visible "de golpe":** comunicar al dueño que la deuda que aparece ya existía (afecta también el "Saldo Actual" del cliente, C5).
- **R3 (= D3, ahora precondición dura) — `Balance` desactualizado en el guard:** ver D3.
- **R4 — Caminos internos:** confirmar que `BookingCancellationService` y los puentes de sobrepago/FC4 NO pasen por `EnsureCollectable` (crean Payments directos). Ya documentado así en `Reserva.cs:304`; re-verificar tras el cambio.
- **R5 (nuevo) — Split B1 a mitad:** si C5/C6/C7/C8 cambian y C9 no (o viceversa), quedan listas inconsistentes. Mitigación: un solo cambio coherente + tests de B1.

## 9. Security/data risks

- Pagos = dato sensible: la anulación (A2) debe seguir exigiendo permiso y dejar rastro (ya lo hace ADR-032). `security-data-risk-reviewer` obligatorio.
- B4 (revert de Cancelled) abre una transición desde terminal: el gate de D2 (sin NC/crédito/refund) + la autorización de supervisor existente (`ReservaService.cs:1047-1052`) deben cubrirla. No permitir revert que esconda movimiento fiscal.
- B3 (acción administrativa de cierre del crédito atascado): debe exigir permiso elevado + rastro, y reusar el camino existente (no escribir `Status` a mano).

## 10. Relación con ADR-032

ADR-032 fijó "cobrabilidad = estado en `ActiveCollectionStatuses`" y está **implementado y testeado** en HEAD. Este ADR **subsume y corrige** esa regla: la cobrabilidad pasa a "venta firme + `Balance > 0`" (agrega `Closed`, agrega el requisito de deuda). Se conserva de ADR-032: la **fuente única**, la **anulación con rastro** (`AnnulPaymentAsync`), la **convergencia de call-sites**, y — corrección v2 — **los guards fiscales/puente de editar/borrar** (que NO eran de estado; el de estado se deprecia, los fiscales quedan). Cambia: el predicado y el desacople de editabilidad respecto del estado. Marcar ADR-032 como *Superseded by ADR-033* en su encabezado al implementar.

## 11. Decisiones finas para el dueño (con recomendación)

| ID | Decisión | Recomendación |
|---|---|---|
| **D-1** | ¿`Cancelled` y `PendingOperatorRefund` quedan fuera del cobro normal? | **Sí, fuera.** Su plata va por el circuito de cancelación/refund, no por recibo (evita tapar NC). |
| **D-2** | ¿La deuda que reaparece post-cierre (factura tardía) vuelve a cobranzas? | **Sí**, automático (deriva de `Balance > 0`). |
| **D-3** | ¿Mantener el hard-block de revert con CAE como está? | **Sí.** Ya no hace falta reabrir para cobrar. |
| **D-4 (A5)** | ¿Estado de cobro derivado **por moneda** (Saldado/Con deuda/Saldo a favor) o solo `Balance` escalar? | **Derivado por moneda, no persistido.** El escalar mezcla ARS+USD; la verdad es por moneda. |
| **D-5 (B1)** | ¿El "Saldo Actual" del cliente y el dashboard incluyen ahora las reservas **Closed con deuda**? | **Sí** — es deuda real cobrable. El lead-won NO cambia (sigue sin Closed). |
| **D-B3** | Crédito de cancelación que **nunca se consume**: ¿acción administrativa para cerrarlo (devolver/baja) reusando el camino existente? | **Sí**, con permiso elevado + rastro. La baja vs devolución del remanente → contador (Parte C punto 4). |
| **D-B4** | Revert de `Cancelled` solo sin NC/crédito/refund — ¿se habilita? | **Sí**, con el gate duro de D2 + autorización de supervisor. |
| **D-B5** | Factura tardía sobre Closed: ¿reabrir a `ToSettle` o excepción? | **Reabrir a `ToSettle`** (trazable) — pendiente firma contador (Parte C punto 3). |
| **D-B7** | Ancla del refund-cap parcial sin factura: ¿a la `BookingCancellationLine`? | **Sí**, ancla por reserva/línea, no por factura — pendiente contador (Parte C punto 4). |

## 12. Fuera de alcance (backlog, NO se diseña acá)

- Carga de pasajeros trabada bajo candado.
- Guard de voucher por scope.
- Divergencia HK vs emitido.
- Granularidad de des-confirmar por pago de proveedor.
- Cruce de monedas en saldo a favor (aplicar saldo USD a deuda ARS con conversión) — sigue siendo MVP same-currency.

---

## 13. Resumen de cambios v1 → v2 (lenguaje simple)

Qué corregí respecto de la primera versión, sin tocar la dirección (que estaba aprobada: separar "cobrar" de "el estado del viaje").

1. **La lista de cobranzas se usaba en más lugares de los que vi.** Una de las listas que pensaba tocar también es la que decide si un **lead se marca como "Ganado"**. Si le agregaba "Cerrada" a ciegas, **cerrar una reserva habría marcado el lead como ganado** — un error. Lo separé en **dos listas con significado distinto**: una para "esto es una venta firme viva todavía" (lead ganado, sin Cerrada) y otra para "esto tiene deuda que se puede cobrar" (cobranzas/saldos, ahora sí con Cerrada). Cada una con una sola fuente.

2. **El saldo a favor (aplicar plata de un cliente a otra reserva suya) lo iba a romper.** Mi cambio le habría sacado el mensaje de error con código que la pantalla usa, y habría debilitado un control que ya tenía (no mezclar monedas: no aplicar dólares a una deuda en pesos). Lo dejé como estaba **y solo le sumé** que ahora también acepte una reserva **cerrada con deuda**.

3. **Me equivoqué sobre las reservas "esperando devolución del operador".** Dije que no tenían salida; en realidad **sí salen** cuando el cliente gasta todo su saldo a favor. El problema real es otro: **si ese saldo a favor nunca se usa, la reserva queda colgada para siempre.** Cambié el diseño para eso: que esas reservas se **vean** en una lista, y que haya una **acción para cerrarlas** (con permiso y rastro), en vez de tocar el estado a la fuerza.

4. **El "estado de cobro" lo definí mal (mezclaba monedas).** Sumar pesos + dólares en un solo número da algo que no significa nada. Ahora el estado de cobro se calcula **por moneda** (la verdad ya está guardada así). Una reserva que debe dólares y tiene saldo a favor en pesos figura, antes que nada, **"con deuda"**.

5. **Antes de "soltar" la edición/borrado de cobros, verifiqué que no quede agujero.** Confirmé que los métodos de editar/borrar pago **ya tienen su propio candado fiscal** (no se puede tocar un cobro con recibo o factura). Así que sacar el candado "por estado" es seguro: el candado fiscal queda. Lo dejé como **precondición**: si en algún lado faltara, hay que ponerlo en el mismo cambio.

6. **Dejé escrita la consulta exacta** que decide si una reserva cancelada se puede "reabrir" (solo si no hubo nota de crédito, ni saldo a favor, ni devolución del operador).

7. **Subí a regla dura** que el control de "se puede cobrar" tiene que correr **después** de recalcular el saldo, no antes (si no, da resultados falsos).

8. **Corregí rutas y nombres** que estaban mal citados (la carpeta del código y unos números de línea).

9. **Aclaré qué necesita firma del contador antes de construir** (4 cosas puntuales: recibo en otro período, cobro vs nota de crédito vs incobrable, factura tardía sobre reserva cerrada, y el ancla de la devolución del operador) y qué avanza sin esperar al contador (todo el desacople, la visibilidad y los controles de estado).

---

## 14. Implementación backend (2026-06-16) — E1–E7

Implementado el subconjunto que **no** necesita contador. Sin migración (todo regla/derivado). Suite unit verde (`!~Integration`): **1888 pasados, 0 fallidos**. No hay frontend en este alcance (las pantallas van por gate UX aparte).

| Etapa | Qué se hizo | Archivos principales |
|---|---|---|
| **E1** | `EstadoReserva.SaleFirmStatuses` (5 firmes, incl. Closed) + `IsSaleFirmStatus`. `Reserva.IsCollectable()` redefinido a **venta firme + `Balance > 0`**. `EnsureCollectable()` con dos mensajes (`NotSaleFirmForChargeMessage` / `NoPendingBalanceForChargeMessage`). `ActiveCollectionStatuses` se conserva con su semántica acotada (operativo vivo). Aplicado en alta: `PaymentService.CreatePaymentAsync`, `ReservaService.AddPaymentAsync(int)` (sin cambio textual: siguen llamando `EnsureCollectable`; cambia el comportamiento). | `TravelApi.Domain/Entities/Reserva.cs` |
| **E2** | Split de listas: `FinancePositionService.ActiveReceivableStatuses` → **`ReceivableDebtStatuses` = `SaleFirmStatuses`** (incluye Closed). Redirigidos: `FinancePositionService` (AR global/por cliente/ranking), `CustomerService` (saldo del cliente), `ReportService` (dashboard/receivables/Excel), `TreasuryService` (escalar AR, antes lista inline propia sin Closed → ahora cierra con el por-moneda), `PaymentService` worklist/summary (`CollectableDebtStatuses`), `AlertService` ("terminado y debe": tercer OR para Closed con `Balance>0`). | `FinancePositionService.cs`, `CustomerService.cs`, `ReportService.cs`, `TreasuryService.cs`, `PaymentService.cs`, `AlertService.cs` |
| **E3** | Eliminado el gate de **estado** para editar/borrar cobro en los 4 call-sites (`PaymentService.UpdatePaymentAsync`/`DeletePaymentAsync`; `ReservaService.UpdatePaymentAsync`/`DeletePaymentAsync`). Verificado en cada uno que conservan su guard **fiscal** (`MutationGuards`/`DeleteGuards` recibo/CAE) y de **puente** (sobrepago/FC4). Helper `EnsureReservaPaymentsEditableAsync` + `Reserva.EnsurePaymentsEditable()` **borrados** (sin uso). `AnnulPaymentAsync` (ADR-032) sigue siendo la salida con rastro en terminal. | `PaymentService.cs`, `ReservaService.cs`, `Reserva.cs` |
| **E4** | `CancelServiceAsync`: gate de **estado vivo** (`ActiveCollectionStatuses`) al inicio, antes del candado fiscal. Revert `Cancelled → InManagement`: agregado a `AllowedRevertTransitions` + **gate duro D2** (query sobre `BookingCancellations`: `CreditNoteInvoiceId != null ∨ ReceivedRefundAmount > 0 ∨ ClientCreditEntry con BookingCancellationId de esta reserva`) en `RevertStatusAsync` y en `GetRevertOptionsAsync` (misma query, para no ofrecer un revert que vaya a 409). Hard-block CAE existente corre antes. | `BookingCancellationService.cs`, `ReservaService.cs` |
| **E5** | FC4 (`ClientCreditService`): `IsCollectableStatus` → `IsSaleFirmStatus` (acepta destino Closed con deuda). Conserva EXACTAMENTE `BusinessInvariantViolationException` + `INV-096` y la validación per-moneda `INV-095`. NO usa `IsCollectable()`/`EnsureCollectable()` escalar. | `ClientCreditService.cs` |
| **E6** | Solo **visibilidad**: bucket de alerta `StuckOperatorRefunds` (reservas `PendingOperatorRefund` con `ClientCreditEntry.RemainingBalance > 0` ligado por `BookingCancellationId`). NO escribe `Cancelled` en el allocate (la salida →Cancelled ya existe en `OnAllCreditConsumedAsync`). La baja/devolución del remanente NO se implementó (espera contador). | `AlertService.cs`, `AlertsResponse.cs` |
| **E7** | Estado de cobro derivado **por moneda** (`ReservaCollectionStatus`: `ConDeuda`/`SaldoAFavor`/`Saldado`, "ConDeuda" gana). Expuesto en `ReservaDto.CollectionStatus` y `ReservaListDto.CollectionStatus`, calculado desde `PorMoneda` (sin persistir, sin migración). | `ReservaCollectionStatus.cs`, `ReservaDto.cs`, `ReservaListDto.cs`, `ReservaService.cs` |

**Las DOS listas y dónde se usa cada una (invariante de diseño B1):**
- **`EstadoReserva.ActiveCollectionStatuses`** = `{InManagement, Confirmed, Traveling, ToSettle}` — "venta operativa viva (pre-cierre)". La usa: `ReservaService.MarkSourceLeadAsWonIfReservaIsFirmAsync` (lead ganado — **NO** debe incluir Closed) y `BookingCancellationService.CancelServiceAsync` (cancelar servicio solo en reserva viva).
- **`FinancePositionService.ReceivableDebtStatuses`** (= `EstadoReserva.SaleFirmStatuses` = lo anterior **+ Closed**) — "tiene cuenta por cobrar / deuda viva cobrable". La usa: AR (global/cliente/ranking), saldo del cliente, dashboard/receivables/Excel, escalar AR de tesorería, worklist/summary de cobranza, alerta "viajó/terminó y debe". La cobrabilidad de alta (`Reserva.IsCollectable`) usa `SaleFirmStatuses` **+ `Balance > 0`**.

**Tests (mín. del pedido) — todos verdes:** cobrar Finalizada (Closed) con deuda → permitido; cobrar en Quotation/Budget/Lost → rechazado; Cancelled/PendingOperatorRefund → rechazado; deuda de Finalizada aparece en AR/cobranza/cliente/dashboard/alerta; lead-won NO se dispara al pasar a Closed; FC4 conserva INV-096/INV-095 y acepta destino Closed con deuda; editar/borrar gateado por lo fiscal y no por estado (terminal sin recibo → permitido; con recibo → bloqueado por fiscal); CancelService rechaza en reserva muerta; revert Cancelled→InManagement con la query (sin huella permite, con NC/crédito bloquea); estado de cobro por moneda (deuda USD + saldo a favor ARS → "ConDeuda"); puentes internos siguen funcionando. Archivos de test: `Adr033DecoupledCollectionTests.cs` (nuevo) + `Adr032CollectableStateRuleTests.cs` (actualizado a la regla nueva) + `FaseDStateSetTests.cs` (Closed ahora visible/cobrable) + ajustes de datos en `AssistanceBalanceRecalcTests.cs`/`Adr020LockGuardTests.cs` (sembrar `Balance > 0` para el guard D3).
