# T4 del rediseño de multas (ADR-044) — pantallas y flujos

> **Qué es esto:** la especificación de diseño de las 5 pantallas/flujos de la Tanda 4 de
> ADR-044 (paso de multa por operador pulido + monitor de comprobantes + fin de la bandeja).
> Autor: agente `ux-ui-disenador`. Fecha: 2026-07-10.
>
> **Regla del dueño:** acá SOLO se especifica lo que la guía (`docs/ux/guia-ux-gaston.md`) ya
> cubre. Todo lo que la guía NO cubre quedó como **preguntas para Gastón** (bloque separado al
> final de este documento y relayado al orquestador). **Nada de esto se construye hasta que
> Gastón responda las preguntas marcadas.** Las partes marcadas **[FIRME]** salen de reglas ya
> respondidas y se pueden construir; las marcadas **[PROVISORIO – Pn]** dependen de una
> respuesta.
>
> **Componentes reales tocados:** `ConfirmarMultaOperadorInline.jsx`, `OperatorPenaltyStepPanel.jsx`,
> `operatorPenaltyBanner.js`, `SupplierExtractoSection.jsx`, `PendientesAfipPage.jsx` +
> `resolveInitialTab.js`, `Sidebar.jsx`, y la página global `/facturacion`.

---

## 0. Reglas de la guía que atraviesan TODAS estas pantallas (aplico tal cual)

Estas ya están respondidas por Gastón; se aplican sin volver a preguntar:

- **Todo EN LÍNEA, jamás ventana flotante** (regla dura "el modal me parece horrible", 2026-06-09
  P3 / ADR-035 C). El único "¿seguro?" que sí es ventanita es el paso irreversible previo a algo
  fiscal (patrón H2 2026-06-24).
- **La complejidad se esconde con defaults; el caso simple NO pregunta nada** (2026-07-08). El
  "tipo de cargo" NO aparece en el camino normal: por dentro va el default = multa del operador
  trasladada 1:1 al cliente. Lo avanzado es acción secundaria separada y clara.
- **La resolución vive en la FICHA; las bandejas son listas pasivas** (fila = link a la ficha, sin
  botones propios) (2026-07-08).
- **Multimoneda, 3 reglas duras** (2026-06-09): monedas siempre separadas, nunca sumadas; **la
  palabra "diferencia de cambio" NUNCA aparece**; si hay una sola moneda se ve como hoy. Recuadro
  de tipo de cambio **solo cuando el cargo cruza de moneda** (P4).
- **Enmascarado de costos** (`cobranzas.see_cost`): quien no puede ver costos ve "—", nunca montos
  de multa/costo/deuda al operador (2026-06-05 / 2026-07-03 P6).
- **8 reglas de voz** (2026-07-08): en los avisos, "nota de débito" → "multa" o "cargo de la
  agencia", "nota de crédito" → "devolución"; sin CAE/RG/jerga/estados en inglés/IDs internos;
  el número siempre es F-2026-xxxx; rioplatense, corto. **Los términos fiscales (nota de crédito,
  CAE, factura B) SÍ se permiten en las pantallas de facturación**, no en la campanita.
- **Anular ≠ Cancelar**: los carteles de la anulada dicen "Anulada", nunca "Cancelada".

---

## 1. Pantalla — "Agregar otro cargo" del operador en la ficha de una anulada

### 1.1 El caso simple NO cambia [FIRME]

El 100% de las anulaciones de hoy son de UN cargo (la multa del operador trasladada 1:1). Eso ya
lo resuelve `ConfirmarMultaOperadorInline` y **queda exactamente como está** (aprobado 2026-07-08):
monto + moneda (precargada de la factura de la reserva, editable) + fecha + referencia, **sin
pregunta de tipo de cargo**. Nada de esta tanda toca ese panel en el caso de un solo cargo y una
sola factura.

