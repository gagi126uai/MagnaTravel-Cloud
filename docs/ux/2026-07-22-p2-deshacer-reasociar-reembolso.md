# Especificación UX — Tanda P2 del circuito proveedor: DESHACER y CORREGIR un reembolso del operador

**Fecha:** 2026-07-22
**Autor:** agente `ux-ui-disenador`
**Pantalla:** ficha del operador (`SupplierAccountPage.jsx`), solapa **"Reembolsos"**
**Permiso de la solapa:** `tesoreria.supplier_payments` (ya vigente). **Permiso de las acciones nuevas:** `caja.edit` (es el que exigen los dos endpoints del motor).
**Estado:** spec entregada con 4 preguntas abiertas para Gastón (ver el bloque final). Lo que la guía cubre se especifica y se cita; lo que no cubre queda como pregunta con recomendación. **Nada de esto se implementa hasta que Gastón responda P1..P4.**

---

## 1. De qué se trata, en criollo

Cuando se anula una reserva que ya tenía plata pagada al operador, el operador queda debiendo devolver esa plata. Cuando el operador la devuelve, alguien de back-office aprieta **"Registrar reembolso recibido"** (en la solapa "Cuenta corriente" del operador) y anota: cuánto, en qué moneda, y a qué reserva anulada corresponde.

**El problema (confirmado leyendo el código, no inferido):** si esa anotación se hace mal — monto equivocado, moneda equivocada, o imputada a la reserva equivocada del desplegable — **hoy no existe ningún botón en ninguna pantalla para arreglarlo.** El motor para arreglarlo ya está construido y probado en el backend (`DELETE .../allocations/{id}` para deshacer, `PATCH .../allocations/{id}/reassociate` para moverlo a otra reserva), pero la pantalla nunca lo llama. Esta tanda conecta ese cable que falta.

Dos acciones nuevas, las dos por reembolso ya registrado:
- **Deshacer** — borra el reembolso mal cargado (queda tachado, con quién y por qué; libera la plata para volver a cargarla bien).
- **Corregir reserva** — mueve el reembolso de la reserva equivocada a la correcta, sin borrarlo.

---

## 2. Qué de esto YA está decidido por la guía (no se pregunta)

Estas piezas salen directo de la guía de Gastón y del código existente. Se especifican, no se preguntan:

| Decisión | Fuente en la guía |
|---|---|
| Vive en la solapa **"Reembolsos"** de la ficha del operador (no en una bandeja global) | 2026-07-03, decisión 5 / P1=C: "los reembolsos se ven operador por operador, en la solapa Reembolsos de cada ficha". |
| **Todo EN LÍNEA** (la ficha de deshacer / corregir se abre **debajo** de la fila, dentro de la misma página). **Nunca ventana flotante.** | Regla dura "el modal me parece horrible" (2026-06-09 P3 / ADR-035 C). Precedente idéntico: `FormReembolsoTardio` dentro de esta misma solapa. |
| **Motivo obligatorio** al deshacer y al corregir, escrito en un casillero en línea, con **contador de caracteres** que habilita el botón recién cuando alcanza el mínimo | Precedente `FormReembolsoTardio` (mismo componente, mismo patrón: textarea + "N / X caracteres mínimos" + Confirmar deshabilitado hasta llegar). El motor exige **mínimo 20 caracteres** en los dos casos (`VoidAllocationRequest` / `ReassociateAllocationRequest`, `MinLength(20)`). |
| **El mensaje de rechazo del motor se muestra tal cual**, nunca se inventa ni se reescribe en el front | Patrón `getApiErrorMessage` ya usado en toda la solapa. Los mensajes ya vienen en criollo desde el controller (`OperatorRefundsController.cs:298-318, 357-376`). |
| **Plata y fechas en formato es-AR** ($ 1.800,00 · 22/07/2026), pesos y dólares siempre separados, nunca sumados | Reglas duras multimoneda (2026-06-09) + `formatDate`. |
| **Sin permiso de ver costos (`cobranzas.see_cost`): montos en "—"**, con un aviso único por pantalla | 2026-07-03 P6=A + regla general de enmascarado (2026-06-05). |
| Cada fila muestra el **número de reserva visible** y el nombre del cliente, nunca códigos internos ni GUIDs | 2026-07-01 P4 + gate de exposición de datos. |
| **Nada de cartelitos aclaratorios de más**; el casillero de motivo lleva una etiqueta clara y basta | Regla general 2026-06-05 (una línea corta de contexto es aceptable acá por ser acción fiscal rara, mismo criterio que el texto de `FormReembolsoTardio`). |

