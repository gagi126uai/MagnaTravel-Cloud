# Rediseño de la pantalla de resguardos / "Volver atrás" (2026-07-30)

> **Estado: ✅ APROBADO POR GASTÓN (2026-07-30).** Respondió las 12 preguntas del bloque final
> **con la opción recomendada en todas: 1A · 2A · 3A · 4A · 5A · 6A · 7A · 8A · 9A · 10A · 11A · 12A.**
> Además **vio la maqueta HTML interactiva de la pantalla y le gustó**. Esta spec es, desde hoy, la
> especificación que el frontend sigue al pie de la letra: cualquier desvío (por costo técnico o
> regla del motor) se le repregunta a Gastón ANTES, nunca se decide solo.
> Las decisiones quedaron registradas en `docs/ux/guia-ux-gaston.md`, sección
> **"Copias de seguridad — rediseño de 'Volver atrás' (2026-07-30)"**.
> Fuente única de UX: `docs/ux/guia-ux-gaston.md`.
> Constitución aplicable: P-4, P-5, P-6, P-7, P-9, P-10, P-13, P-14, P-15, P-17, P-20, P-2, T-5, T-14.

---

## 1. De dónde sale esto (palabras de Gastón, hoy)

> "no me gusta nada como está hecho esto, es confuso y raro" · "la ux/ui es la peor que vi en mi
> vida" · "el modal es un asco, yo no le pondría modal" · "da un mal feedback, no me cierra" ·
> "es un horror, es un cáncer visual".

La función (Administración → Mantenimiento → Zona peligrosa → **"Volver atrás…"**) vive hoy en una
**ventana flotante** (`RestaurarResguardoModal.jsx`) y, cuando el motor rechaza algo, se abre **otra
ventana encima de la primera**.

**Aclaración importante:** el bug del cartel falso "versión MÁS NUEVA" se arregla aparte, en el
motor. Este rediseño asume que las marcas dicen la verdad (Al día / Versión anterior / Versión más
nueva / Versión desconocida).

---

## 2. Lo que YA está firmado y NO se toca

De `guia-ux-gaston.md`, sección **"Volver atrás": resguardos de versiones anteriores (ADR-052,
2026-07-29)** — marco firmado por Gastón, NO se reabre:

1. Aviso claro, **sin paso extra de confirmación**. La confirmación sigue siendo la de siempre:
   **frase exacta + contraseña + motivo (mínimo 10 caracteres)**.
2. **Cuatro situaciones** por copia: al día (sin marca) · versión anterior (marca + aviso) ·
   versión más nueva (marca + aviso, botón igual habilitado) · versión desconocida (aviso neutro).
3. **Ningún botón se apaga por la versión de la copia.** El único freno real es el chequeo del
   motor, que avisa antes de tocar nada.
4. **Textos literales** de los tres carteles y de los tres badges (ámbar / rosa / gris): quedan
   **exactamente como están hoy**, palabra por palabra. Este rediseño **mueve** esos textos de
   lugar, no los reescribe.
5. La **línea extra** en el "¿Seguro?" cuando la copia es más vieja también queda tal cual.

Reglas transversales que mandan acá:

- **P-5** — "Las fichas de trabajo van EN LÍNEA, nunca en ventana flotante. *El modal me parece
  horrible*." Elegir una copia, escribir la frase, la contraseña y el motivo **es trabajo**: no va
  en una ventana.
- **P-4** — el aviso largo de bloqueo del motor va a **UNA sola ventana emergente** (`CartelEmergente`),
  siempre la misma, dos trajes (rojo = freno, ámbar = confirmá). **Una por vez, nunca apilada.**
- **P-14** — toda acción destructiva confirma antes ("¿Seguro?").
- **P-13** — el texto de rechazo del motor se muestra **tal cual**, sin reescribirlo.
- **P-9 / P-10** — botón apagado: motivo **siempre a la vista** al lado, nunca en tooltip.
- **P-6 / P-7** — el error nunca es un globito que se va solo; queda a la vista y **no se pierde
  nada de lo cargado**.
