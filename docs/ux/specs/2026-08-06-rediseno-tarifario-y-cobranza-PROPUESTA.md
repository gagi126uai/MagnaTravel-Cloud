# Rediseño de Tarifario y de Cobranza/Facturación — PROPUESTA (tanda B1, solo diseño)

> # ⚠️ SUPERSEDED — DOCUMENTO HISTÓRICO
> **Gastón respondió las 19 preguntas el mismo día (2026-08-06).** La versión que vale es
> **`docs/ux/specs/2026-08-06-rediseno-tarifario-y-cobranza-FIRMADA.md`**, y las reglas nuevas
> están en `docs/ux/guia-ux-gaston.md`, sección *"Tarifario que se arma solo + Cobranzas: la
> vista de deudores (2026-08-06)"*.
> Este archivo se conserva **solo** para saber qué se le preguntó y con qué opciones.
> **No se implementa nada de acá.**
>
> Respuestas firmadas: P1=B (con el formulario largo relegado a "Carga completa") · P2=A ·
> P3=A · P4=A · P5=A · P6=A · P7 sin opción ("hay que evitar repetidos a toda costa") · P8=A ·
> P9=A · P10=A · P11=A · P12=A · P13=A · P14=A · P15=A (21 días) · P16=A · P17=B · P18=A · P19=A.

> **Fecha:** 2026-08-06 · **Autor:** `ux-ui-disenador` · **Estado: PROPUESTA SIN FIRMAR.**
> Nada de este documento se construye hasta que Gastón responda el bloque
> **PREGUNTAS PARA GASTON** del final. Los mockups que están arriba de ese bloque son el
> dibujo de la recomendación, no una decisión tomada.
>
> **Fuente única de decisiones ya tomadas:** `docs/ux/guia-ux-gaston.md` (leída completa el
> 2026-08-06). Todo lo que NO está ahí se preguntó abajo; no se inventó nada.
>
> **Reglas de la constitución que aplica esta spec:** P-3 (monedas separadas) · P-4/P-5
> (ventana solo para frenos; fichas de trabajo en línea) · P-9 (botón apagado con motivo o
> escondido) · P-10 (palabra al lado del ícono) · P-11 (nada sin salida) · P-15 (sin
> cartelitos aclarativos) · P-16 (un dato una sola vez) · P-17/T-5 (cero jerga, cero nombres
> internos) · P-18 (lo emitido siempre se ve) · P-21 (el sistema sugiere, no pisa) · F-14
> (sin permiso de costos no se ven costos) · T-13 (los números los calcula el motor) · T-14
> (hora argentina).

---

## 0. Qué YA está firmado y por eso NO se pregunta

Estas reglas salen de la guía y se aplican tal cual en todo lo de abajo:

| # | Regla firmada | Dónde |
|---|---|---|
| 1 | El tarifario **se arma solo desde las ventas**: nadie lo carga aparte ("a la gente le da paja"). Se guarda producto + operador + **precio de referencia editable**, no tarifa firme. Todos los tipos. | guía 2026-06-05, "EL CAMBIO DE LÓGICA" |
| 2 | Al elegir un producto existente, el sistema **precarga operador y precio de la última venta como sugerencia visible, editable, marcada en amarillo**. | guía 2026-06-05 |
| 3 | El sistema **sugiere y nunca pisa** lo que el vendedor ya escribió. | P-21 |
| 4 | **Sin resultados → directo a crear**: "No encontramos '{texto}' en tu tarifario" + botón crear. Mientras busca, "Buscando…" sutil. | guía 2026-06-06, Ronda 2 |
| 5 | El sistema tiene que **hacer lo imposible para evitar repetidos**: búsqueda tolerante al tipeo, mostrar parecidos SIEMPRE antes de crear, crear como última opción, y **una pantalla para revisar/unir repetidos**. | guía 2026-06-05 |
| 6 | **Ciudad obligatoria** al crear un hotel; precio de hotel **por noche y por habitación**. | guía 2026-06-05 |
| 7 | **Quien no puede ver costos ve el precio de VENTA**, nunca el costo, en ninguna búsqueda ni pantalla. | guía 2026-06-05 / F-14 |
| 8 | **No se muestra de dónde salió un producto** ("creado en venta" fue derogado: "no tiene sentido que un usuario cualquiera vea eso"). | guía 2026-06-08 |
| 9 | Las **bandejas son listas pasivas**: cada fila es un enlace a la ficha, **sin botones de acción propios**. La acción vive en la ficha. | guía 2026-07-08 |
| 10 | **Las fichas de trabajo van en línea**, nunca en ventana flotante ("el modal me parece horrible"). | P-5 / guía 2026-06-09 |
| 11 | **Cobrar y emitir factura viven EN LÍNEA dentro de la ficha de la reserva** (firmado 2026-06-13 / ADR-035 B / 2026-06-19). | guía |
| 12 | **Nunca se suman pesos y dólares**; total por moneda en una línea: `$ 205.000 · US$ 450`. | P-3 / guía 2026-06-09 |
| 13 | **No hay vencimientos ni cuotas con fecha** en la reserva (2026-06-22) y **no hay antigüedad de deuda 30/60/90** en la cuenta del cliente (2026-07-16, P5=A). | guía |
| 14 | El aviso "**Debe — no viaja**" existe y se prende **solo dentro de la ventana de días** de "Alertas por reservas próximas con deuda" (Configuración). | ADR-036 p.7 / ADR-037 p.3 |
| 15 | **Lo que pide hacer algo va con color (ámbar); lo que solo informa va gris y en una sola línea.** | guía 2026-08-03, P11=A |
| 16 | **Nada se borra.** | guía 2026-08-03 |

