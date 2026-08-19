# Spec ejecutable — Rediseño de Configuración (Mezcla A+B, portada + sección)

> **Fecha:** 2026-08-18 · **Estado: FIRMADO por Gastón hoy** vía maquetas en Claude Design +
> multiple choice ("me cierra"). Esta spec **no reabre la dirección**, es la letra chica para que
> `frontend-senior` implemente literal. Ningún dato acá es una pregunta nueva — donde hizo falta un
> default no firmado explícitamente, está resuelto abajo citando la regla que lo respalda, marcado
> **[DEFAULT]**.

## Reglas de la constitución que esta obra aplica

| Regla | Qué exige | Cómo la respeta esta spec |
|---|---|---|
| **P-5** | Fichas de trabajo en línea, nunca ventana flotante | Cero modales nuevos: portada y sección son dos pantallas normales, navegación por link |
| **P-9 / P-10** | Motivo del botón apagado a la vista · palabra siempre al lado del ícono | No aplica botones apagados en esta obra; los chips de la portada llevan palabra, nunca solo color |
| **P-15** | Sin cartelitos aclarativos en formularios; nada de "(opcional)" | La bajada de cada tarjeta/sección es descripción, no leyenda de ayuda; no se agrega texto nuevo |
| **P-16** | Un dato no se dice dos veces | El botón "Guardar cambios" de Agencia vive en UN solo lugar (ver §5) |
| **B.1 / B.2 / B.3** (`docs/ux/2026-08-11-estandar-visual-y-lavado-de-cara.md`) | Paleta única, tipografía de 3 roles, escala de botones | Título 24/700 (enmienda 18/08, ver rollout), radios 10px inputs / 14px tarjetas, azul boleto `#1D4ED8` único color de acción |
| Guía de rollout (`docs/ux/2026-08-16-guia-rollout-estandar-visual.md`) | Moldes ya construidos: `Button`, `StatusChip`/`badge.jsx`, escala de espaciado 4·8·12·16·24·32·48 | Todo lo nuevo de esta obra usa esos moldes; cero color a mano |
| **Gate UX 2026-06-05 (regla del dueño)** | Nada se programa sin pasar por `ux-ui-disenador` | Esta spec es ese paso, ya firmado |

**Fuente visual exacta** (artboards firmados hoy, complementan — no reemplazan — la paleta B.1):
primary `#1D4ED8` · borde `#E2E8F0` · tinta `#0F172A` · gris dato `#64748B` · labels de grupo
`#94A3B8` · chip verde texto `#047857` / fondo `#ECFDF5` / borde `#A7F3D0` · chip ámbar texto
`#B45309` / fondo `#FEF3C7` / borde `#FDE68A` · caja de ícono `#EFF6FF`.

---

## 1) Realidad del código de hoy (verificada, punto de partida)

- `src/TravelWeb/src/pages/SettingsPage.jsx` (324 líneas): un solo archivo con navegación por
  **pestañas horizontales** (`activeTab` en estado local de React, **sin ruta propia** — hoy
  `/settings` es una sola URL, no hay `?tab=` ni sub-rutas). El tab por defecto es `"agency"`, así
  que hoy entrar a Configuración cae directo en el formulario de Agencia. **Esto cambia**: entrar a
  Configuración ahora muestra la Portada (pantalla 1) — es exactamente lo firmado.
- Las 8 secciones y sus componentes reales, tal cual existen hoy:
  - **Agencia**: formulario **inline dentro de `SettingsPage.jsx`** (líneas 176-297), no es un
    componente separado — es la única de las 8 así.
  - **Operativa y Caja**: `<OperationalFinanceSettingsTab />`
  - **Facturación**: `<AfipSettingsTab />`
  - **Presupuestos y PDF**: `<BudgetPdfSettingsTab />`
  - **WhatsApp Bot**: `<WhatsAppBotTab />`
  - **Inteligencia artificial**: `<AiSettingsTab />` (`adminOnly`)
  - **Workflows de aprobación**: `<ApprovalPoliciesTab />` (`requiredPermission="approvals.policies"`)
  - **Logs y Programación**: `<LogsDashboard />` (solo Admin, chequeo especial `isTabVisible`)
