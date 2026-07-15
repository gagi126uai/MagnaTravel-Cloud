# T5 — Pantalla de confirmar y emitir la devolución por un servicio cancelado (NC parcial)

Fecha: 2026-07-15. Autor: `ux-ui-disenador`. Estado: **APROBADA POR GASTÓN (2026-07-15).** Las 5 preguntas
fueron respondidas (P1=A, **P2=B con doble confirmación**, P3=A, P4=A, P5a=A, P5b=A). La sección final
**"SPEC APROBADA PARA IMPLEMENTAR"** es la que sigue `frontend-senior` al pie de la letra. El bloque de
mockups de arriba refleja los datos y la voz; el cartel "¿Seguro?" (P2=B) está diseñado abajo.

> **Qué es esto, en criollo.** Cuando en una reserva ya facturada se cancela UNO de sus servicios
> (por ejemplo, el hotel de una reserva que también tiene aéreo), la reserva **sigue viva** por lo
> demás, pero hay que devolverle al cliente la parte de ese servicio con una **nota de crédito**
> (la "devolución"). El sistema ya guardó todo: qué servicio se canceló, cuánto se le devuelve
> (monto congelado, NO se recalcula), a qué factura corresponde y en qué moneda. Falta LA PANTALLA
> donde el back-office **confirma y emite** esa devolución a la AFIP, y ve el resultado.
>
> Contrato de datos y estados: `docs/architecture/2026-07-15-t5-emision-nc-parcial-diseno.md` (§7, §8, §11).
> Esta spec diseña lo visual/flujo; NO redefine el contrato.

---

## Lo que YA está decidido por la guía (no se pregunta)

Cada punto cita la regla que lo respalda en `docs/ux/guia-ux-gaston.md`.

1. **La bandeja "Comprobantes por resolver" es una lista pasiva; cada fila es un link a la ficha, sin
   botones propios.** (Guía 2026-07-10, P7/P8/P9 + `ComprobantesPorResolverTab.jsx`.) El caso T5
   "Pendiente de emisión" se suma como **una fila más** de esa lista. El click lleva a la ficha, que
   es donde se resuelve.
2. **La resolución vive en la FICHA, con la acción puntual que aplica al estado.** (Guía 2026-07-08
   "el paso de multa vive en la ficha".) La confirmación/emisión de la devolución se hace **EN LÍNEA
   dentro de la ficha**, nunca en una ventana flotante ni mandando a otra pantalla.
3. **El monto NO se recalcula: se muestra.** (Contrato §7 + criterio matriculado.) La pantalla es de
   solo lectura sobre el monto congelado; el usuario no edita cifras acá.
4. **Multimoneda dura:** el monto va en la **moneda de la factura**; si es en dólares se muestra el
   **tipo de cambio congelado de esa factura**; nunca se ofrece pasar a pesos, nunca aparece la frase
   "diferencia de cambio". (Guía 2026-06-09, tres reglas duras.)
5. **En pantallas de facturación SÍ se puede decir "nota de crédito".** (Guía 2026-07-08, glosario:
   "el término 'nota de crédito' solo aparece en las pantallas de facturación".) Este panel es una
   acción de facturación, así que usa "nota de crédito" y "factura" con naturalidad. En los avisos de
   la campanita, en cambio, seguiría siendo "devolución".
6. **El servicio removido se dice "cancelado" (queda tachado).** (Guía 2026-06-08, tacho dinámico: un
   servicio confirmado por el operador que se saca → "Cancelar" → "Cancelado".) La voz de la pantalla
   habla de **"el servicio cancelado"**, no de "servicio anulado" ("Anular" está reservado a la
   reserva entera).
7. **Estados de emisión: procesando → emitida → rechazada, todo EN LÍNEA, auto-refrescándose.** (Guía
   H2 2026-06-24 P2/P3/P4 + familias `procesando`/`accionTrabada`/`confirmada` de
   `OperatorPenaltyStepPanel.jsx`.) El "procesando" **no atrapa al usuario**: el panel se refresca solo
   y el usuario puede irse de la ficha (guía 2026-07-08 "el en-proceso no atrapa al usuario").
