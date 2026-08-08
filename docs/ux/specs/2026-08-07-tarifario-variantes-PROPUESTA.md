# El Tarifario recuerda por HABITACIÓN y VARIANTE — PROPUESTA (HISTÓRICO)

> ⛔ **SUPERSEDED el mismo día (2026-08-07).** Gastón respondió V1..V11 y además **pivoteó la
> visión** ("quiero que el tarifario sea una especie de IA"). Lo que vale y se construye es
> **`docs/ux/specs/2026-08-07-tarifario-inteligente-FIRMADA.md`**. Este archivo queda solo como
> registro de las preguntas que se le hicieron.

> **Fecha:** 2026-08-07 · **Autor:** `ux-ui-disenador` · **Estado: PROPUESTA. NO se construye nada
> hasta que Gastón responda las preguntas de la §8.**
> Continúa (no reemplaza) a `docs/ux/specs/2026-08-06-rediseno-tarifario-y-cobranza-FIRMADA.md`.
> Todo lo firmado el 2026-08-06 (P1..P19) sigue valiendo salvo lo que las respuestas de esta tanda
> cambien explícitamente.
>
> **Reglas de la constitución que aplica esta propuesta:** P-3 (nunca sumar monedas) · P-5 (se
> resuelve en línea, sin ventanas flotantes) · P-15 (sin cartelitos ni "(opcional)") · P-16 (nada se
> dice dos veces) · P-17 (voz del producto, cero jerga) · P-21 (la sugerencia nunca pisa lo tipeado)
> · F-14 (costos solo con permiso) · T-13 (todo lo derivado lo calcula el motor) · T-14 (fechas
> `dd/MM/aaaa`).

---

## 1. Qué pasó y por qué hay que tocar esto

Gastón miró el Tarifario nuevo en producción y dijo que la información **"se presenta rara"**. Tiene
razón, y el diagnóstico está verificado contra el código:

1. **La memoria guarda UN solo "último precio" por hotel + operador.** Si hoy vendés una doble a
   US$ 48 y mañana una triple a US$ 70, la triple **pisa** a la doble: el precio de la doble
   desaparece para siempre.
2. **La venta SÍ sabe de qué habitación se trata** (Régimen y Tipo de habitación son obligatorios y
   están a la vista en la ficha de hotel desde el 2026-06-06, Ronda 7), pero **la memoria los tira a
   la basura**.
3. Resultado en pantalla: dos renglones de precio de un mismo hotel pueden ser de **habitaciones
   distintas** y se leen como si fueran comparables. **Un precio sin decir de qué habitación es, no
   sirve para cotizar** — es el mismo problema que Gastón ya había resuelto para el operador (P6=A:
   "el precio sin el operador no sirve para decidir").
4. **Doble linaje visible:** los productos que se cargaron con el formulario viejo llevan la
   habitación metida **dentro del nombre** ("Sheraton - Doble Superior"), así que aparecen como
   productos **separados** del "Sheraton" limpio que crean las ventas. Eso es exactamente el
   repetido que Gastón mandó evitar **a toda costa** (P7, 2026-08-06). Y el sufijo lo generó
   **nuestro propio formulario**, no el usuario.
5. Lo mismo, más chico, en **aéreo** (cabina) y **traslado** (tipo de vehículo).
6. Gastón marcó además dos molestias de la pantalla: **la mezcla de tipos de servicio en una sola
   lista** y que **faltan datos del servicio** (régimen, categoría, cabina).

**Su operación real, contada por él hoy:** *"depende del operador, del cliente y de la
disponibilidad"*. O sea: **el precio no es del hotel, es de la combinación hotel + habitación +
régimen + operador**, y todas las combinaciones que vendió valen la pena recordarse.

**Lo que NO se reabre** (ya firmado, va como está): el Tarifario es la memoria de lo vendido (P1=B) ·
una sola lista sin decir el origen (P2=A) · se llama Tarifario (P3=A) · el renglón muestra lo
esencial (P5=A) · el precio se sugiere en amarillo y **nunca pisa lo tipeado** (P-21) · el renglón
gris de procedencia debajo del precio (P9=A) · precio de más de 60 días con la fecha en ámbar
(P10=A) · **evitar repetidos es prioridad absoluta** (P7) · **nada se borra** (2026-08-03) · los
precios **no se editan a mano** en la ficha del producto (2026-08-06).

---

## 2. La idea en una frase

**El producto sigue siendo uno solo (un hotel = un producto). Lo que se parte en pedacitos es la
MEMORIA: el sistema guarda el último precio de cada combinación que vendiste, y en la pantalla cada
precio dice de qué habitación es.**

```
   HOY (rompe)                              PROPUESTA
   ─────────────────────────────            ─────────────────────────────────────────────
   Maitei Posadas                           Maitei Posadas
     Ola Mayorista  US$ 70  03/07             Doble con desayuno
     ↑ ¿de qué habitación es este 70?           Ola Mayorista   US$ 48/noche   22/05/2026
       (era una triple, y borró la doble        Julia Tours     US$ 52/noche   03/07/2026
        de US$ 48)                            Triple con desayuno
                                                Ola Mayorista   US$ 70/noche   03/07/2026
```