**Consecuencia importante del punto 13:** hoy el producto **no tiene el concepto de "fecha
límite de pago"**. Sin esa definición, "deudor vencido" y "recordatorio de cobro" no tienen
de dónde agarrarse. Por eso el **Tema 4** del bloque de preguntas es el que más pesa.

---

## A. Tarifario nuevo — "lo que ya vendiste, con el último precio"

### A.1 Cómo está hoy (verificado en el código)

- `/rates` (menú: **CATÁLOGO → Tarifario**) es una pantalla de **carga previa**: formulario largo
  (tipo de servicio, nombre, proveedor, unidad de precio, aerolínea, código IATA, origen, destino,
  clase, equipaje, hotel, ciudad, categoría, variaciones de habitación, régimen, tipo de precio,
  % de menores, vigencia…), con tarjetas arriba (Total / Hoteles / **Vencidas — "Acción requerida"**)
  y una tabla agrupada por hotel.
- Además existe el **buscador que aprende de las ventas** (`ProductSearchField`, "En tu tarifario"),
  que ya muestra en cada resultado **operador · precio · fecha de la última venta**. Está **apagado
  detrás de una llave** de Configuración (ver P8).
- Las dos cosas conviven y se pisan conceptualmente.

### A.2 Propuesta (depende de P1, P4, P5, P6)

**Pantalla `Tarifario`: una lista de productos aprendidos de las ventas.** Nada de formulario de
20 campos como puerta de entrada.