8. **Rechazo de AFIP: cartel rojo + motivo tal cual de AFIP + reintentar.** (Guía H2 2026-06-24 P4.)
9. **Aviso del plazo de ARCA (15 días): suave, informa, NO bloquea; el tono sube si venció.** (Guía
   2026-07-14 "Deshacer una multa ya emitida", molde del aviso de plazo.) El front NO calcula la fecha:
   la recibe del backend.
10. **Nada de jerga, IDs internos, enums ni texto crudo de error de AFIP fuera del renglón del rechazo.**
    (Guía 2026-07-08, 8 reglas de voz + gate data-exposure.)
11. **Permiso:** ve y usa el botón "Confirmar y emitir" **solo quien puede emitir/anular fiscalmente**
    (`cobranzas.invoice_annul`, el mismo de la anulación total y de la bandeja). (Contrato §11.) Quien no
    lo tiene ve la fila en la bandeja según su permiso, pero no el botón de emitir.

---

## 1. La fila en la bandeja "Comprobantes por resolver"

Se suma un tercer tipo de fila a `fusionarComprobantesPorResolver`, con el mismo molde pasivo:

```
┌──────────────────────────────────────────────────────────────────────┐
│  Comprobantes por resolver                              [Actualizar ⟳] │
├──────────────────────────────────────────────────────────────────────┤
│  DEVOLUCIÓN · SERVICIO CANCELADO   Reserva F-2026-1042            ›     │
│  Falta confirmar y emitir la devolución            hace 2 días          │
├──────────────────────────────────────────────────────────────────────┤
│  MULTA · CARGO AL CLIENTE          Reserva F-2026-1039            ›     │
│  Falta emitir el cargo                             hace 1 día           │
└──────────────────────────────────────────────────────────────────────┘
```

- **Rótulo del comprobante (columna izquierda):** `DEVOLUCIÓN · SERVICIO CANCELADO`.
- **Qué falta (en criollo):** `Falta confirmar y emitir la devolución`.
- **Hace cuánto:** tiempo relativo desde que quedó pendiente (mismo `textoTiempoRelativo` ya usado).
- Click en toda la fila → ficha de la reserva. **Sin botón propio.**
- Orden dentro de la lista: junto a las demás, sin prioridad especial (se define en implementación;
  por defecto respeta el orden que trae el backend).

> Rótulos exactos: ver **P5**.

---

## 2. El panel de confirmación en la ficha (estado "listo para emitir")

Vive EN LÍNEA en la ficha de la reserva (dónde exactamente: **P1**). Muestra los datos del contrato §7,
todo de solo lectura salvo el botón. Mockup del caso normal (agencia Monotributo, una factura, en pesos):

```
┌──────────────────────────────────────────────────────────────────────────┐
│ 💳 Falta emitir una devolución por un servicio cancelado                   │
│                                                                            │
│ Servicio cancelado:  Hotel Maitei (Posadas) · Operador: Turismo Cardozo    │
│ Se le devuelve al cliente:      $ 180.000                                   │
│ Sale de la factura:             Factura B 0001-00012345                     │
│ Saldo de esa factura hoy:       $ 520.000  (queda viva por el resto)        │
│                                                                            │
│ ⚠ Quedan 9 días para emitir esta devolución sin trámites extra ante ARCA   │
│    (vence el 24/07).                                                        │
│                                                                            │
│                          [ Confirmar y emitir la devolución ]  [ Volver ]  │
└──────────────────────────────────────────────────────────────────────────┘
```

Variante en dólares (factura en US$, muestra el TC congelado de la factura, de solo lectura):

```
│ Se le devuelve al cliente:      US$ 450                                     │
│ Sale de la factura:             Factura B 0001-00012346                     │
│ Dólar de la factura:            $ 1.180  (el de la factura, no se cambia)   │
```