- **P-17** — voz de los avisos: nada de "el sistema hace un job", "esquema", "base de datos",
  "migración". Se habla en criollo.
- **P-2 / T-14** — fechas dd/MM/aaaa y **hora argentina siempre**.

---

## 3. Qué está mal hoy, concretamente (diagnóstico, sin opinión de gusto)

| # | Lo que pasa hoy | Regla que rompe |
|---|---|---|
| 1 | Toda la tarea (elegir copia + frase + contraseña + motivo + 3 botones) vive en una **ventana flotante**. | **P-5** |
| 2 | Cuando el motor rechaza, se abre **una segunda ventana ENCIMA** de la primera. | **P-4** (una emergente, nunca apiladas) |
| 3 | La ventana tiene **cinco bloques de texto explicativo** apilados (aviso de versión + "qué hace cada acción" + leyenda de la frase + rótulo del motivo + dos líneas de motivo-apagado). Es una pared de letras. | **P-15** |
| 4 | **Tres botones del mismo tamaño** ("Ver qué contiene" / "Restaurar configuración" / "Restaurar todo"): la pantalla no dice cuál es el camino normal. | (sin regla previa → **pregunta P5**) |
| 5 | El error aparece **desconectado** de la fila que lo causó: la copia elegida queda arriba, tapada por la ventana de error. | **P-6** (el error se queda pegado a lo que hay que corregir) |
| 6 | Si falla listar las copias, hay un cartel rojo **sin botón para reintentar** (hay que cerrar y volver a abrir). | estándar de la casa: todo flujo tiene camino de recuperación |
| 7 | Cada fila dice `Resguardo del 27/07/2026 22:33 — 1,2 MB` en un renglón corrido, con un botoncito de radio: no se lee como una lista, se lee como un formulario. | (sin regla previa → **pregunta P3**) |

---

## 4. LA PROPUESTA (una sola recomendación)

### 4.1 Dónde vive: solapa propia en Administración, sin ninguna ventana

Administración ya es una pantalla con solapas (Usuarios · Roles y Permisos · Comisiones · Auditoría
Central · Mantenimiento). Se le suma una sexta: **"Copias de seguridad"**. Ahí adentro vive todo,
en la página, sin ventanas flotantes.

```
 Administración
 ┌──────────┬───────────────────┬────────────┬──────────────────┬──────────────┬─────────────────────┐
 │ Usuarios │ Roles y Permisos  │ Comisiones │ Auditoría Central│ Mantenimiento│ ▸Copias de seguridad│
 └──────────┴───────────────────┴────────────┴──────────────────┴──────────────┴─────────────────────┘
```

La solapa **Mantenimiento** queda solo con el trabajo de todos los días (Lifecycle de reservas).

### 4.2 Pantalla en reposo (lo primero que se ve)

```
╔══════════════════════════════════════════════════════════════════════════════╗
║  🛡  Copias de seguridad                                                     ║
║  El sistema guarda una copia entera cada vez que se usa "Empezar de cero"    ║
║  y cada vez que se vuelve a una copia anterior.                              ║
╚══════════════════════════════════════════════════════════════════════════════╝

  ┌───────────────────────────────────────────────────────────────────────────┐
  │ CUÁNDO SE GUARDÓ          POR QUÉ SE GUARDÓ            TAMAÑO             │
  ├───────────────────────────────────────────────────────────────────────────┤
  │ 29/07/2026 22:33          Antes de empezar de cero     1,2 MB             │
  │ hace 1 día                                                [ Usar esta ▾ ] │
  ├───────────────────────────────────────────────────────────────────────────┤
  │ 27/07/2026 10:04          Antes de volver a una copia   980 KB            │
  │ hace 3 días   ⟨Versión anterior⟩                          [ Usar esta ▾ ] │
  ├───────────────────────────────────────────────────────────────────────────┤
  │ 12/07/2026 08:12          Antes de empezar de cero       870 KB           │
  │ hace 18 días  ⟨Versión desconocida⟩                       [ Usar esta ▾ ] │
  └───────────────────────────────────────────────────────────────────────────┘
```

