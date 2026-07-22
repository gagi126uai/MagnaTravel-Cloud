# Tratamiento ÚNICO de los avisos largos de bloqueo — especificación

**Fecha:** 2026-07-22
**Autor:** ux-ui-disenador (a partir del feedback textual de Gastón)
**Estado:** SPEC del patrón de bloqueo (rechazos) LISTA + 3 preguntas abiertas sobre los bordes.

---

## 0. De dónde sale esto (palabra del dueño)

Gastón, probando en producción (2026-07-22, textual):

> "los mensajes largos e importantes tendrían que salir como un mensaje emergente y todos este
> tipo de mensajes emergentes tratarlos de la misma forma ya que rompen la estética."

Ejemplo que citó: el aviso rojo que aparece al intentar **bajar el estado de un servicio ya pagado**
("No se puede bajar el estado de este servicio todavía: ya tiene pagos al operador y la reserva aún
no tiene factura emitida… Emití la factura de venta o gestioná el reembolso…").

Hoy ese aviso, y otros de la misma familia, se dibujan **incrustados en la pantalla** (dentro de la
fila, dentro de la ficha). Al ser textos largos, deforman la tabla/ficha donde caen. Gastón quiere
que **todos** salgan como **una ventanita que se abre encima** (un "mensaje emergente"), **todas
iguales**, para que no rompan la estética.

### La tensión que hay que resolver con cuidado

La guía (`docs/ux/guia-ux-gaston.md`) y varias specs firmadas dicen **"en línea, nunca ventana
flotante"** — pero eso es para las **fichas de trabajo** (cargar un servicio, cargar un cobro,
editar en la fila): ahí el usuario está TRABAJANDO y una ventana encima lo interrumpiría. Eso **no se
toca**.

Lo que Gastón está pidiendo mover a ventana es **otra cosa**: los **carteles de RECHAZO largos**
(el sistema le dice "no se puede, y esto es lo que tenés que hacer"). No son fichas de trabajo: son
la respuesta del motor a una acción que el usuario intentó. Esos sí, a la ventana.

Esta spec traza la raya exacta entre **qué va a la ventana** y **qué sigue como está**.

---

## 1. Inventario: los avisos de este tipo que existen hoy

Todos comparten el mismo ADN: **texto largo del motor (varias líneas) + a veces un botón de acción**,
que hoy vive incrustado y deforma la pantalla.

| # | Aviso | Dónde vive hoy | Cómo se ve hoy | Dispara | Botón de acción |
|---|---|---|---|---|---|
| 1 | **Bloqueo al bajar el estado de un servicio pagado** (`status-editor-bloqueo-pago-sin-factura`) | `SupplierAccountPage.jsx` ~1220, dentro de la fila de "Servicios comprados" | recuadro rojo angosto (max 220px) que empuja la fila | intentar bajar el estado de un servicio pagado sin factura | **"Ir a la reserva a facturar"** (link) |
| 2 | **El mismo bloqueo, pero desde la ficha** (`status-bloqueo-aviso`) | `ReservaDetailPage.jsx` ~1294 | recuadro rojo ancho + botón **"Entendido"** | mismo bloqueo, disparado desde la ficha de la reserva | solo "Entendido" (no hay a dónde ir; la factura es de ESTA reserva) |
| 3 | **Error al guardar un servicio** (`inline-card-error`) | `ServiceInlineCard.jsx` ~923 | recuadro rojo dentro de la ficha en línea | guardar un servicio y que el motor lo rechace | a veces **"Emitir factura"** (cuando el motivo es "pago al operador sin factura viva") |
| 4 | **Rechazo al anular la reserva** (`cancelar-inline-conflict-msg`) | `CancelarReservaInline.jsx` ~629 | recuadro rojo dentro del bloque de anular | intentar anular y que el motor lo rechace | ninguno |
| 5 | **Error al deshacer/corregir un reembolso** (`ErrorAccionReembolsoBanner` → `reembolso-accion-error`) | `DeshacerReembolsoInline.jsx` / `CorregirReembolsoInline.jsx` | recuadro rojo | deshacer/corregir un reembolso y que el motor lo rechace | a veces **"Ir a la cuenta del cliente"** (cuando el cliente ya usó el saldo) |
| 6 | **Confirmar costo por debajo de lo pagado** (`inline-card-confirmar-costo`) | `ServiceInlineCard.jsx` ~888 | recuadro **ámbar** dentro de la ficha + **"Sí, confirmar" / "Volver a corregir"** | bajar el costo por debajo de lo ya pagado (NO bloquea: pide confirmar) | confirmar / volver |

