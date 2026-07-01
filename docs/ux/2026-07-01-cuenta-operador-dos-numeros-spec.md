# Especificación — Cuenta del operador: los dos números + Circuito de cancelación (Fase D)

> **Qué es esto:** especificación de frontend (sin código) para `frontend-senior`, aprobada por Gastón el 2026-07-01.
> Sale SOLO de `docs/ux/guia-ux-gaston.md` (sección "Cuenta del operador — LOS DOS NÚMEROS + Circuito de cancelación").
> **No inventar campos ni endpoints:** todo lo que se usa está verificado en el código y listado abajo. Lo que falta se marca como dependencia.
> Fecha: 2026-07-01. Autor: `ux-ui-disenador`.

---

## 0) Contexto en una línea

La ficha del operador (`SupplierAccountPage.jsx`) ya existe: encabezado (identidad + recuadros de saldo por moneda) + 6 solapas (Cuenta corriente · Deuda por reserva · Servicios comprados · Reembolsos · Datos bancarios · Datos). El backend ya calcula, por operador y moneda, los datos nuevos; **la pantalla todavía no los muestra**. Esta spec define exactamente qué cambia.

---

## 1) Reglas duras de exposición (valen para TODO lo de abajo, no negociables)

1. **Multimoneda separada:** pesos y dólares SIEMPRE en bloques/recuadros distintos. **Nunca sumar ni convertir ARS + USD** en un solo número.
2. **Etiquetas en español, tal cual las manda el backend.** Las líneas del circuito ya vienen con `description` = "Multa retenida por el operador" / "Reembolso recibido del operador". Usar ese texto; **no derivar textos de `kind` ni de enums.**
3. **Nunca códigos internos ni identificadores técnicos en pantalla.** No mostrar `sourcePublicId`, GUIDs, ni valores de enum. El número de reserva visible (`documentRef` / `numeroReserva`) sí se muestra.
4. **Enmascarado sin `cobranzas.see_cost`:** cuando `amountsVisible === false` (o el permiso no está), TODOS los montos van "—" en gris, **nunca en color** (ni rojo, ni naranja, ni verde). No revelar si hay deuda, receivable o saldo a favor.
5. **Todo EN LÍNEA:** ninguna ventana flotante. Fichas y formularios se abren debajo, dentro de la página.

---

## 2) Encabezado: TRES recuadros por moneda (reemplaza `BalanceHeaderChips`)

### Qué se ve

```
DESPEGAR ARGENTINA S.A.        CUIT 30-12345678-9 · Resp. Inscripto
Operador

PESOS ($)
┌ Le debo ─────────┐  ┌ Me tiene que devolver ┐  ┌ Saldo a favor ─────┐
│  $ 1.250.000      │  │  $ 0                  │  │  $ 0               │
│  (rojo)           │  │  (naranja)            │  │  (verde)           │
└───────────────────┘  └───────────────────────┘  └────────────────────┘

DÓLARES (US$)
┌ Le debo ─────────┐  ┌ Me tiene que devolver ┐  ┌ Saldo a favor ─────┐
│  US$ 3.400        │  │  US$ 200              │  │  US$ 0             │
└───────────────────┘  └───────────────────────┘  └────────────────────┘
```

### Reglas
- **Un juego de tres recuadros por cada moneda presente.** Rótulo de moneda arriba del juego ("PESOS ($)" / "DÓLARES (US$)"). Orden: pesos primero, dólares después.
- **Colores fijos y distintos:**
  - "Le debo" → **rojo** (la agencia le debe pagar). 0 = gris neutro.
  - "Me tiene que devolver" → **naranja/ámbar** (el operador debe devolver; NO es plata para gastar). 0 = gris neutro.
  - "Saldo a favor" → **verde** (plata a cuenta, gastable). 0 = gris neutro.
  - **Prohibido** usar el mismo verde para "Me tiene que devolver" y "Saldo a favor".
