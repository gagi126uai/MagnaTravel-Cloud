# Lavado de cara de MagnaTravel — estándar visual (piloto: Reservas)

> **Fecha:** 2026-08-11 · **Autor:** agente `ux-ui-disenador` · **Estado: BORRADOR — NADA SE
> IMPLEMENTA HASTA LA FIRMA DE GASTÓN.**
>
> **Origen (firmado hoy por el dueño):** *"parece un trabajo de novato"* — botones más grandes
> unos que otros, alineaciones distintas, cada pantalla hace la suya. Caso concreto que lo enoja:
> en la ficha de reserva el botón secundario **"Perdida"** se ve **más grande y más pesado** que la
> acción principal **"El cliente aceptó"**. Regla que baja de eso: **la acción principal SIEMPRE es
> la más prominente de la pantalla.**
>
> **Alcance:** piloto en **Reservas** (listado + ficha + ficha de carga de servicio). Después se
> extiende al resto con el mismo estándar.
>
> **Cómo se aprueba:** maqueta firmada ANTES de programar; verificación con **capturas lado a lado**
> (antes / después) en el navegador de Gastón contra producción.

**Reglas de la constitución que esta obra aplica y no puede romper**
(`docs/estandares/2026-07-22-constitucion-producto-v1.md`):

| Regla | Qué exige | Cómo la respeta este estándar |
|---|---|---|
| **PR-2** ⭐ | Ninguna decisión de pantalla se toma sin Gastón | Todo lo que la guía no cubría está en la sección D como pregunta con opciones + una recomendación |
| **P-5** | Fichas de trabajo EN LÍNEA, nunca ventana flotante | El lavado de cara no mueve ninguna ficha a ventana: solo cambia la piel de las que ya están en línea |
| **P-6** | El globito que se va solo es SOLO para el éxito | No se toca: los errores siguen en cartel dentro de la ficha |
| **P-9** | Botón que no se puede: apagado **con el motivo a la vista**, o escondido | El estándar define cómo se ve un botón apagado y su motivo (11 px, gris, máximo 2 renglones) — nunca en tooltip |
| **P-10** | La palabra siempre al lado del ícono | Todos los botones del estándar son ícono + palabra; ninguno queda "mudo" |
| **P-14** | Toda acción destructiva confirma antes | "Anular reserva" pasa a botón discreto de contorno rojo; la confirmación no se toca |
| **P-15** | Sin cartelitos aclarativos en formularios | No se agrega ni una leyenda nueva; se sacan las que sobran |
| **P-16** | Un dato no se dice dos veces | Se saca el `title` (globito al apoyar el mouse) que repite lo que el botón ya dice con letras |
| **P-20** | Aviso suave ≠ freno duro | Ámbar = pedir algo · Rojo = freno. Un color, un significado, en toda la app |
| **P-21** | El sistema sugiere, no decide | La ✨ de la IA sigue discreta, sin cajas nuevas ni colores fuertes |
| **P-3** ⭐ | Pesos y dólares nunca se suman | El estándar de tabla alinea importes por moneda, en renglones separados |
| **F-16** | La excepción es discreta, nunca un botón normal | El menú "⋯" queda como terciario, nunca compite con la acción principal |

**Decisiones firmadas que esta obra NO puede pisar:** la ✨ de la IA es discreta y sin cajas nuevas
(2026-08-09/10) · los avisos largos de bloqueo van al **Cartel emergente único** (2026-07-22) ·
**"Cancelar" ≠ "Anular"** en los textos · **nada se borra** (2026-08-03) · el orden de avisos de la
ficha (2026-07-05) · la maqueta de Reservas firmada el 2026-08-03 (los **huesos** no se tocan: qué
hay, en qué orden, con qué palabras; **acá se cambia solo la piel**).

---

## A) AUDITORÍA — qué está desprolijo hoy, con la prueba

Auditado sobre las capturas reales de producción (`a1-listado.png`, `a2-ficha.png`,
`a3-inline-card.png`, `e2-estrella-par.png`) y sobre el código de `src/TravelWeb/src`.

### A.0 El caso que lo enoja: "Perdida" pesa más que "El cliente aceptó" — causa encontrada

En `features/reservas/components/ReservaHeader.jsx`:

- La fila de acciones de afuera alinea al centro (`flex flex-wrap items-center gap-2`, línea 573).
- Pero el grupo de secundarias de adentro (línea 673) **no dice cómo alinear**:
  `flex flex-wrap gap-2 sm:border-l sm:pl-4`. Sin instrucción, los hijos **se estiran a lo alto del
  más alto**.
- El más alto de ese grupo es la columna de **"Archivar"** (líneas 727-742), que abajo lleva el
  motivo del motor en dos renglones ("Solo se pueden archivar reservas en viaje o finalizadas").

**Resultado:** "Perdida" y "Anular reserva" se estiran a ~62 px de alto mientras la acción principal
mide 40 px. **El botón que no querés que usen es el más grande de la pantalla.** Se ve claro en
`a2-ficha.png`: el bloque gris de "Perdida" es visiblemente más alto que el botón lleno de al lado.

Además, la principal (`bg-cyan-600`) es de un color que **no se usa en ningún otro lado de la app**,
así que ni siquiera se lee como "el botón importante".