**Dos parientes que NO son "cartel largo de rechazo" (se aclaran acá para no confundirlos):**

| Aviso | Dónde | Qué es | Va a ventana? |
|---|---|---|---|
| **"🔒 {motivo}" al lado del tacho** (`aviso-bloqueo-anular`) | `ServiceList.jsx` ~1711 y ~2102 | etiqueta CHIQUITA, siempre visible, que explica por qué el botón está apagado. **No la dispara ningún click**: está ahí de entrada. | **NO.** Es "la palabra al lado del icono, siempre a la vista" (regla 2026-06-08). Una ventana no puede estar "siempre abierta". Queda como está. |
| **Banner ámbar "hay factura" al anular** (`cancelar-banner-con-factura`) | `CancelarReservaInline.jsx` ~614 | guía informativa MIENTRAS armás la anulación (no es un rechazo). | **NO.** Es parte de la ficha de trabajo. Queda en línea. |

**Ventanas que YA frenan (precedentes de la casa, hoy con estilos distintos entre sí):**
- "¿Seguro? Va a quedar costo $0 como sugerencia para todos" → ventana (guía Ronda 2, 2026-06-05/06).
- "¿Seguro? · Sí, anular" al anular una reserva con varias facturas → ventana local (`CancelarReservaInline` ~712).
- Los "¿Seguro?" de borrar/eliminar → `showConfirm` (SweetAlert) y `ConfirmModal`.

Hoy cada uno tiene su propia pinta (uno es SweetAlert, otro es un componente React con degradés y
bordes redondeados enormes, otro es un overlay hecho a mano). **Eso es exactamente "rompen la
estética": no hay UNA ventana, hay varias distintas.**

---

## 2. La raya: qué va a ventana, qué sigue como está

**Regla de corte (la que decide sin preguntar caso por caso):**

Un mensaje va a **ventana emergente** cuando cumple las TRES:
1. **Lo disparó un click** del usuario (intentó bajar un estado, guardar, anular, deshacer…). No está
   "de entrada" en la pantalla.
2. **Frena o condiciona** esa acción: o el motor la **rechazó** (bloqueo), o le **pide confirmar**
   una consecuencia antes de seguir.
3. Es un **texto largo del motor** (una o varias frases con instrucción), no un cartelito de dos
   palabras.

Todo lo demás **sigue como hoy**:

| Tipo de mensaje | Ejemplo | Tratamiento | Cambia? |
|---|---|---|---|
| **Rechazo de negocio largo** (después de intentar la acción) | "No se puede bajar el estado… emití la factura o gestioná el reembolso" | **VENTANA (roja)** | **SÍ** → a ventana |
| **Freno antes de una consecuencia** (confirmar) | "¿Seguro? va a quedar costo $0 para todos" | **VENTANA (ámbar)** | unifica pinta |
| Error corto de un campo | "Mínimo 10 caracteres", "Tiene que haber al menos 1 pasajero" | en línea, pegado al campo | no |
| Por qué un botón está apagado (no lo disparó un click) | chip "🔒 {motivo}" al lado del tacho | en línea, siempre visible | no |
| Éxito de una acción | "Guardado", "Reserva desbloqueada 30 min" | globito que se va solo (toast) | no |
| Guía de flujo / estado de la ficha | tira de avisos 2026-07-05, "Reserva anulada — solo lectura" | en línea, en la ficha | no |
| Ficha de trabajo (cargar servicio, cargar cobro, editar en fila) | ServiceInlineCard, cobro en línea | en línea, en la página | no |

En criollo: **la ventana es SOLO para "lo intentaste y te freno" o "lo intentaste y confirmá antes
de seguir".** No para explicar, no para avisar éxito, no para trabajar.

---

## 3. El patrón único: "Cartel emergente"

