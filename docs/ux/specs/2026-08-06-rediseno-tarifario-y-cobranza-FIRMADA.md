# Rediseño de Tarifario y de Cobranzas — ESPECIFICACIÓN FIRMADA (tanda B1)

> **Fecha:** 2026-08-06 · **Firmada por Gastón** (respuestas P1..P19) · **Autor:** `ux-ui-disenador`
> Reemplaza a `docs/ux/specs/2026-08-06-rediseno-tarifario-y-cobranza-PROPUESTA.md` (histórico).
> **Esto es lo que `frontend-senior` implementa al pie de la letra.** Cualquier desvío necesario
> (por costo técnico o regla de negocio) se le repregunta a Gastón ANTES de desviarse.
>
> Las reglas nuevas ya quedaron escritas en `docs/ux/guia-ux-gaston.md`, sección
> **"Tarifario que se arma solo + Cobranzas: la vista de deudores (2026-08-06)"**.
>
> **Reglas de la constitución que aplica esta spec:** P-3 · P-4/P-5 · P-9 · P-10 · P-11 · P-15 ·
> P-16 · P-17/T-5 · P-18 · P-21 · F-14 · T-13 · T-14.
>
> ⚠️ **Tres detalles quedaron abiertos** al bajar las respuestas (§7). No frenan la obra: cada uno
> dice qué se hace mientras tanto.

---

## 1. Resumen de lo firmado

| # | Decisión | Respuesta |
|---|---|---|
| 1 | El **Tarifario** pasa a ser **la memoria de lo que ya vendiste** (lista de productos aprendidos con el último precio por operador y fecha). Además se puede cargar a mano. | P1=B |
| 2 | El **formulario largo de 20 campos MUERE como puerta normal**. El alta a mano es una **fichita de pocos campos**. El formulario completo **sobrevive solo** como camino **"Carga completa"** para casos especiales (vigencias, variaciones de habitación). | P1=B + P4=A |
| 3 | **Una sola lista**: las tarifas viejas entran como un producto más, **sin decir de dónde salieron**. | P2=A |
| 4 | Se sigue llamando **"Tarifario"**. | P3=A |
| 5 | Cada renglón muestra **lo esencial**: nombre + ciudad/tipo · operador · último precio · fecha. | P5=A |
| 6 | **Un renglón por operador** debajo del producto. | P6=A |
| 7 | **"Hay que evitar repetidos a toda costa, que el sistema aprenda a que no haya repetidos"** (textual). La **prevención al crear es prioridad absoluta**; la pantalla de unir repetidos es la **red de seguridad**, no la herramienta principal. | P7 |
| 8 | El buscador que aprende de las ventas **sale directo para todos** y **la llave desaparece** de Configuración. | P8=A |
| 9 | Debajo del precio sugerido va un **renglón gris** con **operador · precio · fecha**. | P9=A |
| 10 | Si el precio es **viejo (más de 60 días)**, se sugiere igual con la **fecha en ámbar**. | P10=A |
| 11 | En Cobranzas, **"Por reserva" se va** y entran **"Viajan pronto y deben"** y **"Deuda por cliente"**. | P11=A |
| 12 | "Viajan pronto y deben" lista **a todos los que deben, ordenados por fecha de salida**. | P12=A |
| 13 | "Deuda por cliente" es una **lista propia** de los que deben; la fila abre la ficha del cliente. | P13=A |
| 14 | "Pendientes de facturar": la fila **lleva a la ficha de la reserva**. Muere la ventana vieja de facturar en esa solapa. | P14=A |
| 15 | **El saldo tiene que estar completo X días antes de la salida**, un número en Configuración, **default 21**. | P15=A |
| 16 | Pasada esa fecha: **rojo en la lista + aviso en la campanita. No traba nada.** | P16=A |
| 17 | Menú: **dos puertas** — **"Cobranzas"** y **"Facturación"**. | P17=B |
| 18 | Recordatorios: **solo el hueco**. El disparador vivirá en la ficha; la lista solo muestra si ya se avisó. **No se construye ahora.** | P18=A |
| 19 | Cuando se construya, el recordatorio sale **por WhatsApp**. **No se construye ahora.** | P19=A |

