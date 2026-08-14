# Spec UX — Fechas del viaje: calculadas, "fecha prometida" y aviso suave (F2 de ADR-053)

- **Fecha**: 2026-08-13
- **Autor**: `ux-ui-disenador`. Diseñado SOLO desde `docs/ux/guia-ux-gaston.md` + el estándar visual
  firmado (`docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md`) + lo ya construido en pantalla.
  Todo lo que la guía NO cubre está al final, en **PREGUNTAS PARA GASTÓN**.
- **Obra**: ADR-053 §0 (5 decisiones firmadas por el dueño el 2026-08-11, NO se reabren), F2 = frontend.
- **Reglas de la constitución que aplica esta spec**: **F-2** (la cabecera se calcula desde las líneas),
  **P-2** (fechas dd/MM/aaaa), **P-9 / P-10** (botón apagado con motivo escrito + palabra al lado de
  cada ícono; enmienda 11/08: en listados de escritorio el motivo puede ir en globito sobre el
  envoltorio — acá NO aplica, estamos en la ficha, va escrito), **P-11** (ningún mensaje deja al
  usuario sin salida), **P-15** (nada de cartelitos aclarativos), **P-16** (un dato no se dice dos
  veces), **P-20** (aviso suave informa y deja seguir), **P-21** (el sistema sugiere, no decide),
  **B.3 / B.4 / B.5** del estándar visual (escala de botones, alineación, molde de chip).
- **Estado**: **BORRADOR — no se construye la parte marcada "espera respuesta"** hasta que Gastón
  conteste P1..P9.

---

## 0. Qué cambia, en una línea

Hoy el vendedor puede escribir a mano la Salida y el Regreso de la reserva desde un botón "Editar
fechas". A partir de esta obra **esas dos fechas las arma el sistema solo, con los servicios que están
vivos**, y **nadie las escribe**. A cambio aparece un par de fechas nuevas, opcionales, que el
vendedor sí escribe: la fecha que **le prometió al cliente** (nombre en pantalla a definir — P4).

Esto ya estaba firmado antes de esta obra: **(2026-06-22, sección "Sacar de viaje")** *"la fecha de
salida sale de los servicios, no se escribe a mano"*. Lo que hace F2 es que la pantalla, por fin, diga
la verdad.

---

## 1. Lo que YA está firmado y NO se pregunta

| Qué | De dónde sale |
|---|---|
| Las fechas del viaje salen de los servicios, no se escriben a mano | Guía, 2026-06-22 ("Sacar de viaje") + ADR-053 §0 decisión 1 |
| Fechas en pantalla **dd/MM/aaaa** | P-2 |
| El renglón de fechas vive en la cabecera, alineado con el título, el cliente y los chips | Estándar visual B.4 ("un solo canal de alineación") + cómo está hoy |
| **Se va el emoji 📅**: ícono de línea, del mismo juego que el resto | Estándar visual B.3, regla de oro 4 ("se van todos los emojis: 🚫 🗄 📅") |
| El chip **"En corrección"** ya existe y se queda con su molde de chip (24 px, redondo, 11 px mayúsculas, ámbar) | Guía 2026-06-22 + 2026-07-05 (2B) + estándar visual B.5 |
| Un aviso que **solo informa** va **gris y de una sola línea**; el que **pide hacer algo** va **ámbar** | Guía 2026-08-03, P11 |
| Un aviso NO se dice dos veces (chip Y banner) | P-16 / guía 2026-07-05, respuesta 2B |
| Los avisos largos de **rechazo** del motor van a la ventana emergente única; **esto NO es un rechazo**, así que **NO** va a ventana emergente | Guía 2026-07-22, "regla de corte" (pide las TRES: click + frena + texto largo) |
| Con la reserva trabada (Confirmada sin autorización viva), un botón de edición queda **gris + candadito, CON la palabra**, y abre la ventana de destrabar | Guía 2026-07-22 (candado C1) + 2026-08-03 (P-9/P-10) |
| **Ninguna ventana flotante nueva**: lo que se edita, se edita en línea | Guía 2026-08-03, P6=A (*"el modal me parece horrible"*) + pasajeros en línea 2026-07-05 |
| "Reprogramar viaje" **se queda igual** (mueve las fechas de los SERVICIOS, no la cabecera) | Guía 2026-06-23 + ADR-053 §1.6 |

