# 2026-06-14 — Saldo a favor aplicado a OTRA reserva (residual "FC4")

## En fácil (para Gastón)

El "saldo a favor" es plata que la agencia ya tiene en la caja (la cobró en su
momento) pero que figura como **deuda con el cliente**. Hasta hoy, cuando se
elegía usar ese saldo para pagar **otra reserva** del mismo cliente, el sistema
**descontaba el saldo pero NO acreditaba la otra reserva** → el cliente perdía su
saldo y la reserva destino seguía figurando impaga. Por eso estaba **bloqueado**.

Ahora quedó terminado: aplicar el saldo a otra reserva crea, por detrás, un
**"pago interno"** en la reserva destino que baja su deuda **sin mover la caja**
(la plata ya entró cuando nació el saldo a favor; solo se cambia la etiqueta de
"le debo al cliente" a "esta reserva está paga"). Es el espejo exacto del
mecanismo que ya existía para el sobrepago.

**Importante:** esto es el **motor por detrás**. La **pantalla** para que el
vendedor elija "usar el saldo en otra reserva" todavía NO se construyó (pasa por
el gate de UX con vos antes). Y el **contador** tiene que firmar el tratamiento
antes de que esto se exponga a vendedores/clientes reales.

## Qué se hizo

Cadena completa de agentes: contador integrado → arquitecto → revisor de
arquitectura → backend → revisor backend + revisor de seguridad → fixes.

### Mecánica
- Al aplicar saldo a favor (`WithdrawalKind.AppliedToNewBooking`), además de
  bajar el bolsillo del cliente, se crea un **Payment "puente" POSITIVO** en la
  reserva destino: `Method="SaldoAFavorAplicado"`, `AffectsCash=false`, **sin**
  asiento en el Libro de Caja, **sin** recibo. Baja la deuda destino porque el
  cálculo de saldo (`ReservaMoneyCalculator`) suma los pagos vivos sin mirar
  `AffectsCash`.
- **Atómico**: bajar el bolsillo + crear el puente + recalcular el saldo de la
  reserva destino van en una sola transacción (cuando el motor es relacional /
  Postgres; en los tests InMemory corre el mismo cuerpo sin transacción). Si algo
  falla, se revierte todo → no queda plata perdida ni a medias.
- **Trazabilidad**: nuevo vínculo `Payment.AppliedFromCreditWithdrawalId` (saldo
  ↔ pago destino) + evento de auditoría `ClientCreditAppliedToBooking`
  (quién, cuándo, de qué saldo, a qué reserva, monto, moneda).

### Reglas de negocio (validaciones)
- **INV-093**: el saldo de un cliente NO se aplica a la reserva de otro cliente.
- **INV-095** (misma moneda / MVP): el saldo solo baja deuda de **su misma
  moneda**. Si la reserva destino no tiene deuda en esa moneda → se rechaza.
  Esto **bloquea el cruce de monedas** (saldo USD → deuda ARS), que es un hecho
  con diferencia de cambio y queda fuera del MVP (lo difiere el contador).
- **INV-096**: solo se aplica a reservas en estado cobrable (En gestión,
  Confirmada, En viaje, A liquidar). Presupuesto/Cotización/Perdida/Cancelada se
  rechazan.
- **INV-097** (tope): no se puede aplicar más que la deuda de la reserva destino
  en esa moneda (no sobre-pagar).

### Blindaje (espejo del puente de sobrepago)
- El puente se **excluye** de todas las listas de pagos visibles (no se puede
  borrar/editar a mano), de tesorería, del libro de caja y de los paneles de
  recaudación/comisiones.
- **No** se le puede emitir recibo (no es un cobro real de caja).
- **I1 (fix permisos)**: se valida ownership de la reserva **destino** — un
  vendedor con alcance acotado no puede aplicar saldo a una reserva del mismo
  cliente pero a cargo de otro vendedor (mismo criterio que el alta de pago
  normal).
- **I2 (fix reportes)**: el panel "Cobros por moneda" ahora filtra `AffectsCash`,
  así el puente positivo no infla los ingresos por moneda (bug latente que el
  puente de sobrepago ya tenía y FC4 agravaba).

## Estado / tests
- Build verde. **Suite unitaria 1823/1823.** (+12 tests nuevos FC4 / I1 / I2.)
- Migración **`Adr030_M1_AddAppliedCreditBridgeToPayments`** (aditiva, reversible;
  columna+índice+FK Restrict). Timestamp `20260614043316` → corre **última**.
- **Pendiente de integración Postgres** (lo corre Gastón en el VPS antes del
  deploy): atomicidad real de la transacción envolvente, FK topológica del puente,
  Up/Down de la migración.

## Lo que NO está hecho (a propósito)
- **Pantalla** para ofrecer "usar saldo en otra reserva" → gate de UX con Gastón.
- **Firma del contador** (tratamiento sin caja/sin recibo, misma moneda) antes de
  exponerlo a clientes reales.
- **Reversa por pantalla** de una aplicación equivocada (hoy solo se deshace a
  mano en la base; la guarda anti-borrado evita orfandad). Follow-up.
- **Cruce de monedas** (diferencia de cambio) — diferido.