- **Fecha completa + "hace cuánto" debajo**, en hora argentina (P-2, T-14). El "hace cuánto" es el
  mismo recurso ya firmado para las bandejas (guía 2026-07-08: "reserva · qué falta · hace cuánto").
- **La marca de versión es un badge chico en la misma fila**, con texto real, nunca solo color
  (ya firmado 2026-07-29, P3=A "con el badge alcanza"). La fila **nunca** se atenúa ni se apaga.
- **Un solo botón por fila**: "Usar esta ▾". No hay radio buttons.
- La columna "Por qué se guardó" **depende del motor** (ver §7). Si Gastón la descarta (pregunta P4),
  la tabla queda con dos columnas y el badge.

### 4.3 Al tocar "Usar esta": la ficha de trabajo se abre EN LÍNEA, debajo de esa fila (P-5)

```
  ├───────────────────────────────────────────────────────────────────────────┤
  │ 27/07/2026 10:04          Antes de volver a una copia   980 KB            │
  │ hace 3 días   ⟨Versión anterior⟩                             [ Cerrar ▴ ] │
  │ ┌───────────────────────────────────────────────────────────────────────┐ │
  │ │ ⚠  Este resguardo es más viejo que el sistema de hoy.                  │ │
  │ │    Se puede usar igual: primero se traen los datos y después el        │ │
  │ │    sistema se pone al día solo. Puede tardar un poco más de lo         │ │
  │ │    normal. Si ese último paso falla, el sistema vuelve solo a como     │ │
  │ │    está ahora, sin perder nada. Esto vale para "Restaurar todo": las   │ │
  │ │    otras dos acciones pueden avisarte que este resguardo no les sirve. │ │
  │ └───────────────────────────────────────────────────────────────────────┘ │
  │                                                                           │
  │   Escribí  RESTAURAR TODO   [ ..................... ]                     │
  │   Tu contraseña             [ ..................... ]                     │
  │   ¿Por qué volvés a esta copia?                                           │
  │   [ .................................................................. ]  │
  │                                                                           │
  │   [ ⟲ Volver a esta copia ]      Ver qué contiene · Reponer configuración │
  │   ⓘ Para "Volver a esta copia" falta escribir el motivo (mínimo 10        │
  │     caracteres).                                                          │
  └───────────────────────────────────────────────────────────────────────────┘
```

- El **aviso de versión** ya no vive suelto arriba de tres botones: vive **dentro de la ficha de la
  copia elegida**, que es lo que lo causa. Texto **idéntico al firmado** (incluida la cláusula de
  alcance, P-20).
- **Un camino principal y dos secundarios** (recomendación, pregunta P5): el botón grande es
  "Volver a esta copia" (el "Restaurar todo" de hoy); "Ver qué contiene" y "Reponer configuración"
  quedan como acciones chicas al costado, para el que las necesite.
- **Se elimina el bloque "Qué hace cada acción" de tres viñetas** (P-15). Cada acción se explica en
  su propio "¿Seguro?", que es donde importa (los textos de esos "¿Seguro?" ya existen y no cambian).
- **El motivo se pide una sola vez, acá**, con rótulo en pregunta ("¿Por qué volvés a esta copia?").
  Sigue siendo obligatorio para "Volver a esta copia" y sigue sin pedirse para las otras dos.
- **El motivo del botón apagado va siempre a la vista, debajo, nombrando la acción** (P-9/P-10).
- Solo se puede tener **una ficha abierta a la vez**: al abrir otra copia, la anterior se cierra.

### 4.4 El "¿Seguro?" — un solo cartel emergente, el de siempre (P-4, P-14)