### A.1 Las cinco inconsistencias más graves

**1. Hay SEIS colores distintos para "el botón principal".**
`bg-blue-600` en "Pasar a presupuesto" (ReservaHeader:588) · `bg-cyan-600` en "El cliente aceptó"
(ReservaHeader:618) · `bg-slate-900` en "Cerrar reserva" (ReservaHeader:663) · `bg-indigo-600` en
"+ Nuevo Presupuesto" y "+ Agregar servicio" (ReservasPage, ReservaDetailPage:2643) ·
`bg-emerald-600` en ReservaDetailPage:2619 · `bg-blue-600` otra vez en "Guardar servicio"
(ServiceInlineCard:1308). En una misma pantalla (`a3-inline-card.png`) conviven el índigo de
"Agregar servicio", el cian de "El cliente aceptó" y el azul de "Guardar servicio". **El ojo no
aprende cuál es "el botón de hacer".**

**2. Hay un juego de piezas compartidas… que casi nadie usa.**
Existen `components/ui/button.jsx` y `badge.jsx` con tamaños definidos (36/40/44 px) y colores por
variable (`styles.css` define `--primary` = índigo). Pero en la app hay **646 botones escritos a
mano** en 153 archivos, contra **20 archivos** que importan el botón compartido; y **894 usos de
colores puestos a dedo** (`bg-indigo-600`, `bg-cyan-600`, `bg-rose-700`…) en 171 archivos. Las
piezas compartidas están, pero cada pantalla se dibujó sola. **Ese es el "trabajo de novato" que se
ve.**

**3. La misma acción se ve distinta en dos pantallas.**
"Archivar" en el listado: botón **chico de contorno**, 28 px, ícono de archivo dibujado
(ReservaTable:163-176). "Archivar" en la ficha: botón **relleno gris**, 40 px (y estirado a 62), con
un **emoji 🗄** en vez del ícono (ReservaHeader:734). Mismo verbo, dos cuerpos y dos íconos. Lo
mismo pasa con los emojis sueltos 🚫 y 📅 mezclados con íconos dibujados en la misma fila.

**4. Alturas, redondeos y aire, todos distintos.**
Solo en la cabecera de la ficha conviven tres tamaños de botón (`px-5 py-2.5` / `px-3 py-2.5` /
`px-2.5 py-1.5`) y dos redondeos (12 px y 8 px). En la sección Reservas hay **308 redondeos
mezclados** (6, 8, 12 y 16 px) entre 40 archivos; en la ficha de carga se mezclan bordes
`slate-300` con `gray-300` (EmitirFacturaInline:1116 vs 1293). Nada está alineado con nada.

**5. La ficha de carga de servicio no tiene modo oscuro.**
La app tiene el interruptor de sol/luna arriba a la derecha, y toda la app trae su versión oscura…
menos la ficha de carga: **cero** reglas de modo oscuro en toda la carpeta `inline-service/`. En
oscuro, la pantalla más usada del sistema queda un rectángulo blanco. Y el marco de esa ficha es un
**borde azul de 2 px** (`border-2 border-blue-500`, ServiceInlineCard:1086) que grita más que
cualquier otra cosa de la pantalla.

### A.2 El resto del inventario (mismo criterio, menor peso)

- **El motivo del botón apagado pesa más que los datos.** En el listado, el renglón gris "Solo se
  pueden archivar reservas en viaje o finalizadas" se repite en **cada fila** (3 renglones de texto
  por fila en `a1-listado.png`). El motivo tiene que estar (P-9), pero hoy la columna de acciones es
  visualmente la más cargada de la tabla. Se resuelve con tipografía y ancho, no sacándolo.
- **La plata se cuenta en dos idiomas.** En la cabecera va como texto plano ("US$1.250,00"); en la
  tabla de servicios va como **cartelito de color** (verde para pesos, índigo para dólares —
  `CurrencyBadge.jsx`). El cartelito de dólares es **del mismo índigo que los botones de acción**:
  compiten. (Ojo: los colores de esos cartelitos vienen de una maqueta aprobada el 2026-06-10 → si
  se cambian, lo tiene que firmar Gastón. Va como consecuencia de la pregunta **P1**.)
- **Los números no están alineados.** Las cifras usan tipografía de ancho variable: en una columna
  de importes, el "1" ocupa menos que el "8" y las comas no caen una debajo de la otra. En un
  sistema donde se leen importes todo el día, eso cansa.
- **Tres tamaños de letra para lo mismo.** Etiquetas de columna en 10, 11 y 13 px según la
  pantalla; renglones secundarios en 10, 11 y 12 px.
- **Globitos que repiten lo que ya está escrito.** Varios botones llevan `title="…"` diciendo casi
  lo mismo que la palabra que ya se ve (ReservaHeader:517, 548, 589). Es ruido (P-16) — el globito
  solo debería existir donde la guía lo aprobó (el chip "Con cambios", 2026-07-05).
- **La palabra "Cancelar" está usada para dos cosas distintas.** En la ficha de carga, el botón de
  salir sin guardar dice **"Cancelar"** (ServiceInlineCard:1302), pero en este producto "Cancelar"
  es un término del negocio (la reserva se cancela = se abona el total) y está firmado que **no se
  confunde con "Anular"**. Recomendación: que diga **"Descartar"**. (Es cambio de palabra, entra en
  la firma de la maqueta.)
