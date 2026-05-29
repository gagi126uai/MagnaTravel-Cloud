# ADR-010 — Bandeja de reconciliacion de NC parciales con recibos vivos (FC1.3 Fase 3)

- **Status**: Accepted (Round 2 — correcciones del reviewer aplicadas en implementacion FC1.3 Fase 3, 2026-05-29).
- **Date**: 2026-05-29.
- **Author(s)**: software-architect agent.
- **Related**:
  - [ADR-009 NC parcial fiscal Hotel](ADR-009-partial-credit-note.md) (Fase 1 + 2). Esta Fase 3 consume el residuo operativo que dejo la decision F2.3 "no cascade-void receipts".
  - [ADR-002 Cancelacion / Refund](ADR-002-cancellation-refund.md).
  - Plan tactico FC1.3 Fase 2 ([plan-tactico-fc1-3-fase2.md](../plan-tactico-fc1-3-fase2.md)).

---

## 1. Contexto

### 1.1 Que dejo abierto la Fase 2 (F2.3)

Cuando se emite una **NC PARCIAL** al ARCA (`BookingCancellation.CreditNoteKind == PartialOnOriginal`),
`AfipService.ApplyPartialCreditNoteReversalAsync` (VERIFICADO en `AfipService.cs:1381-1458`) hace **tres** cosas:

1. Crea un `Payment` de reversa por `-invoice.ImporteTotal` con `OriginalPaymentId = null`
   (no hay un Payment unico al cual atarse cuando la factura se pago en varias cuotas).
2. **NO** hace cascade-void de los recibos (`PaymentReceipt`) de la factura original.
   Eso lo diferencia de la NC TOTAL (`ApplyTotalCreditNoteReversalAsync`, `AfipService.cs:1464+`)
   que si anula el recibo cuando hay match exacto por monto.
3. Emite un audit `PartialCreditNoteEconomicReversalNoCascade` (action string en `AuditLog`)
   con un JSON que incluye `invoiceId`, `bcPublicId`, `reversalAmount`, `liveReceiptIds` (lista)
   y `liveReceiptCount`. Ademas un log de metrica `metric:Fc13.PartialCreditNote.NoCascadeReceiptsPreserved`.

Ejemplo del codigo (G-F2-D): factura `$1.000` pagada en 3 recibos (`$300+$300+$400`),
NC parcial `$750`. No hay "un" recibo de `$750`. El sistema deja los 3 recibos vivos,
crea el reversal economico, y deja constancia en el audit de que un humano tiene que mirar.

### 1.2 El ejemplo pelotudo (kiosco)

El kiosco vende una docena de facturas con tres pagos parciales cada una. Un dia el cliente
devuelve parte de la mercaderia. La maquina vieja rompia todo el ticket y empezaba de cero.
La maquina nueva emite una nota de credito por la parte devuelta, **pero los recibos de plata
que el cliente ya entrego siguen "abiertos"** porque ninguno coincide exacto con lo devuelto.
La maquina anota en un cuaderno "estos 3 recibos quedaron abiertos, alguien tiene que ordenarlos".
**Este ADR es ese cuaderno hecho pantalla**: una lista donde el encargado ve los casos que quedaron
abiertos, anula a mano los recibos que correspondan, y tilda "listo". La maquina **no toca la caja
sola** (eso lo sigue haciendo el cajero donde siempre).

### 1.3 El problema central: el audit NO es una fuente consultable

VERIFICADO: el dato hoy vive en `AuditLog` con `Action = "PartialCreditNoteEconomicReversalNoCascade"`
y los `liveReceiptIds` adentro de `AuditLog.Changes` / `Details` como **JSON string libre**.

Eso sirve para forense ("que paso con esta NC"), pero **NO** para alimentar una bandeja:

- No hay columna de estado (Pendiente/Resuelto). No se puede filtrar "muestrame los abiertos".
- No hay forma de marcar un caso como resuelto sin escribir otro audit y reconciliar por string.
- Querear "casos pendientes" obligaria a `LIKE '%PartialCreditNote%'` sobre el log + parsear JSON
  en cada request + cruzar contra el estado actual de cada recibo. Fragil, lento y no transaccional.
- El audit es append-only por diseno (no se muta). El estado "pendiente -> resuelto" es **mutable**.

