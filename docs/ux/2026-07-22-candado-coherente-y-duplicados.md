# Candado coherente + limpieza de duplicados — especificación

**Fecha:** 2026-07-22
**Autor:** ux-ui-disenador
**Estado:** SPEC del tratamiento visual del candado (C1/C2/C4) LISTA para implementar +
4 preguntas de forma sobre los duplicados (esperan la firma de Gastón, de a una).
**Fuente de las decisiones de fondo:** `docs/architecture/2026-07-22-matriz-candado-decisiones-gaston.md`
(C1, C2, C3, C4 ya firmadas — NO se reabren acá).

> **No choca con la otra obra en curso.** La spec `docs/ux/2026-07-22-tratamiento-unico-avisos-bloqueo.md`
> (firmada) manda los **carteles largos de RECHAZO** a una ventana emergente única ("Cartel
> emergente"). Esta spec es distinta: acá el candado NO tira un cartel largo — apaga botones y ya
> tiene su propia franja fina. La ventana de destrabar (`EditAuthorizationModal`) es una ventana de
> trabajo, no un cartel de rechazo. Y los frenos fiscales que aparezcan DESPUÉS de destrabar (al
> guardar un servicio, al anular) siguen yendo al "Cartel emergente" de esa otra spec. Cero
> superposición.

---

## 0. La idea en una frase

Hoy el candado de una reserva Confirmada **se anuncia pero no frena**: la franja dice "pedí
autorización", pero los botones de editar siguen encendidos y funcionan igual (bug real, reserva 1052).
Esta obra hace que **el candado se vea Y frene, parejo en toda la ficha**: los botones que tocan la
reserva se **apagan con un candadito**; al tocarlos se abre la ventana de destrabar; cuando la reserva
queda **destrabada** (autorización viva de 30 minutos) se **prenden solos**.

---

## 1. El patrón visual del candado (aplica a C1, C2 y C4)

### 1.1 Las tres situaciones de un botón (patrón Fase 4, sin inventar nada nuevo)

Se reusa **exactamente** el criterio ya firmado (Fase 4 / ADR-035, guía 2026-06-19):

| Situación de la reserva | Cómo se ve el botón | Qué pasa al tocarlo |
|---|---|---|
| **No aplica** (estado terminal: Perdida, Anulada, Finalizada, En viaje) | **escondido** (como hoy) | — |
| **Confirmada + candado** (bloqueada, sin autorización viva) | **apagado (gris) + candadito 🔒**, con la palabra a la vista | abre la **ventana de destrabar** (`EditAuthorizationModal`) |
| **Confirmada + destrabada** (`hasLiveEditAuthorization = true`) | **encendido normal** | hace su acción de siempre |

En criollo: **escondido = "acá no va"; gris con candadito = "se puede, pero primero destrabá"; normal =
"dale".** Es la misma lógica de siempre; lo único nuevo es que el escalón del medio (gris con candadito)
ahora **también** se aplica a estos botones, que hoy se saltean el candado.

### 1.2 Reglas de estilo que ya están firmadas y se respetan (NO se rompen)

- **(2026-06-08) La palabra va SIEMPRE al lado del icono. Nada de tooltip.** Los botones apagados
  **siguen mostrando su texto** ("Editar fechas", "Anular", "Confirmar costo"): solo se ponen grises y
  se les suma el candadito 🔒. El candadito lleva su etiqueta accesible para lector de pantalla
  ("Bloqueado — pedí autorización"), como manda la regla de accesibilidad — pero **no** hay tooltip de
  hover ni cartelito de motivo colgando del botón.
- **(ADR-035, 2026-06-19) Botón apagado = gris sobrio, SIN texto de motivo debajo; el motivo vive en UN
  solo cartel arriba.** Ese "cartel único arriba" **ya existe**: es la **franja del candado**
  (`ReservaLockBanner`). No se agrega motivo por botón. Un solo lugar dice por qué está trabado.

> **Aclaración sobre el brief:** el pedido original hablaba de "tooltip con motivo corto" en cada botón.
> Eso **choca de frente** con dos reglas ya firmadas (no-tooltip 2026-06-08 + sin-motivo-por-botón
> ADR-035). Como la guía **sí** cubre esto, se aplica la guía: **el candadito sin tooltip visible; el
> motivo, una sola vez, en la franja de arriba.** No es una decisión mía: es la regla escrita.

### 1.3 La franja del candado (ya existe — no se toca, solo se confirma su rol)

`ReservaLockBanner` ya tiene las tres variantes de una línea (spec 2026-07-05, 4B), y **la franja verde
"destrabada" YA EXISTE** (`reserva-unlocked-banner`: "Destrabada hasta las HH:MM. Podés hacer cambios
ahora; después vuelve a bloquearse sola."). Esta obra **no cambia la franja**: solo hace que los botones
la obedezcan.

- **Bloqueada** → franja **ámbar**: "Reserva confirmada (con candado). Podés destrabarla para editar."
  (Admin) / "Reserva confirmada. Para cambiar algo, pedí autorización." (vendedor) + botón a la derecha.
- **Destrabada** → franja **verde**: "Destrabada hasta las HH:MM…".

### 1.4 La transición bloqueada → destrabada (paso a paso)

```
  [Reserva Confirmada, con candado]
        │
        │  el vendedor/admin toca un botón gris con candadito  (o el botón de la franja ámbar)
        ▼
  ┌───────────────────────────────────────┐
  │  🔒  Reserva bloqueada          [ ✕ ] │   ← EditAuthorizationModal (YA EXISTE, no cambia)
  │  Admin: escribe el motivo (mín 10)    │
  │  Vendedor: "Pedile a un admin…"       │
  │              [ Cancelar ] [ Desbloquear reserva ]
  └───────────────────────────────────────┘
        │  admin confirma → POST edit-authorizations (30 min)
        ▼
  Toast: "Reserva desbloqueada por 30 minutos. Hacé los cambios ahora."
  La ficha se refresca →  franja pasa de ÁMBAR a VERDE
                          TODOS los botones grises se prenden a la vez
        │
        │  (pasan 30 min, o se refresca sin autorización viva)
        ▼
  Vuelve sola a bloqueada: franja ÁMBAR, botones grises otra vez.
```

Punto fino para frontend-senior: hoy `hasLiveEditAuthorization` ya viaja en el DTO **pero ningún botón
la lee** (matriz). El cambio es que cada botón de la lista de C1/C2/C4 decida su estado así:
`si (capacidad-permitida) y (isLocked) y (!hasLiveEditAuthorization) → gris+candadito, click abre el
modal; si (hasLiveEditAuthorization) → encendido normal`. Si la capacidad no aplica por estado terminal,
**escondido como hoy**.

### 1.5 Mockups ASCII — bloqueada vs destrabada

**Botones de fecha (cabecera de la reserva):**

```
 BLOQUEADA (ámbar):
   Salida: 12/06  ·  Regreso: 20/06    [ 🔒 ✏ Editar fechas ]  [ 🔒 ⏩ Reprogramar viaje ]
                                          (gris apagado)          (gris apagado)
   ┌──────────────────────────────────────────────────────────────────────────┐
   │ 🔒 Reserva confirmada (con candado). Podés destrabarla.  [ Destrabar reserva ]│
   └──────────────────────────────────────────────────────────────────────────┘

 DESTRABADA (verde):
   Salida: 12/06  ·  Regreso: 20/06    [ ✏ Editar fechas ]  [ ⏩ Reprogramar viaje ]
                                          (encendido)          (encendido)
   ┌──────────────────────────────────────────────────────────────────────────┐
   │ ✅ Destrabada hasta las 15:20. Podés hacer cambios ahora.                    │
   └──────────────────────────────────────────────────────────────────────────┘
```

**Fila de un servicio (lista de servicios):**

```
 BLOQUEADA:
   Hotel Maitei · 3 noches · $ 205.000      [ 🔒 ✏ Editar ]  [ 🔒 🗑 Anular ]
                                              (gris)           (gris)
   Confirmar costo:  $ 180.000   [ 🔒 Confirmar costo ] (gris)

 DESTRABADA:
   Hotel Maitei · 3 noches · $ 205.000      [ ✏ Editar ]  [ 🗑 Anular ]
   Confirmar costo:  $ 180.000   [ Confirmar costo ]
```

### 1.6 Botón por botón (qué entra a este tratamiento)

| Botón / acción | Decisión | Bloqueada | Destrabada |
|---|---|---|---|
| **Editar fechas** (cabecera) | C1 | gris + 🔒 → modal | normal |
| **Reprogramar viaje** (cabecera) | C1 | gris + 🔒 → modal | normal |
| **Agregar servicio** | C1 | gris + 🔒 → modal | normal |
| **Editar servicio** (lápiz, por fila) | C1 | gris + 🔒 → modal | normal |
| **Borrar / Anular servicio** (tacho, por fila) | C1 + C2 | gris + 🔒 → modal | normal |
| **Anular varios servicios** | C2 | gris + 🔒 → modal | normal |
| **Editar identidad de un pasajero ya cargado** | C1 | gris + 🔒 → modal | normal |
| **Eliminar pasajero** | C1 | gris + 🔒 → modal | normal |
| **Confirmar costo** (por fila) | C4 | gris + 🔒 → modal | normal |

**Quedan LIBRES bajo candado (C3/C4/ratificados — no se apagan nunca por el candado):** Anular la
**reserva** entera · Cobrar · Facturar · Emitir voucher · emitir/anular comprobantes · marcar
confirmado/emitido por el operador · **agregar** un pasajero y completar una identidad **vacía** ·
**adjuntar documentos** · **asignar/repartir pasajeros** a los servicios ("Para: Todos / 2 de 3").

### 1.7 Convivencia con el chip fiscal "🔒 {motivo}" del tacho (no confundir dos candados)

Hay dos "candados" distintos y **no** deben pelearse en el mismo botón:

- **Candado de la reserva** (esta obra): apaga TODOS los botones de edición cuando la reserva está
  Confirmada sin autorización viva. Se destraba con la ventana.
- **Freno fiscal por servicio** (`aviso-bloqueo-anular`, el chip "🔒 {motivo}" al lado del tacho): es
  OTRA cosa — un servicio puntual que no se puede anular por regla fiscal (ej. ya pagado sin factura), y
  **sigue trabado aunque destrabes la reserva**. Ese chip **queda como está** (en línea, siempre
  visible; así lo confirma la spec de avisos únicos).

**Regla de convivencia (para que no haya doble señal en el mismo botón):** con la reserva **bloqueada**,
manda el candado de la reserva → el tacho va **gris + candadito de reserva** y NO se muestra el chip
fiscal (todavía no aplica: primero hay que destrabar). Recién con la reserva **destrabada**, si ese
servicio igual no se puede anular por lo fiscal, aparece su chip "🔒 {motivo}" al lado del tacho, como
hoy. Un candado a la vez por botón.

---

## 2. Los duplicados

### 2.1 Resueltos sin preguntar (la guía o la lógica alcanzan)

**(b) Papelera por fila vs "Anular varios servicios" → CONVIVEN, no se tocan.**
No son lo mismo aunque por dentro usen el mismo endpoint: uno anula **un** servicio puntual (el tacho de
su fila); el otro es para anular **muchos de una** con un solo motivo (como "seleccionar varios" en el
correo). Sacar cualquiera de los dos le quita una comodidad real al vendedor. **Recomendación firme:
quedan los dos.** Lo único a ordenar es la palabra repetida — eso entra en P2.

### 2.2 Necesitan la firma de Gastón (la guía no lo cubre) → ver PREGUNTAS

- **(a)** "Anular reserva" aparece **dos veces** con el mismo botón (cabecera + barra de Estado de
  Cuenta) → P1.
- **(c)** tres botones con la palabra "Anular" a centímetros → P2.
- **(d)** "Editar fechas" y "Reprogramar viaje", juntos y parecidos → P3.
- **(e)** el candado anunciado por tres canales a la vez → P4.

---

## 3. Tabla de migración (elemento de hoy → cómo queda)

| Elemento de hoy | Hoy | Cómo queda |
|---|---|---|
| Botón **Editar fechas** en Confirmada+candado | encendido (bug: ignora el candado) | **gris + 🔒**, click abre la ventana de destrabar; destrabada → normal |
| Botón **Reprogramar viaje** en Confirmada+candado | encendido (ignora candado) | **gris + 🔒** → destrabar; destrabada → normal |
| Botón **Agregar servicio** en Confirmada+candado | encendido | **gris + 🔒** → destrabar |
| **Editar** (lápiz) por fila, en candado | encendido | **gris + 🔒** → destrabar |
| **Borrar / Anular** (tacho) por fila, en candado | encendido (bypass real, C2) | **gris + 🔒** → destrabar; luego corren los frenos fiscales de siempre |
| **Anular varios servicios**, en candado | encendido (bypass real, C2) | **gris + 🔒** → destrabar |
| **Editar identidad** / **Eliminar pasajero**, en candado | encendido | **gris + 🔒** → destrabar |
| **Confirmar costo** por fila, en candado | encendido (C4) | **gris + 🔒** → destrabar |
| **Agregar pasajero** / completar identidad **vacía**, en candado | encendido | **queda encendido** (exención anti-callejón) |
| **Adjuntar documentos** / **repartir pasajeros**, en candado | encendido | **queda encendido** (C4: trabajo diario sin plata) |
| **Anular la reserva** entera, en candado | encendido | **queda encendido** (C3: circuito propio, no es edición) |
| **Cobrar / Facturar / Voucher / comprobantes**, en candado | encendido | **queda encendido** (trabajo normal de una Confirmada) |
| **Franja del candado** (`ReservaLockBanner`) | ya existe, 3 variantes de una línea | **sin cambios**; ahora los botones la obedecen |
| **Ventana de destrabar** (`EditAuthorizationModal`) | ya existe | **sin cambios**; ahora la abren también los botones grises |
| Chip fiscal **"🔒 {motivo}"** del tacho (`aviso-bloqueo-anular`) | en línea, siempre visible | **sin cambios**; solo se muestra con la reserva ya destrabada (§1.7) |
| Segundo botón **"Anular reserva"** duplicado | 2 botones, mismo `data-testid` | **según P1** |
| Palabra "Anular" repetida en 3 botones | 3× "Anular…" | **según P2** |
| **Editar fechas** + **Reprogramar viaje** sueltos | 2 botones juntos | **según P3** |
| 3 canales del candado (candadito en el estado + franja + botones grises) | los 3 a la vez | **según P4** |

---

## 4. Qué NO hacer

- **No** dejar ningún botón de edición encendido bajo candado "porque total avisa la franja": el pedido
  es que **frene**, no solo que avise.
- **No** ponerle tooltip de hover ni cartelito de motivo a cada botón gris: el motivo vive **una sola
  vez** en la franja (ADR-035 + no-tooltip 2026-06-08).
- **No** esconder los botones bajo candado: **gris con candadito**, no invisibles (escondido es solo para
  "no aplica" en estados terminales).
- **No** tocar la franja del candado ni la ventana de destrabar: ya están firmadas y funcionan.
- **No** mandar el candado al "Cartel emergente" de la otra obra: el candado no es un rechazo largo, es
  botones apagados + su franja.
- **No** inventar palabras nuevas para "Anular": el vocabulario está firmado (Anular = dejar sin efecto
  con NC). Cualquier renombre usa esa palabra + el sustantivo del alcance.

---

## PREGUNTAS PARA GASTON

> Cuatro decisiones de **forma** (no de fondo: el fondo del candado ya lo firmaste). Son cortas y las
> podés contestar de a una ("1A, 2B…").

### Tema: el botón "Anular reserva" aparece dos veces
Contexto: hoy el mismo botón "Anular reserva" está en dos lugares de la misma ficha (arriba, junto al
estado; y abajo, en la barra de Estado de Cuenta). Los dos hacen exactamente lo mismo. Conviene dejar uno.

> **✅ RESUELTO por Gaston el 2026-07-22 (con corrección de rumbo):** Gaston
> frenó las podas no pedidas — "no entiendo por qué querés sacar botones donde
> nadie te dijo eso". Decisiones finales:
> **P1 → el botón "Anular reserva" QUEDA EN LOS DOS LADOS** (arriba y Estado
> de Cuenta; no se saca nada).
> **P2 → SÍ se aclaran los nombres**: "Anular reserva" · "Anular varios
> servicios" · "Anular servicio" (solo cambia el texto, nada se agrega ni saca).
> **P3 y P4 → QUEDAN COMO ESTÁN** (no se preguntaron más: eran propuestas del
> diseñador, no pedidos del dueño; si algún día molestan, se retoman).
> Regla de proceso aprendida: las observaciones de Gaston no se convierten en
> podas sin confirmar el alcance con él.

**P1. ¿Dónde dejamos el único "Anular reserva"?**

  **A) Solo en la barra de Estado de Cuenta** (donde se abre el panel de anular y viven la factura y la
  nota de crédito). *(recomendada — el botón queda pegado a donde pasa la acción)*
```
   [ Cobrar ]  [ Facturar ]  [ 🚫 Anular reserva ]
   └───────────── barra de Estado de Cuenta ─────────────┘
```
  **B) Solo arriba, junto al estado de la reserva** (siempre a la vista, entres a la solapa que entres).
