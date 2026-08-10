# El buscador de la ficha de servicio encuentra TODOS los tipos y la solapa salta sola

**Fecha:** 2026-08-10 · **Estado:** FIRMADA (P1=A, P2=A, P3=A + 2 firmas nuevas del mismo día) · **Alcance:** micro-cambio
**Pantalla:** ficha de carga de servicio de una reserva (`ServiceInlineCard` + `ProductSearchField`)
**Reglas aplicadas:** P-1 (nunca jerga), P-15 (nada de cartelitos), P-16 (un dato no se dice dos veces),
P-17 (voz de los avisos), P-21 (el sistema sugiere, no decide), T-11 (sin llaves), PR-9 (en criollo)

---

## Qué pidió Gastón (textual)

> *"que te deje buscar todo más allá de si fue un hotel u otro tipo de servicio y en el caso de
> seleccionarlo cambie automáticamente la solapa"*

Restricción dura del mismo pedido: **misma fachada**. Cero cajas nuevas, cero filtros a la vista, cero
menciones de IA (regla del 2026-08-09).

Traducido: el buscador de producto de la ficha deja de estar encerrado en la solapa donde estás. Si
estás en **Hotel** y escribís el nombre de un aéreo, el aéreo aparece igual en la lista; al elegirlo,
la ficha se para sola en la solapa **Aéreo** con ese producto ya cargado (mismo amarillo editable de
siempre).

---

## Lo que NO cambia (para que nadie invente)

- El casillero del buscador: mismo tamaño, mismo lugar, mismo texto de ayuda, mismo "Buscando…" sutil
  (Ronda 2, 2026-06-06).
- Las 5 solapas y sus formularios: idénticos.
- La chapita verde **"En tu tarifario"** sigue en todas las filas (todas salen del tarifario).
- **"No es ninguno: crear …" sigue SIEMPRE última** y sigue creando en la solapa donde estás
  (P7, 2026-08-06: los parecidos siempre antes de dejar crear).
- El resaltado azul del primer resultado con parecido fuerte y el teclado (↑↓ / Enter / Esc): igual.
- Sin resultados → el mensaje de siempre + crear (Ronda 2, 2026-06-06).
- Ninguna palabra nueva sobre de dónde salió un resultado: **jamás "sugerido", "automático", "IA"**
  (derogación de "creado en venta", 2026-06-08 · degradación silenciosa, 2026-08-07 · P-17).

---

## Decisiones (cubiertas por la guía)

**D1 — Las filas de la solapa donde estás NO llevan ninguna marca de tipo.**
La solapa ya dice que estás en Hotel; repetirlo en cada renglón es decir lo mismo dos veces.
*Guía: "El Tarifario inteligente" (2026-08-07, V8=A) — "muere la columna Tipo (P-16: la solapa ya lo
dice)". Constitución P-16.*

**D2 — Si se marca el tipo (ver P1), se escribe con la palabra del negocio y nada más:**
"Aéreo", "Hotel", "Traslado", "Paquete", "Asistencia". Nunca un valor interno, nunca una sigla, nunca
una explicación al lado.
*Guía: V6=A "en criollo y en una sola frase". Constitución P-1 + PR-9.*

**D3 — El salto de solapa es SILENCIOSO e INMEDIATO: no hay ventana, ni "¿seguro?", ni cartelito.**
Al elegir la fila, la ficha ya está parada en la solapa correcta con el producto cargado en amarillo.
Lo que habla es el amarillo y la solapa pintada, no un texto.
*Guía: "El amarillo es el que habla: no se escribe ningún cartelito 'Entendí esto'" y "el sistema
organiza solo… solo pregunta cuando la duda es GRANDE" (2026-08-07); "Basta de formularios
aclarativos" (2026-06-05, P-15); "Cartel emergente único" (2026-07-22) excluye a propósito las fichas
de trabajo — el vendedor está TRABAJANDO ahí y una ventana lo interrumpiría.*

**D4 — La precarga del formulario destino es sugerencia, no decisión: amarillo, editable, y el
servicio se crea recién cuando el vendedor toca Guardar.** Si se equivocó de fila, cambia de solapa a
mano como hoy y no perdió nada.
*Guía: "Nada se confirma solo" (2026-08-07) + patrón de sugerencias del 2026-06-05. Constitución P-21.*

**D5 — Nada de lo tipeado se pierde al saltar.** Cada solapa ya guarda lo suyo por separado: al volver
a la solapa anterior está todo como lo dejaste.
*Consecuencia obligada del comportamiento actual de la ficha, no es preferencia nueva.*