---

## 2. Pantalla `Tarifario`

**Menú:** CATÁLOGO → **Tarifario** (sin cambios de nombre ni de lugar). Permiso: `tarifario.view`.

### 2.1 Layout

```
┌───────────────────────────────────────────────────────────────────────────────────────┐
│  Tarifario                                                          [ + Agregar producto ]
│  Los productos que ya vendiste, con el último precio de cada operador.                │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  [ 🔍 Buscar hotel, vuelo, paquete…            ]   Tipo [Todos ▾]  Operador [Todos ▾] │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  PRODUCTO                        TIPO      OPERADOR          ÚLTIMO PRECIO    CUÁNDO  │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Maitei Posadas                  Hotel     Ola Mayorista     US$ 48/noche   22/05/2026│
│    Posadas, Misiones                       Julia Tours       US$ 52/noche   03/07/2026│
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Buenos Aires – Miami            Aéreo     Aeromundo         US$ 780        14/06/2026│
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Bariloche 4 noches con aéreo    Paquete   Ñandú Turismo     $ 410.000      01/08/2026│
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Asistencia Universal 15 días    Asist.    Universal         US$ 62         28/07/2026│
└───────────────────────────────────────────────────────────────────────────────────────┘
```

**Reglas del listado:**

- **Una sola lista** (P2=A): las tarifas que hoy están cargadas a mano aparecen como un producto
  más. **Nunca se dice de dónde salió un producto** (la etiqueta "creado en venta" está derogada
  desde el 2026-06-08).
- **Un renglón por operador** debajo del producto (P6=A), ordenados por fecha del precio, el más
  nuevo arriba. El nombre y la ciudad se escriben una sola vez, en el primer renglón (P-16).
- Columnas exactas y en este orden: **Producto** (nombre + ciudad/subtítulo debajo, en gris) ·
  **Tipo** · **Operador** · **Último precio** · **Cuándo** (fecha `dd/MM/aaaa`, P-2/T-14).
- **Sin permiso `cobranzas.see_cost`**: la columna "Último precio" muestra el **precio de venta**,
  nunca el costo (F-14, guía 2026-06-05). No se muestra un "—": se muestra el precio de venta.
- Un precio **de más de 60 días** lleva la **fecha en ámbar** (mismo criterio de P10=A y del
  umbral de "costo a confirmar" ya existente). El resto de la fila no cambia de color.
- La fecha en la columna "Cuándo" es la de la venta que dejó ese precio.
- **Se eliminan** las tarjetas de arriba de la pantalla actual (Total / Hoteles / **Vencidas —
  "Acción requerida"**): una tarifa "vencida" no es una tarea pendiente en un tarifario que se
  arma solo, y "Acción requerida" es de las frases que P-17 no permite.

### 2.2 Ficha del producto (en línea, al tocar el renglón)

```
│  Maitei Posadas                  Hotel     Ola Mayorista     US$ 48/noche   22/05/2026│
│  ┌─────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Nombre *  [ Maitei Posadas                ]   Ciudad *  [ Posadas          ]   │  │
│  │                                                                                 │  │
│  │  Precios que aprendió de tus ventas                                             │  │
│  │    Ola Mayorista      US$ 48 /noche     22/05/2026     F-2026-1042              │  │
│  │    Julia Tours        US$ 52 /noche     03/07/2026     F-2026-1109              │  │
│  │                                                                                 │  │
│  │  [ Carga completa ]                          [ Cancelar ]   [ Guardar ]         │  │
│  └─────────────────────────────────────────────────────────────────────────────────┘  │
```

- Se abre **EN LÍNEA, debajo del renglón** (P-5). Nunca una ventana flotante.
- Se puede corregir **nombre** y **ciudad** (los dos obligatorios para un hotel — la ciudad es el
  arma principal contra los repetidos, guía 2026-06-05).
- Cada precio muestra **operador · precio · fecha · número de reserva**, y **el número de reserva
  es un enlace** a su ficha.
