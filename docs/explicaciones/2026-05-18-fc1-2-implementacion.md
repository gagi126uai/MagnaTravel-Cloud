# Sesion 2026-05-18 — FC1.2 implementacion completa (cierre del modulo cancelacion)

Explicacion nivel trainee/junior de todo lo que cerramos hoy del modulo de
cancelacion de reservas. Si caes aca de nuevo en 6 meses, esto te tiene que
alcanzar para entender que se hizo, por que, y que falta.

## TL;DR

Cerramos FC1.2 (cancelacion + refund operador + credito cliente) en 5 commits.
Quedan los 3 servicios completos, 3 controllers con endpoints REST, 94 tests de
integracion verdes, 6 tests E2E del flujo completo, 10 counters Serilog de
observability, y 10 tests que verifican que el audit fiscal queda persistido en
BD. **Falta pre-prod**: signoff OPS-FISCAL-001 (contador + arca-tax),
backfill `ResponsibleUserId`, y un par de deudas tecnicas chicas.

## El problema que resolvimos

Imaginate este caso real de la agencia:

> Un cliente reservo un hotel en Bariloche por $100.000. Pago $100.000.
> Cuatro dias antes del viaje, cancela. El hotel (operador) tiene una politica
> de "70% reembolsable", asi que devuelve $70.000. Pero ademas le descuenta a
> la agencia un costo bancario de $500 por la transferencia. La agencia recibe
> $69.500 netos. El cliente acepta llevarse $69.500 en efectivo y olvidarse.

Antes de hoy ese flujo no tenia donde vivir en el sistema:
- La NC fiscal (Nota de Credito) que ARCA exige no quedaba ligada a la
  cancelacion del cliente.
- La diferencia entre lo que el operador dijo ($70.000) y lo que entro en la
  caja ($69.500) no quedaba modelada — el dinero del banco se perdia.
- Cuando el cajero le entregaba el efectivo al cliente, el movimiento NO
  aparecia en el Libro de Caja (bug fiscal preexistente).
- No habia limite Ley 25.345 (la ley que prohibe pagos en efectivo arriba de
  cierto umbral).

El modulo de cancelacion arregla todo eso modelando el flujo completo
**T0 → T1 → T2 → T3** (confirmar cancelacion con el cliente → ARCA aprueba la
NC → el operador devuelve la plata → el cliente decide que hacer con el saldo).

## Las 5 piezas del modulo

### 1) `BookingCancellationService` (BC)

El "cerebro" del flujo. Maneja el ciclo de vida de cada cancelacion como una
maquina de estados.

**Que hace** (cuatro operaciones):
- **Draft** — el vendedor crea un borrador. Todavia no se toca ARCA. Es como
  abrir una "ficha" de cancelacion para empezar a juntar info.
- **Confirm** — el vendedor confirma con el cliente. El sistema emite la NC
  fiscal a ARCA + pone la Reserva en estado `PendingOperatorRefund`. **TODO
  en una sola transaccion** (si algo falla, nada queda a medias).
- **Abort** — cancelar el borrador. Solo permitido en estado `Drafted`.
- **ForceArcaConfirmation** — el "boton de emergencia" para cuando ARCA
  acepto la NC pero el callback automatico del sistema fallo (red, timeout).
  Solo Admin.

**Ejemplo agencia**: el vendedor confirma la cancelacion del cliente en
Bariloche. El BC pasa de `Drafted` → `AwaitingFiscalConfirmation`. ARCA
responde 30 segundos despues con un CAE: BC pasa a `AwaitingOperatorRefund`.
La Reserva del cliente se marca como "Pendiente Devolucion Operador".

### 2) `OperatorRefundService` (OR)

El que maneja el dinero que entra del operador.

**Que hace**:
- **RecordReceived** — registra cuando llega plata del operador (cheque,
  transferencia, efectivo). Crea un ingreso en el Libro de Caja.