---

## 2. Pantalla — cabecera de la ficha de reserva

### 2.1 Cómo se ve HOY (verificado en `ReservaHeader.jsx:579-622`)

```
 ← Volver al listado
 Reserva F-2026-1067   [ PRESUPUESTO ]                    ┏━━━━━━━━━━━━━━━━━━┓
 Marisa Salafia                                           ┃ El cliente aceptó┃  Perdida  Archivar  ⋯
 PUNTA CANA · 1 pasajero                                  ┗━━━━━━━━━━━━━━━━━━┛
 Pago: sin movimientos · Factura: sin facturar
 ┌───────────────────────────────────────────────┐
 │ 📅 Salida: 10/02/2027 · Regreso: 15/02/2027   │  [ ✏ Editar fechas ]  [ ⏩ Reprogramar viaje ]
 └───────────────────────────────────────────────┘         ↑ SE RETIRA          ↑ SE QUEDA
```

### 2.2 Cómo queda (esqueleto; el texto exacto lo define P1)

```
 ← Volver al listado
 Reserva F-2026-1067   [ PRESUPUESTO ]                    ┏━━━━━━━━━━━━━━━━━━┓
 Marisa Salafia                                           ┃ El cliente aceptó┃  Perdida  Archivar  ⋯
 PUNTA CANA · 1 pasajero                                  ┗━━━━━━━━━━━━━━━━━━┛
 Pago: sin movimientos · Factura: sin facturar

 ┌───────────────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027                        │   [ Reprogramar viaje ]
 │    según los servicios cargados                       │     secundaria, 32 px
 └───────────────────────────────────────────────────────┘

   (renglón de la fecha prometida — ver §3, solo si hay algo cargado)
```

**Reglas de forma (ya firmadas, no se preguntan):**

- Mismo recuadro liviano que hoy (borde gris, redondeo 10 px, alineado al canal izquierdo — B.4).
- Ícono de línea de calendario, **sin emoji** (B.3).
- Las fechas **no son un campo**: no hay casillero, no hay lápiz, no hay foco de teclado. Es texto.
- El botón **"Editar fechas" desaparece por completo** (los dos estados: el normal y el gris con
  candadito). No se "apaga con motivo": la acción **dejó de existir**, y P-9 dice que lo que
  estructuralmente ya no aplica **no aparece**.
- **"Reprogramar viaje" se queda tal cual** (mismo lugar, mismo molde, mismo candado): mueve las
  fechas de los servicios, que es otra cosa.

### 2.3 Estados de este renglón

| Situación | Qué se ve |
|---|---|
| Hay servicios vivos con fecha | `Del 10/02/2027 al 15/02/2027` |
| Hay solo fecha de inicio (ej. un vuelo suelto de ida) | `Del 10/02/2027` (sin la segunda mitad) — **el "al …" no se muestra vacío** |
| **No hay ningún servicio vivo con fecha** (reserva recién creada, o todos los servicios anulados) | **texto a definir — P2** |
| Cargando la ficha | El recuadro se dibuja gris/vacío igual que el resto de la cabecera (mismo esqueleto de carga que ya usa la ficha), sin saltos de alto |
| No se pudo traer la ficha | Nada especial acá: manda el cartel de error de la ficha entera, que ya existe |

---

## 3. La "fecha prometida" (el par nuevo)

