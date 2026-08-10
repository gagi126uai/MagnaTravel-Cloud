# El Tarifario inteligente: recuerda por habitación y entiende lo que escribís — ESPECIFICACIÓN FIRMADA

> **Fecha:** 2026-08-07 · **Firmada por Gastón** (respuestas V1..V11 + el pivote de visión) ·
> **Autor:** `ux-ui-disenador`
> Reemplaza a `docs/ux/specs/2026-08-07-tarifario-variantes-PROPUESTA.md` (queda como histórico).
> Continúa a `docs/ux/specs/2026-08-06-rediseno-tarifario-y-cobranza-FIRMADA.md`: todo lo de esa
> spec sigue valiendo salvo **la derogación quirúrgica de P6=A** que se detalla en §12.
>
> **Esto es lo que `frontend-senior` implementa al pie de la letra.** Cualquier desvío necesario (por
> costo técnico o regla de negocio) se le repregunta a Gastón ANTES de desviarse.
>
> **Reglas de la constitución que aplica esta spec:** P-3 · P-5 · P-15 · P-16 · P-17 · P-21 · F-14 ·
> T-13 · T-14.
>
> ⚠️ **Quedan SOLO 3 preguntas puntuales** (§13). Todo lo demás está decidido y se puede construir.

---

## 1. El pivote de visión (mandato textual de Gastón, 2026-08-07)

> *"Quiero que el tarifario sea una especie de IA: el que carga (probablemente desde la reserva) no
> se preocupa por NADA, siempre consistente, un buscador donde escribís cualquier producto del rubro
> y el sistema lo acepta y lo organiza automáticamente."*

Eso cambia el eje de la obra. Antes el diseño era *"cómo mostramos bien las habitaciones"*. Ahora es:

**El vendedor escribe como habla. El sistema acepta, entiende, ordena y deja todo precargado para
que él solo mire y confirme. La consistencia es problema del sistema, nunca del que carga.**

Cuatro principios que gobiernan todo lo que sigue:

1. **Se acepta cualquier texto.** Nunca se le dice al vendedor "así no se escribe". Si escribió algo
   raro, el sistema lo guarda y lo ordena por detrás.
2. **El sistema organiza solo** (le decimos **"el bibliotecario"** por adentro; en pantalla esa
   palabra no aparece nunca).
3. **Solo pregunta cuando la duda es grande**, y cuando pregunta es **una línea, sí o no**. Nunca un
   formulario, nunca una ventana en medio del trabajo.
4. **Nada se confirma solo.** Todo lo que arma el sistema queda **precargado en amarillo y
   editable** (P-21, la regla del 2026-06-05 que ya rige las sugerencias). El vendedor guarda.

---

## 2. Resumen de lo firmado hoy

| # | Decisión | Respuesta |
|---|---|---|
| 1 | El precio se recuerda **por combinación**: producto + operador + **variante** (habitación+régimen en hotel, cabina en aéreo, vehículo en traslado). Vender una triple ya **no pisa** el precio de la doble. | **V1=A** |
| 2 | **Todos los tipos** tienen su variante natural. Paquete y asistencia: sin variante. | **V2=todos** |
| 3 | Si el dato de la variante no está cargado, el precio se muestra **sin nada al lado** (no se escribe "Sin especificar"). | **V3=A** |
| 4 | **Texto libre CON MEMORIA** para el nombre fino de la habitación ("Superior", "Vista al mar") y para el vehículo del traslado: la primera vez lo escribís como quieras; después **el sistema te lo ofrece** y **frena las variaciones de tipeo**. | **V4=B ajustada** |
| 5 | La lista se agrupa **por habitación, y adentro los operadores**. | **V5=A** |
| 6 | La habitación va en **una columna propia**, en criollo y en una sola frase ("Doble con desayuno"). | **V6=A** |
| 7 | Se muestran hasta **3 renglones de precio** por producto + línea gris **"+ N precios más"**. | **V7=A** |
| 8 | **Solapas por tipo de servicio** arriba (Hoteles / Aéreos / Paquetes / Traslados / Asistencias). **Muere el desplegable "Tipo"** y la columna "Tipo". | **V8=A** |
| 9 | Al vender, si de esa habitación no hay precio: **el casillero queda vacío** y abajo, en gris, el precio de la habitación parecida diciendo de cuál es. | **V9=A** |
| 10 | Si cambiás la habitación después: la sugerencia **se acomoda sola mientras vos no hayas tocado el número**; si lo escribiste vos, no se toca nunca. | **V10=A** |
| 11 | Los repetidos se muestran **agrupados** (un producto arriba, abajo todos los que se le parecen). | **V11=B** |
| 12 | **El sistema elige solo el nombre limpio** al unir, con rastro. No te hace elegir. | absorbida por el paradigma |
| 13 | Lo que el sistema **no puede unir solo** cae en **la misma bandeja agrupada** de repetidos. | absorbida |
| 14 | La habitación que venía metida en el nombre viejo ("- Doble Superior") **se conserva como habitación**. Nada se pierde. | absorbida |
| 15 | En la ficha del producto se pueden **corregir los textos** (nombre, ciudad, etiqueta de la habitación). **Los importes JAMÁS se editan a mano.** | absorbida |
| 16 | El alta a mano de un hotel **pide habitación y régimen**, con **Doble / Desayuno** ya puestos. | absorbida |
| 17 | **IA REAL** para interpretar el texto libre, sobre la infraestructura que ya existe (ADR-016 F0a). | nueva |
| 18 | **El sistema decide solo**, salvo **dudas grandes** → una línea, sí o no. | nueva |
| 19 | **LA LÍNEA INTELIGENTE**: en la ficha de servicio, una sola caja entiende todo y **arma el servicio completo precargado en amarillo**, editable. | nueva |

---

## 3. LA LÍNEA INTELIGENTE (ficha de carga de servicio)

**Dónde vive:** en la ficha de carga de servicio en línea de la reserva (`ServiceInlineCard` /
`ProductSearchField`). **Es el mismo casillero del buscador de producto de hoy, agrandado** — no se
suma una caja nueva arriba (ver la pregunta puntual **Q1** de §13, por si Gastón prefiere dos cajas).

### 3.1 Momento 1 — el vendedor escribe como habla

