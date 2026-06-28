# Análisis: ¿hay una "desconexión" entre Reserva y Operador? (cancelaciones)

Fecha: 2026-06-28
Autor: software-architect (análisis fundado en código, no en suposiciones)
Alcance: relación Reserva ↔ cuenta del Operador (proveedor), con foco en cancelaciones/anulaciones.

> Todo lo afirmado abajo está verificado contra el código (archivo:línea). Lo que NO pude
> verificar al 100% lo marco explícitamente como "a confirmar". No invento reglas de negocio
> ni fiscales: las decisiones fiscales se marcan para el contador.

---

## 1. Mapa objetivo: qué fluye HOY de la reserva a la cuenta del operador

La cuenta del operador (pantalla `SupplierAccountPage.jsx`) tiene 5 zonas: Servicios comprados,
Deuda por reserva, Cuenta corriente (extracto), Reembolsos a cobrar, Datos bancarios.
El **saldo/extracto/balance** se construye de **exactamente DOS fuentes**:

### Lo que SÍ está conectado (la columna vertebral funciona)

| Dato de la reserva | Cómo llega al operador | Evidencia |
|---|---|---|
| Costo de servicios CONFIRMADOS | Suma a `ConfirmedPurchases` (deuda de compra), por moneda, sólo modo reseller | `SupplierService.cs:501` (`CalculateSupplierConfirmedPurchasesAsync`), regla `WorkflowStatusHelper.CountsForSupplierDebtByType` `:85` |
| Pagos al operador (egresos) | Suma a `TotalPaid` / abonos del extracto | `SupplierService.cs:598-652` |
| Saldo por moneda | `Balance = ConfirmedPurchases − TotalPaid`, materializado | `SupplierService.cs:511-522` (`SupplierBalanceByCurrency`) |
| Deuda por reserva | Sección aparte, misma regla de deuda | `SupplierService.cs` (debt-by-reserva) |
| Pago de MÁS al operador | Genera saldo a favor de primera clase (`SupplierCreditEntry`, ADR-041) | `SupplierCreditService.cs` — alimentado por sobrepago, NO por cancelación |
| Reembolso ESPERADO por anulación | Read-model **separado** "Reembolsos a cobrar" | `OperatorRefundReadModelService.cs:127-139` = `RefundCap − ReceivedRefundAmount` por moneda |

La cancelación arma una **línea por servicio/operador** (`BookingCancellationLine`) con su
operador, moneda, penalidad y circuito de reintegro propio; el `RefundCap` = lo pagado al
operador por ese servicio, topeado por el costo, **menos la penalidad** (en el draft la
penalidad es 0). Ver `BookingCancellationService.cs:4768` (`AssignRefundCapsAsync`) y la
entidad `BookingCancellationLine.cs:135-147`.

### Lo que NO fluye (acá está la desconexión real)

**(a) La penalidad confirmada del operador NUNCA se postea a la cuenta del operador.**
Al confirmar la multa, `CaptureDebitNoteClassification` toca SÓLO campos del BC **padre**
(`bc.PenaltyAmountAtEvent`, `bc.ConceptKind`, `bc.PenaltyStatus`) — `BookingCancellationService.cs:3940-3943`.
Eso existe únicamente para emitir la **Nota de Débito al CLIENTE** (pass-through).
No hay ningún asiento, línea de extracto ni efecto de saldo en la cuenta del operador.

**(b) Confirmar la penalidad NO recalcula el reembolso esperado.**
El comentario del propio código en `:4754-4757` AFIRMA que *"cuando el operador confirma la
penalidad, el flujo de confirmación ajusta el cap restando el monto confirmado"*. **Eso es
falso / no está implementado.** `ConfirmPenaltyAsync` (`:3138`) NO setea `line.PenaltyAmount`
ni reduce `line.RefundCap`. Sólo escribe el escalar del padre, la moneda de la multa en la
línea (registro), las fechas y emite la ND. Resultado: el read-model "Reembolsos a cobrar"
(`OperatorRefundReadModelService.cs:135`, `RefundCap − ReceivedRefundAmount`) sigue mostrando
el monto **sin descontar la multa** → **sobreestima** lo que el operador debe devolver.

**(c) El reembolso RECIBIDO del operador no reconcilia la cuenta del operador.**
`OperatorRefundService` registra la imputación, incrementa `line.ReceivedRefundAmount` y crea
un **`ClientCreditEntry`** (le acredita al CLIENTE) — `OperatorRefundService.cs:486-511`,
`:615-628`. **No escribe nada en la cuenta del operador**: grep de `SupplierPayment` /
`SupplierCreditEntry` en ese servicio → **sin resultados**.

**(d) Consecuencia combinada (lo que percibe el dueño).**
Cuando se cancela un servicio, su estado pasa a "Cancelado" y **deja de contar como deuda**
(`WorkflowStatusHelper.CountsForSupplierDebt` = false para Cancelado, `:71-74`). Si la agencia
**ya le había pagado** al operador, la deuda baja a 0 pero el pago queda → el saldo del
operador se vuelve **negativo** (operador "nos debe") por el **monto total pagado**, sin
descontar la multa que el operador se queda. Y como (c) no postea el reembolso recibido al
operador, ese saldo negativo **nunca se cancela** aunque el operador efectivamente devuelva la
plata. Quedan **dos representaciones paralelas** del mismo hecho ("el operador nos debe") —
saldo negativo de la cuenta vs. read-model "Reembolsos a cobrar"— que **no coinciden** entre sí
y **ninguna** descuenta la multa.