**Qué es** (ADR-053 D3): dos fechas **opcionales**, **que escribe el vendedor**, que el cálculo
**JAMÁS pisa**. Sirven para dejar anotado lo que se le prometió al cliente cuando no coincide con lo
que dicen los servicios cargados.

**Nombre en pantalla: SIN DEFINIR — es P4, y necesita un ejemplo real de la operación de Gastón antes
de ponerle cualquier palabra.** Los mockups de abajo usan `«nombre»` como marcador de posición: el
frontend **no construye este bloque** hasta que la palabra esté firmada.

### 3.1 Propuesta de forma (dónde vive y cómo se edita) — el LUGAR es P3

Recomendación: **escondida hasta que se usa**. Debajo del renglón de fechas calculadas, un enlace
discreto; al tocarlo se abren dos casilleros **en la misma pantalla** (nunca ventana flotante — guía
2026-08-03 P6=A), con [Guardar] y [Descartar] (B.7: el botón de salir sin guardar dice "Descartar").

**Estado 1 — todavía no se usó nunca (lo que ve el 95% de las reservas):**

```
 ┌───────────────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027                        │
 │    según los servicios cargados                       │
 └───────────────────────────────────────────────────────┘
   «nombre» ＋                       ← enlace gris chiquito, sin caja. Nada más.
```

**Estado 2 — el vendedor lo tocó (se abre en línea):**

```
 ┌───────────────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027                        │
 └───────────────────────────────────────────────────────┘
 ┌───────────────────────────────────────────────────────┐
 │ «nombre»                                               │
 │   Sale el [ 12/02/2027 ▾ ]     Vuelve el [ __________ ]│
 │                                                        │
 │                      [ Guardar ]  Descartar            │
 └───────────────────────────────────────────────────────┘
```

- **Las dos son opcionales e independientes**: se puede cargar solo una. Un casillero vacío = no hay
  nada prometido de ese lado.
- **Sin leyenda explicativa dentro del bloque** (P-15): ni "(opcional)", ni "esta fecha no afecta el
  cálculo", ni nada.
- Para **borrar** lo cargado se vacía el casillero y se guarda (mismo gesto que en el resto de la app).
- **Candado**: si la reserva está Confirmada y trabada sin autorización viva, el enlace/botón queda
  **gris con candadito y CON la palabra**, y al tocarlo abre la ventana de destrabar que ya existe
  (`EditAuthorizationModal`) — exactamente el tratamiento que hoy tiene "Editar fechas" (guía
  2026-07-22, candado C1). En reservas Anulada / Perdida / Finalizada / Archivada **no aparece**
  (solo lectura, guía ADR-036).

**Estado 3 — ya hay algo cargado (así lo ve cualquiera que abra la ficha):**

```
 ┌───────────────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027                        │
 └───────────────────────────────────────────────────────┘
   «nombre»: del 12/02/2027 al 17/02/2027      ✏ Editar
   ↑ renglón gris chiquito, debajo, nunca compite con el de arriba
```

Si lo prometido **no coincide** con lo calculado, ¿se marca la diferencia de alguna forma? → **P8**.

---

## 4. El aviso suave cuando un servicio corre las fechas del viaje