Le decimos **"variante"** por adentro; **en pantalla nunca se escribe esa palabra**: se escribe
**"Doble con desayuno"**, "Triple media pensión", "Business", etc. (P-17).

---

## 3. Propuesta base para el Tarifario (sujeta a las respuestas de la §8)

> Dibujado con la recomendación marcada en cada pregunta. Si Gastón elige otra opción, cambia el
> dibujo, no el resto de la spec.

### 3.1 Solapas por tipo, para que no sea una ensalada (pregunta V8)

```
┌───────────────────────────────────────────────────────────────────────────────────────┐
│  Tarifario                                                        [ + Agregar producto ]
│  Los productos que ya vendiste, con el último precio de cada operador.                │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  [ Hoteles (38) ] [ Aéreos (12) ] [ Paquetes (9) ] [ Traslados (5) ] [ Asistencias (3) ]
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

- **Una solapa por tipo de servicio**, con el número al lado. La solapa en cero se ve apagada
  (criterio de solapas en cero, 2026-08-03 P3=B). Reemplaza al desplegable "Tipo" de hoy: la mezcla
  desaparece **y** cada tipo puede tener su propia columna (habitación en hotel, cabina en aéreo).
- **La columna "Tipo" se va** de la tabla: la solapa ya lo dice (P-16, nada se dice dos veces).
- **Las columnas cambian por solapa:**
  - Hoteles: HOTEL · HABITACIÓN · OPERADOR · PRECIO · CUÁNDO
  - Aéreos: RUTA · CABINA · OPERADOR · PRECIO · CUÁNDO
  - Paquetes / Traslados / Asistencias: PRODUCTO · OPERADOR · PRECIO · CUÁNDO (sin columna del medio)
- **Agrupado por habitación, y adentro los operadores** (pregunta V5): así se compara lo comparable
  — "esta doble, ¿quién me la da más barata?". El nombre del hotel y la ciudad se escriben una sola
  vez (P-16); la habitación, una sola vez por grupo.
- **Orden:** los grupos de habitación, por el precio más nuevo arriba; adentro, operadores por fecha,
  el más nuevo arriba (extiende P6=A).
- **Tope de renglones (pregunta V7):** se muestran hasta **3 renglones de precio** por producto y, si
  hay más, una línea gris **"+ N precios más — tocá el hotel para verlos"**. Así un hotel con 3
  operadores × 2 habitaciones (6 precios) no se come la pantalla.
- Sigue todo lo firmado: precio de más de 60 días con **la fecha en ámbar** (P10=A); **sin permiso de
  costos se muestra el precio de venta**, nunca el costo (F-14).

### 3.2 La ficha del producto: todas las variantes, y se pueden corregir (pregunta V15)

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
│  │                                                                                 │  │
│  │  Triple con desayuno                                           [ Corregir ]     │  │
│  │      Ola Mayorista     US$ 70 /noche    03/07/2026    F-2026-1120               │  │
│  │                                                                                 │  │
│  │  Doble Superior   (de una carga vieja)                         [ Corregir ]     │  │
│  │      Ola Mayorista     US$ 55 /noche    18/02/2026                              │  │
│  │                                                                                 │  │
│  │  [ Carga completa ]                          [ Cancelar ]   [ Guardar ]         │  │
│  └─────────────────────────────────────────────────────────────────────────────────┘  │
```

- Se abre **en línea, debajo del renglón** (P-5). Nunca ventana flotante.
- **Los precios siguen sin editarse a mano** (2026-08-06): lo que se corrige es **la etiqueta de la
  habitación**, no el número. `[ Corregir ]` abre en la misma línea dos desplegables (los mismos de
  la venta: Single/Doble/Triple/Cuádruple/Familiar + Solo Alojamiento/Desayuno/Media Pensión/Pensión
  Completa/All Inclusive) y un botón **Guardar**. Si al corregir queda igual que otra variante que ya
  existe, **las dos se juntan y queda el precio más nuevo** — con el mismo aviso de confirmación de
  los repetidos (P7).
- El **número de reserva es un enlace** a su ficha (ya firmado).
- `[ Carga completa ]` sigue siendo el único acceso al formulario largo (P1=B).

### 3.3 La sugerencia al vender, con la habitación adelante

**Caso 1 — hay precio de esa combinación exacta:** igual que lo firmado (P9=A), con la habitación
agregada al renglón gris.

```
│  Habitación * [ Doble ▾ ]   Régimen * [ Desayuno ▾ ]                                  │
│  Operador     [ Ola Mayorista            ▾ ]   ← precargado, amarillo                 │
│  Costo/noche  [ US$ 48,00                  ]   ← precargado, amarillo                 │
│               Último precio: Ola Mayorista · Doble con desayuno · US$ 48 · 22/05/2026 │
```

**Caso 2 — nunca vendió esa habitación, pero sí otra (pregunta V9):** el casillero **queda vacío**
(no se precarga un número que es de otra habitación) y el renglón gris dice de qué es, para que
sirva de referencia.

