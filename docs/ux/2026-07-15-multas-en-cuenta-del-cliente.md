# Las multas por anulación se ven en la cuenta del cliente (2026-07-15)

> **Qué resuelve esto:** hoy, cuando a un cliente se le cobra una multa por anular un viaje,
> esa plata NO aparece por ningún lado en su cuenta corriente. La cuenta del cliente
> (`CustomerAccountPage`) esconde las reservas anuladas, y la multa solo vive en una reserva
> anulada → nunca se ve. Gastón lo reportó: "las multas a cobrar al cliente no aparecen".
>
> **Fuente de las decisiones:** decisiones cerradas por Gastón el 2026-07-15 (abajo) + la guía
> (`docs/ux/guia-ux-gaston.md`) para todo lo ya decidido (multimoneda, voz sin jerga, el molde
> del circuito de la cuenta del operador). Lo que la guía NO cubre está al final, en
> **PREGUNTAS PARA GASTON** (son 2).
>
> **NO reabrir:** el paso de la multa sigue viviendo en la ficha (2026-07-08); las bandejas son
> listas pasivas; la multa siempre se traslada 1:1 al cliente; la multa habla la moneda de la
> factura (ADR-012 §3.3, explicado 2026-07-14). Esta pantalla solo MUESTRA lo que ya existe;
> no cambia ninguna regla de negocio ni fiscal.

---

## Decisiones que ya trajo Gastón (2026-07-15) — base de esta spec, cerradas

1. **Una multa CONFIRMADA cuyo comprobante todavía no salió** (en emisión, o en revisión porque
   falló/quedó a mano) **YA cuenta como deuda del cliente**, y se muestra **distinguida** de la
   que ya tiene comprobante emitido.
2. **Sin alertas nuevas.** Se ve en la cuenta del cliente y en la ficha; las bandejas siguen
   siendo listas pasivas. Nada de campanita ni cartel global nuevo.
3. **Patrón aprobado:** un bloque **"Multa pendiente de cobro"** en la cuenta del cliente, que
   junta las multas de TODAS sus reservas anuladas — **espejo del circuito** que ya funciona en
   la cuenta del operador (recuadros por moneda + líneas con chip de anulación,
   `SupplierExtractoSection.jsx`, aprobado 2026-07-01).

---

## Lo que ya está decidido en la guía y se aplica tal cual (no se pregunta)

- **Multimoneda: pesos y dólares SIEMPRE separados, nunca sumados en un solo número**
  (guía, "Listados y tablas" 2026-06-09 + sección Multimoneda). El bloque muestra un total por
  cada moneda; si hay multas en pesos y en dólares, van en dos líneas distintas.
- **Voz sin jerga.** Nunca "ND", "nota de débito", "CAE", "AFIP", "BC". Se dice **"comprobante de
  la multa"** y **"multa por anulación"** (guía, "El término fiscal nunca se muestra" — reglas de
  voz 2026-07-10; y el cartel de la anulada 2026-07-04 "falta decidir la multa del operador").
- **La multa la ve el cliente en la moneda de su factura** (ADR-012 §3.3): el monto de la multa
  ya viene en esa moneda; acá solo se muestra, no se convierte nada.
- **El bloque aparece SOLO si el cliente tiene al menos una multa pendiente.** Si no tiene
  ninguna, el bloque NO existe (esconder complejidad — regla de defaults del producto). Igual
  que el circuito del operador no aparece si el operador no tiene anulaciones (guía 2026-07-01 P5).

---

## Dónde vive el bloque nuevo

Espejo del molde del operador + coherente con los carteles que la cuenta del cliente YA muestra
arriba de las solapas ("A FAVOR en $/US$", "Saldo a favor aplicado a otras reservas"): el bloque
va **debajo del encabezado (identidad + chips "Debe en $/US$") y arriba de las solapas**, en el
mismo carril donde ya viven esos carteles. No entra en el encabezado de dos columnas (ese carril
es fijo: identidad + tarjetas de resumen), ni dentro de ninguna solapa: es un resumen que cruza
TODAS las reservas del cliente, como los otros carteles de ese carril.