```
┌───────────────────────────────────────────────────────────────────────────────────────┐
│  Tarifario                                                                            │
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

**Al tocar un producto se abre su ficha EN LÍNEA, debajo del renglón** (P-5), para corregir el
nombre/la ciudad y ver de dónde salió cada precio:

```
│  Maitei Posadas                  Hotel     Ola Mayorista     US$ 48/noche   22/05/2026│
│  ┌─────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Nombre *  [ Maitei Posadas                ]   Ciudad *  [ Posadas          ]   │  │
│  │                                                                                 │  │
│  │  Precios que aprendió de tus ventas                                             │  │
│  │    Ola Mayorista      US$ 48 /noche     22/05/2026     F-2026-1042              │  │
│  │    Julia Tours        US$ 52 /noche     03/07/2026     F-2026-1109              │  │
│  │                                                                                 │  │
│  │                                              [ Cancelar ]   [ Guardar ]         │  │
│  └─────────────────────────────────────────────────────────────────────────────────┘  │
```

- El número de reserva de cada precio es **enlace a la ficha** (así se ve el contexto real).
- **Sin permiso de ver costos** (`cobranzas.see_cost`): la columna muestra el **precio de venta**,
  nunca el costo (regla 7 de la tabla de arriba, F-14).
- **Se van** las tarjetas "Total / Hoteles / Vencidas — Acción requerida": una tarifa "vencida" no
  es una acción pendiente en un tarifario que se arma solo (y "Acción requerida" es de las frases
  que P-17 no quiere).

**Estados de la pantalla:**

| Estado | Qué se ve |
|---|---|
| Cargando | Renglones grises (misma pinta que el resto del sistema), sin cartel |
| Vacío (nunca vendiste nada) | "Todavía no hay productos. El tarifario se arma solo: la primera vez que cargues un servicio, el producto queda guardado acá." |
| Vacío (el buscador no encontró) | "No encontramos '{texto}' en tu tarifario." |
| Error | Cartel rojo + botón **"Probar de nuevo"** (mismo criterio que Reservas y Copias de seguridad) |

**Qué NO hay que hacer acá:** ni ventanas flotantes, ni el formulario largo como puerta de
entrada, ni etiquetas de "creado en venta", ni tarjetas de resumen que sumen monedas.

### A.3 El precio sugerido al cargar un servicio (depende de P9, P10)

Lo que ya está firmado y **no cambia**: el buscador muestra los parecidos con
**operador · precio · fecha**, la opción de crear va última, y al elegir un producto el sistema
**precarga operador y precio en amarillo, editable** (regla 2) y **nunca pisa** lo que el vendedor
ya escribió (P-21).

Lo único que agrega esta propuesta es que, ya elegido el producto, **la procedencia del número
quede a la vista** en un renglón gris de una línea (regla 15):

```
│  Producto *   [ Maitei Posadas                                    🔍 ]                │
│                                                                                       │
│  Operador     [ Ola Mayorista            ▾ ]   ← precargado, amarillo                 │
│  Costo/noche  [ US$ 48,00                  ]   ← precargado, amarillo                 │
│               Último precio: Ola Mayorista · US$ 48 · 22/05/2026                      │
│                                                                                       │
│  Entrada [12/09/2026]  Salida [15/09/2026]   Noches 3   Habitaciones [1]              │
```

---

## B. Cobranza — la vista de deudores doble

### B.1 Cómo está hoy (verificado en el código)

- `/payments` = **"Cobranza y Facturación"** con tres solapas: **Por reserva** (lista de reservas
  con saldo, se despliega y muestra los movimientos), **Movimientos**, **Pendientes de facturar**
  (usa la **ventana vieja** `CreateInvoiceModal`, que tiene que morir).
- `/facturacion` = **"Facturación"**, otra entrada de menú, con todos los comprobantes + las
  solapas "Comprobantes por resolver" y "Recibos por regularizar".
- La ficha del cliente (`/customers/:id/account`) ya tiene el **extracto por cliente** firmado el
  2026-07-16.
- **"Por reserva" repite lo que ya da la ficha de la reserva** y no responde ninguna de las dos
  preguntas que Gastón se hace para cobrar.

### B.2 Propuesta (depende de P11, P12, P13, P14)

Dos vistas nuevas, las dos **listas pasivas** (regla 9): la fila entera abre la ficha, sin botones
propios.

**Solapa 1 — "Viajan pronto y deben"** (orden natural = fecha de viaje, decidido por Gastón hoy;
nada de 30/60/90):

```
┌ Cobranzas ────────────────────────────────────────────────────────────────────────────┐
│  [ Viajan pronto y deben ]  [ Deuda por cliente ]  [ Pendientes de facturar ]  [ Movimientos ]
├───────────────────────────────────────────────────────────────────────────────────────┤
│  Falta cobrar:  $ 1.240.000 · US$ 3.150                        [ 🔍 Buscar cliente… ] │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  SALE              RESERVA / DESTINO         CLIENTE          TOTAL       FALTA       │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  en 3 días         F-2026-1042 · Cancún      Fam. García      US$ 2.400   US$ 900     │
│  12/08/2026                                                                           │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  en 11 días        F-2026-1067 · Bariloche   Pérez, Ana       $ 610.000   $ 210.000   │
│  20/08/2026                                                                           │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  en 26 días        F-2026-1071 · Río         López, Juan      $ 380.000   $ 95.000    │
│  04/09/2026                                                                           │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- Vacío: **"Ninguna reserva que salga pronto tiene saldo pendiente."**
- Las reservas ya anuladas/perdidas no entran.

**Solapa 2 — "Deuda por cliente"** (el total cruzando reservas):

```
│  CLIENTE                 RESERVAS CON DEUDA     DEBE                  PRIMERA SALIDA  │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Fam. García             2                      US$ 900               12/08/2026      │
│  Pérez, Ana              1                      $ 210.000             20/08/2026      │
│  López, Juan             3                      $ 95.000 · US$ 120    04/09/2026      │
```

- La fila abre **la ficha del cliente que ya existe** (con su extracto firmado el 2026-07-16).
- Pesos y dólares **separados por un punto medio**, jamás sumados (P-3).

**Solapa 3 — "Pendientes de facturar":** la lista queda, **la ventana vieja de emitir factura
muere**; la fila lleva a la ficha de la reserva, donde el emitir-factura en línea ya está firmado
(regla 11). Ver P14.

**Qué NO hay que hacer acá:** ni cobrar desde la lista, ni emitir factura desde la lista, ni
antigüedad de deuda 30/60/90, ni un número que mezcle monedas, ni repetir el detalle de
movimientos que ya vive en la ficha.

---

## C. "Fecha límite de pago" — el agujero que hay que tapar (todo es pregunta)

- El motor **no tiene** fecha de vencimiento de una factura de venta: lo único con fecha es el
  vencimiento del CAE de ARCA, que es otra cosa.
- La guía dice **"vencimientos = NO"** (2026-06-22) y **"sin antigüedad de deuda"** (2026-07-16).
- Con prepago puro, el ancla natural es la **fecha de viaje**: "el saldo se completa X días antes
  de salir".