```
│  Habitación * [ Triple ▾ ]  Régimen * [ Desayuno ▾ ]                                  │
│  Costo/noche  [                            ]   ← vacío, no se precarga                │
│               De esta habitación no tenés precio. La doble con desayuno la vendiste a │
│               US$ 48 (Ola Mayorista, 22/05/2026).                                     │
```

**Caso 3 — no hay ningún precio de ese producto:** **no aparece nada**. Ya firmado (P9=A: el renglón
gris solo existe si hay precio aprendido; escribir "sin precio anterior" sería un cartelito, P-15).

**Si el vendedor cambia la habitación después de que se precargó (pregunta V10):** mientras **no
haya tocado el número**, la sugerencia se acomoda sola a la habitación nueva (o se vacía, si de esa
no hay precio). **Si ya escribió un número a mano, no se le toca nada** — P-21, la sugerencia nunca
pisa lo tipeado.

### 3.4 Repetidos: la red de seguridad (preguntas V11 a V14)

La solapa **"Repetidos (N)"** que ya está firmada (P7) suma el caso de los productos con la
habitación metida en el nombre:

```
│  [ Hoteles ] [ Aéreos ] [ Paquetes ] [ Traslados ] [ Asistencias ] │ [ Repetidos (4) ] │
├───────────────────────────────────────────────────────────────────────────────────────┤
│  Parecen el mismo hotel, con la habitación escrita adentro del nombre                 │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│   ( ) Sheraton Iguazú                        (•) Sheraton Iguazú                      │
│       Puerto Iguazú · 3 precios                  ← se queda este (nombre más limpio)   │
│   (•) Sheraton Iguazú - Doble Superior           Sheraton Iguazú - Doble Superior     │
│       Puerto Iguazú · 1 precio                   pasa a ser "Doble Superior"          │
│                                                                                       │
│       [ Es el mismo: unirlos ]     [ Son distintos ]                                  │
│  ───────────────────────────────────────────────────────────────────────────────────  │
│   ( ) Maitei Posadas · Posadas · 4 precios                                            │
│   (•) Maitei Posada · Posadas · 1 precio                                              │
│       [ Es el mismo: unirlos ]     [ Son distintos ]                                  │
```

- **El punto elige quién se queda** y viene marcado por defecto en el de nombre más limpio / más
  precios; se puede cambiar (pregunta V12).
- **Al unir, nada se borra** (2026-08-03): el que queda **absorbe los precios del otro**, y el precio
  que venía del nombre con sufijo entra como **variante "Doble Superior"** — no se pierde el dato
  (pregunta V14). El otro producto deja de listarse.
- **"Son distintos"** hace que ese par no vuelva a aparecer.
- **La migración une sola** los casos que puede (el sufijo `" - {habitación} {categoría}"` lo generó
  nuestro propio formulario, así que es reconocible). **Lo que no pudo unir solo cae acá**, con la
  frase de arriba explicando por qué están juntos.
- **Esto es la red de seguridad, no la herramienta principal** (P7): el freno al crear sigue siendo
  la prioridad.

---

## 4. Estados de pantalla

| Estado | Qué se ve |
|---|---|
| Cargando | Renglones grises, sin cartel (como hoy). |
| Solapa vacía | "Todavía no vendiste ningún hotel." (mismo texto por tipo). La solapa en cero se ve apagada. |
| Buscador sin resultados | "No encontramos '{texto}' en tu tarifario." (firmado 2026-06-06). |
| Producto sin ningún precio | El renglón muestra el producto y, en el lugar del precio, "Sin precios cargados" en gris (como hoy). |
| Repetidos en cero | Solapa apagada con "0"; adentro, "No hay productos repetidos para revisar." |
| Uniendo | El par queda con los botones apagados y "Uniendo…" hasta que responde el motor. |
| Unido OK | El par desaparece de la lista + "Listo, quedó uno solo." |
| Error al unir | El par **queda como estaba** + cartel rojo con "Probar de nuevo" (nunca se pierde el trabajo, Ronda 2 2026-06-06). |
| Corrigiendo una variante | La línea se vuelve dos desplegables + Guardar/Cancelar, en la misma línea (patrón "Confirmar costo", Ronda 5 2026-06-06). |
| Sin permiso de costos | Todo igual, con **precio de venta** en la columna de precio (F-14). |

---

## 5. Qué NO hay que hacer

- ❌ Crear **un producto por habitación** ("Sheraton Doble", "Sheraton Triple"): eso es fabricar
  repetidos, justo lo que P7 prohíbe. Un hotel = un producto.
- ❌ Mostrar un precio **sin decir de qué habitación es**.
- ❌ Precargar en el casillero de costo un precio **de otra habitación** (queda a criterio de V9,
  pero la propuesta dice que no).
- ❌ Escribir en pantalla las palabras **"variante"**, "clave", "combinación" (P-17).
- ❌ Ventanas flotantes para corregir una variante o unir repetidos (P-5).
- ❌ Botones de **borrar** producto o precio (nada se borra, 2026-08-03).
- ❌ Sumar monedas en ninguna columna ni total (P-3).
- ❌ Cartelitos explicativos al lado de los campos (P-15).

