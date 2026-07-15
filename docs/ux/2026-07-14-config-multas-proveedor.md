# Config de multas por operador + "Deshacer" del cierre sin multa más claro (2026-07-14)

> **Qué es esto:** especificación de pantalla para tres piezas de la misma tanda. Sale de las
> decisiones YA cerradas de Gastón (2026-07-14) sobre que el sistema no "asuma" que todo operador
> cobra multa, más lo que ya está escrito en `docs/ux/guia-ux-gaston.md`. Lo que la guía NO cubre
> quedó como **PREGUNTAS PARA GASTON** al final: nada de eso se implementa hasta que él responda.
>
> **Decisiones de negocio de Gastón que NO se reabren** (contexto, cerradas hoy):
> 1. Sus operadores son "mitad y mitad" (algunos estables, otros varían por tarifa).
> 2. La config **solo sugiere el camino**; NUNCA pre-completa montos, NUNCA decide sola, NUNCA saca el paso.
> 3. El dato vive en la **ficha del proveedor** (por dentro reusa el campo dormido `Supplier.PenaltyPolicyJson`).

---

## Panorama: qué toca cada pieza

| Pieza | Pantalla | Qué cambia | Estado |
|---|---|---|---|
| 1 | Ficha del operador → solapa **Datos** | Campo nuevo "comportamiento con multas" (3 valores, default = como hoy) | Patrón calcado + preguntas de detalle |
| 2 | Ficha de la reserva **Anulada** → paso de la multa | Pre-resaltar el camino sugerido según la ficha del operador | Decisión NUEVA → preguntas |
| 3 | Ficha de la reserva **Anulada** → cartel "cerrada sin multa" | Texto del "Deshacer" más claro; visibilidad admin (ya está) | Copy → preguntas |

---

## PIEZA 1 — El dato en la ficha del operador

### Qué está DECIDIDO (deriva de la guía + del patrón ya aprobado 2026-07-10)

La guía ya tiene un campo config hermano en la misma solapa: **"Ajuste por el dólar en sus multas"**
(2026-07-10, línea 1213 de la guía), que vive en la ficha del operador → solapa **Datos** → dentro de
**"Más detalles"** (cerrado por defecto), es un desplegable de 3 opciones con default invisible
"Como la configuración general", trae texto de ayuda de una línea, y su valor real se lee aparte
(GET `/suppliers/{id}`, no del overview). El campo nuevo se construye **con exactamente ese patrón**,
para que el implementador no invente nada.

- **Vive en:** ficha del operador → solapa **Datos** (`SupplierAccountPage.jsx`), NO en el modal de
  alta/edición viejo (`SupplierFormModal.jsx`).
- **Tres valores**, con el **default = "no se sabe / varía"**, que deja el paso de la multa
  **exactamente como hoy** (sin ninguna sugerencia). Todos los operadores existentes arrancan en ese
  default → cero cambio visible hasta que alguien lo toque.
- **No es un formulario pesado:** es UN solo desplegable. La complejidad se esconde con el default
  (regla dura: "contemplar todo con defaults, no con preguntas").
- **Enmascarado:** este campo NO expone montos ni costos; se ve con el mismo permiso que editar los
  datos del operador (igual que el campo del ajuste por el dólar).

### Layout (mockup ASCII) — recomendación, sujeta a P1/P2/P3

```
Ficha del operador: Turismo Cardozo
┌───────────────────────────────────────────────────────────────┐
│ [Cuenta corriente] [Deuda] [Servicios] [Reembolsos] [Bancarios]│
│ [ DATOS ]                                                      │
├───────────────────────────────────────────────────────────────┤
│  Datos del operador                                           │
│  Razón social  [ Turismo Cardozo S.A.        ]               │
│  CUIT          [ 30-12345678-9 ]  Cond. fiscal [ R.I. ▾ ]     │
│  Moneda por defecto [ Pesos ▾ ]                              │
│  Contacto [ ... ]  Teléfono [ ... ]                          │
│  Email    [ ... ]                                            │
│  Dirección[ ... ]                                            │
│  ☑ Operador activo                                           │
│ ─────────────────────────────────────────────────────────── │
│  ▸ Más detalles                                              │
│      Ajuste por el dólar en sus multas [ Como config gral ▾ ]│
│      ¿Suele cobrar multa cuando se anula? [ No se sabe ▾ ]  ← CAMPO NUEVO
│         (default "No se sabe / depende de la tarifa")        │
├───────────────────────────────────────────────────────────────┤
│                                        [ Guardar cambios ]    │
└───────────────────────────────────────────────────────────────┘
```