> Por dentro, el backend (ADR-044 T2) crea UNA charge `Kind=cargo administrativo`,
> `CollectionMode=retenido`, transparente. El usuario no ve ni elige nada de eso.

### 1.2 La acción secundaria "Agregar otro cargo de este operador" [FIRME — P1=A, P2=A, P3=A, P4=A]

Cuando el mismo operador aplica **dos cosas a la vez** sobre la misma anulación (el caso real que
el contador confirmó: un cargo administrativo **y** una retención fiscal), hace falta cargar un
segundo cargo. Eso es **acción secundaria, escondida y opcional**: quien no la usa ni se entera.

**Dónde aparece (P1=A):** un **link discreto** **debajo del cargo ya confirmado del operador**,
dentro del bloque de multa de la ficha. Copy: **"+ Agregar otro cargo de este operador"**. Gateado
por el mismo permiso que confirmar la multa. Si el operador no tiene ningún cargo confirmado
todavía, el link NO aparece (primero se confirma el cargo principal por el camino simple).

**Cómo se ve al abrirlo (P2=A, P3=A, P4=A):** una ficha en línea, debajo, con SOLO lo imprescindible
a la vista (tipo de cargo + monto + moneda) y lo avanzado detrás de **"Más detalles"** cerrado por
defecto (regla 2026-06-05):

```
┌─ Otro cargo de este operador — Reserva F-2026-1042 ───────────── [x] ─┐
│                                                                        │
│  Tipo de cargo *          [ Retención fiscal        ▾ ]                │
│      cargo administrativo · impuesto · retención fiscal · otro         │
│                                                                        │
│  Monto *          Moneda *                                             │
│  [  25.000    ]   [ Pesos (ARS) ▾ ]                                    │
│                                                                        │
│  ▸ Más detalles  (cómo lo cobra · traslado al cliente · documento)     │
│                                                                        │
│                                   [ Volver ]  [ Agregar el cargo ]     │
└────────────────────────────────────────────────────────────────────────┘
```

**Dentro de "Más detalles" (recomendación, sujeta a P3/P4):**

```
┌─ Más detalles ─────────────────────────────────────────────────────────┐
│  ¿Cómo lo cobra el operador?                                            │
│   (•) Lo descuenta de lo que te va a devolver   ( ) Te lo factura aparte │
│                                                                          │
│   ── si eligió "Te lo factura aparte": ──                               │
│   Documento del operador *  [ Nº de nota / adjunto............ ]         │
│                                                                          │
│  ¿Qué pasa con el cliente?                                              │
│   (•) Se le traslada tal cual   ( ) + un cargo de gestión   ( ) Lo      │
│                                        [ $____ ]              absorbe    │
│                                                                la agencia │
└──────────────────────────────────────────────────────────────────────────┘
```

**Recuadro de tipo de cambio — SOLO si el cargo cruza de moneda [FIRME]:** si la moneda del cargo
(ej. USD) es distinta de la moneda de la factura del cliente al que se le traslada (ej. ARS),
aparece **debajo**, dentro de la misma ficha, el recuadro de TC que ya usa el cobro cruzado
(2026-06-09 P4 / 2026-06-19 B): tipo de cambio (1 US$ = $___), fuente, fecha, y la línea de cuánto
se le carga al cliente. Los tres datos son obligatorios. **Nunca aparece la palabra "diferencia de
cambio"** (regla dura #2).

**Qué tipo de cambio manda (decisión de negocio de Gastón, 2026-07-10):** el TC del cargo al cliente
es **el del día en que el operador cobró la multa** (no el del día en que se emite el cargo al
cliente). El rótulo del recuadro lo refleja sin jerga — texto: **"Tipo de cambio del día que el
operador cobró"**. La fecha por defecto del recuadro es la fecha en que el operador informó/cobró la
multa (la misma que ya se carga en el paso de la multa), editable.

**Estados de la ficha:**
- **Vacío/inicial:** monto en blanco, moneda precargada con la de la factura, tipo de cargo con el
  primer valor de la lista; botón "Agregar el cargo" apagado hasta que el monto sea > 0.
