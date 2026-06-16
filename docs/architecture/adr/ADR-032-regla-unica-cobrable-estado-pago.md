# ADR-032 — Regla única de "estado cobrable" para registrar / editar / borrar un pago del cliente

## 1. Status

**IMPLEMENTADO (v2, 2026-06-15).** Backend construido con las 3 correcciones del `software-architect-reviewer`
(B1 conserva INV-096 en FC4, B2 = anular-con-rastro en estados terminales, B3 = consulta de control + sin
migración). Suite unitaria (`!~Integration`) verde: **1846/1846**. Ver §13 (changelog v2) para el detalle de
lo construido y dónde difiere del diseño v1.

Nada de esto requiere migración de datos (es regla de negocio, no esquema). Decisiones del dueño ya tomadas
(ver §4). El punto **[DECISIÓN DUEÑO]** de §11 (anulación en terminal) se resolvió por **Opción 1**: existe
`POST /api/payments/{id}/annul`.

Regla del proyecto: **sin feature flags**. Este cambio entró directo.

## 2. Contexto y estado actual del código (verificado en esta revisión, HEAD main `7603b4e`)

Hoy hay **dos caminos** de alta de pago del cliente, con reglas de estado distintas, y el camino de editar/borrar **no mira el estado de la reserva** en absoluto. La cobranza (worklist) en cambio sí filtra por la lista canónica de estados cobrables. Eso produce divergencias.

### 2.1 La lista canónica ya existe en Dominio

`EstadoReserva.ActiveCollectionStatuses` (`src/TravelApi.Domain/Entities/Reserva.cs:89-95`):

```
{ InManagement, Confirmed, Traveling, ToSettle }
```

Quedan FUERA (no cobrables): `Quotation`, `Budget`, `Lost`, `Cancelled`, `Closed`, `PendingOperatorRefund`, y el literal lateral `"Archived"`. La doc de la constante ya dice explícitamente que Quotation/Budget/Lost/Cancelled no entran. Esta lista es el ancla del diseño.

### 2.2 Camino A — POST /api/payments (servicio de cobranza)

- `PaymentsController.CreatePayment` → `PaymentService.CreatePaymentAsync` (`src/TravelApi.Infrastructure/Services/PaymentService.cs:498`).
- Único gate de estado: `if (reserva.Status == EstadoReserva.Budget) throw ...` (**línea 518**).
- **Defecto:** solo bloquea `Budget`. Deja cobrar en `Quotation`, `Lost`, `Cancelled`, `Closed`, `PendingOperatorRefund`, `Archived` — todos fuera de `ActiveCollectionStatuses`.

### 2.3 Camino B — POST /api/reservas/{id}/payments (anidado, legacy) — EL AGUJERO

- `ReservasController.AddPayment` (`src/TravelApi/Controllers/ReservasController.cs:748`), permisos `CobranzasEdit` + `RequireOwnership(Reserva)`.
- → `ReservaService.AddPaymentAsync(string, ...)` (`ReservaService.cs:641`) → `AddPaymentAsync(int, Payment)` (**`ReservaService.cs:2489`**).
- **Defecto:** NINGÚN gate de estado (ni siquiera `Budget`). Acepta cobrar en cualquier estado, incluidos `Quotation`, `Budget`, `Lost`, `Cancelled`, `PendingOperatorRefund`. Este es el bypass principal.

### 2.4 Editar / borrar pago — sin gate de estado de reserva

Ambos servicios tienen pareja de métodos. Ninguno de los cuatro mira `Reserva.Status`:

| Acción | Método | Archivo:línea | Guards que SÍ tiene hoy |
|---|---|---|---|
| Editar (cobranza) | `PaymentService.UpdatePaymentAsync` | `PaymentService.cs:1189` | overpayment-bridge, applied-credit-bridge (FC4), `MutationGuards` (recibo/CAE), cross-currency, overpayment-consumed |
| Borrar (cobranza) | `PaymentService.DeletePaymentAsync` | `PaymentService.cs:1315` | mismos bridges, `DeleteGuards` (recibo/factura), overpayment-consumed |
| Editar (legacy anidado) | `ReservaService.UpdatePaymentAsync(int,int,...)` | `ReservaService.cs:2510` | mismos guards replicados |
| Borrar (legacy anidado) | `ReservaService.DeletePaymentAsync(int,int)` | `ReservaService.cs:2584` | mismos guards replicados |