**D6 — Editando un servicio ya cargado, el buscador sigue limitado a su tipo.** Al editar no se puede
cambiar el tipo de un servicio (la ficha es para ESE servicio y las solapas están apagadas): ofrecer
resultados de otro tipo sería ofrecer un salto imposible.
*Guía: Ronda 1 (2026-06-06) "editar → desde la MISMA ficha, una sola forma para crear y editar".*

**D7 — Después del salto el cursor queda en el buscador del formulario destino**, que ya muestra el
nombre elegido — exactamente lo que pasa hoy al elegir un producto del mismo tipo.
*Pedido textual de Gastón: "misma mecánica de precarga que ya existe".*

---

## El dibujo, caso simple (buscar como siempre)

Estás en la solapa **Hotel** y escribís "sheraton". El tercer resultado es un traslado:

```
 [ Hotel ] [ Aéreo ] [ Traslado ] [ Paquete ] [ Asistencia ]
   ▔▔▔▔▔
 Producto
┌──────────────────────────────────────────────────────────────┐
│ 🔍 sheraton                                                  │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ Sheraton Iguazú                            [En tu tarifario] │
│ Puerto Iguazú                                                │
│ Ola Mayorista · US$ 48/noche · 22/05/2026                    │
├──────────────────────────────────────────────────────────────┤
│ Sheraton Buenos Aires                      [En tu tarifario] │
│ CABA                                                         │
│ Julia Tours · US$ 62/noche · 03/06/2026                      │
├──────────────────────────────────────────────────────────────┤
│ Sheraton Iguazú – Aeropuerto    [Traslado] [En tu tarifario] │   ← otro tipo
│ Puerto Iguazú                                                │
│ Ola Mayorista · $ 18.000 · 12/04/2026                        │
├──────────────────────────────────────────────────────────────┤
│ ＋ No es ninguno: crear "sheraton" como hotel nuevo          │
│    Revisá los de arriba antes — si ya existe, elegirlo…      │
└──────────────────────────────────────────────────────────────┘
```

Tocás esa tercera fila y, sin ningún aviso, queda así:

```
 [ Hotel ] [ Aéreo ] [ Traslado ] [ Paquete ] [ Asistencia ]
                       ▔▔▔▔▔▔▔▔
 Producto
┌──────────────────────────────────────────────────────────────┐
│ 🔍 Sheraton Iguazú – Aeropuerto                              │
└──────────────────────────────────────────────────────────────┘
 Operador            Fecha              Pasajeros   Costo
┌──────────────┐ ┌──────────────┐ ┌──────────┐ ┌──────────────┐
│ Ola Mayorista│ │              │ │          │ │ $ 18.000     │  ← amarillo (sugerido, editable)
└──────────────┘ └──────────────┘ └──────────┘ └──────────────┘
```

Lo que escribiste en Hotel te espera intacto si volvés a esa solapa.

---

## Respuestas de Gastón (2026-08-10) — las tres cerradas

**D8 — P1 = A · La fila de otro tipo lleva una chapita GRIS con la palabra del tipo, a la izquierda de
la verde "En tu tarifario".** Misma forma y tamaño que la verde (es la única chapita que ya existe en
esa lista): no entra ningún elemento nuevo a la pantalla. Las filas del tipo de la solapa activa no
llevan nada (D1).

**D9 — P2 = A · Primero los del tipo de la solapa activa; abajo, los de otros tipos.** Si no hay
ninguno del tipo activo, los otros quedan arriba porque son los únicos. Motivo firmado: que un Enter
rápido no te salte de solapa sin querer. El resaltado azul del primero y "crear nuevo" al final no
cambian.

**D10 — P3 = A · Al saltar no se copia nada de lo que habías tipeado en la solapa anterior.** Cada
solapa guarda lo suyo intacto (D5). ⚠️ Esto NO choca con D13: los datos que viajan al formulario
destino son los que venían **en la frase que se escribió en el buscador**, no los que el vendedor
había cargado a mano en la otra solapa.

---

## La pregunta cortita con ✨ (firmada 2026-08-10)

> **Origen:** firma de Gastón del mismo día, opción **"Preguntas + simbolito"**. Ajusta la regla de
> invisibilidad total del 2026-08-09: el sistema puede preguntar, pero **solo cuando la duda es de
> verdad** y **en una línea**. Es la misma familia que la "duda grande" ya firmada el 2026-08-07.

**D11 — Cuándo aparece:** solo cuando hay una duda **concreta y grande** que cambia qué producto es
("¿El Panamericano de Buenos Aires o el de Bariloche?"). Si no hay duda, **no aparece absolutamente
nada**. Nunca dos preguntas a la vez. No son duda: la forma de escribir, mayúsculas, acentos, el orden
de los datos ni el redondeo — eso lo resuelve el sistema solo.
*Guía: "solo pregunta cuando la duda es GRANDE, y pregunta en UNA LÍNEA… nunca dos dudas a la vez"
(2026-08-07).*