- **Cargando (al guardar):** botón con spinner y texto "Agregando…"; campos deshabilitados.
- **Error recuperable:** cartel rojo arriba de los botones, la ficha queda con TODO lo cargado
  intacto, se reintenta en el mismo botón (regla Ronda 2, 2026-06-06). Nunca se pierde lo cargado.
- **Éxito:** la ficha se cierra y el nuevo cargo aparece como una línea más en el bloque de multa
  del operador (misma familia visual), con su tipo en español.

> **Por qué esto va a preguntas y no lo decido yo:** el "tipo de cargo" es EXACTAMENTE lo que
> Gastón rechazó del viejo `ConfirmPenaltyModal` ("me pedía un tipo de cargo que no entiendo").
> Acá reaparece —legítimamente, porque el contador confirmó que fee + retención conviven— pero en
> una acción secundaria y escondida. Cómo se ve, con qué palabras, y qué va a la vista vs. detrás
> de "Más detalles" **lo tiene que mirar y aprobar Gastón** (P1–P4).

---

## 2. Pantalla — elegir la factura destino cuando hay 2+ facturas activas

### 2.1 Caso simple: 1 factura → nada visible [FIRME]

Si la reserva tiene una sola factura activa (el 95%+ de los casos), el sistema la usa sola, sin
mostrar ningún desplegable (default transparente, principio 2026-07-08). El panel de multa se ve
igual que hoy.

### 2.2 Caso 2+ facturas: desplegable al confirmar el cargo [FIRME — P5=A]

Cuando la reserva tiene 2 o más facturas activas (típico: una en $ y una en US$), al confirmar la
multa (o cargar otro cargo) el usuario tiene que decir **a qué factura del cliente corresponde**
ese cargo — es un dato que el humano conoce y el sistema no puede adivinar sin riesgo fiscal
(ADR-044 T3b, Decisión 1).

El desplegable reusa el formato de lista de facturas ya aprobado (2026-07-01, anulación
multifactura): número + moneda + monto de cada una.

```
┌─ Confirmar multa del operador — Reserva F-2026-1042 ─────────── [x] ─┐
│  ...(monto, moneda, fecha, referencia como hoy)...                   │
│                                                                       │
│  ¿A qué factura del cliente corresponde? *                            │
│  [ Factura B 0001-00012345 — US$ 200            ▾ ]                   │
│      · Factura B 0001-00012345 — US$ 200                              │
│      · Factura B 0001-00012346 — $ 150.000                            │
│                                                                       │
│              [ Volver ]  [ Confirmar y cobrarle la multa al cliente ] │
└───────────────────────────────────────────────────────────────────────┘
```

- El desplegable **solo aparece cuando hay 2+ facturas activas**. Con una sola, no se ve (2.1).
- Botón de confirmar apagado hasta elegir factura.
- **Términos fiscales permitidos acá** (es pantalla de facturación de la ficha, no un aviso): se
  puede decir "Factura B 0001-...".

### 2.4 El renglón de la multa en el comprobante del pasajero nombra al mayorista [FIRME]

Decisión de negocio de Gastón (2026-07-10): en el **comprobante que recibe el pasajero** (la nota de
débito por la multa), **el renglón de la multa SÍ nombra al operador mayorista**. No es un cargo
anónimo: el pasajero ve de qué operador viene la multa. Es dato del comprobante (pantalla de
facturación), no un aviso — el nombre del mayorista se permite acá. El texto exacto del renglón lo
arma el backend; la regla de diseño es: **el renglón identifica al operador**, no lo esconde.

### 2.3 Bloqueado si la multa ya salió [FIRME — P6=A]

