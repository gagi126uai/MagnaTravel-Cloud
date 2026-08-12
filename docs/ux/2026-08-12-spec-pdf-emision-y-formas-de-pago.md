# Spec UI — PDF de presupuesto: "Formas de pago" (ficha + plantilla) y el interruptor por persona/total

> **Fecha:** 2026-08-12 · **Autor:** agente `ux-ui-disenador` · **Para:** `frontend-senior`.
> **Antecedente directo:** `docs/ux/2026-08-12-spec-pdf-presupuesto-ui.md` (maqueta del PDF YA
> FIRMADA). Esa spec diseñó los botones "Emitir PDF" / "Enviar por WhatsApp" de la cabecera (§5,
> **NO SE REDISEÑA ACÁ**) y dejó tres huecos que esta spec cierra: dónde se edita el texto de
> "Formas de pago" por reserva, dónde vive su plantilla en Configuración, y cómo convive el
> interruptor "por persona / total del viaje" con el botón de un solo click.
> **Decisiones YA FIRMADAS por Gastón (marco, no se reabren):** #2 (texto libre por presupuesto +
> plantilla en Configuración, el motor resuelve `texto del presupuesto ?? plantilla ?? nada`) y #4
> (interruptor por emisión "por persona" default / "total del viaje").

**Reglas que esta spec respeta (citarlas en el brief de implementación):**
P-5 (fichas en línea, nunca ventana flotante) · P-6/P-7 (feedback discreto, nada se pierde si falla)
· P-9/P-10 (ícono + palabra, motivo a la vista) · P-15 (sin cartelitos aclarativos) · P-16 (un dato
no se dice dos veces) · P-21 (el sistema sugiere, no decide) · F-16 (el "⋯" es la excepción
discreta) · B.3/B.5 (molde único de botones y chips, estándar visual 2026-08-11) · **precedente
P8c** (2026-08-03, `PassengerCountsWidget`: en esta misma ficha, un dato de la reserva que el
vendedor tipea **se guarda solo, con debounce, sin botón "Guardar" y sin toast** — feedback discreto
al lado).

---

## 0. Qué se toca

1. `ReservaDetailPage.jsx` → un componente nuevo, `PaymentTermsCard`, en la solapa **Servicios**,
   visible **solo** cuando `reserva.status === "Budget"` (Presupuesto — misma condición exacta que
   ya usan las líneas 2192/2498 de ese archivo).
2. `BudgetPdfSettingsTab.jsx` → una **Card 3** nueva, "Formas de pago", debajo de las dos cards
   existentes (Identidad del PDF / Condiciones que van en el PDF).
3. `ReservaHeader.jsx` → el interruptor "por persona / total del viaje" al lado del botón "Emitir
   PDF" ya firmado. **Ver §3 — esta pieza queda con UNA pregunta para Gastón**, porque la decisión
   #4 fijó QUÉ hace el interruptor pero no CÓMO se ve; ni la guía ni ningún patrón existente cubren
   la forma de un control que cambia el resultado de una acción de un solo click.

Nada de lo firmado en la spec anterior se toca: los botones de §5 quedan exactamente como están.

---

## 1. "Formas de pago" — el texto por reserva (solapa Servicios, etapa Presupuesto)

### 1.1 Dónde vive

Dentro de la solapa **Servicios** (`activeTab === "services"`), como una card propia **después**
de `<ServiceList>` y después de la ficha de alta en línea si está abierta (`showInlineCard`). Se
muestra solo cuando `reserva?.status === "Budget"` — la misma condición que ya usan los botones
"Emitir PDF"/"Enviar por WhatsApp" (§5 de la spec anterior) y la solapa Pasajeros (línea 2489). No
aparece en Cotización (todavía no hay nada que emitir) ni en etapas posteriores a Presupuesto (el
PDF ya no se vuelve a emitir — P-9: lo que no aplica en el estado actual no se muestra).