**D12 — Cómo se ve y dónde va:** un **renglón gris de una línea, arriba de todo dentro del desplegable**
(pegado abajo del casillero de búsqueda, que es el campo del que habla), con **✨ adelante** y la
pregunta en palabras del negocio. Sin recuadro, sin color de alerta, sin botón, sin título.

- **Texto:** una sola pregunta corta. **Jamás** las palabras "IA", "modelo", "inteligente",
  "sugerencia", "no estoy seguro", ni un código de error (P-1, P-17, 2026-08-07).
- **No es clickeable ni navegable con las flechas** (v1): es un aviso, no una opción. Se contesta
  **eligiendo la fila de abajo** — que es exactamente la respuesta a la pregunta. No entra en el
  conteo de opciones del teclado ni se puede elegir con Enter.
- **Se va sola:** al seguir tipeando (la lista se rehace), al elegir una fila, al crear nuevo, con Esc
  o al cerrarse el desplegable. No queda pegada después de elegir, ni reaparece al volver al campo.
- **Nunca traba nada:** no frena el Guardar, no va al Cartel emergente — esa regla (2026-07-22) excluye
  a propósito las fichas de trabajo.
- **Para lectores de pantalla** se anuncia igual que el "Buscando…" que ya existe (aviso, no opción).
- **Si el motor no contesta o no está configurado, la línea simplemente no existe** y la pantalla es la
  de siempre, sin una palabra distinta (degradación firmada 2026-08-07).