Conclusion: el audit es la **bitacora**, no la **fuente de verdad operativa**. Hace falta una
estructura consultable propia con estado mutable. Esto es la decision central del ADR (ver §3).

### 1.4 Decisiones de negocio ya tomadas por el dueno (NO reabrir)

| ID | Decision | Implicancia |
|---|---|---|
| D1 | **Cierre MANUAL**. El encargado tilda "resuelto" a mano. No se auto-cierra. | El estado Resolved lo setea una accion explicita, nunca un job. |
| D2 | **La bandeja SOLO AVISA**. La devolucion de plata real se hace donde se hace hoy (caja / cta cte cliente). | La bandeja anula recibos + marca resuelto. NO mueve plata. Sin endpoint de refund. |
| D3 | **Solo casos NUEVOS**. Sin backfill de NC parciales historicas. | El caso lo crea el mismo punto que hoy escribe el audit no-cascade. Cero migracion de datos. |
| D4 | **Cuatro ojos con bypass de admin unico** (patron `Allow4EyesBypassWhenSingleAdmin`, decision G5). | Reusar `TryApplyGr005BypassAsync`. NO inventar mecanismo nuevo. |

### 1.5 Hechos verificados en el repo (anclajes del diseno)

- `AfipService.ApplyPartialCreditNoteReversalAsync` (`src/TravelApi.Infrastructure/Services/AfipService.cs`):
  punto unico donde hoy se computan los `liveReceiptIds` y se escribe el audit no-cascade.
  **Es el lugar natural para crear el caso de reconciliacion**. VERIFICADO.

  **CORRECCION Round 2 (B1, atomicidad real)**: el texto previo decia "mismo `SaveChanges`, misma
  transaccion" como si ya estuviera resuelto, pero el metodo originalmente hacia
  `SaveChangesAsync` del Payment reversal ANTES y escribia el audit DESPUES (best-effort), sin
  transaccion explicita, todo dentro de un job Hangfire. La implementacion Fase 3 agrega las
  entidades del caso (`PartialCreditNoteReconciliation` + hijas) al `_context` **ANTES** de ese
  `SaveChangesAsync`, de modo que **reversal Payment + caso commitean juntos en el mismo
  `SaveChanges`** (misma transaccion implicita de EF). El caso se crea SOLO si
  `liveReceipts.Count > 0`. Esto cierra la ventana "reversal aplicado + recibos vivos + SIN caso
  en la bandeja" (plata invisible). El audit no-cascade sigue escribiendose DESPUES como bitacora
  (no es la fuente operativa). Ver §3.3.
- `PaymentService.VoidReceiptAsync` (`PaymentService.cs:549-659`): anula un recibo reusando el
  workflow de aprobacion (`ApprovalRequestType.ReceiptVoidance`, EntityType `"PaymentReceipt"`,
  EntityId `receipt.Id`). Admin bypassa. Idempotente sobre recibo ya Voided (tira 409). **La
  accion "acomodar recibo" de la bandeja DEBE delegar aca, no duplicar logica.** VERIFICADO.
- `PaymentsController` expone `POST /api/payments/{id}/receipt/void` con
  `[RequirePermission(Permissions.CobranzasReceiptVoid)]` + `RequireOwnership(Payment)`. VERIFICADO.
- `TryApplyGr005BypassAsync` (`BookingCancellationService.cs:1904-1917`): el bypass G5/GR-005.
  Reglas: `settings.Allow4EyesBypassWhenSingleAdmin == true` **Y** comentario `>= 100` chars
  **Y** `CountActiveAdminsAsync() == 1`. VERIFICADO. La verificacion 4-eyes "no self" usa
  `isSelfEdit = bc.DraftedByUserId == userId` (`BookingCancellationService.cs:1112`). VERIFICADO.
- `ApprovalRequestsController` + `ApprovalsInboxPage.jsx`: patron de bandeja existente. Lista
  pending, filtra por tipo en cliente, accion inline con campo de motivo. NO pagina hoy
  (lista en memoria). VERIFICADO.
- `Permissions.ApprovalsReview = "approvals.review"` (`Permissions.cs:92`): permiso de la inbox
  existente. La nueva pantalla la pide el `Sidebar.jsx:37` y `App.jsx:251`. VERIFICADO.