- **La franja naranja de homologación es lo más fuerte de toda la pantalla.** Está en las cuatro
  capturas, arriba de todo, a todo el ancho, en naranja saturado y mayúsculas. Es un aviso de
  configuración, no la información más importante del sistema. → pregunta **P5**.
- **Lo que la maqueta firmada el 2026-08-03 pide y todavía no está** (no es del lavado de cara, pero
  se ve en las capturas): el botón de afuera dice "**+ Nuevo Presupuesto**" cuando lo firmado es
  "**+ Nueva reserva**", y la solapa dice "Presupuestos" donde lo firmado es "**Borradores**". Se
  deja anotado para la tanda que corresponda; **no se toca en esta obra sin decirlo**.

---

## B) PROPUESTA — el sistema visual de MagnaTravel

> Todo lo de esta sección es **propuesta**. Lo que Gastón elija en la sección D manda; lo que no
> conteste, se implementa como está acá **solo después de que firme la maqueta**.

### B.0 La dirección, en una línea

**"Mostrador de agencia": papel blanco de trabajo, tinta oscura, UN solo color de acción (azul
boleto) y el color usado únicamente para decir algo (ámbar = te pide algo, verde = entró plata,
rojo = freno).**

**Por qué esta y no otra:** el que mira esta pantalla es un vendedor que trabaja ocho horas
cargando fechas, nombres e importes. Lo que necesita es **contraste y densidad**: leer 25 filas sin
scrollear y encontrar el importe sin buscarlo. Por eso: nada de fondos crema con letra serif
(pinta de revista), nada de fondo negro con verde flúor (pinta de panel de programador), nada de
línea finita gris pálido tipo diario (se lee mal a las 7 de la tarde). El carácter propio no viene
del fondo: viene **del sello** (ver B.6) y de la disciplina — que TODO esté alineado es, hoy, la
diferencia más visible entre "novato" y "producto serio".

### B.1 Paleta (6 colores + 3 de significado)

| Nombre en criollo | Para qué | Hex |
|---|---|---|
| **Tinta** | Todo el texto importante y los importes | `#0B1220` |
| **Gris dato** | Texto secundario, etiquetas de columna, motivos | `#64748B` |
| **Papel** | Fondo de las tarjetas y las tablas | `#FFFFFF` |
| **Mesa** | Fondo de la pantalla, alrededor de las tarjetas | `#F4F6F9` |
| **Línea** | Bordes y separadores | `#E2E8F0` |
| **Azul boleto** ⭐ | **El único color de acción.** Botón principal, links, solapa activa | `#1D4ED8` (al pasar el mouse `#1E40AF`) |

Los tres de significado (no se usan nunca de adorno):

| | Cuándo | Texto | Fondo |
|---|---|---|---|
| **Ámbar — "te pide algo"** | Sugerencia de la IA, aviso accionable, "falta cargar el titular" | `#B45309` | `#FFFBEB` |
| **Verde — "está bien / entró plata"** | Saldado, cobrado, confirmado | `#047857` | `#ECFDF5` |
| **Rojo — "freno / sin efecto"** | Rechazo del motor, anulado, tachado | `#B91C1C` | `#FEF2F2` |

Regla dura: **un color, un significado.** Si algo no informa nada, va gris.

### B.2 Tipografía (tres roles, una sola familia)

Se queda **Inter** (ya está cargada, no se suma peso a la página). El carácter lo dan el peso y el
tamaño, no una fuente nueva.

| Rol | Dónde | Cómo |
|---|---|---|
| **Título** | "Reserva #F-2026-1067", títulos de sección | 24 px / 20 px, peso 800, letras un poco juntas |
| **Cuerpo** | Todo lo que se lee: nombres, campos, botones | 14 px, peso 400/600. Etiquetas de columna: 11 px, mayúsculas, gris dato |
| **Datos (plata y fechas)** | Importes, saldos, números de reserva | 14 px peso 600 (el número grande de la ficha, 22 px), **con todas las cifras del mismo ancho** para que las comas queden una debajo de la otra en la columna |

Se prohíben los tamaños sueltos: **11 · 12 · 14 · 16 · 20 · 24**. Nada de 10 px ni de 13 px.

### B.3 Botones: la escala y la jerarquía (esto es el corazón del arreglo)

**Una sola altura por contexto.** Botón normal: **40 px**. Botón dentro de una fila de tabla:
**32 px**. Nada más. Redondeo: **10 px** botones y campos · **14 px** tarjetas · redondo completo
solo para los chips.

| Nivel | Cuándo se usa | Cómo se ve |
|---|---|---|
| **1. Principal** | **UNA sola por pantalla**: la acción que el vendedor vino a hacer | Relleno azul boleto, letra blanca 14 semibold, 40 px, sombra apenas perceptible |
| **2. Secundaria** | Acciones normales que no son "la" acción | 40 px, fondo blanco, borde 1 px gris línea, letra tinta |
| **3. Terciaria (fantasma)** | Salidas laterales y cosas que casi no se usan: Perdida, Archivar, "⋯" | 40 px, **sin fondo ni borde**, letra gris dato; al apoyar el mouse se pinta el fondo gris clarito |
| **4. Destructiva discreta** | Anular reserva, Borrar servicio | Molde de la secundaria, con letra roja y borde rosado. **Nunca rellena de rojo.** Siempre confirma (P-14) |
| **5. Apagada** | Lo que el motor no permite | Fondo gris muy claro, letra gris, candadito, y **el motivo escrito** en 11 px gris, máximo 2 renglones (P-9) |

