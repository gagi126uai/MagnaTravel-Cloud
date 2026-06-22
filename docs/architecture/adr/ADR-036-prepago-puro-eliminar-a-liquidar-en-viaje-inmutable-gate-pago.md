# ADR-036 — Prepago puro: eliminar "A liquidar", "En viaje" inmutable, gate de pago del cliente

- Estado: Aceptado (decisión cerrada del dueño)
- Fecha: 2026-06-21
- Supersede parcialmente: ADR-035 (las decisiones 4-bis "reabrir Closed→ToSettle para facturar tarde" y la
  presencia de ToSettle en las listas de capacidades/transiciones quedan derogadas).
- No toca: ADR-002 (anulación formal NC+ND, firmada por el contador) ni ADR-033 (cobrabilidad = venta firme +
  deuda real) salvo por el ajuste de qué estados son "firmes" (sale Traveling).

## Contexto

El negocio de MagnaTravel opera en modo **prepago puro**: el cliente paga el 100% y el operador cobra el
100% **antes** del viaje. No hay una etapa de liquidación posterior al viaje. El ciclo de vida heredado de
ADR-020 todavía arrastraba el estado "A liquidar" (`ToSettle`), un desvío manual opcional post-viaje para
saldar con el operador, que ya no corresponde al modelo real. Además, "En viaje" (`Traveling`) permitía
editar (con autorización), cobrar y facturar, lo que es incoherente: si el viaje ya empezó, no hay nada que
cobrar ni renegociar.

## Decisiones

### 1. Eliminar el estado "A liquidar" (ToSettle)

Todo es prepago. Se elimina `ToSettle` del enum (`EstadoReserva`), de la matriz de transiciones, de las
listas de capacidades, de la deuda con proveedor, del candado de edición, de los contadores del dashboard,
del tab de listado y de toda lista de estados "vivos/firmes". La única salida de `Traveling` es `Closed`
(cierre por fin de viaje).

### 2. "En viaje" (Traveling) = solo lectura TOTAL

`Traveling` se comporta igual que `Closed`/Finalizada: **no se edita** (ni servicios, ni pasajeros, ni datos
de cabecera) **ni con autorización**, **no se cobra y no se factura**. La factura de venta se emite en
`Confirmed` (Confirmada), **antes** de viajar. El bloqueo real de edición lo impone la **política de
capacidades** (`ReservaCapabilityPolicy`), que dejó de incluir `Traveling` en sus estados editables; esa
política es la primera compuerta de todo write-path (`ReservaCapacityRules.Ensure*EditableByStateAsync`,
`BookingService.GuardServicesEditableByStateAsync`). Por eso sacar `Traveling` del `ReservaLockGuard` NO lo
deja editable: el candado de autorización ya no es la defensa de Traveling.

### 3. No reabrir una Finalizada para "facturar tarde"

Se elimina la transición revert `Closed → ToSettle` (introducida en ADR-035 4-bis). `Closed` revierte **solo**
a `Traveling` (revert de cierre prematuro). Si hay que corregir una factura de una reserva finalizada, se
hace con **Nota de Crédito / Débito** (flujo ya permitido, ADR-002), sin reabrir el estado.

### 4. Gate de pago para pasar a "En viaje" (Traveling)

- **Cliente saldado = candado DURO e INCONDICIONAL** (`Balance <= 0`, con tolerancia de redondeo). Sin eso la
  reserva NO viaja. Este gate **NO depende** de la llave `RequireFullPaymentForOperativeStatus` (que sigue
  gobernando otros read-models de tesorería, pero no este pase). Se extrajo un helper puro
  `ReservationEconomicPolicy.IsClientFullyPaid(decimal balance)` que reciben idéntico el pase manual y el job.
  - **Manual** (`ReservaService.EnsureCanStartTravelingAsync`): si el cliente debe → `InvalidOperationException`
    → 409. Mensaje sin montos.
  - **Job** (`ReservaLifecycleAutomationService.AutoTransitionConfirmedToTravelingAsync`): si el cliente debe
    → NO promueve, cuenta como bloqueado, loguea y reintenta en la próxima corrida. **NO lanza excepción**
    (un file con saldo no debe abortar la corrida de los demás).