- `MonthNavigator` existe en `src/TravelWeb/src/components/ui/MonthNavigator.jsx` (filtro mensual
  canonico). VERIFICADO existencia. SUPUESTO su contrato de props (no lo lei en detalle).
- `PaymentReceiptStatuses`: solo `Issued` / `Voided` (`PaymentReceipt.cs:6-10`). VERIFICADO.
- `Invoice.OriginalInvoiceId` self-ref NC->factura (`Invoice.cs:130`). `BookingCancellation`
  tiene `PublicId`, `CreditNoteInvoiceId`, `CreditNoteKind`. VERIFICADO.
- Stack **PostgreSQL** (Npgsql, xmin concurrency, comillas dobles, CHECK constraints). VERIFICADO
  en ADR-009 §1.3 y migraciones del modulo.

---

## 2. Que entra y que NO entra en Fase 3

### 2.1 Entra

1. Una **tabla nueva consultable** con estado Pending/Resolved + snapshot de recibos vivos.
2. El **alta del caso** desde el punto que hoy escribe el audit (transaccional, solo casos nuevos).
3. **Endpoint de listado** con filtros (estado + mes via MonthNavigator) + paginacion.
4. **Accion "acomodar recibo"**: delega en `VoidReceiptAsync` existente (su workflow de aprobacion intacto).
5. **Accion "marcar resuelto"**: cierre manual con 4-ojos + bypass single-admin reusando G5.
6. **Pantalla React** clonando el patron de `ApprovalsInboxPage`, acceso por `approvals.review`.

### 2.2 NO entra (scope cut explicito)

- **Devolucion de plata real al cliente** (D2). La bandeja no tiene endpoint de refund ni mueve
  caja / cta cte. Eso queda donde esta hoy. La bandeja solo avisa + acomoda recibos + cierra.
- **Backfill de NC parciales historicas** (D3). La tabla arranca vacia. Los casos anteriores a
  esta fase se siguen viendo en el audit (forense), no en la bandeja.
- **Re-emision fiscal / tocar la NC en ARCA**. Ya emitida en Fase 2. La bandeja no llama AfipService.
- **Auto-resolucion**. Ningun job cierra casos (D1). El job (si lo hubiera) solo alerta backlog.

---

## 3. Decision: fuente consultable = tabla dedicada `PartialCreditNoteReconciliation`

### 3.1 Opciones evaluadas

**Opcion A — flag + campos sobre entidad existente** (`Invoice` de la NC, o `BookingCancellation`).

- Pros: sin tabla nueva.
- Contras: contamina una entidad fiscal central con estado operativo de UI. `Invoice` es
  inmutable-ish post-CAE (MutationGuards). `BookingCancellation` ya tiene 200+ lineas y un CHECK
  de consistencia; sumar estado de reconciliacion mezcla dos ciclos de vida distintos (el fiscal
  cierra en Fase 2; el operativo recien abre en Fase 3). El snapshot de N recibos vivos no entra
  en columnas escalares sin una tabla hija de todos modos. **Acoplamiento que no quiero.**

**Opcion B — tabla nueva dedicada `PartialCreditNoteReconciliation` (1 fila por caso) + tabla
hija `PartialCreditNoteReconciliationReceipt` (snapshot N recibos)**. ELEGIDA.

- Pros: estado mutable propio aislado del fiscal. Query directa por estado/mes. Snapshot de los
  recibos vivos al momento del evento (auditable: "estos eran los recibos cuando se abrio el caso").
  Cero impacto sobre `Invoice`/`BookingCancellation`. Migracion 100% aditiva.
- Contras: una tabla mas + el alta hay que engancharla en `ApplyPartialCreditNoteReversalAsync`.
  Aceptable: es exactamente donde ya se calcula el dato.

**Opcion C — reusar `ApprovalRequest` con un tipo nuevo** (como hizo Fase 1 con `PartialCreditNoteApproval`).

- Pros: reusa bandeja generica.
- Contras: `ApprovalRequest` modela "pedir permiso para hacer algo", ciclo
  Pending->Approved->Consumed. Aca el caso **ya ocurrio** (la NC ya se emitio); lo que falta es
  trabajo operativo de limpieza, no una autorizacion. Forzar el caso a un ApprovalRequest tuerce
  la semantica y el `Metadata` JSON sufre el mismo problema de no-consultabilidad del audit para
  el snapshot de recibos. **El 4-ojos del CIERRE si reusa el patron de approval (§5), pero el caso
  en si no es un approval.**