---

## 2. Veredicto imparcial sobre la percepción del dueño

**Tiene razón en que hay una desconexión real, pero es más ANGOSTA de lo que la palabra
"desconexión" sugiere.**

- **Dónde tiene razón (sin complacencia, esto es un problema real):** todo el circuito de
  **cancelación/anulación del lado operador** está desconectado de la cuenta del operador. La
  multa no se postea, el reembolso esperado no se neto-de-multa, el reembolso recibido no
  reconcilia el saldo, y hay un comentario en el código que **miente** sobre que esto ya
  funciona (`:4754-4757`). Eso es un hueco estructural, no cosmético.

- **Dónde estaría SOBREvalorando el problema:** la **columna vertebral** reserva→operador (costo
  → deuda, pagos → saldo, por moneda, con su regla única reseller/intermediación) **SÍ está bien
  conectada y con fuente única**. No es cierto que "todo" esté desconectado. El flujo normal
  (comprar, deber, pagar) es sólido.

- **Dónde estaría SUBvalorando el problema:** si piensa que es sólo "un tema de pantalla / que
  no se ve", se queda corto. No es display: faltan **posteos y una reconciliación**. La multa
  sólo vive como dato fiscal hacia el cliente; nunca fue modelada como hecho económico del lado
  operador. El "reembolso esperado" no es un número recalculado de primera clase.

**Resumen del veredicto:** desconexión = SÍ, pero localizada en **cancelación/penalidad/reembolso
del lado operador**, no en el flujo de costo/pago normal.

---

## 3. Causa raíz arquitectónica

La cuenta del operador es una **vista derivada** sobre **exactamente dos fuentes**: compras
confirmadas + pagos al operador. Toda la economía de la cancelación (multa que el operador
retiene, reembolso que debe, reembolso que devuelve) vive en **otro agregado**
(`BookingCancellation` + líneas + `OperatorRefundAllocation` + `ClientCreditEntry`) que fue
cableado hacia el **lado CLIENTE** (NC/ND, saldo a favor del cliente) pero **nunca fue cableado
de vuelta como FUENTE de la cuenta del operador**.

Es, a la vez:
1. **Una fuente faltante**: la cuenta del operador no lee el agregado de cancelación.
2. **Un posteo faltante**: ni la multa confirmada ni el reembolso recibido se asientan en el
   operador.
3. **Un hueco del modelo de dominio**: la penalidad se modela como escalar del padre para la ND
   del cliente, **no** como hecho de la línea (`line.PenaltyAmount`/`RefundCap` no se recalculan);
   el "reembolso esperado" no es un número de primera clase recomputado al confirmar la multa.

---

## 4. Plan por fases (lo más chico-coherente primero)

> Marco cada fase como **TÉCNICA** (se puede hacer sola) o **FISCAL/NEGOCIO** (necesita decisión
> del dueño y/o firma del contador antes de tocar el comportamiento de plata).

### Fase 0 — Verdad del reembolso esperado (TÉCNICA, sin riesgo fiscal)
Al confirmar la multa, escribir el hecho en la **línea**: setear `line.PenaltyAmount` y recalcular
`line.RefundCap = capBeforePenalty − penalidadConfirmada` (nunca negativo). Borrar/arreglar el
comentario falso de `:4754-4757`. Con esto, "Reembolsos a cobrar" deja de mentir.
- Frontera de módulo: vive entero en `BookingCancellationService.ConfirmPenaltyAsync` +
  `OperatorRefundReadModelService` (que ya lee de la línea).
- Datos disponibles: sí, todos (la confirmación ya trae `ConfirmedPenaltyAmount`).
- Tests: confirmar multa → `RefundCap` baja por la multa; multi-operador; multi-moneda; idempotencia.

### Fase 1 — Que la cuenta del operador y "Reembolsos a cobrar" reconcilien (TÉCNICA + 1 decisión)
Hoy hay dos números del mismo hecho que no coinciden. Hay que decidir **una sola verdad**:
- Recomendado: la cuenta del operador debe **mostrar/reflejar el reembolso esperado** derivado
  del mismo agregado de cancelación (no recalcular a mano). Evitar la doble representación: si el
  saldo negativo "operador nos debe" ya nace de que la compra cancelada cae de la deuda, ese
  número debe **conciliar** con "Reembolsos a cobrar" (neto de multa, tras Fase 0).
- Esto es lectura/derivación; no mueve plata todavía.

