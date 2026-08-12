# Spec UI — PDF de presupuesto: campos opcionales, opciones A/B/C y Configuración

> **Fecha:** 2026-08-12 · **Autor:** agente `ux-ui-disenador` · **Para:** `frontend-senior`.
> **Origen:** maqueta del PDF de presupuesto YA FIRMADA por Gastón (artifact publicado). Esta
> spec traduce esas decisiones a las DOS pantallas que hay que tocar: la ficha de carga de
> servicio (campos nuevos + marca de "opción") y Configuración (identidad del PDF + condiciones).
> **Cero preguntas nuevas** — todo lo de acá sale de la guía, del estándar visual firmado
> 2026-08-11 (ya implementado en el código, verificado: `styles.css` tiene `--primary` en azul
> boleto, `ServiceInlineCard.jsx`/`ReservaHeader.jsx` ya usan el molde de botones nuevo) y de
> patrones existentes (`ReservaVoucherTab.jsx` para el envío por WhatsApp, `AiSettingsTab`/
> `SettingsPage` para Configuración).

**Reglas que esta spec respeta (citarlas en el brief de implementación):**
P-3 (una moneda por renglón) · P-5 (fichas en línea, nunca ventana flotante) · P-9 (botón apagado
con motivo a la vista) · P-10 (ícono + palabra) · P-14 (acción destructiva confirma antes) · P-15
(sin cartelitos nuevos, salvo la excepción ya firmada de la pantalla de IA) · P-16 (un dato no se
dice dos veces) · P-20 (ámbar = pide algo, rojo = freno) · P-21 (el sistema sugiere, no decide) ·
F-16 (la excepción "⋯" es discreta) · B.3/B.5 del estándar visual (molde único de botones y chips)
· "nada se borra" con la excepción firmada de que **en Presupuesto, resolver opciones SÍ borra
con rastro** (regla histórica: 2026-08-03; excepción de esta obra: ya firmada por Gastón).

---

## 0. Qué se toca (huesos nuevos, ninguno viejo se saca)

1. `FlightInlineForm.jsx` → 2 campos nuevos dentro de "+ Más detalles" (ya existente, plegado).
2. `HotelInlineForm.jsx` → 1 campo nuevo dentro de "+ Más detalles" (ya existente, plegado).
3. `ServiceInlineCard.jsx` → 1 campo nuevo, A LA VISTA (checkbox "es una alternativa").
4. `ServiceList.jsx` → chip nuevo "Opción A/B/C" + banner de grupo pendiente + acción de fila
   condicional "Elegir esta opción".
5. `ReservaHeader.jsx` → 2 botones nuevos, solo en etapa Presupuesto: "Emitir PDF" y "Enviar por
   WhatsApp".
6. `SettingsPage.jsx` → 1 solapa nueva: "Presupuestos y PDF".

Nada de lo que ya existe se mueve ni se saca. Todo lo nuevo es opcional (nunca obligatorio).

---

## 1. "+ Más detalles" del Aéreo (`FlightInlineForm.jsx`)

**Sin cambios** (siguen igual, mismo lugar): Código de reserva (PNR) · Números de ticket ·
Horarios y escalas (texto libre) · Equipaje (texto libre) · Cabina (select).

**Nuevo — orden final del panel "+ Más detalles":**

| # | Campo | Tipo | Ubicación | Opciones / placeholder | Default |
|---|---|---|---|---|---|
| 1 | Código de reserva (PNR) | texto | (sin cambios) | `ABC123` | vacío |
| 2 | Números de ticket | texto | (sin cambios) | `0741234567890` | vacío |
| 3 | Horarios y escalas | texto libre, col-span-2 | (sin cambios) | `Ej: Sale 10:30 AEP · Escala 1h MDZ · Llega 15:20 IGR` | vacío |
| 4 | Equipaje | texto libre, col-span-2 | (sin cambios) | `Ej: 1 pieza 23kg + 1 de mano` | vacío |
| 5 | **NUEVO** — Qué lleva incluido | 3 casilleros en una fila, col-span-2 | debajo de "Equipaje" | ☐ Mochila o bolso personal · ☐ Equipaje de cabina (carry on) · ☐ Valija despachada | los 3 destildados |
| 6 | Cabina | select | (sin cambios) | Sin especificar/Economy/Premium/Business/Primera | Sin especificar |
| 7 | **NUEVO** — ¿Cómo es el vuelo? | select, al lado de Cabina | mismo renglón que Cabina (grid 2 columnas) | Sin especificar / Directo / Con escala(s) | Sin especificar |