**Las cuatro reglas de oro de una fila de botones:**

1. **Todos los botones de una fila miden lo mismo de alto y arrancan en la misma línea.** (El bug de
   "Perdida" desaparece: la fila se alinea al centro y el motivo del apagado **no estira a los
   hermanos** — cuelga debajo sin empujar nada.)
2. **La principal va primera y es la única rellena.** Si en una pantalla hay dos rellenas, una está
   de más.
3. **Nunca un botón secundario ocupa más área que el principal.**
4. **Ícono + palabra siempre** (P-10), un solo juego de íconos dibujados: **se van todos los
   emojis** (🚫 🗄 📅) y quedan los íconos de línea que ya usa el resto de la app.

### B.4 Espaciado y alineación

Escala única: **4 · 8 · 12 · 16 · 24 · 32 · 48**. Nada de 5, 6, 10, 18, 20.

- Ancho máximo del contenido: **1440 px**, con 24 px de aire a los costados.
- Padding de tarjeta: **20 px**; separación entre tarjetas: **24 px**.
- **Un solo canal de alineación a la izquierda:** el "← Volver al listado", el título, el cliente,
  los chips, las fechas, los avisos y las solapas arrancan **todos en la misma línea vertical**
  (hoy no lo hacen).
- La fila de botones de arriba a la derecha termina **exactamente donde termina la tarjeta**
  (hoy el motivo del apagado se escapa hacia afuera).

### B.5 Chips de estado y tablas

**Chip (un solo molde):** 24 px de alto, redondo completo, 11 px mayúsculas, borde 1 px del mismo
tono, fondo pálido. Tonos: gris (neutro) · azul (en curso) · ámbar (te pide algo) · verde (listo /
plata) · rojo (freno / sin efecto). **Un chip nunca lleva emoji.**

**Tabla:**
- Encabezado 11 px mayúsculas gris dato, sin fondo de color, línea abajo.
- Fila de **56 px** mínimo, separada por una línea de 1 px; al pasar el mouse se pinta gris clarito.
- Texto a la izquierda, **importes a la derecha**, cifras del mismo ancho, una moneda por renglón
  (P-3).
- **Una sola acción por fila**, a la derecha, con la palabra al lado (P-10). Si está apagada, el
  motivo abajo, 11 px, máximo 2 renglones, sin ensanchar la columna.

### B.6 El elemento firma: **el sello**

Para que no parezca una plantilla comprada, MagnaTravel tiene **una** pieza propia: **el sello de
estado**, prestado del sello del pasaporte.

Cuando una reserva llega a un estado que **cierra** la historia (Anulada, Perdida, Finalizada,
Archivada), el estado no se muestra como un chip más: se muestra como un **sello inclinado**, de
doble contorno, letras condensadas en mayúscula, medio transparente, al lado del número de reserva.

```
   Reserva #F-2026-1065     ╭┈┈┈┈┈┈┈┈┈┈┈┈╮
                            ┊  ANULADA   ┊   ← inclinado, doble contorno, medio borrado
                            ╰┈┈┈┈┈┈┈┈┈┈┈┈╯
```

Sirve además para trabajar: una reserva muerta **se ve muerta de lejos**, sin leer nada. Se usa
**solo** ahí (nunca en el listado, nunca en estados vivos): si estuviera en todos lados, dejaría de
ser una firma y sería ruido.

### B.7 La voz de la pantalla (no cambia, se ordena)

Voz activa, criollo, sin jerga: "Guardá el servicio", no "El servicio debe ser guardado". El botón
dice el verbo que hace ("Emitir factura", "Registrar cobro"), nunca "Aceptar" ni "OK". Se respeta
**"Cancelar" ≠ "Anular"**; el botón de salir de una ficha sin guardar dice **"Descartar"**.

---

## C) MAQUETAS — hoy vs. propuesta

### C.1 Cabecera de la ficha de reserva (estado Presupuesto)

**HOY** (así se ve en `a2-ficha.png`):

```
 ← Volver al listado
 Reserva #F-2026-1067  [PRESUPUESTO]        ┌────────────────────┐ ┌───────────┐ ┌──────────────┐
 Marisa Rosana Salafia                      │  El cliente aceptó │ │           │ │ 🗄 Archivar  │(gris
 PUNTA CANA · 1 pasajero                    │   CIAN, 40 px      │ │ ⊗ Perdida │ │  apagado)    │ apagado)
 PAGO: [SIN MOVIMIENTOS] FACTURA:[SIN FACT] └────────────────────┘ │ GRIS LLENO│ │              │
 ┌──────────────────────────────────────┐                          │  62 px !! │ │ Solo se pueden
 │📅 Salida: 10/02/2027 · Regreso: …    │ [✏ Editar fechas 28px]   └───────────┘ │ archivar reservas
 └──────────────────────────────────────┘                                        │ en viaje o…   ← se
 Saldo a cobrar US$0,00 · Recaudado US$0,00 · Inversión US$1.250,00                  escapa afuera
```
Problemas a la vista: el secundario mide **62 px** contra 40 del principal · el principal es de un
color que no se repite en ningún lado · un emoji y un ícono dibujado en la misma fila · el motivo
del apagado se sale de la tarjeta · "Editar fechas" es de otro tamaño (28 px) y otro redondeo.