**Qué llega del motor** (ADR-053 D2 / D2.1): en el `GET` normal de la ficha, `ReservaDto.ScheduleWarning`
trae **un texto ya escrito en criollo por el motor** (ej.: *"Con este hotel, el viaje pasa a terminar el
12/04 — ¿la fecha del hotel está bien?"*). Viene **null** casi siempre; viene con texto solo la primera
vez que el mismo vendedor abre/recarga la ficha después de haber corrido la ventana. Se consume solo.

**Reglas que ya mandan acá:**

- **P-20**: informa y **deja seguir**. No frena nada, no bloquea el guardado, no hay que "aceptarlo".
- **NO va a la ventana emergente única** (guía 2026-07-22): esa es para rechazos del motor que frenan
  una acción. Este no frena nada.
- **El front no reescribe ni resume el texto**: pinta el string tal cual llega (T-13 del ADR).
- No lleva botón: no hay ninguna acción que ofrecer (si la fecha está mal, se arregla en el servicio).

**Forma recomendada (a confirmar en P5): un renglón gris de una línea**, el primero de la tira de
avisos de la ficha, con el molde que ya existe (`AvisoFila variante="info"`, guía 2026-08-03 P11).

```
 Reserva F-2026-1067  [ CONFIRMADA ]                        (cabecera)
 ─────────────────────────────────────────────────────────────────────
 │ 🗓 Con este hotel, el viaje pasa a terminar el 12/04 — ¿la fecha  │   ← gris, 1 línea, sin botón
 │    del hotel está bien?                                          │
 ─────────────────────────────────────────────────────────────────────
 │ ⚠ Reserva confirmada. Para cambiar algo, pedí autorización.  [Pedí…] │  (candado, ya existe)
 ─────────────────────────────────────────────────────────────────────
 │ 2 avisos más  [ Ver ▾ ]                                            │  (plegado, ya existe)
```

- **Va arriba de todo de la tira de avisos**, pero **debajo** de los carteles de estado terminal (esos
  siguen siendo "la foto", orden firmado 2026-07-05).
- **Nunca se pliega dentro de "N avisos más"**: es de una sola vez, si se pliega no lo ve nadie.
- Se va solo: como el motor lo consume al leer, en la siguiente recarga de la ficha ya no está. **No
  hay una "✕" para cerrarlo** (cerrarlo a mano no agrega nada).

---

## 5. Botón "volver a calcular"

**Cuándo aparece**: solo cuando el motor dice que hace falta — hoy eso pasa después de "Sacar de
viaje" (la reserva queda marcada y con el chip **"En corrección"**, que ya existe). El front lo lee de
`reserva.isUnderCorrection`, que **no cambia de nombre ni de significado** (ADR-053 D4).

**Qué hace**: pide al motor que rearme las fechas con los servicios de ahora y apaga la marca. Es
seguro y no destruye nada → **no lleva "¿Seguro?"** (P-14 es para acciones destructivas).

**Después de tocarlo**: cartelito verde corto de siempre ("Listo, fechas actualizadas"), la ficha se
recarga sola, y el chip / renglón de "En corrección" desaparece.

**Dónde vive: es P6.** Hay una tensión real que decide Gastón: hoy "En corrección" es **solo un chip**
(firmado 2026-07-05, para no decir el mismo dato dos veces — P-16), y un chip no puede tener un botón.
Pero ahora ese estado **sí tiene una salida concreta**, y P-11 dice que ningún aviso deja al usuario
sin saber qué hacer. Las opciones y el dibujito están en P6.

---

## 6. Qué se RETIRA de la pantalla (esto sí se puede construir ya)

| Qué se saca | Dónde está hoy |
|---|---|
| Botón **"Editar fechas"** (las dos variantes: normal y gris con candadito) | `src/TravelWeb/src/features/reservas/components/ReservaHeader.jsx:593-622` |
| La ventana flotante de editar fechas | `src/TravelWeb/src/features/reservas/components/EditReservaDatesModal.jsx` (componente entero) y su test `editReservaDatesModal.test.mjs` |
| El estado y el enganche que la abren | `ReservaDetailPage.jsx:904` (`showEditDatesModal`), `:1437` (`onEditDates`), `:2890-2893` (render del modal) |
| La llamada al motor `PATCH /reservas/{id}/dates` y su cartelito "Fechas actualizadas" | `ReservaDetailPage.jsx:1293-1300` (incluido el `showWarning(actualizada.warning)` de esa respuesta, que muere con ella) |
| Cualquier lectura de las fechas "sugeridas" (`suggestedStartDate` / `suggestedEndDate`) | El motor las retira del DTO (ADR-053 D3). Existían solo para precargar el casillero que ahora no existe. Barrer con grep antes de dar por terminado |
| El emoji 📅 del renglón de fechas | `ReservaHeader.jsx:582` → ícono de línea (B.3) |

**Lo que NO se toca**: "Reprogramar viaje" (botón, modal y flujo), el chip "En corrección", el helper
`formatTripDate` de `ReservaHeader.jsx:39-51` (arregla el bug de "fechas corridas un día" leyendo el
texto de la fecha sin pasar por `new Date()` — **el par de fechas prometidas se muestra con el MISMO
helper**, si no vuelve el mismo bug del 2026-07-16).

---

## 7. Estados que hay que cubrir (checklist para el que programa)

| Estado | Qué pasa |
|---|---|
| **Vacío** | Sin servicios vivos → el renglón de fechas dice lo de P2. La fecha prometida sigue pudiendo cargarse (no depende de que haya servicios) |
| **Cargando** | Esqueleto gris del alto exacto del renglón (que no salte la cabecera) |
| **Error al guardar la fecha prometida** | Mensaje corto pegado al bloque en línea, **sin perder lo tipeado** (estándar de formularios del proyecto). No es un rechazo largo del motor ⇒ no va a ventana emergente |
| **Éxito** | Cartelito verde corto + la ficha se recarga sola (mismo patrón que el resto de la ficha) |
| **Sin permiso / con candado** | Enlace o botón **gris + candadito + palabra**, y al tocarlo abre la ventana de destrabar que ya existe (P-9, P-10, candado C1) |
| **Reserva en solo lectura** (En viaje, Anulada, Perdida, Finalizada, Archivada) | No aparece nada de editar; si hay fecha prometida cargada, **se sigue viendo** (lo cargado nunca se esconde) |
| **Aviso suave** | Si `ScheduleWarning` viene vacío o nulo, **no se dibuja nada** (ni un espacio en blanco) |

---

## 8. Qué NO hay que hacer

1. **No** dejar ningún camino para escribir a mano la Salida o el Regreso de la reserva (ni un
   casillero escondido, ni "solo para Admin"). Si aparece uno, la obra falló.
2. **No** inventar el nombre del campo "fecha prometida" — se espera P4. Tampoco copiar la palabra de
   otro sistema ("fecha comprometida", "deadline", "fecha objetivo") por parecer razonable.
3. **No** mandar el aviso suave a la ventana emergente única ni a un `showWarning` con título
   "Advertencia": no es un rechazo ni un error.
4. **No** repetir el mismo dato en dos lugares (P-16): si el aviso suave se muestra como renglón, no va
   además como globito flotante; si "En corrección" pasa a renglón con botón, el chip se va.
5. **No** agregar leyendas explicativas dentro del bloque de la fecha prometida (P-15).
6. **No** poner un "¿Seguro?" antes de "volver a calcular" (no destruye nada).
7. **No** tocar "Reprogramar viaje".
8. **No** mostrar el `ScheduleWarning` en el listado de reservas ni en la campanita: es de la ficha, en
   el momento.

---

## 9. Contrato con el motor (lo que el front espera y no arma)

| Campo del `GET` de la ficha | Para qué |
|---|---|
| `startDate` / `endDate` | El renglón de fechas, ya calculado. El front **no** los recalcula ni los completa |
| `promisedStartDate` / `promisedEndDate` | El par prometido (pueden venir vacíos) |
| `scheduleWarning` | Texto ya escrito del aviso suave. El front lo pinta tal cual |
| `isUnderCorrection` | Prende el chip / renglón "En corrección" y el botón "volver a calcular" |
| `capabilities.canEditReservaData` | Si se puede editar la fecha prometida, y si hay candado |
| `PATCH /reservas/{id}/promised-dates` | Guardar/borrar el par prometido |
| `POST /reservas/{id}/recalculate-dates` | El botón "volver a calcular" |

---

## PREGUNTAS PARA GASTÓN

> Contestá con el número y la letra: *"1A, 2B, 3 otra cosa: …"*. Si ninguna te cierra, contá con tus
> palabras qué querés y lo dibujamos así.

### Tema 1: cómo se ven las fechas del viaje ahora que las arma el sistema

Contexto: hasta hoy había un botón "Editar fechas" para escribirlas a mano. Se va: las fechas salen
solas de los servicios cargados. Falta decidir cómo se lee ese renglón en la ficha.

**P1. ¿El renglón aclara de dónde salen las fechas, o va a secas?**

  A) **Con la aclaración chiquita debajo** *(recomendada)*