---

## 3. Dependencias de backend (NO son decisiones de UX, pero bloquean la construcción)

El agente frontend NO puede construir esto sin que backend provea primero:

1. **Falta un endpoint que LISTE los reembolsos YA REGISTRADOS de un operador** (el historial, no solo los pendientes). Hoy la solapa solo tiene `GET /suppliers/{id}/operator-refunds/pending`, que lista lo que el operador **todavía debe**. Para poder deshacer/corregir necesitamos ver lo que **ya se anotó como recibido**. El endpoint nuevo debe devolver, por cada reembolso registrado (allocation): número de reserva, cliente, moneda, monto, fecha en que se registró, quién lo registró, y si ya está deshecho (`IsVoided` + `VoidedReason`/`VoidedAt`). Todo enmascarado por `cobranzas.see_cost`.
2. **Agregar a `operatorRefundsApi.js` los dos wrappers que faltan:** `voidAllocation(allocationPublicId, reason)` → `DELETE /operator-refunds/allocations/{id}` con body `{ reason }`; y `reassociateAllocation(allocationPublicId, { newBookingCancellationPublicId, reason })` → `PATCH /operator-refunds/allocations/{id}/reassociate`.
3. ~~Arreglar el mensaje con jerga del motor ANTES de conectar el botón "Deshacer".~~ **YA RESUELTO (2026-07-21, mismo día, después de escribir esta spec):** los mensajes del motor ya están en criollo y pasaron el gate de exposición de datos con re-review verde. Hoy dicen: *"No se puede anular este reembolso: el saldo a favor que generó ya fue retirado o aplicado por el cliente. Para deshacerlo primero hay que revertir ese uso del saldo, y eso requiere autorización."* (ídem para reasociar), *"Este reembolso ya estaba deshecho. No hace falta volver a deshacerlo."* y *"Este reembolso está anulado, así que no se puede mover a otra reserva."*. **P4 sigue vigente** pero solo para decidir si además del cartel se ofrece un botón que lleve a algún lado — el texto ya no es bloqueante.

---

## 4. Layout de la solapa "Reembolsos" (con las dos acciones nuevas)