Es decir: hoy se puede editar o borrar un pago de una reserva `Cancelled` o `Closed` siempre que el pago no tenga recibo/CAE/puente. No hay protección por estado terminal.

### 2.5 La cobranza ya filtra por la lista canónica

`PaymentService.GetCollectionsSummaryAsync` (`PaymentService.cs:108`) y `GetCollectionsWorklistAsync` (`PaymentService.cs:167`) filtran `ActiveCollectionStatuses.Contains(r.Status) && r.Balance > 0`. La UI de cobranza solo muestra reservas cobrables, pero el backend deja cobrar fuera de esa lista → **divergencia front/back**. Alinear el backend a `ActiveCollectionStatuses` elimina la divergencia.

### 2.6 Saldo a favor (FC4) ya usa la lista canónica — inline duplicado

`ClientCreditService` (`src/TravelApi.Infrastructure/Services/ClientCreditService.cs:664`) valida el estado de la reserva DESTINO antes de aplicarle un saldo a favor:

```
if (!EstadoReserva.ActiveCollectionStatuses.Contains(targetReserva.Status))
    throw ... "Pasala a En gestion primero."
```

Esto es exactamente la regla que queremos, pero escrita inline. El puente FC4 es un `Payment` con `AffectsCash=false`, monto positivo, creado directamente en `ClientCreditService` (no pasa por `CreatePaymentAsync`). **No debe romperse:** la regla única debe cubrir este punto reemplazando el `Contains` inline por la misma llamada de dominio, sin cambiar el comportamiento (sigue exigiendo estado cobrable en el destino).

### 2.7 Mecanismo de reversa con rastro — YA EXISTE, no hay que inventarlo

- **Soft-delete:** `Payment.IsDeleted` + `DeletedAt` (ver `PaymentService.cs:1371-1373` y `ReservaService.cs:2639-2640`). Borrar nunca borra físico.
- **Contra-asiento de caja (ADR-022):** al editar el monto de un pago que mueve caja, `ReverseLivePaymentLedgerEntryAsync` marca el asiento vigente `IsReversed=true` e inserta su reversa, luego inserta el asiento nuevo (`PaymentService.cs:1290-1297`). Al borrar, mismo contra-asiento (`PaymentService.cs:1378-1379`). El libro `CashLedgerEntry` es inmutable: queda `(+) → reversa (−) → nuevo`.
- **Anulación de artefactos de saldo a favor:** `OverpaymentCreditCleanup.ReverseOverpaymentArtifactsAsync` revierte puente+crédito de sobrepago de forma auditable.
- **Endpoint de anulación previsto:** el comentario en `ReservasController.cs:818-819` ya anticipa `POST /api/payments/{id}/annul` como reemplazo del DELETE anidado. La "reversa con rastro" que pide el dueño = **este soft-delete + contra-asiento que ya existen**. No se crea un mecanismo nuevo.

## 3. Problema

1. Un mismo concepto ("¿puedo cobrar?") está implementado con tres reglas distintas: Budget-only (Camino A), sin-regla (Camino B), y `ActiveCollectionStatuses` (cobranza + FC4). El Camino B es un agujero real (cobrar en Cancelada/Perdida).
2. No hay protección por estado al editar/borrar pagos en reservas terminales (Cancelled/Closed): la corrección libre destruye historia en estados que deberían ser inmutables.
3. La regla de "cobrable" vive como literales dispersos, no como una única función de dominio testeable.

## 4. Decisión

### 4.1 Fuente única de verdad en Dominio

Crear un método de dominio puro sobre `EstadoReserva` / `Reserva` que encapsule la regla, basado en la lista canónica ya existente:

- `EstadoReserva.IsCollectableStatus(string status)` → `ActiveCollectionStatuses.Contains(status)` (case-insensitive, igual criterio que el resto del dominio).
- `Reserva.IsCollectable()` → `EstadoReserva.IsCollectableStatus(Status)`.
- `Reserva.EnsureCollectable()` → si no es cobrable, lanza `InvalidOperationException` con mensaje de negocio único (ej.: *"No se puede registrar un cobro en este estado de la reserva. Pasala a En gestión primero."*). El mensaje NO incluye datos sensibles.