- **CAMBIO DE FUENTE (corrige el bug actual):** hoy los recuadros leen `overview.balancesByCurrency[].balance` (saldo de caja crudo) y pintan "A favor" en verde cuando el saldo es negativo. **Eso ya no.** Los tres montos salen de los campos limpios por moneda del extracto (`GET /suppliers/{id}/account/statement`, ver §5): `iTheyOwe` (Le debo), `theyOweMe` (Me tiene que devolver), `prepayment` (Saldo a favor).
- **Sin permiso `cobranzas.see_cost`:** los tres recuadros en gris con "—". No pintar color. No revelar estado.
- **Qué recuadros mostrar cuando un número es 0:** mostrar los tres recuadros siempre que la moneda esté presente (aunque alguno sea 0), para que el encabezado sea estable y legible. Un 0 se ve en gris.

---

## 3) Solapa "Cuenta corriente": extracto (igual que hoy) + bloque "Circuito de cancelación" (nuevo, colapsable)

El extracto de caja (compras/pagos con Cargo/Abono/Saldo corriente) **queda tal cual** (`SupplierExtractoSection` → `bloque.lines` + `closingBalance`). Debajo del extracto de cada moneda se agrega el bloque nuevo.

### Qué se ve

```
── Circuito de cancelación (Pesos $) ─────────────────  [ ▸ Mostrar ]

  (al abrir:)
  Fecha    Movimiento                          Reserva     Monto
  15/06    Multa retenida por el operador      R-1050      $ 80.000
  20/06    Reembolso recibido del operador     R-1050      $ 270.000
```

### Reglas
- **Un bloque por moneda**, ubicado debajo de la tabla de ese mismo bloque de extracto.
- **Arranca CERRADO.** Se abre con un click ("Mostrar" / "Ocultar"). Es un panel colapsable en la misma página (no ventana).
- **Aparece SOLO si esa moneda tiene líneas de circuito** (`bloque.circuitLines.length > 0`). Si el operador no tuvo anulaciones en esa moneda, el bloque **no se renderiza** (nada de bloque vacío, nada de cartel — decisión P5).
- **Columnas:** Fecha (`date`) · Movimiento (`description`, en español, tal cual) · Reserva (`documentRef`, el número visible; si es null → "—") · Monto.
  - El monto de cada línea: usar `charge` si es la multa/cargo y `credit` si es abono, o el campo de monto que traiga la línea (ver §5, nota de mapeo). Mostrar como cifra positiva con su símbolo de moneda.
- **Enmascarado:** sin `cobranzas.see_cost`, la columna Monto va "—" en gris; el resto (fecha, texto, reserva) se sigue viendo (es estructura, no plata).
- **No mostrar** ningún identificador técnico de la línea.

---

## 4) Botón "Registrar reembolso recibido" en Cuenta corriente (P6=B)

### Qué se ve
Al lado de los botones que ya existen en la solapa Cuenta corriente:

```
[ Registrar pago ]   [ Usar saldo a favor ]   [ Registrar reembolso recibido ]
```

Al tocar "Registrar reembolso recibido" se abre, **en línea debajo** (nunca ventana), una ficha que:
1. **Obliga a elegir a qué reembolso pendiente se imputa.** Muestra la lista de reembolsos pendientes de ESTE operador (una fila por anulación esperando reembolso: número de reserva + cliente + moneda + monto estimado). El usuario elige UNO. **No se permite un monto suelto sin destino.**
2. Pide **monto recibido · moneda (viene fijada por el pendiente elegido) · fecha · método** (y referencia opcional).
3. Botón "Confirmar" guarda; "Cancelar" cierra sin guardar.