---

## 6. Dependencias del motor (para el brief del backend)

> Ninguna es decisión de UX. Continúan la numeración de la spec del 2026-08-06 (que llegaba a M-11).

| # | Qué hace falta | Para qué |
|---|---|---|
| M-12 | **La memoria de precios pasa a ser por (producto, operador, variante)**, en vez de por (producto, operador). Variante = habitación + régimen (hotel) / cabina (aéreo) / vehículo (traslado) / nada (paquete, asistencia), según lo que responda V2. Se guarda además la **etiqueta ya armada en criollo** ("Doble con desayuno") para que el front no arme textos (T-13). | §2, §3.1 |
| M-13 | **La venta deja de tirar la habitación y el régimen**: al guardar un servicio de hotel, la memoria se actualiza en la fila de ESA combinación, sin pisar las demás. | §2 |
| M-14 | **Listado de productos aprendidos agrupado**: producto → variantes → operadores, con precio, moneda, unidad, fecha, número de reserva, marca de "precio viejo" y **el tope de renglones** que se muestra en la lista + el total ("+ N precios más"). Filtro por **tipo de servicio** (para las solapas) con **el conteo por tipo**. Enmascarado sin `cobranzas.see_cost` (F-14). | §3.1 |
| M-15 | **Sugerencia por variante**: dado producto + variante elegida, devolver (a) el precio de esa variante si existe, (b) si no existe, el precio de la variante **más parecida** con su etiqueta, marcado como "es de otra habitación", (c) nada si el producto no tiene precios. | §3.3 |
| M-16 | **Migración de los productos con la habitación en el nombre**: reconocer el patrón `" - {habitación} {categoría}"` que generó el formulario viejo, unificarlos con el producto limpio cuando el nombre base y la ciudad coinciden, y **convertir el sufijo en la variante** de esos precios. Con rastro (nada se borra) y **reversible**. Lo que no se puede unir solo, queda marcado como par sugerido para la solapa Repetidos. | §3.4 |
| M-17 | **Unir dos productos eligiendo cuál sobrevive**: el que queda absorbe los precios y las variantes del otro; si las dos tenían la misma variante, queda el precio más nuevo; el otro deja de listarse pero no se borra. Y **"son distintos"**, que no vuelve a proponer ese par. | §3.4 |
| M-18 | **Corregir la etiqueta de una variante** de un producto (cambiar habitación/régimen) y **fusionar dos variantes** cuando la corrección las deja iguales. Nunca cambia importes. | §3.2 |
| M-19 | Si sale V4=B: **campo nuevo opcional "Nombre de la habitación"** (texto libre: Superior, Vista al mar) en la ficha de hotel de la venta, que entra en la variante. | §8 V4 |

**Riesgo técnico a mirar antes de construir (lo evalúa `frontend-senior` / `backend-dotnet-senior`):**
la lista agrupada por producto → variante → operador puede volverse pesada si un hotel acumula
muchas combinaciones; por eso la propuesta corta en 3 renglones y manda el resto a la ficha.

---

## 7. Posibles contradicciones con lo ya firmado (las decide Gastón, no yo)

1. **P6=A (2026-08-06) dice "un renglón por operador debajo del producto".** La propuesta mete un
   nivel más en el medio (la habitación). No lo contradice en espíritu — el operador se sigue viendo
   en cada renglón, y el motivo de P6 era "el precio sin el operador no sirve" — pero **cambia la
   forma del renglón**. Lo decide **V5**.
2. **P5=A (2026-08-06) dice "el renglón muestra lo esencial"** y Gastón rechazó agregarle cosas.
   Agregar la habitación es agregar un dato. La propuesta lo compensa **sacando la columna "Tipo"**
   (que pasa a ser la solapa), así el renglón no crece. Lo decide **V6 + V8**.
3. **El filtro "Tipo" que ya está construido** desaparecería si entran las solapas. Es reemplazo, no
   duplicación (P-16). Lo decide **V8**.

---

## 8. PREGUNTAS PARA GASTON

> Son 16, agrupadas por tema y ordenadas de lo grande a lo chico. Se pueden responder así:
> "V1 A, V2 B, V5 otra cosa: …". Donde dice **⭐ RECOMENDADA** es lo que yo haría, pero mandás vos.
> **La pregunta madre es la V1: todo lo demás cuelga de esa.**

---

### Tema 1 — Qué tiene que recordar el sistema

Contexto: hoy el sistema guarda **un solo** último precio por hotel y operador. Si vendés una doble
y después una triple, la triple **borra** el precio de la doble, y la lista muestra un número sin
decir de qué habitación es.

**V1. Cuando vendés el mismo hotel con distintas habitaciones, ¿qué precio querés que recuerde?**

  **A) Uno por cada combinación: hotel + habitación + régimen + operador.** ⭐ RECOMENDADA
     (Es lo que dijiste hoy: "depende del operador, del cliente y de la disponibilidad".)
