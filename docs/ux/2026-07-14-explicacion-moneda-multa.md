# Por qué la multa va en la moneda de la factura — una línea que lo explique (spec 2026-07-14)

> **Origen (caso real F-2026-1025):** reserva con servicios en **dólares**, pero la factura del
> cliente se emitió en **pesos**. Al llegar al paso de la multa del operador, el sistema le dijo a
> Gastón (en distintos carteles) que la multa no podía ir en dólares y que la pasaba a pesos —
> **pero ninguna pantalla explica el PORQUÉ**. Gastón lo leyó como una incoherencia del sistema
> ("los servicios están en dólares, ¿por qué no puedo hacer la multa en dólares?").
>
> **La regla es correcta y está firmada** (ADR-012 §3.3): la factura del cliente, sus notas de
> crédito y el comprobante de la multa hablan TODOS la misma moneda — la de la factura. Lo único
> que falta es **una línea en pantalla** que traduzca esa regla a criollo, para que cualquier
> agente de viajes entienda que no es un capricho ni un error.
>
> **Esta spec NO reddiseña el flujo** (el flujo de la multa está congelado: 2026-07-08 "el paso de
> multa vive en la ficha", 2026-07-10 T4, 2026-07-13 conversión de moneda). Solo agrega texto
> explicativo. **Nada se construye hasta que Gastón responda las preguntas del final.**

---

## 0. Qué está decidido y qué falta

**Ya resuelto en la guía (no se toca):**
- La moneda de la multa se **precarga de la factura** de la reserva (`operatorPenaltyCurrency.js`).
- El bloque de conversión ya dice **qué** se hace: "↕ La multa está en dólares y la factura en
  pesos: la pasamos a pesos." + "La factura del cliente está en pesos ($)". (guía 2026-07-13)
- **Nunca** se usa "diferencia de cambio" (regla dura 2026-06-09 / guía #2).
- En modo confirmar, bajo la moneda dice: "El cargo al cliente sale en esta misma moneda." (B2, 2026-07-08)

**El hueco (no cubierto por la guía → preguntas para Gastón):**
- Ningún texto dice **por qué** la factura manda la moneda. El usuario ve "la factura está en pesos"
  pero no entiende que ESA es la regla que obliga a la multa a ir en pesos, más allá de que los
  servicios estén en dólares.
- No está decidido **dónde** va esa línea ni **con qué palabras**.

**Tensión que hay que blanquear (regla 2026-06-05):** la guía prohíbe los "cartelitos aclarativos"
("si un campo necesita explicación, el diseño está mal"). Acá se pide, justamente, un cartelito
explicativo. La diferencia: es una **regla fiscal no obvia** que confundió al propio dueño, y él
pidió la línea. Hay que confirmarle que hace la excepción a sabiendas (P0), para que el revisor de
frontend no la rechace por chocar con esa regla.

---

## 1. Recomendación (para validar, no para asumir)

Poner la explicación en **los dos lugares** (candidato 3), pero como **una sola frase corta y
reusada**, que solo aparece cuando hace falta:

1. **Donde de verdad se confundió: el bloque de conversión** del panel "Corregir monto y moneda".
   Es el cartel que le dijo "la pasamos a pesos" sin explicar por qué. La línea nueva va **arriba
   de todo el bloque**, antes de la mecánica de la conversión.
2. **Como prevención: bajo el selector de Moneda** del panel "Confirmar multa del operador" (el que
   se abre al elegir "Sí, el operador cobró la multa"). Ahí la moneda arranca en la de la factura;
   una línea que aclare el porqué evita que el usuario la cambie a dólares y caiga en el viaje de
   ida y vuelta de la traba + corrección.

Ambos lugares dicen **lo mismo**, con la moneda de la factura rellenada por el sistema. Solo aparece
cuando la factura ya existe (si no hay factura, no hay moneda que mande, no hay línea).

Texto recomendado (se da vuelta solo según la moneda de la factura):

> **La factura de esta reserva salió en pesos. Todo lo que se le cobra o se le devuelve al cliente
> va en esa moneda, incluida la multa — aunque el operador la haya cobrado en dólares.**

---

## 2. Ubicación exacta

### 2.a Bloque de conversión — panel "Corregir monto y moneda"
Archivo de referencia: `ConfirmarMultaOperadorInline.jsx`, bloque `{hayCruce && (…)}` (hoy arranca
con `encabezadoBloqueConversion` + `tituloBloqueConversion`). La línea nueva va **primera**, arriba
del encabezado actual.

```
┌─ Corregir el monto y la moneda de la multa ─────────────── Reserva #1025 ─ [✕] ┐
│                                                                                 │
│  Monto que retiene el operador *        Moneda *                                 │
│  ┌───────────────────┐                  ┌──────────────────────┐                │
│  │ 200               │                  │ Dólares (USD)      ▾ │                │
│  └───────────────────┘                  └──────────────────────┘                │
│                                                                                 │
│  ┌─ La factura del cliente está en pesos ($) ─────────────────────────────────┐ │
│  │                                                                            │ │
│  │  ▸ LÍNEA NUEVA (el "por qué"):                                              │ │
│  │    La factura de esta reserva salió en pesos. Todo lo que se le cobra o    │ │
│  │    se le devuelve al cliente va en esa moneda, incluida la multa —         │ │
│  │    aunque el operador la haya cobrado en dólares.                          │ │
│  │                                                                            │ │
│  │  ↕ La multa está en dólares y la factura en pesos: la pasamos a pesos.     │ │  ← ya existe
│  │                                                                            │ │
│  │  Fecha en que el operador cobró la multa *   [ 05/07/2026 ]                 │ │
│  │  Tipo de cambio del día que el operador cobró *  [ 1 US$ = $ 1.200 ]        │ │
│  │  → Se le cobra al cliente   $ 240.000                                       │ │
│  └────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                 │
│  ¿Por qué corregís el monto o la moneda? *  [ …………………………… ]                       │
│                                          [ Volver ]  [ Guardar corrección ]     │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### 2.b Bajo el selector de Moneda — panel "Confirmar multa del operador"
Archivo de referencia: `ConfirmarMultaOperadorInline.jsx`, el `<div>` que hoy dice "El cargo al
cliente sale en esta misma moneda." (debajo del `select` de moneda). La línea del porqué la
reemplaza o la acompaña (ver P3).

```
│  Monto que retiene el operador *        Moneda *                                 │
│  ┌───────────────────┐                  ┌──────────────────────┐                │
│  │ 200               │                  │ Pesos (ARS)        ▾ │  ← precargada    │
│  └───────────────────┘                  └──────────────────────┘                │
│                                          La factura de esta reserva salió en     │
│                                          pesos: el cargo al cliente va en pesos.  │  ← LÍNEA
└─────────────────────────────────────────────────────────────────────────────────┘
```

---

## 3. Texto — variantes (Gastón elige)

Las tres se dan vuelta solas si la factura está en dólares (pesos ↔ dólares).

- **V1 (completa, recomendada para el bloque de conversión):**
  "La factura de esta reserva salió en **pesos**. Todo lo que se le cobra o se le devuelve al
  cliente va en esa moneda, incluida la multa — aunque el operador la haya cobrado en dólares."
- **V2 (corta, la del pedido):**
  "La factura de esta reserva salió en **pesos**, por eso la multa al cliente también va en pesos
  (aunque el operador la haya cobrado en dólares)."
- **V3 (mínima, recomendada para el panel de confirmar):**
  "La factura de esta reserva salió en **pesos**: el cargo al cliente va en pesos."

Reglas de voz respetadas: sin jerga; sin "nota de débito/crédito" a secas; sin "diferencia de
cambio"; sin números internos ni códigos.

---

## 4. Cuándo aparece / cuándo NO

| Situación | Bloque conversión (2.a) | Confirmar (2.b) |
|---|---|---|
| Factura en pesos, multa que el operador cobró en dólares (F-2026-1025) | Sí, línea "por qué" arriba | Sí (moneda precargada pesos) |
| Factura en dólares, multa en pesos | Sí, dada vuelta | Sí, dada vuelta |
| Multa y factura en la MISMA moneda | El bloque de conversión no existe → no hay línea | Ver P3 (¿siempre o solo si difiere?) |
| Reserva sin factura todavía | Sin moneda que mande → sin línea | Sin línea |

- **NO** usar nunca "diferencia de cambio" (ni variantes).
- **NO** convertir esto en un párrafo largo ni sumar más aclaraciones al panel (regla 2026-06-05):
  es UNA línea.
- **NO** mostrar la línea cuando no hay factura de la cual sacar la moneda.
- **NO** tocar los textos ya aprobados del bloque de conversión (encabezado + "↕ … la pasamos a …"):
  la línea del porqué se SUMA arriba, no los reemplaza.

---

## 5. SPEC APROBADA PARA IMPLEMENTAR (Gastón, 2026-07-14 — P0 sí, P1=C, P2 mezcla, P3=B)

Todo lo de abajo está aprobado y se implementa tal cual. Componente único que se toca:
`src/TravelWeb/src/features/cancellations/components/ConfirmarMultaOperadorInline.jsx` (sirve tanto
al modo "confirmar" como al "corregir"). No se crea ninguna ventana ni componente nuevo.

### 5.1 Excepción autorizada (P0)
Estas dos líneas SON cartelitos explicativos y están **autorizadas a sabiendas** por Gastón, como
excepción puntual a la regla anti-cartelitos del 2026-06-05. El `frontend-reviewer` NO las rechaza
por esa regla. Es la única excepción: no habilita más cartelitos en otras pantallas.

### 5.2 Línea 1 — dentro del bloque de conversión ("Corregir monto y moneda")
- **Ubicación exacta:** dentro del bloque `{hayCruce && (…)}`, como **primer elemento**, arriba del
  `encabezadoBloqueConversion` ("La factura del cliente está en pesos ($)") que ya existe. No se
  tocan ni el encabezado ni el título "↕ … la pasamos a …": la línea nueva se SUMA arriba.
- **Cuándo se ve:** siempre que el bloque de conversión se muestre. Ese bloque ya aparece solo
  cuando `hayCruce` es true (modo "corregir" + moneda elegida distinta de la moneda real de la
  factura + la reserva tiene factura). Si no hay cruce, no hay bloque y no hay línea.
- **Texto final (versión COMPLETA, V1):**
  - **Factura en pesos, operador cobró en dólares:**
    "La factura de esta reserva salió en pesos. Todo lo que se le cobra o se le devuelve al cliente
    va en esa moneda, incluida la multa — aunque el operador la haya cobrado en dólares."
  - **Factura en dólares, operador cobró en pesos (espejo):**
    "La factura de esta reserva salió en dólares. Todo lo que se le cobra o se le devuelve al cliente
    va en esa moneda, incluida la multa — aunque el operador la haya cobrado en pesos."

```
┌─ Corregir el monto y la moneda de la multa ─────────────── Reserva #1025 ─ [✕] ┐
│  Monto que retiene el operador *  [ 200 ]     Moneda *  [ Dólares (USD) ▾ ]      │
│                                                                                 │
│  ┌────────────────────────────────────────────────────────────────────────────┐ │
│  │ La factura de esta reserva salió en pesos. Todo lo que se le cobra o se le  │ │  ← LÍNEA 1 (nueva)
│  │ devuelve al cliente va en esa moneda, incluida la multa — aunque el          │ │
│  │ operador la haya cobrado en dólares.                                         │ │
│  │                                                                            │ │
│  │ La factura del cliente está en pesos ($)                                    │ │  ← ya existe
│  │ ↕ La multa está en dólares y la factura en pesos: la pasamos a pesos.       │ │  ← ya existe
│  │ Fecha en que el operador cobró la multa *   [ 05/07/2026 ]                   │ │
│  │ Tipo de cambio del día que el operador cobró *  [ 1 US$ = $ 1.200 ]          │ │
│  │ → Se le cobra al cliente   $ 240.000                                         │ │
│  └────────────────────────────────────────────────────────────────────────────┘ │
│  ¿Por qué corregís el monto o la moneda? *  [ …………………………… ]                       │
│                                          [ Volver ]  [ Guardar corrección ]     │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### 5.3 Línea 2 — bajo el selector de Moneda ("Confirmar multa del operador")
- **Ubicación exacta:** el `<div>` que hoy muestra "El cargo al cliente sale en esta misma moneda.",
  debajo del `<select>` de Moneda. Ese texto pasa a ser **condicional** (ver abajo).
- **Cuándo se ve (P3=B):** SOLO en modo "confirmar", cuando la reserva tiene factura Y la moneda
  elegida en el selector es **distinta** de la moneda real de la factura. Si coincide (el caso
  normal, porque el selector arranca precargado en la moneda de la factura), queda el texto de hoy
  **sin cambio**: "El cargo al cliente sale en esta misma moneda." En modo "corregir" también queda
  el texto de hoy bajo el selector (la explicación completa ya vive en el bloque de conversión;
  no se duplica).
- **Texto final (versión MÍNIMA, V3):**
  - **Factura en pesos:** "La factura de esta reserva salió en pesos: el cargo al cliente va en pesos."
  - **Factura en dólares (espejo):** "La factura de esta reserva salió en dólares: el cargo al cliente va en dólares."

```
Caso normal (moneda coincide con la factura):
   Moneda *  [ Pesos (ARS) ▾ ]   ← precargada
   El cargo al cliente sale en esta misma moneda.               ← texto de HOY, sin cambio

Caso riesgoso (el usuario cambió a una moneda distinta a la de la factura):
   Moneda *  [ Dólares (USD) ▾ ]   ← la cambió
   La factura de esta reserva salió en pesos: el cargo al          ← LÍNEA 2 (nueva), aparece ahora
   cliente va en pesos.
```

### 5.4 Reglas transversales (valen para las dos líneas)
- La moneda de cada texto la rellena el sistema con la **moneda real de la factura** de la reserva
  (nunca contra `monedaSugerida`, que es editable). El espejo pesos↔dólares es automático.
- **Nunca** aparece si la reserva no tiene factura todavía.
- **Nunca** usar "diferencia de cambio" (ni "diferencia cambiaria", "spread", "ajuste FX").
- Nada de números internos, códigos de invariante, enums ni texto crudo de error de ARCA.
- Es UNA línea por lugar. No se agregan más aclaraciones ni se agranda el texto.
- Accesibilidad: son texto informativo asociado a su campo/bloque (no botones ni links).

### 5.5 Trazabilidad de la decisión
Respuestas de Gastón (2026-07-14): **P0** = sí, excepción justificada y puntual · **P1** = C (los dos
lugares) · **P2** = mezcla (COMPLETA/V1 en el bloque de conversión, MÍNIMA/V3 bajo el selector) ·
**P3** = B (bajo el selector, solo cuando la moneda elegida difiere de la de la factura). Reglas
volcadas en `docs/ux/guia-ux-gaston.md`, sección "Explicar POR QUÉ la multa va en la moneda de la
factura (2026-07-14)".