**Un solo componente reutilizable** ("Cartel emergente de aviso"), con la estética de las ventanas de
la casa que ya gustan (`EditAuthorizationModal` / `ConfirmModal`): fondo oscurecido, tarjeta centrada,
ícono arriba, título corto, el mensaje del motor **tal cual**, y los botones abajo. **La misma
ventana para todos los casos**; lo único que cambia es el color/ícono según la gravedad y qué
botones lleva.

### 3.1 Dos gravedades (una sola ventana, dos "trajes")

| Gravedad | Cuándo | Color | Ícono | Botones |
|---|---|---|---|---|
| **Bloqueo** | el motor rechazó la acción | rojo/rosa | ⛔ / círculo con "!" | primario = la acción para resolver (si existe) · secundario = **"Entendido"** |
| **Confirmación** | el motor pide confirmar una consecuencia antes de seguir | ámbar | ⚠ | primario = **"Sí, confirmar"** · secundario = **"Volver"** |

### 3.2 El mensaje se muestra TAL CUAL

El cuerpo de la ventana es el **texto del motor sin tocar** (respeta los saltos de línea). El front
**nunca** lo reescribe, ni lo resume, ni le agrega jerga. (Sigue la regla dura de siempre: el motor
manda el texto; la pantalla solo lo muestra.)

### 3.3 El botón de acción, cuando hay a dónde ir

Si el mensaje trae una salida (emitir la factura, ir a la reserva, ir a la cuenta del cliente), ese
botón va **adentro de la ventana**, como botón principal, y "Entendido" queda como secundario. Si no
hay salida, la ventana lleva solo **"Entendido"**. (El "a dónde va" cada botón lo define el motivo
real que devuelve el motor, igual que hoy — no se inventa.)

### 3.4 Comportamiento

- **Se abre** cuando el motor responde con el rechazo/confirmación (después del click).
- **Se cierra** con: el botón secundario ("Entendido"/"Volver"), la tecla Escape, o la "✕" de la
  esquina. Cerrar un **bloqueo** no hace nada más (solo reconoce). Cerrar una **confirmación** =
  "Volver" (no confirma nada).
- **Nunca se cierra al tocar afuera** (para que un click al costado no descarte sin querer un aviso
  importante ni dispare una confirmación).
- **Una a la vez.** Nunca se apilan dos ventanas ni una ventana sobre un cartel. Esto de paso arregla
  el problema conocido de `CancelarReservaInline`, donde el rechazo se apilaba con el banner de "hay
  factura": ahora el rechazo sale en ventana y el banner informativo queda solo, en su ficha.
- **El foco** arranca en el botón secundario (el más seguro: un Enter accidental no dispara la
  acción ni cierra a lo bruto), como ya hace la ventana "¿Seguro?" de anular.
- **Accesible**: es un diálogo de verdad (rol de diálogo, se puede manejar con teclado, Escape
  cierra), con su etiqueta para lector de pantalla.

### 3.5 Mockup ASCII

**Traje BLOQUEO (rojo), con salida:**
```
        ┌───────────────────────────────────────────────┐
        │                                          [ ✕ ] │
        │   ⛔  No se puede todavía                       │
        │                                                │
        │   No se puede bajar el estado de este          │
        │   servicio todavía: ya tiene pagos al          │
        │   operador y la reserva aún no tiene            │
        │   factura emitida. Emití la factura de          │
        │   venta o gestioná el reembolso al operador.   │
        │                                                │
        │              [ Entendido ]  [ Ir a facturar ▸] │
        └───────────────────────────────────────────────┘
                (fondo de la pantalla oscurecido detrás)
```

**Traje BLOQUEO (rojo), sin salida:**
```
        ┌───────────────────────────────────────────────┐
        │                                          [ ✕ ] │
        │   ⛔  No se puede todavía                       │
        │                                                │
        │   {mensaje largo del motor, tal cual}          │
        │                                                │
        │                            [    Entendido    ] │
        └───────────────────────────────────────────────┘
```