- Visibilidad real hoy (función `isTabVisible`, línea 65-85): admin-only para IA (vía
  `puedeVerConfiguracionIa`) y para Logs (vía `isAdmin()`); `requiredPermission` genérico para
  Aprobaciones (vía `hasPermission`); el resto, siempre visible. **Esta spec no toca ni una coma de
  esa lógica** — la reutiliza tal cual para decidir qué tarjeta/ítem se ve.
- Ruta real: `App.jsx:306-309`, un solo `<Route path="/settings" element={hasPermission("configuracion.view") ? <SettingsPage/> : <Navigate to="/dashboard"/>} />`. El permiso general
  `configuracion.view` ya envuelve toda la página — no cambia.
- Dato para los chips, ya existe y ya se consume en el código:
  - WhatsApp: `WhatsAppBotTab.jsx` guarda `botStatus` (`GET /webhooks/status` → `data.status`).
    `botStatus === "READY"` es la condición real de "conectado" (línea 264/273 del componente;
    "Bot conectado" ya es el texto que usa hoy).
  - Facturación: `AfipSettingsTab.jsx` guarda `form.isProduction` (booleano, ya viene del backend).

---

## 2) Pantalla 1 — PORTADA (`/settings`)

### 2.1 Textos exactos (firmados, no se tocan)

**Título:** `Configuración` (24px, peso 700 — enmienda de tipografía firmada 2026-08-18 en la guía
de rollout, no 800).

**Bajada:** `Todo lo que define cómo trabaja tu agencia, junto y de un vistazo. Tocá una tarjeta
para entrar.`

**Grupos y tarjetas** (etiqueta de grupo: 11px, mayúsculas, `#94A3B8`):

| Grupo | Tarjeta | Ícono (lucide, ya importado en `SettingsPage.jsx`) | Descripción de la tarjeta (texto exacto) |
|---|---|---|---|
| TU EMPRESA | Agencia | `Building2` | Nombre, CUIT, legajo, dirección y las cuentas donde te depositan. |
| TU EMPRESA | Facturación | `FileText` | Punto de venta, certificados de ARCA y cómo salen tus comprobantes. |
| TU EMPRESA | Operativa y Caja | `Settings2` | Frenos de plata, avisos de deuda y las reglas del día a día. |
| LO QUE VE EL CLIENTE | Presupuestos y PDF | `Palette` | Tu logo, los colores y las condiciones que salen en cada presupuesto. |
| LO QUE VE EL CLIENTE | WhatsApp Bot | `Smartphone` | El número conectado y los mensajes con los que atiende consultas. |
| LO QUE VE EL CLIENTE | Inteligencia artificial | `Sparkles` | El ayudante que sugiere textos y evita cargar cosas dos veces. |
| REGLAS Y SISTEMA | Workflows de aprobación | `ShieldCheck` | Qué cosas necesitan tu OK antes de salir. |
| REGLAS Y SISTEMA | Logs y Programación | `TerminalSquare` | El detrás de escena: registros del sistema y tareas programadas. |

### 2.2 Maqueta ASCII — desktop (3 columnas)

