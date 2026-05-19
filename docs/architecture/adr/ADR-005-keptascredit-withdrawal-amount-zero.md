# ADR-005 — `KeptAsCredit` como withdrawal con `Amount=0` (opcion A)

- **Status**: ACCEPTED
- **Date**: 2026-05-18
- **Author(s)**: Gaston + software-architect agent (sesion FC1.2 commit `27506d9`)
- **Related**: [ADR-002 cancelacion/refund](ADR-002-cancellation-refund.md) §2.10
  (regla 5 policy, saldo cliente sin caducidad),
  plan tactico FC1.2 v3 §2.3 `IClientCreditService.WithdrawAsync`.
- **Trazabilidad**: commit `27506d9` (FC1.2.3 ClientCreditService completo).

## 1. Contexto

El cliente que cancelo una reserva queda con un `ClientCreditEntry` que
tiene `RemainingBalance > 0`. Tiene 5 opciones de retiro
(`WithdrawalKind`): `PhysicalCash`, `Transfer`, `KeptAsCredit`,
`AppliedToNewBooking`, `ReversedToOperator`.

`KeptAsCredit` significa **"dejar el saldo a favor, no retirar ahora"**.
Es una decision **explicita** del cliente (no es el default por hacer
nada). Por ejemplo: el vendedor le pregunta al cliente "que queres hacer
con los $50.000 que te quedaron a favor?" y el cliente dice "dejamelos,
para fin de año uso".

**Pregunta de modelado**: como queda registrada esa decision en el
sistema?

Dos opciones:

- **Opcion A**: generar un `ClientCreditWithdrawal` con
  `Kind=KeptAsCredit` y `Amount=0`. El entry NO se decrementa. Es una
  "marca de decision" en el timeline 1:N de withdrawals.
- **Opcion B**: NO generar withdrawal. Agregar un flag al entry (ej.
  `entry.LastDecisionWasKeepAsCredit = true`). El entry se queda como
  esta.

## 2. Decision

**Opcion A**: generar un `ClientCreditWithdrawal` con `Kind=KeptAsCredit`,
`Amount=0`, `ManualCashMovementId=null`, `ApprovalRequestId=null`.

Implementacion en `ClientCreditService.HandleKeptAsCreditAsync`
(`27506d9`):

```csharp
private Task<ClientCreditWithdrawal> HandleKeptAsCreditAsync(...)
{
    if (request.Amount != 0m)
    {
        throw new ArgumentException(
            "KeptAsCredit requiere Amount=0 (no consume saldo). " +
            "Si queres retirar plata, usar PhysicalCash o Transfer.",
            nameof(request));
    }

    return new ClientCreditWithdrawal
    {
        ClientCreditEntryId = entry.Id,
        Entry = entry,
        Kind = WithdrawalKind.KeptAsCredit,
        Amount = 0m,
        ExecutedAt = DateTime.UtcNow,
        ExecutedByUserId = userId,
        ExecutedByUserName = userName ?? string.Empty,
        ManualCashMovementId = null,
        ApprovalRequestId = null,
    };
}
```

Validaciones especiales:
- `Amount` debe ser exactamente `0` (no `> 0`). Si caller manda valor != 0,
  rechazar — probablemente confundio kind con monto.
- NO decrementa `entry.RemainingBalance` (el saldo intacto).
- NO crea `ManualCashMovement` (no hay movimiento fisico de plata).
- NO dispara `OnAllCreditConsumedAsync` (el entry NO esta `IsFullyConsumed`).

El `ManualCashMovementBuilder` valida y rechaza si lo llaman con
`Kind=KeptAsCredit`:

```csharp
if (withdrawal.Kind == WithdrawalKind.KeptAsCredit)
    throw new InvalidOperationException(
        "KeptAsCredit no genera ManualCashMovement (saldo se queda como credito).");
```

## 3. Consecuencias

### Positivas

- **Timeline completo**: el cliente puede tomar la decision **multiples
  veces** sobre el mismo entry (ej. en mayo dijo "dejalo", en julio dijo
  "retiro", en noviembre dijo "dejalo de nuevo"). El timeline 1:N
  captura cada evento. Una opcion-B con flag sobrescribe la historia.
- **Audit fiscal**: queda registrado `quien` tomo la decision y
  `cuando` (ExecutedByUserId, ExecutedAt). El contador puede preguntar
  "quien autorizo que el saldo del cliente X no se retirara en mayo?"
  y la respuesta esta en BD.