> La forma exacta de mostrar los **ya registrados** depende de **P1**. Acá se dibuja la opción recomendada (A: dos bloques apilados). Si Gastón elige otra, cambia solo este dibujo, no el resto de la spec.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│  Reembolsos a cobrar del operador                          [ ↻ Actualizar ]    │
│  Cancelaciones donde el operador tiene que devolver plata. Los montos son      │
│  estimados, sujetos a las deducciones del operador al momento de pagar.        │
├──────────────────────────────────────────────────────────────────────────────┤
│  Reserva #1042  [Por vencer]   Cliente: Fam. García   Vence: 25/07/2026        │
│     USD  Pagaste US$ 500 − Multa del operador US$ 100 = te devuelven US$ 400…  │
│  ─────────────────────────────────────────────────────────────────────────    │
│  Reserva #1051  [Vencido]      Cliente: Pérez         3 días vencido           │
│     ...                                                       [ Reembolso tardío ]│
└──────────────────────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────────────────────┐
│  Reembolsos ya registrados                                                     │
│  Lo que ya anotaste como devuelto por este operador.                           │
├──────────────────────────────────────────────────────────────────────────────┤
│  Reserva #1042 · Fam. García        USD   US$ 400,00     22/07/2026            │
│                                              [ Deshacer ] [ Corregir reserva ] │
│  ─────────────────────────────────────────────────────────────────────────    │
│  Reserva #1039 · López              $     $ 85.000,00    18/07/2026            │
│                                              [ Deshacer ] [ Corregir reserva ] │
│  ─────────────────────────────────────────────────────────────────────────    │
│  Reserva #1030 · Gómez        DESHECHO  US$ 200,00       12/07/2026 (tachado)  │
│     Deshecho el 15/07/2026 — "monto mal tipeado, iba US$ 250"                  │
└──────────────────────────────────────────────────────────────────────────────┘
```

- El bloque de arriba ("a cobrar") es **exactamente el que ya existe** (`OperatorRefundsPendingSection`). No se toca.
- El bloque de abajo ("ya registrados") es **nuevo**. Una fila por reembolso registrado. Cada fila: `Reserva #N · Cliente`, moneda, monto (es-AR), fecha en que se registró.
- **Los dos botones ("Deshacer" y "Corregir reserva") llevan la palabra al lado del ícono, siempre visible** (regla 2026-06-08), y **solo aparecen si el usuario tiene `caja.edit`**. Sin ese permiso, la fila se ve pero sin botones (solo lectura).
- Una fila **ya deshecha** se muestra **tachada / gris**, con la etiqueta **"Deshecho"**, la fecha y el motivo entre comillas. **No** tiene botones (ya no hay nada que hacer). No desaparece: queda como rastro auditable.
- Sin `cobranzas.see_cost`: la columna de monto va **"—"** y arriba del bloque, el aviso único **"No tenés permiso para ver los montos."** Los botones **igual funcionan** (deshacer/corregir no requieren ver el monto, requieren `caja.edit`).

---

## 5. Flujo "Deshacer" (motivo obligatorio, en línea)

Al apretar **"Deshacer"** en una fila, se abre **debajo de esa fila** una ficha en línea (mismo molde visual que `FormReembolsoTardio`):