- **Operador = solo AVISO, NO traba el viaje.** Limitación de datos: `SupplierPayment.ReservaId` es nullable,
  así que no se puede atribuir de forma confiable un pago al operador a una reserva concreta; bloquear daría
  falsos frenos. El **casillero "pagado al operador" por servicio** que habilitaría un gate real de operador
  es **trabajo siguiente** (fuera del alcance de este ADR).

### 5. Reserva con plata viva NO admite baja simple

En `ReservaCapabilities.EvaluateCancel`, si la reserva tiene **pagos vivos** (`Payments` no soft-deleted,
**incluyendo pagos puente `AffectsCash=false`**: sobrepago→saldo a favor, saldo a favor aplicado) o **factura
con CAE vivo**, `CanCancel = No` con un motivo que enruta a "Anular" (camino formal NC/ND). El guard de
escritura en la transición a `Cancelled` (`ReservaService`) se reforzó: antes solo bloqueaba por CAE vivo;
ahora también bloquea por pagos vivos. El flujo de anulación formal de ADR-002 (NC+ND) **no se toca**.

## Migración: Adr036_M1_DropToSettle

Raw SQL contra Postgres sobre `chk_TravelFiles_status_valid`:

- **Up**: DROP del CHECK → `UPDATE ... SET Status='Closed', ClosedAt=COALESCE(ClosedAt, now()) WHERE
  Status='ToSettle' AND Balance<=0` (saldada → Finalizada) → `UPDATE ... SET Status='Confirmed' WHERE
  Status='ToSettle' AND Balance>0` (con deuda → Confirmada) → recrear CHECK con 10 valores (sin ToSettle):
  `Quotation, Budget, InManagement, Confirmed, Traveling, Closed, Lost, Cancelled, PendingOperatorRefund,
  Archived`.
  - La rama con deuda va a **`Confirmed`, NO a `Traveling`**: una `Traveling` con deuda quedaría en un callejón
    sin salida (Traveling salió de `SaleFirmStatuses`, así que no es cobrable, no aparece en cuentas por cobrar
    formales y no es cerrable porque el cierre exige `Balance<=0`). `Confirmed` sí es cobrable y visible en AR;
    el job re-promueve a `Traveling` recién cuando la reserva queda saldada (gate `IsClientFullyPaid`).
- **Down (LOSSY)**: DROP del CHECK → recrear CHECK con los 11 valores (incluyendo ToSettle). El re-mapeo de
  filas NO se deshace (las que estaban en ToSettle quedan en Closed/Confirmed).

**Validación**: el SQL crudo NO se valida en InMemory (precedente Adr020_M2). Debe correrse/validarse contra
**Postgres real** antes de desplegar (gate del dueño).

## Asimetría intencional: Traveling no es "firme cobrable"

`Traveling` se quitó de `SaleFirmStatuses` y `ActiveCollectionStatuses`. Esto es **intencional** (camino
estricto): en prepago puro una reserva no puede entrar a "En viaje" debiendo (candado duro de pago del
cliente), así que una `Traveling` con deuda no debería existir y no se la trata como cuenta por cobrar viva.
La capacidad `CanRegisterPayment` además bloquea `Traveling` explícitamente con su propio motivo ("En viaje
no se cobra"). El test cruzado de coherencia (`ReservaCapabilitiesCrossCheckTests`) documenta esta asimetría
como esperada (la política nunca dice "sí cobrar" donde `EnsureCollectable` diría "no").

## Llave `RequireFullPaymentForOperativeStatus`

**NO se elimina.** Se verificó que gobierna más que el gate de Traveling: alimenta read-models de tesorería
en `PaymentService` (`BlockedOperationalCount`, filtro "blocked" del worklist de cobranzas) vía
`EconomicRulesHelper.GetOperativeBlockReason`. El gate de Traveling se hizo **independiente** de la llave
(usa el helper puro `IsClientFullyPaid`), pero la llave queda viva para sus otros usos.

## Consecuencias

- Frontend (lote aparte): quitar el chip/tab "A liquidar" (`ToSettleCount` ya no viene en
  `ReservaListSummaryDto`), reflejar Traveling como solo lectura total, mostrar el motivo de "anular en vez de
  cancelar" y el cartel de "cliente debe, no puede viajar".
- El casillero "pagado al operador" por servicio (gate real de operador) queda como trabajo siguiente.