```
     Maitei Posadas
       Doble con desayuno     Ola Mayorista   US$ 48/noche   22/05/2026
       Doble con desayuno     Julia Tours     US$ 52/noche   03/07/2026
       Triple con desayuno    Ola Mayorista   US$ 70/noche   03/07/2026
```

  **B) Uno solo por hotel y operador (como hoy): siempre el último que vendiste.**
```
     Maitei Posadas
       Ola Mayorista    US$ 70/noche   03/07/2026     ← era una triple; borró la doble
       Julia Tours      US$ 52/noche   03/07/2026
```

  **C) Uno por habitación, sin mirar el régimen** (la doble con desayuno y la doble con media
     pensión comparten el mismo precio recordado).
```
     Maitei Posadas
       Doble      Ola Mayorista   US$ 55/noche   03/07/2026    ← se mezclan desayuno y M.P.
       Triple     Ola Mayorista   US$ 70/noche   03/07/2026
```

---

**V2. ¿En qué tipos de servicio querés que el precio se recuerde separado?**

  **A) Solo en Hotel** (por habitación + régimen). En aéreo, traslado, paquete y asistencia se sigue
     recordando un precio por operador, como hoy. ⭐ RECOMENDADA
     (Es donde te duele; y en traslado el "tipo de vehículo" hoy se escribe a mano, así que separar
     por eso fabricaría repetidos, justo lo que pediste evitar a toda costa.)
```
     Maitei Posadas    Doble con desayuno   Ola          US$ 48/noche
     BUE – MIA         (sin separar)        Aeromundo    US$ 780
```

  **B) Hotel (habitación + régimen) y Aéreo (cabina).**
```
     Maitei Posadas    Doble con desayuno   Ola          US$ 48/noche
     BUE – MIA         Economy              Aeromundo    US$ 780
     BUE – MIA         Business             Aeromundo    US$ 2.150
```

  **C) Hotel, Aéreo y también Traslado (por tipo de vehículo).**
```
     AEP → Centro      Auto                 Traslados SA   $ 28.000
     AEP → Centro      Combi                Traslados SA   $ 45.000
```

---

**V3. Cuando el dato no está cargado (por ejemplo, un aéreo sin cabina elegida — hoy es opcional),
¿cómo se muestra ese precio?**

  **A) Sin nada al lado: el precio va solo, como hoy.** ⭐ RECOMENDADA
```
     BUE – MIA         Business             Aeromundo    US$ 2.150
     BUE – MIA                              Aeromundo    US$ 780
```

  **B) Con la palabra "Sin especificar".**
```
     BUE – MIA         Business             Aeromundo    US$ 2.150
     BUE – MIA         Sin especificar      Aeromundo    US$ 780
```

---

**V4. Hoy la venta te deja elegir Single/Doble/Triple/Cuádruple/Familiar, pero NO te deja escribir
"Doble Superior" ni "Vista al mar". Los productos viejos sí tienen eso metido en el nombre.
¿Sumamos un casillero para eso?**

  **A) No: alcanza con Single/Doble/Triple/… y el régimen.** Lo que ya está escrito en los productos
     viejos se respeta como está, pero de acá en adelante no se escribe nada a mano.
     ⭐ RECOMENDADA (todo casillero de texto libre termina siendo "Doble Sup", "doble superior",
     "DBL SUP": tres repetidos del mismo).
```
     Habitación * [ Doble ▾ ]     Régimen * [ Desayuno ▾ ]
```

  **B) Sí: un casillero más, para escribir el nombre de la habitación cuando hace falta.**
```
     Habitación * [ Doble ▾ ]     Régimen * [ Desayuno ▾ ]
     Nombre de la habitación  [ Superior                    ]
```

  **C) Sí, pero eligiendo de una lista que el sistema va aprendiendo** (la primera vez la escribís,
     después te la ofrece).
```
     Habitación * [ Doble ▾ ]  Régimen * [ Desayuno ▾ ]  Categoría [ Superior ▾ ]
                                                          ↑ Estándar · Superior · Vista al mar
```

---

### Tema 2 — Cómo se ve la lista del Tarifario

Contexto: un hotel puede tener 3 operadores y 2 habitaciones = 6 precios. Sin un orden claro, eso es
una sábana.

**V5. ¿Cómo se ordenan los precios de un mismo hotel?**

  **A) Por habitación, y adentro los operadores.** ⭐ RECOMENDADA
     (Vos ya sabés qué habitación necesita el cliente; lo que querés comparar es quién te la da más
     barata.)
```
     Maitei Posadas
     Posadas, Misiones
        Doble con desayuno
            Ola Mayorista    US$ 48/noche   22/05/2026
            Julia Tours      US$ 52/noche   03/07/2026
        Triple con desayuno
            Ola Mayorista    US$ 70/noche   03/07/2026
```

  **B) Por operador, y adentro las habitaciones** (es lo que más se parece a lo que firmaste el
     2026-08-06: un renglón por operador).