- **Allocate** — distribuye ese dinero entre uno o varios BC pendientes.
  Si el hotel devolvio $200.000 y hay 3 cancelaciones distintas pendientes,
  este metodo asigna las 3 a este mismo deposito.
- **VoidAllocation** — anular una allocation (libera el cap).
- **ReassociateAllocation** — mover una allocation de un BC a otro (cuando
  el cajero confundio a que cancelacion pertenecia el dinero).

**Concurrencia**: si dos cajeros allocan el mismo refund al mismo tiempo y la
suma supera el monto recibido, uno gana y el otro recibe HTTP 409 (con retry
xmin + CHECK SQL). Esta probado con 5 tests paralelos.

**Ejemplo agencia**: el hotel le mando $200.000 a la agencia por las 3
cancelaciones del fin de semana ($69.500 + $80.000 + $50.500). El back-office
hace `RecordReceived` por $200.000 y despues 3 `Allocate` (una por BC). El
cap del refund queda en $200.000, perfectamente cuadrado.

### 3) `ClientCreditService` (CC)

El que maneja el saldo a favor del cliente y como se lo devuelve.

**Que hace**:
- **CreateEntry** — crear el "saldo a favor" del cliente. Lo invoca
  automaticamente `OperatorRefundService.Allocate` cuando se alloca a un BC.
- **Withdraw** — el cliente retira ese saldo. Soporta 5 formas (ver seccion
  "las 5 formas de devolverle plata al cliente").
- **GetEntries / GetEntryByPublicId** — queries para la UI.

**Ejemplo agencia**: cuando el OR alloca $69.500 al BC del cliente de
Bariloche, el CC crea un ClientCreditEntry con `RemainingBalance = $69.500`.
El cliente despues va y hace `Withdraw kind=PhysicalCash amount=$69.500`. El
saldo queda en 0. El BC pasa de `ClientCreditApplied` → `Closed` y la
Reserva pasa a `Cancelled`. Fin del flujo.

### 4) Los 3 controllers (HTTP)

Para que el frontend pueda llamar a los services, necesitamos endpoints REST:

- **`CancellationsController`** — 5 endpoints (Draft, Confirm, Abort,
  ForceArca, Get).
- **`OperatorRefundsController`** — 5 endpoints (RecordReceived, Get,
  Allocate, VoidAllocation, Reassociate). Back-office, **sin ownership**
  (cualquier cajero ve todos los refunds).
- **`ClientCreditsController`** — 3 endpoints (lista de entries del BC,
  detalle de entry, withdraw).

Cada endpoint chequea permisos + ownership donde aplique. Por ejemplo: el
Vendedor solo puede hacer `Draft/Confirm/Abort` de **sus** reservas (las que
tienen `ResponsibleUserId = userId`).

### 5) Los tests E2E (extremo a extremo)

6 tests que corren el flujo completo Draft → Confirm → bridge AFIP →
RecordReceived → Allocate → Withdraw → Closed contra Postgres real
(TestContainers). Cubren:

1. Happy path con Transfer.
2. Variante Factura B (no Factura A).
3. Abort sin Confirm.
4. ReversedToOperator con approval.
5. AppliedToNewBooking (no genera movimiento de caja).
6. N retiros parciales (BC cierra solo con el ultimo).

### Como se conectan entre si