```
 Configuración                                                                24/700
 Todo lo que define cómo trabaja tu agencia, junto y de un vistazo.
 Tocá una tarjeta para entrar.                                                 gris

 TU EMPRESA                                                              11px maysc, #94A3B8
 ┌───────────────────────┐  ┌───────────────────────┐  ┌───────────────────────┐
 │ ┌──┐                 ›│  │ ┌──┐         [HOMOLOG.]│  │ ┌──┐                 ›│
 │ │🏢│ Agencia           │  │ │📄│ Facturación       │  │ │⚙ │ Operativa y Caja  │
 │ └──┘                  │  │ └──┘                    │  │ └──┘                  │
 │ Nombre, CUIT, legajo, │  │ Punto de venta,         │  │ Frenos de plata,      │
 │ dirección y las       │  │ certificados de ARCA y  │  │ avisos de deuda y     │
 │ cuentas donde te      │  │ cómo salen tus          │  │ las reglas del día    │
 │ depositan.            │  │ comprobantes.           │  │ a día.                │
 └───────────────────────┘  └───────────────────────┘  └───────────────────────┘

 LO QUE VE EL CLIENTE                                                    11px maysc, #94A3B8
 ┌───────────────────────┐  ┌───────────────────────┐  ┌───────────────────────┐
 │ ┌──┐                 ›│  │ ┌──┐        [CONECTADO]│  │ ┌──┐                 ›│
 │ │🎨│ Presupuestos y PDF│  │ │📱│ WhatsApp Bot      │  │ │✨│ Inteligencia      │
 │ └──┘                  │  │ └──┘                    │  │ └──┘  artificial      │
 │ Tu logo, los colores  │  │ El número conectado y   │  │ El ayudante que       │
 │ y las condiciones que │  │ los mensajes con los    │  │ sugiere textos y      │
 │ salen en cada         │  │ que atiende consultas.  │  │ evita cargar cosas    │
 │ presupuesto.          │  │                         │  │ dos veces.            │
 └───────────────────────┘  └───────────────────────┘  └───────────────────────┘

 REGLAS Y SISTEMA                                                        11px maysc, #94A3B8
 ┌───────────────────────┐  ┌───────────────────────┐
 │ ┌──┐                 ›│  │ ┌──┐                 ›│
 │ │🛡│ Workflows de      │  │ │⌨ │ Logs y Programación│
 │ └──┘  aprobación       │  │ └──┘                  │
 │ Qué cosas necesitan   │  │ El detrás de escena:  │
 │ tu OK antes de salir. │  │ registros del sistema │
 │                       │  │ y tareas programadas. │
 └───────────────────────┘  └───────────────────────┘
```

Nota de la maqueta: `[HOMOLOG.]` y `[CONECTADO]` son los chips reales, van arriba a la derecha de
la tarjeta (mismo renglón que el nombre); las tarjetas sin dato real muestran el chevron `›` en su
lugar, nunca los dos juntos. `🏢📄⚙🎨📱✨🛡⌨` de esta maqueta son marcadores de posición del ASCII —
en la app son los íconos lucide de la tabla de arriba, 20px, dentro de la cajita 40×40 `#EFF6FF`.

### 2.3 Maqueta ASCII — mobile **[DEFAULT §6.1]**

1 columna, mismo orden de grupos, mismo contenido de tarjeta (ancho completo):

```
 Configuración
 Todo lo que define cómo trabaja tu agencia, junto
 y de un vistazo. Tocá una tarjeta para entrar.

 TU EMPRESA
 ┌─────────────────────────────────┐
 │ ┌──┐                           ›│
 │ │🏢│ Agencia                     │
 │ └──┘                            │
 │ Nombre, CUIT, legajo, dirección │
 │ y las cuentas donde te          │
 │ depositan.                      │
 └─────────────────────────────────┘
 ┌─────────────────────────────────┐
 │ ┌──┐            [HOMOLOGACIÓN] ›│
 │ │📄│ Facturación                 │
 │ └──┘                            │
 │ Punto de venta, certificados de │
 │ ARCA y cómo salen tus           │
 │ comprobantes.                   │
 └─────────────────────────────────┘
 ...(igual para el resto, mismo orden)
```

### 2.4 Chips de estado — regla exacta

| Tarjeta | Dato real | Condición | Texto del chip | Tono |
|---|---|---|---|---|
| WhatsApp Bot | `botStatus` (`GET /webhooks/status`) | `botStatus === "READY"` | `CONECTADO` | verde (`#047857` / `#ECFDF5` / borde `#A7F3D0`) |
| WhatsApp Bot | ídem | cargó y **no** es `"READY"` (`OFFLINE`, `STARTING`, `SCAN_QR`) | `DESCONECTADO` **[DEFAULT]** | gris neutro (mismo tratamiento que el chip "Sin movimientos" ya firmado 2026-06-24 — un estado sin urgencia no es ámbar, ámbar es reservado para "te pide algo") |
| WhatsApp Bot | ídem | todavía no respondió la API | *(nada, solo el chevron `›`)* | — |
| Facturación | `form.isProduction` (config de AFIP) | `isProduction === true` | `PRODUCCIÓN` | verde |
| Facturación | ídem | `isProduction === false` | `HOMOLOGACIÓN` | ámbar (`#B45309` / `#FEF3C7` / borde `#FDE68A`) |
| Facturación | ídem | todavía no respondió la API | *(nada, solo el chevron `›`)* | — |
| Todas las demás (Agencia, Operativa y Caja, Presupuestos y PDF, IA, Aprobaciones, Logs) | — | siempre | *(nada, solo el chevron `›`)* | — |