Si el cargo ya generó su nota de débito (o si la factura destino se anuló entremedio, guard B2 del
backend), no se puede reelegir factura. En vez del desplegable va un cartel informativo, en criollo,
sin error crudo. Copy: **"El cargo de esta multa ya se emitió; no se puede cambiar la factura."**
(Si el caso es "la factura destino se anuló", el backend lo manda a revisión y el cartel de la ficha
lo dice — ver pantalla 5.)

---

## 3. Pantalla — el desarme de "Pendientes con AFIP" → monitor de comprobantes en Facturación

**Decisión ya tomada por Gastón (ADR-044, decisión final #2, 2026-07-10):** la pantalla "Pendientes
con AFIP" se DESARMA. La resolución vive en la ficha (ya hecho 2026-07-08) y queda **un monitor
pasivo de comprobantes con nombre claro dentro de Facturación**. Respondido por Gastón (P7=A, P8=A,
P9=A): se llama **"Comprobantes por resolver"**, **desaparece del menú** (vive dentro de la pantalla
de Facturación), y las listas se ordenan "lo de mirar junto, lo de tocar separado" (ver 3.2/3.3).

### 3.1 Qué es un monitor pasivo [FIRME]

Es una **lista pasiva**: comprobantes que quedaron trabados con AFIP, cada fila con **qué falta en
criollo + hace cuánto + estado**, y **cada fila es un link a la ficha** donde se resuelve. **Sin
botones de acción propios** (regla 2026-07-08). **Nunca muestra el texto crudo del error de AFIP.**

```
┌─ Comprobantes por resolver ─────────────────────────────────────────────┐
│  Comprobante            Reserva        Qué falta            Hace         │
│  ───────────────────────────────────────────────────────────────────    │
│  Multa · cargo al cli.  F-2026-1042    Falta reintentar     3 días   →   │
│  Devolución (NC)        F-2026-1031    Esperando AFIP        1 día    →   │
│  Recibo por regularizar F-2026-1020    Falta reconciliar     5 días   →   │
└───────────────────────────────────────────────────────────────────────────┘
```

### 3.2 Las 3 solapas actuales [FIRME — P9=A]

Gastón eligió "lo de mirar junto, lo de tocar separado":
- **"Multas y cargos"** (ya vive en la ficha) **+ "Notas de crédito por revisar"** → se **funden en
  UNA sola lista pasiva "Comprobantes por resolver"** (solo para mirar; cada fila es link a la
  ficha). En las notas de crédito, el ADR pide sumar el aviso de vencimiento (RG 4540) como **"vence
  en X días"** visible en criollo, dentro de la columna "Qué falta".
- **"Recibos por regularizar"** → queda **aparte**, porque tiene **acciones reales** (no es solo un
  espejo). **Conserva su nombre "Recibos por regularizar"** (Gastón no lo cambió).

### 3.3 Qué pasa con el menú [FIRME — P8=A]

La entrada **"Pendientes con AFIP"** del módulo GESTIÓN (`Sidebar.jsx`) **desaparece del menú**. Todo
se accede **dentro de la pantalla de Facturación** (`/facturacion`): la lista "Comprobantes por
resolver" (para mirar) y "Recibos por regularizar" (con sus acciones) viven ahí. Cada una respeta su
propio permiso, como hoy (`cobranzas.invoice_annul` / `cobranzas.view_all` / `approvals.review`).

---

## 4. Pantalla — retoques del extracto del operador (`SupplierExtractoSection`)

El backend (ADR-044 T2/T3b) ya manda dos líneas nuevas en el extracto del operador:
1. **"Cargo del operador facturado aparte"** — deuda nuestra hacia el operador (sube lo que le
   debemos), cuando la multa se factura aparte en vez de retenerse.
2. La línea del ajuste por tipo de cambio (ADR-044 T3b, M3).

### 4.1 Verificación contra la pantalla existente [FIRME salvo el conflicto de 4.2]

Hoy `SupplierExtractoSection` pinta un **chip "Anulación"** (siempre visible, con tooltip) en las
líneas del circuito de cancelación (`PenaltyRetained` / `RefundReceived`), para que no se confundan
con una compra normal. El código que decide el chip es:

```js
const esCircuito = linea.kind === "PenaltyRetained" || linea.kind === "RefundReceived";
```

**Las dos líneas nuevas son también movimientos de una anulación** y deben llevar el mismo chip
"Anulación", igual que sus hermanas. La implementación tiene que **sumar los kinds nuevos** a esa
condición (el nombre exacto del kind lo define el backend; presumiblemente `OperatorChargeInvoiced`
y el kind del ajuste FX). Sin eso, quedarían pintadas como compras normales. El resto del layout
(bloque por moneda, columnas Cargo/Abono/Saldo, enmascarado de costos, línea de reconciliación) NO
cambia y ya cumple.

### 4.2 El ajuste por el dólar — rótulo y configurabilidad [FIRME — P10 resuelto]

El ADR-044 T3b (M3) pedía una línea "Diferencia de cambio" en el extracto del operador; eso chocaba
con la **regla dura** de multimoneda (2026-06-09, regla #2: *"El sistema NUNCA muestra 'diferencia
de cambio' en ninguna pantalla"*). Resolución de Gastón (2026-07-10):

**(a) La etiqueta visible es "Ajuste por el dólar".** La regla dura de la frase prohibida **queda
intacta**: la línea del extracto del operador se rotula **"Ajuste por el dólar"**, nunca "diferencia
de cambio". Lleva el mismo chip **"Anulación"** que sus hermanas (ver 4.1).

```
  10/07   Ajuste por el dólar   [Anulación]        US$ 5    ...  Saldo ...
```

**(b) Quién asume ese ajuste es CONFIGURABLE.** Palabra de Gastón: *"contemplá todos los casos
posibles — un cliente (agencia) puede hacer una cosa y otro otra, lo mismo con los operadores".*
Interpretación adoptada:
- **Por defecto lo asume el cliente**, definido **a nivel de la agencia** (configuración general).
- **Excepción opcional por operador**: un operador puntual puede configurarse distinto (lo absorbe
  la agencia), sin tocar el default general.
- El backend ya está construyendo el comportamiento; la UI son **dos lugares mínimos** con defaults
  invisibles (ver 4.3). El caso normal no muestra ni pregunta nada.

### 4.3 Dónde se configura el ajuste — dos pantallas mínimas [FIRME]

**(1) Configuración general de Facturación — un renglón.** Va como una fila más en la config de
"Operativa, Cobranzas y Facturación" (misma pantalla donde ya viven los casilleros de días, caducidad,
etc.). Un solo control, con su texto en criollo:

```
  Ajuste por el dólar en las multas
  ¿Quién lo asume por defecto?   (•) El cliente     ( ) La agencia
```

- Default: **El cliente**. Sin interruptor de encender/apagar: es una sola elección (coherente con
  el estilo de los otros bloques de esa pantalla).

**(2) Ficha del operador — un campo opcional, escondido.** En la ficha del operador (donde ya viven
sus datos), un campo opcional que solo cambia el default para ESE operador:

```
  Ajuste por el dólar en sus multas
  (•) Como la configuración general   ( ) Lo asume la agencia   ( ) Lo asume el cliente
```

- Default: **"Como la configuración general"** (invisible: si nadie lo toca, manda la config general).
  Solo se aparta del default el operador que de verdad necesita otra cosa. Va detrás de "Más
  detalles"/sección avanzada de la ficha del operador, no en la vista principal.
- Enmascarado: es config, no monto; se ve para quien administra operadores. No expone cifras.

---

## 5. Pantalla — cartel multi-operador y estados nuevos en la ficha

### 5.1 Los textos existentes YA cumplen [FIRME – no se cambian]

Revisé `operatorPenaltyBanner.js` y `OperatorPenaltyStepPanel.jsx` contra las 8 reglas de voz:

- **Cartel multi-operador** (`textoMultiOperador()`): *"Hay multas confirmadas de más de un operador
  en esta anulación. No se cobran solas: hay que revisarlas a mano antes de seguir."* → cumple: dice
  "multa" (no "nota de débito"), "a mano" (no "revisión manual"), sin jerga ni estados en inglés.
- **Nombre del operador por cartel** (`tituloConNombreOperador`): en multi-operador antepone
  "Turismo Cardozo — …"; en mono-operador queda igual que antes. Cumple.
- **Carteles de acción trabada** (`copyAccionTrabada`): "Anulada — el cargo de la multa al cliente
  no salió. Probá de nuevo." etc. → dice "Anulada" (no "Cancelada"), "cargo/multa", en criollo.
  Cumple.

**Conclusión: la guía NO exige cambiar ninguno de estos textos.** Se conservan tal cual.

### 5.2 Cartel para "falta elegir la factura destino" [FIRME — P6=A]

Con 2+ facturas activas, un cargo confirmado sin factura destino elegida (o con la factura destino
anulada entremedio) va a **revisión** en el backend (guard B2 / fallback manual de T3b). Cuando eso
aparece como un estado trabado en la ficha, lleva un cartel naranja de la misma familia
"accionTrabada", con **una acción puntual "Elegir la factura"** que abre el desplegable de la
pantalla 2 (mismo patrón que "Corregir monto y moneda"). Copy propuesto del cartel:
**"Anulada — el cargo de la multa al cliente quedó trabado: falta elegir a qué factura corresponde."**
Si la traba es porque la factura destino se anuló, el cartel lo dice y la acción vuelve a ser
"Elegir la factura". El nombre exacto del estado lo define el backend; `operatorPenaltyBanner.js`
suma esa entrada a `copyAccionTrabada` cuando el backend lo exponga.

---

## Qué NO hay que hacer (recordatorios para frontend-senior)

- **NO** reintroducir el modal `ConfirmPenaltyModal` en el camino normal (Gastón lo rechazó).
- **NO** mostrar "tipo de cargo" en el caso simple: solo en la acción secundaria "otro cargo".
- **NO** sumar pesos y dólares en ninguna línea/total; **NO** escribir "diferencia de cambio".
- **NO** poner botones de acción en las listas del monitor (son pasivas; la fila es un link).
- **NO** mostrar el texto crudo del error de AFIP en ninguna lista ni cartel: "qué falta" en criollo.
- **NO** mostrar montos de multa/costo a quien no tiene `cobranzas.see_cost` (va "—").
- **NO** esconder al mayorista en el renglón de la multa del comprobante del pasajero (SÍ se nombra).
- **NO** usar el TC del día de emisión: el TC del cargo al cliente es el del **día que el operador
  cobró la multa**.

---

## Estado de las preguntas — TODAS RESPONDIDAS (Gastón, 2026-07-10)

Toda la spec quedó **[FIRME]**. Las respuestas:

| # | Tema | Respuesta |
|---|------|-----------|
| P1 | Dónde va "agregar otro cargo" | **A** — link discreto debajo del cargo confirmado |
| P2 | Cómo se pregunta el tipo de cargo | **A** — desplegable con las 4 opciones en español |
| P3 | "Cómo lo cobra el operador" | **A** — detrás de "Más detalles", cerrado por defecto |
| P4 | "Qué pasa con el cliente" | **A** — "Más detalles", default "se le traslada tal cual" |
| P5 | Elegir factura destino (2+ facturas) | **A** — desplegable en el mismo panel al confirmar |
| P6 | Bloqueo si la multa ya salió | **A** — cartel "El cargo de esta multa ya se emitió; no se puede cambiar la factura." |
| P7 | Nombre del monitor | **A** — "Comprobantes por resolver" |
| P8 | Menú | **A** — desaparece; vive dentro de Facturación |
| P9 | Las 3 solapas | **A** — multas+NC en una lista para mirar; recibos aparte (conserva el nombre) |
| P10 | Ajuste por el dólar | Etiqueta "Ajuste por el dólar" (regla vieja intacta) + **configurable**: default por agencia (cliente) + excepción por operador |

Dos decisiones de negocio de Gastón (2026-07-10) que también entran a la spec:
- El **comprobante del pasajero SÍ nombra al mayorista** en el renglón de la multa (ver 2.4).
- El **tipo de cambio es el del día en que el operador cobró** la multa (ver 1.2).

El bloque original de preguntas queda abajo como registro histórico (con las respuestas ya tomadas).

---

## PREGUNTAS PARA GASTON (RESPONDIDAS — registro histórico)

### Tema 1: cargar un SEGUNDO cargo del mismo operador en una anulación
Contexto: hoy cuando anulás y el operador cobra multa, cargás UN dato (monto + moneda) y listo, sin preguntarte nada raro. Eso no cambia. Pero a veces el mismo operador cobra DOS cosas juntas sobre el mismo viaje (por ejemplo, una multa administrativa **y** una retención de impuestos). Para ese caso hace falta poder agregar un segundo cargo. Esto es lo que antes te pedía "el tipo de cargo que no entendías": ahora lo escondemos para que solo aparezca cuando de verdad hace falta.

**P1. ¿Dónde ponemos la opción de "agregar otro cargo"?**
  A) Un link chiquito y discreto debajo del cargo que ya confirmaste (recomendado: no molesta a nadie; el que no lo necesita ni lo ve).
```
  Multa del operador: US$ 200  ✔ confirmada
  + Agregar otro cargo de este operador
```
  B) Un botón más grande y visible al lado del cargo.