- **Los precios NO se editan a mano acá**: son la memoria de lo que pasó. Se cambian vendiendo.
- **`[ Carga completa ]`** es un botón **secundario y discreto**, abajo a la izquierda: abre el
  formulario largo de hoy (vigencias, variaciones de habitación, aerolínea/IATA/clase/equipaje,
  régimen, % de menores) para el producto que estás mirando. Es el **único** lugar desde donde se
  llega a ese formulario.

### 2.3 Alta a mano — fichita de pocos campos

Botón **`[ + Agregar producto ]`** arriba a la derecha. Abre **en línea, arriba del listado**:

```
│  ┌─────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Tipo [ Hotel ▾ ]   Nombre *  [                              ]                  │  │
│  │  Ciudad * [                 ]   Operador [ Ola Mayorista  ▾ ]                   │  │
│  │  Precio   [ US$ ▾ ] [           ]  por noche                                    │  │
│  │                                                                                 │  │
│  │  [ Carga completa ]                          [ Cancelar ]   [ Guardar ]         │  │
│  └─────────────────────────────────────────────────────────────────────────────────┘  │
```

- Campos: **Tipo · Nombre\* · Ciudad\* (solo Hotel) · Operador · Precio + moneda** (+ la unidad
  que corresponda al tipo: "por noche" en hotel, sin unidad en los demás).
- **Sin leyendas ni "(opcional)"**: los obligatorios llevan asterisco y listo (P-15).
- La moneda **soporta pesos y dólares** (guía 2026-06-05); no se asume dólar.
- **Antes de guardar corre el freno de repetidos** de §2.4. Es obligatorio también acá.
- `[ Carga completa ]` lleva lo tipeado al formulario largo, sin perder nada.

### 2.4 Evitar repetidos — prioridad absoluta

> Palabra textual de Gastón (2026-08-06): **"HAY QUE EVITAR REPETIDOS A TODA COSTA, QUE EL SISTEMA
> APRENDA A QUE NO HAYA REPETIDOS."**

**La prevención al crear es la prioridad número uno.** Se refuerza lo firmado el 2026-06-05 y se
aplica en **los dos lugares donde nace un producto** (el buscador de la carga de servicios y la
fichita de "Agregar producto"):

1. **Búsqueda tolerante al tipeo**: "maitei posada", "Maytei", "MAITEI POSADAS" tienen que
   encontrar "Maitei Posadas".
2. **Los parecidos se muestran SIEMPRE antes de dejar crear**, aunque el texto no coincida exacto.
3. **"Crear nuevo" es SIEMPRE la última opción de la lista**, nunca la primera ni un botón suelto.
4. **Si hay un parecido fuerte, el sistema frena y pregunta antes de crear** — Cartel emergente de
   confirmación (ámbar, patrón único 2026-07-22), con el texto del motor:

```
   ┌──────────────────────────────────────────────────────────┐
   │  ⚠  Confirmá antes de seguir                         ✕   │
   │                                                          │
   │  Ya tenés "Maitei Posadas" en Posadas, Misiones.         │
   │  Si es el mismo hotel, elegí ese y evitás tenerlo dos    │
   │  veces con precios distintos.                            │
   │                                                          │
   │        [ Usar "Maitei Posadas" ]   [ Crear uno nuevo ]   │
   └──────────────────────────────────────────────────────────┘
```

5. **El sistema aprende**: cuando alguien elige "Usar el que ya existe" para un texto que había
   escrito distinto, ese texto queda asociado al producto y la próxima vez lo encuentra derecho.
6. **La pantalla de unir repetidos es la red de seguridad**, no la herramienta principal (firmada
   el 2026-06-05). Vive como una **solapa "Repetidos (N)"** dentro del Tarifario; si no hay
   ninguno, la solapa se ve apagada con "0" (criterio de solapas en cero, 2026-08-03 P3=B):

```
│  [ Todos ]   [ Repetidos (3) ]                                                        │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Maitei Posadas · Posadas        Maitei Posada · Posadas                              │
│  Ola · US$ 48 · 22/05/2026       Ola · US$ 47 · 12/04/2026                            │
│                                  [ Es el mismo: unirlos ]   [ Son distintos ]         │
```

- Al unir, **nada se borra** (2026-08-03): el producto que queda absorbe los precios del otro, y
  el otro deja de listarse.