Regla dura, sin excepción: **nunca un chip inventado, nunca un chip "Cargando…"**. Si el dato no
llegó todavía, la tarjeta se ve exactamente igual que las que no tienen chip (chevron a secas). El
chip aparece recién cuando el dato real está confirmado.

### 2.5 Estilo de la tarjeta

- Grid: 3 columnas desktop (`grid-cols-3`, gap 24px — escala B.4), 1 columna mobile.
- Tarjeta: radio 14px, borde 1px `#E2E8F0`, fondo blanco, sombra suave (`shadow-sm`), padding 20px.
- Ícono: 20px lucide, dentro de cajita 40×40px, radio 10px, fondo `#EFF6FF`, color primary.
- Nombre: 15px, peso 700, tinta `#0F172A`.
- Descripción: 13px, gris dato `#64748B`, hasta 2-3 renglones (no se trunca con "…": el texto ya
  entra en el ancho de la tarjeta, son descripciones cortas y fijas).
- A la derecha del nombre: chevron `›` (lucide `ChevronRight`, 16px, gris dato) **o** el chip de
  estado — nunca los dos a la vez (si hay chip, el chip reemplaza al chevron en esa fila; la
  tarjeta sigue siendo 100% clickeable en toda su superficie).

---

## 3) Pantalla 2 — SECCIÓN (`/settings/{slug}`)

### 3.1 Tabla sección → slug → componente reusado → visibilidad → chip

| # | Sección (título 18/700) | Slug | Componente reusado (tal cual, sin tocar adentro) | Visibilidad (regla real, sin cambios) | Chip en el menú lateral |
|---|---|---|---|---|---|
| 1 | Agencia | `agencia` | `AgencySettingsTab` **[nuevo, ver §5 — extracción mecánica del form inline actual, mismos campos]** | Siempre (con `configuracion.view`) | — |
| 2 | Operativa y Caja | `operativa-caja` | `OperationalFinanceSettingsTab` | Siempre | — |
| 3 | Facturación | `facturacion` | `AfipSettingsTab` | Siempre | — |
| 4 | Presupuestos y PDF | `presupuestos-pdf` | `BudgetPdfSettingsTab` | Siempre | — |
| 5 | WhatsApp Bot | `whatsapp` | `WhatsAppBotTab` | Siempre | — |
| 6 | Inteligencia artificial | `ia` | `AiSettingsTab` | Solo Admin (`puedeVerConfiguracionIa`) | — |
| 7 | Workflows de aprobación | `aprobaciones` | `ApprovalPoliciesTab` | `hasPermission("approvals.policies")` | — |
| 8 | Logs y Programación | `logs` | `LogsDashboard` | Solo Admin (`isAdmin()`) | — |

El menú lateral **no** repite los chips de la portada — el dato de "Conectado"/"Homologación" ya se
ve completo adentro de esa misma sección (ej. WhatsAppBotTab ya muestra "Bot conectado" en grande).
Ponerlo también en el menú sería decir el mismo dato dos veces (P-16).

### 3.2 Grupos del menú lateral — misma agrupación y regla de "grupo vacío"

Mismos 3 grupos y mismo orden que la portada (TU EMPRESA / LO QUE VE EL CLIENTE / REGLAS Y
SISTEMA). Un ítem no visible (según §3.1) **no aparece, ni apagado, ni con candado** — coherente
con el resto de la app (P-9: lo que no aplica no se muestra, no se apaga). **Un grupo entero
desaparece** (encabezado incluido) si los items visibles del grupo son cero. Ejemplo real: un
vendedor sin `approvals.policies` y sin ser Admin ve el grupo "REGLAS Y SISTEMA" **completo,
desaparecido** (ni Aprobaciones ni Logs le quedan).