```
        ╔════════════════════════════════════════════════════════════╗
        ║  ⚠  ¿Restaurar TODO el sistema?                            ║
        ║                                                            ║
        ║  Esto devuelve TODO el sistema a como estaba el            ║
        ║  27/07/2026 10:04. Lo que hayas cargado después se pierde. ║
        ║  Antes se guarda una copia del estado actual, así podés    ║
        ║  volver a este momento si te arrepentís.                   ║
        ║  Este resguardo es más viejo: después de traer los datos,  ║
        ║  el sistema se pone al día solo.                           ║
        ║                                                            ║
        ║                     [ Volver ]   [ Sí, restaurar todo ]    ║
        ╚════════════════════════════════════════════════════════════╝
```

Mismo componente y mismos textos que hoy. **Es la ÚNICA ventana de todo el flujo** — y aparece
sobre la página, no sobre otra ventana.

### 4.5 Mientras trabaja (puede tardar minutos)

Es la única operación del producto donde el usuario **no puede seguir trabajando**: mientras corre,
el sistema entero queda tomado. Por eso se mantiene la pantalla de espera a pantalla completa que ya
existe, con el texto corregido para que no prometa de más (P-20; ver pregunta P7):

```
        ┌────────────────────────────────────────────────────────────┐
        │                          ◐                                 │
        │             Estamos volviendo a la copia                   │
        │       del 27/07/2026 10:04. No cierres esta ventana.       │
        │                                                            │
        │   ✓ Guardamos una copia de cómo está el sistema ahora      │
        │   ◐ Trayendo los datos de la copia elegida                 │
        │   ○ Poniendo el sistema al día                             │
        └────────────────────────────────────────────────────────────┘
```

Las otras dos acciones ("Ver qué contiene" / "Reponer configuración") **no** toman el sistema: su
espera es el botón con "Buscando…" / "Reponiendo…", como hoy.

### 4.6 Resultado: éxito

No hay ventana de éxito. La página se actualiza sola y muestra un cartel verde arriba de la lista
(mismo patrón firmado en H2 2026-06-24: PROCESANDO → ÉXITO → RECHAZO, todo en línea):

```
  ┌───────────────────────────────────────────────────────────────────────────┐
  │ ✔  Listo: el sistema volvió a como estaba el 27/07/2026 10:04.            │
  │    Antes de traer los datos guardamos una copia de cómo estaba hasta      │
  │    recién: la vas a ver primera en la lista de abajo.                     │
  │    Los demás usuarios van a tener que volver a entrar.          [ Cerrar ]│
  └───────────────────────────────────────────────────────────────────────────┘
```

Debajo, la lista ya refrescada, con la copia recién creada primera. El mensaje central del motor se
muestra **tal cual** (P-13).

### 4.7 Resultado: rechazo del motor (el feedback que hoy "no cierra")

El motor rechaza **antes de tocar nada**. Dos piezas, nunca apiladas:

1. El **cartel emergente único, en rojo**, con el motivo tal cual lo manda el motor (P-4, P-13).
2. Al cerrarlo, **la ficha de esa copia queda abierta, con todo lo cargado intacto** (P-7) y una
   línea roja fija pegada a la fila:

```
  │ 27/07/2026 10:04          Antes de volver a una copia   980 KB            │
  │ hace 3 días   ⟨Versión anterior⟩                             [ Cerrar ▴ ] │
  │ ┌───────────────────────────────────────────────────────────────────────┐ │
  │ │ ✖  No se pudo volver a esta copia. No se cambió nada.                 │ │
  │ │    [ Ver el motivo ]                                                  │ │
  │ └───────────────────────────────────────────────────────────────────────┘ │
```

"Ver el motivo" vuelve a abrir el mismo cartel emergente con el mismo texto. Nunca se culpa al
usuario y nunca se lo deriva a nadie (P-11): si la copia no sirve, el texto del motor lo dice.

### 4.8 Los otros tres estados de la pantalla

**Cargando:**
```
  ┌───────────────────────────────────────────────────────────────────────────┐
  │  ◐  Buscando las copias guardadas…                                        │
  └───────────────────────────────────────────────────────────────────────────┘
```