```
  ┌─ SERVICIOS ──────────────────────────────────────────────┐
  │ Hotel Riu Cancún · 10/02 al 15/02        US$ 450 [Editar] │
  │ Traslado aeropuerto-hotel                 $ 12.000[Editar]│
  │ ──────────────────────────────────────────────────────── │
  │ TOTAL: $ 12.000 · US$ 450                                 │
  └────────────────────────────────────────────────────────────┘

  ┌────────────────────────────────────────────────────────────┐
  │ 💳  Formas de pago                                          │
  │     Así se ve en el presupuesto que recibe el cliente        │
  ├────────────────────────────────────────────────────────────┤
  │  ┌──────────────────────────────────────────────────────┐  │
  │  │ Seña del 30% al reservar. Saldo 21 días antes de la    │  │
  │  │ salida. Transferencia bancaria o efectivo en la        │  │
  │  │ agencia.                                                │  │
  │  │                                                          │  │
  │  └──────────────────────────────────────────────────────┘  │
  │                                                  Guardado ✓  │
  └────────────────────────────────────────────────────────────┘
```

Se elige este lugar y no uno nuevo porque: (a) es la misma solapa donde ya vive todo lo que define
el presupuesto (servicios, total), (b) reutiliza el slot que en Cotización ocupa el card "Pasajeros
del viaje" — un card de reserva a la vista, sin modal (P-5) — así que no es un patrón nuevo, es el
mismo molde con otro contenido, y (c) evita crear una solapa nueva por un solo campo (P-16: no
inflar la navegación por un dato).

### 1.2 Cómo se materializa la plantilla (cuándo aparece el texto precargado)

**Regla:** el textarea siempre muestra el texto que el PDF va a usar en este momento, nunca queda
vacío si hay algo para mostrar.

- Si `reserva.budgetPaymentTermsText` ya tiene un valor guardado (el vendedor ya lo tocó alguna
  vez para ESTA reserva) → se muestra ese texto. Punto, no se pisa con la plantilla.
- Si está vacío/null → al abrir la solapa se pide la plantilla de Configuración (§2) y se
  **precarga en el textarea, editable, pero SIN guardar nada todavía**. Es una previsualización de
  lo que el PDF va a mostrar si no se toca nada (el motor ya resuelve `texto ?? plantilla` — el
  front solo refleja esa misma regla para que el vendedor vea la verdad antes de emitir).
- **Se materializa (pasa a ser un valor propio de la reserva) recién cuando el vendedor escribe
  algo distinto de lo precargado** — ahí dispara el autoguardado (ver 1.3). Tocar el texto y volver
  a dejarlo idéntico a la plantilla no debería journalear un guardado de más, pero no es grave si lo
  hace (incluso quedando escrito, mañana ahí sigue el mismo contenido).