```
┌ Agregar servicio ─────────────────────────────────────────────────────────────────────┐
│  Tipo  ( Hotel ) ( Aéreo ) ( Paquete ) ( Traslado ) ( Asistencia )                    │
│                                                                                       │
│  Escribilo como te salga                                                              │
│  [ sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9                      🔍 ] │
│                                                                                       │
│  Operador     [                          ▾ ]      Entrada  [           ]              │
│  Habitación   [ Doble ▾ ]  Régimen [ Desayuno ▾ ] Salida   [           ]              │
│  Costo/noche  [                            ]      Noches   [    ]  Habitaciones [ 1 ] │
│                                                                                       │
│                                              [ Cancelar ]   [ Guardar servicio ]      │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- El casillero **acepta cualquier cosa**: una palabra ("sheraton"), o la frase entera con precio y
  fechas. Las dos formas funcionan.
- Mientras escribe, **el buscador de siempre sigue funcionando igual** (parecidos primero, "crear
  nuevo" último, lo firmado el 2026-06-05 y reforzado por P7). La IA no reemplaza al buscador: lo
  **agranda**.

### 3.2 Momento 2 — el sistema piensa (sutil, nunca traba)

```
│  [ sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9                      🔍 ] │
│    Buscando…                                                                          │
```

- Se reusa **el mismo "Buscando…" sutil** ya firmado (Ronda 2, 2026-06-06). **No se inventa un
  cartel nuevo, ni la palabra "IA", ni un reloj de arena que tape la pantalla.**
- **Nunca traba la ficha:** el vendedor puede seguir escribiendo o completar los campos a mano
  mientras tanto. Si el sistema tarda, **la caja se comporta como el buscador de siempre** y listo
  (lección "la factura no atrapa al usuario en un spinner", 2026-07-08).

### 3.3 Momento 3 — "entendí esto": todo precargado en amarillo

```
┌ Agregar servicio ─────────────────────────────────────────────────────────────────────┐
│  Escribilo como te salga                                                              │
│  [ sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9                      🔍 ] │
│                                                                                       │
│  Producto *   [ Sheraton Iguazú · Puerto Iguazú          ]  ← amarillo                │
│  Operador     [ Ola Mayorista                          ▾ ]  ← amarillo                │
│  Habitación   [ Doble ▾ ]  Régimen [ Desayuno ▾ ]           ← amarillo                │
│  Entrada      [ 12/09/2026 ]   Salida [ 15/09/2026 ]        ← amarillo                │
│  Noches       [ 3 ]  Habitaciones [ 1 ]                     ← calculado, como siempre │
│  Costo/noche  [ US$ 48,00                  ]                ← amarillo                │
│               Último precio: Ola Mayorista · Doble con desayuno · US$ 48 · 22/05/2026 │
│                                                                                       │
│                                              [ Cancelar ]   [ Guardar servicio ]      │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

**Reglas de este momento (todas apoyadas en algo ya firmado):**

- **El amarillo es el que habla.** NO se escribe ningún cartelito tipo "Entendí esto" ni "Armado
  automáticamente": el amarillo ya significa "esto lo puso el sistema, revisalo" desde el 2026-06-05
  (P-15 prohíbe el cartelito; P-16 prohíbe decir dos veces lo mismo).
- **Lo que el sistema no entendió queda vacío y en blanco**, sin explicación (P-15). Un casillero
  vacío ya dice lo que hay que decir.
- **Todo es editable.** Tocar un campo lo saca del amarillo: pasa a ser dato del vendedor y **nada
  lo vuelve a pisar** (P-21, y V10=A).
- **Nada se guarda solo.** El servicio se crea cuando el vendedor toca **Guardar servicio**.
- **El renglón gris de procedencia** sigue exactamente como se firmó (P9=A, 2026-08-06), ahora con la
  habitación adentro; fecha en ámbar si el precio tiene más de 60 días (P10=A).
- **Sin permiso de costos** (F-14): el costo lo completa el motor por detrás y **no se muestra**; el
  renglón gris muestra el precio de **venta**. La línea inteligente no puede ser un agujero de fuga
  de costos.
- **Teclado:** el casillero mantiene el foco mientras el sistema piensa; los parecidos se recorren
  con flechas y se eligen con Enter, como hoy. Ningún salto de foco automático.

### 3.4 Momento 4 — el producto no existe todavía

Si de la frase sale un producto que no está en el tarifario, **NO se crea solo**: se ofrece crearlo
como **última opción de la lista**, con los parecidos arriba (P7, prevención de repetidos como
prioridad absoluta):

```
│  [ hotel amerian posadas triple mp julia 91000 pesos                             🔍 ] │
│    ┌───────────────────────────────────────────────────────────┐                      │
│    │  Amerian Posadas                        En tu tarifario   │                      │
│    │  Posadas · Julia Tours · $ 88.000/noche · 14/06/2026      │                      │
│    ├───────────────────────────────────────────────────────────┤                      │
│    │  + No es ninguno: crear "Amerian Posadas" como hotel      │                      │
│    │    Revisá los de arriba antes — si ya existe,             │                      │
│    │    elegirlo evita duplicados.                             │                      │
│    └───────────────────────────────────────────────────────────┘                      │
```

Cuando se elige uno de los parecidos, **el resto de la frase igual se aprovecha**: operador, precio,
fechas y habitación se precargan en amarillo lo mismo.

### 3.5 Degradación — sin IA, la pantalla es la de hoy

| Situación | Qué ve el vendedor |
|---|---|
| No hay clave de IA configurada | **Exactamente el buscador de hoy**: escribe, ve parecidos, elige o crea. Ni una palabra distinta. |
| La IA se cayó o dio error | Ídem: el buscador de siempre. **Cero cartel**, cero "servicio no disponible", cero código de error. |
| La IA tardó demasiado | Ídem: se corta sola y queda el buscador. El "Buscando…" desaparece; nada queda colgado. |
| La IA entendió a medias | Se precarga en amarillo **lo que entendió**; el resto queda vacío. Sin explicación. |
| La IA devolvió algo incoherente (el motor lo descarta) | Se trata como "no entendió": buscador de siempre. |

> **Regla dura (data-exposure):** en ninguno de esos casos aparece jamás un texto técnico, un nombre
> de servicio, un código de error, ni la palabra "IA", "modelo", "token" o "timeout". El producto no
> le cuenta al vendedor cómo está hecho por dentro (P-17 + gate de exposición de datos).

---

## 4. La pregunta de DUDA GRANDE (una línea, sí o no)

**Cuándo aparece:** solo cuando el sistema **no puede decidir solo algo que cambia la plata o la
identidad del producto**. Todo lo demás lo resuelve el sistema.

```
│  Producto *   [ Sheraton Iguazú · Puerto Iguazú          ]  ← amarillo                │
│  Operador     [ Ola Mayorista                          ▾ ]  ← amarillo                │
│  Costo/noche  [ US$ 48,00                  ]                ← amarillo                │
│                                                                                       │
│  ¿"48" es el precio por noche?                              [ Sí ]   [ No ]           │
│  ─────────────────────────────────────────────────────────────────────────────────    │
```

- **Una sola línea**, dentro de la ficha, debajo del campo del que habla. **Nunca una ventana
  encima**: la ficha de carga es trabajo en curso y el Cartel emergente está reservado para los
  rechazos largos del motor disparados por un click (regla 2026-07-22, que excluye explícitamente
  las fichas de trabajo).
- **Dos botones: Sí / No.** "Sí" la cierra y deja el amarillo como está. "No" **vacía ese campo** y
  deja el cursor ahí para que el vendedor lo escriba. Nada más.