**Textos propuestos (a confirmar por Gastón — es su voz):**
- Etiqueta del campo: **"¿Suele cobrar multa cuando se anula?"**
- Opciones del desplegable:
  - `Casi nunca cobra multa`
  - `Casi siempre cobra multa`
  - `No se sabe / depende de la tarifa` **(default)**
- Línea de ayuda gris debajo: **"Esto solo resalta un camino cuando anulás. Nunca completa montos ni decide por vos."**

### Estados
- **Cargando** el valor real: desplegable deshabilitado, texto "Cargando el valor actual…" (igual que el hermano).
- **Error al traer el valor:** cartel + botón "Reintentar", y el "Guardar cambios" del form queda
  bloqueado hasta resolverlo (igual que el hermano — no se guarda a ciegas).
- **Guardado OK:** toast "Datos del operador guardados correctamente." (el que ya existe).

### Qué NO hacer
- NO ponerlo como campo grande arriba de todo (sería un dato de todos los días y no lo es).
- NO agregarlo al alta rápida del operador salvo que Gastón lo pida (ver P2).
- NO pre-completar nunca el monto de la multa desde acá.

---

## PIEZA 2 — La sugerencia en el paso de la multa

### Situación de partida (auditada en código)

En la reserva Anulada, el paso de la multa muestra la pregunta y **dos botones lado a lado**
(`ReservaDetailPage.jsx` ~1570):

```
¿El operador te cobró una multa por anular?
[ Sí, el operador cobró una multa ]   [ No cobró nada / devolvió todo ]
        (naranja)                              (verde/teal)
```
Orden actual: "Sí cobró" primero, "No cobró" segundo. Igual de resaltados.

### Qué está DECIDIDO
- La sugerencia **solo cambia el orden/énfasis y agrega una notita**. Los dos botones **siguen
  estando** y el otro camino queda **a un clic** (nunca se esconde, nunca se decide solo).
- Con el operador en **default ("no se sabe")** → **CERO cambio visual**: se ve igual que hoy.

### Comportamiento propuesto (sujeto a P4/P5/P6)

- **Operador "casi nunca cobra multa"** → el botón **"No cobró nada / devolvió todo"** pasa a
  **primero** y queda **resaltado**; arriba de los botones, una notita gris chica.
- **Operador "casi siempre cobra multa"** → el botón **"Sí, el operador cobró una multa"** queda
  primero y resaltado; su propia notita.
- **Operador "no se sabe"** → sin notita, orden de hoy, ambos iguales.

```
Operador "casi nunca cobra multa":
┌───────────────────────────────────────────────────────────┐
│ ¿El operador te cobró una multa por anular?               │
│ 💡 Este operador casi nunca cobra multa (según su ficha). │  ← notita
│ [ No cobró nada / devolvió todo ]  [ Sí, cobró una multa ]│  ← "No" primero + resaltado
│         (resaltado)                     (normal)          │
└───────────────────────────────────────────────────────────┘

Operador "casi siempre cobra multa":
┌───────────────────────────────────────────────────────────┐
│ ¿El operador te cobró una multa por anular?               │
│ 💡 Este operador casi siempre cobra multa (según su ficha)│
│ [ Sí, el operador cobró una multa ]  [ No cobró / devolvió]│  ← "Sí" resaltado (ya iba primero)
└───────────────────────────────────────────────────────────┘

Operador "no se sabe" (DEFAULT, = HOY):
┌───────────────────────────────────────────────────────────┐
│ ¿El operador te cobró una multa por anular?               │
│ [ Sí, el operador cobró una multa ]  [ No cobró / devolvió]│
└───────────────────────────────────────────────────────────┘
```