```
┌── Deshacer este reembolso ─────────────────────────────────────────────────┐
│  Vas a deshacer el reembolso de US$ 400,00 imputado a la reserva #1042.     │
│  La plata vuelve a quedar como pendiente de este operador y vas a poder     │
│  registrarla de nuevo, bien, cuando quieras.                                │
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────┐     │
│  │ Contá por qué lo deshacés (mínimo 20 caracteres)…                  │     │
│  └───────────────────────────────────────────────────────────────────┘     │
│  12 / 20 caracteres mínimos                                                 │
│                                                                             │
│                                       [ Cancelar ]  [ Deshacer reembolso ]  │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Campo motivo:** textarea, `aria-label` claro, **obligatorio, mínimo 20 caracteres** (lo exige el motor: `VoidAllocationRequest.Reason MinLength(20)`). El front valida el mínimo antes de llamar y muestra el **contador** "N / 20 caracteres mínimos" en ámbar hasta llegar, gris después. El botón **"Deshacer reembolso"** queda **apagado** hasta que el motivo alcanza el mínimo.
- **La línea de contexto** ("Vas a deshacer el reembolso de US$ 400,00 imputado a la reserva #1042…") es corta y concreta: dice qué plata y qué reserva se tocan, en la moneda real. Si el usuario no ve costos, dice "Vas a deshacer el reembolso imputado a la reserva #1042." (sin el monto).
- **Estados:**
  - **Enviando:** botón muestra "Deshaciendo…" y queda deshabilitado (anti doble-click).
  - **Éxito:** la ficha se cierra, la solapa se recarga (la fila pasa a estado "Deshecho" tachado, y el reembolso reaparece como pendiente arriba). Toast: **"Reembolso deshecho. La plata quedó pendiente de nuevo."**
  - **Rechazo del motor:** la ficha **queda abierta con el motivo escrito intacto** + cartel rojo arriba de los botones, con **el mensaje tal cual lo devuelve el backend** (nunca inventado). Reintenta en el mismo botón. Ejemplo real ya en criollo: *"Este reembolso ya estaba deshecho…"*. El caso "el cliente ya usó esa plata" también tiene ya su mensaje en criollo (arreglado 2026-07-21); **P4** solo decide si además se ofrece un botón que lleve a algún lado.
- **¿Lleva un "¿Seguro?" extra además del motivo?** → depende de **P2**.

---

## 6. Flujo "Corregir reserva" (mover a la reserva correcta)

Al apretar **"Corregir reserva"** en una fila, se abre **debajo de esa fila** una ficha en línea con dos partes: elegir la reserva destino + el motivo.

```
┌── Corregir a qué reserva va este reembolso ────────────────────────────────┐
│  Hoy este reembolso de US$ 400,00 está imputado a la reserva #1042.         │
│  Elegí la reserva correcta (solo aparecen las anulaciones de este operador  │
│  que esperan un reembolso en dólares):                                      │
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────┐     │
│  │ ( ) Reserva #1051 · Pérez        te devuelven US$ 400 (estimado)   │     │
│  │ (•) Reserva #1058 · Ruiz         te devuelven US$ 400 (estimado)   │     │
│  └───────────────────────────────────────────────────────────────────┘     │
│                                                                             │
│  ┌───────────────────────────────────────────────────────────────────┐     │
│  │ Contá por qué lo corregís (mínimo 20 caracteres)…                  │     │
│  └───────────────────────────────────────────────────────────────────┘     │
│  9 / 20 caracteres mínimos                                                  │
│                                                                             │
│                                     [ Cancelar ]  [ Mover a la reserva #1058 ]│
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Selector de reserva destino:** lista de opciones (una por anulación) con el mismo molde del selector que YA existe en `RegistrarReembolsoRecibidoInline` (listbox en línea, una fila = una anulación). **Filtrada a: anulaciones de ESTE operador, en la MISMA moneda del reembolso, excluyendo la reserva actual.** Se elige UNA obligatoriamente. → estilo exacto del picker: ver **P3**.
- **Campo motivo:** idéntico al de Deshacer — obligatorio, mínimo 20 caracteres (`ReassociateAllocationRequest.Reason MinLength(20)`), con contador, botón apagado hasta llegar.
- **El botón dice la reserva destino elegida:** "Mover a la reserva #1058" (más claro que un "Confirmar" pelado). Apagado hasta que haya reserva elegida **Y** motivo válido.
- **Estados:**
  - **Sin reservas destino elegibles** (no hay otra anulación del operador en esa moneda esperando reembolso): el selector muestra el cartel neutro **"No hay otra reserva anulada de este operador esperando un reembolso en esta moneda."** y no se puede continuar (solo queda "Cancelar"). En ese caso, la corrección correcta es **Deshacer** y volver a cargar — el cartel puede sugerirlo con una línea.
  - **Éxito:** ficha se cierra, solapa se recarga (la fila ahora muestra la reserva nueva). Toast: **"Listo. El reembolso ahora está en la reserva #1058."**
  - **Rechazo del motor:** ficha queda abierta con lo cargado intacto + mensaje del backend tal cual (ej. moneda que no coincide, o el caso "ya consumido" de **P4**).

---

## 7. Todos los estados de la solapa (checklist para frontend)

| Estado | Qué se ve |
|---|---|
| **Cargando** | "Cargando reembolsos…" en cada bloque. |
| **Vacío — a cobrar** | "No hay reembolsos pendientes del operador. Todo al día." (ya existe). |
| **Vacío — ya registrados** | "Todavía no registraste ningún reembolso de este operador." |
| **Error de carga** | "No se pudo cargar la información. Intentá de nuevo." + botón Reintentar (ya existe). |
| **Sin `caja.edit`** | Filas visibles, **sin** botones Deshacer/Corregir. |
| **Sin `cobranzas.see_cost`** | Montos "—" + aviso único; botones igual funcionan. |
| **Fila deshecha** | Tachada/gris, etiqueta "Deshecho", fecha y motivo; sin botones. |
| **Éxito deshacer / corregir** | Toast + recarga automática de la solapa (los dos bloques quedan consistentes con "Me tiene que devolver" del encabezado — regla de coherencia 2026-07-01 P2). |

---

## 8. Qué NO hay que hacer

- **No** abrir Deshacer ni Corregir en una ventana flotante. Todo en línea, debajo de la fila (regla dura).
- **No** reescribir ni "mejorar" el mensaje de rechazo del motor en el front: se muestra tal cual (`getApiErrorMessage`). Si el mensaje trae jerga, **eso se arregla en el backend** (dependencia 3 / P4), no tapándolo en la pantalla.
- **No** dejar deshacer/corregir sin motivo, ni con menos de 20 caracteres: el botón queda apagado.
- **No** borrar la fila deshecha de la lista: queda como rastro tachado (auditoría).
- **No** mostrar GUIDs, ni nombres de campo internos, ni el número de "allocation": la fila se identifica por **número de reserva + cliente**.
- **No** sumar pesos y dólares en ningún total; cada reembolso va en su moneda.
- **Fuera de alcance de esta tanda** (no tocar acá): agregar un paso de "revisá antes de guardar" al alta de reembolso (`RegistrarReembolsoRecibidoInline`). Es una mejora relacionada (el "agravante #8" del inventario) pero es otra pieza; si Gastón la quiere, va en su propia mini-tanda con su propia pregunta.

---

## PREGUNTAS PARA GASTON

> **✅ RESPONDIDAS Y FIRMADAS por Gaston el 2026-07-21** (eligió la recomendación en las 4):
> **P1 = A** (bloque aparte "Reembolsos ya registrados" debajo del de "a cobrar") ·
> **P2 = A** (alcanza el motivo obligatorio ≥20, sin "¿Seguro?" extra) ·
> **P3 = A** (lista filtrada: solo anulaciones del mismo operador en la misma moneda) ·
> **P4 = B** con botón **"Ir a la cuenta del cliente"** (cartel que explica + botón directo; la bandeja de Cobranzas queda para más adelante si aparece el caso gemelo).
> Con esto la spec queda CERRADA para implementación.

> Todo lo demás de esta pantalla ya está cubierto por tus decisiones anteriores (ver sección 2) y no se pregunta. Estas 4 eran las únicas que la guía no respondía.

### Tema: cómo se ven los reembolsos que ya anotaste
Contexto: hoy la solapa "Reembolsos" solo muestra lo que el operador **todavía te debe**. Para poder deshacer o corregir uno, primero hay que **verlo**. Necesitamos mostrar también los reembolsos que **ya anotaste como recibidos**.

**P1. ¿Dónde ponemos la lista de "reembolsos ya registrados"?**
  A) **Un bloque nuevo, aparte, debajo del de "a cobrar"** (dos cajas apiladas: arriba "a cobrar", abajo "ya registrados").