- **Una duda por vez.** Si hubiera dos, se muestra la de plata primero; la otra aparece después de
  resolver la primera.
- **Es opcional ignorarla:** si el vendedor guarda sin contestar, se guarda lo que está en pantalla.
  La duda **no traba el Guardar** (nada de esta obra traba nada).
- **La escribe el motor** (T-13) y viaja **por código**, no por texto libre (patrón 2026-07-22).

**Ejemplos de duda grande (SÍ preguntan):**

```
│  ¿"48" es el precio por noche?                              [ Sí ]   [ No ]           │
│  ¿"del 12 al 15/9" es septiembre de 2026?                   [ Sí ]   [ No ]           │
│  ¿"ola" es Ola Mayorista?                                   [ Sí ]   [ No ]           │
```

**Qué NO es duda grande (el sistema decide solo, sin preguntar):** cómo se escribe la habitación
("dbl sup" → "Doble Superior"), mayúsculas y acentos, si el hotel es "Sheraton Iguazú" o "sheraton
iguazu", el orden de los datos en la frase, el redondeo de las noches.

---

## 5. El Tarifario: cómo queda la lista

### 5.1 Solapas por tipo (V8=A) y agrupación por habitación (V5=A, V6=A, V7=A)

```
┌───────────────────────────────────────────────────────────────────────────────────────┐
│  Tarifario                                                        [ + Agregar producto ]
│  Los productos que ya vendiste, con el último precio de cada operador.                │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  [ Hoteles (38) ] [ Aéreos (12) ] [ Paquetes (9) ] [ Traslados (5) ] [ Asistencias (3) ]
│                                                                    [ Repetidos (4) ]  │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  [ 🔍 Buscar hotel…                                    ]   Operador [ Todos ▾ ]       │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  HOTEL                    HABITACIÓN            OPERADOR        PRECIO        CUÁNDO  │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Maitei Posadas           Doble con desayuno    Ola Mayorista   US$ 48/noche  22/05/26│
│  Posadas, Misiones                              Julia Tours     US$ 52/noche  03/07/26│
│                           Triple con desayuno   Ola Mayorista   US$ 70/noche  03/07/26│
│                           + 3 precios más — tocá el hotel para verlos                 │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Howard Johnson Posadas   Doble media pensión   Julia Tours     US$ 61/noche  11/06/26│
│  Posadas, Misiones                                                                    │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- **Una solapa por tipo**, con el conteo. La solapa en cero se ve apagada (criterio 2026-08-03 P3=B).
  **Muere el desplegable "Tipo"** y **muere la columna "Tipo"** (P-16: la solapa ya lo dice).
- **Columnas por solapa:**
  - Hoteles: HOTEL · HABITACIÓN · OPERADOR · PRECIO · CUÁNDO
  - Aéreos: RUTA · CABINA · OPERADOR · PRECIO · CUÁNDO
  - Traslados: TRAYECTO · VEHÍCULO · OPERADOR · PRECIO · CUÁNDO
  - Paquetes y Asistencias: PRODUCTO · OPERADOR · PRECIO · CUÁNDO (sin columna del medio)
- **Agrupado por habitación; adentro, los operadores** ordenados por fecha, el más nuevo arriba. Los
  grupos, por su precio más nuevo arriba.
- **Tope: 3 renglones de precio** por producto + línea gris **"+ N precios más — tocá el hotel para
  verlos"**.
- **Sin variante cargada (V3=A):** la celda del medio queda **vacía**. No se escribe "Sin
  especificar".
- Sigue valiendo: fecha en **ámbar** si el precio tiene más de 60 días (P10=A); **precio de venta**
  para quien no ve costos (F-14); **una sola lista sin decir el origen** del producto (P2=A y
  derogación 2026-06-08).

### 5.2 El texto libre con memoria (V4=B ajustada)

El nombre fino de la habitación (y el vehículo del traslado) se escribe libre **la primera vez**;
después el sistema lo ofrece y **frena las variaciones de tipeo**:

```
   PRIMERA VEZ                              LAS SIGUIENTES
   ─────────────────────────────            ─────────────────────────────────────────
   Habitación [ Doble ▾ ]                   Habitación [ Doble ▾ ]
   Nombre     [ Superior            ]       Nombre     [ sup                       ]
              (podés escribir lo que sea)              ┌──────────────────────────┐
                                                       │  Superior                │
                                                       │  Vista al mar            │
                                                       │  + Usar "sup" tal cual   │
                                                       └──────────────────────────┘
```

- Lo que ya se escribió alguna vez **aparece primero**; escribir algo nuevo **siempre se puede**
  (última opción de la lista, igual que "crear producto nuevo" — P7).
- Si lo nuevo es **una variación de tipeo** de algo que ya existe ("dbl sup", "SUPERIOR", "superio"),
  el sistema **lo unifica solo** con lo que ya estaba. No pregunta: eso no es duda grande (§4).
- **En pantalla la habitación se lee siempre en una frase criolla**: "Doble Superior con desayuno".

---

## 6. El bibliotecario y la bandeja "Repetidos" (V11=B, V12, V13, V14)

**Qué hace el bibliotecario, solo y sin molestar a nadie:** normaliza mayúsculas, acentos, espacios y
abreviaturas; **une lo que es obviamente el mismo producto**; **elige el nombre limpio**; y convierte
en habitación lo que el formulario viejo había metido dentro del nombre
("Sheraton Iguazú - Doble Superior" → producto "Sheraton Iguazú" + habitación "Doble Superior").

Lo que **no** puede resolver solo cae en la bandeja, **agrupado** (V11=B):

```
│  [ Hoteles ] [ Aéreos ] [ Paquetes ] [ Traslados ] [ Asistencias ] │ [ Repetidos (4) ]│
├───────────────────────────────────────────────────────────────────────────────────────┤
│  Sheraton Iguazú · Puerto Iguazú · 3 precios                                          │
│     se le parecen:                                                                    │
│     · Sheraton Iguazú - Doble Superior · 1 precio        [ Es el mismo ]  [ Es otro ] │
│       la habitación pasaría a ser "Doble Superior"                                    │
│     · Sheraton Iguazu · Puerto Iguazu · 2 precios        [ Es el mismo ]  [ Es otro ] │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Maitei Posadas · Posadas · 4 precios                                                 │
│     se le parecen:                                                                    │
│     · Maitei Posada · Posadas · 1 precio                 [ Es el mismo ]  [ Es otro ] │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│  Ordenados y unidos por el sistema esta semana: 12          [ Ver qué ordenó ]        │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- **El de arriba es el que se queda** (el nombre limpio lo eligió el sistema, V12). Debajo, todos los
  que se le parecen, cada uno con su propio par de botones.
- **"Es el mismo"** une: el de arriba **absorbe los precios y las habitaciones** del otro; si los dos
  tenían la misma habitación, **queda el precio más nuevo**. **Nada se borra** (2026-08-03): el
  absorbido deja de listarse, con rastro.
