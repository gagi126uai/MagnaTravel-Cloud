# ADR-043 — Mesa de revisión de cambios (renglón por evento, acciones correctivas, gate de facturar)

- **Estado:** Aprobado con cambios (revisión de `software-architect-reviewer` incorporada, 2026-07-06). Listo para implementación por fases (§11).
- **Fecha:** 2026-07-06
- **Autor:** software-architect
- **Insumos:** `docs/explicaciones/2026-07-06-reglas-mesa-revision-cambios.md` (matriz E1–E5, `travel-agency-domain-expert`), decisiones cerradas del dueño (puntos 1–10 del pedido).
- **Relacionado:** [[ADR-027]] (marca "confirmada con cambios"), [[ADR-020]] (ciclo de vida), [[ADR-035]] (capacidades por estado), [[ADR-037]] (desacople facturación), [[ADR-002]]/[[ADR-042]] (anulación/NC-ND), Tanda 4 (vigía de coherencia W1–W5), Tanda 5 (avisos con `ResolutionKey`).

> Este ADR NO incluye código de producción. Define modelo de datos, disparadores, contratos, gates, apagado derivado, migración, rollback y estrategia de test. La UI concreta pasa por el gate `ux-ui-disenador` antes de implementarse (regla del dueño). D1 (NC/ND) ya está cerrado por el dueño como "dos pasos" (punto 5); D2/D3/D5 cerrados (puntos 6/7/9); quedan abiertas las preguntas de la sección 12.

---

## 1. Contexto (estado actual verificado)

Todo lo de abajo está verificado leyendo el código en `HEAD` (no asumido).

### 1.1. La marca hoy

- `Reserva.HasUnacknowledgedChanges` (bool) + `ChangesPendingSince` (fecha pegajosa) + `ChangesAckByUserId/Name/At`. La marca vive **solo en Confirmed/estados vivos**; la baja **una persona**.
- Se prende por **dos caminos**:
  - **Camino A (E3 precio/costo)** — `ReservaService.MarkUnacknowledgedChangesIfLiveAsync` (llamado desde `UpdateBalanceAsync(int, PendingServiceChange?)`, el chokepoint post-recálculo de saldo). Graba filas `ReservaPendingChange` (una por campo `SalePrice`/`NetCost`; enum `PendingChangeFields`). **Único camino con renglón estructurado.**
  - **Camino B (E1/E4/E5)** — `ReservaAutoStateService.MarkNeedsReview` (llamado desde `EvaluateAndApplyAsync`, que corre tras cada mutación de servicio vía `UpdateBalanceAsync`). Prende el flag y escribe **texto libre** en `LastRegressionReason`/`LastRegressionAt`; **no** graba filas. Usa `ServiceResolutionRules.GetUnresolvedLiveServiceLabels` / `HasAnyLiveService` para armar el texto.
- **"Dar OK"** = `ReservaService.AcknowledgeChangesAsync` (endpoint `acknowledge-changes`, `ReservasController`): baja flag + limpia `ChangesPendingSince` + `LastRegression*` + graba `ChangesAck*` + **borra TODAS** las filas `ReservaPendingChange`. Idempotente. Es un **aceptar-todo ciego**: no recalcula, no distingue plata, no toca factura.

### 1.2. Gates hoy