```
 ┌────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027             │
 │    según los servicios cargados            │   ← gris chiquito
 └────────────────────────────────────────────┘
```
  Por qué la recomiendo: es la primera vez que las fechas dejan de ser editables; sin esa línea el
  vendedor va a buscar el botón que ya no está.

  B) **A secas, sin ninguna aclaración**
```
 ┌────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027             │
 └────────────────────────────────────────────┘
```

  C) **Como está hoy, con las dos palabras**
```
 ┌────────────────────────────────────────────────────────┐
 │ 🗓 Salida: 10/02/2027 · Regreso: 15/02/2027             │
 └────────────────────────────────────────────────────────┘
```

**P2. Cuando la reserva todavía no tiene ningún servicio vivo (o los anularon a todos), ¿qué dice ese renglón?**

  A) **Cuenta por qué está vacío** *(recomendada)*
```
 ┌──────────────────────────────────────────────────────────┐
 │ 🗓 Sin fechas todavía — se arman al cargar los servicios  │
 └──────────────────────────────────────────────────────────┘
```

  B) **Corto y seco**
```
 ┌──────────────────────────┐
 │ 🗓 Sin fechas             │
 └──────────────────────────┘
```

  C) **Como hoy**
```
 ┌────────────────────────────────────────────┐
 │ 🗓 Salida: sin cargar · Regreso: sin cargar │
 └────────────────────────────────────────────┘
```

  D) **No se muestra el renglón** hasta que haya al menos un servicio con fecha.

