# Spec: banner de devolución (T5) · celda Estado de "Servicios comprados" · pasajero 100% en línea

> **Fecha:** 2026-08-18 · **Autor:** `ux-ui-disenador` · Fuentes ÚNICAS: `docs/ux/guia-ux-gaston.md`,
> `docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md` (B.1–B.5, P-5/P-6/P-9/P-10/P-15/P-16) y
> `docs/ux/2026-08-16-guia-rollout-estandar-visual.md` (moldes `Button`/`StatusChip`).
> Las tres decisiones de fondo (compactar T5 · celda Estado con fila de expansión · matar el modal
> de pasajero) **ya están firmadas**; este documento es la letra chica visual. Nada de lo que sigue
> agrega, saca ni reordena datos — solo cambia cómo se ven y se abren.
>
> **No se implementa código.** Entrega para `frontend-senior`, con `frontend-reviewer` verificando
> además contra `docs/ux/guia-ux-gaston.md`.

---

## 1) Panel de devoluciones (T5) → fila de aviso compacta que se expande

**Reusa:** `PartialCreditNoteEmissionPanel.jsx`, `T5ResolverLegacyList.jsx`. **Reglas que aplica:** P-5
(en línea, nunca ventana — la única excepción sigue siendo el cartel "¿Seguro?", P-14), P-6 (los
errores no son globitos que se van solos), P-9/P-10 (motivo a la vista, ícono + palabra), B.1 (ámbar
= pide algo · verde = plata entrada · rojo = freno), voz de avisos 2026-07-08 (glosario: "devolución"
en el aviso corto; "nota de crédito"/"factura" siguen permitidos en el detalle, por ser esta una
pantalla de facturación — excepción ya firmada 2026-07-15).

**Corrección de nombre que arrastra el código actual:** el título dice hoy "SERVICIO ANULADO"; el
glosario firmado (2026-07-08) reserva "Anular" para la reserva entera — acá se anuló el *servicio*
solamente, la reserva sigue viva. Pasa a decir **"servicio cancelado"** en todos lados (ya es lo que
dice el texto del cuerpo, solo faltaba corregir el título).

**Corrección de texto que arrastra el código actual:** el aviso del plazo de ARCA en pantalla hoy dice
"El plazo informativo de 15 días venció el {fecha}. Esto no impide emitir." — eso **no** es el texto
que Gastón firmó el 2026-07-15 (P4=A). Se reemplaza por el texto exacto firmado (más abajo). No es una
decisión nueva, es aplicar la que ya existe.

### 1.1 Fila colapsada — un renglón, con ícono + texto + flechita a la derecha

Molde nuevo (no había uno construido para "aviso que se expande"): mismo mecanismo que ya usa toda la
app para no abrir ventanas — clic en toda la fila, igual que "+ Más detalles" de la ficha de carga o
el casillero de `ResolverServicioInline`. Alto 40 px (B.3), redondeo 10 px, colores de significado
B.1. Vive arriba de la ficha, en la misma tira de avisos accionables (orden ya firmado 2026-07-05).

```
Pendiente (ámbar) — hay que hacer algo:
┌──────────────────────────────────────────────────────────────────────┐
│ ⚠  Tenés una devolución pendiente de US$ 200,00 al cliente.       ▾  │
└──────────────────────────────────────────────────────────────────────┘

Procesando (ámbar, sin flecha activa pero se puede igual mirar el detalle):
┌──────────────────────────────────────────────────────────────────────┐
│ ⏳ Estamos emitiendo la devolución en ARCA. En un rato tenés el       │
│    resultado.                                                     ▾  │
└──────────────────────────────────────────────────────────────────────┘

Emitida (verde):
┌──────────────────────────────────────────────────────────────────────┐
│ ✔  Devolución emitida · NC B 0001-00000987                        ▾  │
└──────────────────────────────────────────────────────────────────────┘

Falló (rojo):
┌──────────────────────────────────────────────────────────────────────┐
│ ✕  ARCA rechazó la devolución.                                    ▾  │
└──────────────────────────────────────────────────────────────────────┘

Trabada (ámbar, variante "faltan datos"):
┌──────────────────────────────────────────────────────────────────────┐
│ ⚠  Hay 2 devoluciones viejas por resolver antes de emitir.        ▾  │
└──────────────────────────────────────────────────────────────────────┘

Trabada (ámbar, variante "necesita al contador" — agencia RI):
┌──────────────────────────────────────────────────────────────────────┐
│ ⚠  Esta devolución necesita la firma de un contador.               ▾ │
└──────────────────────────────────────────────────────────────────────┘
```