```
┌─ Cuenta Corriente · Fam. García ─────────────────────────────────┐
│  [avatar]  Fam. García   mail · tel · CUIT                        │
│            Ventas $… /US$…   Cobrado …   Reservas 7   Facturas 3  │   ← encabezado (no cambia)
│                                            ┌───────────────────┐  │
│                                            │ Debe en $  120.000 │  │   ← chips "Debe" (no cambian)
│                                            └───────────────────┘  │
├──────────────────────────────────────────────────────────────────┤
│  ┌── ⚠ MULTA PENDIENTE DE COBRO ───────────────────────────────┐ │   ← BLOQUE NUEVO
│  │  En $         45.000        │   En US$        200            │ │   (uno por moneda, separados)
│  │                             │   · 150 sin comprobante todavía│ │
│  │ ─────────────────────────────────────────────────────────── │ │
│  │  R-1042 · Cancún           $ 45.000   Pendiente de cobro  ›  │ │
│  │  R-1051 · Miami           US$ 50      Pendiente de cobro  ›  │ │
│  │  R-1067 · Punta Cana      US$ 150     Comprobante en camino › │ │
│  └─────────────────────────────────────────────────────────────┘ │
├──────────────────────────────────────────────────────────────────┤
│  [ A FAVOR en $ · Usar saldo a favor ]   (cartel existente)       │
├──────────────────────────────────────────────────────────────────┤
│  Reservas │ **Estado de cuenta** │ Facturación │ Datos bancarios  │   ← solapas (no cambian)
└──────────────────────────────────────────────────────────────────┘
```

---

## Qué muestra el bloque

### 1) Cabecera con el total por moneda (recuadros, separados)

- **Un recuadro por moneda que tenga multas**, nunca sumando pesos + dólares.
- Dentro de cada recuadro, el número grande es la **suma de las multas que YA tienen comprobante
  emitido** (deuda firme, cobrable), en **rojo** (mismo criterio que el chip "Debe" del cliente).
- **Si además hay multas sin comprobante todavía** (en emisión o en revisión), debajo del número
  grande va una **segunda línea en ámbar**: `· 150 sin comprobante todavía`. Así queda cumplida
  la decisión 1 (distinguida) sin mezclar en un mismo número la plata firme con la que todavía no
  tiene papel.
- Título del bloque: **"Multa pendiente de cobro"** con un ícono de atención (⚠). Rótulo de cada
  recuadro: **"En $"** / **"En US$"**.

### 2) Lista de multas, una fila por reserva anulada con multa

Cada fila (mismo molde que las líneas del extracto del operador: descripción + monto + chip +
link):

| Parte | Contenido | Notas |
|---|---|---|
| Reserva | `R-1042 · Cancún` (número + nombre de la reserva) | El número es un **link a la ficha** de esa reserva anulada (ahí vive el paso de la multa). |
| Monto | `$ 45.000` / `US$ 150` | En la moneda de la multa (= moneda de la factura). Formato de siempre. |
| Estado (chip) | ver tabla de abajo | Es lo que distingue una de otra (decisión 1). |
| `›` | va a la ficha | Toda la fila es clickeable, como en las bandejas pasivas. |

**Los tres estados en criollo (chip por fila):**

| Estado real (interno) | Chip que ve el usuario | Color | Qué significa |
|---|---|---|---|
| Multa con comprobante emitido (`MultaPorCobrar`) | **Pendiente de cobro** | rojo | El comprobante de la multa ya salió; falta que el cliente la pague. Es deuda firme. |
| Multa confirmada, comprobante emitiéndose | **Comprobante en camino** | ámbar | Se confirmó la multa y el comprobante se está emitiendo. Ya cuenta como deuda, pero todavía sin papel. |
| Multa confirmada, comprobante trabado/a revisar (`MultaEnRevision`) | **En revisión** | ámbar | El comprobante quedó trabado o a revisión. Ya cuenta como deuda; se resuelve desde la ficha. |