```
   Reserva #1052  [CONFIRMADA]      … [ 🚫 Anular reserva ]
```

---

### Tema: la palabra "Anular" repetida en tres botones cercanos
Contexto: en la misma pantalla conviven "Anular reserva" (toda la reserva), "Anular varios servicios"
(varios de una) y, en cada fila, "Anular" (ese servicio). Tres veces la misma palabra a centímetros
puede confundir. No inventamos palabras nuevas (Anular = dejar sin efecto): solo aclaramos de un vistazo
QUÉ anula cada uno.

**P2. ¿Le agregamos a cada uno el "de qué" para distinguirlos?**

  **A) Sí, cada botón dice su alcance:** "Anular reserva" · "Anular varios servicios" · y el de la fila
  pasa de "Anular" a **"Anular servicio"**. *(recomendada — misma palabra firmada, pero se lee de un
  vistazo qué toca cada uno)*
```
   Arriba:  [ Anular reserva ]
   Lista:   [ Anular varios servicios ]        Fila:  [ 🗑 Anular servicio ]
```
  **B) Se quedan como están** ("Anular reserva" / "Anular varios" / "Anular"): el contexto (arriba, la
  lista, la fila) ya alcanza para saber cuál es cuál.

---

### Tema: "Editar fechas" y "Reprogramar viaje" juntos y parecidos
Contexto: son dos cosas distintas pero suenan casi igual. "Editar fechas" cambia a mano las fechas de la
tapa de la reserva; "Reprogramar viaje" corre TODAS las fechas de los servicios de una, desde una nueva
salida. Puestos uno al lado del otro, se confunden.