```
  Multa del operador: US$ 200  ✔    [ + Otro cargo ]
```

**P2. Ese segundo cargo SÍ necesita que digas de qué tipo es (multa administrativa / impuesto / retención fiscal / otro). ¿Cómo lo preguntamos?**
  A) Lista desplegable con las 4 opciones en español, arriba de todo del formulario (recomendado).
```
  Tipo de cargo *   [ Retención fiscal            ▾ ]
                     administrativo · impuesto · retención · otro
```
  B) Cuatro botones grandes en fila, elegís uno.
```
  Tipo de cargo *
  [ Administrativo ] [ Impuesto ] [ Retención ] [ Otro ]
```

**P3. ¿Dónde va lo de "cómo lo cobra el operador" (te lo descuenta de lo que te va a devolver, o te lo factura aparte)?**
  A) Escondido detrás de "Más detalles", cerrado por defecto — lo abre solo el que lo necesita (recomendado: la mayoría no toca esto).
```
  ▸ Más detalles  (cómo lo cobra · traslado al cliente)
```
  B) Siempre a la vista, dentro del formulario.
```
  ¿Cómo lo cobra?  (•) Lo descuenta de lo que te devuelve   ( ) Te lo factura aparte
```

**P4. ¿Y qué pasa con el cliente (se le traslada tal cual, o le sumás un cargo de gestión, o lo absorbe la agencia)?**
  A) También detrás de "Más detalles", y viene marcado por defecto en "tal cual" (recomendado: el default es lo más común, no te hace pensar).