### Fase 2 — Reconciliar el reembolso RECIBIDO contra la cuenta del operador (TÉCNICA + FISCAL)
Cuando el operador devuelve la plata (`OperatorRefundService`), postear el efecto en la cuenta
del operador para que el saldo negativo **se cancele**. Acá la multa retenida deja de ser un
hueco y el reembolso devuelto cierra el círculo.
- **Necesita firma del contador**: cómo se asienta la multa que el operador retiene (¿costo/gasto
  de la agencia? ¿queda como mayor costo del servicio?) y cómo se representa el "por cobrar al
  operador" (¿saldo a favor de primera clase tipo `SupplierCreditEntry`? ¿una cuenta por cobrar
  distinta?). No avanzar el posteo sin esa definición.

### Fase 3 — Cierre SIN multa / reembolso total (NEGOCIO, gate UX con Gastón)
Hoy el botón "Confirmar multa" asume que **siempre hay multa**
(`ReservaCapabilities.cs:696-700`, gated por `HasPendingOperatorPenalty`) y confirmar **exige**
monto + emite ND. Falta el camino explícito "el operador confirmó **sin** multa / reembolso
total", para cerrar el BC sin forzar una multa fantasma ni una ND.
- Gate UX obligatorio: la pantalla/flujo lo define Gastón.

### Fase 4 — Moneda de la multa hacia la ND (FISCAL, follow-up ya marcado en código)
`BookingCancellationLine.cs:86-97` ya advierte que la moneda en que el operador retuvo la multa
(ISO puro, USD/ARS) **no está cableada** a la moneda de emisión de la ND al cliente (espacio
ARCA). Es un seam fiscal aparte (cliente, no operador). Requiere mapper ISO→ARCA + firma del
contador. Mantener fuera del alcance salvo que se priorice.

**Orden recomendado:** 0 → 1 → 3 → 2 → 4. (La Fase 3 antes que la 2 porque el cierre-sin-multa es
negocio puro y desbloquea casos hoy trabados; la 2 toca plata y espera al contador.)

---

## 5. Preguntas abiertas para cerrarlo a la primera

### Para Gastón (negocio/producto)
1. **Cierre sin multa.** ¿Querés un botón explícito "el operador no cobró multa / devuelve todo",
   separado de "Confirmar multa"? — *Recomiendo: sí, es el caso más común y hoy no existe.*
2. **Qué número manda en la cuenta del operador tras una cancelación.** ¿La cuenta del operador
   debe mostrar "te debe $X" (reembolso esperado, neto de multa) como saldo a favor, o lo dejamos
   sólo en la sección "Reembolsos a cobrar"? — *Recomiendo: una sola verdad, reflejada en el saldo
   del operador, derivada del circuito de reembolso (no dos números).*
3. **Visibilidad de la multa en la ficha del operador.** ¿Querés ver, en la cuenta del operador,
   cuánta plata se "comió" en multas ese operador (histórico)? — *Recomiendo: sí, es info de
   negocio útil para negociar; va como dato, sin afectar saldo hasta definir lo contable.*

### Para el contador (fiscal/contable) — NO avanzar Fase 2 sin esto
4. **Naturaleza de la multa retenida por el operador.** Cuando el operador se queda con una multa
   sobre plata ya pagada, ¿cómo se asienta del lado de la agencia? (¿mayor costo del servicio?
   ¿gasto? ¿pérdida?). — *Recomiendo que el contador lo defina; no asumo.*
5. **"Por cobrar al operador" como cuenta.** El reembolso esperado del operador, ¿se representa
   como saldo a favor de proveedor (mismo mecanismo que el sobrepago, `SupplierCreditEntry`) o
   como una cuenta por cobrar distinta? — *Recomiendo reusar el saldo a favor de operador si el
   contador lo valida, para no multiplicar mecanismos.*
6. **Reembolso recibido: doble efecto.** Hoy el reembolso recibido sólo acredita al CLIENTE
   (`ClientCreditEntry`). ¿El mismo evento debe además cancelar el "por cobrar al operador"? ¿Hay
   riesgo de doble conteo entre el crédito al cliente y el saldo del operador? — *Recomiendo
   validar con el contador que cliente y operador son dos patas del mismo asiento, no dos ingresos.*

---

## Apéndice: correcciones a los "hechos" de partida (imparcialidad)

- El supuesto "la ND se emite **hard-coded a ARS**" es **impreciso**. La moneda de emisión de la
  ND la toma de la factura original: `FreezeDebitNoteSnapshot` usa `originatingInvoice.MonId`
  (puede ser DOL) — `BookingCancellationService.cs:4092-4094`. El default ARS sólo aplica si
  `MonId` viene vacío o "PES". El "ARS" del flujo de confirmación (`:3271`) es para **auditoría**,
  no para la emisión. El ManualReview por moneda extranjera se dispara cuando es extranjera **Y**
  la cotización es poco confiable (≤0 o =1), no por ser extranjera — `:4070-4080`. Es un seam del
  lado CLIENTE, no de la cuenta del operador; lo separo para no inflar el problema del operador.
- Confirmado el resto de los hechos de partida (Set A: cuenta = compras + pagos, sin enlace a
  multas/reembolsos; Set B: la multa emite ND al cliente y no recalcula el reembolso). El matiz
  que AGREGO es que el comentario `:4754-4757` documenta un recálculo que **no existe**.
