# ADR-004 — Bypass del approval `InvoiceAnnulment` cuando hay `InvariantOverride` del BC

- **Status**: ACCEPTED (provisional — bloqueado por OPS-FISCAL-001 para merge a prod)
- **Date**: 2026-05-18
- **Author(s)**: Gaston + software-architect agent (sesion FC1.2 commit `81b8332`)
- **Related**: [ADR-002 cancelacion/refund](ADR-002-cancellation-refund.md) §2.13,
  plan tactico FC1.2 v3 §13 OPS-FISCAL-001, BR-V2-03.
- **Trazabilidad**: cierra fix F5 del review (Net/Gross + approval bypass), commit `81b8332`.

## 1. Contexto

Antes de FC1.2, el sistema tenia 2 workflows de approval **separados**:

1. **`InvariantOverride`** (ADR-001) — approval admin para saltearse una
   invariante de dominio. Por ejemplo: "esta reserva ya tiene factura
   emitida, pero el cliente quiere cancelar igual, Admin aprueba con
   razon".
2. **`InvoiceAnnulment`** — approval admin para anular una Invoice
   especifica (emitir NC). Usado historicamente cuando el back-office
   anulaba facturas sin contexto de cancelacion.

Con la nueva maquina de estados de FC1, ambos approvals coexisten en el
mismo flujo:

- El vendedor confirma una cancelacion (`BookingCancellation.ConfirmAsync`).
- Si la reserva tiene factura emitida, la invariante INV-XXX dispara
  ApprovalRequired → admin aprueba un `InvariantOverride` scoped al BC.
- El BC llama a `InvoiceService.EnqueueAnnulmentAsync` para emitir la NC.
- Aca el sistema preguntaba: **necesito tambien un `InvoiceAnnulment`
  separado?**

Opciones consideradas en plan tactico v3 (BR-V2-03):

- **(a)** Un solo approval cubre: el `InvariantOverride` del BC ya implica
  el approval de la NC fiscal. Se hace **bypass** del approval
  `InvoiceAnnulment`.
- **(b)** Doble approval: el BC pide su override + el InvoiceService pide
  su `InvoiceAnnulment` aparte. Dos approvals para confirmar la
  cancelacion.
- **(c)** Refactor cross-reference: `EnqueueAnnulmentAsync` deja de
  aceptar `requesterIsAdmin: true`, exige siempre approval, y el BC
  service automaticamente clona el `InvariantOverride` como
  `InvoiceAnnulment`.

Gaston eligio (a) en plan v3. Aceptada **provisional** sujeta a signoff
del contador (ver §6).

## 2. Decision

**Opcion (a): un solo `InvariantOverride` del BC cubre la NC fiscal**.

El bypass se hace de forma controlada:

1. `BookingCancellationService.ConfirmAsync` recibe la `ConfirmCancellationRequest`.
2. Si la invariante de "reserva con factura emitida" dispara
   `ApprovalRequired`, el caller (admin) tiene que pasar
   `request.OverrideReason` + `request.ApprovalRequestPublicId`.
3. El service valida que ese `ApprovalRequest` sea:
   - `Type = InvariantOverride`.
   - `EntityType = "BookingCancellation"`, `EntityId = bc.Id`.
   - `Status = Approved`.
   - No consumido aun.
4. El service invoca a
   `InvoiceService.EnqueueAnnulmentAsync(..., requesterIsAdmin: approvalRequest != null, ...)`.
   El bypass del `InvoiceAnnulment` se activa SOLO si hay override aprobado
   del BC.
5. Trazabilidad cruzada persistida:
   - `Invoice.AnnulmentReason` recibe prefijo
     `"BC override <approvalRequestPublicId>: <reason original>"`.
   - `Invoice.AnnulmentApprovalRequestId` (FK nullable nueva, migracion
     FC1.2.0) recibe el `ApprovalRequest.Id` del override.
   - Audit log `BookingCancellationConfirmed` con metadata del approval.
   - Audit log `InvoiceAnnulmentRequested` (existente, no cambia)
     cruzable con `Invoice.AnnulmentApprovalRequestId`.
6. El `ApprovalRequest` se marca `Consumed` post-EnqueueAnnulment.

**Bug detectado en review (F5)**: el codigo original tenia
`requesterIsAdmin: true` **hardcoded**, lo que activaba el bypass aun sin
override. Fix en commit `81b8332` cambio a
`requesterIsAdmin: approvalRequest != null`.

Snippet del fix (`BookingCancellationService.cs:373`):

```csharp
await _invoiceService.EnqueueAnnulmentAsync(
    originatingInvoiceId,
    userId, userName,
    reason: crossRefReason,
    requesterIsAdmin: approvalRequest != null,  // <- antes era true hardcoded
    ct: ct,
    approvalRequestId: approvalRequest?.Id);
```

## 3. Consecuencias

### Positivas

- **UX**: el admin aprueba UNA SOLA VEZ una cancelacion con factura. Doble
  approval seria un mal flujo.
- **Trazabilidad**: el `Invoice.AnnulmentApprovalRequestId` + prefijo en
  `AnnulmentReason` permite al contador cruzar quien aprobo cada NC con
  el approval original del BC.
- **Tests dedicados** (commit `81b8332`):
  - `Confirm_SinAdminOverride_NoBypaseaApprovalDelInvoiceAnnulment` (T2):
    sin override, el bypass no se activa.
  - `Confirm_ConOverrideAprobado_BypaseaApprovalDelInvoiceAnnulment` (T2-bis):
    con override, el bypass se activa.