- **"Es otro"** hace que ese par no vuelva a proponerse.
- **La línea de habitación** ("la habitación pasaría a ser 'Doble Superior'") aparece **solo** cuando
  el candidato viene del nombre viejo con sufijo. Es la única aclaración permitida acá, porque
  explica qué va a pasar con un dato (V14).
- **El rastro de lo que hizo el sistema vive SOLO acá**, en una línea al pie con
  **[ Ver qué ordenó ]** (abre la lista, en línea, de lo unido/renombrado con fecha y **Deshacer**).
  **En la lista normal del Tarifario NO se muestra ninguna etiqueta de origen** — eso está derogado
  desde el 2026-06-08 ("no tiene sentido que un usuario vea si fue creado en el tarifario o en la
  venta").
- **Vacío:** solapa apagada con "0" y adentro "No hay productos para revisar."

---

## 7. La ficha del producto (V15)

```
│  Maitei Posadas          Doble con desayuno    Ola Mayorista   US$ 48/noche  22/05/26 │
│  ┌─────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Nombre *  [ Maitei Posadas               ]   Ciudad *  [ Posadas           ]   │  │
│  │                                                                                 │  │
│  │  Precios que aprendió de tus ventas                                             │  │
│  │  ─────────────────────────────────────────────────────────────────────────────  │  │
│  │  Doble con desayuno                                            [ Corregir ]     │  │
│  │      Ola Mayorista     US$ 48 /noche    22/05/2026    F-2026-1042               │  │
│  │      Julia Tours       US$ 52 /noche    03/07/2026    F-2026-1109               │  │
│  │  Triple con desayuno                                           [ Corregir ]     │  │
│  │      Ola Mayorista     US$ 70 /noche    03/07/2026    F-2026-1120               │  │
│  │  Doble Superior con desayuno                                   [ Corregir ]     │  │
│  │      Ola Mayorista     US$ 55 /noche    18/02/2026                              │  │
│  │                                                                                 │  │
│  │  [ Carga completa ]                          [ Cancelar ]   [ Guardar ]         │  │
│  └─────────────────────────────────────────────────────────────────────────────────┘  │
```

- Se abre **en línea, debajo del renglón** (P-5). Nunca ventana flotante.
- **Se corrigen TEXTOS: nombre, ciudad y la etiqueta de la habitación. Los importes JAMÁS** (son la
  memoria de lo que pasó; se cambian vendiendo — firmado 2026-08-06).
- `[ Corregir ]` abre en la misma línea los desplegables de siempre + el nombre fino con memoria
  (§5.2) + Guardar / Cancelar. Si al corregir queda igual que otra habitación que ya existe, **las
  dos se juntan solas y queda el precio más nuevo** (no es duda grande).
- El **número de reserva es un enlace** a su ficha.
- `[ Carga completa ]` sigue siendo el único acceso al formulario largo (P1=B).

---

## 8. Alta a mano de un producto (V16)

```
│  ┌─────────────────────────────────────────────────────────────────────────────────┐  │
│  │  Tipo [ Hotel ▾ ]   Nombre *  [                              ]                  │  │
│  │  Ciudad * [                 ]   Operador [ Ola Mayorista  ▾ ]                   │  │
│  │  Habitación [ Doble ▾ ]  Régimen [ Desayuno ▾ ]  Nombre [               ]       │  │
│  │  Precio   [ US$ ▾ ] [           ]  por noche                                    │  │
│  │                                                                                 │  │
│  │  [ Carga completa ]                          [ Cancelar ]   [ Guardar ]         │  │
│  └─────────────────────────────────────────────────────────────────────────────────┘  │
```

- **Habitación y Régimen con Doble / Desayuno ya puestos** (mismos desplegables de la venta, mismos
  defaults del 2026-06-06 Ronda 7). Sin asterisco: vienen completos.
- **Nombre fino** de la habitación: texto libre con memoria (§5.2).
- En Aéreo aparece **Cabina**; en Traslado, **Vehículo** (texto libre con memoria); en Paquete y
  Asistencia, ninguno de los dos.
- **Antes de guardar corre el freno de repetidos** (P7). Sin leyendas ni "(opcional)" (P-15).

---

## 9. Estados de pantalla (todos)

| Dónde | Estado | Qué se ve |
|---|---|---|
| Tarifario | Cargando | Renglones grises, sin cartel. |
| Tarifario | Solapa vacía | "Todavía no vendiste ningún hotel." (adaptado por tipo). |
| Tarifario | Buscador sin resultados | "No encontramos '{texto}' en tu tarifario." |
| Tarifario | Error al traer la lista | Cartel rojo + "Probar de nuevo". |
| Tarifario | Producto sin precios | Celda de precio: "Sin precios cargados", en gris. |
| Ficha producto | Guardado OK | "Producto guardado." y se cierra. |
| Ficha producto | Error al guardar | Queda **abierta con todo intacto** + cartel rojo arriba de los botones; se reintenta en el mismo botón (Ronda 2, 2026-06-06). |
| Repetidos | Vacío | Solapa apagada "0" + "No hay productos para revisar." |
| Repetidos | Uniendo | Ese renglón con los botones apagados y "Uniendo…". |
| Repetidos | Unido OK | El renglón desaparece del grupo + "Listo, quedó uno solo." |
| Repetidos | Error al unir | El grupo **queda como estaba** + cartel rojo con "Probar de nuevo". |
| Línea inteligente | Pensando | "Buscando…" sutil, sin trabar nada. |
| Línea inteligente | Entendió todo | Campos en amarillo + renglón gris de procedencia. |
| Línea inteligente | Entendió a medias | Lo entendido en amarillo; el resto vacío, **sin explicación**. |
| Línea inteligente | No entendió / sin IA / IA caída / tardó | **El buscador de siempre**, sin una sola palabra distinta. |
| Línea inteligente | Duda grande | Una línea con **Sí / No** debajo del campo. No traba Guardar. |
| Todas | Sin permiso de costos | Precio de **venta** en vez de costo; el costo lo completa el motor por detrás (F-14). |

---

## 10. Qué NO hay que hacer

- ❌ Crear **un producto por habitación**. Un hotel = un producto (P7).
- ❌ Mostrar un precio **sin decir de qué habitación es** (salvo que no haya dato: entonces vacío).
- ❌ Precargar en el costo un precio **de otra habitación** (V9=A: queda vacío + renglón gris).
- ❌ Escribir en pantalla **"IA"**, "variante", "clave", "modelo", "confianza", "no disponible", ni
  ningún código de error (P-17 + gate de exposición de datos).
- ❌ Un cartelito "Entendí esto" o "Armado automáticamente": **habla el amarillo** (P-15, P-16).
- ❌ **Guardar el servicio solo**, o crear un producto nuevo solo, sin que el vendedor toque Guardar.
- ❌ Ventanas flotantes en la ficha de carga (P-5, regla del Cartel emergente 2026-07-22).
- ❌ Preguntas de más: solo **dudas grandes**, de a una, en una línea sí/no.
- ❌ Botones de borrar producto, precio o habitación (nada se borra, 2026-08-03).
- ❌ Etiquetas de origen ("lo ordenó el sistema") **en la lista normal** — solo en la bandeja de
  repetidos (derogación 2026-06-08).
- ❌ Sumar monedas en ninguna columna ni total (P-3).

---

## 11. Dependencias del motor (para el brief del backend)

> Ninguna es decisión de UX. Continúan la numeración de la spec del 2026-08-06 (llegaba a M-11).
> Todo lo derivado lo calcula el motor (T-13): el front no resta fechas, no convierte monedas y no
> arma textos.

### 11.1 Variantes (la memoria por habitación)

| # | Qué hace falta | Para qué |
|---|---|---|
| M-12 | **La memoria de precios pasa a ser por (producto, operador, variante)**. Variante = habitación + régimen + nombre fino (hotel) / cabina (aéreo) / vehículo (traslado) / **ninguna** (paquete, asistencia). Se guarda además **la etiqueta ya armada en criollo** ("Doble Superior con desayuno"). | §5.1 |
| M-13 | **La venta deja de tirar la habitación y el régimen**: al guardar un servicio, se actualiza la fila de ESA combinación, sin pisar las demás. | §5.1 |
| M-14 | **Listado agrupado**: producto → habitación → operadores, con precio, moneda, unidad, fecha, número de reserva, marca de precio viejo, **el tope de 3 renglones + el total ("+N precios más")**, filtro por tipo con **conteo por tipo** (solapas) y enmascarado sin `cobranzas.see_cost` (F-14). | §5.1 |
| M-15 | **Sugerencia por variante**: dada la combinación elegida devuelve (a) su precio si existe, (b) si no, el de la habitación **más parecida** con su etiqueta y marcado como "es de otra habitación" (para el renglón gris, sin precargar), (c) nada si no hay precios. | §3.3 |
| M-16 | **Migración del nombre viejo con sufijo** (`" - {habitación} {categoría}"`, que generó nuestro propio formulario): unificar con el producto limpio cuando el nombre base y la ciudad coinciden y **convertir el sufijo en habitación**. **Con rastro y reversible**; lo que no se puede unir solo, queda como candidato de la bandeja. | §6 |
| M-17 | **Unir dos productos** (el de arriba absorbe precios y habitaciones; misma habitación → queda el precio más nuevo; el absorbido no se borra, deja de listarse) y **"es otro"** (no volver a proponer). | §6 |
| M-18 | **Corregir la etiqueta de una habitación** y **fusionar dos habitaciones** cuando la corrección las deja iguales. **Nunca toca importes.** | §7 |
| M-19 | **Texto libre con memoria** para el nombre fino de la habitación y el vehículo: guardar lo escrito por producto/agencia, devolverlo como sugerencias, y **unificar solo las variaciones de tipeo** de algo ya escrito. | §5.2 |

### 11.2 La línea inteligente (IA)

> **Infraestructura verificada hoy en el repo:** `IAiAssistantService.CompleteStructuredAsync<T>`
> (`src/TravelApi.Application/Interfaces/IAiAssistantService.cs`) ya hace salida estructurada con
> deserialización estricta, **un reintento** y **degradación elegante** (`Succeeded=false`, nunca
> lanza). Implementación en `src/TravelApi.Infrastructure/Ai/AiAssistantService.cs` (ADR-016 F0a).
> **No hay que inventar plomería nueva**: esta obra es un consumidor más.

| # | Qué hace falta | Para qué |
|---|---|---|
| M-20 | **Endpoint "interpretar un texto de servicio"**: recibe el texto libre + el tipo de servicio elegido + la reserva, y devuelve **producto candidato (o parecidos), operador, habitación/régimen/nombre fino (o cabina/vehículo), precio, moneda, unidad, fechas**. Cada dato con su **nivel de confianza**. | §3.3 |
| M-21 | **Contexto acotado** para la IA: solo el tarifario de esa agencia (productos + habitaciones ya usadas) y sus operadores. **Nunca** datos de pasajeros, clientes, documentos ni importes de otras reservas. | privacidad |
| M-22 | **Las dudas grandes las decide el motor**, por **código** (no texto libre) + el texto en criollo ya armado. Máximo una por respuesta, priorizando la de plata. | §4 |
| M-23 | **Degradación**: sin clave / IA caída / JSON inválido tras el reintento / tardanza → responde "no interpretado" **sin error** y el front cae al buscador de siempre. **Nada técnico llega a la pantalla.** | §3.5 |
| M-24 | **Bibliotecario v0 (determinístico, primero):** normalizar mayúsculas/acentos/espacios/abreviaturas conocidas, unir lo idéntico, y armar los **grupos de parecidos** de la bandeja. Sin IA. | §6 |
| M-25 | **Bibliotecario v1 (con IA, después):** proponer agrupaciones más finas sobre el mismo contrato que ya consume la bandeja. **La pantalla no cambia** cuando v1 reemplace a v0. | §6 |
| M-26 | **Rastro y Deshacer** de todo lo que el sistema ordenó solo (unió, renombró, convirtió un sufijo en habitación): qué, cuándo, y volver atrás. Alimenta `[ Ver qué ordenó ]`. | §6 |
| M-27 | **Permisos en la interpretación**: sin `cobranzas.see_cost` la respuesta **no trae costos**; el costo lo completa el motor al guardar, como ya hace hoy (F-14). | §3.3 |

> **M-28 a M-33** (configurar la IA desde Configuración, con la clave cifrada en la base) están en la
> **adenda §15.10**.

**Orden sugerido de construcción** (lo decide el orquestador con Gastón): M-12/M-13/M-14 (la memoria
por habitación y la lista con solapas — es lo que Gastón ve roto hoy) → M-16/M-24/M-17 (migración del
sufijo + bibliotecario v0 + bandeja) → M-15/M-19 (sugerencia por variante y texto libre con memoria)
→ M-20..M-23/M-27 (la línea inteligente) → M-18/M-26 (correcciones y rastro) → M-25 (bibliotecario
v1).

---

## 12. Derogaciones y contradicciones resueltas

1. **⚠️ DEROGACIÓN QUIRÚRGICA de P6=A (2026-08-06)** — *"un renglón por operador debajo del
   producto"*. Con **V5=A** manda **habitación primero, operadores adentro**. **Lo que motivaba P6
   sigue intacto**: el operador se ve en **todos** los renglones de precio, porque "el precio sin el
   operador no sirve para decidir". Cambia el orden de los niveles, no la información.
2. **P5=A ("el renglón muestra lo esencial") se mantiene**: se agrega la columna HABITACIÓN pero
   **se saca la columna TIPO** (que pasa a ser la solapa, V8=A). El renglón no crece.
3. **El desplegable "Tipo" construido en la tanda B1 desaparece**, reemplazado por las solapas
   (V8=A). Es reemplazo, no duplicación (P-16).
4. **V4=B (texto libre) convive con P7 ("evitar repetidos a toda costa")** gracias al ajuste que
   Gastón pidió: el texto libre **tiene memoria** y **el sistema unifica solo las variaciones de
   tipeo**. Sin esa memoria, el texto libre sería una fábrica de repetidos y contradiría P7.
5. **La duda grande NO va al Cartel emergente** (2026-07-22): esa regla excluye explícitamente las
   fichas de trabajo, donde el vendedor está trabajando y una ventana lo interrumpiría.
6. **El rastro del bibliotecario NO se muestra en la lista normal** (derogación del 2026-06-08 sobre
   etiquetas de origen): vive solo en la bandeja de repetidos, que es pantalla de revisión.

---

## 13. Lo único que falta que Gastón firme (3 preguntas)

> Son de forma, no de fondo. Con cualquiera de las opciones la obra arranca igual: si no responde,
> se construye la marcada con ⭐.

**Q1. La línea inteligente, ¿es el mismo casillero del buscador de producto, o una caja aparte?**

  **A) El mismo casillero, agrandado: escribís una palabra o la frase entera, y funciona igual.**
     ⭐ RECOMENDADA (un solo lugar donde escribir; no hay que explicarle a nadie cuál usar).