- **Pase a "En viaje": gateado por la marca** en el **job nocturno** (`ReservaLifecycleAutomationService`) **y en el pase manual**. (GAP: el dueño quiere que el manual pase **con aviso + constancia**, no bloqueado — hoy está bloqueado.)
- **FACTURAR: NO gateado.** `ReservaCapabilityPolicy.EvaluateInvoiceSale` solo mira estado (`InvoiceableStatuses` = {Confirmed, Traveling, Closed}, ADR-037). `ReservaCapabilityContext` **no transporta el flag**. (**GAP #2**.)
- **Cobrar: NO gateado** por la marca (correcto: la decisión es aviso, no bloqueo).

### 1.3. Piezas transversales que se reutilizan (no se reinventan)

- **Punto único de transición de estado:** `ReservaStatusTransitioner.ApplyAsync` + tabla declarativa `ReservaStateCleanupRules`. Entrar a Confirmed **nunca** limpia la marca.
- **Avisos:** `NotificationResolutionKeys.ForTyped(NotificationTypes.ReservaNeedsReview, reservaId)`; dedup por clave viva; `INotificationService.ResolveByKeyAsync(key)` apaga los vivos. Auto-resolución en `NotificationCauseResolutionRules` (W4) — su `ReservaCauseState` lee `HasUnacknowledgedChanges`.
- **Vigía de coherencia:** `CoherenceChecks` (W1 limpia marcas colgadas en terminales; W3 plata; W2/W5 reportan). W4 apaga avisos zombie.
- **Capacidades:** `ReservaCapabilityPolicy` (pura) + contexto armado en `ReservaService` detalle. Gate real de escritura vive en los services (la capacidad es compuerta de UI).
- **Cancelar servicio:** `BookingCancellationService.CancelServiceAsync` (soft-cancel). **Anular:** `BookingCancellationService` Draft/Confirm (ADR-002/042). **Reprogramar:** `BookingService.RescheduleAsync`. **Recalcular saldo:** `ReservaMoneyPersister` vía `UpdateBalanceAsync` (escritor único de plata).
- **Front:** `avisosFicha.js` (deriva avisos informativos; ya lee `reserva.hasUnacknowledgedChanges`), `moneyStatus.js` (fuente única de plata en UI). `ReservaDetailPage.jsx` ya renderiza el banner "con cambios" desde `hasUnacknowledgedChanges` + `lastRegressionReason`.

---

## 2. Problema

La marca es booleana con detalle a medias. La "mesa" pedida requiere:

1. **Un renglón por evento** con antes→después para los **5 eventos** (hoy solo E3 tiene renglón — **GAP #1**).
2. **Estado por renglón** + **acciones correctivas** por renglón (hoy solo hay aceptar-todo ciego — **GAP #4**).
3. **Colapsado** de cambios repetidos sobre el mismo servicio (punto 6) manteniendo el rastro individual.
4. **Disparador de reprogramación de fecha** (hoy inexistente — **GAP #3**).
5. **Gate duro de FACTURAR** con revisión abierta (**GAP #2**).
6. **Aviso + constancia** (no bloqueo) en cobrar y en pase manual a viaje.
7. **Apagado derivado**: resolver el último renglón apaga marca + cartel + campanita + aviso, reutilizando `ResolutionKey`.
8. **Pendiente fiscal explícito** "emitir NC/ND por la diferencia" cuando la reserva ya está facturada (punto 5, D1=B).
9. **Migración** de las reservas que hoy tienen la marca con el modelo viejo (texto libre) **sin perder nada**.

---

## 3. Opciones consideradas (decisión de fondo: modelo de datos)

### Opción A — Extender `ReservaPendingChange`

Agregar a la tabla existente: `LineType` (E1–E5), `LineStatus`, colapsado, snapshot genérico antes/después.

- **Contra (decisivo):** `ReservaPendingChange` es hoy un **log append-only de movimientos** (una fila por edición de campo, nunca se pisa — ver comentario de la entidad). El renglón de la mesa es una **unidad de decisión colapsada y con máquina de estados**. El punto 6 pide **las dos cosas a la vez**: renglón colapsado para decidir + movimientos individuales en auditoría. Meterlos en la misma tabla obliga a elegir uno y rompe el otro. Además la lógica de enmascarado de costo (`ApplyCostMaskingAsync`) y W4 dependen del shape actual.
- **A favor:** una sola tabla, sin migración de esquema grande.

### Opción B (recomendada) — Nuevo agregado "Mesa" + reutilizar `ReservaPendingChange` como log de movimientos

- `ReservaChangeReview` (la mesa: **una abierta por reserva**) + `ReservaChangeReviewLine` (los renglones, con `LineType`, snapshot antes→después, `LineStatus`, pendiente fiscal).
- `ReservaPendingChange` **se conserva** como el **log de movimientos individuales** que respaldan un renglón colapsado (punto 6) y mantiene el enmascarado de costo ya probado.
- La **marca** `HasUnacknowledgedChanges` **sigue siendo la fuente booleana persistida** que todo el resto del sistema ya lee (gates, W1, W4, avisos, cleanup rules); la mesa es el **detalle estructurado detrás del flag**, en lockstep con él.

### Opción C — Derivar la marca de "existe review abierta" (query on-the-fly)

Eliminar el bool y calcular `HasUnacknowledgedChanges = EXISTS(review abierta)` en cada lectura.

- **Contra (decisivo):** el bool lo leen **muchos** puntos hoy como columna: el contexto de capacidades (`file.HasUnacknowledgedChanges`), el snapshot de W4 (`ReservaCauseState`), el vigía W1, la cleanup table, el front (`reserva.hasUnacknowledgedChanges`). Derivarlo en caliente obliga a tocar todos esos read-paths y a garantizar consistencia flag↔review en cada uno. Es complejidad distribuida sin beneficio real (la mesa igual necesita persistir sus renglones). Va contra "no introducir complejidad distribuida sin razón clara".

**Elección: Opción B.** La marca queda como **proyección booleana** de "hay review abierta", escrita por **un único servicio** que abre/cierra la review y setea el flag en la misma unidad de trabajo, con un **invariante** `review.Status == Open ⟺ HasUnacknowledgedChanges == true` custodiado por un check del vigía (W6, §8).

---

## 4. Decisión — Modelo de datos

### 4.1. `ReservaChangeReview` (la mesa)

| Campo | Tipo | Notas |
|---|---|---|
| `Id` | int PK | |
| `ReservaId` | int FK → TravelFiles | **Única abierta por reserva** (índice único filtrado `WHERE Status='Open'`). Cascade delete. |
| `Status` | string(20) | `Open` / `Closed`. |
| `OpenedAt` | datetime | Espeja `ChangesPendingSince` (fecha pegajosa: no se re-pisa). |
| `ClosedAt` | datetime? | |
| `ClosedByUserId/Name` | string? | Quién resolvió el último renglón (o "aceptar todo"). |

- **Regla R1 (una mesa por reserva):** si ya hay `Open`, un evento nuevo **agrega renglón**, no abre otra. `OpenedAt` se mantiene (idempotente, igual que hoy `ChangesPendingSince`).

### 4.2. `ReservaChangeReviewLine` (el renglón)

| Campo | Tipo | Notas |
|---|---|---|
| `Id` | int PK | |
| `ReviewId` | int FK → ReservaChangeReview | Cascade. |
| `LineType` | string(30) | `OperatorCancelledService` (E1) / `OperatorRescheduledDate` (E2) / `PriceChanged` (E3) / `ServiceAdded` (E4) / `ReservaWithoutServices` (E5). |
| `ServiceKind` | string(50)? | "Hotel"/"Aéreo"/… (negocio, para mostrar). Null en E5. |
| `ServiceDescription` | string(300)? | "Hotel NH Centro". |
| `ServicePublicId` | Guid? | Linkea el renglón con la fila del servicio (E5 = null). **Clave de colapsado** (§4.4). |
| `Severity` | string(10) | `Minor` / `Major`. Derivado en el disparador (regla dura: toca plata/fechas o deja sin servicios ⇒ Major). |
| `BeforeText` | string(400) | Antes en castellano ("confirmado" / "sale 10/03" / "$150.000" / "no estaba"). |
| `AfterText` | string(400) | Después. Se **refresca** ante nuevo movimiento del mismo servicio (R8). |
| `BeforeDate` / `AfterDate` | datetime? | Solo E2 (y E2+E3 combinado, §4.5). |
| `OldValue` / `NewValue` / `Currency` | decimal?/string? | Solo cuando toca plata (E3, E4, E1-con-baja). Enmascarado de costo cuando aplique (reusar `ApplyCostMasking`). |
| `LineStatus` | string(25) | Primera versión (§11.0): `Pending` / `Accepted` / `LineCancelled` / `Annulled` / `Acknowledged` (constancia en viaje). |
| `ResolutionKind` | string(15)? | Sub-motivo de resolución cuando `LineStatus=Accepted`: `Accepted` / `Corrected` / `Substituted`. Reemplaza estados terminales separados en la primera versión (§11.0), sin perder el rastro de qué se hizo. |
| `FiscalFollowUp` | string(15) | `None` / `PendingCreditNote` / `PendingDebitNote` / `PendingInvoice` / `Done`. Punto 5 (D1=B): al aceptar plata sobre reserva ya facturada, el renglón queda "resuelto en lo operativo" pero con `FiscalFollowUp != None` hasta emitir la nota. **Un renglón con `FiscalFollowUp` pendiente NO cuenta como resuelto** para apagar la marca. |
| `ResolvedByUserId/Name` | string? | Auditoría de la acción (R7). |
| `ResolvedAt` | datetime? | |
| `Version` (xmin) | uint | Concurrencia optimista para idempotencia de acciones (§6.3). |

- **Estados terminales de renglón** (cuenta como "resuelto"): `Accepted` (incluye `ResolutionKind` Corrected/Substituted; con `FiscalFollowUp` en `None`/`Done`), `LineCancelled`, `Annulled`, `Acknowledged`.
- **NO terminal:** `Pending`, o cualquier `LineStatus` con `FiscalFollowUp` pendiente.

### 4.3. Relación con `ReservaPendingChange` (log de movimientos)

- `ReservaPendingChange` **gana** una FK nullable `ReviewLineId` (→ `ReservaChangeReviewLine`) para colgar cada movimiento individual del renglón que lo agrupa (punto 6: "los movimientos individuales quedan en auditoría").
- Se conserva su shape (masking + W-nada nuevo). Ya **no** se borra en el "Dar OK": la limpieza pasa a estar atada al cierre de la review (§5) — pero como log de auditoría, la política preferida es **conservarlo** (marcar como histórico al cerrar la review, no borrar). Ver §5.3.

### 4.4. Colapsado (punto 6, D2=B)

- **Clave de colapsado = `(ReviewId, LineType-familia, ServicePublicId)`** con la review abierta.
- Dos cambios de precio sobre el mismo servicio sin resolver ⇒ **un** renglón `PriceChanged`: `OldValue` = valor del **primer** movimiento (original), `NewValue`/`AfterText` = **último** (final). Cada edición agrega una fila `ReservaPendingChange` colgada del renglón (rastro completo).
- **E3 borde (releer, no confiar):** `Accept` lee el `NewValue` **vigente** del renglón al momento de aceptar (recomputado desde el servicio), nunca el valor pintado en el front.

### 4.5. Reprogramación + precio en el mismo movimiento (punto 7, D3)

- **Un** renglón sobre el mismo servicio con `LineType = OperatorRescheduledDate` **más** columnas de plata (`Old/NewValue`) y de fecha (`Before/AfterDate`) pobladas. `BeforeText/AfterText` describen ambas cosas. La decisión es sobre el servicio como un todo.

---

## 5. Decisión — Marca derivada y reemplazo de "Dar OK"

### 5.1. La marca es proyección de la review

Un servicio nuevo `ChangeReviewService` es el **único** que abre/cierra la review y **en la misma unidad de trabajo** escribe el flag:

- **Abrir/agregar renglón** ⇒ `HasUnacknowledgedChanges = true`, `ChangesPendingSince ??= now` (pegajoso), `LastRegressionReason` = texto derivado del/los renglón(es) abiertos (para el banner que el front ya lee).
- **Cerrar la review** (último renglón resuelto **y** sin `FiscalFollowUp` pendiente) ⇒ `HasUnacknowledgedChanges = false`, `ChangesPendingSince = null`, `LastRegression* = null`, `ChangesAck* = actor`, `review.Status = Closed`, **y** `ResolveByKeyAsync(ForTyped(ReservaNeedsReview, reservaId))`.
- **Invariante:** `review.Status==Open ⟺ HasUnacknowledgedChanges==true`. Se testea (cross-check) y lo vigila W6.

- **B3 — Atomicidad (restricción DURA de implementación):** `ChangeReviewService` **escribe siempre al `DbContext`/ChangeTracker del caller** (el flag de la reserva + la review + los renglones + los movimientos `ReservaPendingChange`) y se persiste en **el MISMO `SaveChanges` del flujo que dispara el evento** (la mutación de servicio, el recálculo de saldo). **Nunca** hace un `SaveChanges` propio ni abre transacción aparte. Razón: si el flag y la review se guardaran en dos commits distintos, existiría una ventana con `flag=true` sin review (o al revés) que W6 reportaría como incoherencia falsa (ruido), y peor, un crash entre ambos commits dejaría el invariante roto de forma persistente. Esto es lo **contrario** al patrón "SaveChanges separado self-healing" de ADR-026/027 (M1): acá el acople flag↔review debe ser atómico. El disparador E3 hoy corre como `SaveChanges` separado (`MarkUnacknowledgedChangesIfLiveAsync`, §6.1); al enchufar la review, ese punto debe pasar a escribir en el tracker del recálculo de saldo, no en un save propio — es un cambio de encuadre transaccional que el implementador debe respetar.

### 5.2. Qué reemplaza a "Dar OK" (GAP #4)

- El endpoint actual `acknowledge-changes` (aceptar-todo ciego) **se reemplaza** por dos cosas:
  1. **Acción por renglón** (§6): cada renglón se resuelve con su acción; resolver el último cierra la mesa.
  2. **"Aceptar todo tal cual"** (punto 2, R3): endpoint que **solo procede si TODOS los renglones abiertos son `Minor`**. Server-side, si hay **al menos un** `Major` ⇒ `409` con motivo legible ("Hay cambios que tocan plata o fechas: revisalos uno por uno"). Marca todos los `Minor` como `Accepted` y cierra la review.
- **Compatibilidad:** el producto está en desarrollo sin clientes (memoria del dueño) ⇒ se reemplaza la semántica del endpoint sin ceremonia de versionado. La ruta puede reusarse (`POST …/change-review/accept-all-minor`) o renombrarse; el front migra en la misma tanda.

### 5.3. Limpieza de `ReservaPendingChange`

- Hoy `AcknowledgeChangesAsync` y `ReservaStateCleanupRules` **borran** las filas. Con el nuevo modelo, al **cerrar** la review por resolución humana **se conservan** las filas (auditoría, punto 6/R7), marcando el renglón `Closed`.
- **Excepción — transiciones que descartan la revisión** (terminales / revert a pre-venta, tabla `ReservaStateCleanupRules`): siguen apagando la marca; ahí la review abierta se **cierra como `Discarded`** (nuevo `Status`), no se pierde el rastro. `ReservaStatusTransitioner.ApplyAsync` debe además cerrar la review cuando `ClearUnacknowledgedChanges` es true (extender la tabla declarativa, no duplicar criterio).
- **M3 — Dependencia DURA de secuenciación:** la extensión de `ReservaStateCleanupRules` / `ReservaStatusTransitioner` para **cerrar la review** cuando se apaga la marca es **prerrequisito de la PRIMERA tanda que cree reviews** (Fase 2, §11). No puede quedar para después: si una tanda empieza a abrir reviews antes de que las transiciones a terminal las cierren, una reserva anulada/perdida quedaría con `flag=false` (lo limpia la cleanup rule) pero `review.Status=Open` ⇒ rompe el invariante flag↔review y dispara falsos W6. Por eso esta extensión va **junto con** el modelo de datos y **tiene test propio** (integración: anular una reserva con review abierta ⇒ review `Discarded` + flag off + sin finding W6).

---

## 6. Decisión — Disparadores (dónde se enganchan los 5 eventos)

Principio: enganchar en los **puntos únicos** por donde ya pasan los cambios, para no dispersar la lógica.

### 6.1. E3 (precio) — reusar el chokepoint existente

- `MarkUnacknowledgedChangesIfLiveAsync` (vía `UpdateBalanceAsync(int, PendingServiceChange?)`). En vez de solo agregar `ReservaPendingChange`, delega en `ChangeReviewService.UpsertLine(PriceChanged, colapsado por ServicePublicId)` y cuelga el movimiento. Sin nuevo chokepoint.

### 6.2. E1/E4/E5 (servicio cayó / se agregó / quedó vacía) — reusar el motor de estados

- `ReservaAutoStateService.MarkNeedsReview` ya corre tras cada mutación de servicio y ya distingue "servicios que dejaron de estar resueltos" vs "sin servicios vivos" (`GetUnresolvedLiveServiceLabels` / `HasAnyLiveService`). Se **enriquece** para emitir renglones estructurados en vez de solo texto:
  - servicio confirmado→cancelado por operador ⇒ `OperatorCancelledService` (E1);
  - servicio nuevo sin resolver que antes no existía ⇒ `ServiceAdded` (E4);
  - cero servicios vivos ⇒ `ReservaWithoutServices` (E5, renglón único sin `ServicePublicId`).
- **Distinguir E1 de E4 — regla EXPLÍCITA del dueño (cierra Q2, reemplaza la heurística `ConfirmedAt` anterior):**
  - un servicio que **llegó a estar confirmado** por el operador (tuvo `ConfirmedAt` sellado en su vida) y ahora está cancelado ⇒ **E1** (`OperatorCancelledService`);
  - un servicio **agregado** que **nunca** llegó a confirmarse ⇒ **E4** (`ServiceAdded`).
  - **Borde (respuesta del dueño):** un renglón **E4** cuyo servicio el operador **cancela ANTES** de que se revise el renglón: **si el servicio nunca llegó a confirmarse, el renglón E4 se RETIRA solo** (era un agregado que se deshizo, no queda nada que revisar); **si llegó a confirmarse en el ínterin, el renglón pasa a E1** (ya hay un compromiso que se cayó). La condición operativa es "¿existe algún `ConfirmedAt` en la historia del servicio?" — dato ya presente en la entidad, no inventa columna; la **regla** de negocio (retirar vs promover a E1) es explícita, no una adivinanza.
- **R8 (refresh, no cerrar):** si el operador re-confirma el servicio antes de resolver el renglón, el renglón **sigue abierto** pero `AfterText` se refresca ("el operador lo volvió a confirmar"). El motor ya refresca el texto del motivo hoy; se extiende al renglón. El **Accept** de un renglón E1/E2 **recomputa el "después" vigente desde el servicio** en el momento de aceptar (mismo criterio que E3 en §4.4): el usuario nunca acepta un snapshot viejo.

### 6.3. E2 (reprogramación de fecha) — **disparador nuevo** (GAP #3)

- Hoy una fecha nueva que mantiene el servicio confirmado **no** prende nada. Se agrega captura del **delta de fecha** en el mismo descriptor `PendingServiceChange` que ya viaja por `UpdateBalanceAsync` desde los `Update*Async` de `BookingService` (extender el descriptor con `OldStartDate/NewStartDate`). Así:
  - editar la fecha de un servicio confirmado ⇒ renglón `OperatorRescheduledDate` (E2);
  - si en la misma edición cambió también el precio ⇒ **un** renglón con ambas columnas (§4.5), naturalmente, porque es el mismo descriptor.
- **`RescheduleAsync` (reprogramación masiva) NO abre review** (decisión del dueño, cierra Q1): es una **acción deliberada del usuario** con su propia autorización y auditoría, no un cambio "que vino de abajo". Queda como acción directa con su rastro; es además una de las **acciones correctivas** del renglón E2 (§7).
- **Borde E2 (fecha inviable):** si `AfterDate` cae fuera de la ventana del viaje (después del regreso), el renglón marca `Severity=Major` + un flag `IncompatibleDate` que impide `Accept` a ciegas (obliga a Corregir/Sustituir/Anular). Reusa el cálculo de itinerario de `ReservaScheduleCalculator`.

---

## 7. Decisión — Acciones por renglón (contratos)

Recurso REST bajo la reserva (dueño de la mesa):

- `GET  /api/reservas/{id}/change-review` → la mesa vigente (renglones + `LineStatus` + acciones habilitadas + `Severity` + pendiente fiscal). Enmascara costos según permiso.
- `POST /api/reservas/{id}/change-review/lines/{lineId}/accept`
- `POST …/{lineId}/correct` (body: nuevo monto/dato)
- `POST …/{lineId}/substitute` (body: servicio de reemplazo)
- `POST …/{lineId}/cancel-line`
- `POST …/{lineId}/add-service` (E4/E5: alta del servicio faltante)
- `POST …/{lineId}/annul` (anula la reserva completa)
- `POST …/{lineId}/acknowledge` (constancia en viaje, punto 8)
- `POST /api/reservas/{id}/change-review/accept-all-minor` (R3, gateado all-minor)

### 7.1. Permisos

- Todas: `[RequirePermission(ReservasEdit)]` + `[RequireOwnership(Reserva, bypass ReservasViewAll)]` — **misma** política que `acknowledge-changes` hoy (verificado). Vendedor no opera reservas ajenas.
- `annul` y las que emiten NC/ND exigen además el permiso fiscal correspondiente (el que ya piden `BookingCancellationService` / `InvoiceService`). La mesa **no** relaja esos permisos: delega en los services, que revalidan todo.

### 7.2. Qué transición/flujo dispara cada acción (reutilización)

| Acción | Reutiliza | Efecto | Transición estado |
|---|---|---|---|
| Accept (E2/E3/E4) | `UpdateBalanceAsync`→`ReservaMoneyPersister` (recalcula saldo) | Sella `NewValue`/fecha vigente; deja traza (R4). Si ya facturada y mueve total ⇒ `FiscalFollowUp = PendingCredit/Debit/Invoice` (D1=B, punto 5) | Ninguna (sigue Confirmed) |
| Correct | edición de servicio existente (`Update*Async`) con el monto/dato corregido | Igual que Accept pero con valor negociado | Ninguna |
| Substitute | alta de servicio (existente) + `CancelServiceAsync` del caído | Nuevo servicio "solicitado"; recalcula | Ninguna (motor podrá re-confirmar) |
| Cancel-line | `BookingCancellationService.CancelServiceAsync` (soft-cancel) | Servicio queda cancelado; si ya facturada ⇒ `FiscalFollowUp=PendingCredit` | Ninguna |
| Add-service (E4/E5) | alta de servicio (existente) | Suma servicio; sube deuda; si facturada ⇒ `PendingInvoice`/`PendingDebit` | Ninguna |
| Annul | `BookingCancellationService` Draft/Confirm (ADR-002/042) | NC total + ND multa si la hay; saldo a favor / reembolso por el flujo formal | Vía `ReservaStatusTransitioner` (Cancelled/PendingOperatorRefund) → **cierra la review como `Discarded`** por cleanup rules |
| Acknowledge (constancia, en viaje) | ninguno (solo sella) | No muta plata; deja quién/cuándo (punto 8). Plata se corrige aparte por NC/ajuste | Ninguna (Traveling) |

### 7.3. Concurrencia e idempotencia (regla EXACTA — B2)

Toda acción sobre un renglón **exige el `Version` (xmin) del renglón** en el request. La resolución es determinista:

1. **Renglón `Pending` + `Version` coincide** ⇒ ejecuta la acción.
2. **Renglón `Pending` + `Version` STALE** (alguien lo movió, o el renglón se refrescó — E1/E3 colapsó otro movimiento) ⇒ **`409 Conflict` con instrucción de recargar**. **Nunca** se procede en silencio ni se trata como no-op: el usuario podría estar aceptando un "después" viejo. El front recarga la mesa y el usuario re-decide sobre el valor vigente.
3. **Renglón ya en estado terminal + MISMA acción** ⇒ **no-op idempotente** que devuelve el estado actual (retry de red del mismo click; mismo criterio que `AcknowledgeChangesAsync` hoy).
4. **Renglón ya en estado terminal + acción DISTINTA** ⇒ `409` (el renglón ya se resolvió de otra forma; no se re-resuelve).

- **Recompute del "después" (liga con §4.4 y §6.2):** para E1/E2/E3 el `Accept` **relee el valor/fecha vigente desde el servicio** antes de sellar; no confía en lo pintado. Un `Version` stale es justamente la señal de que hubo un movimiento nuevo desde que el front pintó el renglón.
- **Idempotencia fiscal:** las acciones que emiten comprobante (annul, NC/ND del paso 2) llevan además la **idempotencia ya existente** de ese flujo (ADR-042 lock pesimista `FOR UPDATE`; `Adr041_M6` idempotency key). La mesa **no** inventa idempotencia fiscal nueva: delega.

### 7.4. Pendiente fiscal (D1=B, punto 5) — dos pasos

- Sobre reserva **ya facturada**, `Accept`/`Cancel-line`/`Add-service` que mueve el total: **paso 1** recalcula y deja el renglón con `FiscalFollowUp = Pending{CreditNote|DebitNote|Invoice}` + botón "Emitir NC/ND por la diferencia" en el propio renglón. **Paso 2** (acto consciente) emite contra AFIP por el flujo fiscal existente y pasa `FiscalFollowUp = Done`. Recién ahí el renglón cuenta como resuelto. Emitir un comprobante fiscal **nunca** se dispara de costado (regla del candado fiscal).

- **M2 — Pendiente fiscal de larga data enganchado a la vigilancia existente (no se reinventa):**
  - **Fallo de emisión:** si el paso 2 (emitir la NC/ND del renglón) **falla contra AFIP**, NO queda muerto en el renglón: se encola en la **bandeja de reintento ya existente** (mismo patrón "Reintentar ND" del caso #F-2026-1025 / retry de anulación). El renglón mantiene `FiscalFollowUp = Pending…`; el reintento exitoso lo pasa a `Done` y recién ahí puede cerrar la review.
  - **Envejecimiento:** un `FiscalFollowUp` que lleva **N días** abierto genera un **aviso** (misma infraestructura de notificaciones con `ResolutionKey`, que se apaga solo cuando pasa a `Done`). `N` es **configurable** (setting operacional, junto a los demás umbrales de `OperationalFinanceSettings`); default sugerido **3 días hábiles**, a confirmar con el dueño/contador. Esto evita que una reserva quede con la marca puesta indefinidamente por una nota nunca emitida.

---

## 8. Decisión — Gates

### 8.1. FACTURAR bloqueado con revisión abierta (GAP #2, R5)

- **Capacidad (UI):** agregar `HasUnacknowledgedChanges` a `ReservaCapabilityContext` (ya disponible como `file.HasUnacknowledgedChanges` donde se arma el contexto). `EvaluateInvoiceSale`: si la marca está puesta ⇒ `Cap.No("Esta reserva tiene cambios sin revisar. Revisalos antes de facturar.")`.
- **Gate real (escritura):** `InvoiceService` (emisión de **factura de venta**) debe rechazar server-side si `HasUnacknowledgedChanges`. La capacidad es compuerta de UI; el guard fino manda (regla ADR-035). Test cruzado: la política nunca dice `Allowed=true` para algo que el guard rechaza.
- **Precisión importante:** el bloqueo es sobre **emitir factura de venta nueva** (`CanInvoiceSale`). **NO** bloquea la **NC/ND correctiva** que es la acción del renglón (D1=B): esa va por `CanEmitCreditDebitNote` / el flujo de nota, que es un carril distinto. Bloquear la nota rompería el paso 2 de §7.4.

### 8.2. Cobrar y pase a viaje = aviso + constancia (R6)

- **Cobrar:** **no** se bloquea (correcto hoy). Se agrega un **aviso fuerte** en UI y, si el usuario avanza, se registra **constancia automática** (quién/cuándo avanzó con revisión abierta) en la auditoría del cobro. **Sin motivo escrito** (decisión del dueño, cierra Q3). No hay capability nueva de bloqueo.
- **Pase a "En viaje" — regla anti-deadlock (B1, decisión adoptada):** el aflojamiento del pase manual **NO es incondicional**. "En viaje" es **inmutable** (ADR-036): una vez ahí, la única acción de renglón disponible es `Acknowledge` (constancia), que **no** limpia un renglón Major ni un `FiscalFollowUp` pendiente. Si dejáramos entrar a Traveling una review con un renglón **Major** o **FiscalFollowUp** pendiente, esa review **nunca cerraría** ⇒ flag `true` para siempre ⇒ no se puede facturar ni tocar. Por eso:
  - **Job nocturno** (`ReservaLifecycleAutomationService`): **sigue frenado** por la marca (verificado hoy; el dueño lo mantiene).
  - **Pase manual, con revisión abierta:**
    - **Si TODOS los renglones abiertos son `Minor`** (no tocan plata/fechas, no dejan sin servicios) y **ninguno** tiene `FiscalFollowUp` pendiente ⇒ **aviso + constancia**: exige `acknowledgeReviewOpen: true` en el request y escribe la constancia en `ReservaStatusChangeLog` (Reason = "Avanzó a En viaje con revisión abierta, todos los cambios menores"). Sin ese flag ⇒ `409` con aviso.
    - **Si hay al menos un renglón `Major` o algún `FiscalFollowUp` pendiente** ⇒ **bloqueo DURO** (mismo criterio que facturar), con motivo legible: "Hay cambios que tocan plata o fechas sin resolver: resolvelos antes de pasar la reserva a En viaje." No hay flag que lo saltee.
  - **Vía de escape en Traveling (por si algo llegó igual — legacy/backfill):** la sub-acción "**emitir NC/ND**" del renglón **queda invocable en Traveling**. Está soportado por las capacidades existentes: `EvaluateCreditDebitNote` habilita la nota siempre que haya `HasLiveCae` (no depende de que la reserva sea editable), así que un renglón con `FiscalFollowUp` pendiente que quedó en Traveling **puede saldarse** emitiendo la nota → pasa a `Done` → la review puede cerrar. Sin esta vía, una reserva legacy que ya estaba En viaje con la marca quedaría trabada. **El gate manual anti-deadlock evita que esto ocurra en el flujo normal; la vía de escape cubre el residuo histórico.**
  - **Es un cambio de comportamiento** (hoy el manual está bloqueado sin matices); se destaca para el reviewer.

---

## 9. Decisión — Apagado derivado (cartel / campanita / alerta)

- **Fuente única:** todos derivan de `HasUnacknowledgedChanges` + la clave `ForTyped(ReservaNeedsReview, reservaId)`.
- **Cierre:** `ChangeReviewService` al cerrar la review baja el flag **y** llama `ResolveByKeyAsync(clave)` en la misma unidad de trabajo ⇒ cartel + campanita + aviso urgente se apagan solos (mecanismo Tanda 5, ya probado).
- **Red de seguridad (W4):** `NotificationCauseResolutionRules.IsCauseResolved` ya apaga el aviso `ReservaNeedsReview` cuando `!HasUnacknowledgedChanges`. **No cambia** (sigue leyendo el bool que ahora es proyección de la review). Correcto por construcción.
- **Front:** `avisosFicha.js` / `ReservaDetailPage.jsx` dejan de mostrar el banner de texto libre y muestran **la mesa** (renglones) cuando `hasUnacknowledgedChanges`. El banner "Dar OK" se reemplaza por la lista de renglones + "Aceptar todo tal cual" (solo si all-minor). `moneyStatus.js` sigue siendo la fuente de plata; los renglones de precio **leen** de ahí, no recalculan en el front. **Esto pasa por el gate `ux-ui-disenador` antes de implementarse.**

### 9.1. Vigía de coherencia — nuevo check W6

- **Sí necesita check propio.** W6: detectar incoherencia `flag ↔ review`:
  - `HasUnacknowledgedChanges==true` sin `ReservaChangeReview` abierta (o sin renglones abiertos), **o**
  - review `Open` con `HasUnacknowledgedChanges==false`.
- **Auto-repair: NO** (a diferencia de W1). Un flag sin review puede indicar un disparador que falló (evento perdido); auto-crear un renglón **taparía** ese bug. W6 **reporta** para que una persona mire (mismo criterio que W2/W5 con plata/multa). El caso "review abierta con flag apagado" sí es seguro re-sincronizar (subir el flag), pero por prudencia se reporta en la primera versión y se evalúa auto-repair después. **Recomendación, no bloqueante.**

---

## 10. Migración y rollback

### 10.1. Esquema

- `Adr043_M1`: crea `ReservaChangeReviews` + `ReservaChangeReviewLines` + FK `ReviewLineId` (nullable) en `ReservaPendingChanges`. Aditiva, EF puro, `Down` forward-only. Timestamp posterior a `Adr042_M1`.

### 10.2. Backfill de reservas ya marcadas (sin perder nada)

**M1 — Backfill RESUMIBLE, per-reserva-transaccional** (patrón `Adr027_M3`/`M4` de reparación legacy). Corre en un job idempotente de arranque (no en la migración de esquema), procesando **una reserva por transacción** para que un crash a mitad no deje trabajo a medias ni obligue a rehacer todo. Para **cada** reserva con `HasUnacknowledgedChanges==true` al momento del deploy:

1. **Chequeo de COMPLETITUD (no de mera existencia):** el re-run tras crash no alcanza con "¿ya hay review?". Se considera **ya migrada** solo si existe una `ReservaChangeReview` abierta **y** su conjunto de renglones es coherente con el estado actual (tiene los renglones de precio esperados desde `ReservaPendingChange` **y** los renglones no-precio esperados desde el estado de servicios). Si la review existe pero está **incompleta** (crash entre pasos), se **completa** (agrega los renglones faltantes), no se salta ni se duplica.
2. Crear (si falta) `ReservaChangeReview(Status=Open, OpenedAt = ChangesPendingSince ?? now)`.
3. **PRECEDENCIA explícita para no duplicar renglones** (regla de dedup del backfill):
   - **Primero E3 (precio) desde `ReservaPendingChange`:** colapsar por `ServicePublicId` a un renglón `PriceChanged` (original→final) y colgar los movimientos (`ReviewLineId`). Reusa datos exactos ya grabados. Registrar los `ServicePublicId` cubiertos.
   - **Después E1/E4/E5 desde el estado ACTUAL de los servicios**, con los **mismos** detectores de los disparadores (`ServiceResolutionRules` + la regla E1/E4 de §6.2). **Se omite** generar un renglón no-precio para un `ServicePublicId` que **ya** quedó cubierto por un renglón de precio en el paso anterior (evita dos renglones para el mismo servicio cuando el cambio fue de precio). Un mismo servicio con precio movido **y** caído se representa según §4.5 (renglón único con ambas dimensiones), no dos renglones.
4. **Preservación total:** el `LastRegressionReason` (texto libre) se copia a `LegacyNote` de la review (o a un renglón `LineType=LegacyReview` cuando no se pudo derivar ningún renglón estructurado), resoluble por las mismas acciones. **Nada se pierde**.
5. **Invariante post-backfill:** toda reserva marcada queda con review abierta y completa (flag↔review consistente). W6 lo verifica en la primera corrida y **no** debería reportar nada tras un backfill exitoso.

- **Idempotente y resumible:** por el chequeo de completitud del paso 1, correr el job dos veces (o tras un crash) converge al mismo estado sin duplicar renglones.
- **Advertencia (riesgo):** el paso 3 fotografía el estado **al momento del deploy**, no el evento original. Si entre el evento y el deploy el operador re-confirmó, el renglón reflejará la realidad **vigente** (que es lo que el dueño necesita para decidir hoy). Esto es **coherente** con R8 ("mostrar la realidad vigente, no hacer cancelar algo que ya volvió"). Se documenta explícitamente.

### 10.3. Rollback

- El feature es **aditivo**. Rollback = dejar de exponer los endpoints de la mesa y volver el front al banner + `acknowledge-changes` (que sigue existiendo hasta que se retire). Las tablas nuevas quedan inertes (no rompen lecturas viejas). El flag sigue funcionando igual (es la misma columna). **No hay pérdida de datos** en rollback: `ReservaPendingChange` se conservó.
- **Punto de no retorno:** una vez que la mesa empiece a **cerrar** reviews conservando `ReservaPendingChange` (en vez de borrarlas como el "Dar OK" viejo), volver al `acknowledge-changes` viejo re-instauraría el borrado. Aceptable (el viejo borra; no corrompe). Se documenta.

---

## 11. Plan de implementación por fases (reemplaza al plan anterior)

Cada fase es **desplegable e independiente**, de menor a mayor riesgo. Se puede parar entre fases sin dejar el sistema roto.

- **Fase 1 — Gate de FACTURAR (valor inmediato, riesgo bajo).** Sumar `HasUnacknowledgedChanges` a `ReservaCapabilityContext` + `EvaluateInvoiceSale` (UI) + **guard real en `InvoiceService`** para la factura de venta + test cross-check (la política nunca dice `Allowed=true` donde el guard rechaza). No toca el modelo de la mesa; usa el flag que ya existe. Cierra GAP #2 de entrada.
- **Fase 2 — Renglones E3 estructurados + acción por renglón.** Modelo de datos (`ReservaChangeReview`/`Line`, FK en `ReservaPendingChange`) + `ChangeReviewService` (atómico, B3) + disparador E3 emitiendo renglones + `Accept` por renglón + `accept-all-minor` (R3). **Prerrequisito duro:** la extensión de `ReservaStateCleanupRules`/`ReservaStatusTransitioner` que cierra la review al entrar a terminal (M3), con su test. Se **conserva `AcknowledgeChangesAsync`** como fallback hasta migrar el front (no se rompe la ficha existente).
- **Fase 3 — Disparador E2 (fecha) + `Substitute`/`Correct`.** Extender el descriptor `PendingServiceChange` con delta de fecha (GAP #3) + renglón combinado fecha+precio (§4.5) + las acciones de corrección/sustitución que reusan los flujos de edición/alta de servicio.
- **Fase 4 — `FiscalFollowUp` dos pasos + pase manual refinado + W6 + backfill no-precio.** Circuito NC/ND en dos pasos (§7.4, con M2: reintento + aviso por envejecimiento), regla anti-deadlock del pase manual (B1), check del vigía W6, y el backfill resumible de los renglones no-precio (§10.2). Es la fase de mayor riesgo (fiscal + migración) y va última, cuando el resto ya está probado.

### 11.0. Trade-off adoptado — colapsar `Substituted`/`Corrected`/`LineCancelled` en la primera versión

La sugerencia del reviewer se **adopta parcialmente**: para no inflar la máquina de estados en Fase 2, la **primera versión** modela como estados de renglón el conjunto mínimo que el dueño pidió explícitamente (`Pending`, `Accepted`, `LineCancelled`, `Annulled`, `Acknowledged`), y trata **`Corrected`/`Substituted` como variantes de resolución que terminan en `Accepted`** con un sub-motivo (`ResolutionKind = Accepted | Corrected | Substituted`) en lugar de estados terminales separados. **Trade-off:** se conserva lo que el dueño pidió (un renglón con esas acciones disponibles y su rastro de qué se hizo — el `ResolutionKind` + la traza R7 lo registran), y se simplifica la matriz de estados (menos combinaciones a testear). **No se pierde** información de negocio: la acción concreta queda auditada. Si más adelante se necesita un flujo distinto por cada una (ej. reglas fiscales propias de "sustituir"), se promueven a estados de primera clase. Se documenta para que el reviewer y el implementador lo tengan explícito.

---

## 12. Estrategia de test

### 12.1. Unit (dominio puro, sin base) — la matriz E1–E5

- **`ChangeReviewLine` máquina de estados:** cada `LineType` con sus acciones legales; acción sobre renglón terminal = no-op; `FiscalFollowUp` bloquea "resuelto".
- **Severidad:** regla dura (toca plata/fechas o deja sin servicios ⇒ Major) por cada evento.
- **Colapsado (D2):** dos movimientos de precio mismo servicio ⇒ un renglón original→final + dos `ReservaPendingChange`.
- **E2+E3 (D3):** un movimiento con fecha y precio ⇒ un renglón con ambas columnas.
- **R3 (accept-all-minor):** con ≥1 Major ⇒ rechazo; all-minor ⇒ cierra.
- **Invariante flag↔review** (cross-check, gate de merge, estilo test C2 de ADR-035).
- **Capacidad `EvaluateInvoiceSale`:** flag on ⇒ No; flag off ⇒ Yes.

### 12.2. Integración (con base)

- **Disparadores:** E1 (cancelar servicio confirmado abre renglón), E2 (editar fecha de servicio confirmado abre renglón — **el que hoy no existe**), E3 (precio, ya cubierto — extender), E4 (agregar servicio), E5 (dejar sin servicios). Uno por fila de la matriz.
- **Gate real FACTURAR:** `InvoiceService` rechaza factura de venta con flag on; **permite** la NC/ND correctiva.
- **Apagado derivado:** resolver el último renglón ⇒ flag off + aviso resuelto por `ResolveByKeyAsync` (verificar en `Notifications`).
- **Pase manual (B1):** all-minor + `acknowledgeReviewOpen` ⇒ pasa con constancia; con un renglón Major o `FiscalFollowUp` pendiente ⇒ **bloqueo duro**; job nocturno frenado en ambos casos.
- **Vía de escape Traveling:** reserva legacy en Traveling con `FiscalFollowUp` pendiente ⇒ emitir NC/ND es invocable y cierra el renglón.
- **Deadlock evitado:** no existe combinación que deje una review abierta imposible de cerrar en Traveling.
- **D1 dos pasos:** aceptar plata sobre reserva facturada deja `PendingCreditNote`; emitir la nota cierra el renglón.
- **Anular desde renglón:** cierra review como `Discarded`, no pierde `ReservaPendingChange`.
- **Backfill:** reserva legacy marcada (camino B, solo texto) ⇒ review abierta con renglón derivado + `LegacyNote` preservada; reserva marcada por precio ⇒ renglones colapsados desde `ReservaPendingChange`.
- **W6:** flag sin review ⇒ finding reportado (no auto-reparado); review incompleta tras backfill ⇒ el re-run la completa.
- **Concurrencia (B2):** `Version` stale sobre renglón `Pending` ⇒ `409` (no no-op); terminal + misma acción ⇒ no-op.
- **B3 atomicidad:** un fallo al persistir el flujo disparador no deja `flag=true` sin review (mismo `SaveChanges`).

### 12.3. Reviewers obligatorios (CLAUDE.md)

`travel-agency-domain-expert` (ya emitió la matriz), `software-architect-reviewer` (este ADR), luego `backend-dotnet-senior` + `backend-dotnet-reviewer` + `security-data-risk-reviewer` (toca reservas/facturas/cancelaciones/auditoría), `ux-ui-disenador` **antes** del front, `frontend-senior`/`reviewer`, `data-exposure-reviewer` (respuestas API + mensajes al usuario), `travel-agency-accountant-argentina` para el circuito NC/ND del paso 2 (D1), `qa-automation-senior`/`reviewer`.

---

## 13. Consecuencias

### 13.1. Positivas

- Un modelo explícito y testeable para los 5 eventos; cierra GAP #1–#4.
- La marca sigue siendo la fuente booleana que todo el sistema ya lee: **cero ripple** en gates/W4/W1/front (Opción B vs C).
- Reutiliza los chokepoints existentes (motor de estados, `UpdateBalanceAsync`) y los flujos formales (cancelar servicio, anular, NC/ND) — no duplica reglas de plata ni fiscales.
- Apagado derivado gratis por `ResolutionKey` (Tanda 5).
- El candado fiscal se respeta (NC/ND en dos pasos, acto consciente).

### 13.2. Negativas / costos

- Tabla nueva + máquina de estados por renglón = más superficie que un bool. Justificado por el requerimiento (mitigado por el colapso de estados de §11.0).
- El pase manual a viaje **cambia de comportamiento** (bloqueo → aviso+constancia si all-minor, bloqueo duro si hay Major/fiscal). Hay que comunicarlo y testearlo (B1).
- El backfill de E1/E4/E5 fotografía el estado actual, no el evento histórico (aceptable por R8, pero es una pérdida de fidelidad temporal que se documenta).
- La regla E1-vs-E4 se apoya en "¿existió `ConfirmedAt` alguna vez?", dato ya presente; la **decisión** de negocio (retirar E4 vs promover a E1) es explícita del dueño (Q2 cerrada), no una adivinanza.

### 13.3. Puntos donde dudé (resueltos con el reviewer y el dueño)

1. **Marca derivada (Opción B vs C).** Dudé entre eliminar el bool y derivarlo. Elegí **conservar el bool como proyección** porque lo leen demasiados read-paths como columna y derivarlo en caliente es complejidad distribuida sin beneficio (la mesa igual persiste). El costo es mantener el invariante flag↔review, que mitigo con un único escritor + W6. **Si el reviewer prefiere una sola fuente**, la alternativa es una vista/consulta y refactor de los ~4 lectores — más caro y más riesgoso.
2. **`ReservaPendingChange`: extender vs conservar.** Elegí **conservarlo como log de movimientos** y crear tablas nuevas para el renglón, porque el punto 6 pide colapsado-para-decidir + movimientos-en-auditoría simultáneamente y la tabla actual es append-only con masking probado. Extenderla forzaba elegir uno.
3. **`RescheduleAsync` masivo, ¿abre review?** El dueño cerró: **no** (acción directa con su rastro). El disparador E2 es la edición de fecha de un servicio confirmado (Q1 cerrada).
4. **W6 auto-repair.** Dudé entre auto-reparar (como W1) o solo reportar. Elegí **reportar** para no tapar disparadores fallidos. Reevaluable.
5. **Limpieza vs conservación de `ReservaPendingChange` al cerrar.** El "Dar OK" viejo borra; el nuevo conserva (auditoría). Cambia una conducta existente; lo señalo.
6. **Estados de renglón: mínimos vs completos.** Adopté el colapso de `Corrected`/`Substituted` en `Accepted`+`ResolutionKind` para la primera versión (§11.0), con el trade-off documentado.

---

## 14. Preguntas del dueño — TODAS CERRADAS (registro)

- **Q1 — Reprogramación masiva (`RescheduleAsync`): CERRADA.** No abre mesa; es acción directa con su rastro. El disparador E2 es la edición de fecha de un servicio confirmado. (§6.3)
- **Q2 — Servicio agregado y después cancelado antes de revisar: CERRADA.** Si nunca se confirmó, el renglón E4 se **retira solo**; si llegó a confirmarse, pasa a **E1**. (§6.2)
- **Q3 — Constancia al cobrar: CERRADA.** Aviso fuerte + registro automático de quién/cuándo, **sin motivo escrito**. (§8.2)
- **Q4 — "Aceptar todo tal cual": CERRADA (confirmada).** Con **un solo** renglón Major el botón no aparece; se decide renglón por renglón (R3). (§5.2)
- **Q5 — Cambio de plata sobre reserva ya facturada: CERRADA (confirmada).** Dos pasos: aceptar recalcula y deja el botón "emitir NC/ND"; el renglón no cierra hasta emitir la nota. (§7.4)

**Único parámetro a confirmar (no bloquea el diseño):** `N` días de envejecimiento del `FiscalFollowUp` antes del aviso (M2); default sugerido 3 días hábiles, configurable.

---

## 15. Fuera de alcance (explícito)

- **Comisión del vendedor** (punto 10, D6): no se liquida hoy una comisión devengada por reserva; los efectos sobre comisión quedan como **diseño futuro** (tope cero cuando exista). No se implementa en esta mesa.
- Rediseño visual fino de la mesa: lo define `ux-ui-disenador` desde `docs/ux/guia-ux-gaston.md` antes de implementar el front.
- Validez fiscal final del circuito NC/ND del paso 2: la confirma `travel-agency-accountant-argentina` / contador (regla del proyecto).

---

## 16. Hechos verificados vs asunciones

**Verificado en `HEAD`:** ubicación y semántica de `HasUnacknowledgedChanges`/`ReservaPendingChange`/`AcknowledgeChangesAsync`; camino A vs B; `EvaluateInvoiceSale` no mira el flag y el contexto no lo transporta; el pase a viaje está gateado por la marca en job y manual; `ReservaStatusTransitioner` + `ReservaStateCleanupRules` (Confirmed nunca limpia la marca); `NotificationResolutionKeys.ForTyped` + `ResolveByKeyAsync` + W4 leyendo el bool; `RescheduleAsync` como operación atómica separada de los `Update*Async`; `CoherenceChecks` W1–W5 con W4 en Tanda 5.

**Decisiones del dueño (ya no son asunciones):** regla E1-vs-E4 (Q2) y `RescheduleAsync` sin mesa (Q1) — ambas cerradas por el dueño.

**No verificado (queda para implementación):** el punto exacto del gate manual de pase a viaje (hay que ubicar el call-site en `UpdateStatusAsync`/lifecycle); la firma exacta del descriptor `PendingServiceChange` para sumarle el delta de fecha. Se señala como trabajo de `backend-dotnet-senior`, no como hecho establecido.