**Traje CONFIRMACIÓN (ámbar):**
```
        ┌───────────────────────────────────────────────┐
        │                                          [ ✕ ] │
        │   ⚠  Confirmá antes de guardar                 │
        │                                                │
        │   El costo nuevo ($ 45.000) queda por debajo   │
        │   de lo ya pagado al operador ($ 60.000).      │
        │   Van a quedar $ 15.000 a favor con este       │
        │   operador. ¿Confirmás?                        │
        │                                                │
        │                 [ Volver ]  [ Sí, confirmar ]  │
        └───────────────────────────────────────────────┘
```

---

## 4. Tabla de migración (cada aviso de hoy → cómo queda)

| Aviso de hoy | Hoy | Queda | Traje | Botón principal |
|---|---|---|---|---|
| 1. Bloqueo bajar estado (SupplierAccountPage) | recuadro rojo en la fila | **ventana** | bloqueo | "Ir a la reserva a facturar" |
| 2. Bloqueo bajar estado (ReservaDetailPage) | recuadro rojo + "Entendido" | **ventana** | bloqueo | solo "Entendido" |
| 3. Error al guardar servicio (`inline-card-error`) | recuadro rojo en la ficha | **ventana** | bloqueo | "Emitir factura" si el motivo lo trae; si no, "Entendido" |
| 4. Rechazo al anular (`cancelar-inline-conflict-msg`) | recuadro rojo en el bloque | **ventana** | bloqueo | "Entendido" |
| 5. Error deshacer/corregir reembolso | recuadro rojo | **ventana** | bloqueo | "Ir a la cuenta del cliente" si aplica; si no, "Entendido" |
| 6. Confirmar costo < pagado (`inline-card-confirmar-costo`) | recuadro ámbar en la ficha | **VER PREGUNTA P1** | confirmación | "Sí, confirmar" |
| "🔒 {motivo}" al lado del tacho | chip chico | **queda igual** (en línea) | — | — |
| Banner "hay factura" al anular | banner ámbar en la ficha | **queda igual** (en línea) | — | — |
| "¿Seguro? costo $0" | ventana propia (Swal) | **misma ventana** (unifica pinta) | confirmación | "Sí, confirmar" |
| "¿Seguro? Sí, anular" (multi-factura) | overlay propio | **misma ventana** (unifica pinta) | confirmación | "Sí, anular" |
| "¿Seguro?" de borrar/eliminar | `showConfirm` / `ConfirmModal` | **misma ventana** (unifica pinta) | confirmación / bloqueo-rojo si es destructivo | según el caso |

> Nota para frontend-senior: el objetivo NO es solo mover los rechazos a una ventana, es que **haya
> UNA sola ventana** en toda la app para estas cosas. Las ventanas que hoy ya frenan (costo $0,
> ¿seguro anular?, borrar/eliminar) migran a la MISMA pinta. Ese es el corazón del pedido de Gastón
> ("todos… tratarlos de la misma forma").

---

## 5. Qué NO hacer

- **No** convertir en ventana los cartelitos cortos de validación de un campo (siguen pegados al campo).
- **No** convertir en ventana el chip "🔒 {motivo}" del tacho (es un aviso permanente al lado del
  botón, no una respuesta a un click).
- **No** meter en ventana las fichas de trabajo (cargar servicio, cargar cobro, editar en fila) ni la
  tira de avisos de estado de la ficha (2026-07-05). Esas siguen en línea.
- **No** reescribir el mensaje del motor: va tal cual.
- **No** apilar dos ventanas ni una ventana sobre un cartel: siempre una sola.
- **No** cerrar la ventana al tocar el fondo.
- **No** inventar el "a dónde va" del botón: sale del motivo real que devuelve el motor.

---

## PREGUNTAS PARA GASTON