```
[Vendedor UI]
     |
     v
CancellationsController.Confirm
     |
     v
BookingCancellationService.ConfirmAsync
     |   |
     |   +---> InvoiceService.EnqueueAnnulmentAsync (encola NC a Hangfire)
     |              |
     |              v
     |         (Hangfire procesa, AFIP responde)
     |              |
     |              v
     |         InvoiceService dispatch al bridge
     |              |
     |              v
     |   <----  IInvoiceAnnulmentBcBridge.OnArcaSucceededAsync
     |          (implementado por BookingCancellationService)
     v
[BC en AwaitingOperatorRefund, Reserva en PendingOperatorRefund]

         (dias / semanas despues...)

[Back-office UI]
     |
     v
OperatorRefundsController.RecordReceived + Allocate
     |
     v
OperatorRefundService.RecordReceivedAsync + AllocateAsync
     |
     +---> ManualCashMovementBuilder.BuildIncomeForRefund (helper estatico)
     |
     +---> ClientCreditService.CreateEntryAsync (mismo DbContext, 1 tx)
     |
     v
[BC en ClientCreditApplied, ClientCreditEntry creado]

         (cliente decide retirar...)

[Cajero UI]
     |
     v
ClientCreditsController.Withdraw
     |
     v
ClientCreditService.WithdrawAsync
     |
     +---> ManualCashMovementBuilder.BuildExpenseForWithdrawal
     |     (excepto KeptAsCredit / AppliedToNewBooking)
     |
     +---> BookingCancellationService.OnAllCreditConsumedAsync
     |     (si entry.RemainingBalance == 0 y kind != KeptAsCredit)
     |     |
     |     +--- Reverify SQL crudo + ChangeTracker (patron MR-02)
     |
     v
[BC en Closed, Reserva en Cancelled]
```

**Clave**: todo lo que pasa dentro de una operacion del usuario corre en UNA
sola transaccion EF. Si algo falla a la mitad, nada queda persistido. La
unica excepcion es el callback ARCA, que vuelve via Hangfire en otra
transaccion.

## Las decisiones criticas tomadas

### 1) `AllocatedAmount` usa `NetAmount` (no `GrossAmount`)

**El bug que tuvimos que arreglar**: cuando el operador devuelve $1000 pero
$200 son penalidad (deduccion), el sistema antes hacia
`refund.AllocatedAmount += 1000` (el bruto). Resultado: parecia que el cap
del refund se estaba consumiendo con los $1000 originales, **pero la caja
solo recibia $800 netos**. Si el operador hacia un solo refund de $1000 para
varios BC, el sistema te dejaba allocar mucho mas de lo que la agencia
realmente tenia en mano.

**Decision**: `AllocatedAmount` acumula **netos** (lo que efectivamente entra
a la caja). Esto cierra la causa raiz B1 del review.

**Ejemplo agencia**: hotel devuelve $1.000.000, pero descuenta $50.000 de
comision bancaria. Para el sistema, ese refund tiene "cap" de $1.000.000
recibido, pero la asignacion neta va sumando los $950.000 reales que entran
a la caja de la agencia. [ADR-003](../architecture/adr/ADR-003-allocated-amount-net-vs-gross.md) documenta esto.

### 2) El bypass de approval del NC depende del override del BC

**Que pasa**: cuando confirmas un BC con un `InvariantOverride` aprobado
(porque tenes una factura emitida y queres cancelar la reserva), el sistema
hace **bypass** del approval normal de la NC fiscal. Hasta el review, ese
bypass estaba hardcoded en `true` siempre — error grave.

**Decision**: el bypass se hace SOLO cuando hay override del BC.
`requesterIsAdmin: approvalRequest != null`. Si no hay override, la NC tiene
que pasar por su workflow de approval normal (`InvoiceAnnulment`).

**Por que importa fiscalmente**: si un caller no-admin (Vendedor) podia
emitir NCs sin control, el contador tendria NCs huerfanas en libro fiscal.
[ADR-004](../architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md) cubre esto y lleva el blocker
**OPS-FISCAL-001** (necesita signoff escrito del contador antes de prod).

### 3) `ForceArcaConfirmation` — el escape hatch

**Que pasa**: el callback automatico del bridge ARCA puede fallar (red caida,
timeout, bug). El BC queda trabado en `AwaitingFiscalConfirmation` para
siempre. Sin escape, no podes seguir el flujo aunque ARCA YA haya aprobado
la NC.

**Decision**: nuevo endpoint admin
`POST /api/cancellations/{publicId}/force-arca-confirmation`. Requiere:
- Permiso especifico `CancellationsForceArcaConfirmation` (solo Admin).
- `ApprovalRequest` aprobado tipo `InvariantOverride` scoped al BC.
- `Reason` (min 20 chars).
- La NC indicada tiene que ser una NC valida del `OriginatingInvoice` con
  CAE aprobado por AFIP.