### Multi-operador (ADR-044 T1)
Cuando la anulación tiene varios operadores, hay un paso de multa POR operador (cada uno con su
título "Nombre del operador — ..."). La sugerencia se calcula **por operador**: cada bloque mira la
ficha de SU operador. Uno puede sugerir "no cobró" y otro "sí cobró" sin problema.

### Qué NO hacer
- NO deshabilitar ni esconder el camino no sugerido.
- NO pre-abrir ningún panel (ni el de cargar monto ni el de cerrar sin multa): la sugerencia
  resalta, no ejecuta.
- NO mostrar la notita cuando el operador está en "no se sabe".

---

## PIEZA 3 — El "Deshacer" del cierre sin multa: más claro, admin-only

### Hallazgo importante (verificado en código)
El "Deshacer" del cierre sin multa **YA es admin-only**: en `ReservaDetailPage.jsx` (línea 1450) el
enlace está dentro de `{isAdmin() && ...}`, y la guía (2026-07-04, líneas 982 y 995-996) ya dice
"solo para administradores... ya estaba así; se conserva". El panel que se abre
(`DeshacerCierreSinMultaInline`) también está documentado como admin-only.

**Conclusión:** la parte "pasa a verse solo para administradores" **ya está hecha**. Gastón lo vio
porque **él es administrador** (es el único usuario). Lo que queda por resolver es la **claridad del
texto** — que hoy no dice qué se deshace ni tranquiliza sobre los comprobantes.

### Copy actual (auditado)
- Enlace en el cartel rosa "cerrada sin multa": **"· Deshacer: el operador sí cobró una multa"**
- Cabecera del panel: **"Deshacer el cierre sin multa"**
- Explicación del panel: "Esto reabre el paso de la multa del operador. Vas a poder volver a elegir
  entre cargar la multa o cerrar sin multa otra vez."
- Confirmación (2do paso): "Esto reabre el paso de la multa de la reserva {n}. Vas a poder cargar la
  multa o cerrar sin multa otra vez."

### El riesgo de confusión con el OTRO "Deshacer" (ADR-044)
En una reserva puede aparecer, en otro estado, el "Deshacer" de la multa YA emitida (2026-07-14):
**"· Deshacer: el operador cobró mal esta multa"** (cartel verde). Son dos cosas distintas:

| | "Deshacer" del cierre SIN multa (Pieza 3) | "Deshacer" de la multa YA emitida (ADR-044) |
|---|---|---|
| Cuándo aparece | Cartel **rosa** "cerrada sin multa" | Cartel **verde** "multa confirmada" |
| Qué pasó | Se cerró SIN cobrar nada (nunca hubo comprobante) | Se cobró una multa con comprobante ARCA |
| Qué hace | Reabre el paso; **no toca comprobantes** (no hay) | Emite el comprobante inverso + reabre el paso |
| Texto hoy | "el operador sí cobró una multa" | "el operador cobró mal esta multa" |

Los dos son admin-only y casi nunca conviven (son estados distintos del mismo operador). Con
multi-operador podrían verse a la vez, uno por operador, cada uno bajo el nombre de su operador.

### Qué está DECIDIDO
- El "Deshacer" del cierre sin multa **se queda admin-only** (ya lo está).
- El **texto** debe dejar claro que **reabre el paso de la multa** y que **no toca ningún
  comprobante** (en este caso nunca hubo uno).

### Copy PROPUESTO (sujeto a P7/P8)
- Enlace: **"· Reabrir el paso de la multa"** (en vez de "Deshacer: el operador sí cobró una multa")
  — dice qué hace, no adivina el motivo.
- Cabecera del panel: **"Reabrir el paso de la multa"**.
- Explicación (arriba del motivo): **"Volvés a la pregunta '¿el operador cobró una multa?'. No se
  toca ningún comprobante: este cierre nunca emitió ninguno."**