```
     Escribilo como te salga
     [ sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9                     🔍 ]
     Producto * [ Sheraton Iguazú · Puerto Iguazú     ]  ← se completó solo, amarillo
```

  **B) Una caja aparte arriba de la ficha, y abajo los campos de siempre.**
```
     ┌ Pegá o escribí todo junto ──────────────────────────────────────────────────┐
     │ [ sheraton iguazu doble desayuno ola 48 usd del 12 al 15/9      ] [ Armar ] │
     └─────────────────────────────────────────────────────────────────────────────┘
     Producto *  [ 🔍 Buscar hotel…                    ]
     Operador    [                                   ▾ ]
```

---

**Q2. Cuando el sistema tiene una duda grande, ¿precarga igual y pregunta abajo, o deja el campo
vacío hasta que contestes?**

  **A) Precarga lo que entendió (en amarillo) y pregunta abajo, en una línea.** ⭐ RECOMENDADA
     (si acertó, tocás "Sí" y seguís; nunca tenés que escribir de nuevo algo que ya estaba bien).
```
     Costo/noche  [ US$ 48,00                  ]   ← amarillo
     ¿"48" es el precio por noche?                 [ Sí ]   [ No ]
```

  **B) Deja el campo vacío y lo completa recién cuando contestás.**
```
     Costo/noche  [                            ]
     ¿"48" es el precio por noche?                 [ Sí ]   [ No ]
```

