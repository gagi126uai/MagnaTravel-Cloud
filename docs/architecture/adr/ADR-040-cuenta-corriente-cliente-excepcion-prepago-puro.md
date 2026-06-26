# ADR-040 — Cuenta corriente del cliente: excepción controlada al prepago puro (ADR-036)

Fecha: 2026-06-26
Estado: Aceptado (Fase 1 — el MOTOR). La pantalla de configuración del cliente y el aging/mora son Fase 2.
Supersede parcialmente: ADR-036 (prepago puro) — ver "Decisión".

## Contexto

ADR-036 hizo el sistema **prepago puro**: una reserva no puede pasar a "En viaje" ni cerrarse si el cliente
debe (candado duro `ReservationEconomicPolicy.IsClientFullyPaid`, "incondicional, siempre"). Eso es correcto
para clientes de mostrador/ocasionales, pero las agencias también tienen clientes a **cuenta corriente**
(empresas, mayoristas, corporativo) que viajan debiendo y pagan en un plazo.

El dueño decidió incorporar la cuenta corriente **sin romper el prepago**: cada cliente puede ser prepago o a
cuenta, con un default de agencia.

## Decisión

El candado de pago por el lado del cliente deja de ser "incondicional para todos" y pasa a ser **condicional al
modo de cobro del cliente**:

- Cliente **Prepaid** (default): exactamente lo de hoy. `IsClientFullyPaid` queda **byte-idéntico** (cero cambios).
- Cliente **Account** (cuenta corriente): el candado de "saldado" se reemplaza por un candado de **crédito**
  (`ClientCreditPolicy.EvaluateCanTravel` / `EvaluateCanClose`): puede viajar/cerrar debiendo **dentro de su
  límite de crédito por moneda**.

Esto es una **excepción explícita** a la frase de ADR-036 "incondicional, siempre" (ya anotada en el comentario
de `ReservationEconomicPolicy.IsClientFullyPaid`). La bifurcación por modo vive en los *callers* (el gate
manual y el job); la función pura del prepago no se toca.

### Reglas firmes del dueño

1. **Modo por cliente**, con default de agencia (`OperationalFinanceSettings.DefaultCustomerBillingMode`).
2. **Límite de crédito por moneda** (tabla nueva `CustomerCreditLimitByCurrency`). El campo zombie
   `Customer.CreditLimit` queda **muerto** (no se lee ni se escribe).
3. **"Sin límite definido en una moneda" = esa moneda es PREPAGO**: si el cliente debe en una moneda que no
   tiene fila de límite, se bloquea (límite cero, no infinito). **FRENA SIEMPRE, aun con la llave en "solo
   avisar"** (decisión del dueño 2026-06-26): es una garantía de prepago que la agencia no relajó.
4. **Mora = frena todo** (cualquier deuda vencida bloquea entero). **Fase 1 NO implementa mora**: el evaluador
   recibe `enMora=false`. Punto de extensión documentado para la Fase 2 (vencimientos = `PaymentTermsDays`).
5. **Pasarse de un límite DEFINIDO al viajar = FRENA por default**, configurable a "solo avisar" por agencia
   (`OperationalFinanceSettings.BlockTravelWhenCreditExceeded`, default true=FRENA). La llave SOLO afloja este
   caso (superar un límite que sí tiene); NO afloja el de moneda sin límite (punto 3). Nunca toca prepago. Aun
   en "avisar", el branch Account SIEMPRE emite el aviso.
6. **Comisión del vendedor = se gana al COBRAR (como hoy)**. Cerrar una reserva Account con deuda NO devenga
   comisión.
7. **El cierre de un Account NO re-chequea el límite** (decisión del dueño 2026-06-26): el viaje ya ocurrió, no
   tiene sentido trabar el cierre por crédito; la deuda queda viva en su cuenta corriente (AR canónico). El
   cierre Account es **incondicional** y no llama a la política de crédito (no existe un `EvaluateCanClose`).