- Todo lo demás del panel (motivo obligatorio 5–500, confirmación en dos pasos, error inline con
  datos intactos, bloqueo si el saldo ya se usó) **se conserva tal cual**.

### Qué NO hacer
- NO tocar el flujo ni los estados: es solo copy + confirmar la visibilidad ya existente.
- NO unificar los dos "Deshacer" en un mismo texto: son acciones fiscalmente distintas.

---

## PREGUNTAS PARA GASTON

> Se relevan tal cual: cada respuesta se vuelve regla en la guía. Podés responder "1A, 2B, 3 otra
> cosa: ...". Todo lo de arriba marcado "propuesto/sujeto a Pn" espera estas respuestas.

### Tema: El dato nuevo en la ficha del operador (Pieza 1)
Contexto: querés un campito en la ficha del operador que diga cómo se porta con las multas, para que
después el sistema resalte un camino cuando anulás. Arranca en "no se sabe" para todos, así nada
cambia hasta que vos lo toques.

**P1. ¿Dónde ponemos el campito dentro de la solapa "Datos" del operador?**
  A) Escondido dentro de "Más detalles" (igual que el del ajuste por el dólar), porque no es un dato
     de todos los días.
```
     ▸ Más detalles
         Ajuste por el dólar en sus multas [ ... ▾ ]
         ¿Suele cobrar multa cuando se anula? [ No se sabe ▾ ]
```
  B) A la vista, como un campo más (abajo de "Moneda por defecto"), porque es info útil del operador.
```
     Moneda por defecto [ Pesos ▾ ]
     ¿Suele cobrar multa cuando se anula? [ No se sabe ▾ ]
     ▸ Más detalles ...
```

**P2. Cuando das de alta un operador nuevo, ¿aparece este campito o no?**
  A) NO: en el alta va lo mínimo; este campo se carga después, editando el operador. (Arranca en
     "no se sabe".)
  B) SÍ: que aparezca también en el alta, así lo dejás listo de una.

**P3. ¿Te cierran estas tres opciones y estos textos, o los decís con otras palabras?**
  A) Me cierran tal cual:
```
     ¿Suele cobrar multa cuando se anula?
       ( ) Casi nunca cobra multa
       ( ) Casi siempre cobra multa
       ( ) No se sabe / depende de la tarifa   ← default
```
  B) Otra cosa (contame cómo lo dirías: la etiqueta y cada opción).

### Tema: Cómo se ve la sugerencia al anular (Pieza 2)
Contexto: cuando anulás y el operador está marcado "casi nunca cobra" o "casi siempre cobra", el
sistema resalta el camino más probable. El otro camino sigue a un clic; nunca decide solo.

**P4. ¿Cómo resaltamos el camino sugerido?**
  A) Lo pongo PRIMERO y con color más fuerte + una notita arriba:
```
     💡 Este operador casi nunca cobra multa (según su ficha).
     [ NO cobró nada / devolvió todo ]   [ Sí, cobró una multa ]
              (resaltado)                       (normal)
```
  B) Dejo los botones en el mismo orden de siempre y solo agrego la notita (sin mover nada):
```
     💡 Este operador casi nunca cobra multa (según su ficha).
     [ Sí, cobró una multa ]   [ No cobró nada / devolvió todo ]
```
  C) Solo muevo el sugerido a primero, SIN notita (más limpio, sin texto):
```
     [ NO cobró nada / devolvió todo ]   [ Sí, cobró una multa ]
```

**P5. La notita, ¿cuánto cuenta?**
  A) Dice de dónde salió: "Este operador casi nunca cobra multa (según su ficha)."
  B) Más corta, sin mencionar la ficha: "Este operador casi nunca suele cobrar multa."
  C) Otra cosa (contame).

**P6. Si el operador está en "no se sabe" (el default), confirmás que NO aparece ninguna notita ni
se mueve nada, ¿sí?**
  A) Sí, en "no se sabe" se ve igual que hoy (sin sugerencia).
  B) No, quiero que igual muestre algo (contame qué).