### 3.2 Modelo elegido (HAY QUE CREAR)

Tabla `PartialCreditNoteReconciliation`:

| Columna | Tipo | Nota |
|---|---|---|
| `Id` | int identity | PK |
| `PublicId` | uuid | `IHasPublicId`, como el resto del dominio |
| `CreditNoteInvoiceId` | int FK -> `Invoices.Id` | la NC parcial emitida |
| `OriginalInvoiceId` | int FK -> `Invoices.Id` | la factura original (de donde salen los recibos) |
| `BookingCancellationId` | int? FK -> `BookingCancellations.Id` | nullable (fallback por-monto sin BC) |
| `ReversalAmount` | decimal(18,2) | `-ImporteTotal` de la NC (mismo dato del audit) |
| `Status` | varchar(20) | `Pending` / `Resolved`. CHECK constraint. |
| `OpenedAt` | timestamptz | momento del evento (UtcNow del reversal) |
| `OpenedByUserId` / `OpenedByUserName` | varchar | quien disparo la cancelacion (de `invoice.OriginalInvoice.AnnulledBy*`) |
| `ResolvedAt` | timestamptz? | null hasta cierre |
| `ResolvedByUserId` / `ResolvedByUserName` | varchar? | quien cerro (4-ojos: distinto de OpenedBy salvo bypass) |
| `ResolutionNotes` | varchar(1000)? | motivo del cierre (>=100 chars si fue bypass single-admin) |
| `FourEyesBypassApplied` | bool default false | se cerro con bypass G5 (single admin) |
| `xmin` | xid (rowversion) | concurrency token (patron Postgres del proyecto) |

Indice: `(Status, OpenedAt)` para la query de bandeja "pending ordenados por fecha".
Indice unico: `(CreditNoteInvoiceId)` — un caso por NC, garantiza idempotencia del alta.

Tabla hija `PartialCreditNoteReconciliationReceipt` (snapshot de recibos vivos al abrir):

| Columna | Tipo | Nota |
|---|---|---|
| `Id` | int identity | PK |
| `ReconciliationId` | int FK -> `PartialCreditNoteReconciliation.Id` ON DELETE CASCADE | |
| `PaymentReceiptId` | int FK -> `PaymentReceipts.Id` | el recibo vivo (uno de los `liveReceiptIds`) |
| `SnapshotAmount` | decimal(18,2) | monto del recibo al momento del snapshot |
| `SnapshotStatus` | varchar(30) | `Issued` cuando se abrio el caso |

**Por que tabla hija y no array JSON**: necesito poder mostrar el estado ACTUAL de cada recibo
en la UI (¿sigue Issued o ya lo anularon?) cruzando contra `PaymentReceipts` en vivo. Con FK
real el join es directo y consistente. El snapshot ademas deja constancia de "como estaba al abrir".

**NO duplico el ciclo de vida del recibo en la tabla hija.** El estado vigente del recibo es
`PaymentReceipts.Status` (fuente de verdad). La tabla hija es snapshot historico + puntero.

### 3.3 Quien crea el caso

En `ApplyPartialCreditNoteReversalAsync` (`AfipService.cs:1381`), **dentro del mismo
`SaveChangesAsync` / transaccion** donde hoy se calculan `liveReceiptIds` y se escribe el audit:

- Si `liveReceiptIds.Count > 0` -> crear 1 `PartialCreditNoteReconciliation` (Status=Pending) +
  N filas hijas. El audit existente se mantiene (bitacora, no se toca).
- Si `liveReceiptIds.Count == 0` -> no se crea caso (nada que acomodar). Edge case verificado:
  factura sin recibos emitidos. El audit igual queda.

Idempotencia: el indice unico sobre `CreditNoteInvoiceId` mas un check `AnyAsync` previo evita
caso duplicado si el reversal se reintenta (Hangfire reintenta jobs). Si ya existe -> no-op + log.

---

## 4. Migracion (Postgres, aditiva)