```
  (dentro de Más detalles)
  ¿Qué pasa con el cliente?
   (•) Se le traslada tal cual   ( ) + cargo de gestión [$__]   ( ) Lo absorbe la agencia
```
  B) A la vista siempre, para no perderlo de vista.

---

### Tema 2: elegir a qué factura del cliente va la multa (cuando hay más de una)
Contexto: si una reserva tiene UNA sola factura, no te preguntamos nada, el sistema la usa sola. Pero si tiene dos (por ejemplo una en pesos y una en dólares), el sistema no puede adivinar a cuál corresponde la multa: eso lo sabés vos. Así que hay que elegirla.

**P5. Cuando la reserva tiene 2+ facturas, ¿en qué momento te pedimos elegir la factura?**
  A) Al confirmar la multa, dentro del mismo panel de siempre, aparece un desplegable extra (recomendado: lo elegís en el momento, sin pasos aparte).
```
  ¿A qué factura del cliente corresponde? *
  [ Factura B 0001-00012345 — US$ 200   ▾ ]
     · Factura B 0001-00012345 — US$ 200
     · Factura B 0001-00012346 — $ 150.000
```
  B) En un paso aparte, después de confirmar el monto.

**P6. Si la multa YA se emitió (o la factura elegida se anuló mientras tanto), ¿qué mostramos en vez del desplegable?**
  A) Un cartel simple, sin números raros: **"El cargo de esta multa ya se emitió; no se puede cambiar la factura."** (recomendado).
  B) Otro texto que prefieras (contame).