### Tema: El "Deshacer" del cierre sin multa (Pieza 3)
Contexto: cerraste un paso "sin multa" y te confundió que todavía se pudiera deshacer, y que un
vendedor lo viera. **Dato:** ese "Deshacer" YA solo lo ven los administradores (vos lo viste porque
sos admin). Lo que sí conviene arreglar es que el texto deje claro qué hace.

**P7. El enlace de hoy dice "· Deshacer: el operador sí cobró una multa". ¿Lo cambiamos a algo que
diga qué hace?**
  A) "· Reabrir el paso de la multa"
```
     Anulada — cerrada sin multa del operador. Solo lectura.
     · Reabrir el paso de la multa        (solo lo ve un admin)
```
  B) "· Deshacer el cierre sin multa"
  C) Dejarlo como está ("· Deshacer: el operador sí cobró una multa").
  D) Otra cosa (contame).

**P8. Cuando abrís ese "Deshacer", ¿sumamos una línea que aclare que no se toca ningún comprobante?**
  A) Sí: "Volvés a la pregunta '¿el operador cobró una multa?'. No se toca ningún comprobante: este
     cierre nunca emitió ninguno."
  B) No hace falta, con "reabre el paso de la multa" alcanza.
  C) Otra cosa (contame).

**P9. En una anulación con varios operadores podrían verse a la vez el "Deshacer" del cierre sin
multa (cartel rosa) y el "Deshacer" de una multa ya cobrada (cartel verde), uno por operador.
¿Los dejamos con textos distintos para que no se confundan?**
  A) Sí, distintos:
```
     · Reabrir el paso de la multa            (cartel rosa: se cerró sin cobrar)
     · Deshacer: el operador cobró mal esta multa   (cartel verde: se cobró con comprobante)
```
  B) Que digan lo mismo los dos (contame cómo).
  C) No me preocupa, casi nunca pasa; dejalo como salga.

---

# SPEC APROBADA PARA IMPLEMENTAR (Gastón, 2026-07-14 — todas las recomendadas)

> Gastón aprobó P1..P9 = A. Esta sección es la única fuente para `frontend-senior`. Cualquier desvío
> por costo técnico o regla de negocio se le repregunta a Gastón ANTES; nunca se decide solo.

## PIEZA 1 — Campo "comportamiento con multas" en la ficha del operador

**Ubicación:** `SupplierAccountPage.jsx` → solapa **"Datos"** → dentro de **"Más detalles"** (cerrado
por defecto), DEBAJO del campo existente "Ajuste por el dólar en sus multas". NO se toca el modal
viejo `SupplierFormModal.jsx`. NO aparece en el alta del operador (`NuevoOperadorInline`).

**Molde exacto:** el del campo `treasuryFxAssumedByOverride` (2026-07-10): valor real cargado aparte
(GET `/suppliers/{id}`, no del overview); mientras carga → desplegable deshabilitado + "Cargando el
valor actual…"; si la carga falla → cartel de error + botón "Reintentar" + "Guardar cambios"
bloqueado (no se guarda a ciegas). Por dentro reusa `Supplier.PenaltyPolicyJson`.

**Textos finales:**
- Etiqueta: `¿Suele cobrar multa cuando se anula?`
- Desplegable (3 opciones):
  - `Casi nunca cobra multa`
  - `Casi siempre cobra multa`
  - `No se sabe / depende de la tarifa`  ← **DEFAULT** (valor con el que arranca todo operador)
- Línea de ayuda gris: `Esto solo resalta un camino cuando anulás. Nunca completa montos ni decide por vos.`

**Layout final:**
```
▸ Más detalles
    Ajuste por el dólar en sus multas   [ Como la config general ▾ ]
    ¿Suele cobrar multa cuando se anula? [ No se sabe / depende de la tarifa ▾ ]
       Esto solo resalta un camino cuando anulás. Nunca completa montos ni decide por vos.
```

**Visibilidad / permisos:** mismo permiso que editar los datos del operador. No expone montos.

**Qué NO hacer:** no ponerlo a la vista fuera de "Más detalles"; no meterlo en el alta; no
pre-completar montos de multa desde acá.