- **Coherencia con modelo del ADR-002**: el modelo es "N retiros por
  entry" (regla 12 policy + ADR-002 §2.10). `KeptAsCredit` es un retiro
  con monto 0 — encaja semanticamente.
- **No requiere campo nuevo en `ClientCreditEntry`** (que ya tiene
  bastantes columnas).

### Negativas

- **Withdrawals con `Amount=0`** se ven raro a primera vista. Hay que
  filtrarlos en queries de reportes (ej. "cuanto retiro el cliente este
  mes" debe excluir KeptAsCredit). Mitigacion: queries de reporting
  explicitas con `WHERE Kind != WithdrawalKind.KeptAsCredit`.
- **El validador del Amount tiene una excepcion** (`KeptAsCredit
  requiere == 0`, los otros requieren `> 0`). Se manejo con guard
  branch en `ValidateAmountCommon` que delega al handler especifico.

### Riesgos

- **Caller olvida que `KeptAsCredit` requiere Amount=0** y manda
  algun monto. Resultado: `ArgumentException` con mensaje claro. El
  caller corrige.
- **Reporte de "saldo retirado" suma withdrawals sin filtrar** y suma
  ceros (no afecta el total). Si el reporte cuenta "cantidad de
  retiros", contaria los KeptAsCredit. Mitigacion: documentar en
  reportes que `KeptAsCredit` no es un retiro fisico.

## 4. Alternativas consideradas

| Alternativa | Por que NO |
|---|---|
| **Opcion B: flag en entry** (`entry.IsKeptAsCredit = true`) | Sobrescribe la historia. Si el cliente cambia de decision 3 veces, solo veo la ultima. No captura quien tomo cada decision. |
| **Opcion C: tabla separada `ClientCreditDecisionLog`** | Overengineering. El modelo `ClientCreditWithdrawal` 1:N ya cubre el caso, solo necesita el enum extra. |
| **No modelar la decision** (cliente no decide nada hasta retirar) | El vendedor pierde trazabilidad. El cliente dijo "dejame" en una reunion, pero queda como si nunca hubiera dicho nada. Mala UX. |

## 5. Migration plan / rollback

**Migration**: ninguna nueva. El enum `WithdrawalKind` ya tenia
`KeptAsCredit = 0` desde la migracion de FC1.1 (`6ef46e1`). El cambio
del 18 es **comportamiento del codigo**, no de schema.

**Rollback**: si se descubre que la opcion A causa problemas, se puede
migrar a opcion B con:

1. Agregar columna `entry.LastDecisionWasKeepAsCredit bool`.
2. Backfill: `UPDATE entries SET LastDecisionWasKeepAsCredit = true WHERE EXISTS (SELECT 1 FROM withdrawals WHERE EntryId = entry.Id AND Kind = 0 ORDER BY ExecutedAt DESC LIMIT 1)`.
3. Quitar el handler `HandleKeptAsCreditAsync` y rechazar el kind.

El rollback es costoso pero posible. No esperamos hacerlo.

## 6. Testing strategy

Tests existentes (commit `27506d9`):

- `Withdraw_KeptAsCredit_NoConsumeSaldo` — el saldo queda intacto.
- `Withdraw_KeptAsCredit_AmountDistintoDeCero_TiraArgumentException`.
- `Withdraw_KeptAsCredit_NoCreaManualCashMovement`.
- `Withdraw_KeptAsCredit_NoDispararOnAllCreditConsumed` (cubierto
  implicito por el test "retiro parcial NO cierra el BC").

Tests del builder (`ManualCashMovementBuilderTests`):
- `BuildExpenseForWithdrawal_ConKindKeptAsCredit_LanzaInvalidOperationException`.

## 7. Auto-critica

- **Verificado en repo**: si — `ClientCreditService.cs` lineas 196,
  207, 303 (skip OnAllCreditConsumed), 341-371 (handler), 700-712
  (validador). `ManualCashMovementBuilder.cs:716`.
- **Edge case cubierto**: cliente cambia de decision multiples veces —
  el modelo lo soporta naturalmente.
- **Pendiente**: documentar en reportes que `KeptAsCredit` no es un
  retiro fisico (relevante cuando se construya el modulo de reportes
  de tesoreria post-FC4).