Vive en Dominio porque `EstadoReserva` ya está en `TravelApi.Domain.Entities` y es accesible desde Infrastructure (ambos servicios) sin acoplar nada nuevo. Es regla de negocio pura, sin I/O → fácil de testear.

### 4.2 Una sola regla aplicada en TODOS los caminos de alta

| Punto de entrada | Cambio |
|---|---|
| `PaymentService.CreatePaymentAsync` (`PaymentService.cs:518`) | **Reemplazar** el `if (Status == Budget)` por `reserva.EnsureCollectable();`. Pasa a bloquear todo lo no cobrable, no solo Budget. |
| `ReservaService.AddPaymentAsync(int, Payment)` (`ReservaService.cs:2489`) | **Agregar** `file.EnsureCollectable();` justo después de cargar `file` (tras el null-check, línea ~2492). Cierra el agujero. |
| `ClientCreditService` aplicación de saldo a favor (`ClientCreditService.cs:664`) | **Reemplazar** el `Contains` inline por `targetReserva.EnsureCollectable();` (o `IsCollectable()` con el mensaje propio si se quiere conservar el texto "Pasala a En gestion"). Comportamiento idéntico, ahora compartido. El puente `AffectsCash=false` no cambia. |

El puente de sobrepago→saldo a favor que se crea DENTRO de `CreatePaymentAsync`/`UpdatePaymentAsync` (`ConvertOverpaymentToClientCreditAsync`) **no necesita gate propio**: solo se ejecuta sobre una reserva que ya pasó `EnsureCollectable()` en el alta. No se le agrega chequeo (sería redundante y podría romper la conversión legítima de un cobro válido).

### 4.3 Regla de editar / borrar por estado

Decisión del dueño: en reservas **terminales** (`Cancelled`, `Closed`) NO se permite edición/borrado libre; la corrección va por **reversa con rastro** (el soft-delete + contra-asiento que ya existen). Definición exacta del set:

- **Permiten editar/borrar libre:** los estados **cobrables** (`ActiveCollectionStatuses` = InManagement, Confirmed, Traveling, ToSettle). Es donde el saldo está vivo y corregir un cobro mal cargado es operación normal.
- **NO permiten editar/borrar libre (exigen reversa con rastro):** `Cancelled`, `Closed`, `PendingOperatorRefund`, `Lost`, `Quotation`, `Budget`, `Archived`. En estos estados no debería existir un pago vivo editable a mano:
  - `Quotation`/`Budget`/`Lost`: nunca deberían tener pagos (con esta ADR ya no se pueden crear ahí). Si por dato histórico existe alguno, se trata como inmutable y se corrige por reversa.
  - `Cancelled`/`Closed`/`PendingOperatorRefund`: terminales o cuasi-terminales; tocar plata a mano rompe la inmutabilidad. La corrección legítima es anular el pago (soft-delete + contra-asiento), que deja rastro.

Implementación: una **única guarda** `Reserva.EnsureEditablePayments()` (o `IsCollectable()` reutilizado) al inicio de los cuatro métodos editar/borrar, **antes** de los guards fiscales existentes:

| Método | Archivo:línea | Agregar |
|---|---|---|
| `PaymentService.UpdatePaymentAsync` | `PaymentService.cs:1189` | gate de estado tras cargar el payment y resolver su `Reserva` |
| `PaymentService.DeletePaymentAsync` | `PaymentService.cs:1315` | idem |
| `ReservaService.UpdatePaymentAsync(int,int,...)` | `ReservaService.cs:2510` | idem (ya carga `file`) |
| `ReservaService.DeletePaymentAsync(int,int)` | `ReservaService.cs:2584` | idem (ya carga `file`) |

Mensaje de bloqueo: *"Esta reserva está cerrada/cancelada. No se puede editar ni borrar el cobro directamente; la corrección se hace anulando el cobro (queda rastro)."*

**Importante — orden y compatibilidad con los guards existentes:** el gate de estado es **adicional**, no reemplaza nada. Los guards de recibo/CAE (`MutationGuards`/`DeleteGuards`), los de puente (overpayment-bridge, applied-credit-bridge FC4) y el de cross-currency siguen vigentes y se evalúan después. Un pago en estado cobrable pero con recibo emitido sigue bloqueado por el guard fiscal, como hoy.

