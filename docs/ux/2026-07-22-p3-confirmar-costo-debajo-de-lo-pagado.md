# Spec UX — P3: confirmar al bajar el costo del operador por debajo de lo ya pagado

**Fecha:** 2026-07-22
**Pantalla:** ficha de edición de servicio en línea (`ServiceInlineCard.jsx`), dentro de la ficha de una reserva.
**Decisión de negocio (YA FIRMADA, NO reabrir):** D2 del inventario del circuito proveedor
(`docs/architecture/2026-07-21-circuito-proveedor-inventario.md`). Al editar un servicio y bajar el
costo del operador por debajo de lo que YA se le pagó, **no se bloquea** (puede ser un descuento real
del operador) pero **se exige confirmación explícita** con el monto exacto de la diferencia:
*"esto va a generar $X de saldo a favor con este operador — ¿confirmás?"*.
Esta spec diseña SOLO la parte visible; el contrato técnico (409 + code
`COST_BELOW_PAID_CONFIRMATION_REQUIRED` + reenvío con marca de confirmación) ya está decidido.

**Fuente de diseño:** `docs/ux/guia-ux-gaston.md` únicamente. Lo que la guía no cubre está al final,
en "PREGUNTAS PARA GASTON".

---

## 0. Alcance — dónde puede aparecer este cartel (verificado en código)

- **SÍ:** ficha de edición de servicio en línea (`ServiceInlineCard.jsx`), abierta desde la ficha de
  la reserva. Es el único lugar donde se edita el **valor del costo** (`netCost`) de un servicio.
- **NO:** la ficha del operador (`SupplierAccountPage.jsx`, solapa "Servicios comprados"). Ahí el costo
  se muestra **solo lectura** (`formatCurrency(service.netCost, …)`, línea 2156-2160); los únicos
  editores en línea de esa solapa son el de **estado del servicio** y el de **código de confirmación**
  — ninguno toca el valor del costo. Por lo tanto el code `COST_BELOW_PAID_CONFIRMATION_REQUIRED` **no
  se dispara desde la ficha del operador**. El alcance queda acotado a la ficha de reserva.
  (Si el editor de estado de esa solapa llegara a chocar contra un guard, ese es otro caso —
  des-confirmar, Tanda P1 — no este.)

---

## 1. Dónde y cómo aparece el cartel

**Reusa la MISMA ubicación que ya tiene el cartel de error rojo de la ficha** (arriba de los botones
Guardar/Cancelar, dentro de la ficha, `ServiceInlineCard.jsx` ~820-845), pero **NO es el mismo cartel
ni el mismo color**: es un cartel aparte, de **aviso**, que ocupa ese lugar cuando el guardado vuelve
con el code `COST_BELOW_PAID_CONFIRMATION_REQUIRED`.

- **NO reemplaza al cartel de error rojo.** El rojo (`inline-card-error`) sigue reservado para "no se
  pudo guardar" de verdad (falló la conexión, un rechazo real). Este es un **aviso accionable**: el
  guardado no falló, el sistema solo necesita que confirmes una consecuencia de plata. Son dos estados
  distintos y **nunca se muestran los dos a la vez** (el 409 de confirmación llega por su propio code,
  se enruta a este estado, no al de error).
- **Color: ámbar/naranja, distinto del rojo.** Esto responde a la pregunta "¿color distinto por ser
  aviso y no error?" → **sí**. La guía ya fija la semántica: **naranja = "algo hay que hacer / hay que
  decidir algo"** (sección "Monitor Pendientes con AFIP", 2026-07-08: *"Color del cartel: naranja, el de
  'algo se trabó, hay que hacer algo'"*), mientras el **rojo queda para errores** (Formularios, Ronda 2:
  *"cartel rojo… No se pudo guardar"*). Un pedido de confirmación no es un error → va en ámbar.
- **Todo en línea, dentro de la ficha. Nada se abre encima.** No es una ventana flotante. Fundamento:
  (a) regla de la casa "fichas inline, nunca modal"; (b) la guía reserva la ÚNICA ventana flotante
  permitida para el *"¿Seguro? antes de algo irreversible"* (Ventanas emergentes, 2026-07-15 y 2026-06-24)
  — y **bajar el costo es reversible** (se corrige el número y se vuelve a guardar; el saldo a favor no
  es un hecho irreversible), así que **no** califica para la excepción de ventana flotante; (c) el
  contrato técnico ya definido es "409 → cartel en la ficha → reenviar el mismo guardado", que es
  exactamente el mismo mecanismo del cartel de la Tanda P1 que ya vive inline en este archivo
  (`inline-card-error` + botón "Emitir factura").
  > **Nota de transparencia (no es contradicción):** existe un precedente parecido, el *"¿confirmás
  > costo $0?"* (Formularios, 2026-06-06/2026-06-05), que la guía resolvió como *"ventana que frena"*.
  > Aquel es una comprobación del front **antes** de mandar el guardado, sobre algo que fija $0 "como
  > sugerencia para todos"; éste llega como respuesta del motor **después** del intento de guardar y es
  > reversible. Por eso este caso sigue el patrón inline (P1), no el de la ventana que frena. Queda
  > anotado para que la elección sea consciente, no un desvío silencioso.
