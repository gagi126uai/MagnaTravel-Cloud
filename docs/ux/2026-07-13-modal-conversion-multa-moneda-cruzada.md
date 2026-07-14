# Corregir la multa cuando la moneda no coincide con la factura — spec de pantalla (2026-07-13)

> **Bug que resuelve (F-2026-1033):** cuando la multa del operador se cargó en una moneda
> (ej. US$ 200) distinta de la de la factura del cliente (ej. pesos), el panel "Corregir monto y
> moneda" volvía a trabar la multa con el mismo motivo, una y otra vez — un círculo sin salida.
> Arquitectura ya cerró el diseño de fondo: cuando las monedas no coinciden, el mismo panel
> **guía la conversión** (monto original + fecha que el operador cobró → el sistema sugiere el
> dólar del BNA de ese día → muestra el resultado en la moneda de la factura), y al guardar la
> multa queda ya convertida a la moneda de la factura.
>
> **De dónde sale cada decisión de esta spec:** todo lo marcado `[GUÍA]` ya está respondido por
> Gastón en `docs/ux/guia-ux-gaston.md`; lo marcado `[PATRÓN]` calca un patrón que ya existe en la
> app (recuadro del cobro cruzado); lo marcado `[PREGUNTA]` NO está cubierto y espera respuesta de
> Gastón (bloque al final). **Nada `[PREGUNTA]` se construye hasta que Gastón responda.**

---

## 0. Qué es esta pantalla (y qué NO es)

- Es el panel **EN LÍNEA** (recuadro que se abre dentro de la ficha de la reserva anulada, sin
  ventana flotante) que ya existe hoy: `ConfirmarMultaOperadorInline` en `modo="corregir"`.
  Se lo llama "Corregir monto y moneda" desde el cartel naranja de la ficha. `[GUÍA: "el paso de
  multa vive en la ficha", 2026-07-08; "el modal me parece horrible" → todo en línea]`
- **No es una ventana nueva.** Se reusa el mismo recuadro naranja de hoy; lo único que se le suma
  es el bloque de conversión, y SOLO cuando la moneda no coincide.
- **El caso normal (misma moneda) NO cambia en nada.** Si la multa está en la misma moneda que la
  factura, el panel se ve y funciona EXACTAMENTE como hoy: monto + moneda + "¿por qué corregís?" +
  Guardar. Cero cambio visible. `[GUÍA: "El caso simple NO cambia", 2026-07-10; regla dura
  multimoneda #3: una sola moneda se ve como hoy]`

---

## 1. Cuándo aparece el bloque de conversión

- Aparece **solo cuando la moneda elegida de la multa es distinta de la moneda de la factura del
  cliente.** `[GUÍA 2026-07-10: "Recuadro de tipo de cambio SOLO si el cargo cruza de moneda";
  regla dura multimoneda P4 2026-06-09]`
- La comparación es contra la **moneda real de la factura**, no contra el valor con el que arranca
  el selector. Si el usuario cambia el selector de Moneda y hace que coincida con la factura, el
  bloque desaparece (y vuelve al caso normal). Igual que el recuadro del cobro cruzado, que aparece
  y desaparece según el selector. `[PATRÓN: RegistrarCobroInline]`
- Si la reserva tiene 0 factura todavía, no hay contra qué cruzar → no aparece el bloque
  (se comporta como caso normal).

> **Dependencia para frontend-senior (no es decisión de UX):** el panel necesita recibir la
> **moneda real de la factura** como dato aparte (hoy solo recibe `monedaSugerida`, que es editable
> y no sirve para comparar). Y al guardar en caso cruzado, el `correct-penalty` debe viajar con:
> monto original + su moneda, tipo de cambio, fuente, fecha del operador, y el monto ya convertido a
> la moneda de la factura. Eso es contrato backend, no UX.

---

## 2. Mockup principal — caso moneda cruzada (multa US$, factura en $)