### 1.2 Expansión — estado "Pendiente" (READY)

Se abre debajo de la misma fila, sin mover nada más de la ficha (P-5). El botón "Confirmar y emitir"
sigue disparando el cartel **"¿Seguro?"** existente — esa es la única ventana permitida (P-14).

```
┌──────────────────────────────────────────────────────────────────────┐
│ ⚠  Tenés una devolución pendiente de US$ 200,00 al cliente.       ▴  │
│ ──────────────────────────────────────────────────────────────────  │
│  Servicio cancelado    Hotel Delfos · Cancún                         │
│  Monto a devolver      US$ 200,00                                    │
│  Factura               Factura B 0001-00012345                       │
│  Saldo antes           US$ 450,00 (la factura sigue viva por el resto)│
│  Dólar de la factura   $ 1.050 (el de la factura, no se cambia)       │
│                                                                        │
│  ⏳ ARCA da 15 días para emitir esta devolución sin trámite extra.    │
│     Quedan 6 días (vence el 24/08/2026).                              │
│                                                                        │
│                                     [ Volver ]  [ Confirmar y emitir ]│
└──────────────────────────────────────────────────────────────────────┘
```

Texto EXACTO del plazo (firmado 2026-07-15 P4=A, no se cambia una palabra):

- **Dentro de plazo:** "Quedan {N} días para emitir esta devolución sin trámites extra ante ARCA
  (vence el {fecha})."
- **Pasado el plazo:** "Pasaron más de 15 días desde que se canceló el servicio. Se puede emitir
  igual, pero convendría consultarlo con un contador antes de seguir."

Error al confirmar (viene del motor): si es corto va en rojo debajo de los botones, dentro de la
misma fila expandida; si es largo va al Cartel emergente único (2026-07-22) — nunca los dos a la vez,
mismo criterio que `ResolverServicioInline`.

### 1.3 Expansión — estado "Trabada" (BLOCKED)

Reusa **tal cual** el contenido de `T5ResolverLegacyList` (lista de servicios viejos a resolver, cada
uno con su factura/monto) o el aviso de firma del contador, solo que ahora vive adentro de la fila que
se abre, no en un panel siempre visible:

```
┌──────────────────────────────────────────────────────────────────────┐
│ ⚠  Hay 2 devoluciones viejas por resolver antes de emitir.        ▴  │
│ ──────────────────────────────────────────────────────────────────  │
│  Estos servicios se cancelaron cuando el sistema todavía no          │
│  guardaba a qué factura correspondía cada uno. Decinos de qué        │
│  factura sale cada devolución y por cuánto.                          │
│                                                                        │
│  Hotel Delfos · Cancún                                                │
│  US$ 200,00                    [ Factura B 0001-00012345 ▾ ] [Resolver]│
│  ──────────────────────────────────────────────────────────────────  │
│  Excursión Bariloche                                                  │
│  $ 45.000,00                   [ Factura A 0001-00008821 ▾ ] [Resolver]│
└──────────────────────────────────────────────────────────────────────┘
```

Variante "necesita al contador" (agencia RI): la expansión muestra solo el texto, sin lista ni botón
— "Esta devolución necesita la firma de un contador antes de emitirse." (ya firmado 2026-07-15).

### 1.4 Expansión — "Emitida" / "Falló" / "Procesando"

Mismo patrón (fila colapsada → clic → detalle + acciones), para no tener un caso especial:

```
Emitida:
│ ✔  Devolución emitida · NC B 0001-00000987                        ▴  │
│ ──────────────────────────────────────────────────────────────────  │
│  Nota de crédito emitida el 18/08/2026.                              │
│                                          [ Ver PDF ]  [ Enviar al cliente ]│

Falló:
│ ✕  ARCA rechazó la devolución.                                     ▴ │
│ ──────────────────────────────────────────────────────────────────  │
│  Motivo de ARCA: «{texto tal cual, sin traducir ni resumir}»         │
│                                                    [ Reintentar ]     │

Procesando:
│ ⏳ Estamos emitiendo la devolución en ARCA. En un rato lo vas a ver. ▴│
│ ──────────────────────────────────────────────────────────────────  │
│  Podés seguir usando la reserva; este estado se actualiza solo.      │
```

---

## 2) Celda "Estado" de Servicios comprados (cuenta del operador)