---

### Tema 2: la fecha que le prometiste al cliente

Contexto: campo nuevo, opcional. Son dos fechas (una de ida y una de vuelta) que escribís vos y que el
sistema **nunca** pisa, para cuando lo prometido no coincide con lo que dicen los servicios.

**P3. ¿Dónde vive?**

  A) **Escondida hasta que la usás**: debajo de las fechas, un enlace chiquito; recién al tocarlo
     aparecen los casilleros *(recomendada — la gran mayoría de las reservas no la va a usar y no
     conviene sumar cajas a una cabecera que ya está cargada)*
```
 ┌────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027             │
 └────────────────────────────────────────────┘
   «nombre» ＋
```

  B) **Siempre a la vista**, al lado de las calculadas, aunque estén vacías
```
 ┌────────────────────────────────────────────┐  ┌──────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 15/02/2027             │  │ «nombre»: — — —      ✏ Editar│
 └────────────────────────────────────────────┘  └──────────────────────────────┘
```

  C) **En otra solapa** (dentro de "Estado de cuenta" / una solapa de datos de la reserva), no en la
     cabecera
```
 [ Servicios ] [ Pasajeros ] [ Documentos ] [ Estado de cuenta ] [ Historial ]
                                                  └─ acá adentro, un bloque "«nombre»"
```

**P4. ¿Cómo se llama en pantalla? — y ANTES de elegir, contame un caso real tuyo.**

  Esta es la pregunta que quedó pendiente del 11/08. Para no ponerle un nombre de manual, necesito un
  ejemplo de tu operación: **¿en qué situación concreta le prometés al cliente una fecha distinta de la
  que dicen los servicios cargados?** (Ej.: todavía no confirmó el hotel y le decís "salís el 12";
  esperás cupo aéreo; el operador te va a mover el traslado; una promo que sale recién si junta gente…)
  Con ese caso en la mano elegimos la palabra.

  Mientras tanto, tres candidatos:

  A) **"Fecha prometida al cliente"**