**D12-bis — Lo firmado el 2026-08-07 para las dudas sobre un DATO sigue vivo tal cual:** cuando la duda
no es qué producto es, sino un dato precargado ("¿'48' es el precio por noche?", "¿'del 12 al 15/9' es
septiembre de 2026?"), va **debajo de ese campo, con Sí / No**, y el "No" vacía el campo y deja el
cursor ahí. La línea con ✨ del desplegable **no reemplaza** eso: son dos momentos distintos (elegir el
producto vs. revisar un dato ya cargado).

---

## Escribir la frase completa en el mismo buscador (firmada 2026-08-10)

> **Origen:** firma de Gastón del mismo día. Es un **atajo para el que lo conoce**: no se anuncia en
> ningún lado.

**D13 — El vendedor puede tirar la frase entera en el MISMO casillero de búsqueda** —
*"llao llao del 10/02 al 15/02 con Delfos"*— y al **elegir el hotel de la lista**, lo demás que venía en
la frase (**fechas y operador**) queda **precargado en amarillo, editable**.

> **Precio: NO en v1** (decisión 2026-08-10). Gastón pidió textual "hotel + operador y fecha"; el
> precio además se cruza con el permiso de ver costos. Si algún día lo pide, es una v2 con su firma.

**D13-bis (FIRMADO por Gastón, 2026-08-10 tarde) — la frase también ayuda al CREAR nuevo.** Si el
producto de la frase NO existe todavía y el vendedor termina en "Crear nuevo", el producto se crea con
el **nombre limpio** (como ya decía D13) **y las fechas + operador de la frase quedan precargados en
amarillo editable**, igual que si hubiera elegido uno existente. Nada se confirma solo: se guarda
recién con Guardar (P-21). Motivo: Gastón probó *"llao llao del 10/02 al 15/02 con delfos"* sin tener
el Llao Llao cargado y la frase se tiraba a la basura — exactamente el caso donde más ayuda crea.

- **La fachada no cambia:** mismo casillero, mismo tamaño, **mismo texto de ayuda de hoy**. Cero
  placeholder nuevo, cero leyenda, cero ejemplo, cero "probá escribiendo…". Nada insinúa que existe.
  *(P-15 + restricción del dueño "misma fachada".)*
- **El amarillo es el que habla:** no se escribe ningún cartelito tipo "entendí esto" ni "cargado
  solo". Lo que no entendió **queda vacío, sin explicación**.
  *Guía: 2026-08-07.*
- **Es sugerencia, no decisión:** todo editable, **nunca pisa** lo que el vendedor ya escribió a mano, y
  el servicio se crea **recién al tocar Guardar** (P-21 + "nada se confirma solo", 2026-08-07).
- **El nombre del producto que queda en el casillero es el nombre limpio del producto elegido**, nunca
  la frase entera. Si termina en "crear nuevo", se crea con el nombre limpio, no con la frase.
  *Guía: 2026-08-07, "el producto nuevo NO se crea solo… si se elige un parecido, el resto de la frase
  igual se aprovecha".*
- **Combina con el salto de solapa:** si lo elegido es de otro tipo, la ficha salta y **los datos de la
  frase viajan al formulario destino** (eso es la misma selección). Lo que NO viaja es lo que el
  vendedor había tipeado a mano en la solapa anterior (D10).
- **Sin motor:** la frase se comporta como un texto de búsqueda cualquiera. Ni un aviso, ni una palabra
  distinta (degradación firmada 2026-08-07).

---

## Cómo queda el dibujo con todo junto

Estás en **Hotel** y tirás la frase entera. Hay dos "Panamericano", así que aparece la línea con ✨:

```
 [ Hotel ] [ Aéreo ] [ Traslado ] [ Paquete ] [ Asistencia ]
   ▔▔▔▔▔
 Producto
┌──────────────────────────────────────────────────────────────┐
│ 🔍 panamericano del 10/02 al 15/02 con Delfos                │
└──────────────────────────────────────────────────────────────┘
┌──────────────────────────────────────────────────────────────┐
│ ✨ ¿El Panamericano de Buenos Aires o el de Bariloche?        │  ← gris, sin botón
├──────────────────────────────────────────────────────────────┤
│ Panamericano Buenos Aires                  [En tu tarifario] │
│ CABA                                                         │
│ Delfos · US$ 95/noche · 14/06/2026                           │
├──────────────────────────────────────────────────────────────┤
│ Panamericano Bariloche                     [En tu tarifario] │
│ San Carlos de Bariloche                                      │
│ Ola Mayorista · US$ 88/noche · 02/05/2026                    │
├──────────────────────────────────────────────────────────────┤
│ Panamericano – Aeropuerto       [Traslado] [En tu tarifario] │  ← otro tipo, siempre abajo
│ CABA                                                         │
├──────────────────────────────────────────────────────────────┤
│ ＋ No es ninguno: crear "Panamericano" como hotel nuevo       │
│    Revisá los de arriba antes — si ya existe, elegirlo…      │
└──────────────────────────────────────────────────────────────┘
```

Elegís "Panamericano Buenos Aires". La pregunta desaparece y el formulario queda así (amarillo =
sugerido y editable):

```
 Producto
┌──────────────────────────────────────────────────────────────┐
│ 🔍 Panamericano Buenos Aires                                 │   ← nombre limpio, no la frase
└──────────────────────────────────────────────────────────────┘
 Operador          Entrada      Salida      Noches   Precio/noche
┌──────────────┐ ┌──────────┐ ┌──────────┐ ┌──────┐ ┌──────────────┐
│ Delfos ▒▒▒▒▒▒│ │10/02 ▒▒▒▒│ │15/02 ▒▒▒▒│ │  5   │ │ US$ 95 ▒▒▒▒▒▒│
└──────────────┘ └──────────┘ └──────────┘ └──────┘ └──────────────┘
   ▒ = amarillo (sugerido, editable, se guarda recién con Guardar)
   Último precio: Delfos · US$ 95 · 14/06/2026        ← renglón gris de siempre (P9, 2026-08-06)
```

Y si la fila elegida hubiera sido la del traslado, la ficha se para sola en **Traslado** con el
producto, el operador y las fechas de la frase ya puestos, sin ningún aviso (D3).

---

## Qué NO hay que hacer (para el que implemente)

- No agregar filtros, casilleros, desplegables ni un "buscar en todos los tipos": la fachada no cambia.
- No poner ningún cartel, toast ni ventana al saltar de solapa (P-15, 2026-07-22).
- No escribir de dónde salió un resultado ni nombrar la IA en ninguna forma (2026-08-07, 2026-08-09).
- No mover "crear nuevo" de la última posición ni dejar de mostrarlo (P7, 2026-08-06).
- No usar valores internos como texto ("Aereo", "Transfer"): solo palabras del negocio (P-1).
- No habilitar el cruce de tipos al EDITAR un servicio ya cargado (D6).
- No tocar el resaltado azul, el teclado, ni el "Buscando…" existentes.
- **La línea con ✨ no lleva botones, no se puede elegir con Enter ni con las flechas, y no cuenta como
  una opción más de la lista** (D12). Tampoco se queda pegada después de elegir.
- **Nunca más de una pregunta a la vez**, y solo si la duda cambia qué producto es (D11).
- **No anunciar el atajo de la frase completa**: ni placeholder, ni ejemplo, ni ayudita, ni tour (D13).
- No dejar la frase entera como nombre del producto ni al elegir ni al crear (D13).
- No precargar nada encima de lo que el vendedor ya escribió a mano (P-21).