- **Sin una respuesta de Gastón acá, no se puede construir ni el rojo de "está vencido" ni ningún
  recordatorio**: no habría contra qué compararlo. Es el Tema 4 de las preguntas.

---

## D. Menú (depende de P17)

Hoy conviven, dentro de VENTAS, **"Cobranza y Facturación"** y **"Facturación"**: la misma palabra
dos veces, dos puertas, y el vendedor no sabe cuál es cuál.

```
   HOY                                     PROPUESTA (recomendada, ver P17)
   VENTAS                                  VENTAS
     Clientes                                Clientes
     Posibles clientes                       Posibles clientes
     Cobranza y Facturación  ← ¿?            Cobranzas        ← lo que me deben y los cobros
     Facturación             ← ¿?            Facturación      ← los comprobantes emitidos
   CATÁLOGO                                CATÁLOGO
     Tarifario                               Tarifario        ← (nombre: ver P3)
     Países y destinos                       Países y destinos
```

---

## E. Recordatorios de cobro — solo el hueco, no se construye (depende de P18, P19)

Dónde viviría el día que se construya, para que el diseño de hoy no lo bloquee:

- **El disparador vive en la ficha** (de la reserva o del cliente), no en la lista: la lista sigue
  siendo pasiva (regla 9).
- La lista de deudores **muestra si ya se le avisó y cuándo**, en un renglón gris de una línea
  ("Último aviso: 02/08/2026"), sin botón (regla 15).
- El texto del aviso lo arma el motor (T-13) y habla como manda P-17: sujeto = la reserva o la
  persona, cero jerga fiscal.
- **Nada de esto se construye en esta tanda.**

---

## F. Contradicciones y dependencias que hay que resolver ANTES de construir

1. **La llave apagada vs. "basta de llaves".** La guía (2026-06-06, Ronda 6) firmó una llave en
   Configuración → Funciones avanzadas: *"Tarifario que se arma solo desde las ventas"*, y hoy está
   **apagada** (`enableCatalogFindOrCreate = false`). Pero el dueño después dio la orden general
   **"basta de llaves, las cosas salen directas"**. **No lo resuelvo yo** → P8.
2. **"Vencimientos = NO" (2026-06-22) vs. la necesidad de una fecha límite de pago** para poder
   marcar deudores y mandar recordatorios. **No lo resuelvo yo** → P15/P16.
3. **Dependencias del motor** (no son decisiones de UX, pero sin esto no hay pantalla):
   - lista de **productos aprendidos** con el último precio **por operador** y su fecha (hoy solo
     existe la búsqueda por texto y el listado del tarifario viejo);
   - lista de **reservas con saldo ordenada por fecha de salida**, con el faltante por moneda;
   - **deuda total por cliente** cruzando reservas;
   - el campo/derivación de **"cuándo vence el saldo"**, según lo que responda P15.
   Todos los números los calcula el motor; la pantalla solo pinta (T-13).

---

# PREGUNTAS PARA GASTON

> Son 19, ordenadas de lo grande a lo chico y agrupadas por tema. Podés contestar corto:
> "1A, 2B, 3 otra cosa: …". En cualquiera podés decir algo distinto a las opciones.

---

## Tema 1 — El Tarifario

**Contexto:** hoy "Tarifario" es una pantalla donde alguien tiene que sentarse a cargar tarifas de
antemano, con un formulario largo. Vos cotizás a mano cada vez y no cargás tarifas por adelantado.
La idea es que el Tarifario pase a ser **la memoria de lo que ya vendiste**: qué producto, a qué
operador se lo compraste, a cuánto, y cuándo.

---

**P1. ¿Para qué querés que sirva la pantalla "Tarifario"?**

**A) Es la memoria de tus ventas** (recomendada 👉): una lista de los productos que ya vendiste
alguna vez, con el último precio de cada operador y la fecha. No hay formulario largo.

```
  Tarifario
  [ 🔍 Buscar… ]
  Maitei Posadas       Hotel    Ola Mayorista   US$ 48/noche   22/05/2026
    Posadas                     Julia Tours     US$ 52/noche   03/07/2026
  Buenos Aires–Miami   Aéreo    Aeromundo       US$ 780        14/06/2026
```

**B) Las dos cosas:** arriba la lista de lo vendido, y abajo se siguen pudiendo cargar tarifas a
mano con el formulario largo de hoy.

```
  Tarifario
  ── Lo que ya vendiste ──────────────────────────
  Maitei Posadas   Ola Mayorista   US$ 48/noche
  ── Tarifas cargadas a mano ─────────────────────
  Hotel Marriott (vigencia 01/06 a 30/09)   [+ Cargar tarifa]
```

**C) Se saca del menú:** el tarifario deja de ser una pantalla; solo existe adentro del buscador
cuando cargás un servicio.