```
   [ Reembolsos a cobrar del operador ]   ← lo que ya existe
   [ Reembolsos ya registrados        ]   ← nuevo, con Deshacer / Corregir
```
  B) **Todo en una sola lista**, mezclando pendientes y registrados, con una etiqueta de color que diga en qué estado está cada uno.
```
   [ Reembolsos del operador ]
     Reserva #1042  [Pendiente]  ...
     Reserva #1039  [Registrado] ...  [Deshacer] [Corregir]
     Reserva #1030  [Deshecho]   ...
```
  → **Recomiendo A.** Son dos cosas distintas (una es plata que reclamás, la otra es plata que ya entró y podés corregir); separarlas se lee más claro y no toca el bloque que ya funciona.

---

### Tema: cuánto freno le ponemos a "Deshacer"
Contexto: deshacer un reembolso mueve plata. Ya obligamos a escribir un motivo (mínimo 20 letras) antes de poder apretar el botón, que de por sí obliga a pensarlo.

**P2. ¿Alcanza con el motivo obligatorio, o querés un "¿Seguro?" extra?**
  A) **Alcanza con el motivo.** Escribís por qué, apretás "Deshacer reembolso", y listo (igual que "Registrar reembolso tardío", que ya funciona así).
```
   [ motivo... ] 12/20   [ Cancelar ] [ Deshacer reembolso ]
```
  B) **Un paso más:** después de apretar "Deshacer reembolso", una última pregunta "¿Seguro que querés deshacerlo?" con Sí / No.