```
┌─ Corregir el monto y la moneda de la multa ─────────────── Reserva #1033 ─ [✕] ┐
│                                                                                 │
│  ¿Qué va a pasar? Vas a corregir el monto o la moneda que quedaron mal          │
│  cargados. Al guardar, el sistema reintenta cobrarle la multa al cliente con    │
│  estos datos corregidos. Esta acción no se puede deshacer.                      │
│                                                                                 │
│  Monto que retiene el operador *        Moneda *                                 │
│  ┌───────────────────┐                  ┌──────────────────────┐                │
│  │ 200               │                  │ Dólares (USD)      ▾ │                │
│  └───────────────────┘                  └──────────────────────┘                │
│                                                                                 │
│  ┌─ La factura del cliente está en pesos ($) ─────────────────────────────────┐ │
│  │  ↕ La multa está en dólares y la factura en pesos: la pasamos a pesos.      │ │
│  │                                                                            │ │
│  │  Fecha en que el operador cobró la multa *                                  │ │
│  │  ┌──────────────┐                                                          │ │
│  │  │ 05/07/2026   │                                                          │ │
│  │  └──────────────┘                                                          │ │
│  │                                                                            │ │
│  │  Tipo de cambio del día que el operador cobró *                             │ │
│  │  ┌──────────────────┐   Dólar oficial del BNA del 05/07.                    │ │
│  │  │ 1 US$ = $ 1.200  │   Si ponés otro número, lo tomamos "a mano".         │ │
│  │  └──────────────────┘                                                      │ │
│  │                                                                            │ │
│  │  → Se le cobra al cliente   $ 240.000                                       │ │
│  └────────────────────────────────────────────────────────────────────────────┘ │
│                                                                                 │
│  ¿Por qué corregís el monto o la moneda? *                                       │
│  ┌────────────────────────────────────────────────────────────────────────┐    │
│  │ El operador informó la multa en dólares, no en pesos.                    │    │
│  └────────────────────────────────────────────────────────────────────────┘    │
│                                                                                 │
│                                          [ Volver ]  [ Guardar corrección ]     │
└─────────────────────────────────────────────────────────────────────────────────┘
```

### Orden de los elementos (de arriba a abajo)

1. **Cabecera** (igual que hoy): "Corregir el monto y la moneda de la multa" · Reserva #… · ✕.
2. **Explicación** (igual que hoy, texto del modo corregir).
3. **Monto que retiene el operador** + **Moneda** (misma fila, igual que hoy). Acá el usuario carga
   el monto en la **moneda original del operador** (US$ 200) — no lo convierte él. `[arquitectura]`
4. **Bloque de conversión** (NUEVO, aparece solo si cruza). Ver §3.
5. **"¿Por qué corregís el monto o la moneda?"** (obligatorio, igual que hoy).
6. **Botones** Volver / Guardar corrección (igual que hoy).

---

## 3. El bloque de conversión, por dentro `[GUÍA + PATRÓN]`

Recuadro con borde propio (mismo estilo que el recuadro del cobro cruzado). Contiene, en este orden:

1. **Título del recuadro:** una línea que dice qué va a pasar, en criollo, sin la palabra "diferencia
   de cambio" (prohibida). Texto: **"↕ La multa está en dólares y la factura en pesos: la pasamos a
   pesos."** (se da vuelta si es al revés: "…está en pesos y la factura en dólares: la pasamos a
   dólares"). `[GUÍA: nunca "diferencia de cambio"; PATRÓN: cobro cruzado usa una línea así]`