Counter Serilog `cancellation_force_arca_executed`: si lo ves disparar
muchas veces, hay un problema sistemico con el callback automatico.

**Ejemplo agencia**: ARCA aprobo la NC el lunes. El callback nuestro fallo
porque el servidor estaba reiniciandose. El BC queda atrancado el martes.
El Admin va, verifica en el portal de ARCA que la NC tiene CAE, abre un
approval con razon "Callback caido, NC verificada en portal AFIP",
ejecuta `ForceArcaConfirmation` y el BC desbloquea.

### 4) El patron MR-02: "reverify SQL + ChangeTracker"

**El problema**: cuando dos `Withdraw` corren al mismo tiempo sobre entries
distintos del mismo BC, el codigo necesita decidir cuando cerrar el BC
(estado `Closed`). Si lo hace mirando solo el `IsFullyConsumed` en memoria
del entry que el caller acaba de tocar, puede equivocarse: el otro entry
del BC ya quedo a cero en BD pero el caller no lo ve, o viceversa.

**Decision**: en `OnAllCreditConsumedAsync`, antes de cerrar el BC:
1. Correr SQL crudo `SELECT COUNT(*) FROM ClientCreditEntries WHERE BC=X AND RemainingBalance > 0` → cuenta lo que esta persistido.
2. Sumar lo que esta in-memory (ChangeTracker Added/Modified con RemainingBalance > 0).
3. Si la suma es > 0, NO cerrar. Si es 0, cerrar.

**Por que SQL crudo y no LINQ**: `_db.ClientCreditEntries.CountAsync(...)`
en EF Core 8 NO ve los cambios del ChangeTracker (solo BD), pero el SQL
crudo via `_db.Database.SqlQueryRaw<int>(...)` tampoco — y eso es **lo que
queremos**: la BD nos dice "no hay otra tx con saldo pendiente", y nosotros
agregamos in-memory lo nuestro.

**Idempotencia**: la transicion solo va `ClientCreditApplied → Closed`. Si
ya esta `Closed`, no-op. Si esta en otro estado, log warning.

[ADR-006](../architecture/adr/ADR-006-mr-02-reverify-sql-changetracker.md) documenta el patron MR-02.

### 5) El bridge BC ↔ Invoice tiene contrato implicito

**El problema**: `BookingCancellationService` invoca a `InvoiceService` para
encolar la NC. Pero `InvoiceService` (al procesar el callback ARCA) tiene
que avisarle a `BookingCancellationService` que ARCA aprobo. Dependencia
circular clasica.

**Solucion**: interface chica `IInvoiceAnnulmentBcBridge` con solo 2 metodos
(`OnArcaSucceededAsync`, `OnArcaFailedAsync`). `BookingCancellationService`
implementa ambas interfaces: la "publica" para la UI
(`IBookingCancellationService`) y la "tecnica" para el bridge. DI registra
la misma instancia para ambas.

**Contrato implicito**: el InvoiceService DEBE llamar al bridge tras
SaveChanges del callback. Si no lo hace, el BC queda zombie. Mitigamos con
counter `metric:bc_bridge_failed` en el catch del bridge.

[ADR-008](../architecture/adr/ADR-008-invoice-annulment-bc-bridge.md) documenta el contrato.

### 6) `KeptAsCredit` genera withdrawal con `Amount=0` (opcion A)

**El problema**: cuando el cliente decide "dejame el saldo a favor, no
retiro ahora", queremos dejar **huella** de esa decision en el timeline,
pero NO consumir el saldo.

**Decision opcion A**: generar un `ClientCreditWithdrawal` con
`Kind=KeptAsCredit` y `Amount=0`. El timeline del cliente muestra "el 12/05
decidio dejar $50.000 como saldo a favor", el saldo sigue intacto.