Los 3 casilleros de equipaje usan `type="checkbox"` con `accent-color: var(--primary)` (mismo
azul boleto que el resto de la app, no un color nuevo). Ninguno de los dos campos nuevos es
obligatorio ni se valida al guardar — si quedan todos vacíos/sin marcar, el PDF simplemente no
muestra esa línea (es un espejo de lo cargado, nunca inventa un dato).

## 2. "+ Más detalles" del Hotel (`HotelInlineForm.jsx`)

**Sin cambios:** Confirmación del operador · Dirección.

**Nuevo — va PRIMERO dentro de "+ Más detalles"** (antes de "Confirmación del operador"), porque
es un dato descriptivo del hotel como Régimen/Tipo de habitación, no un dato operativo:

| Campo | Tipo | Opciones | Default |
|---|---|---|---|
| **Estrellas del hotel** | select | Sin especificar / 1 estrella / 2 estrellas / 3 estrellas / 4 estrellas / 5 estrellas | Sin especificar |

Ojo: esto es DISTINTO del campo "Categoría" que ya existe a la vista (fuera de "Más detalles",
`roomCategory` — el nombre fino de la habitación tipo "Superior", "Vista al mar"). No se tocan ni
se confunden: "Categoría" sigue siendo de la habitación, "Estrellas" es nuevo y es del hotel.

---

## 3. Marcar un servicio como "Opción" (A/B/C)

### 3.1 Cómo se marca (en la carga)

En `ServiceInlineCard.jsx`, **a la vista** (no es un dato de "Más detalles" — cambia cómo se
relaciona este servicio con otros), inmediatamente debajo de las pestañas de tipo y antes del
formulario específico:

```
[ Hotel ][ Aéreo ][ Traslado ][ Paquete ][ Asistencia ]

☐ Es una alternativa de otro servicio ya cargado

  (si se tilda, aparece:)
  ¿Alternativa de cuál?  [ Hotel Riu Cancún · 10/02 al 15/02        ▾ ]
                          Ninguno todavía: es la primera opción
```

Un solo campo, checkbox + select condicional (mismo patrón que cualquier campo condicional de la
app). El select lista los servicios YA cargados en esta reserva (nombre + fechas). El sistema
arma el grupo y le pone letra por orden de carga (A, B, C…) solo: no hay casillero de "grupo" ni
de "letra" a la vista — sería un dato técnico que el vendedor no necesita escribir.

### 3.2 Cómo se ve en la tabla de servicios (`ServiceList.jsx`)

Mientras el grupo está **sin resolver**, cada servicio del grupo lleva el chip "OPCIÓN A" (B, C…)
con el **mismo molde de chip que ya usa el estado del servicio** (`rounded-full border px-2.5
py-0.5 text-[11px] font-bold uppercase tracking-wider`), en la misma línea donde ya vive el chip
"Operador: X" (debajo del nombre del servicio — `ServiceList.jsx:744` y `:2325`). Tono **ámbar**
(P-20: un grupo sin resolver "pide algo"), no gris — es la única excepción de tono para un chip
puramente informativo, justificada porque acá SÍ hay una acción pendiente.