```
  (no hay entrada "Tarifario" en el menú)
  Al cargar un servicio:  [ Maitei…  🔍 ]  →  Maitei Posadas · Ola · US$ 48 · 22/05
```

👉 **Recomendada: A.** Es lo que ya firmaste el 05/06 ("el tarifario se arma solo a base de las
reservas") y lo que hacen las agencias en el mundo real. La C te dejaría sin ningún lugar donde
corregir un nombre mal escrito o unir dos productos repetidos.

---

**P2. Hoy hay tarifas cargadas a mano en esa pantalla. ¿Qué hacemos con ellas?**

**A) Entran a la lista como un producto más** (recomendada 👉), sin decir de dónde salieron.

```
  Maitei Posadas       Hotel   Ola Mayorista   US$ 48/noche   22/05/2026
  Hotel Marriott       Hotel   Ola Mayorista   US$ 95/noche   10/03/2026
```

**B) Quedan en un rincón aparte,** en una solapa "Cargadas a mano".

```
  [ Lo que vendiste ]  [ Cargadas a mano ]
```

**C) Se archivan:** no se ven más en la lista (no se borran, quedan guardadas).

👉 **Recomendada: A.** El 08/06 ya dijiste que al usuario no le importa si algo se creó en la venta
o en el tarifario ("no tiene sentido que un usuario cualquiera vea eso"). Una sola lista, una sola
cabeza.

---

**P3. ¿Cómo se llama en el menú?**

**A) "Tarifario"** (recomendada 👉) — como hoy. Es la palabra que ya usa el sistema en los textos
que aprobaste ("No encontramos X en tu tarifario").

**B) "Productos y precios"**

**C) "Precios"**

```
  A)  CATÁLOGO            B)  CATÁLOGO                 C)  CATÁLOGO
        Tarifario                Productos y precios          Precios
        Países y destinos        Países y destinos            Países y destinos
```

👉 **Recomendada: A.** Cambiar la palabra ahora obliga a cambiar también los textos del buscador
que ya firmaste.

---

**P4. ¿Se puede dar de alta un producto a mano, sin haberlo vendido?** (Por ejemplo, cargar un
hotel nuevo antes de la temporada.)

**A) Sí, con pocos campos** (recomendada 👉): un botón "Agregar producto" que abre una fichita en
línea con lo mínimo (tipo, nombre, ciudad, operador, precio).

```
  Tarifario                                            [ + Agregar producto ]
  ┌────────────────────────────────────────────────────────────────────┐
  │ Tipo [Hotel ▾]  Nombre * [                    ]  Ciudad * [      ] │
  │ Operador [Ola ▾]   Precio [US$        ] por noche                  │
  │                                        [ Cancelar ]  [ Guardar ]   │
  └────────────────────────────────────────────────────────────────────┘
```

**B) No:** el tarifario aprende solo de las ventas, no se carga nada a mano.

**C) Sí, pero con el formulario largo de hoy** (aerolínea, IATA, clase, equipaje, vigencia,
variaciones de habitación…).

👉 **Recomendada: A.** No te obliga a cargar nada, pero te deja hacerlo el día que quieras, sin las
20 preguntas de hoy.

---

**P5. ¿Qué querés ver de cada producto en la lista?**

**A) Lo esencial** (recomendada 👉): nombre + ciudad/tipo · operador · último precio · fecha.

```
  Maitei Posadas      Hotel   Ola Mayorista   US$ 48/noche   22/05/2026
```

**B) Lo esencial + cuántas veces lo vendiste.**

```
  Maitei Posadas      Hotel   Ola Mayorista   US$ 48/noche   22/05/2026   vendido 7 veces
```

**C) Lo esencial + tu ganancia promedio.**

```
  Maitei Posadas      Hotel   Ola   costo US$ 48 · venta US$ 62 · ganás US$ 14
```

👉 **Recomendada: A.** El tarifario es para acordarte el precio, no para analizar el negocio (eso
vive en Reportes). Si después querés B o C, se agrega.

---

**P6. Un mismo hotel te lo venden dos operadores a precios distintos. ¿Qué mostramos?**

**A) Un renglón por operador, debajo del producto** (recomendada 👉).

```
  Maitei Posadas               Hotel
    Posadas, Misiones                  Ola Mayorista    US$ 48/noche   22/05/2026
                                       Julia Tours      US$ 52/noche   03/07/2026
```

**B) Un solo precio: el último, sea de quien sea.**

```
  Maitei Posadas    Hotel   Julia Tours   US$ 52/noche   03/07/2026
```

👉 **Recomendada: A.** El precio sin el operador no sirve para decidir; y al cargar la venta el
sistema puede sugerirte el del operador que elegiste.

---

**P7. Cuando se cuela un producto repetido ("Maitei Posadas" cargado dos veces), ¿dónde lo juntás?**