**Opcion B descartada**: NO generar withdrawal, solo flag en el entry. Se
rechazo porque:
- El cliente puede cambiar de opinion varias veces sobre el mismo saldo.
- Un flag no captura QUIEN tomo la decision ni cuando.
- El timeline 1:N permite multiples eventos por entry.

[ADR-005](../architecture/adr/ADR-005-keptascredit-withdrawal-amount-zero.md) documenta la decision A vs B.

## Las 5 formas de devolverle plata al cliente

Los 5 `WithdrawalKind` (de `ClientCreditService.WithdrawAsync`):

### 1) `PhysicalCash` — efectivo fisico

**Cuando**: el cliente quiere llevarse plata fisica.

**Validaciones**:
- INV-094: si `amount > Ley25345ThresholdAmount` (setting), rechaza
  (Ley 25.345 prohibe efectivos arriba del umbral).
- Si `amount > PhysicalRefundAlertThreshold` (otro setting, mas bajo),
  guarda audit `ClientCreditPhysicalRefundAlert` (no bloquea, solo avisa
  al Admin).

**Efecto**: `ManualCashMovement` tipo `Expense`. El Libro de Caja registra
el egreso. Decrementa `entry.RemainingBalance`.

**Ejemplo agencia**: el cliente quiere $50.000 en efectivo. Setting esta en
$200.000 (umbral Ley) y alerta en $30.000. Pasa el umbral de alerta —
audit `ClientCreditPhysicalRefundAlert` queda guardado. La caja muestra el
egreso. Si quisiera $300.000 en efectivo, rechaza con INV-094.

### 2) `Transfer` — transferencia bancaria

**Cuando**: el cliente quiere recibir el saldo en su cuenta.

**Validaciones**: sin limite Ley 25.345 (la ley solo aplica a efectivo).

**Efecto**: `ManualCashMovement` tipo `Expense` con `method` del request
(banco, alias, CBU). Decrementa `entry.RemainingBalance`.

**Ejemplo agencia**: el cliente da CBU. El cajero hace `Withdraw Transfer
amount=$50.000`. Saldo queda en 0. Caja muestra egreso por transferencia.

### 3) `KeptAsCredit` — dejar saldo a favor

**Cuando**: el cliente no quiere retirar ahora.

**Validaciones**: `Amount` debe ser exactamente 0 (si no, falla).

**Efecto**: NO crea `ManualCashMovement`. NO decrementa el saldo. Solo
queda huella en el timeline.

**Ejemplo agencia**: el cliente dice "dejame $50.000 que para fin de año
viajo de nuevo y los uso". El BC NO cierra (saldo > 0). El timeline del
entry muestra "el 18/05 decidio quedar como credito".

### 4) `AppliedToNewBooking` — aplicar a otra reserva

**Cuando**: el cliente usa el saldo para pagar otra reserva.

**Validaciones**: la reserva destino debe ser del mismo customer.

**Efecto**: NO crea `ManualCashMovement` aca (el `PaymentService` lo
hara en FC4 cuando se implemente esa integracion). Decrementa
`entry.RemainingBalance`.

**Ejemplo agencia**: el cliente cancelo Bariloche, queda con $69.500 a
favor. Una semana despues reserva Cordoba por $80.000 y dice "usame los
$69.500 que tengo a favor + pago $10.500 mas". El cajero hace
`Withdraw AppliedToNewBooking amount=$69.500 targetReserva=<Cordoba>`.

### 5) `ReversedToOperator` — cliente devuelve plata ya cobrada

**Cuando**: el cliente recibio plata pero despues quiere "volver atras".
Caso raro pero posible.

**Validaciones**:
- Requiere `ApprovalRequest` aprobado tipo `ClientRefundReversal`.
- Audit reforzado `ClientRefundReversalApproved`.

**Efecto**: el builder construye `ManualCashMovement` tipo **Income**
(caso especial: vuelve a caja). Decrementa `entry.RemainingBalance`. El
approval se marca consumido.