```
     Maitei Posadas
     Posadas, Misiones
        Ola Mayorista
            Doble con desayuno    US$ 48/noche   22/05/2026
            Triple con desayuno   US$ 70/noche   03/07/2026
        Julia Tours
            Doble con desayuno    US$ 52/noche   03/07/2026
```

  **C) Todo plano, un renglón por precio, del más nuevo al más viejo.**
```
     Maitei Posadas      Triple con desayuno   Ola Mayorista   US$ 70/noche   03/07/2026
     Posadas, Misiones   Doble con desayuno    Julia Tours     US$ 52/noche   03/07/2026
                         Doble con desayuno    Ola Mayorista   US$ 48/noche   22/05/2026
```

---

**V6. ¿Cómo se escribe la habitación en el renglón?**

  **A) Como una columna propia, con la habitación y el régimen juntos en criollo.** ⭐ RECOMENDADA
```
     HOTEL              HABITACIÓN            OPERADOR       PRECIO         CUÁNDO
     Maitei Posadas     Doble con desayuno    Ola Mayorista  US$ 48/noche   22/05/2026
```

  **B) Dos columnas separadas, habitación y régimen.**
```
     HOTEL            HABITACIÓN   RÉGIMEN     OPERADOR       PRECIO        CUÁNDO
     Maitei Posadas   Doble        Desayuno    Ola Mayorista  US$ 48/noche  22/05/2026
```

  **C) Pegado al precio, en chiquito debajo.**
```
     HOTEL              OPERADOR        PRECIO                    CUÁNDO
     Maitei Posadas     Ola Mayorista   US$ 48/noche              22/05/2026
                                        Doble con desayuno
```

---

**V7. Un hotel con 6 precios, ¿se muestra entero en la lista o se corta?**

  **A) Se muestran los 3 más nuevos y una línea gris "+3 precios más — tocá el hotel para verlos".**
     ⭐ RECOMENDADA (la lista queda pareja y nada se pierde: adentro están todos).
```
     Maitei Posadas     Doble con desayuno    Ola Mayorista   US$ 48/noche   22/05/2026
     Posadas, Misiones                        Julia Tours     US$ 52/noche   03/07/2026
                        Triple con desayuno   Ola Mayorista   US$ 70/noche   03/07/2026
                        + 3 precios más — tocá el hotel para verlos
```

  **B) Se muestran todos siempre.**
```
     Maitei Posadas     Doble con desayuno    Ola Mayorista   US$ 48/noche   22/05/2026
     Posadas, Misiones                        Julia Tours     US$ 52/noche   03/07/2026
                                              Sur Turismo     US$ 50/noche   19/06/2026
                        Triple con desayuno   Ola Mayorista   US$ 70/noche   03/07/2026
                                              Julia Tours     US$ 74/noche   12/07/2026
                                              Sur Turismo     US$ 69/noche   01/08/2026
```

  **C) Se muestra solo el más nuevo de cada hotel y el resto se ve al abrirlo.**
```
     Maitei Posadas     Triple con desayuno   Ola Mayorista   US$ 70/noche   03/07/2026
     Posadas, Misiones  + 5 precios más — tocá el hotel para verlos
```

---

**V8. Dijiste que te molesta ver hoteles, aéreos y paquetes mezclados en la misma lista. ¿Cómo lo
separamos?**

  **A) Solapas arriba, una por tipo** (y se va el desplegable "Tipo" de hoy). ⭐ RECOMENDADA
     (Además deja poner la columna que sirve en cada tipo: habitación en hoteles, cabina en aéreos.)
```
     [ Hoteles (38) ] [ Aéreos (12) ] [ Paquetes (9) ] [ Traslados (5) ] [ Asistencias (3) ]
     ─────────────────────────────────────────────────────────────────────────────────────
     HOTEL              HABITACIÓN           OPERADOR        PRECIO         CUÁNDO
```

  **B) Todo en una lista, pero cortada en secciones con un título** (se ve todo de un scroll).
```
     HOTELES
       Maitei Posadas       Doble con desayuno    Ola          US$ 48/noche   22/05/2026
       Howard Johnson       Doble media pensión   Julia        US$ 61/noche   11/06/2026
     AÉREOS
       Buenos Aires – Miami                       Aeromundo    US$ 780        14/06/2026
```

  **C) Como está hoy, pero con el filtro de tipo más grande y a la vista** (botones en vez de
     desplegable).
```
     Mostrar:  ( Todos ) ( Hoteles ) ( Aéreos ) ( Paquetes ) ( Traslados ) ( Asistencias )
     PRODUCTO           TIPO     HABITACIÓN           OPERADOR      PRECIO       CUÁNDO
```

---

### Tema 3 — El precio que te sugiere cuando estás vendiendo

Contexto: al cargar un hotel en una reserva, el sistema te precarga el último precio en amarillo. Si
ahora recuerda por habitación, hay que decidir qué pasa cuando de esa habitación no hay precio.

**V9. Elegís "Triple" y de triple nunca vendiste, pero sí tenés precio de la doble. ¿Qué hace?**

  **A) Deja el casillero vacío y te muestra abajo, en gris, el precio de la doble como referencia.**
     ⭐ RECOMENDADA (un número de otra habitación metido en el casillero se te cuela a la cotización
     sin que lo mires).