- "Son distintos" hace que el par no vuelva a aparecer.

### 2.5 Estados de la pantalla

| Estado | Qué se ve |
|---|---|
| Cargando | Renglones grises (mismo patrón que el resto del sistema). Sin cartel. |
| Vacío total | "Todavía no hay productos. El tarifario se arma solo: la primera vez que cargues un servicio, el producto queda guardado acá." + botón `[ + Agregar producto ]`. |
| Buscador sin resultados | "No encontramos '{texto}' en tu tarifario." (texto ya firmado, 2026-06-06). |
| Error al traer la lista | Cartel rojo + botón **"Probar de nuevo"** (mismo criterio que Reservas y Copias de seguridad). |
| Guardado OK | "Producto guardado." y la ficha se cierra. |
| Error al guardar | La ficha **queda abierta con todo lo cargado intacto** + cartel rojo arriba de los botones: "No se pudo guardar. Revisá la conexión y probá de nuevo." Se reintenta en el mismo botón (Ronda 2, 2026-06-06). |
| Sin permiso de costos | Se ve toda la pantalla, con **precio de venta** en la columna de precio (F-14). |

### 2.6 Qué NO hacer en el Tarifario

- ❌ El formulario largo como puerta de entrada (solo detrás de `[ Carga completa ]`).
- ❌ Ventanas flotantes para crear/editar un producto (P-5).
- ❌ Etiquetas de origen del producto ("creado en venta", "cargado a mano").
- ❌ Tarjetas de resumen arriba, y menos si suman monedas (P-3).
- ❌ La palabra "Vencida" / "Acción requerida" sobre una tarifa.
- ❌ Botones de borrar producto (nada se borra, 2026-08-03: se une o se archiva).
- ❌ Mostrar costos a quien no tiene `cobranzas.see_cost`.

---

## 3. El precio sugerido al cargar un servicio

**Dónde:** la ficha de carga de servicio en línea (`ServiceInlineCard` / `ProductSearchField`).
**Cambio de alcance (P8=A): sale para TODOS, sin llave.**

### 3.1 Buscador (queda como está, ya firmado)

```
│  Producto *   [ maitei                                            🔍 ]                │
│               ┌───────────────────────────────────────────────────────┐               │
│               │  Maitei Posadas                     En tu tarifario   │               │
│               │  Posadas · Ola Mayorista · US$ 48/noche · 22/05/2026  │               │
│               ├───────────────────────────────────────────────────────┤               │
│               │  Maitei Villa Gesell                En tu tarifario   │               │
│               │  Villa Gesell · Ola · US$ 39/noche · 11/01/2026       │               │
│               ├───────────────────────────────────────────────────────┤               │
│               │  + No es ninguno: crear "maitei" como hotel nuevo     │               │
│               │    Revisá los de arriba antes — si ya existe,         │               │
│               │    elegirlo evita duplicados.                         │               │
│               └───────────────────────────────────────────────────────┘               │
```

Sin cambios respecto de lo firmado: parecidos primero, crear último, "Buscando…" sutil mientras
busca, y "No encontramos '{texto}' en tu tarifario" cuando no hay nada. **Se le suma el freno de
§2.4 punto 4** cuando el parecido es fuerte.

### 3.2 Lo que cambia: el renglón gris de procedencia (P9=A, P10=A)

```
│  Operador     [ Ola Mayorista            ▾ ]   ← precargado, amarillo                 │
│  Costo/noche  [ US$ 48,00                  ]   ← precargado, amarillo                 │
│               Último precio: Ola Mayorista · US$ 48 · 22/05/2026                      │
```

Cuando el precio tiene **más de 60 días**, la parte de la fecha va en **ámbar**:

```
│  Costo/noche  [ US$ 48,00                  ]                                          │
│               Último precio: Ola Mayorista · US$ 48 · 22/05/2026 (hace 5 meses)       │
│                                                            └── ámbar ──┘              │
```

- El renglón es **gris, una sola línea, informativo** (regla firmada 2026-08-03 P11=A).
- **Solo aparece si hay un precio aprendido.** Si el producto es nuevo, no hay renglón (no se
  escribe "sin precio anterior": eso sería un cartelito, P-15).