---

### Tema 3: se va la pantalla "Pendientes con AFIP" y queda un monitor dentro de Facturación
Contexto: ya decidiste desarmar "Pendientes con AFIP". La resolución de cada cosa vive en la ficha (eso ya está). Queda una **lista para mirar** (no para tocar) de los comprobantes que quedaron trabados, dentro de Facturación. Faltan tres detalles.

**P7. ¿Cómo la llamamos?**
  A) **"Comprobantes por resolver"** (recomendado: dice qué es sin jerga).
  B) **"Comprobantes"** a secas.
  C) **"Comprobantes con problemas"**.
```
  ┌─ Comprobantes por resolver ───────────────────────────────────┐
  │  Multa · cargo al cliente   F-2026-1042   Falta reintentar  →  │
  │  Devolución (NC)            F-2026-1031   Esperando AFIP    →  │
  │  Recibo por regularizar     F-2026-1020   Falta reconciliar →  │
  └───────────────────────────────────────────────────────────────┘
```

**P8. Hoy hay una entrada "Pendientes con AFIP" en el menú de GESTIÓN. ¿Qué hacemos con ella?**
  A) Desaparece del menú; el monitor se ve dentro de la pantalla de Facturación (recomendado: menos puertas, como pediste con las bandejas).
  B) Se queda una entrada en el menú, pero renombrada como el monitor (P7).