**Vacío (no hay ninguna copia):**
```
  ┌───────────────────────────────────────────────────────────────────────────┐
  │                              🗂                                            │
  │            Todavía no hay ninguna copia guardada.                         │
  │   El sistema guarda una sola cada vez que se usa "Empezar de cero".       │
  └───────────────────────────────────────────────────────────────────────────┘
```

**No se pudo traer la lista** (hoy no tiene salida — se agrega el reintento):
```
  ┌───────────────────────────────────────────────────────────────────────────┐
  │ ✖  No pudimos traer las copias guardadas.        [ Probar de nuevo ]      │
  └───────────────────────────────────────────────────────────────────────────┘
```

---

## 5. Qué NO hay que hacer (para el que implemente)

1. **NO** poner nada de esto en una ventana flotante, salvo el "¿Seguro?" y el cartel de rechazo
   (P-4/P-5).
2. **NUNCA** dos ventanas al mismo tiempo. Si hay una emergente abierta, no puede abrirse otra.
3. **NO** reescribir ni resumir los textos firmados de los tres avisos de versión, ni los tres
   "¿Seguro?", ni el mensaje que manda el motor (P-13, ADR-052).
4. **NO** apagar ningún botón por la marca de versión (decisión firmada 2026-07-29).
5. **NO** mostrar el nombre del archivo, ni rutas, ni "esquema", "migración", "base de datos",
   "dump", "tabla" (T-5, P-17).
6. **NO** avisar el error con un globito que se va solo (P-6).
7. **NO** perder lo cargado cuando algo falla (P-7).
8. **NO** volver a poner el bloque "Qué hace cada acción" de tres viñetas arriba de los botones
   (P-15): esa explicación vive en el "¿Seguro?" de cada acción.

---

## 6. Permisos

Igual que hoy: la pantalla vive dentro de `/admin`, que ya es **solo Admin**. Sin cambios.

---

## 7. Lo que necesita el motor (dependencias, si Gastón aprueba)

1. **Columna "Por qué se guardó"** (pregunta P4): hoy `GET /admin/danger/backups` devuelve solo
   fecha, tamaño y marca de versión. Habría que sumar un campo con el origen **ya traducido a
   criollo** por el motor ("Antes de empezar de cero" / "Antes de volver a una copia" / "Guardada a
   mano"), nunca el nombre del archivo (T-5).
2. **Pasos de la espera** (pregunta P7, opción con pasos): el motor tendría que decir en qué paso
   está. Si eso no existe, va la versión sin pasos (solo el texto y el girito).
3. Todo lo demás ya existe: la lista, los tres modos, el motivo obligatorio, la marca de versión y
   la pantalla de espera.

---

# PREGUNTAS PARA GASTON — ✅ RESPONDIDAS EL 2026-07-30 (todas con la recomendada: 1A a 12A)

> **Cerradas. No se reabren.** Se dejan escritas con sus dibujos porque son el registro de qué se
> le preguntó y qué eligió. La opción marcada (RECOMENDADA) en cada una es la que quedó firme.

---

### Tema 1 — Dónde vive esta función

**P1. Hoy "Volver atrás" se abre como una ventana encima de Mantenimiento. ¿Dónde querés que viva?**

- **A) Solapa propia "Copias de seguridad" en Administración, todo en la página (RECOMENDADA)**
```
 [Usuarios][Roles y Permisos][Comisiones][Auditoría][Mantenimiento][▸Copias de seguridad]
 ─────────────────────────────────────────────────────────────────────────────────────
  🛡 Copias de seguridad
  ┌──────────────────────────────────────────────────────────────┐
  │ 29/07/2026 22:33 · hace 1 día · 1,2 MB        [ Usar esta ▾ ] │
  │ 27/07/2026 10:04 · hace 3 días · ⟨Versión anterior⟩          │
  └──────────────────────────────────────────────────────────────┘
```
  *Por qué la recomiendo: es una tarea con su propia lista, merece su lugar; y Administración ya
  funciona con solapas, así que no aprendés nada nuevo.*

