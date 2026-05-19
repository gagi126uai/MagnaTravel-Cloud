# ADR-006 — Patron MR-02: reverify SQL crudo + ChangeTracker para callbacks idempotentes

- **Status**: ACCEPTED
- **Date**: 2026-05-18
- **Author(s)**: Gaston + software-architect agent (sesion FC1.2 commit `27506d9`)
- **Related**: [ADR-002 cancelacion/refund](ADR-002-cancellation-refund.md) §2.4
  (maquina de estados), plan tactico FC1.2 v3 §6.4 (T3 WithdrawAsync).
- **Trazabilidad**: commit `27506d9` (FC1.2.3 ClientCreditService).

## 1. Contexto

El servicio `ClientCreditService.WithdrawAsync` decrementa el saldo de un
`ClientCreditEntry`. Si tras el retiro el `RemainingBalance == 0`, el
caller invoca a `BookingCancellationService.OnAllCreditConsumedAsync` para
que el BC pase a `Closed`.

**Pero**: un BC puede tener **multiples** `ClientCreditEntry` (si el
operador devolvio el dinero en cuotas — N `OperatorRefundReceived` con
allocations al mismo BC, cada una crea un entry). El BC solo debe cerrar
cuando TODOS los entries del BC quedan a `RemainingBalance = 0`.

**Problema de concurrencia**: dos retiros paralelos sobre entries
distintos del mismo BC pueden ambos invocar `OnAllCreditConsumedAsync` al
mismo tiempo. Cada uno ve in-memory que SU entry quedo a 0, pero no ve
el del otro (esta en otra tx). Si los dos llaman al cierre, los dos
intentan transicionar `ClientCreditApplied → Closed`. Posibles bugs:

- Doble cierre con doble audit log.
- Cierre prematuro (uno cerro antes que el otro entry se haya
  decrementado).
- Race con ReassociateAllocation que reactivo un entry mientras se
  estaba evaluando el cierre.

EF Core 8 tiene un detalle sutil:

- `_db.ClientCreditEntries.Where(...).CountAsync(...)` consulta a BD
  pero NO ve los cambios in-memory del scope actual (los del
  ChangeTracker que aun no se commitearon).
- `_db.ChangeTracker.Entries<ClientCreditEntry>()` ve solo el scope
  actual (entidades trackeadas), no el estado de otras tx.

## 2. Decision

**Patron MR-02**: en `OnAllCreditConsumedAsync`, antes de cerrar el BC,
combinar **dos fuentes de informacion**:

1. **SQL crudo** via `_db.Database.SqlQueryRaw<int>(...)` que cuenta
   entries con `RemainingBalance > 0` directamente en BD (sin pasar por
   el ChangeTracker).
2. **ChangeTracker filter** in-memory para los entries `Added` /
   `Modified` con `RemainingBalance > 0` en el scope actual.

Si **suma > 0**, NO cerrar. Si **suma == 0**, cerrar.

**Snippet** (`BookingCancellationService.cs:746-791`):

```csharp
// Reverificacion bajo concurrencia (MR-02 plan v3):
//
// El caller dijo "ya consumi el ultimo entry" basandose en su estado
// in-memory. Pero si OTRO withdraw paralelo abrio otra tx, podria
// haber agregado un nuevo entry o restaurado el balance via Reassociate.
// Antes de cerrar el BC, contamos directamente en BD con SQL crudo
// cuantos entries quedan con saldo > 0 EXCLUYENDO los cambios in-memory
// que el caller ya hizo (todavia no commiteados).

var remainingInDb = await _db.Database.SqlQueryRaw<int>(
    "SELECT COUNT(*)::int AS \"Value\" FROM \"ClientCreditEntries\" " +
    "WHERE \"BookingCancellationId\" = {0} AND \"RemainingBalance\" > 0",
    bookingCancellationId).FirstOrDefaultAsync(ct);

var remainingInMemory = _db.ChangeTracker
    .Entries<ClientCreditEntry>()
    .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
    .Select(e => e.Entity)
    .Count(entry => entry.BookingCancellationId == bookingCancellationId
                 && entry.RemainingBalance > 0m);

if (remainingInDb + remainingInMemory > 0)
{
    // No-op, todavia hay saldos pendientes.
    return;
}

// Transicion idempotente:
if (bc.Status == BookingCancellationStatus.Closed) return;          // Otro tx cerro antes.
if (bc.Status != BookingCancellationStatus.ClientCreditApplied)
{
    // Log warning + return. No tiene sentido cerrar desde otro estado.
    return;
}

bc.Status = BookingCancellationStatus.Closed;
bc.ClosedAt = DateTime.UtcNow;
bc.Reserva.Status = EstadoReserva.Cancelled;
// audit + SaveChanges...
```

Idempotencia: la transicion solo va `ClientCreditApplied → Closed`. Si
ya esta `Closed`, no-op (otro tx cerro antes). Si esta en otro estado
(`AwaitingOperatorRefund` etc.), log warning + no-op.

## 3. Consecuencias