Arriba de las filas del grupo, un renglón ámbar (mismo idioma visual que "Falta cargar el
titular" en la cabecera):

```
  ⚠ Elegí cuál se confirma para "Hotel en Cancún" — las otras 2 opciones se anulan.

  ─────────────────────────────────────────────────────────────────────
  Hotel Riu Cancún · 10/02 al 15/02      [OPCIÓN A]      Elegir esta opción
  Hotel Barceló Cancún · 10/02 al 15/02  [OPCIÓN B]      Elegir esta opción
  ─────────────────────────────────────────────────────────────────────
```

**Acción de la fila** (respeta B.5 "una sola acción por fila"): mientras el grupo está pendiente,
la acción de esa fila pasa a ser el botón secundario `size="sm"` (32px, `variant="outline"`)
"Elegir esta opción" con ícono de check. Si la fila ya tenía otra acción (Editar, por ejemplo),
esa se corre adentro del menú "⋯" hasta que el grupo se resuelva (F-16: la excepción es discreta).

**Confirmar (P-14):** al tocar "Elegir esta opción" aparece, EN LÍNEA pegado a esa fila (nunca una
ventana — P-5), un mini-confirm ámbar:

```
  ¿Esta es la que el cliente eligió? Las otras 2 opciones se anulan.
  [ Volver ]  [ Sí, esta ]
   outline      default
```

Al confirmar: el resto del grupo **desaparece de la lista** — el backend lo BORRA físicamente
(decisión firmada 11/08: borrar es válido en etapa Presupuesto; el rastro queda en el Historial
con el evento "Opciones resueltas", que guarda qué se borró con su plata — PR-12). NO esperar
una fila "Anulada" tachada: esa corrección es de esta spec (2°ronda, tras review del backend);
el front refresca la reserva y las filas perdedoras ya no vienen. El chip "OPCIÓN A" desaparece
de la fila ganadora — ya no es una alternativa, es el servicio.

### 3.3 Si se aprieta "El cliente aceptó" con un grupo sin resolver

El motor rechaza (código de negocio propio, ej. `OPTIONS_GROUP_UNRESOLVED`, texto criollo tal
cual). En `ReservaHeader.jsx` esto dispara el **Cartel emergente único**, variante **Bloqueo**
(rojo, ⛔): el mensaje es el texto del motor sin reescribir, con botón de salida real "Ver las
opciones" (además de "Entendido") que cierra el cartel y hace scroll hasta el primer grupo
pendiente en la tabla de servicios — igual al patrón ya usado para otros rechazos con salida
(`CartelEmergente.jsx`, ver ejemplos "Emitir factura" / "Ir a la cuenta del cliente" en la guía).
**No se abre ninguna ventana para elegir**: la elección siempre pasa en la fila, como en 3.2.

---

## 4. Configuración → nueva solapa "Presupuestos y PDF"

En `SettingsPage.jsx`, se agrega al arreglo `tabs`, **después de "Facturación" y antes de
"WhatsApp Bot"** (agrupa lo que sale hacia el cliente). Sin restricción de admin — mismo criterio
que "Facturación" y "Operativa y Caja", que tampoco la tienen.

```
tabs: Agencia · Operativa y Caja · Facturación · [Presupuestos y PDF] (NUEVA) · WhatsApp Bot ·
      Inteligencia artificial (solo admin) · Workflows de aprobación · Logs
```

### Card 1 — "Identidad del PDF"

Mismo molde que la card "Datos de la Agencia" (header con ícono + título + descripción corta,
cuerpo con los campos, footer con un solo botón "Guardar cambios"):

1. **Logo** — miniatura del logo actual (o placeholder "Sin logo cargado") + botón `outline`
   "Cambiar logo" (acepta PNG/JPG/SVG).
2. **Color de la banda** — selector de color nativo + una franja de muestra al lado que se pinta
   en vivo con ese color, para verlo antes de guardar.
3. **Legajo EVT** — campo de texto, placeholder `Ej: 12345`. Sin cartelito de ayuda (P-15) — el
   label alcanza.

### Card 2 — "Condiciones que van en el PDF"

Un acordeón vertical (no pestañas-dentro-de-pestañas: ya está el tabbar de arriba) con 6 bloques,
cada uno con un textarea grande: **Aéreos · Hoteles · Traslados · Paquetes · Asistencias ·
Generales**. Debajo de cada textarea, un link terciario discreto (mismo criterio 2026-08-07/10:
sin caja nueva, sin color fuerte): "✨ Ayudame a redactarlo" — genera un borrador que cae en el
textarea para que el dueño lo revise y edite; nunca se guarda solo (P-21, el sistema sugiere, no
decide). Un solo botón "Guardar cambios" al pie de la card, para los 6 bloques juntos.

---

## 5. Botón "Emitir PDF" en la cabecera de la ficha

Solo en **etapa Presupuesto** (igual que "El cliente aceptó" solo aparece ahí). Vive en la misma
fila de acciones de `ReservaHeader.jsx`, **inmediatamente después del botón principal** y ANTES
del separador de las acciones terciarias (Perdida / Archivar / ⋯):

```
┏━━━━━━━━━━━━━━━━━━┓  ┌───────────┐  ┌──────────────────┐   │  Perdida   Archivar   ⋯
┃ El cliente aceptó┃  │ Emitir PDF│  │ Enviar por        │   │  ‾‾‾‾‾‾‾   ‾‾‾‾‾‾‾‾   ‾
┗━━━━━━━━━━━━━━━━━━┛  └───────────┘  │ WhatsApp          │   │
  variant="default"    variant=       └──────────────────┘   │
  (PRINCIPAL)           "outline"      variant="outline"     │
                        (SECUNDARIA)   (SECUNDARIA)
```

- **"Emitir PDF"** (`variant="outline"`, ícono de documento): genera el PDF con los datos
  actuales de la reserva (siempre la última versión — "el PDF es espejo de lo cargado", nunca
  un archivo viejo guardado) y lo abre/descarga.
- **"Enviar por WhatsApp"** (`variant="outline"`, ícono `Send`): mismo mecanismo YA construido
  para vouchers (`ReservaVoucherTab.jsx` → `POST /messages/…`): resuelve el destinatario solo (acá
  es siempre el cliente de la reserva, no hay ambigüedad de pasajeros como en el voucher), envía y
  confirma con un toast ("Presupuesto enviado por WhatsApp a {nombre}."). Si el cliente no tiene
  teléfono cargado, mismo mensaje claro que ya usa el voucher ("no tiene teléfono cargado. Agregá
  uno y reintentá."). Sin ventana ni paso intermedio — un solo click.

Ninguno de los dos es relleno: la regla de oro del estándar visual es que **la principal es la
única rellena** de la pantalla ("El cliente aceptó" sigue siéndolo).

---

## 6. Qué NO hacer (para quien programe)

1. No convertir "Horarios y escalas" ni "Equipaje" en campos estructurados — siguen siendo texto
   libre; los nuevos campos son ADICIONALES, no reemplazos.
2. No confundir "Estrellas del hotel" (nuevo) con "Categoría" (`roomCategory`, ya existente): son
   dos datos distintos, con dos labels distintos.
3. No abrir ninguna ventana flotante para elegir la opción ganadora — siempre en la fila (P-5).
4. No usar un color nuevo para el chip "Opción A/B/C": es el molde de chip existente en tono
   ámbar de la paleta ya firmada (B.1), nada inventado.
5. No dejar que "Emitir PDF" o "Enviar por WhatsApp" sean botones rellenos ni que compitan en
   tamaño con "El cliente aceptó".
6. No guardar sola una condición redactada por la IA (✨): siempre cae en el textarea para que el
   dueño la revise antes de guardar.

---

## Supuestos de diseño aplicados (no son preguntas — defaults justificados por la guía)

- **Chip en tono ámbar, no gris:** un grupo sin resolver "pide algo" → P-20. Si al verlo armado
  Gastón prefiere gris neutro, es un cambio de una clase CSS, no de estructura.
- **Solapa de Configuración sin restricción de admin:** mismo criterio que Facturación/Operativa,
  que hoy tampoco la tienen.
- **"Emitir PDF"/"Enviar por WhatsApp" SOLO en Presupuesto** (no en Cotización): es lo que pide el
  brief textual; si Gastón los quiere también en Cotización al verlo armado, es agregar una
  condición de estado, no rediseñar nada.