- El chip **nunca** muestra el texto crudo del error ni términos fiscales: solo estas tres voces.
- La fila **linkea a la ficha** de la reserva (donde el back-office resuelve el comprobante trabado
  con el panel que ya existe, 2026-07-08). El bloque **no** trae botones de acción propios
  (es una lista pasiva, igual que las bandejas).

---

## Estados de la pantalla (el bloque)

- **Vacío (lo normal):** el cliente no tiene ninguna multa pendiente → **el bloque no aparece**.
  No se muestra "no hay multas": simplemente no está.
- **Cargando:** mientras carga el resumen del cliente, el bloque no parpadea con números falsos;
  aparece recién cuando el dato llegó (mismo criterio que las tarjetas "Ventas/Cobrado" del
  encabezado, que muestran "…" hasta tener el dato).
- **Con multas:** se ve el bloque descrito arriba.
- **Error al cargar:** si el resumen del cliente falla, ya hay manejo de error a nivel de página
  (cartel "No se pudo cargar la cuenta corriente"); el bloque simplemente no se dibuja hasta que
  haya dato. No se inventa un estado de error propio para el bloque.

---

## La solapa "Reservas" del cliente (punto 2 del pedido)

La solapa Reservas ya tiene la lógica lista para pintar la fila de una anulada con multa
(`ContextoAnuladaCuenta` en `CustomerAccountPage.jsx`), pero hoy el servidor no le manda el dato
(el DTO `CustomerAccountReservaListItemDto` no trae `cancelledMoneyContext` — comentado en el
código). Cuando el servidor empiece a mandarlo:

- La columna **Saldo** de una reserva anulada con multa muestra el **monto de la multa** con la
  etiqueta **"multa por anulación"** en ámbar (ya está así en el código, no se toca la voz).
- **Ajuste de voz recomendado:** alinear la etiqueta con los chips del bloque nuevo, para que
  digan lo mismo en los dos lados:
  - multa con comprobante emitido → **"multa por anulación · pendiente de cobro"**;
  - multa sin comprobante todavía → **"multa por anulación · sin comprobante todavía"** (ámbar).
- **Coherencia técnica (nota para frontend-senior):** esta solapa es de la cuenta del cliente
  (no es el listado de reservas del vendedor). Acá SÍ se muestran las multas sin comprobante
  todavía (decisión 1). **Pero `getMoneyStatus`/`moneyStatus.js` NO se cambia globalmente:** en la
  ficha y en el listado del vendedor, `MultaEnRevision` tiene que seguir oculto (una promesa de
  cobro sin papel no se le muestra al vendedor — regla vigente en `moneyStatus.js`). La cuenta del
  cliente lee su propio dato (el que alimenta el bloque nuevo), no cambia la función compartida.

---

## El extracto global (solapa "Estado de cuenta") — RESUELTO (P1)

**Gastón respondió (2026-07-15): la multa vive SOLO en el bloque nuevo; el extracto NO cambia.**
El extracto (`EstadoCuentaClienteTab` / `EstadoCuentaExtracto`) sigue mostrando solo reservas
vivas, y su saldo de cierre sigue reconciliando 1:1 con el chip "Debe" del encabezado (decidido
2026-06-22). **No se agrega ningún renglón de multa al extracto.**

---

## Qué NO hay que hacer

- **No** sumar pesos + dólares en un total combinado del bloque.
- **No** poner botones de acción en el bloque ni en las filas: es una lista pasiva; la acción vive
  en la ficha (link).
- **No** mostrar términos fiscales ("nota de débito", "CAE", "AFIP", "ND") en ningún chip ni texto.
- **No** cambiar `moneyStatus.js` para mostrarle al vendedor las multas sin comprobante: ese
  cambio es SOLO de la cuenta del cliente.
- **No** mostrar el bloque cuando el cliente no tiene ninguna multa pendiente.
- **No** inventar un estado de error propio del bloque.