```
   Fecha prometida al cliente:  sale el [ 12/02/2027 ]  vuelve el [ 17/02/2027 ]
```
  B) **"Fecha que le dimos al cliente"**
```
   Fecha que le dimos al cliente:  sale el [ 12/02/2027 ]  vuelve el [ 17/02/2027 ]
```
  C) **"Fecha estimada de viaje"**
```
   Fecha estimada de viaje:  sale el [ 12/02/2027 ]  vuelve el [ 17/02/2027 ]
```
  D) **Otra: la palabra que usás vos cuando se lo decís al cliente por teléfono.**

**P8. Cuando lo prometido NO coincide con lo que dicen los servicios, ¿se marca la diferencia?**

  A) **No se marca nada**: se muestran los dos renglones y listo *(recomendada — vos ya ves las dos
     fechas juntas; pintarlo de color lo convierte en un reto que nadie pidió)*
```
 🗓 Del 10/02/2027 al 15/02/2027
   «nombre»: del 12/02/2027 al 17/02/2027            ✏ Editar
```

  B) **Un renglón gris que lo dice** (informa, no pide nada)
```
 🗓 Del 10/02/2027 al 15/02/2027
   «nombre»: del 12/02/2027 al 17/02/2027            ✏ Editar
   No coinciden: le prometiste 2 días después
```

  C) **Ámbar, como algo a revisar**
```
 ⚠ Lo prometido al cliente no coincide con los servicios cargados
```

**P9. ¿Esa fecha prometida sale en el PDF del presupuesto que le mandás al cliente?**

  A) **No, es una nota interna de la agencia** *(recomendada por ahora — el PDF ya quedó firmado el
     11/08 y meterle un dato más sin necesidad lo ensucia)*
  B) **Sí**: si está cargada, el PDF muestra esa fecha en vez de la de los servicios.
  C) **Sí, pero después**: lo dejamos anotado para cuando esté andando y lo vemos con el PDF en la mano.

---

### Tema 3: el aviso de "che, esto te movió las fechas del viaje"

Contexto: si cargás o editás un servicio y eso corre la fecha de inicio o de fin del viaje, el sistema
te avisa **sin frenarte** (podés seguir trabajando igual). El texto lo escribe el motor, algo así como:
*"Con este hotel, el viaje pasa a terminar el 12/04 — ¿la fecha del hotel está bien?"*.

**P5. ¿Cómo te lo muestra?**

  A) **Renglón gris de una línea arriba de la ficha**, junto con los otros avisos; se va solo cuando
     recargás *(recomendada — es el molde que ya firmaste el 03/08: lo que solo informa va gris y de
     una línea)*
```
 Reserva F-2026-1067  [ CONFIRMADA ]
 ───────────────────────────────────────────────────────────────
 │ 🗓 Con este hotel, el viaje pasa a terminar el 12/04 —       │
 │    ¿la fecha del hotel está bien?                           │
 ───────────────────────────────────────────────────────────────
 │ ⚠ Reserva confirmada. Para cambiar algo, pedí autorización. │
 ───────────────────────────────────────────────────────────────
```

  B) **Globito flotante** en la esquina, que aparece unos segundos y se va solo
```
                                         ┌────────────────────────────┐
                                         │ 🗓 Con este hotel, el viaje │
                                         │  pasa a terminar el 12/04… │
                                         └────────────────────────────┘
```

  C) **Renglón ámbar** (más fuerte, como los avisos que piden algo)
```
 ───────────────────────────────────────────────────────────────
 │ ⚠ Con este hotel, el viaje pasa a terminar el 12/04 —       │
 │   ¿la fecha del hotel está bien?                            │
 ───────────────────────────────────────────────────────────────
```

  D) **Pegado al renglón de las fechas**, en la cabecera, en vez de arriba con los otros avisos