**El soft-delete (anulación) NO se rompe:** `DeletePaymentAsync` ES el mecanismo de reversa. Si más adelante se construye `POST /api/payments/{id}/annul`, debe poder anular pagos de reservas terminales (es justamente la salida válida). Por eso el gate de estado de §4.3 aplica al **DELETE/PUT de edición libre actual**; la futura ruta de anulación explícita debe quedar EXENTA del gate de estado terminal (anular siempre se permite). Mientras esa ruta no exista, hay un caso sin salida operativa: anular un pago de una reserva ya Cancelled. Ver §11 [DECISIÓN DUEÑO].

## 5. Consecuencias

### Positivas
- Una sola regla de "cobrable", en Dominio, testeable; tres call-sites convergen a ella.
- Se cierra el agujero del Camino B (cobrar en Cancelada/Perdida vía endpoint anidado).
- Front y back dejan de divergir (ambos `ActiveCollectionStatuses`).
- Reservas terminales quedan protegidas; la corrección pasa por un rastro auditable.

### Negativas / costos
- El Camino A se vuelve más estricto (antes permitía Quotation/Lost/Closed/PendingOperatorRefund). Si hay flujos o datos que dependían de cobrar en Quotation, se rompen — **ver riesgo R1**.
- Bloquear editar/borrar en terminales puede dejar sin salida la anulación legítima de un pago en Cancelled hasta que exista la ruta de anulación explícita (R3 / §11).

## 6. Alternativas consideradas

- **A) Solo cerrar el agujero del Camino B (agregar Budget-check), sin unificar.** Rechazada: mantiene tres reglas distintas y la divergencia con la cobranza; no resuelve el problema de fondo.
- **B) Poner la regla en cada controller.** Rechazada: la lógica de negocio debe vivir en dominio/servicio (controllers finos), y se replicaría en 2-3 lugares.
- **C) Permitir editar/borrar libre en cualquier estado y confiar solo en los guards fiscales.** Rechazada por el dueño: las reservas terminales deben ser inmutables salvo reversa con rastro.
- **D) Inventar un mecanismo nuevo de "reversa de pago".** Rechazada: ya existe soft-delete + contra-asiento de caja (ADR-022) + anulación de artefactos de saldo a favor. Reutilizar.

## 7. Plan de implementación backend (ordenado)

1. **Dominio:** agregar `EstadoReserva.IsCollectableStatus(string)`, `Reserva.IsCollectable()`, `Reserva.EnsureCollectable()` (alta) y la guarda de edición/borrado (puede ser el mismo `IsCollectable()` con mensaje propio, ya que el set "editable libre" = set "cobrable"). Helper puro, sin I/O.
2. **Alta — Camino A:** en `PaymentService.CreatePaymentAsync`, reemplazar el `if (Status==Budget)` (línea 518) por `reserva.EnsureCollectable()`.
3. **Alta — Camino B (agujero):** en `ReservaService.AddPaymentAsync(int, Payment)`, agregar `file.EnsureCollectable()` tras el null-check (línea ~2492).
4. **Alta — FC4:** en `ClientCreditService` (línea 664), reemplazar el `Contains` inline por la llamada de dominio. Verificar que el puente `AffectsCash=false` y el recálculo del destino no cambian.
5. **Editar/borrar — 4 métodos:** agregar el gate de estado al inicio de los cuatro métodos (`PaymentService.cs:1189`, `:1315`; `ReservaService.cs:2510`, `:2584`), **antes** de los guards fiscales/puente existentes. No quitar ningún guard actual.
6. **Auditoría:** registrar el bloqueo (ya hay `_logger.LogWarning` en los caminos de edición; replicar el patrón en el gate de alta del Camino B). Si existe `AuditService` para eventos de negocio, evaluar loguear el intento rechazado de cobro/edición en estado no permitido (no bloqueante para esta ADR).
7. **Controllers:** sin cambios de contrato. El servicio lanza `InvalidOperationException` → los controllers ya la mapean a 409 Conflict (ver `ReservasController.cs:762-765`, `:800-805`). Confirmar que `PaymentsController.CreatePayment` también mapee `InvalidOperationException` a 409 (verificar en implementación; hoy CreatePaymentAsync ya lanzaba para Budget, así que el mapeo debería existir).

## 8. Riesgos y mitigaciones