- **Una migracion EF** `FC1_3_Fase3_AddPartialCreditNoteReconciliation`:
  - `CREATE TABLE "PartialCreditNoteReconciliations"` + `"PartialCreditNoteReconciliationReceipts"`.
  - FKs a `Invoices`, `BookingCancellations`, `PaymentReceipts`. La hija con `ON DELETE CASCADE`.
  - CHECK `chk_pcnr_status` en `Status IN ('Pending','Resolved')`.
  - CHECK `chk_pcnr_resolved_consistency`: si `Status='Resolved'` entonces `ResolvedAt`,
    `ResolvedByUserId` NOT NULL (no se cierra sin trazabilidad).
  - Indices: unico `(CreditNoteInvoiceId)`, no-unico `(Status, OpenedAt)`.
  - `xmin` como rowversion (patron del proyecto, `UseXminAsConcurrencyToken`).
- **100% aditiva**. No toca tablas existentes. Sin backfill (D3). Sin riesgo de data loss.
- Aplicar en VPS con la cautela habitual del modulo (prevalidacion + deploy controlado).
- **Rollback**: drop de las dos tablas. Como no hay backfill ni dependencia de otras tablas hacia
  estas, el rollback es limpio. Recordatorio: si ya se crearon casos en prod, drop pierde ese
  estado operativo (los recibos en si NO se pierden — viven en `PaymentReceipts`). El rollback
  fiscal no esta en juego: la NC ya esta emitida e independiente de esta tabla.

---

## 5. Endpoints backend

Controller nuevo `PartialCreditNoteReconciliationController`, `[Route("api/credit-note-reconciliation")]`,
`[Authorize]`. Servicio nuevo `IPartialCreditNoteReconciliationService` (logica de aplicacion).
Endpoints/controllers finos (delegan al service), patron del proyecto.

### 5.1 Listado — `GET /api/credit-note-reconciliation`

- `[RequirePermission(Permissions.ApprovalsReview)]` (ver §6 justificacion del permiso).
- Query params estilo proyecto: `status` (`pending`|`resolved`|`all`, default `pending`),
  `year` + `month` (filtro mensual del MonthNavigator, opcional), `page`, `pageSize`.
- Devuelve `PagedResponse<PartialCreditNoteReconciliationDto>` (mismo wrapper que el resto de listados
  paginados del proyecto — VERIFICADO que existe `PagedResponse<T>` en PaymentsController).

DTO (alto nivel):

```
PartialCreditNoteReconciliationDto {
  publicId, status, openedAt, openedByUserName,
  creditNoteNumber, originalInvoiceNumber, reversalAmount, currency,
  bookingCancellationPublicId?,        // link al BC si existe
  reservaPublicId?, customerName?,     // contexto para que el encargado ubique el caso
  resolvedAt?, resolvedByUserName?, resolutionNotes?, fourEyesBypassApplied,
  receipts: [ {                        // los recibos del snapshot CON estado vigente
     paymentReceiptPublicId, receiptNumber, snapshotAmount,
     currentStatus,                    // Issued | Voided LEIDO EN VIVO de PaymentReceipts
     voidedAt?, voidedByUserName?
  } ]
}
```

El `currentStatus` se resuelve con join a `PaymentReceipts` (no se confia en el snapshot).
Esto es lo que permite a la UI mostrar "2 de 3 recibos ya anulados".

### 5.2 Acomodar un recibo — NO endpoint nuevo, REUSO

La accion "anular este recibo del caso" reusa **el endpoint existente**
`POST /api/payments/{paymentPublicId}/receipt/void` (delega en `VoidReceiptAsync`).

- VERIFICADO: ese endpoint ya tiene `RequirePermission(CobranzasReceiptVoid)` + ownership +
  workflow de aprobacion `ReceiptVoidance` (admin bypassa). **No lo tocamos.**
- La UI de la bandeja, por cada recibo `Issued`, ofrece el boton "Anular recibo" que llama a ese
  endpoint con el `paymentPublicId` del recibo. Misma UX que ya existe para anular recibos.
- Implicancia de permisos: el encargado de la bandeja necesita ADEMAS `cobranzas.receipt_void`
  para anular (o ser Admin). Esto es **deseable**: separa "ver/cerrar la bandeja" de "tener
  autoridad fiscal para anular un comprobante". Un reviewer sin `cobranzas.receipt_void` ve el
  caso y puede cerrarlo, pero la anulacion del recibo la hace quien tiene esa autoridad
  (o dispara el approval `ReceiptVoidance`). SUPUESTO de diseno — confirmar con Gaston (Q1).