### 3.3 Maqueta ASCII — desktop

```
 ← Configuración                    │  Facturación                                    18/700
 ‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾  13/600 azul     │  Punto de venta, certificados de ARCA y cómo     gris
                                     │  salen tus comprobantes.
 TU EMPRESA                         │
   Agencia                          │  ┌──────────────────────────────────────────────┐
  ┏━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┓ │  │                                              │
  ┃ Facturación         (ACTIVO)  ┃ │  │   ...CONTENIDO EXISTENTE DE AfipSettingsTab...│
  ┗━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━┛ │  │   (tal cual está hoy, sin tocar adentro)      │
   Operativa y Caja                 │  │                                              │
                                     │  └──────────────────────────────────────────────┘
 LO QUE VE EL CLIENTE                │
   Presupuestos y PDF                │
   WhatsApp Bot                      │
   Inteligencia artificial           │
                                     │
 REGLAS Y SISTEMA                    │
   Workflows de aprobación           │
   Logs y Programación               │
```

- Columna izquierda: 248px, fondo blanco, borde derecho 1px `#E2E8F0`.
- Arriba de todo: link `← Configuración` (13px, peso 600, color primary `#1D4ED8`), vuelve a
  `/settings`.
- Debajo, los mismos grupos/ítems de la portada, sin íconos (solo texto — la portada ya mostró el
  ícono; repetirlo acá no suma nada nuevo, es el mismo criterio de "un dato no dos veces").
- Ítem activo: fondo `#EFF6FF`, texto primary, peso 600. Ítems inactivos: texto gris `slate-500`,
  sin fondo.
- A la derecha: cabecera de la sección (nombre 18px/700 + bajada 14px gris — **texto idéntico a la
  descripción de la tarjeta de portada, no se inventa copy nuevo**, ver §3.4) y debajo, el contenido
  existente del componente, tal cual.

### 3.4 Cabecera de sección — textos exactos **[DEFAULT: misma bajada que la tarjeta]**

| Sección | Título (18/700) | Bajada (14px, gris) |
|---|---|---|
| Agencia | Agencia | Nombre, CUIT, legajo, dirección y las cuentas donde te depositan. |
| Operativa y Caja | Operativa y Caja | Frenos de plata, avisos de deuda y las reglas del día a día. |
| Facturación | Facturación | Punto de venta, certificados de ARCA y cómo salen tus comprobantes. |
| Presupuestos y PDF | Presupuestos y PDF | Tu logo, los colores y las condiciones que salen en cada presupuesto. |
| WhatsApp Bot | WhatsApp Bot | El número conectado y los mensajes con los que atiende consultas. |
| Inteligencia artificial | Inteligencia artificial | El ayudante que sugiere textos y evita cargar cosas dos veces. |
| Workflows de aprobación | Workflows de aprobación | Qué cosas necesitan tu OK antes de salir. |
| Logs y Programación | Logs y Programación | El detrás de escena: registros del sistema y tareas programadas. |

### 3.5 Maqueta ASCII — mobile **[DEFAULT §6.1]**

Sin menú lateral (248px no entra en una pantalla de celular sin inventar un cajón nuevo — P-5). Se
entra a una sección tocando su tarjeta en la portada, y se vuelve tocando "← Configuración":

```
 ← Configuración                                 13/600 azul, siempre arriba de todo

 Facturación                                     18/700
 Punto de venta, certificados de ARCA y cómo      gris
 salen tus comprobantes.

 ┌─────────────────────────────────┐
 │                                 │
 │  ...CONTENIDO EXISTENTE DE      │
 │  AfipSettingsTab...             │
 │  (tal cual está hoy)            │
 │                                 │
 └─────────────────────────────────┘
```