**Hallazgo antes de diseñar (importante):** al leer el código de hoy, la celda **ya fue rediseñada**
en la tanda del 24/07 al 31/07 (`ResolverServicioInline.jsx`, fix #34 + F4): reusa el mismo botón
"Marcar confirmado"/"Marcar emitido" de la ficha, y "Corregir a mano" **ya es** un link chico y
discreto debajo (exactamente lo que pedía la spec 2026-07-24 P4=A). No está roto como en la
descripción original de la tarea — está aplicando al pie de la letra lo que Gastón firmó el
2026-07-24: **"un casillero chico EN LA MISMA FILA"**.

Lo que sí sigue siendo un problema real: esa fila vive en una **grilla de 9 columnas densas**
(`DataGrid density="compact"`, columna Estado de ~140 px de ancho). Meter ahí, apilado verticalmente,
la etiqueta "N° de confirmación del operador" + el campo + dos botones + un posible error, se ve
apretado — es el mismo síntoma que describía la tarea, aunque el componente de abajo ya sea el
correcto. La solución no es tocar `ResolverServicioInline` (sigue sirviendo igual en la ficha, donde
las filas son anchas): es darle **más aire** solo acá, con una fila de expansión que ocupa todo el
ancho de la tabla (colspan) en lugar de apretarse en la columna. Esto **no contradice** lo firmado
24/07 — sigue siendo "en línea, sin ventana, en el lugar donde está el servicio" — solo le da más
lugar en una tabla angosta. (Igual va como pregunta al final, por las dudas — es un cambio visual de
algo que ya funciona.)

### 2.1 Fila normal (servicio pendiente)

```
 TIPO    DESCRIPCIÓN          RESERVA       FECHA   ESTADO                CÓDIGO   COSTO       VENTA
 ──────────────────────────────────────────────────────────────────────────────────────────────────────
 Hotel   Hotel Delfos·Cancún  F-2026-1067   10/02   [ Marcar confirmado ]  —        US$ 700,00  US$ 900,00
```

### 2.2 Fila con la expansión abierta (clic en "Marcar confirmado")

```
 Hotel   Hotel Delfos·Cancún  F-2026-1067   10/02   [ Marcar confirmado ⌃ ]  —      US$ 700,00  US$ 900,00
┌──────────────────────────────────────────────────────────────────────────────────────────────────────┐
│  N° de confirmación del operador (podés dejarlo vacío)                                                  │
│  [ ABC123                                    ]              [ Confirmar ]   [ Cancelar ]                │
│                                                                                                            │
│  Corregir a mano                                                                                          │
└──────────────────────────────────────────────────────────────────────────────────────────────────────┘
 ──────────────────────────────────────────────────────────────────────────────────────────────────────
 Vuelo   AR1234 EZE-MIA       F-2026-1067   15/02   [ Marcar emitido ]      —        US$ 500,00  US$ 650,00
```

Al tocar "Corregir a mano" (link chico, gris, per P-9/P-10 sin ser un botón que compita), se abre
debajo el desplegable viejo de estado (Solicitado/Confirmado/Cancelado) — mismo comportamiento de
hoy, solo que ahora con lugar propio en vez de apretado en la columna.

### 2.3 Caso de error del motor

```
│  [ ABC123                                    ]              [ Confirmar ]   [ Cancelar ]                │
│  ⚠ La reserva está bloqueada. Pedí autorización para modificar este servicio.                            │
```

Corto → texto rojo ahí mismo (como hoy). Largo (candado, gate de nombres, freno de plata) → Cartel
emergente único, sin cambios (2026-07-22).

---

## 3) Alta/edición de pasajero — todo en línea, muere el modal

**Reusa lógica ya construida** (no se reinventa nada): `pasajeroSearchLogic.js`
(`cumpleUmbralBusqueda`, `construirUrlBusquedaHistorica`, `mapearSugerenciaAlForm`,
`esDuplicadoEnReserva`) y el endpoint `/fiscal/search` de la lupa AFIP, hoy solo en
`PassengerFormModal.jsx`. Se portan al `PasajeroInlineForm.jsx`, que además gana la sección **"+ Más
detalles"** que hoy no tiene (por eso el modal seguía vivo: sin esa sección no había dónde cargar
fecha de nacimiento, vencimientos, nacionalidad, contacto o notas desde la ficha).

**Reglas que aplica:** 2026-07-05 P9=A ("la fila se abre en el lugar, nunca aparte") · P10=A (solo
nombre + tipo + documento a la vista, el resto detrás de "+ Más detalles") · 2026-06-23 (histórico +
dedup suave) · molde "+ Más detalles" ya construido en `HotelInlineForm.jsx` (mismo texto, mismo
ícono, plegado por defecto salvo que ya haya datos cargados).