---

## Dependencia de backend (no se diseña acá, pero el front lo necesita)

- Un dato **por cliente y por moneda** con las multas pendientes de cobro de TODAS sus reservas
  anuladas: por cada una, el número de reserva + nombre, el link a la ficha, el monto+moneda, y en
  qué estado está el comprobante (emitido / emitiéndose / trabado-a-revisar). Hoy ese dato no
  llega a esta pantalla; el bloque no puede dibujarse sin él.
- Que la solapa Reservas empiece a recibir `cancelledMoneyContext` (+ monto y moneda de la multa)
  en su DTO, para que `ContextoAnuladaCuenta` deje de estar "listo pero mudo".

---

## Respuestas de Gastón (2026-07-15) — las dos preguntas quedaron cerradas

- **P1 → la multa vive SOLO en el bloque nuevo.** El "Estado de cuenta" (extracto) NO cambia: se
  preserva la coincidencia saldo↔"Debe" de junio. No se agrega renglón de multa al extracto.
- **P2 → las multas confirmadas sin comprobante las ven TODOS** los que abran la cuenta del
  cliente, cada una con su cartelito ("Comprobante en camino" / "En revisión"). No hay gating por
  permiso en este bloque.

---

# SPEC APROBADA PARA IMPLEMENTAR

> Todo lo de arriba está aprobado por Gastón. Frontend-senior implementa exactamente esto; cualquier
> desvío por costo técnico o regla de negocio se le repregunta a Gastón ANTES, nunca se decide solo.
> Después de implementar: gate obligatorio `data-exposure-reviewer` (que ningún término interno/
> fiscal ni GUID llegue al usuario).

## Contrato del backend (ya construido)

La cuenta del cliente expone `pendingPenalties`:

```
pendingPenalties: {
  items: [
    {
      reservaPublicId,      // GUID interno — SOLO para el link/key, NUNCA se muestra como texto
      numeroReserva,        // "R-1042" — texto visible de la reserva
      name,                 // "Cancún" — nombre de la reserva
      amount,               // monto de la multa (en su moneda)
      currency,             // "ARS" | "USD"
      status                // "pendingCollection" | "issuing" | "underReview"
    }
  ],
  totalsByCurrency: [
    {
      currency,             // "ARS" | "USD"
      firmAmount,           // suma de las multas con comprobante emitido (status pendingCollection)
      notYetIssuedAmount    // suma de las multas sin comprobante todavía (issuing + underReview)
    }
  ]
}
```

Regla de dibujo: **el front NO recalcula montos ni suma monedas**; usa `totalsByCurrency` para los
recuadros y `items` para las filas. Si `pendingPenalties` no viene o `items` está vacío → el bloque
no se dibuja.

## 1) Ubicación (inequívoca)

Bloque full-width en el carril de arriba de `CustomerAccountPage`, **entre el encabezado (identidad
+ tarjetas de resumen + chips "Debe en $/US$") y la barra de solapas**, en el mismo carril donde ya
viven los carteles "A FAVOR en $/US$" y "Saldo a favor aplicado a otras reservas". Orden sugerido de
ese carril, de arriba hacia abajo: **(1) Multa pendiente de cobro** → (2) A FAVOR por moneda →
(3) Saldo a favor aplicado. (La multa primero: es deuda del cliente, lo más "urgente" del carril.)

NO va dentro del encabezado de dos columnas ni dentro de ninguna solapa.

## 2) Cabecera del bloque: recuadros por moneda (de `totalsByCurrency`)

- Título del bloque: **"Multa pendiente de cobro"** con ícono de atención (⚠, `AlertTriangle`).
- **Un recuadro por cada entrada de `totalsByCurrency`.** Nunca sumar ARS + USD.
- Número grande = `firmAmount`, en **rojo** (deuda firme, cobrable). Rótulo del recuadro:
  **"En $"** (ARS) / **"En US$"** (USD).