---

**Q3. ¿Cuánto puede unir el sistema por su cuenta?**

  **A) Une solo lo idéntico (mismo nombre con otra escritura). Todo lo demás te lo deja en la
     bandeja para que decidas vos.**
```
     Unió solo:  "sheraton iguazu"  →  "Sheraton Iguazú"
     Te deja:    "Sheraton Iguazú - Doble Superior"      [ Es el mismo ]  [ Es otro ]
```

  **B) Une también los "casi seguros" (mismo nombre + misma ciudad + el sufijo que puso nuestro
     propio formulario), te avisa en la bandeja y podés deshacerlo cuando quieras.** ⭐ RECOMENDADA
     (es lo que pediste: "que decida solo"; y nada se pierde porque todo tiene Deshacer).
```
     Ordenados y unidos por el sistema esta semana: 12        [ Ver qué ordenó ]
        Sheraton Iguazú - Doble Superior → Sheraton Iguazú
        (la habitación quedó como "Doble Superior")   07/08/2026      [ Deshacer ]
```

---

## 14. Fuera de alcance de esta tanda

- **Cobranzas** y la fecha límite de pago: ya firmadas el 2026-08-06, no se tocan acá.
- **El formulario largo** ("Carga completa"): sigue vivo detrás de su botón, sin cambios.
- **Chat / copiloto general**: esta obra usa la IA **solo** para interpretar la línea de carga de
  servicio y ordenar el tarifario. Nada más.
- **Los tres detalles abiertos D1/D2/D3** de la spec del 2026-08-06 siguen abiertos tal cual.

---

## 15. ADENDA (2026-08-07): Configuración → Inteligencia artificial

> **Pedido textual de Gastón, el mismo día:** *"que en Configuración haya un lugar para configurar la
> IA, universal para cualquier tipo de IA (Claude/Grok/Groq/Llama/etc.)"*.
>
> **Restricciones técnicas verificadas** (el diseño no promete imposibles): el cerebro habla el
> formato **compatible con OpenAI**, así que entran **Groq, OpenAI, Claude (Anthropic), Gemini
> (Google), Grok (X), OpenRouter y cualquier Llama hosteado** con tres datos (dirección, clave,
> modelo). **GitHub Copilot y "Codex" NO se pueden conectar así: no se ofrecen** (ofrecerlos sería
> prometer algo que no funciona).

### 15.1 Dónde vive

**Configuración → solapa nueva "Inteligencia artificial"**, al lado de "Facturación" y "WhatsApp
Bot". **Solo Admin** (misma puerta que la solapa de Facturación). Un vendedor común **no la ve**: no
existe la solapa para él, no aparece apagada.

```
   Configuración
   [ Agencia ] [ Operativa y Caja ] [ Facturación ] [ WhatsApp Bot ]
   [ Inteligencia artificial ]  ← nueva, solo Admin
   [ Workflows de aprobación ] [ Logs y Programación ]
```

### 15.2 La pantalla completa (estado: ya configurada y funcionando)