### Estados obligatorios de la ficha (frontend-standards)
- **Cargando** la lista de pendientes.
- **Vacío:** si el operador no tiene reembolsos pendientes, la ficha no ofrece nada que imputar → mostrar "No hay reembolsos pendientes de este operador" y no dejar continuar (o esconder el botón; ver nota de permiso).
- **Validación:** monto > 0 y menor o igual al pendiente elegido; fecha requerida; reembolso pendiente elegido requerido. Mensajes claros, en criollo.
- **Éxito:** cerrar la ficha, refrescar el extracto, los recuadros de arriba, y la solapa "Reembolsos" (todos consistentes). Toast de éxito.
- **Error de servidor:** mostrar el mensaje seguro (`getApiErrorMessage`), preservar lo cargado, permitir reintentar en el mismo botón.
- **Sin permiso:** el botón solo se muestra si el usuario tiene el permiso de operar caja (`caja.edit`, ver §5). Sin permiso, no aparece.

### Consistencia con la solapa Reembolsos
- La solapa "Reembolsos" sigue siendo el **detalle/estado** (semáforo, vencimientos, reembolso tardío). La **acción de "recibí la plata"** se dispara desde Cuenta corriente.
- Registrar desde Cuenta corriente debe **actualizar la solapa Reembolsos** (el pendiente imputado baja/desaparece) y el recuadro "Me tiene que devolver" (baja por ese monto). Recargar ambos al confirmar.

---

## 5) Backend: qué existe (verificado) y qué es dependencia

### Ya existe y se consume tal cual
- **Extracto del operador con los campos nuevos:** `GET /suppliers/{publicId}/account/statement` → `SupplierAccountStatementDto`. Por cada bloque de moneda (`currencies[]` = `SupplierAccountStatementCurrencyBlockDto`) ya vienen:
  - `iTheyOwe` → recuadro **"Le debo" (X)**.
  - `theyOweMe` → recuadro **"Me tiene que devolver" (Y)**.
  - `prepayment` → recuadro **"Saldo a favor"**.
  - `circuitLines[]` (`SupplierAccountStatementLineDto`) → bloque **"Circuito de cancelación"**. Cada línea trae `date`, `description` (español), `documentRef` (nº de reserva), `currency`, y su monto (`charge`/`credit`; ver nota de mapeo).
  - `lines[]` + `closingBalance` → el extracto de caja de siempre (sin cambios).
  - `amountsVisible` (a nivel DTO) → enmascarado.
- **Reembolsos pendientes del operador (para el selector del §4):** `GET /suppliers/{publicId}/operator-refunds/pending` → `OperatorRefundPendingItemDto[]`. Cada item: `bookingCancellationPublicId`, `reservaPublicId`, `numeroReserva`, `clienteNombre`, `semaphore`, `operatorRefundDueBy`, `estimatedRefundsByCurrency[]` (con `currency` + `estimatedAmount`), `amountsMasked`. Permiso: `tesoreria.supplier_payments` para listar.
- **Registrar el reembolso recibido (los dos pasos que hoy existen):**
  - Paso 1 — `POST /operator-refunds` (`RecordOperatorRefundRequest`: `supplierPublicId`, `receivedAmount`, `currency`, `receivedAt`, `method?`, `reference?`). Permiso `caja.edit`. Crea el ingreso físico.
  - Paso 2 — `POST /operator-refunds/{publicId}/allocations` (`AllocateRefundRequest`: `bookingCancellationPublicId`, `grossAmount`, `deductions[]`). Permiso `caja.edit`. Imputa el ingreso a la cancelación elegida y genera el saldo a favor del cliente.
  - Ambos gateados por el flag `EnableNewCancellationFlow` (si está off, el backend rechaza con mensaje claro, no 500).

### ⚠️ Dependencias / riesgos que NO puede resolver el frontend (subir a backend/dominio ANTES de construir el §4)