- **Si `notYetIssuedAmount > 0`**, debajo del número grande, una segunda línea en **ámbar**:
  **"· {monto} sin comprobante todavía"** (ej. `· US$ 150 sin comprobante todavía`).
- Si en una moneda `firmAmount = 0` pero `notYetIssuedAmount > 0`, el número grande muestra ese
  total en ámbar con el mismo rótulo "sin comprobante todavía" (no se muestra un `$ 0` rojo
  engañoso).

```
┌─ ⚠ Multa pendiente de cobro ─────────────────────────────────┐
│   En $                        En US$                          │
│   $ 45.000  (rojo)            US$ 50  (rojo)                   │
│                              · US$ 150 sin comprobante todavía │  (ámbar)
└───────────────────────────────────────────────────────────────┘
```

## 3) Lista de filas (de `items`), una por reserva anulada con multa

Cada fila, orden de columnas: **`numeroReserva · name`** | **monto+moneda** | **chip de estado** | `›`.
Toda la fila es un **link a `/reservas/{reservaPublicId}`** (donde vive el paso de la multa). Sin
botones de acción propios (lista pasiva).

Textos EXACTOS del chip según `status`:

| `status` (backend) | Chip visible | Color |
|---|---|---|
| `pendingCollection` | **Pendiente de cobro** | rojo (rose) |
| `issuing` | **Comprobante en camino** | ámbar (amber) |
| `underReview` | **En revisión** | ámbar (amber) |

- El chip **nunca** muestra el `status` crudo, ni el error de AFIP, ni términos fiscales.
- Monto en la moneda de `currency`, con `formatCurrency` (formato de siempre).

```
R-1042 · Cancún        $ 45.000    ● Pendiente de cobro    ›
R-1051 · Miami         US$ 50      ● Pendiente de cobro    ›
R-1067 · Punta Cana    US$ 150     ● Comprobante en camino ›
R-1088 · Bariloche     $ 0 …       ● En revisión           ›
```

## 4) Condiciones de visibilidad (inequívocas)

- `pendingPenalties` ausente / `items` vacío → **el bloque no se dibuja** (ni título, ni "no hay
  multas"). Igual que el circuito del operador cuando no tiene anulaciones.
- Un recuadro de moneda solo aparece si esa moneda está en `totalsByCurrency`.
- La segunda línea ámbar solo aparece si `notYetIssuedAmount > 0`.
- **Todos** los que abran la cuenta del cliente ven el bloque completo, con las tres voces
  (P2 = todos, sin gating por permiso).
- Estados de carga/error: el bloque aparece recién cuando el dato llegó (no parpadea con números
  falsos); no tiene estado de error propio — si el resumen del cliente falla, ya hay manejo a nivel
  de página.

## 5) Solapa "Reservas"

Cuando el DTO de la solapa (`CustomerAccountReservaListItemDto`) empiece a traer el contexto de
multa, `ContextoAnuladaCuenta` pinta la columna Saldo de una anulada con multa así:

- multa con comprobante emitido → **"multa por anulación · pendiente de cobro"** (ámbar);
- multa sin comprobante todavía → **"multa por anulación · sin comprobante todavía"** (ámbar).

## 6) Qué NO hacer (recordatorio para el implementador)

- **NO** tocar el extracto "Estado de cuenta": ni renglón de multa, ni cambio de saldo (P1).
- **NO** cambiar `moneyStatus.js` global: `MultaEnRevision`/`underReview` sigue oculto para el
  vendedor en la ficha y el listado de reservas. La cuenta del cliente lee `pendingPenalties`, no
  `getMoneyStatus`.
- **NO** sumar pesos + dólares en ningún total.
- **NO** poner botones de acción en el bloque ni en las filas (lista pasiva; la acción vive en la
  ficha vía link).
- **NO** mostrar `status` crudo, GUID, ni términos fiscales ("nota de débito", "CAE", "AFIP", "ND").
- **NO** dibujar el bloque cuando no hay multas.