**Ejemplo agencia**: el cliente recibio $69.500 ayer, pero hoy llama
diciendo "me confundi, no queria cancelar, me dan el viaje devuelta". El
Admin abre un approval `ClientRefundReversal` con razon "Cliente devuelve
$69.500 para reactivar reserva". El cajero ejecuta `Withdraw
ReversedToOperator amount=$69.500`. La caja muestra ingreso. El approval
queda consumido.

## Lo que pasa cuando AFIP responde

### El "puente" (bridge)

`BookingCancellationService` y `InvoiceService` se hablan a traves de la
interface `IInvoiceAnnulmentBcBridge`. Cuando ARCA responde a un encolado
de NC:

1. `InvoiceService` procesa el callback (Hangfire).
2. Persiste el CAE en `Invoice.AnnulmentStatus = Succeeded`.
3. SaveChanges.
4. Llama al bridge: `bridge.OnArcaSucceededAsync(invoiceId, ct)`.
5. El bridge (implementado por `BookingCancellationService`) busca el BC
   por `OriginatingInvoiceId` y lo transiciona a `AwaitingOperatorRefund`.
6. SaveChanges del BC.

Mismo patron para `OnArcaFailedAsync` (caso rechazo).

### Que pasa si el puente falla

Tres mitigaciones encadenadas:

1. **Counter Serilog** `metric:bc_bridge_failed` en el catch del bridge.
   Si lo ves disparar, hay un BC zombie (Invoice OK, BC sin moverse).
2. **Counter Serilog** `metric:cancellation_arca_succeeded` /
   `metric:cancellation_arca_failed` por cada exito/fallo del bridge.
3. **Escape hatch** `ForceArcaConfirmation` — el Admin puede destrabar
   manualmente con approval + audit.

## Auditoria y observabilidad

### Audit logs (la "libreta" del sistema)

Cada operacion guarda un `AuditLog` en BD con accion canonica:
- `BookingCancellationDrafted` / `Confirmed` / `Aborted` / `Closed`.
- `BookingCancellationArcaSucceeded` / `BookingCancellationArcaFailed`.
- `BookingCancellationArcaConfirmedManually` (force ARCA).
- `OperatorRefundReceivedRegistered`.
- `OperatorRefundAllocated`.
- `OperatorRefundAllocationVoided`.
- `OperatorRefundReassociated`.
- `ClientCreditWithdrawn`.
- `ClientRefundReversalApproved` (audit reforzado para ReversedToOperator).
- `ClientCreditPhysicalRefundAlert` (warning Ley 25.345 cerca del umbral).

10 tests `AuditLogPresenceTests` verifican que cada operacion realmente
persiste su audit log en BD. Estos tests usan el `AuditService` real (NO
mock), porque exactamente el bug que detectan es "el codigo dice que
audita pero no commitea".

### Counters Serilog (la "senial" operativa)

Distintos al audit: el audit es fiscal/contable (esta en BD, lo lee el
contador). Los counters son seniales para Grafana / dashboards:

- `metric:cancellation_drafted` / `confirmed` (con `WithOverride` flag) /
  `aborted` / `arca_succeeded` / `arca_failed` / `arca_executed` (force) /
  `closed`.
- `metric:operator_refund_received` / `allocated`.
- `metric:client_credit_withdrawn` (con `Kind` estructurado).

Si Grafana muestra `cancellation_drafted = 50` y `cancellation_closed = 30`
en el mes, hay 20 BCs abiertos. Si `arca_failed` salta arriba de un
umbral, hay problema con AFIP. Si `force_arca_executed` sube, el callback
automatico esta fallando.

## Bugs interesantes que arreglamos

### Bug 1: EF Core 8 + `CountAsync` no ve in-memory

**Sintoma**: el codigo de `OnAllocationVoidedAsync` hacia
`_db.OperatorRefundAllocations.Where(...).CountAsync(...)` para verificar
allocations activas. En EF Core 8, ese count NO ve los cambios que el
mismo scope acaba de hacer (van al ChangeTracker, no a BD).

**Fix**: filtrar in-memory via ChangeTracker para los IDs que acaban de
marcarse como `Voided=true`. ChangeTracker.Entries devuelve lo del scope
actual sin pegarle a BD.

**Ejemplo agencia**: el back-office hace `VoidAllocation` sobre 3
allocations en paralelo. La 1ra ve via CountAsync que las otras 2 estaban
"activas" (porque aun no estan en BD), entonces no avanza el cierre del
BC. Las 3 quedan voided. El BC nunca se cierra. Con el fix, la 3ra
verifica via ChangeTracker que las otras ya estan Voided y avanza.

### Bug 2: FK navigation en lugar de FK escalar (EF8)

**Sintoma**: cuando `ClientCreditService.CreateEntryAsync` recibia
`operatorRefundAllocationId: int` y creaba el entry con esa FK escalar,
EF Core 8 a veces persistia el entry ANTES del allocation (porque la
allocation aun no tenia Id). Resultado: error de FK al SaveChanges.

**Fix**: la firma cambio a recibir la **entidad** `OperatorRefundAllocation`
(no el int). EF Core 8 resuelve la FK por navigation en el orden topologico
correcto: primero allocation, despues entry.

**Ejemplo agencia**: el cajero hace `Allocate $69.500`. EF antes ponia el
entry primero ("alloc Id = ¿?") y fallaba. Ahora pone la allocation
primero (ya con Id real) y despues el entry con FK real al allocation.

### Bug 3: AuditService.LogBusinessEventAsync tragaba excepciones criticas

**Sintoma**: en el commit anterior, `AuditService` tenia un
`catch (Exception)` generico que tragaba TODO (incluso
`BusinessInvariantViolationException` y `DbUpdateConcurrencyException`).
Resultado: si el SaveChanges fallaba por concurrencia, el audit lo
ocultaba y el caller no se enteraba. Era la causa real del over-allocation
concurrente que un test detecto.

**Fix**: el catch ahora deja pasar `BusinessInvariantViolationException`,
`DbUpdateConcurrencyException` y `DbUpdateException`. Solo traga
excepciones de logging/serializacion.

**Pendiente** (deuda tecnica): refactor para que
`AuditService.LogBusinessEventAsync` NO commitee por adentro (rompe el
patron HC1 — "audit commit deberia ir con el flujo principal"). Lo
documentamos en [ADR-007](../architecture/adr/ADR-007-audit-service-savechanges-deuda-tecnica.md) para una sesion futura.

## Lo que queda pendiente antes de prod

### 1) Signoff OPS-FISCAL-001 (BLOQUEANTE)

Necesita firma escrita de:
- Contador real de MagnaTravel.
- `arca-tax-expert-argentina` (agente o experto).

Sobre el documento `docs/legal/signoffs/2026-05-XX-fc1-2-fiscal-approval.md`
(crear el dia de la reunion) que conteste 4 preguntas:

1. Es valido fiscalmente que la NC se emita tras un `InvariantOverride` BC
   sin requerir el approval `InvoiceAnnulment` adicional?
2. La cross-reference via `Invoice.AnnulmentReason` prefix +
   `Invoice.AnnulmentApprovalRequestId` FK satisface el requisito de
   trazabilidad del que aprobo la annulacion fiscal?
3. Hay obligacion legal/ARCA de un workflow de approval separado para la
   NC, independiente del workflow de la cancelacion de reserva?
4. Si la respuesta a alguna es negativa, cual es la alternativa: Plan B
   (doble approval) o Plan C (refactor cross-reference)?

Mientras tanto, `EnableNewCancellationFlow = true` solo en `dev` y
`staging` para QA. **NO prod**.

Detalle en [ADR-004](../architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md) y plan tactico v3 §13.

### 2) Backfill `ResponsibleUserId`

Antes de prender el flag en prod, correr la query bloqueante (ver doc en
`docs/operations/2026-05-18-precondicion-responsibleuserid.md`).

Caso A (resultado = 0): podes prender el flag.
Caso B (resultado > 0): ejecutar comando `users.set-responsible` (B1.15) o
documentar que las reservas viejas solo se cancelan via roles con
`ReservasViewAll`.

### 3) Validar `TaxCondition` no `Unknown`

Antes del flag, correr:

```sql
SELECT DISTINCT "TaxCondition" FROM "Customers" WHERE "TaxCondition" IS NOT NULL;
```

Confirmar que ningun valor caiga en `Unknown` del normalizador
(`TaxConditionNormalizer`). Si hay strings raros, agregar al mapeo.

### 4) Cuelgue de tests HTTP (deuda tecnica, no bloquea features)

Los 22 tests HTTP de `Cancellation/Http/` no se corren completos porque
`WebApplicationFactory` + TestContainers se cuelga (mismo sintoma que
`InvoicesControllerAnnul*` pre-existente). 4 confirmados verdes, 0 rojos
observados. Investigar como ticket separado.

### 5) Refactor AuditService (deuda tecnica)

`AuditService.LogBusinessEventAsync` hoy hace `SaveChanges` por adentro.
Rompe el patron HC1 ("audit commit deberia ir con el flujo principal").
Refactor diferido — no bloquea features.

## Commits que cierran esta sesion

```
9e63b06 feat(cancellation): FC1.2.7b counters Serilog observability + 10 tests audit presence
3640ba9 test(cancellation): FC1.2.7a E2E flujo completo Draft→Closed (6 tests, 84/84 suite)
2893221 feat(cancellation): FC1.2.4 controllers + endpoints + ownership + tests HTTP
27506d9 feat(cancellation): FC1.2.3 ClientCreditService completo (5 kinds + MR-02 + 16 tests)
81b8332 fix(cancellation): FC1.2 arreglos del review (Net/Gross, approval bypass, EF8 CountAsync, FK navigation, audit catch)
```

Mas los commits previos del 17 y madrugada del 18: ver
`docs/explicaciones/2026-05-18-fc12-base-y-services.md`.

## Archivos clave para retomar manana / en 6 meses

- **Plan tactico v3**: `docs/architecture/plan-tactico-fc1-2.md` (1106 lineas).
- **ADR-002 cancelacion**: `docs/architecture/adr/ADR-002-cancellation-refund.md`.
- **ADRs de decisiones v2/v3 cerradas hoy**:
  - [ADR-003 Net/Gross policy](../architecture/adr/ADR-003-allocated-amount-net-vs-gross.md).
  - [ADR-004 Bypass approval NC via override BC](../architecture/adr/ADR-004-invoice-annulment-bypass-via-bc-override.md).
  - [ADR-005 KeptAsCredit Amount=0](../architecture/adr/ADR-005-keptascredit-withdrawal-amount-zero.md).
  - [ADR-006 Patron MR-02 reverify](../architecture/adr/ADR-006-mr-02-reverify-sql-changetracker.md).
  - [ADR-007 AuditService SaveChanges deuda tecnica](../architecture/adr/ADR-007-audit-service-savechanges-deuda-tecnica.md).
  - [ADR-008 Bridge BC ↔ Invoice](../architecture/adr/ADR-008-invoice-annulment-bc-bridge.md).
- **Precondicion ResponsibleUserId**: `docs/operations/2026-05-18-precondicion-responsibleuserid.md`.
- **Services**:
  - `src/TravelApi.Infrastructure/Services/BookingCancellationService.cs`.
  - `src/TravelApi.Infrastructure/Services/OperatorRefundService.cs`.
  - `src/TravelApi.Infrastructure/Services/ClientCreditService.cs`.
- **Controllers**: `src/TravelApi/Controllers/CancellationsController.cs`,
  `OperatorRefundsController.cs`, `ClientCreditsController.cs`.
- **Tests E2E**: `src/TravelApi.Tests/Cancellation/Integration/CancellationFlowE2ETests.cs`.
- **Tests audit presence**: `src/TravelApi.Tests/Cancellation/Integration/AuditLogPresenceTests.cs`.