- **B) Se queda dentro de Mantenimiento, pero abajo de todo y en la misma página (sin ventana)**
```
  Mantenimiento
  ├ Lifecycle de reservas          [ Ejecutar ahora ]
  ├ Zona peligrosa
  │  Empezar de cero…
  │  ┌─ Copias guardadas ─────────────────────────────────────┐
  │  │ 29/07/2026 22:33 · 1,2 MB              [ Usar esta ▾ ] │
  │  └────────────────────────────────────────────────────────┘
```

- **C) Pantalla aparte, con su propia entrada en el menú de Gestión**
```
  GESTIÓN
  ├ Aprobaciones
  ├ Pendientes con AFIP
  ├ Administración
  └ ▸ Copias de seguridad        ← entrada nueva en el menú
```

---

**P2. "Empezar de cero" (el hermano de esta función) también es una ventana flotante hoy. ¿Qué hacemos?**

- **A) Se muda a la misma solapa nueva y también deja de ser ventana (RECOMENDADA)**
```
  🛡 Copias de seguridad
  ┌─ Volver a una copia ─────────────────────────────────┐
  │ (la lista de copias)                                 │
  └──────────────────────────────────────────────────────┘
  ┌─ Empezar de cero ────────────────────────────────────┐
  │ Elegí qué borrar…                                    │
  └──────────────────────────────────────────────────────┘
```
  *Por qué: son la misma familia (una borra y guarda copia, la otra vuelve a la copia). Tenerlas en
  dos lugares y con dos formas distintas confunde.*

- **B) Se queda como está ahora (ventana en Mantenimiento) y solo arreglamos "Volver atrás"**
```
  Mantenimiento → Zona peligrosa → [ Empezar de cero… ]  ← ventana, igual que hoy
```

---

### Tema 2 — Cómo se ve la lista de copias

**P3. ¿Cómo querés ver cada copia guardada?**

- **A) Tabla con columnas, un botón por fila (RECOMENDADA)**
```
  CUÁNDO SE GUARDÓ      POR QUÉ SE GUARDÓ           TAMAÑO
  29/07/2026 22:33      Antes de empezar de cero    1,2 MB    [ Usar esta ▾ ]
  hace 1 día
  ─────────────────────────────────────────────────────────────────────────
  27/07/2026 10:04      Antes de volver a una copia  980 KB   [ Usar esta ▾ ]
  hace 3 días  ⟨Versión anterior⟩
```

- **B) Tarjetas apiladas, una por copia**
```
  ┌────────────────────────────────────┐   ┌────────────────────────────────────┐
  │ 29/07/2026 22:33 · hace 1 día      │   │ 27/07/2026 10:04 · hace 3 días     │
  │ Antes de empezar de cero · 1,2 MB  │   │ ⟨Versión anterior⟩ · 980 KB        │
  │            [ Usar esta copia ]     │   │            [ Usar esta copia ]     │
  └────────────────────────────────────┘   └────────────────────────────────────┘
```

- **C) Como hoy: un renglón corrido con la bolita para elegir**
```
  ( ) Resguardo del 29/07/2026 22:33 — 1,2 MB
  (•) Resguardo del 27/07/2026 10:04 — 980 KB   ⟨Versión anterior⟩
```

---

**P4. ¿Querés ver POR QUÉ se guardó cada copia?** (hoy no se ve; el motor lo tendría que empezar a mandar)

- **A) Sí, una columna en criollo (RECOMENDADA)**
```
  29/07/2026 22:33   Antes de empezar de cero
  27/07/2026 10:04   Antes de volver a una copia
  12/07/2026 08:12   Guardada a mano
```
  *Por qué: en una emergencia, saber cuál es "la de antes del desastre" es lo primero que buscás.*

- **B) No hace falta: con la fecha y el tamaño me arreglo**
```
  29/07/2026 22:33 · 1,2 MB
  27/07/2026 10:04 · 980 KB
```