```
┌ Inteligencia artificial ──────────────────────────────────────────────────────────────┐
│  El sistema usa la inteligencia artificial para entender lo que escribís al cargar un │
│  servicio y para ordenar el tarifario. Si no hay nada configurado, todo funciona      │
│  igual, sin esas ayudas.                                                              │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  🟢  Funcionando con Groq                                                             │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  ¿Con cuál querés trabajar?                                                           │
│                                                                                       │
│    (•) Groq            Gratis para arrancar. Es la más simple.                        │
│    ( ) OpenAI          La de ChatGPT.                                                 │
│    ( ) Claude          La de Anthropic.                                               │
│    ( ) Gemini          La de Google.                                                  │
│    ( ) Grok            La de X.                                                       │
│    ( ) OpenRouter      Una sola clave para usar varias.                               │
│    ( ) Otra            Ponés la dirección y el modelo a mano.                         │
│                                                                                       │
│  Clave     Configurada ✓ · empieza con  gsk_…            [ Cambiar la clave ]         │
│                                                                                       │
│  [ Probar conexión ]        Funciona ✓ (contestó en 0,8 s)                            │
│                                                                                       │
│  ▸ Ajustes avanzados                                                                  │
│                                                                                       │
│                                              [ Cancelar ]   [ Guardar ]               │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

- **El nombre de cada opción es el de la calle** (Groq, OpenAI, Claude, Gemini, Grok, OpenRouter),
  con **una línea corta** que dice qué es. **Groq viene recomendado y marcado por defecto cuando no
  hay nada configurado**, porque es gratis para arrancar.
- **Elegir una opción precarga sola la dirección y el modelo** que corresponden. El usuario **no ve
  ni toca** esos dos datos salvo que abra "Ajustes avanzados" o elija "Otra".
- **Excepción de P-15 acotada a esta pantalla:** se permite **una línea de ayuda por campo**, porque
  el dato viene de afuera del sistema (la clave la da el proveedor en su página). Es el mismo
  criterio que ya rige la solapa **Facturación** (certificados de ARCA). **No habilita cartelitos en
  el resto de la app.**

### 15.3 La clave: se pega, nunca se vuelve a ver

**Nunca configurada:**
```
│  Clave     [                                              ]                           │
│            Te la da Groq en su página, al crear una cuenta.                           │
```

**Ya configurada** (así queda para siempre; el sistema no la muestra nunca más):
```
│  Clave     Configurada ✓ · empieza con  gsk_…            [ Cambiar la clave ]         │
```

**Cambiándola** (al tocar "Cambiar la clave"):
```
│  Clave     [ ●●●●●●●●●●●●●●●●●●●●●●●●●●●●●●             ]   [ Cancelar el cambio ]   │
│            Pegá la nueva. La anterior se reemplaza al guardar.                        │
```

- **La clave se guarda cifrada** con el mismo mecanismo que ya protege los datos sensibles de ARCA.
- **Es de una sola dirección:** entra y no sale. La pantalla muestra **"Configurada ✓" + los primeros
  4 caracteres**, nunca la clave entera. Cambiarla es **pegar una nueva encima**.
- **No se puede "ver" ni "copiar"**: no hay ojito, no hay botón de copiar.
- Queda registrado **quién la cambió y cuándo** (mismo criterio que el resto de Configuración).

### 15.4 "Probar conexión": la respuesta, en criollo

El botón manda un saludo mínimo al proveedor y contesta en la misma línea, al lado del botón:

```
   Probando…
   [ Probar conexión ]        Probando…

   Anduvo
   [ Probar conexión ]        Funciona ✓ (contestó en 0,8 s)

   Clave mal puesta o vencida
   [ Probar conexión ]        ✕ La clave no sirve o venció.

   El proveedor no contesta
   [ Probar conexión ]        ✕ No hay conexión con el proveedor. Probá de nuevo en un rato.

   Dirección escrita a mano que no responde (solo en "Otra")
   [ Probar conexión ]        ✕ Esa dirección no responde. Revisá que esté bien escrita.

   El modelo elegido no existe para ese proveedor
   [ Probar conexión ]        ✕ Ese modelo no existe para este proveedor.
```

- **Prueba lo que hay en pantalla**, aunque todavía no se haya guardado (así se prueba antes de
  romper lo que funcionaba).
- **El motivo viaja por código**, no comparando textos (patrón 2026-07-22); el front solo elige la
  frase. **Jamás** se muestra el mensaje crudo del proveedor, ni un número de error, ni un nombre
  técnico.
- **Probar no guarda.** Guardar es el botón de abajo.

### 15.5 La foto de arriba (el estado, en una línea)

```
   🟢  Funcionando con Groq
   ⚪  Sin configurar — el sistema funciona igual, sin las ayudas inteligentes.
   🟠  Configurada con Claude, pero la última prueba no anduvo.
