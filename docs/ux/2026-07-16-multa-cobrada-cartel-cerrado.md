# La multa cobrada se ve "cerrada" — cartel histórico en la ficha (2026-07-16)

> **Obra:** "La multa cobrada se tiene que ver cerrada" (Tanda C del plan de coordinación de anulaciones).
> **Origen:** el cartel de multa confirmada de la ficha (`OperatorPenaltyStepPanel`, familia `confirmada`,
> estado backend `Done`) daba por "terminada" la multa apenas la Nota de Débito obtenía CAE (emitida), y
> NUNCA miraba si el cliente ya la había PAGADO. Resultado: una multa emitida **y cobrada del todo** seguía
> mostrando el cartel verde con sus acciones a la vista para siempre.
> **Decisiones de Gastón:** P1=B, P2=B, P3=B (respondidas 2026-07-16 sobre esta spec).
> **NO reabre** nada de las multas: sigue valiendo todo lo del paso de multa (2026-07-08 "el paso vive en
> la ficha", 2026-07-10 T4, 2026-07-14 config operador + Deshacer emitida) y las 3 reglas duras de
> multimoneda (2026-06-09).

---

## Cuándo aparece el estado "cerrado"

- Estado backend `Done` (multa emitida con CAE) **Y** `IsFullyCollected = true` (el cliente pagó el 100%).
- Si `IsFullyCollected = false` → el cartel sigue **EXACTAMENTE como hoy** (verde, con "+ Agregar otro
  cargo" y "Deshacer" a la vista). Cero cambio para ese caso. Es la condición de "no rompo lo que anda".

## Tratamiento visual

- Fondo **gris neutro** (el registro de "esto ya está, nada que hacer"), no el verde de "recién resuelto".
  Coherente con el patrón existente **Finalizada = gris + ✅** (guía 2026-07-05, P3).
- **✓** al inicio del título.
- Sin naranja ni ámbar: no hay nada trabado ni pendiente.

## Texto exacto (P1 = B)

- **Título:** `✓ Multa del operador cobrada` + el monto a la derecha (ej. `US$ 200`).
- **Línea de detalle (con fecha):** `Se cobró por completo el 12/07.` (fecha en formato dd/MM).
- **Fallback si falta la fecha:** si el backend manda `IsFullyCollected = true` pero NO manda la fecha de
  cobro completado, la línea cae a: `Ya se cobró por completo.` El front **nunca inventa la fecha** (regla
  "el front no deduce, lo dice el backend", 2026-07-03).

## "Agregar otro cargo de este operador" (P2 = B)

- En el estado cerrado, el link **"+ Agregar otro cargo de este operador"** NO va a la vista: queda
  **escondido detrás de un "Más ▾"** que se abre con un clic (en línea, dentro del cartel; nunca ventana
  flotante). Al abrirlo aparece el mismo `AgregarOtroCargoOperadorInline` de siempre.
- Sigue gateado por el permiso de siempre (`cancellations.classify_agency_penalty`). Sin permiso, el "Más
  ▾" no aparece.

## "Deshacer: el operador cobró mal esta multa" (P3 = B)

- **Sigue disponible SIEMPRE**, a perfil bajo (link chico, línea fina arriba, Admin-only), mientras el
  backend lo habilite (`canUndoDebitNote` + Admin, vía `debeMostrarReintentarDeshacer`).
- **NO desaparece a los 15 días.** La regla del 2026-07-14 (Deshacer de multa emitida) **sigue vigente,
  no se deroga:** pasados 15 días desde la emisión, al intentar el Deshacer aparece el aviso suave
  "⚠ Pasaron más de 15 días desde que se emitió este comprobante. Se puede deshacer igual, pero convendría
  consultarlo con un contador antes de seguir." (ese aviso vive dentro del panel `DeshacerMultaEmitidaInline`,
  no en el cartel cerrado). El Deshacer abre ese panel tal cual hoy — no se toca.

## Estados de la pantalla (checklist de flujo)

- **Cargando la ficha:** sin spinner propio (el panel se monta con la reserva ya cargada).
- **Cerrado (Deshacer disponible):** mockup A.
- **Cerrado, sin permiso Admin / sin `canUndoDebitNote`:** el link Deshacer no aparece y nada ocupa su
  lugar (mockup B).
- **Sin permiso `cobranzas.see_cost`:** el monto se muestra `—` (nunca el número). El resto igual.
- **Al tocar "Más ▾":** se despliega en línea el `AgregarOtroCargoOperadorInline`.
- **Al tocar "Deshacer":** abre `DeshacerMultaEmitidaInline` (sin cambios respecto de hoy).

## Mockups definitivos

### A — cerrado, con monto visible y Deshacer disponible
```
┌──────────────────────────────────────────────────────────┐  (fondo gris)
│ ✓ Multa del operador cobrada              US$ 200         │
│   Se cobró por completo el 12/07.                        │
│ ─────────────────────────────────────────────────────────│
│   Más ▾        · Deshacer: el operador cobró mal esta multa (chico)
└──────────────────────────────────────────────────────────┘

  (al tocar "Más ▾")
│   Más ▴                                                   │
│   + Agregar otro cargo de este operador                  │
```

### B — cerrado, sin Deshacer (no Admin o backend no lo habilita)
```
┌──────────────────────────────────────────────────────────┐  (fondo gris)
│ ✓ Multa del operador cobrada              US$ 200         │
│   Se cobró por completo el 12/07.                        │
└──────────────────────────────────────────────────────────┘
```
*(Si tampoco tiene permiso para "Agregar otro cargo", el "Más ▾" tampoco aparece: el cartel queda solo
título + línea de detalle.)*

### C — sin permiso de ver montos
```
┌──────────────────────────────────────────────────────────┐  (fondo gris)
│ ✓ Multa del operador cobrada              —               │
│   Se cobró por completo el 12/07.                        │
│ ─────────────────────────────────────────────────────────│
│   Más ▾        · Deshacer: el operador cobró mal esta multa (chico)
└──────────────────────────────────────────────────────────┘
```

### Sin fecha disponible (fallback P1)
```
│ ✓ Multa del operador cobrada              US$ 200         │
│   Ya se cobró por completo.                              │
```

## Qué NO hacer

- No dejar el fondo verde cuando la multa está cobrada del todo.
- No mostrar "+ Agregar otro cargo" en primer plano en el estado cerrado (va detrás del "Más ▾").
- No hacer desaparecer el "Deshacer" a los 15 días (sigue siempre; el aviso de 15 días vive dentro del
  panel de Deshacer, no acá).
- No inventar la fecha de cobro en el front (si no viene, va el texto "Ya se cobró por completo.").
- No usar ventana flotante para el "Más ▾" ni para el desglose (todo en línea).
- No tocar el chip de estado de la reserva (sigue "Anulada") ni el resto de la ficha (solo lectura).
- No mostrar "CAE"/"diferencia de cambio"/Id/enum/texto de código.

## Dependencia backend (para los implementadores)

- **Nuevo dato requerido:** `IsFullyCollected` (bool) por situación de multa (`operatorPenaltySituations`).
- **Dato deseable para P1=B:** la **fecha en que se completó el cobro** de la multa. Si no está disponible,
  el front usa el fallback "Ya se cobró por completo." (no bloquea la obra, solo degrada el texto).
- Reusa los datos que el cartel `confirmada` ya recibe (`amount`, `currency`, `charges`, `canUndoDebitNote`,
  `lastDebitNoteUndo`). El front no re-deriva nada: obedece lo que manda el backend.

## Archivo de código afectado

- `src/TravelWeb/src/features/cancellations/components/OperatorPenaltyStepPanel.jsx` — bloque
  `if (familia === "confirmada")` (hoy líneas 221-325). Se agrega la rama "cerrado" cuando
  `IsFullyCollected` es true.