### 3.1 Editar un pasajero ya cargado — la fila se transforma en el lugar

**No** se abre una tarjeta nueva debajo dejando la fila vieja arriba (eso duplicaría el nombre dos
veces en pantalla, algo que la guía no quiere — P-16). La fila cambia de contenido: el avatar y la
etiqueta ("Adulto 1") se quedan como referencia, pero donde estaban el nombre/chips/Editar/Borrar
ahora están los campos editables. Los chips de vencimiento (pasaporte/DNI/menor) se ocultan mientras
se edita — vuelven a verse al guardar o cancelar, no hay lugar limpio para mostrarlos junto a un campo
de texto activo.

```
Antes de tocar "Editar":
┌────────────────────────────────────────────────────────────────────────┐
│ 🅹  ADULTO 1   JUAN PÉREZ  [Pasaporte vence en 45 días]    Editar Borrar│
│    DNI 30111222                                                        │
└────────────────────────────────────────────────────────────────────────┘

Con "Editar" tocado — la MISMA fila se vuelve el formulario:
┌────────────────────────────────────────────────────────────────────────┐
│ 🅹  ADULTO 1                                                            │
│    [ Juan Pérez______________ ]  [ DNI ▾ ] [ 30111222_______ 🔍 ]       │
│                                                                          │
│    + Más detalles                                                      │
│                                              [ Cancelar ]  [ Guardar ]  │
└────────────────────────────────────────────────────────────────────────┘
```

Con "+ Más detalles" abierto (mismo molde que `HotelInlineForm`, mismos campos que tenía el modal,
sin ninguno nuevo):

```
│    + Más detalles                                                       ▴│
│    ┌──────────────────────────────────────────────────────────────────┐ │
│    │ Fecha de nacimiento     Vencimiento del pasaporte                 │ │
│    │ [ dd/mm/aaaa       ]    [ dd/mm/aaaa                ]             │ │
│    │ Vencimiento DNI (solo si el tipo elegido es DNI)                  │ │
│    │ [ dd/mm/aaaa       ]                                              │ │
│    │ Nacionalidad             Género                                   │ │
│    │ [ Argentina        ]    [ Masculino ▾ ]                           │ │
│    │ Teléfono                 Email                                    │ │
│    │ [ +54 9 11...      ]    [ correo@ejemplo.com          ]           │ │
│    │ Notas                                                             │ │
│    │ [ Preferencias alimenticias, asistencia especial...            ]  │ │
│    └──────────────────────────────────────────────────────────────────┘ │
│                                              [ Cancelar ]  [ Guardar ]   │
```

**Lupa AFIP:** mismo lugar de siempre — botón 🔍 pegado al campo de documento, funciona editando o
creando (igual que hoy en el modal).

**Histórico (base propia):** **solo al crear un pasajero nuevo**, igual que hoy en el modal — al
editar uno ya cargado no tiene sentido buscar "quién es" (ya se sabe). No es una decisión nueva: es
la misma regla que ya tiene `PassengerFormModal`, portada tal cual.

### 3.2 Agregar un pasajero extra — al final de la lista, con el histórico abierto

El botón "Agregar Pasajero" (cabecera de la solapa) abre una fila nueva **al final de la lista de
pasajeros**, con el mismo formulario vacío, en modo "full" (igual que hoy hace `PasajeroInlineForm`
para los slots vacíos declarados — no hay etiqueta de categoría porque es un pasajero "de más").

```
 ... (pasajeros ya cargados arriba) ...

 Con "Agregar Pasajero" tocado, al final de la lista:
┌────────────────────────────────────────────────────────────────────────┐
│ 🅿  PASAJERO 4                                                          │
│    [ Mar_____________________ ]  [ DNI ▾ ] [ ______________  🔍 ]      │
│    ┌ Pasajeros de viajes anteriores ──────────────────────────────┐    │
│    │ Marisa Rosana Salafia · DNI 28900111 · viajó Feb/2025         │    │
│    │ Mariana López · DNI 30556677 · viajó Ago/2024                 │    │
│    └────────────────────────────────────────────────────────────────┘  │
│    + Más detalles                                                      │
│                                              [ Cancelar ]  [ Guardar ]  │
└────────────────────────────────────────────────────────────────────────┘
```

Al elegir una sugerencia del histórico se autocompletan todos los campos (igual que hoy en el modal).
Si esa persona ya está cargada en la reserva, aviso suave de duplicado (2026-06-23, sin bloquear) y no
autocompleta.

### 3.3 Qué NO hay que hacer