- **Nunca se pierde lo cargado.** Igual que con el cartel de error: la ficha queda abierta con TODOS
  los datos intactos; el cartel de aviso aparece arriba de los botones y el vendedor decide sin perder
  nada (Formularios, Ronda 2).

**Texto del cartel = el mensaje del motor, tal cual.** El backend ya devuelve el mensaje en es-AR con
el monto exacto (ej.: *"Esto va a generar $ 45.000 de saldo a favor con este operador. ¿Confirmás?"*).
El front **lo muestra sin reescribir** (regla de la casa: el mensaje del motor se muestra tal cual).
El front NO calcula ni arma el monto: lo toma del mensaje del motor.

---

## 2. Los dos botones del cartel (textos exactos)

Van dentro del cartel ámbar, a la derecha, en la misma línea o debajo del texto según entre. La voz
sale del precedente ya aprobado *"Volver / Sí, confirmar"* del "¿confirmás costo $0?".

| Botón | Texto exacto | Qué hace |
|---|---|---|
| **Confirmar** (primario, ámbar/relleno) | **`Sí, confirmar`** | Reenvía el MISMO guardado con la marca de confirmación. El motor esta vez no rechaza → guarda. |
| **Volver** (secundario, borde) | **`Volver a corregir`** | Cierra el cartel de aviso y deja la ficha abierta con todo intacto; el foco vuelve al campo del costo para que el vendedor cambie el número. No manda nada. |

Notas:
- Mientras se reenvía el guardado (tras "Sí, confirmar"), el botón muestra **`Guardando…`** y ambos
  botones quedan deshabilitados (mismo comportamiento de `guardando` que ya tiene la ficha) — evita
  doble envío.
- "Volver a corregir" **no** pierde datos ni cierra la ficha: solo saca el cartel. Es distinto de
  "Cancelar" (que cierra la ficha sin guardar) — por eso el texto es "Volver a corregir" y no "Cancelar".

---

## 3. Qué pasa tras confirmar

Al apretar **"Sí, confirmar"**:
1. Se reenvía exactamente el mismo guardado, ahora con la marca de confirmación.
2. El motor guarda (no vuelve a rechazar por este motivo) y registra la diferencia como **saldo a favor
   con ese operador**.
3. **Guardado normal, igual que cualquier edición exitosa:** la ficha se cierra y el servicio queda
   como una fila más de la lista con el costo nuevo (comportamiento actual de `onGuardado`; hoy
   `ServiceInlineCard` cierra en silencio al guardar bien — no inventamos un aviso nuevo que la guía no
   pida).
4. El saldo a favor generado queda visible donde ya vive esa información: el recuadro **"Saldo a favor"
   (verde)** de la cuenta del operador (decisión 2026-07-01, Fase D). No hace falta que la ficha lo
   repita: el cartel de aviso ya dijo el monto exacto antes de confirmar y el vendedor lo aceptó a
   propósito.

Si el reenvío fallara por otra cosa (conexión, otro rechazo real), aplica el cartel **rojo** de error
de siempre (Ronda 2), no este cartel ámbar.

**Selectores estables sugeridos (automation standards):** `inline-card-cost-confirm` (cartel ámbar),
`inline-card-cost-confirm-si` (botón Sí, confirmar), `inline-card-cost-confirm-volver` (botón Volver a
corregir). El cartel lleva `role="alert"`, como el de error.

---

## 4. Mockup ASCII

Estado normal de la ficha de edición (así se ve hoy, sin el aviso):

```
┌─ Editar servicio ─────────────────────────────────────────────────────┐
│ [Hotel]  Aéreo  Traslado  Paquete  Asistencia   (pestañas bloqueadas)  │
│                                                                        │
│  Nombre [ Hotel Maitei        ]   Ciudad [ Posadas ]                   │
│  Entrada [10/08]  Salida [13/08]  Noches 3  Habitaciones 1             │
│  Costo/noche [ 30.000 ]  Venta/noche [ 40.000 ]  Moneda [ ARS ]        │
│  ▸ Más detalles                                                        │
│ ────────────────────────────────────────────────────────────────────  │
│  Venta $ 360.000    Ganás $ 30.000              [ Cancelar ] [Guardar cambios] │
└────────────────────────────────────────────────────────────────────────┘
```

