# Cuenta del operador: reembolsos, multa a la vista y navegación — SPEC FINAL

> **Estado: FINAL, APROBADA por Gastón (2026-07-03).** Las 6 preguntas se respondieron: P1=C, P2=A,
> P3=A, P4=A, P5=A, P6=A. frontend-senior implementa esto al pie de la letra; cualquier desvío por
> costo técnico o regla de negocio se le repregunta a Gastón ANTES.
>
> **OJO — decisiones 1 y 4 dependen de campos que el backend HOY no expone** (ver "Dependencias
> técnicas"). Esas dos partes no se pueden construir hasta que el backend agregue esos datos; las
> decisiones 2, 3 y 5 se pueden construir ya.
>
> **Fuente única:** `docs/ux/guia-ux-gaston.md`. Contrastado con el código real (ver "Estado real" abajo).

---

## Contexto — 5 decisiones de dirección de Gastón (2026-07-02, NO se reabren)

1. **"Registrar reembolso recibido"** debe **mostrar la multa** de la cancelación y explicar el estimado como **"pagado − multa"**. El backend ya descuenta la multa; falta mostrar el desglose (hoy se ve un "Estimado" pelado).
2. La **multa del operador** debe poder **verse/gestionarse desde el lado del operador** (hoy solo se confirma desde la reserva; falta el puente visual ficha-del-operador ↔ cancelación-de-la-reserva).
3. En la **solapa Servicios de la reserva**: chip **"Operador: X" clickeable** que lleva a la ficha del operador (respetando el permiso de ver proveedores; quien no lo tiene ve el texto sin link).
4. Cuando el reembolso estimado da **$0**, explicar **por qué** en la pantalla (ej. "todavía no le pagaste nada al operador" / "la multa cubre todo lo pagado"), en vez del "$0" seco.
5. **"Reembolsos operador" sale del menú principal** y queda como **solapa dentro de la ficha de cada operador** (la lista ya vive ahí; es reordenar navegación, sin lógica nueva).

---

## Estado real verificado en el código (para no diseñar sobre datos que no existen)

- **Ficha del operador** `SupplierAccountPage.jsx`: encabezado con los 3 recuadros por moneda (Le debo / Me tiene que devolver / Saldo a favor) **ya construidos** + 6 solapas: Cuenta corriente · Deuda por reserva · Servicios comprados · **Reembolsos** · Datos bancarios · Datos. La solapa "Reembolsos" ya existe (usa `OperatorRefundsPendingSection`).
- **Panel "Registrar reembolso recibido"** `RegistrarReembolsoRecibidoInline.jsx`: en línea, dentro de "Cuenta corriente", obliga a elegir un reembolso pendiente, muestra por renglón solo **"Estimado: US$ X"**. NO muestra multa ni "pagado".
- **Bandeja global** `/operator-refunds` (`OperatorRefundsPage.jsx`): lista los reembolsos pendientes de **todos** los operadores, con semáforo (A tiempo / Por vencer / **Vencido** / Abandonado). Los vencidos NO desaparecen (requisito explícito). Gateada por `tesoreria.supplier_payments`. **Esta es la que la decisión 5 saca del menú.**
- **Solapa Servicios** `ServiceList.jsx`: el renglón muestra el nombre del servicio (`svc.name`); **hoy NO muestra el operador**. Los datos `svc.supplierName` y `svc.supplierPublicId` YA existen en el objeto del servicio (se usan internamente). El chip de la decisión 3 es un elemento nuevo.
- **Ruta de la ficha del operador:** `/suppliers/:publicId/account`.
- **Dato que HOY el backend NO expone** (dependencia técnica, ver al final): en cada reembolso pendiente solo viene `estimatedAmount` (= pagado − multa) por moneda. **No** vienen por separado "pagado al operador" ni "multa retenida", ni un motivo del $0. Decisiones 1 y 4 necesitan esos campos.

---

## Reglas de la guía que se aplican tal cual (no reinventar)

1. **Cuenta del operador — LOS DOS NÚMEROS + Circuito (2026-07-01):** 3 recuadros por moneda; **"Me tiene que devolver" (naranja) = total; solapa Reembolsos = su detalle** (misma cifra por moneda); circuito de cancelación como **tablita colapsable** dentro de Cuenta corriente con la línea **"Multa retenida por el operador"** (etiqueta en español, número de reserva visible, nunca códigos internos); botón **"Registrar reembolso recibido"** en Cuenta corriente, en línea, obliga a elegir el reembolso.
2. **Multimoneda dura (2026-06-09/10):** pesos y dólares **siempre separados, nunca sumados**; una sola moneda se ve como hoy.
3. **Enmascarado sin `cobranzas.see_cost`:** costos/deuda al operador se muestran **"—" en gris**, nunca en color, con **un** aviso único por pantalla ("No tenés permiso para ver los montos"). **La multa y el "pagado al operador" son cifras de costo → se enmascaran igual** (regla general 2026-06-05).
4. **Todo EN LÍNEA, nunca ventana flotante** (2026-06-09 P3).
5. **Montos de reembolso = "estimado, sujeto a deducciones"** (patrón ya existente); nunca presentarlos como cifra firme.
6. **Labels en español, nada de jerga/IDs/GUID/códigos internos** (gate data-exposure).
7. **Links a otra ficha respetando permiso** (patrón ya usado en la app; decisión 3 lo pide explícito).

---

## Mockups propuestos (reflejan la recomendación de cada pregunta; se ajustan según respuestas)

### A. Panel "Registrar reembolso recibido" — multa a la vista (decisiones 1 y 4) · ver P3/P4

```
┌─ Registrar reembolso recibido ───────────────────────── [x] ─┐
│  ¿A qué reembolso pendiente corresponde? *                   │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ ○ Reserva #R-1042 · Fam. García            US$        │   │
│  │      Pagaste US$ 500 − Multa US$ 100 = te devuelven   │   │
│  │      US$ 400 (estimado)                               │   │
│  │ ○ Reserva #R-1050 · Pérez                  $          │   │
│  │      No hay nada para devolver: la multa se quedó     │   │
│  │      con todo lo que le pagaste                       │   │
│  └──────────────────────────────────────────────────────┘   │
│  Reembolso elegido: reserva R-1042 en dólares.               │
│                                                              │
│  Monto recibido (US$)  Fecha *      Método       Referencia │
│  [ 400,00 ]            [ 03/07 ]    [Transfer.▾]  [ # ]      │
│                                    [ Cancelar ] [ Confirmar ]│
└──────────────────────────────────────────────────────────────┘
```
- El desglose "Pagaste − Multa = te devuelven" solo se ve con permiso de ver costos; sin permiso, el renglón muestra "—" y el aviso único de siempre.

### B. Solapa "Reembolsos" del operador — desglose + puente a confirmar la multa (decisión 2) · ver P2

```
┌─ Reembolsos a cobrar del operador ───────────── [Actualizar] ┐
│  Cancelaciones donde el operador tiene que devolver plata.   │
│  Los montos son estimados, sujetos a deducciones.            │
│ ──────────────────────────────────────────────────────────  │
│  Reserva #R-1042  [Por vencer]   Cliente: Fam. García        │
│     Pagado US$ 500 · Multa US$ 100 · Te devuelven US$ 400    │
│     (estimado)                                Vence: 10/07   │
│ ──────────────────────────────────────────────────────────  │
│  Reserva #R-1060  [Vencido 5 días]   Cliente: López          │
│     ⚠ Falta confirmar la multa de esta anulación.           │
│       [ Ir a la reserva a confirmar ]                        │
└──────────────────────────────────────────────────────────────┘
```
- La confirmación de la multa se sigue haciendo en la reserva (un solo lugar); desde acá solo se salta a ella.

### C. Solapa Servicios de la reserva — chip "Operador" clickeable (decisión 3) · ver P5

```
 TIPO     DESCRIPCIÓN                      FECHA      ESTADO
 ✈ AÉREO  Buenos Aires ➔ Madrid           12/08      Emitido
          Operador: Despegar  →                       ← chip link
 🏨 HOTEL  Hotel Riu Plaza                 12/08      Confirmado
          Operador: Ola Mayorista  →
```
- "Operador: Despegar" es un link a `/suppliers/{id}/account` **solo si el usuario puede ver proveedores**; si no, se ve el texto plano "Operador: Despegar" sin link.

### D. Navegación — "Reembolsos operador" sale del menú, SIN vista global (decisión 5 + P1=C)

```
ANTES (menú principal)           DESPUÉS
  Cobranzas                        Cobranzas
  Proveedores                      Proveedores
  Reembolsos operador   ← se saca     (dentro de cada operador →
  ...                                  solapa "Reembolsos")
```

- **P1=C (elección consciente de Gastón, contra la recomendación):** "Reembolsos operador" se saca del
  menú y **NO se reemplaza con ninguna vista global**. Los reembolsos se ven **operador por operador**,
  en la solapa "Reembolsos" de cada ficha.
- **Trade-off que queda anotado:** hoy la pantalla global juntaba los reembolsos **vencidos de todos los
  operadores** en un solo lugar para no perderlos de vista. Al sacarla, **para detectar un vencido hay
  que entrar operador por operador**. Gastón lo eligió así a sabiendas.
- **Mitigación posible (NO se diseña acá, solo se menciona):** la guía ya tiene el patrón de **tarjetas de
  Cobranzas** donde viven avisos como "deuda con proveedores" (guía 2026-06-06, línea de la campanita).
  Si en el futuro se quiere que los reembolsos vencidos "canten" sin una pantalla nueva, ese es el lugar
  natural para un aviso, reusando un patrón existente. **Queda como idea, fuera del alcance de esta spec.**

---

## Pantallas tocadas (resumen para frontend-senior)

- **`RegistrarReembolsoRecibidoInline.jsx`** — agregar el desglose "pagado − multa = te devuelven" en el renglón (y el estimado del elegido), y el texto explicativo cuando da $0. Todo enmascarado sin `cobranzas.see_cost`.
- **`OperatorRefundsPendingSection.jsx`** (solapa Reembolsos de la ficha) — mostrar el mismo desglose por caso + el aviso "Falta confirmar la multa" con link a la reserva (según P2).
- **`ServiceList.jsx`** (solapa Servicios de la reserva) — chip "Operador: X" clickeable (desktop y mobile), gateado por permiso de ver proveedores.
- **Navegación** (`Sidebar.jsx` + `App.jsx`) — sacar la entrada "Reembolsos operador" del menú; la ruta global `/operator-refunds` se elimina o se reubica según P1. La solapa "Reembolsos" de la ficha queda igual.
- **Circuito de cancelación** (Cuenta corriente) — ya especificado por la guía 2026-07-01 P4; la línea "Multa retenida por el operador" es la vista de solo lectura de la multa (base de la decisión 2).

---

## Decisiones tomadas por Gastón (2026-07-03) — copy exacto

- **P1=C** — "Reembolsos operador" sale del menú, **sin vista global** (ver sección D: trade-off + mitigación anotada).
- **P2=A** — Solapa "Reembolsos" del operador: en los casos con **multa sin confirmar**, aviso **"Falta confirmar la multa de esta anulación."** + botón **"Ir a la reserva a confirmar"**. La confirmación fiscal se hace **solo en la reserva** (un solo lugar).
- **P3=A** — Panel "Registrar reembolso recibido": en el **renglón elegido**, la cuenta completa. Copy: **"Pagaste US$ 500 − Multa del operador US$ 100 = te devuelven US$ 400 (estimado)."** (con los montos y la moneda reales del caso).
- **P4=A** — Reembolso $0: en lugar del "US$ 0", el motivo en criollo según el caso:
  - Sin pagos al operador: **"Todavía no le pagaste nada al operador por este viaje."**
  - La multa cubre todo: **"No hay nada para devolver: la multa del operador se quedó con todo lo que le pagaste."**
  - (Si el backend informa "ya reembolsado por completo": **"Ya te devolvió todo por este viaje."**)
- **P5=A** — Chip **"Operador: Despegar"** discreto, **debajo del nombre** del servicio; link a la ficha del operador si el usuario puede ver proveedores, texto plano si no.
- **P6=A** — La **multa** y **"lo pagado al operador"** se **tapan igual que todo costo** (ve "—" + aviso único) para quien no tiene `cobranzas.see_cost`.

---

## Dependencias técnicas (NO son decisiones de UX — para backend/frontend)

- **Decisiones 1, 3-P3 y 4 necesitan campos NUEVOS en el DTO de reembolso pendiente.** Hoy `OperatorRefundEstimatedAmountDto` (en `OperatorRefundPendingDtos.cs`) solo trae `Currency` + `EstimatedAmount` (= pagado − multa). Para las pantallas de arriba el backend debe agregar, **por caso (cancelación+operador) y por moneda**:
  1. **`PaidToOperator`** (decimal) — lo que la agencia le pagó al operador por ese viaje. Es lo que se muestra como "Pagaste US$ 500".
  2. **`PenaltyRetained`** (decimal) — la **multa** que el operador retuvo (la multa confirmada). Es lo que se muestra como "− Multa del operador US$ 100".
  3. **`ZeroRefundReason`** (enum/discriminador) — solo cuando `EstimatedAmount == 0`, para elegir el texto del P4. Valores mínimos: `NoPaymentToOperator` (no le pagaste nada), `PenaltyCoversAll` (la multa se quedó con todo), `AlreadyRefunded` (ya te devolvió todo). El front NO deduce el motivo restando montos; lo dice el backend.
  4. **Todos enmascarados por `cobranzas.see_cost`:** sin permiso, estos tres van en 0 y `AmountsMasked = true` (igual que `EstimatedAmount` hoy). El front muestra "—" y el aviso único.
  - Invariante para que la cuenta cierre en pantalla: **`EstimatedAmount == PaidToOperator − PenaltyRetained`** (antes de deducciones finales del operador). Si el backend no lo garantiza, la cuenta "Pagaste − Multa = te devuelven" no cuadra visualmente.
- **Reconciliación (ya registrada en el gate anterior):** el recuadro **"Me tiene que devolver"** (naranja) y el **total de la solapa "Reembolsos"** deben dar la MISMA cifra por moneda; hoy salen de dos cálculos distintos (`SupplierCancellationCircuitReader.TheyOweMe` vs `OperatorRefundReadModelService`). Conciliar es tarea de backend; no se garantiza desde el front.
- **Decisión 2 (puente a confirmar la multa):** la solapa Reembolsos necesita saber, por caso, si **la multa está pendiente de confirmar**. El backend debe exponer un flag tipo **`PenaltyPendingConfirmation`** (bool) + el `ReservaPublicId` (ya viene) para armar el link **"Ir a la reserva a confirmar"** (`/reservas/{reservaPublicId}`, a la acción de confirmar multa que ya existe en la reserva). Ninguna acción fiscal nueva del lado del operador.
- **Decisión 3 (chip Operador):** el renglón del servicio ya tiene `svc.supplierName` y `svc.supplierPublicId`; el link va a `/suppliers/{supplierPublicId}/account`, visible **solo con el permiso de ver proveedores** — confirmar con frontend cuál es el gate exacto del acceso a la sección Proveedores (la ruta `/suppliers/:publicId/account` hoy no tiene guard propio; usar el mismo criterio que muestra/oculta la entrada "Proveedores" del menú). Aplica en desktop **y** mobile.
- **Decisión 5 (navegación):** sacar la entrada "Reembolsos operador" del menú (`Sidebar.jsx`) y **eliminar** la ruta global `/operator-refunds` + su página `OperatorRefundsPage.jsx` (P1=C: no se reubica). El componente `OperatorRefundsPendingSection` sigue vivo dentro de la solapa "Reembolsos" de la ficha (`SupplierAccountPage.jsx`), sin cambios de navegación.