**A) En el mismo Tarifario, una solapa "Repetidos"** (recomendada 👉) donde el sistema te muestra
los pares sospechosos y elegís cuál queda.

```
  [ Todos ]  [ Repetidos (3) ]
  Maitei Posadas  ·  Maitei Posada        → [ Es el mismo: unirlos ] [ Son distintos ]
```

**B) En Administración,** junto con las otras herramientas de mantenimiento.

**C) No hace falta pantalla:** solo el aviso al momento de crear ("¿no será este?").

👉 **Recomendada: A.** Es donde vas a estar mirando los productos; mandarte a otro lado a limpiar
lo mismo es un viaje al pedo.

---

**P8. ⚠️ Contradicción que no puedo resolver solo.** El buscador que aprende de las ventas está
hoy **apagado detrás de una llave** en Configuración → Funciones avanzadas ("Tarifario que se arma
solo desde las ventas"), como se firmó el 06/06. Después diste la orden general de que **no haya
más llaves**: las cosas salen directas.

**A) Sale directo para todos y la llave desaparece** de Configuración (recomendada 👉).

**B) Queda la llave** y la prendés vos cuando quieras.

```
  A)  Configuración → Funciones avanzadas          B)  Configuración → Funciones avanzadas
        (la llave ya no está)                            [ ○ ] Tarifario que se arma solo…
```

👉 **Recomendada: A**, por tu propia orden de "basta de llaves". Avisame si en este caso querés la
excepción.

---

## Tema 2 — El precio que te sugiere al cargar un servicio

**Contexto:** ya está firmado que, al elegir un producto, el sistema te precarga **operador y
precio de la última venta en amarillo, editables**, y que **nunca pisa** un número que vos ya
escribiste. Falta definir dos detalles.

---

**P9. ¿Querés ver de dónde salió ese precio sugerido?**

**A) Un renglón gris debajo del casillero** (recomendada 👉).

```
  Costo/noche  [ US$ 48,00 ]        ← amarillo (sugerido)
               Último precio: Ola Mayorista · US$ 48 · 22/05/2026
```

**B) Solo el número en amarillo, sin explicación.**

```
  Costo/noche  [ US$ 48,00 ]
```

**C) Un cartelito de color al lado.**

```
  Costo/noche  [ US$ 48,00 ]   ⚠ Precio sugerido de la última venta
```

👉 **Recomendada: A.** Un precio sin fecha ni operador no te sirve para decidir si lo dejás o lo
cambiás; y va gris y en una línea, como firmaste el 03/08 (lo que solo informa, gris).

---

**P10. Si el último precio es viejo (por ejemplo, de hace 5 meses), ¿qué hace el sistema?**

**A) Te lo sugiere igual, pero la fecha va en ámbar** (recomendada 👉) para que veas que está viejo.

```
  Costo/noche  [ US$ 48,00 ]
               Último precio: Ola Mayorista · US$ 48 · 22/05/2026 (hace 5 meses)
```

**B) No te sugiere nada:** el casillero queda vacío.

```
  Costo/noche  [                ]
               Último precio: hace más de 4 meses, mejor confirmalo.
```

**C) Te lo sugiere igual, sin ninguna marca.**

👉 **Recomendada: A.** Un precio viejo sigue siendo mejor que nada como punto de partida, pero
tenés que verlo. (El límite de "viejo" hoy son 60 días en otra parte del sistema; si querés otro
número, decilo.)

---

## Tema 3 — Cobranza: ver a quién le tengo que cobrar

**Contexto:** hoy la pantalla "Cobranza y Facturación" tiene una solapa "Por reserva" que repite
lo mismo que ya ves en la ficha de la reserva. Vos dijiste que para cobrar mirás **dos cosas**: las
reservas que **viajan pronto y deben** (ordenadas por fecha de viaje) y **cuánto te debe cada
cliente en total**.

---

**P11. ¿Qué solapas querés en la pantalla de Cobranzas?**

**A) Cambiamos "Por reserva" por las dos vistas nuevas** (recomendada 👉).

```
  [ Viajan pronto y deben ]  [ Deuda por cliente ]  [ Pendientes de facturar ]  [ Movimientos ]
```

**B) Agregamos las dos y dejamos "Por reserva" también.**

```
  [ Por reserva ]  [ Viajan pronto y deben ]  [ Deuda por cliente ]  [ Pendientes… ]  [ Movimientos ]
```

**C) Las dos vistas nuevas van en una pantalla aparte** del menú ("Deudores"), y Cobranzas queda
como está.

```
  VENTAS
    Cobranza y Facturación
    Deudores            ← nueva
```

👉 **Recomendada: A.** "Por reserva" no te dice nada que la ficha no diga; sacarla es una pantalla
menos donde perderse.

---