- **R1 — Datos históricos: pagos vivos en reservas no cobrables.** Con la regla nueva, esas reservas no podrán recibir más pagos (correcto) pero tampoco editar/borrar libre los existentes (quedan bajo "reversa con rastro"). *Mitigación:* es producto en desarrollo, sin clientes reales (contexto del dueño), así que el volumen de datos históricos inconsistentes es bajo. Antes de mergear, correr una consulta de diagnóstico (read-only) contando `Payments` vivos cuyo `Reserva.Status` no esté en `ActiveCollectionStatuses`, para dimensionar. **No** es migración de datos; es verificación.
- **R2 — Romper sobrepago→saldo a favor / puentes FC4.** *Mitigación:* el gate de alta corre ANTES de la conversión a saldo a favor (que es interna a un cobro ya válido); los puentes `AffectsCash=false` no pasan por los gates de alta; FC4 sigue exigiendo destino cobrable (mismo comportamiento). Tests dedicados (§9).
- **R3 — Anulación sin salida en terminales.** Bloquear DELETE en Cancelled deja sin forma de anular un pago legítimamente mal cargado en una reserva ya cancelada. *Mitigación / pendiente:* o bien la cancelación nunca deja pagos editables (revisar el flujo ADR-002, que ya mueve plata), o se prioriza la ruta `POST /api/payments/{id}/annul` exenta del gate de estado. Ver §11.
- **R4 — Cancelación que mueve plata.** El flujo de cancelación (ADR-002) crea/mueve Payments (refund, puentes). *Mitigación:* esos movimientos NO pasan por los endpoints de alta/edición del usuario; los crea el servicio de cancelación directamente. El gate de §4 aplica a las acciones manuales del usuario, no al motor de cancelación. Verificar en implementación que ningún paso del `BookingCancellationService` llame a `CreatePaymentAsync`/`AddPaymentAsync(int)` con la reserva ya en estado no cobrable (si lo hiciera, habría que exceptuarlo o reordenar).
- **R5 — Migración:** ninguna (regla de negocio, sin cambio de esquema).

## 9. Estrategia de tests

Todos unitarios sobre los servicios + dominio (el proyecto ya tiene >1800 unit tests con InMemory). Casos mínimos:

**Alta — regla única (los DOS caminos):**
- Cobrar en `InManagement`/`Confirmed`/`Traveling`/`ToSettle` → OK (ambos caminos).
- Cobrar en `Quotation`/`Budget`/`Lost`/`Cancelled`/`PendingOperatorRefund`/`Closed`/`Archived` → rechaza, ambos caminos. **Incluir explícitamente el endpoint anidado** `AddPaymentAsync(int)` cobrando en `Cancelled` y `Lost` (es el agujero que existe hoy → debe pasar a rechazar). El test actual `CreatePaymentAsync_OnBudgetReservation_Throws` solo cubre el Camino A; agregar su gemelo para el Camino B y ampliar a todos los estados no cobrables.

**FC4 — saldo a favor:**
- Aplicar saldo a favor a una reserva destino `InManagement` → OK (puente `AffectsCash=false`, deuda baja).
- Aplicar a destino `Cancelled`/`Budget` → rechaza (regla compartida).
- Verificar que el monto del bolsillo y la deuda del destino quedan correctos (no se rompe el puente).

**Sobrepago:**
- Cobro que sobrepaga una reserva `InManagement` → genera saldo a favor (no bloqueado por el gate).

**Editar/borrar:**
- Editar/borrar un pago en `InManagement` (cobrable) sin recibo/CAE → OK (ambos servicios).
- Editar/borrar un pago en `Cancelled` y en `Closed` → rechaza pidiendo reversa (ambos servicios, los 4 métodos).
- Pago con recibo emitido en estado cobrable → sigue rechazado por el guard fiscal (no se debilitó nada).
- Puente de saldo a favor (overpayment / applied-credit FC4) → sigue rechazando edición/borrado directo (guard de puente intacto).

**Mapeo HTTP:** alta/edición rechazada por estado → 409 Conflict con mensaje de negocio, en ambos controllers.

## 10. Observabilidad

- `LogWarning` en cada rechazo por estado (alta y edición), con `ReservaId`, `Status` y razón, siguiendo el patrón ya usado en los guards de edición (`PaymentService.cs:1202`, etc.). Sin datos personales ni montos sensibles en el log más allá de identificadores.
- Opcional (no bloqueante): evento de auditoría de negocio para intento de cobro/edición en estado no permitido, si se quiere trazar abuso.