1. **"Registrar reembolso recibido" es un flujo de DOS pasos con deducciones fiscales.** El botón simple que pidió Gastón mapea a `RecordReceived` + `Allocate`, y `Allocate` exige `deductions[]` (líneas tipificadas: retenciones AR, impuesto extranjero, etc.) y valida una matriz fiscal agencia-operador. **Decisión pendiente de backend/dominio (NO de UX, NO inventar):** para el camino simple, ¿se imputa el total recibido **sin deducciones** (todo va a saldo a favor del cliente) y las deducciones quedan para un flujo aparte del contador? Recomendación (a confirmar con `travel-agency-accountant-argentina` / `backend-dotnet-senior`): sí, camino simple = `grossAmount` = monto recibido, `deductions = []`; si hay deducciones, se hacen desde el flujo de caja/cancelación existente. **Lo ideal sería un endpoint de conveniencia que haga los dos pasos en una llamada dado un `bookingCancellationPublicId` + monto + fecha + método**, para no orquestar dos POST desde el front; evaluarlo con backend. Hasta que esto se cierre, el §4 NO se construye.

2. **Conciliación "Me tiene que devolver" (recuadro) vs total de la solapa "Reembolsos".** Gastón pidió que den el mismo número por moneda y no puedan divergir. Hoy salen de **dos cálculos distintos**: el recuadro Y viene de `SupplierAccountStatementCurrencyBlockDto.theyOweMe` (derivado por `SupplierCancellationCircuitReader`: `RefundCap − ReceivedRefundAmount` por línea de servicio cancelado, incluye residuos de BC cerradas sub-reembolsadas); la solapa suma `estimatedRefundsByCurrency[].estimatedAmount` (montos "estimados sujetos a deducciones", del read-model de pendientes, que lista estados AwaitingOperatorRefund/Abandoned). **Pueden no coincidir.** Esto es una **tarea de backend** (que la solapa y el recuadro salgan del MISMO cálculo, o que backend concilie), NO garantizable desde el front. Recomendación: alinear ambos a `theyOweMe` (la misma fuente que ya no mintea plata). Subir a `backend-dotnet-senior` para verificar/conciliar antes de dar por cerrada la coherencia.

### Nota de mapeo del monto de las líneas del circuito
`SupplierAccountStatementLineDto` reutiliza `charge` / `credit` / `runningBalance`. Para las `circuitLines`, frontend-senior debe confirmar con backend cuál de los dos campos trae el monto de cada `kind` ("PenaltyRetained" / "RefundReceived") y mostrarlo como cifra positiva. No asumir; verificar el poblado real en `SupplierService` antes de renderizar.

---

## 6) Estados de pantalla (frontend-standards) — checklist
- **Cargando:** encabezado y extracto ya tienen skeleton/loader; mantener.
- **Vacío:** operador sin movimientos → extracto vacío como hoy; sin anulaciones → sin bloque circuito y "Me tiene que devolver $0" gris (P5).
- **Error de carga:** reintento como hoy.
- **Sin permiso de costos:** todos los montos "—" gris, sin color (regla dura 4).
- **Sin permiso de caja (`caja.edit`):** el botón "Registrar reembolso recibido" no aparece.
- **Éxito de registro:** refresca extracto + recuadros + solapa Reembolsos.

---

## 7) Qué NO hacer
- No sumar ni convertir pesos y dólares en ningún número.
- No pintar en color ningún monto sin `cobranzas.see_cost`.
- No usar el mismo verde para "Me tiene que devolver" y "Saldo a favor".
- No seguir derivando "A favor" del saldo de caja negativo: leer `iTheyOwe`/`theyOweMe`/`prepayment`.
- No mostrar `sourcePublicId`, GUIDs, ni valores de enum; las etiquetas del circuito salen de `description` (español).
- No inventar un endpoint de "registrar reembolso": usar los reales (§5) y **frenar el §4 hasta que backend/dominio cierren las dos dependencias**.
- No usar ventanas flotantes: todo en línea.
- No construir nada del §4 sin resolver antes las dependencias fiscales/conciliación de §5.