```
   [ motivo... ] 20/20   [ Deshacer reembolso ]
        ↓
   ¿Seguro?  [ No ]  [ Sí, deshacer ]
```
  → **Recomiendo A.** El motivo obligatorio ya es un freno fuerte y es el patrón que vos ya aprobaste para el reembolso tardío; un "¿Seguro?" arriba del motivo suma un clic sin agregar seguridad real. (Si preferís el doble freno para plata, decime B y lo pongo.)

---

### Tema: cómo elegís la reserva correcta al corregir
Contexto: "Corregir reserva" mueve el reembolso de la reserva equivocada a la correcta. Hay que elegir cuál es la correcta.

**P3. ¿Cómo elegís la reserva destino?**
  A) **De una lista corta ya filtrada:** el sistema te muestra solo las anulaciones **de este operador** que esperan un reembolso **en la misma moneda**, y elegís una. (Es el mismo selector que ya usás para registrar el reembolso.)
```
   Elegí la reserva correcta:
   ( ) Reserva #1051 · Pérez   te devuelven US$ 400 (estimado)
   (•) Reserva #1058 · Ruiz    te devuelven US$ 400 (estimado)
```
  B) **Buscando la reserva a mano**, escribiendo el número o el nombre del cliente en un buscador libre.
```
   Buscar reserva:  [ 1058____________ 🔍 ]
```
  → **Recomiendo A.** Un reembolso solo puede ir a otra anulación del mismo operador y misma moneda; la lista filtrada te muestra únicamente las válidas y te evita elegir una reserva a la que el sistema después va a rechazar. El buscador libre dejaría elegir cualquier cosa y frustra con errores.

---

### Tema: qué pasa si esa plata el cliente ya la usó
Contexto: a veces el reembolso ya se le devolvió al cliente (lo usó para otra reserva o lo retiró). En ese caso el sistema **no te deja deshacerlo sin más** — primero hay que deshacer la devolución al cliente. El mensaje que muestra el sistema ya está en criollo (arreglado 2026-07-21): *"No se puede anular este reembolso: el saldo a favor que generó ya fue retirado o aplicado por el cliente. Para deshacerlo primero hay que revertir ese uso del saldo, y eso requiere autorización."*

**P4. Cuando no se puede deshacer porque el cliente ya usó esa plata, ¿qué le decimos y a dónde lo mandamos?**
  A) **Un cartel que explica y frena, sin mandarlo a ningún lado:** *"No se puede deshacer: esta plata ya se le devolvió al cliente. Primero hay que deshacer esa devolución en la cuenta del cliente."* (el usuario va solo a la cuenta del cliente).
  B) **Lo mismo, pero con un botón que lo lleva** derecho a la cuenta del cliente / a la bandeja de revisión de Cobranzas donde se destraba ese caso.
```
   ⚠ No se puede deshacer todavía: esta plata ya se le devolvió al
     cliente. Hay que deshacer esa devolución primero.
     [ Ir a la cuenta del cliente ]
```
  → **Recomiendo B**, mandándolo a la **misma bandeja de revisión de Cobranzas** que ya venías por decidir para el caso gemelo (deshacer multa ya consumida). Si construimos esa bandeja una vez, sirve para los dos casos. Igual es tu operación: decidí vos a dónde conviene mandarlo. (Nota: el texto en criollo del backend ya quedó arreglado el 2026-07-21; esta pregunta solo decide si al lado del cartel se ofrece un botón que te lleve a resolverlo.)