**PROPUESTA:**

```
 ← Volver al listado
 Reserva #F-2026-1067   [ PRESUPUESTO ]                      ┏━━━━━━━━━━━━━━━━━━┓
 Marisa Rosana Salafia · PUNTA CANA · 1 pasajero             ┃ El cliente aceptó┃  Perdida   Archivar   ⋯
 Pago: sin movimientos  ·  Factura: sin facturar             ┗━━━━━━━━━━━━━━━━━━┛  ‾‾‾‾‾‾‾   ‾‾‾‾‾‾‾‾   ‾
                                                              AZUL LLENO 40 px     fantasma   fantasma  40
 📆 Salida 10/02/2027 · Regreso 15/02/2027   [ Editar fechas ]                      40 px      40 px
                                              secundaria 32 px                                 └ Solo se archivan
 Saldo a cobrar   Recaudado    Inversión                                                         reservas en viaje
 US$ 0,00         US$ 0,00     US$ 1.250,00                                                      o finalizadas.
                                                                                                 (11 px, cuelga,
                                                                                                  no estira nada)
```
Qué cambia, en criollo: **una sola cosa llena de color** y es la que hay que apretar · las otras dos
son texto gris que no compite · las tres miden lo mismo y arrancan en la misma línea · el motivo
cuelga debajo de su botón sin agrandar a los hermanos ni salirse de la tarjeta · el título y el
cliente entran en menos renglones · los importes quedan en columna, alineados.

Con la reserva **Anulada**, la misma cabecera con la firma:

```
 Reserva #F-2026-1065    ╭┈┈┈┈┈┈┈┈┈╮
                         ┊ ANULADA ┊                                      Archivar    ⋯
 PI0724 Consumidor Final ╰┈┈┈┈┈┈┈┈┈╯                                      ‾‾‾‾‾‾‾‾    ‾
 3 de 3 servicios anulados
```

### C.2 Fila del listado de reservas

**HOY** (`a1-listado.png`):

```
 RESERVA          CLIENTE / PASAJEROS    ESTADO       CREADA      FINANZAS      ACCIONES
 ─────────────────────────────────────────────────────────────────────────────────────────────
 #F-2026-1066     👤 Marisa Rosana …   (Confirmada🔒) 06/08/2026  US$800,00    ┌─────────┐
 PLAYA DEL CARMEN 👥 3 pax                            test        [Saldado]    │🗄Archivar│(28px
 • Viaja: 29/08/2026                                                           └─────────┘ apagado)
                                                                               Solo se pueden archivar
                                                                               reservas en viaje o
                                                                               finalizadas.      ← 3 renglones
                                                                                                   por fila
```

**PROPUESTA:**

```
 RESERVA           CLIENTE · PASAJEROS      ESTADO        CREADA        VENTA          
 ────────────────────────────────────────────────────────────────────────────────────────────────
 #F-2026-1066      Marisa Rosana Salafia    (Confirmada🔒)  06/08/2026     US$ 800,00     Archivar
 Playa del Carmen  3 pasajeros                              Maite          Saldado        ‾‾‾‾‾‾‾‾
 Viaja 29/08/2026                                                                         Solo en viaje
                                                                                          o finalizadas.
 ────────────────────────────────────────────────────────────────────────────────────────────────
 #F-2026-1067      Marisa Rosana Salafia    (Presupuesto)   09/08/2026     US$ 1.450,00   Archivar
 Punta Cana        1 pasajero                               Maite          Sin movimientos ‾‾‾‾‾‾‾‾
 Viaja 10/02/2027
```
Qué cambia: se van los íconos de persona/personas que no agregan nada (la columna ya se llama
Cliente) · el número y el destino con una sola jerarquía clara · los importes alineados a la derecha
con las cifras del mismo ancho · la acción de la fila es **fantasma** (no un botón de contorno que
pelea con los datos) · el motivo del apagado en dos renglones cortos, no tres.

### C.3 Ficha de carga de servicio (la que más se usa)

**HOY** (`a3-inline-card.png` / `e2-estrella-par.png`):

```
 ╔══════════════════════════════════════════════════════════════════╗ ← borde AZUL de 2 px, grita
 ║ (Hotel) ( Aéreo ) ( Traslado ) ( Paquete ) ( Asistencia )        ║   pastillas azul lleno
 ║ Hotel                                                            ║
 ║ [🔍 Escribí el nombre del hotel…                              ]  ║
 ║ Operador                                                         ║
 ║ [ Seleccioná un operador…                                     ▾] ║
 ║ Entrada        Salida        Noches   Habitaciones   Pasajeros   ║
 ║ [dd/mm/aaaa]   [dd/mm/aaaa]  [ — ]    [ 1 ]          [ 1 ]       ║
 ║ Régimen *      Tipo de habitación *   Categoría                  ║
 ║ ˅ + Más detalles                                                 ║
 ║                                         [ Cancelar ] [Guardar servicio]║ ← "Cancelar" choca con
 ╚══════════════════════════════════════════════════════════════════╝   la palabra del negocio;
                                                                        botón AZUL distinto del de
                                                                        "+ Agregar servicio" (índigo)
```