```
     Habitación * [ Triple ▾ ]   Régimen * [ Desayuno ▾ ]
     Costo/noche  [                       ]
                  De esta habitación no tenés precio. La doble con desayuno la vendiste
                  a US$ 48 (Ola Mayorista, 22/05/2026).
```

  **B) Precarga igual el precio de la doble, en amarillo, aclarando de dónde salió.**
```
     Habitación * [ Triple ▾ ]   Régimen * [ Desayuno ▾ ]
     Costo/noche  [ US$ 48,00             ]  ← amarillo
                  Ojo: es el precio de la DOBLE con desayuno (Ola Mayorista, 22/05/2026).
```

  **C) No muestra nada: si de esa habitación no hay precio, la pantalla queda limpia.**
```
     Habitación * [ Triple ▾ ]   Régimen * [ Desayuno ▾ ]
     Costo/noche  [                       ]
```

---

**V10. Ya te precargó el precio de la doble y cambiás la habitación a triple. ¿Qué pasa con el
número que estaba puesto?**

  **A) Se acomoda solo a la habitación nueva, siempre y cuando vos no lo hayas tocado. Si lo
     escribiste a mano, no se toca nunca.** ⭐ RECOMENDADA (es la regla que ya vale en todo el
     sistema: la sugerencia jamás pisa lo que vos escribiste).
```
     Doble  →  Costo/noche [ US$ 48,00 ]  ← lo puso el sistema
     Triple →  Costo/noche [ US$ 70,00 ]  ← se acomodó solo

     Doble  →  Costo/noche [ US$ 45,00 ]  ← lo escribiste vos
     Triple →  Costo/noche [ US$ 45,00 ]  ← queda tu número, no se toca
```

  **B) No se toca nunca: lo que se precargó primero queda hasta que vos lo cambies.**
```
     Doble  →  Costo/noche [ US$ 48,00 ]
     Triple →  Costo/noche [ US$ 48,00 ]  ← sigue el de la doble
```

  **C) Se vacía y esperás a escribirlo vos.**
```
     Doble  →  Costo/noche [ US$ 48,00 ]
     Triple →  Costo/noche [           ]
```

---

### Tema 4 — Los repetidos y el botón de unirlos

Contexto: los productos cargados con el formulario viejo tienen la habitación metida en el nombre
("Sheraton Iguazú - Doble Superior"), así que conviven con el "Sheraton Iguazú" limpio que crean las
ventas. La migración va a unir sola las que pueda; el resto hay que resolverlas a mano.

**V11. ¿Cómo querés ver los repetidos para decidir?**

  **A) De a pares, uno al lado del otro, con sus precios a la vista.** ⭐ RECOMENDADA
```
     Sheraton Iguazú                   Sheraton Iguazú - Doble Superior
     Puerto Iguazú · 3 precios         Puerto Iguazú · 1 precio
     Ola · US$ 120 · 22/05/2026        Ola · US$ 155 · 18/02/2026
                      [ Es el mismo: unirlos ]   [ Son distintos ]
```

  **B) Agrupados: un producto arriba y abajo todos los que se le parecen.**
```
     Sheraton Iguazú · Puerto Iguazú · 3 precios
        se le parecen:
        · Sheraton Iguazú - Doble Superior   1 precio    [ Unir ]  [ Es otro ]
        · Sheraton Iguazu                    2 precios   [ Unir ]  [ Es otro ]
```

  **C) Una lista simple con el aviso, y el detalle se abre al tocar.**
```
     ⚠ Sheraton Iguazú tiene 2 posibles repetidos            [ Revisar ]
     ⚠ Maitei Posadas tiene 1 posible repetido               [ Revisar ]
```

---

**V12. Al unir dos, ¿quién se queda con el nombre?**

  **A) Elegís vos con un puntito, y viene marcado el que tiene el nombre más limpio.**
     ⭐ RECOMENDADA
```
     (•) Sheraton Iguazú                     ← se queda este
     ( ) Sheraton Iguazú - Doble Superior
              [ Es el mismo: unirlos ]
```

  **B) El sistema decide solo: se queda el que tiene más precios.**
```
     Sheraton Iguazú (3 precios) se queda con los precios de
     Sheraton Iguazú - Doble Superior (1 precio).
              [ Unirlos ]
```

  **C) Elegís vos y además podés escribir el nombre final.**
```
     (•) Sheraton Iguazú     ( ) Sheraton Iguazú - Doble Superior
     Nombre final [ Sheraton Iguazú                   ]
              [ Es el mismo: unirlos ]
```

---

**V13. Los productos con la habitación en el nombre que la máquina NO pudo unir sola, ¿dónde
aparecen?**

  **A) En la misma solapa "Repetidos", con un título que explica por qué están ahí.**
     ⭐ RECOMENDADA (un solo lugar para revisar; no inventamos otra pantalla).