- **"hace 5 meses" lo calcula el motor** y viene listo en el dato (T-13); el front no resta fechas.
- **Nunca pisa lo tipeado** (P-21): si el vendedor ya escribió un precio, la sugerencia **no
  reemplaza el número**; el renglón gris igual se muestra, para que compare.
- **Sin permiso de costos**: el renglón muestra el **precio de venta** de la última vez, con la
  misma forma, y el casillero de costo lo completa el motor por detrás (guía 2026-06-05).

---

## 4. Cobranzas

**Menú (P17=B):** dentro de VENTAS quedan **dos puertas** con nombres que no se pisan:

```
   ANTES                                    AHORA
   VENTAS                                   VENTAS
     Clientes                                 Clientes
     Posibles clientes                        Posibles clientes
     Cobranza y Facturación   ← ¿?            Cobranzas      ← a quién le cobro
     Facturación              ← ¿?            Facturación    ← los comprobantes emitidos
```

- **"Cobranzas"** (`/payments`, permiso `cobranzas.view`): el título de la pantalla pasa a ser
  **"Cobranzas"** y la bajada, **"A quién le tenés que cobrar y los cobros que entraron."**
- **"Facturación"** (`/facturacion`, permiso `cobranzas.view_all`): sin cambios en esta tanda.

### 4.1 Solapas de Cobranzas (P11=A)

```
  [ Viajan pronto y deben ]   [ Deuda por cliente ]   [ Pendientes de facturar ]   [ Movimientos ]
```

- **"Por reserva" se elimina** (repetía lo que ya da la ficha de la reserva).
- **También se saca la solapa "NC por revisar"** que todavía sobrevive en esta barra: sus entradas
  fueron derogadas el **2026-07-08** ("fin de las bandejas por tipo de comprobante"); hoy solo
  redirige a Facturación. Sacarla es aplicar una regla ya firmada.

### 4.2 Solapa "Viajan pronto y deben" (P12=A, P15=A, P16=A)

```
┌ Cobranzas ────────────────────────────────────────────────────────────────────────────┐
│  A quién le tenés que cobrar y los cobros que entraron.                               │
│  [ Viajan pronto y deben ]  [ Deuda por cliente ]  [ Pendientes de facturar ]  [ Movimientos ]
├───────────────────────────────────────────────────────────────────────────────────────┤
│  Falta cobrar:  $ 1.240.000 · US$ 3.150                        [ 🔍 Buscar cliente… ] │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  SALE              RESERVA / DESTINO         CLIENTE          TOTAL       FALTA       │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│ 🔴 en 3 días       F-2026-1042 · Cancún      Fam. García      US$ 2.400   US$ 900     │
│  12/08/2026        El saldo tenía que estar completo el 22/07/2026                    │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  en 11 días        F-2026-1067 · Bariloche   Pérez, Ana       $ 610.000   $ 210.000   │
│  20/08/2026                                                                           │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  en 26 días        F-2026-1071 · Río         López, Juan      $ 380.000   $ 95.000    │
│  04/09/2026                                                                           │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- **Lista pasiva** (2026-07-08): **la fila entera abre la ficha de la reserva**. **Sin botones por
  fila**, sin cobrar desde acá.
- **Están TODOS los que deben** (P12=A), ordenados por **fecha de salida**, el que sale primero
  arriba. Sin filtro de días, sin corte.
- Columna **SALE**: cuenta regresiva arriba ("en 3 días", "en 4 meses") y la fecha
  `dd/MM/aaaa` debajo, en gris. **La cuenta regresiva la calcula el motor** (T-13, T-14).
- Columna **FALTA**: lo que falta cobrar, **por moneda separada** (`$ 95.000 · US$ 120` si la
  reserva tiene las dos). Jamás un número mezclado (P-3).
- **Renglón rojo (P16=A):** cuando **ya pasó la fecha en que el saldo tenía que estar completo**
  (§4.5), la fila se marca en rojo y debajo del número de reserva aparece **una línea** con el
  texto del motor: *"El saldo tenía que estar completo el 22/07/2026."* **No traba nada.**
- La franja de arriba muestra **el total que falta cobrar por moneda**, en una línea (P-3).
- **No entran** las reservas Anuladas ni Perdidas.
- **Vacío:** "Ninguna reserva que salga pronto tiene saldo pendiente."
- **Error:** cartel rojo + "Probar de nuevo".

### 4.3 Solapa "Deuda por cliente" (P13=A)

```
├───────────────────────────────────────────────────────────────────────────────────────┤
│  Te deben:  $ 1.240.000 · US$ 3.150                            [ 🔍 Buscar cliente… ] │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  CLIENTE                 RESERVAS CON DEUDA     DEBE                  PRIMERA SALIDA  │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│ 🔴 Fam. García           2                      US$ 900               12/08/2026      │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Pérez, Ana              1                      $ 210.000             20/08/2026      │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  López, Juan             3                      $ 95.000 · US$ 120    04/09/2026      │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- **Solo los clientes que deben.** El que no debe nada no aparece.
- **Lista pasiva**: la fila abre **la ficha del cliente que ya existe** (`/customers/:id/account`),
  con su extracto firmado el 2026-07-16. No se duplica ninguna pantalla.
