# ADR-003 — `OperatorRefundReceived.AllocatedAmount` acumula montos netos (no brutos)

- **Status**: ACCEPTED
- **Date**: 2026-05-18
- **Author(s)**: Gaston + software-architect agent (sesion FC1.2 commit `81b8332`)
- **Related**: [ADR-002 cancelacion/refund](ADR-002-cancellation-refund.md) §2.5,
  plan tactico FC1.2 v3 §6.3, commit `81b8332` (fix Net/Gross + 5 tests T1-T5).
- **Trazabilidad**: cierra causa raiz B1 del review del 18 (Net/Gross).

## 1. Contexto

El modulo de cancelacion tiene una entidad `OperatorRefundReceived` que
representa **un deposito fisico** del operador a la agencia (cheque,
transferencia, efectivo). Ese deposito se distribuye via `OperatorRefundAllocation`
entre uno o varios `BookingCancellation` pendientes.

Cada `OperatorRefundAllocation` tiene 2 campos de monto:

- **`GrossAmount`**: lo que el operador dijo que devolvio (por ejemplo,
  $1.000.000 segun el remito).
- **`NetAmount`**: lo que efectivamente entro a la caja de la agencia
  (por ejemplo, $950.000 — porque el operador descontó $50.000 de
  comision bancaria o penalidad).

La diferencia entre ambos son las `DeductionLine` (1:N por allocation).

`OperatorRefundReceived.AllocatedAmount` es un **denormalizado** que
acumula cuanto del refund ya fue asignado. Es el "cap" que el CHECK SQL
`chk_OperatorRefundsReceived_allocated_not_exceeds` valida:

```sql
CHECK ("AllocatedAmount" >= 0 AND "AllocatedAmount" <= "ReceivedAmount")
```

**Pregunta**: cuando se hace una nueva allocation, debe el cap acumular
`GrossAmount` o `NetAmount`?

## 2. Decision

**`AllocatedAmount` acumula `NetAmount`**. El cap representa "cuanto de la
plata que entro a la caja ya esta destinado a clientes".

Implementacion:
- `OperatorRefundService.TryAllocateOnceAsync` paso 9:
  `refund.AllocatedAmount += allocation.NetAmount`.
- `OperatorRefundService.VoidAllocationAsync` paso 5:
  `allocation.Refund.AllocatedAmount -= allocation.NetAmount`.
- `OperatorRefundService.ReassociateAllocationAsync` paso 6:
  ajusta cap con `oldAllocation.NetAmount` y `newAllocation.NetAmount`.

`BookingCancellation.ReceivedRefundAmount` (denormalizado del BC) tambien
acumula netos (`SUM(allocations.NetAmount WHERE NOT IsVoided)`).

## 3. Consecuencias

### Positivas

- El cap del refund refleja la realidad fiscal: solo se puede asignar lo
  que efectivamente entro a la caja.
- Coherente con `BookingCancellation.ReceivedRefundAmount` (que tambien es
  neto) y con `ClientCreditEntry.CreditedAmount = NetAmount` (el cliente
  recibe el neto).
- El CHECK SQL nunca se viola si el codigo respeta la regla.

### Negativas

- Si el operador en su sistema externo trackea por bruto, el reconcile
  manual va a mostrar diferencias (operador dice $1.000.000, agencia
  contabiliza $950.000). Mitigacion: la deduccion queda guardada en
  `DeductionLine` con `SupportingDocumentRef` o `JustificationComment`,
  el contador puede cruzar.

### Riesgos

- Si alguien futuro hace `AllocatedAmount += GrossAmount` por error, el
  cap se infla artificialmente y queda capacidad "fantasma" que no
  existe en caja. **Mitigacion**:
  - 5 tests dedicados (T1-T5 del commit `81b8332`):
    - `RefundAllocatedAmount_ConDeducciones_UsaNetAmount`.
    - `ReceivedRefundAmount_ConDeducciones_CoincideConSumNet`.
    - `VoidAllocation_ConDeducciones_LiberaSoloNetDelCap`.
    - `Reassociate_ConDeducciones_NoDuplicaCap`.
    - `RefundAllocatedAmount_SinDeducciones_NetEqualGross`.
  - Comentario didactico XML doc en `OperatorRefundReceived.AllocatedAmount`
    + `OperatorRefundAllocation.NetAmount`.

## 4. Alternativas consideradas

| Alternativa | Por que NO |
|---|---|
| **Cap acumula `GrossAmount`** | El cap deja de reflejar la caja real. Si el operador devuelve $1M con $50k de deduccion, el cap dice "$1M asignable" pero la agencia solo tiene $950k. Permite over-allocate la caja. |
| **2 caps separados** (`AllocatedNet` + `AllocatedGross`) | Doble bookkeeping. La regla fiscal solo necesita uno (el neto, lo que realmente esta disponible). YAGNI. |
| **Sin denormalizado, calcular cap on-the-fly** | Performance: cada Allocate haria un SUM agregado, no aprovecha el CHECK SQL atomic. Plan tactico v3 §6.3 ya rechazo esta opcion. |

## 5. Migration plan / rollback

**Migration**: el campo `AllocatedAmount` ya existe en la migracion de FC1.1
(`6ef46e1`). El fix del 18 es solo **comportamiento del codigo**, no de
schema. No hay migracion EF nueva.

**Rollback**: revertir el commit `81b8332` deja el codigo en el estado
"acumula gross" anterior — los tests T1-T5 fallarian inmediatamente,
sirve como red de seguridad. Datos en BD no requieren backfill (el campo
es denormalizado y se puede recalcular con `SUM(allocations.NetAmount)`).

**Recalculo defensivo** (si se descubre inconsistencia historica):

```sql
UPDATE "OperatorRefundReceived" r
SET "AllocatedAmount" = (
  SELECT COALESCE(SUM(a."NetAmount"), 0)
  FROM "OperatorRefundAllocations" a
  WHERE a."OperatorRefundReceivedId" = r."Id" AND a."IsVoided" = false
);
```

Disponible como tarea ad-hoc del DBA si nunca corrio el modulo con bruto.

## 6. Testing strategy

- 5 tests integration TestContainers contra Postgres real (commit `81b8332`).
- Tests cubren: con deducciones, sin deducciones, void con deducciones,
  reassociate con deducciones, multiples allocations en cascada.
- Tests parallel del cap (`OperatorRefundConcurrencyTests`) verifican que
  el CHECK SQL bloquea over-allocate aun con xmin retry.

## 7. Auto-critica

- **Verificado en repo**: si — `OperatorRefundService.cs` lineas 388-399
  (Allocate), 660-665 (Void), 850-906 (Reassociate). Todos usan
  `NetAmount`.
- **Cobertura test**: si — 5 tests dedicados + 5 tests concurrencia.
- **Pendiente**: validar que NO haya allocations historicas con cap
  incorrecto (corrido del recalculo SQL una vez en prod si el modulo se
  uso en bruto antes). Como FC1.2 no estuvo en prod aun, este riesgo
  es teorico.