---

**P5. ¿Cómo se muestra la antigüedad de cada copia?**

- **A) Fecha y hora completas + "hace cuánto" debajo (RECOMENDADA)**
```
  29/07/2026 22:33
  hace 1 día
```
- **B) Solo la fecha y hora**
```
  29/07/2026 22:33
```
- **C) "Hoy 22:33" / "Ayer 10:04" / y para las más viejas la fecha**
```
  Hoy 22:33
  Ayer 10:04
  12/07/2026 08:12
```

---

### Tema 3 — Las tres acciones

**P6. Hoy hay tres botones iguales y no se sabe cuál es el camino normal. ¿Cómo los ordenamos?**

- **A) Uno grande y dos chiquitos al costado (RECOMENDADA)**
```
  [ ⟲ Volver a esta copia ]        Ver qué contiene · Reponer configuración
```
  *Por qué: el 95% de las veces querés volver a la copia entera. Las otras dos son para casos raros
  y no tienen que competir con la principal.*

- **B) Los tres iguales, como hoy**
```
  [ Ver qué contiene ] [ Restaurar configuración ] [ Restaurar todo ]
```

- **C) Uno grande, y las otras dos escondidas detrás de "Más opciones"**
```
  [ ⟲ Volver a esta copia ]
  Más opciones ▾
```

---

**P7. ¿Cómo llamamos a la acción principal?** (hoy dice "Restaurar todo", y la función entera se llama
"Volver atrás" — que es la misma palabra que usás para retroceder una reserva de etapa)

- **A) La función se llama "Copias de seguridad" y el botón "Volver a esta copia" (RECOMENDADA)**
```
  Solapa: Copias de seguridad     Botón: [ ⟲ Volver a esta copia ]
```
- **B) La función se llama "Resguardos" y el botón "Restaurar todo"**
```
  Solapa: Resguardos              Botón: [ Restaurar todo ]
```
- **C) Se queda todo como está: "Volver atrás" y "Restaurar todo"**
```
  Botón en Mantenimiento: [ Volver atrás… ]   Botón final: [ Restaurar todo ]
```

---

### Tema 4 — Mientras trabaja y cuando termina

**P8. Restaurar puede tardar segundos hoy, pero minutos con muchos datos. Hoy la pantalla de espera
dice "Volvemos en un minuto". ¿Qué querés que diga?**

- **A) Sin promesa de tiempo, y mostrando en qué paso va (RECOMENDADA)**
```
                          ◐
           Estamos volviendo a la copia del 27/07/2026 10:04
                   No cierres esta ventana.

     ✓ Guardamos una copia de cómo está el sistema ahora
     ◐ Trayendo los datos de la copia elegida
     ○ Poniendo el sistema al día
```
  *Por qué: prometer "un minuto" y tardar cinco es exactamente el "mal feedback". Ver el paso te
  dice que está vivo.*

- **B) Sin promesa de tiempo, pero sin pasos (más simple)**
```
                          ◐
           Estamos volviendo a la copia del 27/07/2026 10:04
          Puede tardar unos minutos. No cierres esta ventana.
```

- **C) Como está hoy**
```
                          ◐
              Estamos restaurando el sistema
                  Volvemos en un minuto
```

---

**P9. Cuando termina bien, ¿qué ves?**

- **A) Cartel verde arriba de la lista, en la misma página (RECOMENDADA)**
```
  ✔ Listo: el sistema volvió a como estaba el 27/07/2026 10:04.
    Guardamos una copia de cómo estaba hasta recién: es la primera de la lista.
    Los demás usuarios van a tener que volver a entrar.            [ Cerrar ]
  ─────────────────────────────────────────────────────────────────────────
  (la lista, ya actualizada)
```
- **B) Una ventana de "listo" que tenés que cerrar (como hoy)**
```
        ╔══════════════════════════════════════════╗
        ║ ✔ Se restauró todo el sistema            ║
        ║                             [ Cerrar ]   ║
        ╚══════════════════════════════════════════╝
```
- **C) Que la pantalla se recargue sola y no diga nada**