- **DEBE**: total del cliente **cruzando todas sus reservas**, **por moneda separada** (P-3).
- **PRIMERA SALIDA**: la salida más próxima entre sus reservas con deuda.
- **Punto rojo** en el cliente que tiene al menos una reserva con el saldo pasado de fecha (§4.5).
- **Orden:** por **primera salida**, la más próxima arriba (ver §7, detalle abierto D2).
- **Vacío:** "Ningún cliente tiene saldo pendiente."

### 4.4 Solapa "Pendientes de facturar" (P14=A)

- La lista **queda**, con las mismas columnas de hoy.
- **La fila entera lleva a la ficha de la reserva**, donde emitir la factura ya vive en línea
  (firmado 2026-06-13 / ADR-037).
- **Muere el uso de la ventana vieja de facturar (`CreateInvoiceModal`) en esta solapa.** No se
  reemplaza por otra ventana: no hay ningún botón de facturar en la lista.

```
│  RESERVA / DESTINO           CLIENTE          SIN FACTURAR       ESTADO               │
│  F-2026-1042 · Cancún        Fam. García      US$ 2.400          Sin facturar         │
│  F-2026-1067 · Bariloche     Pérez, Ana       $ 210.000          Facturada en parte   │
```

- **Vacío:** "No hay reservas pendientes de facturar."

### 4.5 "El saldo tiene que estar completo X días antes de la salida" (P15=A)

**Configuración → Operativa / Cobranzas y Facturación**, en el mismo bloque donde ya vive el aviso
de reservas próximas con deuda:

```
│  Cobranzas                                                                            │
│  ─────────────────────────────────────────────────────────────────────────────────    │
│  El saldo tiene que estar completo  [ 21 ]  días antes de la salida.                  │
│                                                                                       │
│  [✓] Alertas por reservas próximas con deuda                                          │
│      Días previos para alertar  [ 7 ]                                                 │
```

- **Default: 21 días.** Un solo número, vale para todas las reservas. Nadie carga fechas a mano en
  ninguna reserva (coherente con la Ronda 8 del 2026-06-06, que sacó el campo "Fecha límite").
- **La fecha concreta la calcula el motor** por reserva (fecha de salida − X días) y la manda
  lista, junto con el "ya se pasó" sí/no (T-13). El front **nunca** resta fechas.
- **Qué produce (P16=A), y nada más:**
  1. la fila **roja** con su línea en la lista de deudores (§4.2);
  2. un **aviso en la campanita** para el vendedor de esa reserva y para los admins (mismo criterio
     que "Próximos inicios": cada vendedor las suyas, el admin todas).
- **NO traba nada**: no impide facturar, ni emitir voucher, ni cargar servicios. El único freno de
  plata que existe sigue siendo el ya firmado ("no viaja si debe").
- Voz del aviso de la campanita (P-17, sujeto = la reserva, cero jerga):
  **"F-2026-1042 · Fam. García — el saldo tenía que estar completo el 22/07. Falta US$ 900."**

### 4.6 Qué NO hacer en Cobranzas