**P12. En "Viajan pronto y deben", ¿a quiénes listamos?**

**A) A todos los que deben, ordenados por fecha de salida** (recomendada 👉), el que sale primero
arriba.

```
  en 3 días    12/08   F-2026-1042 · Cancún      Fam. García   falta US$ 900
  en 11 días   20/08   F-2026-1067 · Bariloche   Pérez, Ana    falta $ 210.000
  en 26 días   04/09   F-2026-1071 · Río         López, Juan   falta $ 95.000
  en 4 meses   15/12   F-2026-1102 · Madrid      Sosa, Luis    falta US$ 1.800
```

**B) Solo los que salen dentro de los días que ya configuraste** para el aviso de "reservas
próximas con deuda".

```
  (con el aviso configurado en 30 días)
  en 3 días    12/08   F-2026-1042 · Cancún      Fam. García   falta US$ 900
  en 11 días   20/08   F-2026-1067 · Bariloche   Pérez, Ana    falta $ 210.000
  en 26 días   04/09   F-2026-1071 · Río         López, Juan   falta $ 95.000
```

**C) Todos, pero con un filtro arriba** para elegir "próximos 15 / 30 / 60 días / todos".

👉 **Recomendada: A.** Es una sola lista, siempre completa, y los urgentes quedan arriba solos. Si
después molesta el largo, se agrega el filtro de la C.

---

**P13. "Deuda por cliente": ¿pantalla nueva o le agregamos el saldo al listado de Clientes?**

**A) Una lista propia con los que deben** (recomendada 👉), de mayor a menor, y cada fila abre la
ficha del cliente que ya existe.

```
  CLIENTE          RESERVAS CON DEUDA   DEBE                 PRIMERA SALIDA
  Fam. García      2                    US$ 900              12/08/2026
  Pérez, Ana       1                    $ 210.000            20/08/2026
  López, Juan      3                    $ 95.000 · US$ 120   04/09/2026
```

**B) Una columna "Debe" en el listado de Clientes** que ya existe, y no hacemos lista nueva.

```
  CLIENTE          TELÉFONO        MAIL              DEBE
  Fam. García      11 5555-1234    g@mail.com        US$ 900
  Gómez, Marta     11 4444-9876    m@mail.com        —
```

**C) Las dos cosas.**

👉 **Recomendada: A.** El listado de Clientes es la agenda (están todos, deban o no); para cobrar
querés ver **solo a los que deben** y en orden de cuánto.

---

**P14. "Pendientes de facturar" hoy abre una ventana vieja para facturar. Esa ventana se muere.
¿Qué pasa al tocar una fila?**

**A) Te lleva a la ficha de la reserva** (recomendada 👉), donde emitir la factura ya está a la
vista y funciona en línea.

```
  F-2026-1042 · Cancún · Fam. García · US$ 2.400 sin facturar   →  abre la reserva
```

**B) Se factura ahí mismo,** en una fichita en línea dentro de la lista.

```
  F-2026-1042 · Cancún ▾
  ┌───────────────────────────────────────────────┐
  │ Factura B · 3 renglones · US$ 2.400           │
  │                        [ Emitir factura ]     │
  └───────────────────────────────────────────────┘
```

**C) Se saca la solapa:** las reservas sin facturar ya se ven en el listado de reservas por el chip
"Sin facturar".

👉 **Recomendada: A.** Ya firmaste que **las bandejas son listas pasivas y la acción vive en la
ficha** (08/07). Facturar desde una lista, sin ver la reserva, es donde se meten los errores caros.

---

## Tema 4 — Cuándo una deuda "está vencida" (esto es lo más importante)

**Contexto:** hoy el sistema **no sabe** cuándo hay que terminar de pagar una reserva. Sabe cuánto
falta, pero no si eso ya se pasó de fecha. Sin esa definición no puedo pintar nada en rojo ni
preparar recordatorios. Vos cobrás por adelantado (nadie viaja debiendo), así que la fecha natural
es la de salida — pero decime cómo lo pensás vos.

---

**P15. ¿Cuándo decís vos que una seña o un saldo "ya se venció"?**

**A) Un número general: "X días antes de salir"** (recomendada 👉). Lo ponés una vez en
Configuración (por ejemplo 21 días) y vale para todas.

```
  Configuración
    El saldo tiene que estar completo  [ 21 ] días antes de la salida.

  Lista de deudores
  en 3 días    12/08   F-2026-1042 · Cancún   falta US$ 900   🔴 vencido hace 18 días
  en 26 días   04/09   F-2026-1071 · Río      falta $ 95.000  — al día
```

**B) Fecha a mano en cada reserva:** en cada reserva escribís "tiene que pagar todo antes del
__/__/____".

```
  Ficha de la reserva
    Saldo a cobrar US$ 900     Pagar todo antes del [ 20/07/2026 ]
```