**P9. Hoy adentro hay 3 solapas: "Multas y cargos", "Notas de crédito", "Recibos por regularizar". La de recibos SÍ tiene botones de verdad (no es solo para mirar). ¿Cómo las ordenamos?**
  A) Una sola lista con todo mezclado (multas + notas de crédito) para mirar, y **los recibos aparte** porque tienen acciones reales (recomendado: lo de mirar junto, lo de tocar separado).
  B) Siguen las 3 solapas como hoy, pero renombradas.
  - En cualquier caso, "Recibos por regularizar" se conserva. ¿Le dejamos ese nombre o preferís otro? (contame)

---

### Tema 4: el extracto del operador y una palabra prohibida
Contexto: cuando el dólar del día que sale la multa es distinto del dólar del día que el operador te liquida, queda una pequeña diferencia. El diseño técnico quiere mostrar esa línea en el extracto del operador. **Acá hay un choque con una regla tuya vieja.**

**P10. ⚠️ Vos ya decidiste hace tiempo (multimoneda) que la frase "diferencia de cambio" NO aparece NUNCA en ninguna pantalla. Pero el diseño nuevo necesita mostrar esa línea. ¿Qué hacemos?**
  A) Mostramos la línea con **otro nombre** que no sea la frase prohibida. Propuesta: **"Ajuste por el dólar"**.
```
  10/07   Ajuste por el dólar   [Anulación]        US$ 5    ...
```
  B) La llamamos **"Diferencia por el tipo de cambio"** (una variante, pero sigue teniendo la palabra "cambio" — vos decidís si te molesta o no).
  C) No la mostramos como línea aparte: la escondemos dentro de la cuenta y listo (queda menos claro de dónde salió el numerito).
  D) Otra cosa (contame). Nota: cambiar tu regla vieja también es una opción, pero eso lo decidís vos, no lo toco solo.