**PROPUESTA** (los huesos no se tocan: mismos campos, mismo orden, mismo "Más detalles"):

```
 ┃━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓
 ┃ Nuevo servicio                                                    ┃ ← franja azul de 3 px a la
 ┃ [ Hotel ][ Aéreo ][ Traslado ][ Paquete ][ Asistencia ]           ┃   izquierda; borde normal
 ┃  ‾‾‾‾‾                                                            ┃   1 px como el resto
 ┃                                                                   ┃
 ┃ Hotel                                                             ┃   selector de tipo: molde
 ┃ [ 🔍 Escribí el nombre del hotel…                              ]  ┃   único, gris con el activo
 ┃    ✨ ¿Hotel Robot QA de Córdoba o el de Mendoza?                 ┃   en azul (discreto)
 ┃    Hotel Robot QA · Córdoba · delfos · $0,00/noche  [En tu tarifario]
 ┃    Hotel Robot QA · Mendoza · delfos · $0,00/noche  [En tu tarifario]
 ┃    + No es ninguno: crear "hotel robot qa" como hotel nuevo       ┃
 ┃                                                                   ┃
 ┃ Operador                    Entrada       Salida       Noches     ┃
 ┃ [ Seleccioná un operador ▾] [10/02/2027]  [15/02/2027] 5          ┃
 ┃ Habitaciones  Pasajeros   Régimen      Tipo de habitación         ┃
 ┃ [ 1 ]         [ 1 ]       [Desayuno ▾] [ Doble ▾ ]                ┃
 ┃ Costo por noche   Venta por noche   Moneda                        ┃
 ┃ [ 0,00 ]          [ 0,00 ]          [ ARS (pesos) ▾]              ┃
 ┃ ˅ Más detalles                                                    ┃
 ┃ ─────────────────────────────────────────────────────────────────  ┃
 ┃ Venta US$ 1.450,00 · Ganás US$ 200,00      Descartar  ┏━━━━━━━━━━┓┃
 ┃                                            ‾‾‾‾‾‾‾‾‾  ┃ Guardar  ┃┃ ← única llena, azul boleto,
 ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┗━━━━━━━━━━┛┛   misma altura que "Descartar"
```
Qué cambia: el marco deja de gritar (franja de 3 px en vez de borde azul de 2 px en todo el
contorno) · todos los campos alineados a la misma grilla · **la ficha funciona en modo oscuro** ·
"Cancelar" pasa a **"Descartar"** · el botón de guardar es del mismo azul que el resto de la app ·
la ✨ de la IA queda **igual de discreta** que hoy (una línea, sin caja nueva — decisión firmada
2026-08-09/10, P-21).

---

## D) PREGUNTAS PARA GASTÓN

> Son **cinco**, las que de verdad moldean el rediseño. Todo lo demás de este documento ya está
> resuelto por la guía o por la constitución, y no se te vuelve a preguntar.
> Podés contestar cortito: "1B, 2A, 3A, 4A, 5 otra cosa: …".
>
> **Lo que NO se pregunta porque ya está firmado:** el orden de los avisos de la ficha (2026-07-05),
> que los avisos largos de bloqueo van a ventana emergente única (2026-07-22), que la ✨ es discreta
> (2026-08-10), que la palabra va siempre al lado del ícono (2026-06-08 / P-10), que el botón
> apagado muestra el motivo escrito (P-9), y toda la maqueta de Reservas del 2026-08-03.

### Tema 1: el color del sistema

Contexto: hoy conviven **seis colores** distintos para "el botón importante" (cian, azul, índigo,
verde, gris oscuro y otro azul). Hay que elegir **uno solo** y usarlo en toda la app. El resto de
los colores queda para decir algo (ámbar = te pide algo, verde = entró plata, rojo = freno).

**P1. ¿De qué color es "el botón de hacer" en todo MagnaTravel?**

```
  A) AZUL BOLETO (el que recomiendo) ✅
     Serio, alto contraste, se lee bien todo el día. Es el azul de los formularios
     y boletos de toda la vida.
     ┌──────────────────────────────────────────────────────┐
     │ Reserva #F-2026-1067  [PRESUPUESTO]                  │
     │                          ┏━━━━━━━━━━━━━━━━━┓         │
     │                          ┃ El cliente      ┃ Perdida │
     │                          ┃ aceptó   (AZUL) ┃         │
     │                          ┗━━━━━━━━━━━━━━━━━┛         │
     └──────────────────────────────────────────────────────┘

  B) VIOLETA/ÍNDIGO (el que ya usa el logo "MT" y varios botones)
     Es el que más aparece hoy; cambia menos, pero se parece más a
     cualquier sistema nuevo que ves por ahí.
     ┌──────────────────────────────────────────────────────┐
     │                          ┏━━━━━━━━━━━━━━━━━┓         │
     │                          ┃ El cliente      ┃ Perdida │
     │                          ┃ aceptó (VIOLETA)┃         │
     │                          ┗━━━━━━━━━━━━━━━━━┛         │
     └──────────────────────────────────────────────────────┘

  C) AZUL MARINO OSCURO CASI NEGRO, con el naranja quemado como acento de marca
     Más carácter, más "agencia con historia"; menos brillo, más sobrio.
     ┌──────────────────────────────────────────────────────┐
     │                          ┏━━━━━━━━━━━━━━━━━┓         │
     │                          ┃ El cliente      ┃ Perdida │
     │                          ┃ aceptó (MARINO) ┃         │
     │                          ┗━━━━━━━━━━━━━━━━━┛         │
     └──────────────────────────────────────────────────────┘
```
**Ojo, una consecuencia:** hoy el cartelito de **dólares** (`US$`) es violeta, del mismo color que
los botones. Si elegís A o C, ese cartelito pasa a **gris tinta** para que no compita con las
acciones (los cartelitos de moneda los aprobaste en junio, por eso te aviso antes de tocarlos).
¿Va bien ese cambio? (**Recomiendo que sí.**)