## Modelo de datos (aditivo, reversible — migración `Adr040_M1`)

| Dónde | Campo | Tipo | Default |
|---|---|---|---|
| `Customer` | `BillingMode` | `CustomerBillingMode?` (nullable) | null (= hereda) |
| `Customer` | `PaymentTermsDays` | int | 0 (Fase 2) |
| `OperationalFinanceSettings` | `DefaultCustomerBillingMode` | enum (int) | Prepaid (0) |
| `OperationalFinanceSettings` | `BlockTravelWhenCreditExceeded` | bool | true (FRENA) |
| tabla `CustomerCreditLimitByCurrency` | (CustomerId, Currency, Limit) | — | índice único (CustomerId, Currency) |

`Account` no se activa para nadie hasta setear `BillingMode=Account` a mano. Un binario viejo ignora lo nuevo →
sigue prepago. La migración es aditiva; el `Down` la revierte.

## Cómo quedaron los 4 arreglos del review de arquitectura

- **B1 — exposición incluye Traveling**: se NO reusa `FinancePositionService.ReceivableDebtStatuses` (que
  excluye Traveling por ADR-036). Se agregó el set DEDICADO `EstadoReserva.CreditExposureStatuses`
  ({InManagement, Confirmed, Traveling, Closed}) y el lector `CustomerCreditExposureReader`. Un cliente a cuenta
  con un hermano ya "En viaje" cuenta esa deuda contra su límite (sin esto, la exposición quedaba subestimada).
- **B2 — re-validación de concurrencia en el job**: el apply, además del `MoneyGate` escalar, lleva un
  `ClientCreditRecheck` que **re-lee la exposición TOTAL FRESCA del cliente por moneda** y re-evalúa
  `ClientCreditPolicy` justo antes de aplicar. Un cobro en OTRA reserva del mismo cliente entre el plan y el
  commit se ve. Para Account el `MoneyGate` escalar es `None` (no exigimos saldo cero).
- **B3 — límite por moneda en tabla nueva**: `CustomerCreditLimitByCurrency` (espejo de
  `SupplierBalanceByCurrency`). `Customer.CreditLimit` queda zombie. Conjunto de monedas abierto. Ausencia de
  fila = moneda prepago.
- **B4 — cerrar con deuda (Account)**: bifurcado en los 3 gates (job confirmed→traveling, job traveling→closed,
  cierre manual `EnsureCanCloseAndStampClosedAt`). `CommissionAccrualPersister`/`SellerCommissionCalculator` ya
  cortan si `Balance > 0` → cerrar con deuda NO devenga comisión (verificado, sin cambios). El KPI
  `TotalPendingBalance` excluye Closed por diseño; la deuda de un Account cerrado vive en el AR canónico
  (`FinancePositionService`, cuyo `ReceivableDebtStatuses` incluye Closed con saldo) — documentado en el código.

## Consecuencias

- Cero regresión sobre el prepago: el branch Prepaid es byte-idéntico (mismo set de tests de ADR-036 verde).
- La exposición de crédito es un query por cliente al pasar a viajar/cerrar (O(reservas con deuda del cliente)),
  trivial para el volumen actual; el job lo batchea (anti N+1).
- Pendiente Fase 2: pantalla de config del cliente (gate UX), vencimientos/mora (`PaymentTermsDays`), aging y un
  KPI de cartera vencida.

## Lo no verificado / abierto

- La llave "solo avisar" hoy aplica tanto al caso "supera el límite" como al caso "debe en moneda sin límite"
  (ambos son "exposición por encima de lo permitido"). Si el dueño quiere que la moneda-sin-límite SIEMPRE frene
  aunque la llave esté en avisar, es un ajuste de una línea en `ClientCreditPolicy`. **A confirmar con el dueño.**
- Reconocimiento de ingreso y forma de pago del comprobante en cuenta corriente: decisión del contador (no toca
  este motor; la facturación ya está desacoplada del cobro por ADR-037).