**Contenido, de arriba a abajo (todo lo da el backend, el front no deduce):**
1. Título del panel.
2. Servicio(s) cancelado(s) en este evento + operador, con su monto bruto congelado.
3. **Monto de la devolución** = suma de las líneas contra esa factura (congelado).
4. **Factura destino** por su número legible (nunca un Id interno).
5. **Saldo vivo de esa factura antes de esta devolución** + aclaración "queda viva por el resto".
6. Si la factura es en dólares: **dólar congelado de la factura** (solo lectura). (Rótulo: **P5**.)
7. **Aviso de plazo (15 días)**, si el backend lo manda: texto de **P4**. No bloquea.
8. Si la agencia es **RI**: aviso neutro de que la emisión necesita la firma del contador sobre la
   alícuota, y el botón queda bloqueado (default multi-condición fiscal; hoy la agencia es Monotributo y
   este aviso no aparece). Texto recomendado: "Esta devolución necesita la firma del contador antes de
   emitirse." (Se puede ajustar; no se pregunta ahora porque es camino futuro con default seguro.)
9. Botones: **"Confirmar y emitir la devolución"** (gateado por permiso) y **"Volver"**.

**Motivo:** ver **P3** (recomendación: sin motivo nuevo; el motivo ya se dio al cancelar el servicio;
queda registrado automáticamente quién emite).

---

## 3. Estado "necesita que resuelvas la factura o el monto antes de emitir" (trabada)

Si el backend responde que la línea quedó sin factura destino elegida o sin monto (moneda que no
coincide, TC incoherente — `INV-T5-EMIT-UNRESOLVED`), el panel NO ofrece emitir: muestra el cartel
naranja de acción trabada (familia `accionTrabada`), con la acción puntual que corresponda, reusando lo
que YA existe:

```
┌──────────────────────────────────────────────────────────────────────────┐
│ 🟠 Falta resolver la factura o el monto antes de emitir la devolución       │
│                                                                            │
│ Servicio cancelado:  Hotel Maitei (Posadas)                                 │
│ Esta reserva tiene 2 facturas activas: elegí a cuál corresponde.            │
│                                                                            │
│ ¿A qué factura del cliente corresponde?  [ Elegí una…            ▾ ]        │
│                                                                            │
│                                              [ Guardar y seguir ]           │
└──────────────────────────────────────────────────────────────────────────┘
```

- Elegir factura destino con 2+ facturas: **reusa `ElegirFacturaDestinoInline`** y el formato de opción
  `· Factura B 0001-00012345 — US$ 200` (guía 2026-07-10 P5). El botón queda apagado hasta elegir.
- Si el problema es de moneda cruzada de la línea, se reusa el **mismo bloque de conversión guiada** ya
  aprobado (guía 2026-07-13). No se inventa nada nuevo.
- Resuelto esto, el panel vuelve al estado "listo para emitir" (§2).

---

## 4. Estados después de apretar "Confirmar y emitir"

Mismo molde de familias de la multa + H2. Todo EN LÍNEA, se refresca solo, **no atrapa** al usuario.

**a) Emitiendo (ámbar, se refresca solo, sin botón):**
```
┌──────────────────────────────────────────────────────────────────────────┐
│ ⏳ Estamos emitiendo la nota de crédito en AFIP. En unos instantes vas a    │
│    ver el resultado.                                                        │
└──────────────────────────────────────────────────────────────────────────┘
```
El usuario puede irse de la ficha; al volver ve el estado actualizado (mismo hook de auto-refresco que
"se está emitiendo la multa").