Para cambiar de sección en mobile: volver a la portada y tocar otra tarjeta. **No hay un selector
nuevo, no hay menú desplegable nuevo** — es la ruta más corta que ya cumple P-5 sin inventar un
componente de navegación que nadie pidió.

---

## 4) Accesibilidad **[DEFAULT §6.4]**

- El menú lateral de la Pantalla 2 es un `<nav aria-label="Secciones de Configuración">` con una
  lista de `<Link>`; el ítem activo lleva `aria-current="page"` (no existe todavía este patrón en
  el resto de la app — se establece acá como el primero, y sirve de referencia si se necesita en
  otro menú lateral futuro).
- Las tarjetas de la portada son `<Link>` (navegación), no `<button onClick>` — toda la tarjeta es
  el área clickeable (`<Link className="block ...">` envolviendo el contenido), con foco visible.
- Foco de teclado (tarjetas y ítems del menú): `ring-2` con el token `--ring` ya definido en
  `src/TravelWeb/src/styles.css:48` (mismo azul boleto que `--primary`, ya es el estándar de foco
  de toda la app — no se inventa un color de foco nuevo), `ring-offset-2`.
- El link `← Configuración` es semánticamente un link (`<Link to="/settings">`), no un botón.

---

## 5) Botón "Guardar cambios" — regla exacta **[DEFAULT §6.3, con precisión]**

Texto firmado: la cabecera de sección lleva "botón primario **si la sección lo tiene**", y el único
ejemplo firmado es **Agencia: "Guardar cambios"**. Resolución sin contradicción con P-16 (un dato,
o acá una acción, no se repite):

- **Agencia** es hoy la única de las 8 secciones cuyo contenido vive **inline** en
  `SettingsPage.jsx` (no es un componente separado) y cuyo botón de guardar hoy está **dentro** de
  la tarjeta del formulario, no en un lugar de cabecera de página.
- Al extraer Agencia a su propio componente `AgencySettingsTab.jsx` (mismos campos, mismo orden,
  mismas etiquetas, mismas llamadas a la API — **no se toca ni un campo**, es una extracción
  mecánica, no un rediseño), el botón **"Guardar cambios"** pasa a vivir **una sola vez**, en la
  cabecera de la sección (18/700 + bajada), disparando el mismo `submit` del formulario de siempre
  (por ejemplo con `form="agency-settings-form"` en el botón y `id="agency-settings-form"` en el
  `<form>`). **Se elimina** el botón que hoy está al pie de la tarjeta "Datos de la Agencia"
  (`SettingsPage.jsx:251-259`) para que no queden dos botones de guardar en la misma pantalla.
- **Las otras 7 secciones no reciben botón en la cabecera.** Sus componentes actuales
  (`OperationalFinanceSettingsTab`, `AfipSettingsTab`, `BudgetPdfSettingsTab`, `WhatsAppBotTab`,
  `AiSettingsTab`, `ApprovalPoliciesTab`, `LogsDashboard`) ya resuelven sus propias acciones
  adentro (algunos tienen varios formularios/botones internos, otros ninguno por ser solo lectura
  como Logs) — la cabecera de esas 7 secciones muestra **solo título + bajada**, sin botón.

---

## 6) Routing — default técnico (no es UX, no se preguntó) **[DEFAULT §6, corrección de dato]**

> Corrección sobre el brief original: la ruta vieja **no** es `/configuracion?tab=`, es
> `/settings` a secas (una sola URL, sin parámetro — `App.jsx:306-309`, `activeTab` es estado local
> de React). No hay ningún `?tab=` que redirigir.

- Se **mantiene** el prefijo real `/settings` (no se inventa `/configuracion` — cambiar el prefijo
  de una ruta que ya está enlazada desde `Sidebar.jsx:157` y protegida por el permiso
  `configuracion.view` en `App.jsx` no aporta nada visible para Gastón y agranda el cambio sin
  necesidad).
- `/settings` → Pantalla 1 (Portada). Antes caía directo en el formulario de Agencia; ahora cae en
  la Portada — **ese es justamente el cambio firmado hoy**, no hace falta redirect adicional.