### 5.3 Marcar resuelto — `POST /api/credit-note-reconciliation/{publicId}/resolve`

- `[RequirePermission(Permissions.ApprovalsReview)]`.
- Body: `ResolvePartialCreditNoteReconciliationRequest { notes }`.
- Flujo de cierre con 4-ojos (ver §5.4).
- Idempotente: si ya esta `Resolved` -> 409 con mensaje claro (no re-cierra ni pisa quien cerro).

### 5.4 Flujo de cierre manual + cuatro ojos (reusa patron G5)

El cierre replica `TryApplyGr005BypassAsync` (NO se inventa nada):

1. Resolver `currentUserId` del claim (igual que el resto de controllers).
2. `isSelfClose = (reconciliation.OpenedByUserId == currentUserId)`.
3. Si **NO** es self-close -> 4-ojos cumplido naturalmente. Cerrar:
   `Status=Resolved`, `ResolvedAt=UtcNow`, `ResolvedBy*`, `ResolutionNotes=notes`,
   `FourEyesBypassApplied=false`.
4. Si **es** self-close -> evaluar bypass G5 con la misma regla verificada:
   - `settings.Allow4EyesBypassWhenSingleAdmin == true` **Y**
   - `notes` trim `>= 100` chars **Y**
   - `CountActiveAdminsAsync() == 1`.
   - Si las 3 OK -> cerrar con `FourEyesBypassApplied=true` + audit reforzado.
   - Si alguna falla -> `409` con mensaje en criollo:
     "No podes cerrar un caso que vos mismo abriste. Que lo cierre otra persona, o (si sos el
     unico admin) escribi un motivo de al menos 100 caracteres explicando por que."
5. **Audit obligatorio** en ambos caminos: `action="PartialCreditNoteReconciliationResolved"`,
   `entityName="PartialCreditNoteReconciliation"`, `entityId`, details JSON con
   `{ resolvedBy, fourEyesBypassApplied, notes, receiptStatesAtClose: [...] }`. El
   `receiptStatesAtClose` deja constancia de en que estado quedaron los recibos cuando se cerro
   (queda "resuelto" aunque algun recibo siga Issued — el encargado decide; la bandeja solo avisa, D2).

**Decision deliberada**: NO exijo que todos los recibos esten Voided para cerrar. El dueno dijo
que la bandeja "solo avisa" (D2): puede haber casos legitimos donde el recibo se mantiene vivo
(ej. la plata no se devuelve, queda como saldo a favor en cta cte). Forzar "todos Voided" seria
inventar una regla de negocio que el dueno no pidio. El audit registra el estado al cierre. (Q2).

---

## 6. UI + permiso

### 6.1 Permiso: REUSO `approvals.review` (no creo permiso nuevo)

Justificacion: la bandeja de reconciliacion es conceptualmente "back-office de revision", igual
que la inbox de aprobaciones. El permiso `approvals.review` ya describe exactamente al rol que
debe operar esto (Admin/Colaborador, NO Vendedor — VERIFICADO `Permissions.cs:166,203`). Crear
`reconciliation.review` agregaria superficie de permisos sin separar una responsabilidad distinta.
Pragmatico: reusar. (Si en el futuro se quiere separar quien ve aprobaciones de quien ve
reconciliaciones, se crea ahi; hoy seria over-engineering — Q3 si Gaston discrepa).

La autoridad fiscal para anular recibos sigue siendo `cobranzas.receipt_void` (separada, §5.2).

### 6.2 Pantalla — clona el patron de `ApprovalsInboxPage`

- Archivo nuevo: `src/TravelWeb/src/features/reconciliation/pages/PartialCreditNoteReconciliationPage.jsx`
  (o dentro de `features/approvals/` si se prefiere co-ubicar — decision frontend-senior).
- Ruta `/credit-note-reconciliation/inbox` en `App.jsx`, gateada con
  `hasPermission("approvals.review")` (mismo patron que `App.jsx:251`).
- Entrada en `Sidebar.jsx` con `requiredPermission: "approvals.review"`, icono distinto
  (ej. `Receipt` de lucide) y badge con count de pendientes (mismo patron que la inbox).