```
 ┌────────────────────────────────────────────────────────┐
 │ 🗓 Del 10/02/2027 al 12/04/2027                          │
 │    ⤷ lo movió el hotel que acabás de cargar             │
 └────────────────────────────────────────────────────────┘
```

---

### Tema 4: el botón "volver a calcular"

Contexto: cuando sacás una reserva de viaje (porque entró por error), queda con el cartelito **"En
corrección"**. Ahora va a haber un botón para decirle al sistema "listo, rearmá las fechas". Hoy "En
corrección" es **solo un cartelito chico arriba** y un cartelito no puede tener botón.

**P6. ¿Dónde ponemos ese botón?**

  A) **Adentro del "⋯"** (el botón de tres puntitos donde ya viven "Volver atrás", "Destrabar
     reserva" y "Sacar de viaje"). El cartelito "En corrección" queda igual que hoy.
```
 Reserva F-2026-1067  [ CONFIRMADA ]  [ EN CORRECCIÓN ]     …   Archivar   ⋯
                                                                           └─┬─────────────────────┐
                                                                             │ Volver atrás        │
                                                                             │ Sacar de viaje      │
                                                                             │ Volver a calcular   │
                                                                             │ las fechas          │
                                                                             └─────────────────────┘
```

  B) **El cartelito se transforma en un renglón ámbar con el botón al lado** (y deja de estar arriba
     como cartelito, para no decir lo mismo dos veces) *(recomendada — el estado ahora tiene una salida
     concreta, y tu regla es que un aviso nunca te deje sin saber qué hacer)*
```
 Reserva F-2026-1067  [ CONFIRMADA ]
 ─────────────────────────────────────────────────────────────────────────────
 │ ⚠ Falta revisar las fechas del viaje.      [ Volver a calcular las fechas ]│
 ─────────────────────────────────────────────────────────────────────────────
```

  C) **Las dos cosas**: el cartelito chico arriba **y** el renglón ámbar con el botón.
```
 Reserva F-2026-1067  [ CONFIRMADA ]  [ EN CORRECCIÓN ]
 ─────────────────────────────────────────────────────────────────────────────
 │ ⚠ Falta revisar las fechas del viaje.      [ Volver a calcular las fechas ]│
 ─────────────────────────────────────────────────────────────────────────────
```
  (Aviso honesto: esta opción dice el mismo dato dos veces, que es justo lo que sacamos el 05/07.)

**P7. ¿Ese botón existe siempre, o solo cuando el sistema dice que hace falta?**

  A) **Solo cuando hace falta** (después de "Sacar de viaje", o si el sistema marca que hay que
     revisar). El resto del tiempo no aparece *(recomendada — la pantalla no ofrece cosas que no
     hacen falta)*
```
   Reserva normal:      …   Archivar   ⋯       (sin "volver a calcular")
   Reserva en corrección: ⚠ Falta revisar las fechas   [ Volver a calcular las fechas ]
```

  B) **Siempre disponible**, escondido en el "⋯", por si alguna vez las fechas quedan raras y querés
     forzar el recálculo vos mismo.
```
   Cualquier reserva:  ⋯ → │ Volver atrás        │
                           │ Volver a calcular   │
                           │ las fechas          │
```

---

## Estado de la obra tras esta spec

- **Se puede construir YA** (firmado, sin preguntas): §6 completo — retirar el botón "Editar fechas",
  la ventana flotante, el `PATCH /dates` y el emoji; y dejar el renglón de fechas de solo lectura con
  el texto **provisorio de hoy** hasta que P1 se conteste.
- **Espera respuesta**: el texto final del renglón (P1, P2), todo el bloque de la fecha prometida
  (P3, P4, P8, P9), la forma del aviso suave (P5) y el lugar del botón "volver a calcular" (P6, P7).