- `/settings/{slug}` → Pantalla 2 (Sección), con los slugs de la tabla §3.1 (`agencia`,
  `operativa-caja`, `facturacion`, `presupuestos-pdf`, `whatsapp`, `ia`, `aprobaciones`, `logs`).
  Entrar por link directo a una sección funciona (deep-link).
- Si el slug no existe o la sección no es visible para el usuario logueado (según la regla de
  §3.1/§3.2) → redirige a `/settings` (la Portada), no a "Agencia" — la Portada es ahora el punto
  de entrada por defecto de todo Configuración, no una sección en particular.
- El permiso general `configuracion.view` sigue envolviendo **todo** `/settings/*` exactamente
  igual que hoy (sin cambios en `App.jsx` más que agregar la ruta con el parámetro `:slug`).

---

## 7) Estados hover / focus **[DEFAULT §6.2]**

| Elemento | Reposo | Hover (mouse) | Foco (teclado) |
|---|---|---|---|
| Tarjeta de portada | borde `#E2E8F0`, sombra `shadow-sm` | borde `slate-300`, sombra `shadow-md`, cursor pointer (transición simple, sin mover la tarjeta de lugar) | `ring-2 ring-primary/40 ring-offset-2` (token `--ring`) |
| Ítem del menú lateral (inactivo) | texto `slate-500`, sin fondo | fondo `slate-50`, texto `slate-700` (mismo patrón que ya usa hoy la navegación de pestañas, `SettingsPage.jsx:160`) | `ring-2 ring-primary/40` hacia adentro del ítem |
| Ítem del menú lateral (activo) | fondo `#EFF6FF`, texto primary 600 | igual que reposo (ya está "prendido", no necesita más señal) | igual que inactivo |
| Link `← Configuración` | color primary, sin subrayado | subrayado | `ring-2 ring-primary/40` |

---

## 8) Qué NO hacer

1. **No tocar el contenido interno de ninguna de las 8 secciones.** Cero cambios de campos, orden,
   textos, validaciones o llamadas a API dentro de `OperationalFinanceSettingsTab`,
   `AfipSettingsTab`, `BudgetPdfSettingsTab`, `WhatsAppBotTab`, `AiSettingsTab`,
   `ApprovalPoliciesTab`, `LogsDashboard`. La única excepción es Agencia, y solo para **mover** el
   botón de guardar a la cabecera (§5) — los campos del formulario de Agencia tampoco cambian.
2. **No inventar chips nuevos.** Los únicos dos chips de portada son los de §2.4 (WhatsApp,
   Facturación), con exactamente esos textos y esas condiciones. Ninguna otra tarjeta lleva chip.
   Nunca un chip "Cargando…".
3. **Ningún modal, cajón (drawer) ni ventana flotante nuevos**, ni en desktop ni en mobile (P-5).
   La navegación es siempre entre dos pantallas normales con `Link`.
4. **No duplicar el botón de guardar de Agencia.** Es uno solo, en la cabecera (§5) — se borra el
   que hoy está al pie de la tarjeta.
5. **No poner chips en el menú lateral de la Pantalla 2.** El dato ya está completo adentro de la
   sección (P-16).
6. **No esconder ni apagar** una tarjeta o un ítem de menú que el usuario no puede ver: directamente
   no aparece (ni con candado, ni gris, ni con motivo — coherente con el resto de la app para
   permisos de sección completa).
7. **No cambiar el prefijo de ruta** `/settings` por `/configuracion` (§6) — es un cambio que no
   aporta nada visible y no fue pedido.
8. **No usar ningún color fuera de la paleta citada** en la cabecera de este documento — ni para
   los chips, ni para hover/focus, ni para nada nuevo de esta obra.

---

## 9) Preguntas para Gastón

**Ninguna.** Todo lo que no venía firmado explícitamente en el brief de hoy se resolvió arriba con
un default citando la regla de la constitución o del estándar visual que lo respalda (marcados
**[DEFAULT]**). Si al ver la pantalla real algo no le cierra, es la firma final de Gastón mirando
producción la que manda (como siempre) — no hace falta reabrir esta spec para eso, alcanza con que
lo diga y se ajusta.