## PIEZA 2 — Sugerencia en el paso de la multa (reserva Anulada)

**Ubicación:** `ReservaDetailPage.jsx`, bloque de la pregunta "¿El operador te cobró una multa por
anular?" (~1552–1596), POR CADA situación de operador. El "camino sugerido" lo entrega el backend por
situación (el front no re-deriva).

**Regla por valor del operador:**

| Valor en la ficha | Orden de los botones | Botón resaltado | Notita |
|---|---|---|---|
| Casi nunca cobra multa | **"No cobró nada / devolvió todo"** primero, luego "Sí, cobró una multa" | el de "No cobró" | `💡 Este operador casi nunca cobra multa (según su ficha).` |
| Casi siempre cobra multa | "Sí, el operador cobró una multa" primero (como hoy), luego "No cobró" | el de "Sí cobró" | `💡 Este operador casi siempre cobra multa (según su ficha).` |
| No se sabe / depende de la tarifa **(default)** | Orden de HOY (Sí, luego No) | ninguno (como hoy) | ninguna |

**Mockups finales:**
```
Casi nunca cobra multa:
  ¿El operador te cobró una multa por anular?
  💡 Este operador casi nunca cobra multa (según su ficha).
  [ No cobró nada / devolvió todo ]   [ Sí, el operador cobró una multa ]
          (resaltado)                          (normal)

Casi siempre cobra multa:
  ¿El operador te cobró una multa por anular?
  💡 Este operador casi siempre cobra multa (según su ficha).
  [ Sí, el operador cobró una multa ]   [ No cobró nada / devolvió todo ]
          (resaltado)                          (normal)

No se sabe (DEFAULT = igual que hoy):
  ¿El operador te cobró una multa por anular?
  [ Sí, el operador cobró una multa ]   [ No cobró nada / devolvió todo ]
```

**Multi-operador (ADR-044 T1):** cada bloque de operador calcula su propia sugerencia; pueden diferir.

**Qué NO hacer:** no deshabilitar ni esconder el camino no sugerido; no pre-abrir ningún panel; no
mostrar notita ni reordenar cuando el operador está en "No se sabe".

## PIEZA 3 — "Deshacer" del cierre sin multa: texto más claro

**Ubicación:** `ReservaDetailPage.jsx` (cartel rosa "Anulada — cerrada sin multa del operador",
~1421–1465) y `DeshacerCierreSinMultaInline.jsx`.

**Visibilidad:** admin-only, **YA implementada** (gate `isAdmin()` línea 1450) — no se cambia, se
confirma. El resto del flujo del panel (motivo obligatorio 5–500, confirmación en dos pasos, error
inline con datos intactos, bloqueo `SALDO_YA_USADO`) **se conserva tal cual**.

**Cambios de texto (lo único que se toca):**
- Enlace en el cartel rosa: de `· Deshacer: el operador sí cobró una multa` → **`· Reabrir el paso de la multa`**.
- Cabecera del panel: de `Deshacer el cierre sin multa` → **`Reabrir el paso de la multa`**.
- Explicación dentro del panel (reemplaza la actual): **`Volvés a la pregunta '¿el operador cobró una multa?'. No se toca ningún comprobante: este cierre nunca emitió ninguno.`**
  - Aplica en las dos vistas del panel (la explicación inicial y la confirmación del 2do paso, con la
    misma idea; el texto de la confirmación puede conservar la mención a la reserva).

**Distinción con el otro "Deshacer" (ADR-044, multa YA emitida):** el de la multa emitida (cartel
verde) **queda como está**: `· Deshacer: el operador cobró mal esta multa`. Los dos textos distintos
conviven sin confundirse en multi-operador.

**Layout final del cartel rosa:**
```
Anulada — cerrada sin multa del operador. Solo lectura.
Cerrada sin multa el 12/07 por Gastón — motivo: ...        (rastro, como hoy)
· Reabrir el paso de la multa                              (solo lo ve un admin)
```

**Qué NO hacer:** no tocar estados ni flujo (es solo copy + confirmar visibilidad); no unificar los
dos "Deshacer".