**b) Emitida (verde, con el número del comprobante — es pantalla de facturación):**
```
┌──────────────────────────────────────────────────────────────────────────┐
│ ✔ Devolución emitida. Nota de crédito B 0001-00000987.                     │
│   [ Ver/Descargar PDF ]   [ Enviar al cliente ]                            │
└──────────────────────────────────────────────────────────────────────────┘
```
- Tipo + número del comprobante a la vista (molde H2 P3/P5). Acciones Ver/Descargar y Enviar reusan lo
  que ya existe. El servicio en la lista queda tachado "Cancelado"; la factura sigue viva por el resto.

**c) Rechazada por AFIP (rojo, motivo tal cual + reintentar):**
```
┌──────────────────────────────────────────────────────────────────────────┐
│ ✕ No se pudo emitir la devolución.                                         │
│   Motivo de AFIP: «…lo que devuelva AFIP, tal cual…»                        │
│                                              [ Reintentar ]                 │
└──────────────────────────────────────────────────────────────────────────┘
```
- Molde H2 P4. La factura NO se tocó, sigue viva. El reintento es seguro (idempotente, garantía del
  backend).

**d) Errores de guardas (carteles neutros, sin jerga):**
- `INV-T5-EMIT-CAP` ("otra devolución consumió el saldo mientras tanto") → "El saldo de la factura
  cambió; revisá el monto." Vuelve a mostrar el panel con el dato fresco.
- Falta de permiso → mensaje neutro del módulo (no aparece el botón para quien no puede).

---

## 5. Qué NO hay que hacer

- **NO** una ventana flotante en ningún paso (todo en línea en la ficha).
- **NO** dejar editar el monto ni el TC (son congelados: se muestran, no se recalculan).
- **NO** ofrecer pasar la devolución a pesos si la factura es en dólares.
- **NO** mostrar la frase "diferencia de cambio", ni "CAE"/"RG 4540"/Id interno/enum/texto de código.
- **NO** marcar la reserva como Anulada ni tratar esto como anulación total: la reserva **sigue viva**
  por los servicios no cancelados.
- **NO** atrapar al usuario en un spinner a pantalla completa: el "emitiendo" se refresca solo y se puede
  abandonar la ficha.
- **NO** poner botones de acción en la fila de la bandeja (es pasiva).

---

## SPEC APROBADA PARA IMPLEMENTAR (Gastón, 2026-07-15)

Respuestas: **P1=A** (aviso arriba, accionable, siempre visible) · **P2=B** (CON segundo cartel "¿Seguro?")
· **P3=A** (sin motivo nuevo, registro automático de quién/cuándo) · **P4=A** (textos del aviso de 15 días
tal cual) · **P5a=A** (`DEVOLUCIÓN · SERVICIO CANCELADO`) · **P5b=A** (`Dólar de la factura: $ X …`).

### A. Fila en la bandeja "Comprobantes por resolver" (pasiva, link a la ficha)
Como el §1 de arriba. Rótulo `DEVOLUCIÓN · SERVICIO CANCELADO`, "qué falta" = `Falta confirmar y emitir la
devolución`, hace-cuánto, chevron, click en toda la fila → ficha. Sin botón propio.

### B. Aviso accionable ARRIBA de la ficha (P1=A)
Vive en la tira de avisos accionables (arriba, siempre visible, junto al "Dar OK"; regla 2026-07-05). Es el
panel del §2 de arriba, con el monto/factura/saldo/aviso-15-días/botones. En dólares muestra la línea
`Dólar de la factura: $ 1.180 (el de la factura, no se cambia)` (P5b=A). Botón principal **"Confirmar y
emitir la devolución"** (solo con permiso `cobranzas.invoice_annul`) + **"Volver"**. Sin campo de motivo
(P3=A). Si falta resolver factura/monto → cartel naranja de acción trabada (§3). Aviso de 15 días con los
textos de abajo (P4=A).

### C. El segundo cartel "¿Seguro?" antes de mandarla a ARCA (P2=B) — DISEÑO FINAL
Gastón eligió la doble confirmación **a sabiendas** (opción NO recomendada). **DIFIERE del anular con UNA
factura** (2026-06-25 Caso 4, que no tiene "¿seguro?" extra) y **coincide con el anular multi-factura**
(2026-07-01 P2). Respetar tal cual.