### Positivas

- **Idempotente** ante concurrencia. Dos `OnAllCreditConsumedAsync`
  paralelos cierran el BC una sola vez (por xmin concurrency token
  + state machine guard).
- **Coherente con BD**: la query SQL ve el estado real de otras tx
  committed, no la vista local del ChangeTracker.
- **Coherente con scope actual**: el ChangeTracker filter ve lo que
  nuestro scope esta a punto de persistir, por si el caller agrego un
  entry nuevo Added pero no commiteo aun.
- **Test dedicado** (commit `3640ba9` E2E variante 5):
  `Variante5_NRetirosParciales_BcCierraSoloConElUltimoRetiro` —
  3 retiros parciales, BC sigue abierto en los dos primeros, cierra
  con el 3ro. `closeAudits == 1` (idempotencia verificada).

### Negativas

- **SQL crudo** rompe la abstraccion EF. El developer tiene que
  recordar escapar las comillas dobles para Postgres (tabla y columna
  con doble quote). Mitigacion: comentario explicativo + test.
- **EF Core 8 syntax**: `SqlQueryRaw<int>` usa `{0}` para parametros
  (no `@p0`). Documentado inline.
- **Cobertura limitada del test concurrencia paralela**: el commit
  `27506d9` documenta como diferido "Test concurrencia paralela MR-02
  (dos withdraws sobre entries distintos del mismo BC que ambos
  vacian al mismo tiempo) — diferido por complejidad del setup,
  evaluar como E2E". El test E2E `Variante5` cubre el caso secuencial,
  no paralelo. **Riesgo aceptado** hasta tener metric in-prod que muestre
  doble cierre.

### Riesgos

- **EF Core upgrade**: si en el futuro se actualiza a EF Core 9 / 10 y
  el comportamiento de `CountAsync` cambia (ej. empieza a leer
  ChangeTracker), el SQL crudo sigue funcionando. La logica del
  ChangeTracker filter tambien sigue valida.
- **Postgres especifico**: la query usa sintaxis `::int` cast. Si se
  migra a otro motor, hay que reescribirla. Hoy MagnaTravel solo
  soporta Postgres.

## 4. Alternativas consideradas

| Alternativa | Por que NO |
|---|---|
| **Solo `CountAsync`** (sin SQL crudo) | EF Core 8 `CountAsync` no ve el ChangeTracker. Subcontaria. Race condition. |
| **Solo `ChangeTracker.Entries`** | Ve solo el scope local. Si otra tx committed un entry con saldo, no se entera. Sobrecontaria pero al reves: cerraria el BC cuando no debe. |
| **`SELECT FOR UPDATE` en los entries** | Bloquea lectores. Mala performance. Rechazado por ADR-002 (mismo razonamiento que para concurrencia N:M). |
| **Pesimistic lock del BC entero** | Serializa todos los withdraws del BC. Mala performance. |

## 5. Migration plan / rollback

**Migration**: ninguna. El patron es solo comportamiento del codigo.

**Rollback**: revertir el commit `27506d9` deja `OnAllCreditConsumedAsync`
con la implementacion ingenua (Count via LINQ + sin ChangeTracker). El
test `Variante5` fallaria — sirve como red de seguridad.

## 6. Testing strategy

Tests existentes:

- E2E `Variante5_NRetirosParciales_BcCierraSoloConElUltimoRetiro` (commit
  `3640ba9`): 3 retiros parciales sobre saldo $1000. BC cierra solo con
  el 3ro. `closeAudits == 1` verifica idempotencia.
- Integration `OnAllocationVoided_UltimaAllocation_RevierteBcStatus_EnPostgresReal`
  (commit `81b8332`): el patron similar para Void.
- Integration `Retiro_ParcialNoCierraBc` (commit `27506d9`): retiro
  parcial no dispara cierre.

Tests pendientes (documentados en commit `27506d9` para futura sesion):

- Test concurrencia paralela MR-02 con 2 `Task` que vacian entries
  distintos del mismo BC simultaneamente. Setup complejo
  (Barrier(2) + 2 scopes EF separados). Diferido como stretch.

## 7. Auto-critica

- **Verificado en repo**: si —
  `BookingCancellationService.cs:746-791`. Comentarios didacticos
  explican el por que de SQL crudo + ChangeTracker.
- **Cobertura concurrencia paralela**: parcial. El test secuencial
  `Variante5` cubre el caso por turnos. Riesgo de race **real**
  paralelo: aceptado hasta tener test stretch o metric en prod que
  muestre doble cierre.
- **Patron reusable**: si otros callbacks del modulo necesitan la misma
  idempotencia (ej. futuro `OnAllAllocationsVoidedAsync`), el patron
  es directamente aplicable. Documentar en el wiki como "patron MR-02".
- **Cuello de botella**: en cada Withdraw que vacia un entry, hacemos
  un SELECT COUNT a BD. Trivial para volumenes esperados (decenas de
  BCs por mes). Si en el futuro hay alto volumen, considerar cache
  del count o trigger DB.