- Estructura visual identica a `ApprovalsInboxPage`:
  - Header con icono + titulo "Recibos a reconciliar" + subtitulo en criollo.
  - Barra de filtros: selector estado (Pendientes / Resueltos / Todos) + **MonthNavigator**
    (filtro mensual canonico) + boton Refrescar.
  - Lista de filas (no tabla pesada): por caso muestra NC #, factura original #, monto reversa,
    cliente/reserva, fecha, y la **sub-lista de recibos** con su estado vigente (chip Issued/Voided).
  - Acciones por fila:
    - Por cada recibo `Issued`: boton "Anular recibo" (llama al endpoint void existente, §5.2).
    - Boton "Marcar resuelto" con textarea de notas (igual que el textarea de notas de la inbox).
      Si es self-close y aplica bypass, la UI exige >=100 chars antes de habilitar el boton y
      muestra el cartel de cuatro-ojos.
  - Estados: loading / error / empty ("No hay casos pendientes") / permission (la ruta ya gatea).
  - Chips de estado del caso: Pendiente (ambar) / Resuelto (verde) — reusar `ApprovalStatusPill`
    o un pill equivalente.
- Hook `usePartialCreditNoteReconciliationList(status, year, month, page)` analogo a `useApprovalsList`.
- `data-testid` estables en filas/botones para automatizacion (qa-automation-senior).

---

## 7. Riesgos y trade-offs

### 7.1 Fiscales

- **R-F1**: la bandeja anula recibos (comprobantes internos) post-NC. El recibo es comprobante
  interno NO fiscal-AFIP (VERIFICADO: `PaymentReceipt` se preserva con Status=Voided para
  numeracion correlativa, no se borra). La NC fiscal ya esta emitida en Fase 2 e independiente.
  Anular un recibo NO toca ARCA. Riesgo fiscal **bajo**, pero **requiere signoff contador** sobre
  el criterio de "cerrar con recibos vivos" (Q2) — la bandeja deja un caso "Resuelto" con plata
  potencialmente sin devolver, y eso es una decision contable, no tecnica.

### 7.2 Datos / concurrencia

- **R-D1**: dos reviewers cierran el mismo caso a la vez. Mitigado por `xmin` concurrency token
  + chequeo de Status==Pending antes de cerrar (`DbUpdateConcurrencyException` -> 409).
- **R-D2**: el reversal job de Fase 2 reintenta y crea caso duplicado. Mitigado por indice unico
  `(CreditNoteInvoiceId)` + `AnyAsync` previo.
- **R-D3**: un recibo del snapshot se anula por afuera de la bandeja (desde la pantalla de pagos).
  No es bug: el listado lee `currentStatus` en vivo, asi que la bandeja lo refleja. El snapshot
  historico queda intacto (a proposito).

### 7.3 Operativos

- **R-O1**: backlog crece silencioso si nadie mira la bandeja. Mitigacion: badge de pendientes en
  el Sidebar (ya hay patron) + la metrica `Fc13.PartialCreditNote.NoCascadeReceiptsPreserved`
  existente sirve de alerta. Opcional (NO en scope salvo que Gaston lo pida): job que loguee
  conteo de Pending viejos. Ese job **no cierra nada** (D1).

### 7.4 Trade-off principal

Tabla dedicada = una entidad mas que mantener vs. estado consultable limpio y aislado del fiscal.
Lo asumo: el costo de mantener 2 tablas chicas es menor que el costo de parsear el audit por string
o contaminar `Invoice`/`BookingCancellation`. Es la opcion mantenible y testeable.

---

## 8. Estrategia de testing

- **Unit (sin Docker, InMemory + Moq — patron del proyecto, DB es VPS remoto)**:
  - `ApplyPartialCreditNoteReversalAsync` crea 1 caso + N hijas cuando hay recibos vivos.
  - No crea caso cuando `liveReceiptCount == 0`.
  - Idempotencia: segundo reversal sobre la misma NC no duplica (no-op).
  - Cierre no-self: setea Resolved + audit, `FourEyesBypassApplied=false`.
  - Cierre self sin bypass habilitado -> 409.
  - Cierre self con bypass (single admin + >=100 chars) -> Resolved + `FourEyesBypassApplied=true`.
  - Cierre self con <100 chars -> 409 aunque sea single admin.
  - Re-cierre de caso ya Resolved -> 409 idempotente.
  - Listado: filtro por estado + mes + paginacion devuelve el shape esperado y `currentStatus`
    refleja el estado VIVO del recibo (test: recibo anulado por afuera aparece Voided).