- ❌ Cobrar o facturar desde una lista (la acción vive en la ficha, 2026-07-08).
- ❌ Antigüedad de deuda 30/60/90 (derogada 2026-07-16 P5=A: acá manda la fecha de viaje).
- ❌ Un total que sume pesos y dólares (P-3).
- ❌ Repetir el detalle de movimientos de una reserva (ya está en su ficha, P-16).
- ❌ Mostrar costos o deuda al operador en estas pantallas (son del lado cliente).
- ❌ Trabar cualquier acción por la fecha límite de pago (P16=A).

---

## 5. Recordatorios de cobro — SOLO EL HUECO (no se construye)

Queda dibujado para que la obra de hoy no lo bloquee:

- **El disparador va a vivir en la ficha** (de la reserva o del cliente), como un botón
  **"Recordar pago"** junto a "Registrar cobro" y "Emitir factura" (P18=A).
- **La lista de deudores solo va a mostrar si ya se avisó y cuándo**, en un **renglón gris de una
  línea** debajo de la reserva: *"Último aviso: 02/08/2026."* **Sin botón** (lista pasiva).
- **Sale por WhatsApp** (P19=A), reusando el envío que ya existe para los vouchers.
- El texto lo arma el motor (T-13) y habla como manda P-17.
- **Nada de esto entra en esta tanda.** Lo único que hay que respetar hoy: dejar el lugar del
  renglón gris en la fila de la lista de deudores.

---

## 6. Dependencias del motor (para el brief del backend)

> Ninguna de estas es decisión de UX. Sin ellas la pantalla no se puede pintar. Todo dato derivado
> viene **calculado del motor** (T-13); el front no resta, no suma y no convierte monedas.

| # | Qué hace falta | Para qué |
|---|---|---|
| M-1 | **Lista de productos aprendidos**: producto (nombre, tipo, ciudad/subtítulo) + **un renglón por operador** con su **último precio, moneda, unidad, fecha** y el **número de reserva** de esa venta. Con buscador por texto tolerante al tipeo, filtro por tipo y por operador, y paginado. Debe **incluir las tarifas viejas** cargadas a mano como un producto más (P2=A). Enmascarado: sin `cobranzas.see_cost` devuelve **precio de venta**, nunca costo (F-14). | §2.1 |
| M-2 | **Marca de "precio viejo"** calculada por el motor (umbral 60 días, el mismo que ya usa "costo a confirmar") + el texto relativo listo ("hace 5 meses"). El front no calcula antigüedad. | §2.1, §3.2 |
| M-3 | **Alta a mano de un producto** con pocos campos (tipo, nombre, ciudad, operador, precio+moneda+unidad), pasando por el **mismo control de repetidos** que el alta desde la venta. | §2.3 |
| M-4 | **Detección de parecidos reforzada** (P7): búsqueda tolerante al tipeo, veredicto de "parecido fuerte" que dispara el freno antes de crear, y **aprendizaje del alias** cuando el usuario elige "usar el que ya existe". El motivo del freno viaja **por código**, no por texto libre (patrón 2026-07-22). | §2.4 |
| M-5 | **Lista de pares repetidos** + acciones **unir** (el que queda absorbe los precios; nada se borra) y **marcar como distintos** (no vuelve a aparecer). | §2.4 |
| M-6 | **Deudores por fecha de salida**: reservas con saldo, ordenadas por fecha de salida, con destino, cliente, total y **faltante por moneda**, la **cuenta regresiva** ya resuelta, y el **veredicto "el saldo ya se pasó de fecha" + la fecha concreta**. Excluye Anuladas y Perdidas. Respeta el alcance del vendedor (las suyas) vs. admin (todas), igual que el listado de reservas. | §4.2 |
| M-7 | **Deuda total por cliente**: solo los que deben, con cantidad de reservas con deuda, **total por moneda** (nunca sumado) y **primera salida**, + la marca de "tiene al menos una pasada de fecha". | §4.3 |
| M-8 | **Config nueva** `el saldo tiene que estar completo N días antes de la salida` (**default 21**, rango razonable como el resto), en el mismo endpoint de configuración operativa. De ahí sale la **fecha límite por reserva** y el veredicto de M-6/M-7. | §4.5 |
| M-9 | **Aviso en la campanita** cuando una reserva pasa esa fecha con saldo: una sección más de las que ya existen, con el alcance de siempre (cada vendedor las suyas, el admin todas). Texto armado por el motor. | §4.5 |
| M-10 | **Muere la llave `enableCatalogFindOrCreate`** (P8=A): **F1.3 de ADR-017 sale directo para todos**. Se quita la llave de Configuración → Funciones avanzadas, se saca la condición del front (ficha de servicio y campanita) y del backend. **Consecuencia a tener en cuenta:** la sección **"Costos a confirmar"** de la campanita, que hoy depende de esa llave, pasa a estar **siempre activa** para quien tiene permiso de costos — que es exactamente lo firmado el 2026-06-05 (Q4b). | §3, §7 |
| M-11 | **"Pendientes de facturar"**: la fila necesita el número/destino de la reserva y su estado de facturación para linkear a la ficha. Ya existe; solo se saca el uso de la ventana vieja. | §4.4 |