Flujo: el usuario ve el panel con TODOS los datos (paso 1) → aprieta **"Confirmar y emitir la devolución"**
→ recién ahí aparece el cartel "¿Seguro?" (paso 2, la ÚNICA ventana flotante permitida en este flujo, mismo
criterio que el "¿seguro?" de emitir factura H2 y el multi-factura):

```
┌────────────────────────────────────────────────────────┐
│  ¿Seguro?                                                │
│                                                          │
│  Se va a emitir la nota de crédito en AFIP por la        │
│  devolución del servicio cancelado.                      │
│  Una vez emitida no se puede deshacer.                   │
│                                                          │
│                          [ Volver ]   [ Sí, emitir ]     │
└────────────────────────────────────────────────────────┘
```

- **Texto:** "Se va a emitir la nota de crédito en AFIP por la devolución del servicio cancelado. Una vez
  emitida no se puede deshacer." (voz del molde multi-factura 2026-07-01 P2 / H2 P1, adaptada al singular).
- **Botones:** **[Volver]** (cierra el cartel, vuelve al panel con los datos intactos) · **[Sí, emitir]**
  (dispara la emisión → estado "Emitiendo…").
- **[Sí, emitir]** se bloquea mientras se procesa el click para evitar doble emisión (no reenviar dos veces).
- Es el último paso antes de algo irreversible; NO se muestra si el usuario no llegó a apretar "Confirmar y
  emitir" (no aparece solo).

### D. Estados después de "Sí, emitir" (EN LÍNEA, se refrescan solos, NO atrapan)
Exactamente los del §4 de arriba:
- **Emitiendo (ámbar):** "⏳ Estamos emitiendo la nota de crédito en AFIP. En unos instantes vas a ver el
  resultado." (auto-refresco; el usuario puede irse de la ficha).
- **Emitida (verde):** "✔ Devolución emitida. Nota de crédito B 0001-00000987." + "Ver/Descargar PDF" +
  "Enviar al cliente".
- **Rechazada (rojo):** "✕ No se pudo emitir la devolución." + "Motivo de AFIP: «…»" + "Reintentar".
- **`INV-T5-EMIT-CAP`:** "El saldo de la factura cambió; revisá el monto." (vuelve al panel con dato fresco).

### E. Aviso del plazo de la AFIP (15 días) — textos finales (P4=A)
- **Dentro de plazo:** "⚠ Quedan {N} días para emitir esta devolución sin trámites extra ante ARCA (vence
  el {fecha})."
- **Pasado el plazo:** "⚠ Pasaron más de 15 días desde que se canceló el servicio. Se puede emitir igual,
  pero convendría consultarlo con un contador antes de seguir."
- **No corresponde / backend no manda el dato:** no aparece, nada ocupa ese lugar. El front NO calcula la
  fecha ni los días: los recibe del backend.

### F. Estados que el front debe contemplar (checklist de implementación)
Cargando (trae el panel) · vacío (no hay T5 pendiente → el aviso no aparece) · listo-para-emitir · trabada
(falta factura/monto) · confirmación "¿Seguro?" · emitiendo (async, auto-refresco) · emitida (éxito) ·
rechazada por AFIP (reintento) · error de guarda (cap/permiso) · sin permiso (no aparece el botón). Selectores
estables/observables para cada estado (data-testid), sin sleeps arbitrarios.

### G. Qué NO hacer (recordatorio para el reviewer)
Ver §5. En especial: la ÚNICA ventana flotante permitida es el "¿Seguro?" de C; todo lo demás va EN LÍNEA.
No editar monto/TC; no pasar a pesos una factura USD; no "diferencia de cambio"/CAE/RG 4540/Id/enum/código;
no marcar la reserva Anulada (sigue viva); no spinner que atrape; no botones en la fila de la bandeja.