- No abrir `PassengerFormModal` desde ningún lado — se jubila como ventana (2026-07-05).
- No mostrar el histórico al editar (solo al crear, regla ya firmada).
- No agregar leyendas del tipo "(opcional)" en ningún campo de "+ Más detalles" (P-15).
- No mostrar los tres campos de arriba (nombre/tipo/documento) más chicos que el resto del sistema:
  mismo alto de campo (40 px, B.3) que cualquier otro formulario.

---

## PREGUNTAS PARA GASTÓN

Son 5, todas sobre comportamiento que la guía no cubre todavía. El resto de este documento ya sale
de decisiones firmadas y no hace falta confirmarlo de nuevo.

### Tema: la fila de la devolución (T5)

**P1. ¿Cómo se abre la fila colapsada de la devolución — la de "Tenés una devolución pendiente…"?**

```
  A) Tocando CUALQUIER PARTE de la fila (la recomendada) ✅
     Es el mismo gesto que ya usás en toda la app para abrir cosas en línea
     (el "+ Más detalles" de la ficha de carga, el botón de confirmar servicio).
     ┌──────────────────────────────────────────────────────┐
     │ ⚠  Tenés una devolución pendiente de US$ 200,00.  ▾  │  ← toda la fila es clickeable
     └──────────────────────────────────────────────────────┘

  B) Solo tocando la flechita de la derecha
     El resto de la fila no hace nada; hay que apuntarle justo a la flechita.
```

**P2. Cuando la devolución YA se emitió o YA falló, ¿"Ver PDF"/"Enviar al cliente"/"Reintentar" se
ven de una, sin tocar nada, o quedan igual que las demás — detrás de abrir la fila?**

```
  A) SIEMPRE detrás de abrir la fila (la recomendada) ✅ — mismo comportamiento en los 5 estados,
     nada especial que aprender.
     ┌──────────────────────────────────────────────────────┐
     │ ✔  Devolución emitida · NC B 0001-00000987        ▾  │
     └──────────────────────────────────────────────────────┘

  B) Ya visibles en la fila colapsada, sin tener que abrirla
     ┌──────────────────────────────────────────────────────┐
     │ ✔  Devolución emitida         [Ver PDF] [Enviar]  ▾  │
     └──────────────────────────────────────────────────────┘
```

### Tema: la celda de Estado de Servicios comprados (cuenta del operador)

**P3. La forma de resolver un servicio (botón "Marcar confirmado" + casillero de N° de confirmación)
ya funciona hoy apretado dentro de la columna. ¿Le damos más aire con una fila que se abre debajo, a
todo el ancho de la tabla (como en la maqueta de la sección 2), o lo dejamos como está hoy (más
angosto, pero es una función que ya anda)?**

```
  A) FILA DE EXPANSIÓN a todo el ancho (la recomendada) ✅ — se ve prolijo, sigue siendo "en línea"
  B) DEJAR COMO ESTÁ — angosto, adentro de la columna Estado, sin tocar nada
```

### Tema: pasajeros

**P4. Al "Agregar Pasajero" (uno de más, no un slot declarado), ¿el formulario aparece al final de
la lista (como en la maqueta 3.2) o preferís que aparezca arriba de todo, primero?**

```
  A) AL FINAL de la lista (la recomendada) ✅ — mismo lugar donde después va a quedar,
     no reordena a los que ya están cargados.
  B) ARRIBA DE TODO — el más nuevo siempre primero.
```

**P5. Mientras se edita un pasajero ya cargado, los chips de "pasaporte por vencer"/"DNI
vencido"/"menor sin autorización" se esconden (vuelven al guardar). ¿Va bien, o los dejamos siempre
visibles arriba del formulario mientras se edita?**

```
  A) SE ESCONDEN mientras se edita (la recomendada) ✅ — menos ruido al lado de un campo de texto activo
  B) SIEMPRE VISIBLES arriba del formulario, aunque se esté editando
```

---

## RESPUESTAS DE GASTÓN (18/08 — FIRMADAS, multiple choice)

- **P1 = A**: la fila de la devolución se abre tocando CUALQUIER parte de la fila.
- **P2 = A**: las acciones (Ver PDF / Enviar / Reintentar) SIEMPRE detrás de abrir la fila, en los 5 estados.
- **P3 = A**: fila de expansión a todo el ancho en "Servicios comprados".
- **P4 = A**: el formulario de "Agregar Pasajero" extra aparece AL FINAL de la lista.
- **P5 = A**: los chips de aviso del pasajero SE ESCONDEN mientras se edita (vuelven al guardar).

Con esto la spec queda completa y ejecutable sin más preguntas.