## 11. Puntos que requieren decisión del dueño

- **[DECISIÓN DUEÑO] Anulación de un pago en reserva ya Cancelled/Closed.** Con esta ADR, editar/borrar libre queda bloqueado en terminales. ¿Cómo se corrige un cobro legítimamente mal cargado en una reserva ya cancelada o cerrada?
  - Opción 1 (recomendada): habilitar la ruta de **anulación explícita** (`POST /api/payments/{id}/annul`, ya anticipada en el código) que SÍ permite anular en terminales (soft-delete + contra-asiento), con permiso elevado. El DELETE/PUT de edición libre queda solo para estados cobrables.
  - Opción 2: dejarlo sin salida por ahora (no se puede anular en terminal) y resolverlo cuando aparezca el caso. Más simple, pero puede trabar una corrección real.
  Recomiendo Opción 1, pero la ruta de anulación es trabajo adicional fuera del alcance mínimo de cerrar el agujero. Confirmar alcance.

- **[CONFIRMAR] Mensaje al usuario.** ¿Texto exacto del cartel cuando se intenta cobrar en un estado no cobrable y cuando se intenta editar/borrar en terminal? (Pasa por el gate UX si cambia algo visible en pantalla.)

## 12. Lo que NO está verificado / fuera de alcance

- No verifiqué cada paso del `BookingCancellationService` para confirmar que no llama a `CreatePaymentAsync`/`AddPaymentAsync(int)` sobre una reserva ya no-cobrable (R4). Es una verificación a hacer en implementación.
- No conté cuántos `Payment` vivos existen hoy en reservas no cobrables (R1) — requiere consulta a la base; se hace antes de mergear.
- La construcción de `POST /api/payments/{id}/annul` (§11 Opción 1) es trabajo separado; esta ADR solo define la regla y reutiliza el mecanismo de reversa existente.
- Validación de UX de los mensajes pasa por el gate UX del proyecto si afecta pantalla.

## 13. Changelog v2 (2026-06-15) — lo realmente construido

Esta sección refleja el código mergeado, con las 3 correcciones del `software-architect-reviewer` ya aplicadas.
Donde difiere del diseño v1 (§1-§12), manda esta sección.

### 13.1 Fuente única en Dominio (`src/TravelApi.Domain/Entities/Reserva.cs`)

- `EstadoReserva.IsCollectableStatus(string?)` — predicado puro, case-insensitive sobre `ActiveCollectionStatuses`.
  Estado nulo/vacío = no cobrable. Es la **única** definición de "¿puedo cobrar?".
- `Reserva.IsCollectable()` — delega en el predicado con el `Status` propio.
- `Reserva.EnsureCollectable()` — guard de **alta**; lanza `InvalidOperationException` con
  `Reserva.NotCollectableForChargeMessage`.
- `Reserva.EnsurePaymentsEditable()` — guard de **editar/borrar**; lanza `InvalidOperationException` con
  `Reserva.NotCollectableForEditMessage` (mensaje que apunta a "anular el cobro").
- Dos constantes de mensaje públicas (sin datos sensibles) para que el test asserte el texto sin duplicarlo.

### 13.2 Alta de cobro — regla única en los dos caminos del usuario

| Punto de entrada | Cambio real |
|---|---|
| `PaymentService.CreatePaymentAsync` | el `if (Status == Budget)` se reemplazó por `reserva.EnsureCollectable()`. Ahora bloquea **todo** lo no cobrable, no solo Budget. |
| `ReservaService.AddPaymentAsync(int, Payment)` | **se agregó** `file.EnsureCollectable()` tras el null-check. **Cierra el agujero** (el endpoint anidado no miraba estado). |
| `ClientCreditService` (FC4, aplicación de saldo a favor) | **B1**: el `ActiveCollectionStatuses.Contains(...)` inline se reemplazó por `EstadoReserva.IsCollectableStatus(...)`, pero **se conserva** el `throw new BusinessInvariantViolationException(..., invariantCode: "INV-096")`. NO se usa `EnsureCollectable()` (tira `InvalidOperationException` y perdería el `invariantCode` que el `GlobalExceptionHandler` propaga al 409). Comportamiento/respuesta HTTP de FC4 idénticos; lo verifica el test `Fc4_AppliedToNonCollectable_StillThrowsInv096_NotGenericInvalidOperation` y el preexistente `AppliedToNonCollectibleReserva_RejectsInv096`. |