**Orden sugerido de construcción** (decide el orquestador con Gastón): M-8/M-6/M-7 (deudores, es lo
que Gastón mira todos los días) → M-10 (la llave, es sacar código) → M-1/M-2 (Tarifario) →
M-3/M-4/M-5 (alta a mano y repetidos).

---

## 7. Detalles que quedaron abiertos al bajar las respuestas

> Ninguno frena la obra. Cada uno dice **qué se hace mientras tanto**, apoyándose siempre en una
> regla ya firmada — no en una preferencia inventada.

**D1 — ⚠️ Dos números parecidos en la misma pantalla de Configuración.** P15=A crea *"el saldo tiene
que estar completo **21** días antes de la salida"*. Ya existe *"Alertas por reservas próximas con
deuda — Días previos para alertar: **7**"*, y la guía fue explícita el 2026-06-21 (ADR-036 P5=B):
**"NO se inventa un parámetro nuevo"** para el aviso de "Debe — no viaja". Ahora hay dos números que
un usuario puede leer como el mismo. **Mientras tanto:** se construyen **los dos, separados**, con
las etiquetas de §4.5 — el de 21 días decide **cuándo el saldo está vencido**, el de 7 días sigue
decidiendo **cuándo aparece el chip "Debe — no viaja"**, que es lo que ya está firmado y no se
toca. **Para preguntarle a Gastón en la próxima tanda:** ¿son dos números distintos o querés uno
solo que sirva para las dos cosas?

**D2 — Cómo se ordena "Deuda por cliente" cuando hay dos monedas.** P13=A pidió la lista "de mayor a
menor", pero con pesos y dólares no existe un "mayor" sin sumarlos, y sumarlos está prohibido
(P-3). **Mientras tanto:** se ordena por **primera salida**, la más próxima arriba — el mismo
criterio que Gastón firmó hoy para la otra lista (P12=A). **Para preguntarle:** ¿ordenamos por
fecha de salida, o preferís elegir la moneda arriba y ordenar por monto dentro de esa moneda?

**D3 — Dónde se ven las vigencias y las variaciones de habitación en la lista única.** P1=B deja
vivo el camino "Carga completa" (vigencias, tipos de habitación), pero P5=A dejó el renglón con lo
esencial, que no tiene lugar para eso. **Mientras tanto:** el renglón muestra **lo esencial** y esos
datos viven **adentro** de la ficha del producto (detrás de `[ Carga completa ]`), que es donde se
cargaron. **Para preguntarle:** ¿querés alguna marca en el renglón cuando un producto tiene tarifas
con vigencia cargadas, o alcanza con verlo al abrirlo?

---

## 8. Fuera de alcance de esta tanda

- **Salidas grupales propias con cupo**: obra aparte. Esta spec no la diseña, pero tampoco la
  bloquea — un producto del tarifario podrá después tener su propia salida con cupo sin cambiar
  nada de lo de acá.
- **Recordatorios de cobro**: solo el hueco (§5).
- **Facturación** (`/facturacion`): no se toca en esta tanda, salvo el nombre en el menú.
- **Reportes**: la ganancia por producto no entra en el Tarifario (P5=A).