- **Integration (TestContainers, los corre el reviewer en VPS — NO en main session)**:
  - Migracion aplica + CHECK constraints rechazan Status invalido y Resolved sin ResolvedAt.
  - FK cascade de la hija al borrar el caso (solo escenario de test, no operativo).
- **Frontend**: estados loading/empty/error, boton resolver deshabilitado <100 chars en bypass,
  refresco de lista tras anular recibo / cerrar caso.

---

## 9. Plan de implementacion por capas (para backend-dotnet-senior + frontend-senior)

Orden pensado para que cada capa sea testeable antes de la siguiente:

1. **Modelo + migracion** (backend): entidades `PartialCreditNoteReconciliation` +
   `...Receipt`, config EF, migracion aditiva Postgres, CHECK constraints, indices, xmin.
   Build + unit de mapeo.
2. **Alta del caso** (backend): enganchar la creacion del caso dentro de
   `ApplyPartialCreditNoteReversalAsync` (misma transaccion, idempotente). El audit existente se
   mantiene. Unit tests de §8 (alta + idempotencia + count==0).
3. **Endpoint de listado** (backend): `IPartialCreditNoteReconciliationService` + controller +
   DTO + `currentStatus` en vivo + filtros + paginacion. `RequirePermission(ApprovalsReview)`.
   Unit del shape + filtros.
4. **Endpoint de cierre 4-ojos** (backend): `resolve` reusando regla G5 + audit reforzado +
   concurrencia. Unit de los 4 caminos de cierre.
   (La accion "acomodar recibo" NO necesita backend nuevo: reusa `VoidReceiptAsync` existente.)
5. **UI** (frontend): pagina clon de `ApprovalsInboxPage` + ruta + sidebar + hook + filtros
   (MonthNavigator) + acciones (anular recibo via endpoint existente, resolver). Estados.
6. **Reviewers**: backend-dotnet-reviewer + frontend-reviewer + security-data-risk-reviewer
   (toca recibos/NC/audit) + qa-automation. Integration tests en VPS.

---

## 10. Preguntas abiertas para Gaston (en criollo)

**Q1 — ¿Quien anula el recibo, el mismo que cierra la bandeja?**
Ejemplo pelotudo: en el negocio, ¿el encargado que ordena el cuaderno puede ademas romper los
recibos de plata, o eso solo lo hace el cajero? Tecnicamente: para anular un recibo desde la
bandeja hace falta el permiso de "anular comprobante" (`cobranzas.receipt_void`), aparte de ver
la bandeja. Mi propuesta: que sean dos permisos separados (ver/cerrar la bandeja vs anular recibo).
¿Te cierra, o queres que cualquiera que vea la bandeja pueda anular?

**Q2 — ¿Se puede dar por "resuelto" un caso con recibos todavia vivos?**
Ejemplo: el cliente devolvio mercaderia pero la plata se la dejamos como saldo a favor (no se la
devolvimos). Los recibos siguen "abiertos" pero el caso esta cerrado para nosotros. Mi propuesta:
SI se puede cerrar con recibos vivos (vos dijiste que la bandeja "solo avisa"), y el sistema anota
en que estado quedaron los recibos al cerrar. ¿Confirmas? **Esto necesita el OK del contador**
(es una decision contable, no tecnica).

**Q3 — ¿Permiso reusado o nuevo?**
Propongo usar el mismo permiso de la bandeja de aprobaciones (`approvals.review`) para entrar a
esta pantalla, en vez de inventar uno nuevo. Es el mismo tipo de gente (back-office, no vendedores).
¿Te parece bien o queres un permiso aparte por si manana lo das a otra persona?

**Q4 — ¿Hace falta un aviso automatico si un caso queda sin tocar mucho tiempo?**
Hoy te propongo solo el numerito (badge) al lado del menu + la metrica que ya existe. No un mail
ni un job que moleste. ¿Alcanza con eso, o queres una alerta si un caso lleva, digamos, mas de X
dias abierto? (Esto NO cierra nada solo, sigue siendo manual como pediste.)