2. **Fecha en que el operador cobró la multa \*** (campo de fecha, obligatorio). Es la fecha que
   define qué dólar se usa. `[GUÍA 2026-07-10: "El TC que manda es el del DÍA EN QUE EL OPERADOR
   COBRÓ"; los tres datos del recuadro son obligatorios]`
   - No puede ser futura (tope = hoy). Reusa la validación que ya existe ("La fecha no puede ser
     futura."). `[PATRÓN: validarCamposMulta]`

3. **Tipo de cambio del día que el operador cobró \*** (campo numérico, formato "1 US$ = $ ___",
   obligatorio). `[GUÍA 2026-07-10: rótulo "Tipo de cambio del día que el operador cobró"]`
   - Al elegir la fecha, el sistema **sugiere el dólar oficial del BNA de ese día** ya guardado y
     **rellena el campo con ese número** (editable). Para poner otro, se **escribe encima**. Debajo,
     en gris: **"Dólar oficial del BNA del 05/07. Si ponés otro número, lo tomamos 'a mano'."**
     `[GASTÓN 2026-07-13, P2=A: viene ya escrito y se pisa escribiendo encima]`
   - **La fuente del tipo de cambio se resuelve sola, no se pregunta:** vale "Dólar oficial del BNA"
     mientras el usuario no toque el número; pasa a **"a mano"** apenas lo cambia. `[GASTÓN
     2026-07-13, P2=A: si lo cambia, la fuente queda "a mano"]`
   - **Aviso suave si el número escrito está muy lejos del oficial (no frena):** si el usuario pisa
     el sugerido con un número que se aparta bastante del dólar del BNA de ese día, aparece un
     **cartelito amarillo** debajo del campo: **"⚠ El dólar que pusiste está muy lejos del oficial.
     Revisalo."** Es solo un aviso: **se puede guardar igual.** `[GASTÓN 2026-07-13, P1=A: aviso
     suave que no frena]`
     - **Cuánto es "muy lejos" es una regla nueva** que hoy no existe (la pantalla de cobro no tiene
       este aviso). Es la ÚNICA pieza de esta spec que necesita que dominio/negocio fijen el umbral
       (ej. ±X %). Frontend-senior no lo inventa: si al construir no está fijado, se deja el aviso
       apagado detrás del umbral y se pregunta el número. No bloquea el resto de la pantalla.

4. **Línea de resultado:** **"→ Se le cobra al cliente $ 240.000"** (el monto ya convertido a la
   moneda de la factura). Se recalcula solo a medida que cambian monto / fecha / tipo de cambio.
   `[PATRÓN: cobro cruzado muestra "→ Se cancelan US$ X de la deuda"]`

**Regla de guardado:** el botón **Guardar corrección** queda apagado (gris) mientras falte la fecha
o el tipo de cambio del recuadro (además de las reglas de siempre: monto > 0 y motivo cargado).
`[PATRÓN: cobro cruzado bloquea Confirmar si falta TC o fecha]`

---

## 4. Estados (vacío / cargando / error / éxito)

| Estado | Qué se ve |
|---|---|
| **Caso normal (misma moneda)** | Panel de hoy, sin el bloque de conversión. Cero cambio. |
| **Cruza de moneda, con dólar del BNA disponible** | Bloque de conversión con el TC ya sugerido y el resultado calculado. |
| **Cruza de moneda, SIN dólar del BNA para esa fecha** | El campo de TC arranca **vacío** (no hay sugerencia). Debajo, en gris: **"No tenemos el dólar del BNA para el 05/07. Escribí el tipo de cambio a mano."** La fuente queda en "a mano". El resultado no se calcula hasta que se escribe el TC. **No se puede guardar** hasta que haya un TC escrito. `[arquitectura: sin cotización y sin TC escrito no se puede confirmar]` |
| **Fecha futura** | Cartel del campo: "La fecha no puede ser futura." Botón apagado. `[PATRÓN]` |
| **Guardando** | Botón "Guardando…" con girito, campos deshabilitados. `[igual que hoy]` |
| **Falló guardar (conexión / conflicto)** | Cartel rojo arriba de los botones con el mensaje en criollo; TODO lo cargado queda intacto; se reintenta en el mismo botón. `[GUÍA Ronda 2: nunca se pierde lo cargado]` |
| **Éxito** | "Listo. Se está cobrando la multa al cliente." (mismo aviso de hoy). El cartel naranja de multa trabada desaparece de la ficha. |

---

## 5. Qué NO hay que hacer (barandas)

- **NO** abrir una ventana flotante: todo pasa dentro del recuadro en línea de la ficha. `[GUÍA]`
- **NO** usar nunca la frase "diferencia de cambio" (ni "diferencia cambiaria", "spread", "ajuste
  FX", etc.) en ningún texto de este panel. `[GUÍA regla dura multimoneda #2]`
- **NO** tocar el caso de misma moneda: si multa y factura coinciden, el panel es el de hoy, letra
  por letra.
- **NO** mostrar tokens internos, códigos de invariante, enums en número, ni el texto crudo del
  error de AFIP. Los mensajes van en criollo. `[gate data-exposure]`
- **NO** hacer que el vendedor convierta a mano: él carga el monto en la moneda del operador; el
  sistema convierte y muestra el resultado.
- **NO** inventar un dólar cuando no hay cotización guardada: en ese caso se le pide al usuario que
  lo escriba (ver estado "sin dólar del BNA").

---

## 6. Preguntas para Gastón — RESPONDIDAS (2026-07-13)

Las 2 preguntas quedaron cerradas por Gastón el 2026-07-13, las dos con la opción recomendada (A):

- **P1 = A (aviso suave que no frena).** Si el dólar escrito se aparta bastante del oficial del BNA
  de ese día, aparece un cartelito amarillo ("⚠ El dólar que pusiste está muy lejos del oficial.
  Revisalo."); **se puede guardar igual.** Ya integrado en §3 (punto 3) y §4. Único pendiente de
  negocio: el umbral de "muy lejos" (no lo inventa el front).
- **P2 = A (el sugerido viene ya escrito y se pisa escribiendo encima).** El casillero de tipo de
  cambio arranca con el dólar del BNA de ese día; para poner otro, se escribe encima; al hacerlo, la
  fuente pasa a "a mano". Ya integrado en §3 (punto 3).

**La spec queda CERRADA.** Todo lo de arriba se puede construir tal cual; la única dependencia
abierta es el umbral del aviso de P1, que no bloquea el resto de la pantalla.
</content>
</invoke>