> **✅ RESPONDIDAS Y FIRMADAS por Gaston el 2026-07-22** (eligió la
> recomendada en las 3):
> **P1 = A** — el ámbar de "confirmar costo < pagado" TAMBIÉN va a la ventana
> (sin excepciones; PISA la spec P3 del 2026-07-22 en ese punto, que queda
> modificada) · **P2 = A** — el botón de acción ("Emitir factura", "Ir a la
> cuenta del cliente") va ADENTRO de la ventana, junto a "Entendido" ·
> **P3 = A** — con título corto genérico por tipo.
> Con esto la spec queda CERRADA para implementación.

### Tema: los avisos largos que hoy salen incrustados en la pantalla
Contexto: nos pediste que los mensajes largos e importantes salgan como una ventanita que se abre
encima, todas iguales, porque incrustados rompen la estética. Ya lo dejamos definido para los
carteles de **rechazo** ("no se puede, hacé esto"). Quedan tres bordes finos para que decidas vos.

---

**P1. El cartel ámbar de "confirmar costo por debajo de lo pagado" — ¿también va a la ventana?**

Este es distinto de un rechazo: NO te frena, te pregunta "¿seguro? van a quedar $X a favor con el
operador" y vos decidís antes de guardar. Hace pocos días (misma semana) elegiste que este puntual
fuera **en línea** dentro de la ficha, porque es algo que se puede deshacer y es parte de guardar. Tu
pedido de ahora es "todos estos mensajes, iguales y en ventana". Hay que decidir cuál manda para
ESTE caso.

  **A) También a la ventana (ámbar), como todo lo demás.** Una sola forma para todo; más parejo.
  *(recomendada — es lo más fiel a "todos tratarlos de la misma forma")*
```
    Bajás el costo y guardás →  se abre la ventana ámbar "¿Confirmás? quedan $X a favor" →
                                [ Volver ] [ Sí, confirmar ]
```

  **B) Este queda en línea, como lo decidiste antes.** La ventana es solo para los que te FRENAN
  ("no se puede"); los que solo piden confirmar-antes-de-guardar siguen dentro de la ficha.
```
    Bajás el costo →  aparece el cartelito ámbar DENTRO de la ficha (como hoy) →
                      [ Volver a corregir ] [ Sí, confirmar ]
```

---

**P2. Cuando el aviso tiene una salida ("Emití la factura", "Andá a la cuenta del cliente"), ¿el botón para ir va ADENTRO de la ventana?**

Contexto: algunos rechazos te ofrecen un camino para resolverlos (ir a facturar, ir a la cuenta del
cliente). Hay que decidir dónde queda ese botón.

  **A) El botón va adentro de la ventana**, al lado de "Entendido". Resolvés desde ahí mismo.
  *(recomendada — no te hace buscar después dónde estaba)*
```
        ┌──────────────────────────────────────┐
        │  ⛔  No se puede todavía               │
        │  {mensaje del motor}                  │
        │        [ Entendido ] [ Ir a facturar ▸]│
        └──────────────────────────────────────┘
```

  **B) La ventana solo dice "Entendido"** y el botón para ir queda donde estaba antes (en la fila / la
  ficha), después de cerrar la ventana.
```
        ┌──────────────────────────────────────┐
        │  ⛔  No se puede todavía               │
        │  {mensaje del motor}                  │
        │                        [ Entendido ]  │
        └──────────────────────────────────────┘
   (y el link "Ir a facturar" queda en la fila, como ahora)
```

---

**P3. El titulito corto de arriba de la ventana.**

Contexto: la ventana muestra el mensaje largo del motor tal cual. Arriba de ese texto puede ir (o no)
un título corto genérico, para que de un vistazo sepas si es un freno o una confirmación. El título
NO cuenta nada del caso (eso lo dice el mensaje): es solo una etiqueta de "qué tipo de aviso es".

  **A) Sí, título corto según el tipo**: "No se puede todavía" (rojo) / "Confirmá antes de seguir"
  (ámbar). *(recomendada — ayuda a leerlo rápido)*

  **B) Sin título**: solo el ícono (⛔ / ⚠) y abajo el mensaje del motor.

```
   A)  ⛔  No se puede todavía        │   B)   ⛔
       {mensaje del motor}           │        {mensaje del motor}
```

---

> **Aclaración de coherencia para el orquestador:** el patrón de **rechazos** (bloqueos rojos) ya
> queda cerrado por el feedback de Gastón (él nombró exactamente ese caso). Las tres preguntas son
> solo los bordes. En cuanto Gastón responda, actualizo `docs/ux/guia-ux-gaston.md` (sección
> "Ventanas emergentes y avisos") con la regla de corte + el patrón único, marcando que la parte de
> "confirmar costo < pagado" de la spec del 2026-07-22 queda **modificada** según lo que él elija en P1.