### Negativas

- **Acoplamiento fiscal entre dos workflows historicamente separados**.
  El contador puede objetar.
- **No hay separacion fisica de los approvals**. Si un Admin no-fiscal
  aprueba el `InvariantOverride`, esta implicitamente aprobando la NC.
  Mitigacion: el `InvariantOverride` requiere reason >= 20 chars + admin
  rol + audit log.
- **El contador puede pedir Plan B o Plan C** en la reunion de signoff
  (ver §6). Si dice no, fallback documentado en plan v3 §10.3.

### Riesgos

- **OPS-FISCAL-001 sin signoff**: si MagnaTravel pone
  `EnableNewCancellationFlow = true` en prod sin la firma del contador y
  el contador despues lo rechaza, hay un problema de cumplimiento. Las
  NCs ya emitidas son validas fiscalmente (CAE de ARCA), pero el
  workflow de approval interno puede no satisfacer auditoria.
- **Mitigacion**: feature flag `EnableNewCancellationFlow = false` en
  prod hasta signoff. Habilitar solo en dev/staging para QA.

## 4. Alternativas consideradas

| Alternativa | Por que NO |
|---|---|
| **Plan B — Doble approval** | Mala UX. El admin tiene que abrir 2 tickets para una sola cancelacion. Fallback si signoff es negativo. |
| **Plan C — Refactor cross-reference** | +6h codigo + cambio de interface publica `InvoiceService.EnqueueAnnulmentAsync`. Costoso. Fallback ultimo si el contador exige separacion fisica. |
| **Sin trazabilidad cruzada** | El contador no podria auditar "quien aprobo esta NC" sin abrir el modulo de cancelacion. Rechazada por R3 review ADR-001. |
| **Hardcoded `requesterIsAdmin: true` (estado pre-fix)** | Permite a un caller no-admin emitir NCs sin control. Bug F5 del review. Rechazada. |

## 5. Migration plan / rollback

**Migration FC1.2.0** (commit `1c25192`):
- `Invoice.AnnulmentApprovalRequestId` FK nullable nueva, sin backfill
  (NCs historicas pre-FC1.2 quedan NULL).
- Rollback: la columna es nullable, `dotnet ef database update <PreviousMigration>`
  revierte sin perdida.

**Rollback de comportamiento** (sin tocar schema):
- Cambiar `EnableNewCancellationFlow = false` en
  `OperationalFinanceSettings`.
- Las cancelaciones nuevas usan el flujo viejo (no toca BC ni hace
  bypass).
- Las cancelaciones ya iniciadas con el flujo nuevo siguen funcionando
  hasta cerrarse.

**Fallback fiscal** si signoff es negativo:
- Plan B: implementar doble approval (+3h codigo +2h tests).
- Plan C: refactor cross-reference (+6h codigo +3h tests).
- Detalle: plan tactico v3 §10.3.

## 6. Testing strategy

Tests existentes (commit `81b8332`):

- `Confirm_SinAdminOverride_NoBypaseaApprovalDelInvoiceAnnulment` — sin
  override aprobado, `EnqueueAnnulmentAsync` se llama con
  `requesterIsAdmin: false`. Si la Invoice requiere approval normal, lo
  pide.
- `Confirm_ConOverrideAprobado_BypaseaApprovalDelInvoiceAnnulment` —
  con override aprobado, `requesterIsAdmin: true`. La NC se encola
  directamente sin requerir el approval `InvoiceAnnulment`.
- E2E `HappyPath_FlujoCompletoConTransferAlCliente_CierraBcYCancelaReserva`
  (commit `3640ba9`) — cubre el flujo completo con override.

## 7. OPEN QUESTION OPS-FISCAL-001 — BLOQUEA MERGE A PROD

**Signoff requerido**:

- Reunion con (1) contador real de MagnaTravel + (2) `arca-tax-expert-argentina`.
- Items a confirmar por escrito:
  1. Es valido fiscalmente que la NC se emita tras un `InvariantOverride`
     BC sin requerir el approval `InvoiceAnnulment` adicional? (Si/No).
  2. La cross-reference via `Invoice.AnnulmentReason` prefix +
     `Invoice.AnnulmentApprovalRequestId` FK satisface el requisito de
     trazabilidad del que aprobo la annulacion fiscal? (Si/No).
  3. Hay obligacion legal/ARCA de un workflow de approval separado para
     la NC, independiente del workflow de la cancelacion de reserva?
     (Si/No con cita al art./normativa).
  4. Si respuesta negativa a 1, 2 o 3, alternativa preferida: Plan B
     (doble approval) o Plan C (refactor cross-reference)?

- Documento de signoff (a crear el dia de la reunion):
  `docs/legal/signoffs/2026-05-XX-fc1-2-fiscal-approval.md`. Firmas
  requeridas: nombre + matricula contador + fecha.

**Estado actual**: PENDIENTE. Feature flag `EnableNewCancellationFlow`
queda en `false` en prod hasta tener el signoff. Habilitable en dev /
staging para QA.

## 8. Auto-critica

- **Verificado en repo**: si — `BookingCancellationService.cs:373` usa
  `requesterIsAdmin: approvalRequest != null`. `Invoice.cs` tiene el
  campo `AnnulmentApprovalRequestId` post-FC1.2.0.
- **Decision provisional**: aceptada pero bloqueada por signoff externo.
  Riesgo de tener que pasar a Plan B / C si el contador rechaza.
- **Mitigacion del riesgo**: feature flag default `false` en prod. Tests
  cubren tanto el path con override como sin override.