Al guardar bajando el costo por debajo de lo ya pagado al operador (llega el 409 de confirmación):

```
┌─ Editar servicio ─────────────────────────────────────────────────────┐
│ [Hotel]  Aéreo  Traslado  Paquete  Asistencia   (pestañas bloqueadas)  │
│                                                                        │
│  Nombre [ Hotel Maitei        ]   Ciudad [ Posadas ]                   │
│  Costo/noche [ 15.000 ]  Venta/noche [ 40.000 ]  Moneda [ ARS ]        │
│                                                                        │
│ ────────────────────────────────────────────────────────────────────  │
│  Venta $ 360.000    Ganás $ 315.000                                    │
│                                                                        │
│   ┌─ (ÁMBAR / naranja — aviso, NO rojo) ───────────────────────────┐   │
│   │ ⚠  Esto va a generar $ 45.000 de saldo a favor con este         │   │
│   │    operador. ¿Confirmás?                                        │   │
│   │                          [ Volver a corregir ]  [ Sí, confirmar ]│   │
│   └─────────────────────────────────────────────────────────────────┘   │
│                                                 [ Cancelar ] [Guardar cambios] │
└────────────────────────────────────────────────────────────────────────┘
```

- El texto *"Esto va a generar $ 45.000 de saldo a favor con este operador. ¿Confirmás?"* es el mensaje
  del **motor**, mostrado tal cual (es-AR, con el monto exacto). Acá es solo un ejemplo.
- "Sí, confirmar" reenvía y guarda → la ficha se cierra y la fila queda con el costo nuevo.
- "Volver a corregir" saca el cartel, deja la ficha abierta y el foco en el campo Costo/noche.

---

## 5. Qué NO hacer

- **No** mostrar este aviso en rojo (es aviso, no error) ni mezclarlo con el cartel de error.
- **No** abrir una ventana flotante / modal para confirmar (es reversible; va inline).
- **No** reescribir, recortar ni recalcular el mensaje del motor: se muestra tal cual (el monto lo pone
  el backend, no el front).
- **No** cerrar la ficha ni perder datos al elegir "Volver a corregir".
- **No** agregar este cartel en la ficha del operador (`SupplierAccountPage`): ahí el costo es solo
  lectura, el code no se dispara.
- **No** convertir monedas ni mostrar "diferencia de cambio": el saldo a favor queda en la misma moneda
  del servicio (reglas duras de multimoneda).

---

## PREGUNTAS PARA GASTON

> **✅ RESPONDIDA Y FIRMADA por Gaston el 2026-07-21**: eligió la opción A
> (**guarda calladito**) — tras "Sí, confirmar" el guardado es como cualquier
> edición: la ficha se cierra y la fila muestra el costo nuevo, sin cartelito
> verde extra. El saldo a favor se ve después en la cuenta del operador.
> Con esto la spec queda CERRADA para implementación.

### Tema: aviso cuando bajás el costo del operador por debajo de lo que ya le pagaste
Contexto: cuando editás un servicio y le ponés un costo más bajo que lo que ya le pagaste al operador,
el sistema no te frena (puede ser un descuento que te dieron) pero te avisa cuánta plata te queda a
favor con ese operador y te pide confirmar. Casi todo ya está definido por reglas tuyas anteriores (el
cartel va adentro de la ficha, en naranja porque es un aviso y no un error, con el texto que arma el
sistema y los botones "Volver a corregir" / "Sí, confirmar"). Queda **una sola** cosa que las reglas
de antes no definen.

**P1. Después de confirmar y guardar, ¿el sistema te dice algo o guarda calladito?**
Cuando apretás "Sí, confirmar", el servicio se guarda con el costo nuevo. La pregunta es si, al
terminar, querés un cartelito breve confirmando la plata que quedó a favor, o preferís que guarde sin
decir nada (como cuando editás cualquier otro servicio).

  A) **Guarda calladito, como cualquier edición.** El cartel de antes ya te dijo el monto y vos lo
     confirmaste; el saldo a favor aparece en la cuenta del operador (el recuadro verde "Saldo a
     favor"). *(RECOMENDADA: es lo que ya hace la ficha al guardar bien, y no repite algo que recién
     confirmaste.)*
```
   [ Sí, confirmar ]  →  la ficha se cierra, la fila queda con el costo nuevo. (sin cartel extra)
```

  B) **Un cartelito verde de confirmación al terminar**, tipo:
```
   ✓ Guardado. Quedaron $ 45.000 de saldo a favor con este operador.
```
     (Se ve unos segundos y se va. Es un poco más de tranquilidad, a costa de un cartel más.)