**P3. ¿Cómo los ordenamos?**

  **A) Un solo botón "Fechas del viaje ▾"** que al tocarlo abre dos opciones claras. *(recomendada —
  ocupa menos y obliga a elegir con la explicación al lado)*
```
   [ 📅 Fechas del viaje ▾ ]
        ├─ Editar fechas (cambiar salida/regreso a mano)
        └─ Reprogramar viaje (correr todos los servicios juntos)
```
  **B) Se quedan los dos botones separados**, como hoy, uno al lado del otro.
```
   [ ✏ Editar fechas ]   [ ⏩ Reprogramar viaje ]
```

---

### Tema: el candado se anuncia por tres lados a la vez
Contexto: cuando la reserva está trabada, hoy se avisa por tres canales: (1) un candadito 🔒 chiquito
al lado del estado, arriba; (2) la franja ámbar "Reserva confirmada… pedí autorización"; y (3) ahora,
con esta obra, los botones grises con candadito. Puede ser demasiado.

**P4. ¿Dejamos los tres o sacamos alguno?**

  **A) Dejamos los tres** — cada uno cumple un rol distinto: el candadito del estado es la "etiqueta" de
  que está trabada, la franja te dice qué hacer para destrabar, y los botones grises te muestran
  exactamente qué no podés tocar todavía. *(recomendada — no se pisan, se complementan)*
  **B) Sacamos el candadito chiquito del estado** y quedan solo la franja + los botones grises (el
  candadito del estado repetía lo que ya dice la franja).
```
   A)  #1052 [CONFIRMADA 🔒]  +  franja ámbar  +  botones grises
   B)  #1052 [CONFIRMADA]     +  franja ámbar  +  botones grises
```

---

> **Al recibir las respuestas:** actualizo `docs/ux/guia-ux-gaston.md` (sección "Botones y acciones" con
> el patrón del candado gris+candadito, y "Ventanas emergentes y avisos" si P4 toca la franja), y esta
> spec queda cerrada para que la tome frontend-senior. El tratamiento visual (§1) ya está listo para
> implementar sin esperar P1–P4; las preguntas son solo de forma de los duplicados.