- Si la plantilla de Configuración también está vacía (Gastón nunca la cargó) y la reserva tampoco
  tiene texto propio → el textarea arranca vacío con el placeholder `Ej: Seña del 30% al reservar,
  saldo antes de la salida…`. El PDF, en ese caso, omite la sección entera (ya resuelto por el
  motor, decisión #2).

### 1.3 Cómo se guarda: autoguardado, sin botón (precedente P8c, no un patrón nuevo)

**Mismo mecanismo que `PassengerCountsWidget` en esta misma pantalla** (2026-08-03, P8c: "como el
resto del producto, el dato se guarda solo apenas el vendedor deja de tocar los casilleros — sin
botón, sin acordarse de nada"). Se aplica igual acá porque es el mismo tipo de dato (un campo de
la reserva que se edita en la propia ficha, no en un formulario de alta):

- Debounce de 600 ms después de la última tecla (no autoguarda mientras se sigue escribiendo).
- Mientras guarda: texto chico gris "Guardando…" a la derecha, debajo del textarea.
- Al terminar bien: "Guardado ✓" en verde, 2 segundos, después desaparece. **Nunca un toast** —
  interrumpiría al vendedor que puede seguir editando (mismo criterio P8c).
- Si falla el guardado: el error **sí se avisa** (alerta/`showError`, igual que el resto de la app
  — un fallo real nunca se traga en silencio, aunque el guardado en sí sea "silencioso" en el caso
  de éxito). El texto tipeado **nunca se pierde**: sigue en el textarea, se reintenta solo al
  seguir escribiendo o se puede reintentar manualmente si el patrón del componente lo permite
  (mismo criterio P-7).
- No hay botón "Guardar" en esta card (a diferencia de la Card 3 de Configuración, §2 — son
  pantallas distintas con el patrón ya establecido cada una: la ficha de reserva autoguarda, las
  pantallas de Configuración piden "Guardar cambios" explícito).

### 1.4 Después de Presupuesto

Cuando la reserva avanza a "En gestión" y siguientes, la card **desaparece** de la solapa Servicios
(no queda ni de solo lectura) — el mismo criterio que ya usan los botones "Emitir PDF"/"Enviar por
WhatsApp": si no se puede volver a emitir el PDF ahí, no tiene sentido mostrar ni editar el campo
que lo alimenta (P-9). El dato **no se borra**: sigue en la reserva por si en una tanda futura se
decide mostrarlo de solo lectura en Documentos junto al PDF ya emitido — pero eso no es parte de
esta obra (queda anotado como posible mejora futura, no como pendiente de este brief).

---

## 2. La plantilla en Configuración → Card 3 nueva en "Presupuestos y PDF"

### 2.1 Por qué una card aparte (y no un 7º bloque del acordeón de Card 2)

Card 2 ("Condiciones que van en el PDF") es un acordeón de **6 bloques por tipo de servicio**
(Aéreos, Hoteles, Traslados, Paquetes, Asistencias, Generales) — es la "letra chica" fiscal/legal
de cada tipo de producto. "Formas de pago" es un dato **distinto**: es UN solo texto, no varía por
tipo de servicio, y no es letra chica sino la explicación de cómo se cobra. Meterlo como 7º bloque
del mismo acordeón mezclaría dos conceptos que no son lo mismo (P-16 aplicado al revés: si dos
cosas son datos distintos, no comparten el mismo contenedor). Por eso: **card aparte**, mismo
molde visual que las otras dos (header con ícono + título + bajada, cuerpo, footer con "Guardar
cambios"), para no inventar un componente nuevo.

Tampoco va en Card 1 ("Identidad del PDF"): esa card es pura identidad visual (logo, color,
legajo) — "Formas de pago" es contenido, no identidad.

### 2.2 Mockup

```
┌──────── Identidad del PDF ────────┐  ┌─── Condiciones que van en el PDF ───┐
│ (Card 1 — sin cambios)             │  │ (Card 2 — sin cambios, acordeón     │
│                                     │  │  de 6 bloques)                      │
└─────────────────────────────────────┘  └────────────────────────────────────┘

┌────────────────────────────────────────────────────────────────────────────┐
│ 💳  Formas de pago                                                          │
│     Plantilla que se precarga en cada presupuesto nuevo — cada vendedor la  │
│     puede editar para SU reserva sin tocar esto de acá                      │
├────────────────────────────────────────────────────────────────────────────┤
│  ┌────────────────────────────────────────────────────────────────────┐   │
│  │ Seña del 30% al reservar. Saldo 21 días antes de la salida.          │   │
│  │ Transferencia bancaria o efectivo en la agencia.                     │   │
│  └────────────────────────────────────────────────────────────────────┘   │
│  ✨ Ayudame a redactarlo                                                    │
├────────────────────────────────────────────────────────────────────────────┤
│                                                       [ Guardar cambios ]   │
└────────────────────────────────────────────────────────────────────────────┘
```

- Card **de ancho completo** (`lg:col-span-2` sobre la grilla `grid-cols-2` que ya usan Card 1/2),
  debajo de las otras dos — es una sola card, no tiene sentido angostarla a la mitad y dejar un
  hueco vacío al lado.
- Un solo textarea (sin acordeón: es un solo bloque, no seis).
- El link "✨ Ayudame a redactarlo" **igual al de Card 2** (mismo componente/criterio, discreto,
  P-21: cae en el textarea, nunca se guarda solo).
- Botón "Guardar cambios" al pie, **igual al patrón ya usado en Card 1 y Card 2** de esta misma
  pantalla — Configuración SÍ pide guardado explícito (a diferencia de la ficha de reserva, §1.3).
  Es la misma pantalla, dos patrones distintos y ya cada uno tiene su propio precedente firme; no
  hace falta unificarlos.

### 2.3 Contrato asumido con el motor (a confirmar con `backend-dotnet-senior`, no es una decisión de UX)

- `GET /reports/budget-payment-terms-template` → `{ text }`
- `PUT /reports/budget-payment-terms-template` → `{ text }`
- `POST /reports/budget-payment-terms-template/draft` → `{ currentText }` → `{ text }` (mismo
  patrón que ya usa `/reports/budget-conditions/{kind}/draft`)
- La reserva expone/acepta `budgetPaymentTermsText` en el endpoint de actualización que ya existe,
  con **anti-clobber** (si el campo no viaja en el PATCH/PUT, no se toca — igual criterio que ya
  pide la decisión #2 firmada).

Estos nombres son una propuesta para mantener consistencia con los endpoints ya construidos de
Card 2; el backend en construcción manda si difiere.

---

## 3. El interruptor "por persona / total del viaje" al emitir

### 3.1 Lo que está firmado (decisión #4, no se reabre)

El interruptor existe, es **por emisión** (no es una configuración fija de la reserva), y el
default es **"por persona"**. Lo que NO está resuelto es la forma: ni la guía, ni el estándar
visual 2026-08-11, ni ningún patrón ya construido definen cómo se ve un control que cambia el
resultado de un botón de un solo click sin abrir ventana (P-5) y sin desarmar la fila de 3 botones
ya firmada en §5 de la spec anterior. Por eso esta única pieza queda con una pregunta (§4) en vez
de un default — según la instrucción de este brief, se prefiere preguntar antes que inventar la
forma de un control que Gastón ya nombró explícitamente como "interruptor".

### 3.2 Mi recomendación (opción A de la pregunta)

Un interruptor chico, real (dos estados, un click cambia), pegado inmediatamamente a la izquierda
de "Emitir PDF", usando el **molde de chip que ya existe** (B.5: 24 px de alto, redondo, borde
fino) en vez de inventar un componente de switch nuevo — es más angosto que cualquiera de los tres
botones de la fila (respeta B.3 regla 3: "nunca un secundario ocupa más área que el principal"), y
queda siempre a la vista (el vendedor ve QUÉ va a emitir antes de tocar el botón, sin sorpresas).

```
  ⦗ Por persona ⇄ ⦘   ┌───────────┐   ┌──────────────────┐   │  Perdida  Archivar  ⋯
                       │ Emitir PDF│   │ Enviar por        │   │
                       └───────────┘   │ WhatsApp          │   │
                        outline         └──────────────────┘
```

Un click sobre el chip cambia el texto a "Total del viaje ⇄" (mismo chip, se invierte el estado);
el siguiente click en "Emitir PDF" usa ese formato. No persiste entre visitas — cada vez que se
abre la ficha vuelve a "Por persona" (el default firmado), así nunca sorprende con un formato viejo
elegido semanas atrás.

---

## 4. PREGUNTAS PARA GASTON

### Tema: cómo se ve el interruptor "por persona / total del viaje" al lado de "Emitir PDF"
Contexto: ya firmamos que existe ese interruptor y que el default es "por persona" (decisión #4).
Lo que falta decidir es la FORMA: cómo se ve y dónde toca para cambiarlo, sin abrir ninguna
ventana y sin desarmar la fila de botones "Emitir PDF" / "Enviar por WhatsApp" que ya quedó firmada.

**P1. Cuando el vendedor quiere mandar el presupuesto con el TOTAL del viaje (no por persona), ¿cómo lo cambia?**

  A) **Un interruptor chico y visible, al lado de "Emitir PDF"** (mi recomendación). Se ve siempre,
     un click lo cambia, vuelve solo a "Por persona" la próxima vez que se abre la reserva.
     ```
     ⦗ Por persona ⇄ ⦘   [ Emitir PDF ]   [ Enviar por WhatsApp ]
     ```

  B) **Escondido en el menú "⋯"** (el mismo de "Perdida / Archivar / ⋯"). "Emitir PDF" queda
     siempre por persona; para el total hay una opción aparte adentro del menú.
     ```
     [ Emitir PDF ]   [ Enviar por WhatsApp ]   │  Perdida  Archivar  ⋯
                                                 │           ┌─────────────────────────┐
                                                 │           │ 🧾 Emitir con el total   │
                                                 │           │    del viaje             │
                                                 │           └─────────────────────────┘
     ```

  C) **Una flechita pegada al propio botón "Emitir PDF"** que despliega las dos opciones justo
     debajo (sin ventana, se cierra solo al elegir).
     ```
     [ Emitir PDF ▾ ]
        ├ Por persona (elegido)
        └ Total del viaje
     ```