**C) Manda el operador:** vence cuando vence el pago al operador de esa reserva.

```
  en 3 días  12/08  F-2026-1042 · Cancún   falta US$ 900
                     🔴 al operador había que pagarle el 25/07
```

**D) No existe "vencido":** lo único que importa es que esté todo pagado antes de salir, y ya lo
avisa el "Debe — no viaja".

```
  en 3 días  12/08  F-2026-1042 · Cancún   falta US$ 900   (sin marca de vencido)
```

👉 **Recomendada: A.** Es una sola decisión, la tomás una vez, y sirve para todas las reservas sin
que nadie tenga que cargar fechas a mano (que fue justo lo que sacamos de la carga de servicios el
06/06). La B te obliga a acordarte en cada venta.

---

**P16. Cuando se pasa esa fecha y el cliente no pagó, ¿qué hace el sistema?**

**A) Lo pinta y lo avisa, nada más** (recomendada 👉): renglón en rojo en la lista de deudores + un
aviso en la campanita.

```
  🔴 en 3 días  12/08  F-2026-1042 · Cancún  Fam. García  falta US$ 900  vencido hace 18 días
```

**B) Además traba algo** (por ejemplo, no deja emitir el voucher).

**C) Nada visible:** solo queda para el recordatorio automático del futuro.

👉 **Recomendada: A.** Trabar la operación por una fecha que pusiste vos mismo te complica a vos,
no al cliente; y el freno de verdad ("no viaja si debe") ya existe.

---

## Tema 5 — El menú

**P17. Hoy dice "Cobranza y Facturación" y, dos renglones abajo, "Facturación". ¿Cómo lo ordenamos?**

**A) Una sola puerta** que se llama "Cobranza y Facturación", con los comprobantes adentro como una
solapa más.

```
  VENTAS
    Clientes
    Posibles clientes
    Cobranza y Facturación
      [ Viajan pronto y deben ] [ Deuda por cliente ] [ Pendientes… ] [ Comprobantes ] [ Movimientos ]
```

**B) Dos puertas, con nombres que no se pisen** (recomendada 👉): **"Cobranzas"** (lo que te deben y
los cobros) y **"Facturación"** (los comprobantes emitidos, que ve solo administración).

```
  VENTAS
    Clientes
    Posibles clientes
    Cobranzas        ← a quién le cobro
    Facturación      ← los comprobantes emitidos
```

**C) Queda como está.**

👉 **Recomendada: B.** Los comprobantes los mira administración y las cobranzas las mira el
vendedor: son dos trabajos distintos. Con la A, una sola pantalla queda con 5 o 6 solapas y media
escondida por permisos.

---

## Tema 6 — Recordatorios de cobro (solo dejamos el lugar preparado, NO se construye ahora)

**P18. El día que se construya, ¿desde dónde querés mandar el recordatorio?**

**A) Desde la ficha de la reserva o del cliente** (recomendada 👉); la lista de deudores solo te
muestra si ya se le avisó y cuándo.

```
  Lista:   en 3 días  12/08  F-2026-1042 · Cancún  falta US$ 900
                             Último aviso: 02/08/2026
  Ficha:   [ Registrar cobro ]  [ Emitir factura ]  [ Recordar pago ]
```

**B) Un botón en cada fila de la lista de deudores.**

```
  en 3 días  12/08  F-2026-1042 · Cancún  falta US$ 900   [ Recordar pago ]
```

**C) Automático,** el sistema manda solo X días antes y vos no tocás nada.

👉 **Recomendada: A.** Es lo que ya firmaste para todas las bandejas (la lista mira, la ficha
hace), y así no le mandás un mensaje al cliente equivocado de un click.

---

**P19. ¿Por dónde saldría ese recordatorio?**

**A) WhatsApp** (recomendada 👉) — el sistema ya manda vouchers por WhatsApp, así que es el mismo
camino.

**B) Mail.**

**C) Ninguno de los dos: que el sistema me avise a mí** y yo lo llamo.

```
  A)  WhatsApp a Fam. García: "Hola! Te recordamos que queda…"
  B)  Mail a garcia@mail.com
  C)  Campanita: "3 clientes para llamar hoy"
```

👉 **Recomendada: A.** Es por donde le hablás al cliente hoy y ya está construido el envío.

---

## Después de las respuestas

1. Cada respuesta se escribe como regla nueva, con fecha, en `docs/ux/guia-ux-gaston.md`.
2. Con eso se arma la **especificación final** (esta misma, sin "PROPUESTA" y sin preguntas), que
   es la que sigue `frontend-senior` al pie de la letra.
3. Lo que dependa del motor (lista de productos aprendidos, lista de deudores, deuda por cliente,
   fecha límite de pago) se pide al backend recién con la respuesta de P15 firmada.