---

**P10. Cuando el motor rechaza la copia (no se puede usar), ¿cómo te lo mostramos?**

- **A) El cartel rojo de siempre y, al cerrarlo, una marca roja pegada a esa copia (RECOMENDADA)**
```
        ╔════════════════════════════════════════════╗
        ║ ✖ (el motivo, tal cual lo dice el motor)   ║
        ║                            [ Entendido ]   ║
        ╚════════════════════════════════════════════╝
  ↓ al cerrar
  │ 27/07/2026 10:04  ⟨Versión anterior⟩                        [ Cerrar ▴ ] │
  │ ✖ No se pudo volver a esta copia. No se cambió nada. [ Ver el motivo ]   │
```
  *Por qué: el cartel te obliga a leerlo, y después queda la marca pegada a la copia que falló, así
  no te olvidás cuál fue.*

- **B) Solo el cartel rojo, y cuando lo cerrás no queda nada (como hoy)**
```
        ╔════════════════════════════════════════════╗
        ║ ✖ (el motivo)              [ Entendido ]   ║
        ╚════════════════════════════════════════════╝
```
- **C) Sin cartel: el motivo aparece en rojo pegado a la copia, dentro de la página**
```
  │ 27/07/2026 10:04  ⟨Versión anterior⟩                        [ Cerrar ▴ ] │
  │ ✖ (el motivo completo, acá adentro)                                      │
```

---

**P11. Dónde escribís la frase, la contraseña y el motivo.**
(Esto NO agrega ningún paso: es la misma confirmación de siempre, la pregunta es dónde vive)

- **A) Dentro de la ficha de la copia elegida, en la página; el "¿Seguro?" queda como único cartel (RECOMENDADA)**
```
  │ 27/07/2026 10:04  ⟨Versión anterior⟩                        [ Cerrar ▴ ] │
  │  Escribí RESTAURAR TODO   [ .................. ]                        │
  │  Tu contraseña            [ .................. ]                        │
  │  ¿Por qué volvés a esta copia?  [ ............................. ]       │
  │  [ ⟲ Volver a esta copia ]   Ver qué contiene · Reponer configuración   │
```
- **B) Todo dentro del cartel "¿Seguro?" (la lista queda limpia, sin casilleros)**
```
        ╔══════════════════════════════════════════════════╗
        ║ ⚠ ¿Volver a la copia del 27/07/2026 10:04?       ║
        ║  Escribí RESTAURAR TODO  [ ................ ]    ║
        ║  Tu contraseña           [ ................ ]    ║
        ║  ¿Por qué?               [ ................ ]    ║
        ║              [ Volver ]  [ Sí, volver a esta ]   ║
        ╚══════════════════════════════════════════════════╝
```
- **C) Como hoy: los casilleros arriba de los tres botones, en la ventana grande**

---

**P12. El bloque "Qué hace cada acción" (las tres viñetas explicativas de hoy).**

- **A) Se saca de la pantalla; cada acción se explica en su "¿Seguro?" (RECOMENDADA)**
```
  [ ⟲ Volver a esta copia ]   →  ╔═══════════════════════════════════════╗
                                 ║ ⚠ Esto devuelve TODO el sistema a...  ║
                                 ╚═══════════════════════════════════════╝
```
  *Por qué: tu regla de siempre — si un botón necesita un párrafo al lado para entenderse, el
  problema es el botón. Y la explicación sirve justo antes de apretar, no antes de elegir.*

- **B) Se queda como está, arriba de los botones**
```
  ⓘ Qué hace cada acción:
    · Ver qué contiene: arma una copia de prueba…
    · Restaurar configuración: repone solo las partes vacías…
    · Restaurar todo: vuelve TODO el sistema…
```
- **C) Se queda pero escondido detrás de un "¿Qué hace cada una?" que se abre si lo tocás**
```
  ¿Qué hace cada una? ▾
```