Caminos internos NO tocados (verificado): puente de sobrepago (`ConvertOverpaymentToClientCreditAsync`,
dentro de un cobro ya gateado), puente FC4 (`_db.Payments.Add` directo), y cancelación/refund (crean Payments
vía `_context.Payments.Add` / reversas en `AfipService`, no pasan por los métodos de alta del usuario). El gate
de alta es solo para los puntos de entrada del usuario.

### 13.3 Editar/borrar + anular con rastro (B2 — Opción 1)

- Gate de estado agregado a los **4** métodos de editar/borrar, **después** de los guards de puente y **antes**
  de los guards fiscales (corrección **D1**: un puente lo bloquea su propio guard con su mensaje, no el de estado):
  `PaymentService.UpdatePaymentAsync` / `DeletePaymentAsync`, `ReservaService.UpdatePaymentAsync(int,int,...)` /
  `DeletePaymentAsync(int,int)`.
- En `PaymentService` el gate se aplica vía `EnsureReservaPaymentsEditableAsync(payment.ReservaId, ct)` (carga el
  `Status` de la reserva con `AsNoTracking`). En `ReservaService` se reusa el `file` ya cargado:
  `file.EnsurePaymentsEditable()`.
- **Refactor para no duplicar**: el cuerpo de reversa de `DeletePaymentAsync` se extrajo a `DeletePaymentCoreAsync`
  (guards fiscales + limpieza de sobrepago + soft-delete `IsDeleted` + contra-asiento de caja ADR-022 + recálculo).
  Los guards de puente se centralizaron en `EnsureNotBridge`.
- **`AnnulPaymentAsync(publicId, reason, ct)`** (nuevo, en `IPaymentService`): anular con rastro. Reusa
  `DeletePaymentCoreAsync` (mismo mecanismo de reversa) pero **NO** llama al gate de estado → **sí** opera en
  reservas terminales. Mantiene `EnsureNotBridge` y los guards fiscales (un cobro con recibo/CAE vivo sigue
  exigiendo la anulación fiscal existente — no se duplica). Deja evento de auditoría `PaymentAnnulled`.
- Endpoint **`POST /api/payments/{id}/annul`** (`PaymentsController.AnnulPayment`): `RequirePermission(CobranzasEdit)`
  + `RequireOwnership(Payment, bypass CobranzasViewAll)` + `[Authorize(Roles="Admin")]` (mismo nivel que el DELETE).
  Body opcional `AnnulPaymentRequest(string? Reason)`. `InvalidOperationException` → 409.

### 13.4 B3 — consulta de control (sin migración)

`scripts/ops/adr032-check-payments-noncollectable.sh` (read-only): reporta (a) cobros reales vivos en cualquier
estado no cobrable, (b) subconjunto en estados terminales (los que necesitarían anulación), (c) puentes vivos en
estado no cobrable (informativo). Nombres de tabla/columna verificados contra `AppDbContext`: reserva =
`"TravelFiles"."Status"`, pago = `"Payments"."TravelFileId"`/`"IsDeleted"`, puentes = `Method IN
('SaldoAFavor','SaldoAFavorAplicado') AND "AffectsCash" = false`. **No hay migración**: es regla de negocio.

### 13.5 Tests (`src/TravelApi.Tests/Unit/Adr032CollectableStateRuleTests.cs`, 47 casos)

Regla pura (Theory cobrables/no-cobrables + case-insensitive + null); alta por ambos caminos en todos los
estados; **D2**: rechazo del endpoint anidado en Cancelada a través del `ReservasController` real → 409
(`ConflictObjectResult`); **B1**: FC4 conserva `BusinessInvariantViolationException` + INV-096; puente de
sobrepago y puente FC4 siguen creándose en estado cobrable; editar/borrar (los 4 métodos) en Cancelada/Cerrada
→ rechaza con el mensaje de "anular"; **anular en terminal** → soft-delete + asiento revertido + contra-asiento.
Suite completa `!~Integration`: **1846/1846** verde.

### 13.6 Pendiente / fuera de alcance v2

- UI: el endpoint de anular existe en backend; la pantalla pasa por el gate UX del proyecto.
- Integración Postgres real (atomicidad/transacción envolvente de la anulación) y la corrida del script B3 en el
  VPS las hace el dueño antes del deploy.