---

### Tema 2: cuánto aire tiene la pantalla

Contexto: sos vos y tus vendedores mirando listas de reservas todo el día. Cuanto más apretado,
más filas entran sin bajar la pantalla; cuanto más aire, más descansado se lee.

**P2. ¿Cómo querés las listas?**

```
  A) COMPACTA (la que recomiendo) ✅  — ~25 reservas sin bajar la pantalla
     ─────────────────────────────────────────────────────────────
     #F-2026-1066  Marisa Rosana Salafia  (Confirmada)  US$ 800,00
     Playa del Carmen · 3 pasajeros · Viaja 29/08/2026
     ─────────────────────────────────────────────────────────────
     #F-2026-1067  Marisa Rosana Salafia  (Presupuesto) US$ 1.450,00
     Punta Cana · 1 pasajero · Viaja 10/02/2027
     ─────────────────────────────────────────────────────────────

  B) CÓMODA — ~15 reservas sin bajar la pantalla, todo más grande y separado
     ═════════════════════════════════════════════════════════════
                                                                  
      #F-2026-1066     Marisa Rosana Salafia                      
      Playa del Carmen                                            
      3 pasajeros · Viaja 29/08/2026     (Confirmada)  US$ 800,00 
                                                                  
     ═════════════════════════════════════════════════════════════
```

---

### Tema 3: los botones de arriba de la ficha

Contexto: esto es lo que te enojó. Hoy "Perdida" se ve más grande que "El cliente aceptó". Ya está
decidido que **la acción principal es siempre la más prominente**; falta decidir **cuánto se
apagan las otras**.

**P3. ¿Cómo se ven "Perdida" y "Archivar" al lado del botón principal?**

```
  A) TEXTO PELADO, sin caja (el que recomiendo) ✅
     Solo el principal tiene forma de botón. Las salidas laterales son texto gris.
     ┏━━━━━━━━━━━━━━━━━━┓
     ┃ El cliente aceptó┃    Perdida     Archivar     ⋯
     ┗━━━━━━━━━━━━━━━━━━┛

  B) CON CONTORNO FINITO (caja de línea, fondo blanco)
     Se ven como botones, pero mucho más livianos que el principal.
     ┏━━━━━━━━━━━━━━━━━━┓  ┌─────────┐  ┌──────────┐  ┌───┐
     ┃ El cliente aceptó┃  │ Perdida │  │ Archivar │  │ ⋯ │
     ┗━━━━━━━━━━━━━━━━━━┛  └─────────┘  └──────────┘  └───┘

  C) ESCONDIDAS DENTRO DEL "⋯"  (arriba queda SOLO la acción principal)
     ┏━━━━━━━━━━━━━━━━━━┓  ┌───┐        Al tocar "⋯":  ┌──────────────┐
     ┃ El cliente aceptó┃  │ ⋯ │                       │ Perdida      │
     ┗━━━━━━━━━━━━━━━━━━┛  └───┘                       │ Archivar     │
                                                        │ Volver atrás │
                                                        └──────────────┘
```
(En los tres casos "Anular reserva" queda con **letra roja y contorno rosado**, nunca relleno rojo,
y sigue pidiendo confirmación.)

---

### Tema 4: que no parezca una plantilla

Contexto: querés que se note que es **tu** sistema, no una plantilla bajada de internet. Propongo
**una sola** pieza propia, usada con cuentagotas.

**P4. ¿Qué le pone identidad a MagnaTravel?**

```
  A) EL SELLO (el que recomiendo) ✅
     Las reservas que ya no van más (Anulada, Perdida, Finalizada) muestran el estado
     como un sello de pasaporte, inclinado y medio borroneado. Se ve muerta de lejos.
      Reserva #F-2026-1065    ╭┈┈┈┈┈┈┈┈┈╮
                              ┊ ANULADA ┊
      PI0724 Consumidor Final ╰┈┈┈┈┈┈┈┈┈╯

  B) LA FRANJA DE COLOR AL COSTADO
     Cada ficha y cada fila lleva una barrita de color a la izquierda según el estado,
     como el talón de un ticket de embarque.
      ┃ #F-2026-1066  Playa del Carmen ...     (verde = confirmada)
      ┃ #F-2026-1067  Punta Cana ...           (azul  = presupuesto)

  C) NINGUNA — que sea sobrio y listo, la prolijidad ya es suficiente identidad.
```