```

- Es **una sola línea**, arriba de todo. **Cero palabras técnicas**: no aparece "endpoint", "token",
  "API", "modelo", "proveedor caído", "timeout" ni ningún código.
- El ámbar aparece **solo** si la última prueba guardada falló; se apaga solo cuando una prueba
  vuelve a andar.

### 15.6 Ajustes avanzados (plegados) y la opción "Otra"

```
│  ▾ Ajustes avanzados                                                                  │
│      Dirección  [ https://api.groq.com/openai/v1                        ]             │
│      Modelo     [ llama-3.3-70b-versatile                               ]             │
│      [ Volver a los valores recomendados ]                                            │
```

- **Cerrados por defecto** (criterio "Más detalles cerrado", 2026-06-06 Ronda 7).
- Se abren solos y **quedan obligatorios** cuando se elige **"Otra"** (es la única forma de conectar
  un Llama propio o algo que todavía no está en la lista).
- **"Volver a los valores recomendados"** deja la dirección y el modelo del preset elegido. No toca
  la clave.

### 15.7 Qué pasa con la llave `EnableAiCopilot` que ya existe

**Se muere.** Hoy es un interruptor aparte, y tener dos lugares para prender lo mismo es
exactamente lo que la orden general del dueño prohíbe (**"basta de llaves"**, la misma que mató
`enableCatalogFindOrCreate` con P8=A el 2026-08-06).

**La regla nueva es una sola: si hay una IA configurada, las ayudas inteligentes funcionan. Si no
hay, no funcionan y el sistema anda igual** (§3.5). No hace falta prender nada.

- Se saca el interruptor de donde esté expuesto en Configuración.
- El campo del motor puede quedar un tiempo por compatibilidad, pero **ninguna pantalla lo muestra y
  ninguna decisión lo mira**: manda "¿hay configuración válida?".

### 15.8 Estados de la pantalla

| Estado | Qué se ve |
|---|---|
| Cargando | Renglones grises, sin cartel. |
| Sin configurar | Foto ⚪ + Groq marcado por defecto + clave vacía. **Guardar apagado hasta que haya clave.** |
| Configurada | Foto 🟢 + "Configurada ✓ · empieza con gsk_…". |
| Probando | "Probando…" al lado del botón; el botón apagado mientras tanto (no se puede disparar dos veces). |
| Guardando | "Guardar" apagado con "Guardando…". |
| Guardado OK | "Listo, la inteligencia artificial quedó configurada." + la foto se actualiza sola. |
| Error al guardar | La pantalla **queda como estaba, con todo lo cargado intacto** + cartel rojo arriba de los botones + se reintenta en el mismo botón (Ronda 2, 2026-06-06). |
| Cambió el proveedor pero no pegó la clave nueva | Al guardar: "Pegá la clave de {OpenAI} para poder usarla." pegado al campo (error corto de campo, no ventana). |
| La clave la puso el técnico al instalar (respaldo del servidor) | Foto 🟢 + en el campo: "La puso el técnico al instalar. Si pegás una acá, manda la tuya." |
| Sin permiso (no Admin) | **La solapa no existe.** |

### 15.9 Qué NO hacer

- ❌ Volver a mostrar la clave, ni un ojito, ni un botón de copiar.
- ❌ Mandar la clave al navegador en ninguna respuesta del motor.
- ❌ Ofrecer **GitHub Copilot** o **Codex**: no se conectan de esta forma.
- ❌ Palabras técnicas en pantalla: "endpoint", "API key", "token", "timeout", "modelo LLM",
  "provider", códigos de error, nombres de clases o de servicios (P-17 + gate de exposición).
- ❌ Un interruptor de "prender la IA" además de la configuración (§15.7).
- ❌ Que "Probar conexión" guarde, o que "Guardar" pruebe solo y frene por una prueba fallida: se
  puede guardar algo que no anduvo (quizás el proveedor está caído en ese momento).
- ❌ Cartelitos largos explicando qué es la inteligencia artificial: una línea de bajada y listo.

### 15.10 Dependencias del motor (adenda)

| # | Qué hace falta | Para qué |
|---|---|---|
| M-28 | **Configuración de IA guardada en la base**, con la **clave CIFRADA** con el mismo mecanismo que ya protege los datos sensibles de ARCA (`ISensitiveDataProtector`). Guarda: proveedor elegido, dirección, modelo, clave cifrada, **prefijo de 4 caracteres** (lo único que se muestra), y el resultado de la última prueba. **La respuesta del GET nunca incluye la clave** (write-only). | §15.3 |
| M-29 | **Precedencia: lo cargado en la pantalla MANDA sobre las variables de entorno.** El env queda como **respaldo** para cuando no hay nada configurado. Esto es una **adenda a ADR-016**, que decía "config solo por env": **decisión del dueño, 2026-08-07** (ver §15.11). | §15.5 |
| M-30 | **Cache invalidado SIEMPRE al guardar.** ⚠️ **Lección del cache de AfipSettings:** el resolver de configuración cachea, y si no se invalida al guardar, el sistema sigue usando la clave vieja sin que nadie entienda por qué. Además, **el consumidor crítico relee la configuración autoritativa** antes de usarla, no confía en el cache. | §15.2 |
| M-31 | **Endpoint "probar conexión"**: dispara un saludo mínimo (la llamada más barata posible) contra lo que viene en el pedido — **aunque todavía no esté guardado** — y devuelve **un código de resultado** (anduvo / clave inválida / no responde / dirección inválida / modelo inexistente) **+ el tiempo que tardó**. **Nunca devuelve el texto crudo del proveedor.** Con límite de intentos para que no se use como sonda. | §15.4 |
| M-32 | **Presets del lado del motor** (nombre en criollo, dirección y modelo recomendados por proveedor), para que agregar un proveedor nuevo mañana **no obligue a tocar el front**. La lista sale del motor, no está escrita a mano en la pantalla. | §15.2 |
| M-33 | **`EnableAiCopilot` deja de gobernar nada** (§15.7): el interruptor sale de la superficie y la decisión pasa a ser "¿hay configuración de IA utilizable?". La degradación de §3.5 sigue igual. | §15.7 |

**Guardas de seguridad que el reviewer tiene que verificar** (no son decisión de UX, son piso):
solo Admin puede leer o escribir esta configuración; la clave **no aparece en logs** ni en mensajes
de error; el cambio queda auditado; y el endpoint de prueba **no** se puede usar para pegarle a una
dirección interna arbitraria sin control (lo evalúa `security-data-risk-reviewer`).

### 15.11 Adenda formal a ADR-016 (dejarla escrita al construir)

ADR-016 (F0a) definió que la conexión al cerebro vive **solo en variables de entorno** (`Ai__*`) y
que *"la API key es un secreto y nunca va a la DB"*.

**Gastón derogó eso para este caso el 2026-08-07:** la clave **sí** va a la base, **cifrada**, con el
mismo mecanismo que ya usa ARCA, porque el dueño de una agencia tiene que poder configurar su IA
**desde la pantalla**, sin un técnico y sin tocar el servidor. **El env queda como respaldo** cuando
no hay nada cargado (M-29). Esta adenda se escribe en el ADR al construir, para que nadie la
"corrija" después creyendo que es un error.

---

## ADDENDUM FIRMADO 2026-08-08 — V17: Excursiones con solapa propia

Pregunta surgida en review (la spec original fijaba 5 solapas en V8=A y no cubría
Excursión ni "Otro", que quedaban invisibles tras el alta):

**Respuesta de Gastón (2026-08-08): "Excursiones con solapa propia".**

- **V17=C**: se agrega la **sexta solapa "Excursiones"** (mismo comportamiento que
  las otras: conteo, apagada en cero, vacío "Todavía no vendiste ninguna excursión.").
- **"Otro" queda AFUERA del tarifario**: no se ofrece en el alta a mano, no se
  lista ni se aprende. Se sigue vendiendo normal en las reservas.
- Deroga quirúrgicamente el "5 solapas" de V8=A (pasa a 6); el resto de V8=A intacto.

---

## ADDENDUM FIRMADO 2026-08-09 — V18: la IA se esconde y se enfoca en evitar duplicados

Gastón vio la línea inteligente construida y deployada (F2, commit `51dc4f5f`) y
**no le cerró el diseño de la ficha** (palabras textuales: *"la forma de funcionar
es como que exige usar la IA cuando no lo veo necesario, sería algo más backend;
eso de 'escribilo como te salga' es raro, no sé si así tendría que andar en un
ERP"*). Sus respuestas explícitas del 2026-08-09:

1. **"Sacar la ayuda de la ficha"** → **§3 y §4 quedan DEROGADOS para la ficha de
   servicio** (Q1 y Q2 sin efecto). La ficha de carga vuelve a verse y usarse
   EXACTAMENTE como antes de F2: mismo buscador, mismos labels, sin precargado de
   frases, sin renglón "Producto *", sin preguntas Sí/No. Las preguntas de duda
   quedan construidas en el motor, dormidas, para un contexto futuro.
2. **"La idea es que esto sea más una inteligencia que ayude a evitar
   duplicados"** → la IA de la ficha pasa a ser un **matcher anti-duplicados
   INVISIBLE** (refuerza P7): con 2+ palabras y sin parecido fuerte local, el
   sistema consulta al motor por detrás y solo hace dos cosas, sin ningún cambio
   visual: (a) **mejora la lista de parecidos** del desplegable de siempre para
   que el vendedor elija el producto que YA existe aunque lo escriba distinto
   ("sheraton iguazu dbl"); (b) **limpia el nombre de la opción "crear …"** para
   que un alta nueva no nazca con basura en el nombre. Nada más de la respuesta
   del motor se usa. Si la IA no está o falla, el desplegable es el de hoy.
3. **Orden confirmado**: 1º el buscador invisible; 2º el bibliotecario nocturno
   (F3 / M-25, que sigue firmado tal cual — la bandeja no cambia).
4. **§15 (Configuración → Inteligencia artificial) queda INTACTO**: la pantalla
   no recibió objeción y sigue vigente completa.

El motor construido en F2 (M-20..M-23, M-27) **no se tira**: queda como proveedor
del matcher (mismo endpoint, el front consume solo `productCandidates` y
`productSearchText`). Las guardas de privacidad y anti-invento aprobadas siguen
todas vigentes.