```
     [ Repetidos (4) ]
     Parecen el mismo hotel, con la habitación escrita adentro del nombre
       Sheraton Iguazú   /   Sheraton Iguazú - Doble Superior      [ Unirlos ] [ Son distintos ]
     Se escriben parecido
       Maitei Posadas    /   Maitei Posada                         [ Unirlos ] [ Son distintos ]
```

  **B) En una solapa aparte, solo para los del formulario viejo.**
```
     [ Repetidos (2) ]  [ Cargas viejas por revisar (2) ]
```

  **C) En ningún lado especial: quedan como están y los vas uniendo cuando los cruzás.**
```
     Sheraton Iguazú                      Ola   US$ 120/noche   22/05/2026
     Sheraton Iguazú - Doble Superior     Ola   US$ 155/noche   18/02/2026
```

---

**V14. Cuando unís "Sheraton Iguazú - Doble Superior" con "Sheraton Iguazú", ¿qué pasa con el
"Doble Superior"?**

  **A) Ese precio queda guardado como la habitación "Doble Superior" del Sheraton.** ⭐ RECOMENDADA
     (no se pierde nada de lo que ya sabías).
```
     Sheraton Iguazú
        Doble con desayuno    Ola   US$ 120/noche   22/05/2026
        Doble Superior        Ola   US$ 155/noche   18/02/2026
```

  **B) Ese precio entra como un precio más del Sheraton, sin decir de qué habitación era.**
```
     Sheraton Iguazú
        Ola   US$ 155/noche   18/02/2026
        Ola   US$ 120/noche   22/05/2026
```

  **C) Al unir te pregunta a qué habitación corresponde y lo elegís vos.**
```
     Ese precio, ¿de qué habitación era?
     Habitación [ Doble ▾ ]   Régimen [ Desayuno ▾ ]        [ Unir ]
```

---

### Tema 5 — La ficha del producto (lo que se abre al tocar un renglón)

**V15. Adentro de la ficha, ¿qué querés poder hacer con las habitaciones aprendidas?**

  **A) Verlas agrupadas y poder CORREGIR una que quedó mal escrita** (los importes no se tocan
     nunca: son la memoria de lo que pasó). ⭐ RECOMENDADA
```
     Precios que aprendió de tus ventas
       Doble con desayuno                                   [ Corregir ]
           Ola Mayorista   US$ 48 /noche   22/05/2026   F-2026-1042
       Triple con desayuno                                  [ Corregir ]
           Ola Mayorista   US$ 70 /noche   03/07/2026   F-2026-1120
```

  **B) Solo verlas. Si una quedó mal, se arregla vendiendo de nuevo.**
```
     Precios que aprendió de tus ventas
       Doble con desayuno     Ola Mayorista   US$ 48 /noche   22/05/2026
       Triple con desayuno    Ola Mayorista   US$ 70 /noche   03/07/2026
```

  **C) Verlas y poder juntar dos que son la misma** (sin corregir textos).
```
     Precios que aprendió de tus ventas
       Doble con desayuno     Ola   US$ 48   22/05/2026       [ Juntar con… ]
       Doble Superior         Ola   US$ 55   18/02/2026       [ Juntar con… ]
```

---

### Tema 6 — Cargar un producto a mano

Contexto: el alta a mano es la fichita corta que firmaste (tipo · nombre · ciudad · operador ·
precio). Hoy no pregunta habitación ni régimen.

**V16. Cuando cargás un hotel a mano y ponés un precio, ¿te pregunta de qué habitación es?**

  **A) Sí, con los mismos desplegables de la venta y con Doble / Desayuno ya puestos.**
     ⭐ RECOMENDADA (si no, ese precio queda "sin habitación" y no se puede comparar con los que
     aprendió de las ventas).
```
     Tipo [ Hotel ▾ ]   Nombre * [ Maitei Posadas          ]
     Ciudad * [ Posadas          ]   Operador [ Ola Mayorista ▾ ]
     Habitación [ Doble ▾ ]  Régimen [ Desayuno ▾ ]
     Precio [ US$ ▾ ] [        ] por noche
```

  **B) No: queda como está hoy, el precio se guarda sin habitación.**
```
     Tipo [ Hotel ▾ ]   Nombre * [ Maitei Posadas          ]
     Ciudad * [ Posadas          ]   Operador [ Ola Mayorista ▾ ]
     Precio [ US$ ▾ ] [        ] por noche
```

  **C) Ni siquiera pide precio: das de alta el hotel pelado y el precio lo aprende de la primera
     venta.**
```
     Tipo [ Hotel ▾ ]   Nombre * [ Maitei Posadas          ]
     Ciudad * [ Posadas          ]
```

---

## 9. Qué pasa después de que respondas

1. Cada respuesta se escribe como regla, con fecha, en `docs/ux/guia-ux-gaston.md`.
2. Esta propuesta se convierte en `...-tarifario-variantes-FIRMADA.md` con el dibujo final, y es lo
   que `frontend-senior` implementa al pie de la letra.
3. Si alguna respuesta choca con algo de la §7, se te avisa **antes** de construir y decidís cuál
   vale.