---

### Tema 5: la franja naranja de arriba

Contexto: la franja naranja "MODO HOMOLOGACIÓN ACTIVO — COMPROBANTES SIN VALIDEZ LEGAL" es hoy **lo
más fuerte de toda la pantalla**, en todas las pantallas. El aviso tiene que estar (que no te
confundas y creas que facturaste de verdad), pero está gritando más que tu trabajo.

**P5. ¿Cómo la querés?**

```
  A) TIRA FINA GRIS OSCURO con un puntito naranja (la que recomiendo) ✅
     ────────────────────────────────────────────────────────────────
      ● Modo prueba — los comprobantes no tienen validez legal
     ────────────────────────────────────────────────────────────────
      (mitad de alto que hoy, sin mayúsculas, no compite con nada)

  B) COMO ESTÁ HOY, naranja a todo lo ancho y en mayúsculas
     ████████████████████████████████████████████████████████████████
      ⓘ MODO HOMOLOGACIÓN ACTIVO • COMPROBANTES SIN VALIDEZ LEGAL ⓘ
     ████████████████████████████████████████████████████████████████

  C) CHIQUITA Y AL COSTADO, pegada al nombre del sistema arriba
      ☰  MagnaTravel ERP  (modo prueba)                    🔍 Buscar…
```

---

## E) QUÉ **NO** HAY QUE HACER (para el que programe)

1. **No tocar los huesos.** No se agrega, saca ni reordena ningún campo, botón, solapa ni aviso.
   Esta obra cambia **cómo se ve**, no **qué hay**. Si algo parece sobrar, se pregunta.
2. **No inventar colores nuevos.** Solo los de B.1. Si hace falta un color que no está, es que el
   diseño está mal: se pregunta.
3. **No escribir más botones a mano.** Todo botón nuevo o tocado sale del molde único (B.3), con
   las cinco variantes. Cero `bg-cyan-600` / `bg-emerald-600` sueltos.
4. **No sacar el motivo de un botón apagado** (P-9) ni meterlo en un globito al apoyar el mouse.
   Se achica la letra, nunca se esconde el texto.
5. **No agregar leyendas ni cartelitos aclarativos** a los formularios (P-15).
6. **No mover ninguna ficha de trabajo a ventana flotante** (P-5) ni convertir errores en globitos
   que se van solos (P-6).
7. **No tocar la ✨ de la IA**: sigue siendo una línea discreta, sin caja nueva ni color fuerte.
8. **No usar emojis** como íconos de acción: un solo juego de íconos dibujados.
9. **No cambiar palabras** que no estén en este documento. "Cancelar" ≠ "Anular" sigue firme.
10. **No arrancar sin la firma.** Maqueta primero, capturas lado a lado después.

## F) Cómo se verifica

1. Maqueta (HTML) con las respuestas de Gastón aplicadas → **firma**.
2. Se programa **solo la piel** de: cabecera de la ficha · fila del listado · ficha de carga.
3. Captura **lado a lado** (antes / después) de las mismas tres pantallas, en claro y en oscuro.
4. Repaso de la lista E, punto por punto, y de las reglas citadas arriba por número.
5. OK final: Gastón mirando la pantalla real en producción.

---

## Anexo — evidencia de la auditoría (archivo:línea)

| Hallazgo | Dónde |
|---|---|
| Secundarias se estiran (causa del "Perdida" grande) | `features/reservas/components/ReservaHeader.jsx:673` (grupo sin alineación) + `:727-742` (motivo en 2 renglones) |
| Seis colores de botón principal | `ReservaHeader.jsx:588, 618, 663` · `ReservaDetailPage.jsx:2619, 2643` · `inline-service/ServiceInlineCard.jsx:1308` |
| Piezas compartidas casi sin usar | `components/ui/button.jsx`, `badge.jsx`, `styles.css` (`--primary`) vs. 646 botones a mano en 153 archivos y 894 colores a dedo en 171 |
| "Archivar" con dos cuerpos y dos íconos | `ReservaTable.jsx:163-176` (contorno, 28 px, ícono) vs. `ReservaHeader.jsx:728-736` (relleno, 40 px, emoji) |
| Redondeos y tamaños mezclados | 308 redondeos distintos en 40 archivos de `features/reservas`; `EmitirFacturaInline.jsx:1116` (`slate-300`) vs `:1293` (`gray-300`) |
| Ficha de carga sin modo oscuro | carpeta `features/reservas/inline-service/` — **cero** reglas de modo oscuro |
| Marco azul de 2 px de la ficha de carga | `ServiceInlineCard.jsx:1086` |
| "Cancelar" usado como "descartar" | `ServiceInlineCard.jsx:1302` |
| Cartelito de dólares del mismo color que las acciones | `components/ui/CurrencyBadge.jsx:27` (`bg-indigo-700`) |
| Globitos que repiten el texto visible | `ReservaHeader.jsx:517, 548, 589` |
| Textos pendientes de la maqueta firmada 2026-08-03 | `ReservasPage.jsx:284` dice "Nuevo Presupuesto" (firmado: "+ Nueva reserva"); solapa "Presupuestos" (firmado: "Borradores") |
