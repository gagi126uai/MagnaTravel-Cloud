# Spec UX — Tandas 5 a 9 del plan de remediación del contrato pantalla ↔ motor

**Fecha**: 2026-07-20
**Autor**: ux-ui-disenador (diseña SOLO desde `docs/ux/guia-ux-gaston.md`)
**Insumos**: `docs/architecture/2026-07-18-contrato-pantalla-motor-PLAN-remediacion.md` (Tandas 5-9, Decisiones A/B/C, §6 con D1-D4 firmadas 2026-07-18) · `docs/ux/2026-07-18-t1-t2-contrato-pantalla-motor.md` (formato de referencia) · código real de las 5 pantallas afectadas.
**Para**: frontend-senior (implementa al pie de la letra).
**No toca**: `docs/ux/2026-07-20-t3-t4-contrato-pantalla-motor.md` (otro agente).

---

## Resumen para el orquestador (leer esto primero)

**Las cinco tandas quedan CUBIERTAS por la guía + patrones ya construidos y decisiones ya firmadas. NO hay preguntas bloqueantes para Gastón.**

Dos ajustes se resolvieron aplicando un precedente ya firmado en vez de inventar UI nueva:

- **T7 (papelera del servicio):** el plan de arquitectura sugería "tooltip" para el aviso previo. Un tooltip (solo al pasar el mouse) **choca** con la regla dura del 2026-06-08 ("nada de tooltip, el texto va siempre a la vista"). La guía YA tiene el patrón correcto para exactamente este caso — bloqueo por servicio, en una lista — en la sección "Cancelación — pantallas" (2026-06-13): *"Los servicios bloqueados (factura/voucher vivo) aparecen VISIBLES con el casillero apagado y la explicación al lado."* Se aplica ese patrón (texto corto siempre visible al lado de la papelera apagada), no un tooltip.
- **T5 (Emitir factura):** en vez de inventar un mecanismo nuevo de "apagado por capacidad", se extendió el patrón que la propia ficha `EmitirFacturaInline.jsx` YA usa para el bloqueo por deuda (`bloqueadoPorDeuda`, cartel rojo antes del formulario) al caso "no invoiceable por estado".

Un punto queda anotado como **backlog, no como pregunta bloqueante** (T7, caso "sin cliente asignado"): hoy no existe en todo el producto una pantalla para asignar/cambiar el cliente de una reserva ya creada, así que ese caso no puede tener "el botón correcto" que pide el plan — se documenta como límite conocido, con el texto explicando qué falta, sin botón que prometa algo que no existe.

---

# TANDA 5 — "Emitir factura" sin jerga y apagada por estado

## Qué cubre la guía (citado)

1. **La ficha de emisión ya es en línea, con confirmación previa y sin ventana flotante** — sección "Emisión de la factura de venta" (H2, 2026-06-24). No se rediseña el flujo; se corrige el aviso de bloqueo.
2. **Patrón ya construido para bloquear la ficha ANTES del formulario cuando la reserva no puede facturar:** `EmitirFacturaInline.jsx` ya tiene el bloque `bloqueadoPorDeuda` (líneas ~373-375 y ~869-904): cartel de color, ícono `AlertCircle`, título + texto del motivo, sin formulario debajo. Se extiende el MISMO patrón visual al nuevo caso "no facturable por estado", en vez de crear un bloque nuevo.
3. **Nunca perder lo cargado / nunca dejar al usuario sin salida** — regla general (Ronda 2, 2026-06-06) y la regla de las 3 tandas anteriores ("cada rechazo dice QUÉ HACER AHORA").
4. **Cero jerga, cero nombres de estado interno** — regla transversal del plan, heredada de la guía general de textos.

**Conclusión T5:** cubierto. No hay decisión de diseño nueva; se documenta el ajuste puntual.

## Qué NO se toca

La barra de acciones de la solapa "Estado de Cuenta" (`ReservaDetailPage.jsx` ~2287-2346) **ya** apaga/esconde el botón "Emitir factura" leyendo `reserva.capabilities.canInvoiceSale.allowed` (variable `facturaHabilitada` → `mostrarFactura`). Cuando la reserva todavía no puede facturar (antes de Confirmada) o ya no puede (estado terminal salvo anuladas), **el botón directamente no aparece** — coherente con cómo se muestran/esconden los demás botones de esa misma barra (Registrar cobro, Anular reserva), y con la regla unificada del 2026-06-26 (botón ya-cumplido o no-aplicable-todavía = se esconde; bloqueado por candado/permiso = gris + cartel). **Esto no se cambia.**

## El agujero real: el texto crudo cuando igual se llega a intentar facturar

`InvoiceService.cs:377-379` (backend) arma el mensaje de rechazo a mano, interpolando el enum crudo del estado:

```
$"No se puede facturar una reserva en estado '{reserva.Status}'. " +
"La factura de venta se emite desde Confirmada en adelante, salvo en reservas anuladas."
```

Con una reserva en "En gestión", esto imprime literalmente: **`No se puede facturar una reserva en estado 'InManagement'.`** — el peor caso confirmado del inventario (JERGA, un nombre de estado interno en inglés, a la vista de Gastón).

**Fix (ya identificado por el plan, no es decisión de diseño): reusar la constante limpia que YA existe**, `ReservaCapabilityPolicy.NotInvoiceableStatusReason` (`src/TravelApi.Domain/Reservations/ReservaCapabilities.cs:130-132`):

```
"No se puede facturar en este estado. La factura de venta se emite desde Confirmada en adelante, " +
"salvo en reservas anuladas."
```

Sin ningún token en inglés, sin comillas con el nombre del estado. El backend lanza esta constante en vez de reconstruir el string a mano.

## Por qué igual se puede llegar a ver el error (y cómo se lo muestra)

El botón de la barra de acciones ya filtra el 99% de los casos. Pero **`EmitirFacturaInline.jsx` puede abrirse desde otro lugar sin pasar por ese filtro** — en particular, la Tanda 7 de este mismo documento agrega un botón "Emitir factura" dentro del cartel del candado R1 (D1 firmada). Ese candado puede aparecer en una reserva que todavía no llegó a Confirmada (el candado R1 solo exige "hay plata pagada al operador sin factura", no exige que la reserva esté firme). En ese cruce, apretar "Emitir factura" desde el candado puede caer en el mismo rechazo de estado — y ahí es donde el texto limpio importa.

**Spec: extender el bloque `bloqueadoPorDeuda` existente a un segundo caso, `bloqueadoPorEstado`.**

### Estado HOY (si se fuerza la apertura de la ficha en un estado no facturable)

```
┌─ Nueva Factura AFIP ──────────────────────────────────────── [x] ┐
│  Fam. García · CUIT 20-12345678-9                                │
│                                                                    │
│  [ ... todo el formulario de renglones, igual que siempre ... ]  │
│                                                                    │
│                                         [ Emitir factura ]        │
└────────────────────────────────────────────────────────────────┘
   (click) →  cartel rojo: "No se puede facturar una reserva en
               estado 'InManagement'. ..."     ← el enum crudo
```

### Estado NUEVO (mismo patrón visual que `bloqueadoPorDeuda`, ANTES del formulario)

```
┌─ Nueva Factura AFIP ──────────────────────────────────────── [x] ┐
│  Fam. García · CUIT 20-12345678-9                                │
│                                                                    │
│  ┌──────────────────────────────────────────────────────────┐   │
│  │ ⓧ No se puede facturar todavía                            │   │
│  │   No se puede facturar en este estado. La factura de      │   │
│  │   venta se emite desde Confirmada en adelante, salvo en   │   │
│  │   reservas anuladas.                                       │   │
│  └──────────────────────────────────────────────────────────┘   │
│                                                    [ Cerrar ]     │
└────────────────────────────────────────────────────────────────┘
```

- **Cuándo se muestra este bloque:** apenas se abre la ficha, si `reserva.capabilities?.canInvoiceSale?.allowed === false` — SIN esperar a que el usuario llene el formulario y apriete "Emitir factura". Mismo criterio que `bloqueadoPorDeuda`, que ya se calcula al abrir (líneas 370-375).
- **El formulario de renglones NO se muestra** mientras este bloque está activo (igual que hoy con `bloqueadoPorDeuda`, que reemplaza el contenido normal por el cartel).
- **Un solo botón: "Cerrar"** (llama a `onCancelar`, el mismo callback que ya usa el ícono X del encabezado). No hay "reintentar" porque no hay nada que reintentar sin cambiar el estado de la reserva primero — eso se hace desde afuera de esta ficha.
- **Si por una carrera real** (la reserva cambió de estado justo mientras la ficha estaba abierta con capabilities viejas) **el POST igual explota**, el cartel de error de siempre (`errorEnvio`, ya existe en la ficha) muestra el mismo texto limpio que llega del backend — ya no hay enum crudo posible en ningún camino.
- **Color:** rojo (mismo que `bloqueadoPorDeuda` cuando es bloqueo duro, no el ámbar de "override habilitado" — acá no hay override posible, es un estado que no admite excepción).

## Checklist de estados (T5)

| Estado | Qué se ve |
|---|---|
| Ficha se abre, reserva SÍ puede facturar | Formulario normal (sin cambios). |
| Ficha se abre, reserva NO puede facturar por estado | Cartel rojo con el texto limpio + botón "Cerrar". Formulario oculto. |
| Formulario completo, se aprieta "Emitir factura" y explota igual (carrera) | Cartel de error de siempre (`errorEnvio`), ahora con texto limpio garantizado. |
| Botón "Emitir factura" de la barra de acciones (caso normal) | Sin cambios: se esconde solo cuando no aplica (comportamiento ya correcto). |

## Qué NO hay que hacer (T5)

- NO tocar la lógica de mostrar/esconder el botón de la barra de acciones (`ReservaDetailPage.jsx`): ya está bien.
- NO agregar un tooltip ni un motivo debajo del botón de la barra: sigue sin aplicar acá (el botón directamente no aparece).
- NO dejar ningún camino donde `{reserva.Status}` (o cualquier enum) llegue interpolado a un mensaje.
- NO agregar botón de "reintentar" al bloque nuevo: no hay nada que reintentar desde esta ficha.

---

# TANDA 6 — Editar/Eliminar cobro mira el PAGO, no solo la reserva

## Qué cubre la guía (citado)

1. **"Ver/reimprimir un papel ya hecho" sí; "crear/anular/editar" no** — sección "Estados congelados" (2026-06-22). Ya establece que un documento fiscal ya emitido (recibo, factura) no se edita/anula sobre la marcha; el patrón "editable solo mientras no hay documento vivo" ya es un principio firmado, esta tanda solo lo baja a granularidad de PAGO en vez de reserva.
2. **Precedente de "apagado + explicación al lado" para bloqueos por fila** — sección "Cancelación — pantallas" (2026-06-13): *"Los servicios bloqueados (factura/voucher vivo) aparecen VISIBLES con el casillero apagado y la explicación al lado."* Mismo criterio aplicado acá: Editar/Eliminar de UN cobro puntual se apagan con su explicación al lado, sin tocar los demás cobros de la lista.
3. **Ningún mensaje se reescribe si ya está bien** — Decisión C / D2 firmada (2026-07-18): los tres mensajes de bloqueo que ya existen en el backend (`MutationGuards.cs:137-159`, `DeleteGuards.cs:343-353`) son criollo-con-camino; se REUSAN tal cual, no se inventan textos nuevos.
4. **Nada de tooltip; el texto va siempre a la vista** — regla 2026-06-08.

**Conclusión T6:** cubierto. Se documenta el patrón exacto por bloqueo.

## Auditoría del patrón vigente (el agujero real)

`PaymentReceiptActions` (`ReservaDetailPage.jsx` ~396-556) hoy calcula `cobroEsEditable` con la capacidad a nivel RESERVA (`canEditOrDeletePayment.allowed`) más el chequeo de que el recibo no esté `Voided`. **No mira si ESTE cobro puntual tiene un recibo `Issued` (activo) o está atado a una factura con CAE viva.** Resultado: un cobro con recibo YA emitido sigue mostrando "Editar"/"Eliminar" — el usuario llena el formulario entero y recién ahí el backend lo rechaza (peor caso #9 del inventario).

Los backends YA tienen el guard correcto, solo falta que el DTO de cada pago lo exponga por fila (Decisión B, plan Tanda 6). Los mensajes reales, verificados en el código:

**Bloqueo de EDITAR** (`MutationGuards.cs:133-159`):

| Situación del cobro | Texto real (se reusa tal cual) |
|---|---|
| Tiene un recibo **emitido** (Issued) | "No se puede editar el pago porque tiene un recibo emitido. Anulá el recibo y registrá un nuevo pago." |
| Tiene SOLO un recibo **anulado** (Voided, sin uno vigente) | "No se puede editar el pago porque tiene un recibo anulado que debe preservarse para auditoría." |
| Está vinculado a una **factura con CAE vivo** | "No se puede editar el pago porque está vinculado a una factura emitida (CAE). Generá una nota de crédito si corresponde." |

**Bloqueo de ELIMINAR** (`DeleteGuards.cs:338-354`):

| Situación del cobro | Texto real |
|---|---|
| Tiene un recibo **vigente** (Issued) | "No se puede eliminar el pago porque tiene un comprobante vigente. Anulá primero el comprobante." |
| Está vinculado a una **factura** | "No se puede eliminar el pago porque está vinculado a una factura. Generá una nota de crédito si corresponde." |

*(Nota de limpieza, no de diseño — mismo criterio que T1 "acento faltante": el texto de "eliminar" en el backend hoy dice literalmente "No se puede **anular** el pago porque tiene un comprobante vigente. **Anula** primero..." con el verbo cambiado y sin tilde. Al tocar este guard para exponerlo por fila, se corrige a "No se puede **eliminar** el pago..." / "**Anulá** primero el comprobante." — es prolijidad de redacción, no cambio de contenido ni de diseño.)*

## Spec de pantalla — fila de cobro en Estado de Cuenta

### Estado HOY (el callejón — peor caso #9)

```
[ REC-000123 ]  [ Ver PDF ]  [ Anular comprobante ]  [ ✏ Editar ]  [ 🗑 Eliminar ]
                                                        ↑ siempre visibles aunque
                                                          el recibo ya esté emitido
```

Click en "Editar" → se abre la ficha de cobro completa → el usuario cambia algo → "Guardar" → recién ahí el cartel rojo de siempre explica que no se puede.

### Estado NUEVO (apagado + explicación al lado, ANTES de abrir el formulario)

```
[ REC-000123 ]  [ Ver PDF ]  [ Anular comprobante ]  [ ✏ Editar ]  [ 🗑 Eliminar ]
                                                         (gris)       (gris)

  🔒 No se puede editar ni eliminar este cobro: tiene un recibo emitido.
     Anulá el recibo primero si necesitás corregirlo.
```

- **"Editar" y "Eliminar" van GRISES (no clickeables)** cuando el flag por-pago dice que están bloqueados — la palabra sigue a la vista siempre (regla 2026-06-08), solo cambia el color y se saca el `onClick`.
- **Debajo de la fila del cobro (o al lado, si entra en el ancho), un renglón chico con candado 🔒 explica el motivo** — usa el TEXTO REAL del backend tal cual (tabla de arriba), sin reescribirlo. Un solo renglón por cobro (el motivo más específico gana: recibo emitido > recibo anulado > factura con CAE, mismo orden que ya usa el backend al evaluarlos).
- **Si el cobro NO está bloqueado**, la fila se ve exactamente igual que hoy: sin renglón extra, botones activos.
- **Sigue vigente:** un recibo YA `Voided` (anulado) no ofrece Editar/Eliminar del cobro tampoco — comportamiento actual (`reciboAnulado` ya lo cubre), no cambia.
- **Nada de tooltip.** El motivo nunca vive solo en un `title=` al pasar el mouse; el `title=`/`aria-label` puede repetir el mismo texto como redundancia de accesibilidad (mismo patrón ya usado en "Ver comprobante de pago"), nunca como única fuente.

## Checklist de estados (T6)

| Estado | Qué se ve |
|---|---|
| Cobro sin recibo, sin factura vinculada | Editar/Eliminar activos, sin cambios. |
| Cobro con recibo emitido (Issued) | Editar/Eliminar grises + renglón "🔒 ...tiene un recibo emitido. Anulá el recibo primero...". |
| Cobro con recibo anulado (Voided, sin otro vigente) | Editar gris + renglón "🔒 ...tiene un recibo anulado que debe preservarse para auditoría." (Eliminar sigue permitido, regla ya existente — el Voided no bloquea el delete). |
| Cobro vinculado a factura con CAE vivo | Editar/Eliminar grises + renglón "🔒 ...está vinculado a una factura emitida (CAE). Generá una nota de crédito si corresponde." |
| Se fuerza igual por carrera | El cartel de error de siempre muestra el mismo texto real (nunca un genérico). |

## Qué NO hay que hacer (T6)

- NO usar tooltip como única fuente del motivo.
- NO reescribir los 5 mensajes reales (solo la corrección de verbo/tilde en el de "eliminar", que es prolijidad, no rediseño).
- NO ocultar Editar/Eliminar cuando están bloqueados: van GRISES con motivo, no desaparecen (esto no es "acción ya cumplida", es "acción vedada por candado fiscal" — la otra rama de la regla 2026-06-26).
- NO abrir el formulario de edición para recién ahí mostrar el rechazo.

---

# TANDA 7 — Anular un servicio: pre-chequeo + candado R1 con camino servido

## Qué cubre la guía (citado)

1. **Precedente exacto para "apagado + explicación al lado" en una fila de servicio** — "Cancelación — pantallas" (2026-06-13): *"Bloqueo por factura/voucher vivo: si el servicio tiene factura con CAE o voucher emitido, NO se puede cancelar. Ventanita clara con el motivo + número de factura + botón 'Ir a la factura' para anularla. Mismo formato para el caso del voucher."* Y, para el pre-chequeo (antes de intentar): *"Los servicios bloqueados (factura/voucher vivo) aparecen VISIBLES con el casillero apagado y la explicación al lado."*
2. **Etiquetas chicas al lado de CADA servicio ya son un patrón establecido** — ADR-036 punto 5 (2026-06-21, P4=B): la fila de cada servicio ya muestra chips como "⚠️ Operador impago" / "✔ Operador pagado". El aviso nuevo de papelera-bloqueada usa el MISMO lugar y formato.
3. **"El paso de multa vive en la ficha; nunca se deriva a alguien que ya es el usuario"** — 2026-07-08. Aplica al rediseño del modal: cada motivo real (voucher, R1, sin cliente) necesita SU propio texto y SU propio camino, no el texto fijo de "nota de crédito" puesto a todos por igual.
4. **Botón "Emitir factura" en el candado R1 — D1 firmada por Gastón (2026-07-18):** *"Recomiendo: sí, botón 'Emitir factura' en el cartel."* Gastón aprobó la recomendada. No se repregunta.
5. **Vocabulario Cancelar→Anular** — regla dura del dueño, ya en ejecución por la Tanda 4 (acople T4→T7: T4 corrige el texto del candado R1 primero, T7 le agrega el `code` sobre el texto ya corregido).
6. **Nada de tooltip; texto siempre a la vista** — 2026-06-08 (ver el ajuste al plan en el resumen de arriba).

**Conclusión T7:** cubierto por el precedente 2026-06-13 (pre-chequeo con explicación al lado) + la D1 firmada (botón "Emitir factura") + el patrón de mapeo por código ya usado en `DeshacerMultaEmitidaInline` (Decisión C).

## Los 3 motivos que hoy explotan sin avisar antes

`ServiceList.jsx` ofrece la papelera de CUALQUIER servicio confirmado sin pre-chequear nada; cuando el backend rechaza, `ModalBloqueoCancelacionServicio` (líneas 558-644) muestra SIEMPRE el mismo texto fijo — *"Para poder anular este servicio primero hay que gestionar la nota de crédito correspondiente en la sección de facturación de la reserva"* — y el mismo botón *"Ver facturas de la reserva"*, **sin importar cuál de los 3 motivos reales sea**:

| Motivo real | Texto real del backend (post T4, vocabulario "anular") | Camino correcto |
|---|---|---|
| **(R1) Pagado al operador sin factura** | "No se puede anular este servicio todavía: ya tiene pagos al operador y la reserva aún no tiene factura emitida para registrar el reembolso a tu favor. Emití la factura de venta o gestioná el reembolso con el operador antes de anular el servicio." | Botón **"Emitir factura"** (D1 firmada) |
| **(d) Voucher emitido** | "No se puede anular este servicio: la reserva tiene vouchers emitidos. Anulá los vouchers primero si necesitás corregir datos." | Botón **"Ver vouchers de la reserva"** (nuevo, mismo patrón que "Ver facturas") |
| **(f) Sin cliente asignado, con factura viva** | "No se puede anular este servicio: la reserva tiene una factura emitida pero no tiene un cliente asignado para facturarle la nota de crédito. Asigná un cliente a la reserva antes de anular." | **No existe hoy ningún camino en el producto** para asignar/cambiar el cliente de una reserva ya creada — ver nota de backlog más abajo. Sin botón; el texto ya dice qué falta. |

## 1) Pre-chequeo: papelera apagada con explicación al lado (ANTES del clic)

Aplicando el precedente 2026-06-13 (mismo lugar donde ya vive "⚠️ Operador impago"):

### Estado HOY

```
Hotel Maitei — Doble · Confirmado    [✏ Editar] [🗑 Anular]
                                                    ↑ siempre clickeable
```

### Estado NUEVO — ejemplo caso R1 (pagado al operador, sin factura)

```
Hotel Maitei — Doble · Confirmado    [✏ Editar] [🗑 Anular]
                                                    (gris)
  🔒 Ya tiene pagos al operador y la reserva no tiene factura. Emití la
     factura de venta antes de anular este servicio.
```

### Estado NUEVO — ejemplo caso voucher emitido

```
Traslado Aeropuerto · Confirmado     [✏ Editar] [🗑 Anular]
                                                    (gris)
  🔒 La reserva tiene vouchers emitidos. Anulá los vouchers primero
     si necesitás corregir datos.
```

- **La papelera va gris** (no clickeable) cuando el pre-chequeo (nuevo flag de capabilities por servicio) detecta cualquiera de los 3 motivos. La PALABRA ("Anular"/"Borrar") sigue a la vista siempre, solo cambia el color — regla 2026-06-08.
- **Debajo de la fila, el mismo formato de chip/renglón chico ya usado para "Operador impago"**, con el texto real del backend (tabla de arriba), acortado a lo esencial si no entra completo en una línea, pero SIN inventar una versión distinta del motivo (mismo texto, no una paráfrasis).
- **Si el servicio NO tiene ningún bloqueo, la fila se ve exactamente igual que hoy** — sin renglón extra.
- **Nada de tooltip.** (Ver ajuste al plan en el resumen del documento.)

## 2) Cuando igual se fuerza (carrera) — el modal por motivo real

`ModalBloqueoCancelacionServicio` deja de tener un párrafo fijo de "nota de crédito" y pasa a mostrar el mensaje real + el botón que corresponde a CADA motivo, detectado por `code` (Decisión C, mismo patrón que `DeshacerMultaEmitidaInline`/`esErrorSaldoAplicadoAlDeshacerMulta`), no por texto.

### Caso R1 (pagado al operador sin factura) — con el botón nuevo de la D1

```
┌─ No se puede anular el servicio ─────────────────────── [x] ┐
│                                                                │
│  No se puede anular este servicio todavía: ya tiene pagos al │
│  operador y la reserva aún no tiene factura emitida para     │
│  registrar el reembolso a tu favor. Emití la factura de      │
│  venta o gestioná el reembolso con el operador antes de      │
│  anular el servicio.                                          │
│                                                                │
│                         [ Entendido ]  [ 🧾 Emitir factura ] │
└────────────────────────────────────────────────────────────┘
```

- Click en **"Emitir factura"** abre `EmitirFacturaInline` en la misma ficha (mismo componente de la Tanda 5, con su bloqueo ya limpio si correspondiera) — no navega a otra pantalla, resuelve en el lugar.

### Caso voucher emitido — botón "Ver vouchers de la reserva" (nuevo, mismo patrón que "Ver facturas")

```
┌─ No se puede anular el servicio ─────────────────────── [x] ┐
│                                                                │
│  No se puede anular este servicio: la reserva tiene           │
│  vouchers emitidos. Anulá los vouchers primero si             │
│  necesitás corregir datos.                                    │
│                                                                │
│                    [ Entendido ]  [ 📄 Ver vouchers de la    │
│                                       reserva ]                │
└────────────────────────────────────────────────────────────┘
```

- Click navega a la solapa "Vouchers" de la misma ficha (`setActiveTab("voucher")`), mismo mecanismo que ya usa `onIrAFacturas` con la solapa "Estado de Cuenta" (`setActiveTab("account")`). Se agrega el callback análogo `onIrAVouchers`.

### Caso sin cliente asignado — sin botón de acción (backlog, ver nota)

```
┌─ No se puede anular el servicio ─────────────────────── [x] ┐
│                                                                │
│  No se puede anular este servicio: la reserva tiene una      │
│  factura emitida pero no tiene un cliente asignado para      │
│  facturarle la nota de crédito. Asigná un cliente a la       │
│  reserva antes de anular.                                     │
│                                                                │
│                                              [ Entendido ]    │
└────────────────────────────────────────────────────────────┘
```

- Solo el texto real (ya dice qué falta) + "Entendido". Sin botón de camino porque no existe la pantalla que lo resolvería (ver nota de backlog abajo).

## 3) A3(b) — el error de EDITAR un servicio enganchado al "Pedí autorización" que ya existe

Cuando la ficha en línea de edición de un servicio (`ServiceInlineCard.jsx`) recibe el rechazo por candado de reserva Confirmada sin autorización (`ReservaLockGuard.LockedMessage`: *"La reserva está confirmada (con candado). Pedí autorización para editarla antes de modificarla."*), hoy el cartel rojo de la ficha (`errorGuardado`, ya construido — Ronda 2, 2026-06-06) muestra ese texto y ahí termina: el usuario tiene que darse cuenta solo de que existe el botón **"Pedí autorización"** en la franja de arriba de la ficha de la reserva (`ReservaLockBanner`, decisión #1 del ciclo de vida, 2026-06-08).

### Estado NUEVO

```
┌ Editando: Hotel Maitei ──────────────────────────────────────┐
│  [ ... campos del formulario, intactos ... ]                 │
│                                                                 │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ ⓧ La reserva está confirmada (con candado). Pedí        │  │
│  │   autorización para editarla antes de modificarla.       │  │
│  └────────────────────────────────────────────────────────┘  │
│                                    [ Pedí autorización ]      │
│                                    [ Cancelar ] [ Guardar ]    │
└────────────────────────────────────────────────────────────┘
```

- **Cuando el error detectado es EXACTAMENTE este candado** (detección recomendada por código estable, no por matcheo de texto — nota de implementación, no de diseño: hoy `ReservaLockGuard.LockedMessage` es un `InvalidOperationException` plano sin `code`; agregarle uno evita depender del string exacto), aparece un botón adicional **"Pedí autorización"** junto al cartel de error, que dispara el MISMO modal que ya abre la franja de arriba (`EditAuthorizationModal` vía `setShowEditAuthModal(true)` en `ReservaDetailPage.jsx`). Requiere pasar ese callback como prop hacia `ServiceInlineCard`/`ServiceList` (mismo mecanismo ya usado para `onIrAFacturas`).
- **Para cualquier otro motivo de rechazo al editar** (ej. CAE/voucher vivo, guard de negocio distinto), el cartel se ve exactamente igual que hoy, sin este botón extra — ese candado NO se resuelve pidiendo autorización (es un bloqueo fiscal permanente, no temporal).

## Nota de backlog (no bloquea T7): "sin cliente asignado" no tiene camino todavía

El plan pide "el botón correcto" para el caso (f), pero **hoy no existe en ninguna pantalla del producto una forma de asignar o cambiar el cliente (Payer) de una reserva ya creada** — se revisó `ReservaDetailPage.jsx`, `ReservaHeader.jsx` y el resto de `features/reservas`, no hay ningún `onAssignCustomer` ni selector de cliente para una reserva existente. Es un caso raro (dato legado/edge-case, según el propio comentario del backend). **No se inventa una pantalla nueva para esto en T7** (excede el alcance pedido): el texto real ya explica qué falta ("Asigná un cliente a la reserva antes de anular"), y el modal se cierra con "Entendido" sin botón de acción. Si este caso empieza a doler en la práctica, es una feature aparte (crear/editar el Payer de una reserva existente) que necesita su propia ronda de preguntas a Gastón — no se resuelve acá.

## Checklist de estados (T7)

| Estado | Qué se ve |
|---|---|
| Servicio sin bloqueo | Papelera activa, sin cambios. |
| Servicio con R1 (pago sin factura) | Papelera gris + chip "🔒 Ya tiene pagos al operador...". Si se fuerza: modal con botón "Emitir factura". |
| Servicio con voucher emitido | Papelera gris + chip "🔒 La reserva tiene vouchers emitidos...". Si se fuerza: modal con botón "Ver vouchers de la reserva". |
| Servicio con factura viva y sin cliente asignado | Papelera gris + chip con el texto real. Si se fuerza: modal solo con "Entendido" (sin camino, backlog). |
| Edición bloqueada por candado de Confirmada | Cartel de error + botón "Pedí autorización" (abre el modal ya existente). |
| Edición bloqueada por otro motivo (CAE/voucher) | Cartel de error de siempre, sin botón extra. |

## Qué NO hay que hacer (T7)

- NO usar tooltip como fuente del motivo del pre-chequeo.
- NO mostrar siempre el mismo párrafo de "nota de crédito" en el modal: cada motivo tiene su propio texto y su propio botón (o ninguno, si no existe camino).
- NO inventar una pantalla de "asignar cliente" para tapar el hueco del caso (f): queda documentado como backlog.
- NO reescribir los 3 textos reales del backend (solo el ajuste de vocabulario "cancelar→anular" que ya trae la Tanda 4, y que T7 hereda).
- NO ofrecer "Ver facturas de la reserva" para el caso de voucher (bug actual): cada motivo con su botón correcto.

---

# TANDA 8 — "Deshacer multa" con impuestos: dar un camino (D3 firmada)

## Qué cubre la guía y el plan (citado)

1. **D3 firmada por Gastón (2026-07-18, §6.B):** *"'Deshacer multa' cuando tiene impuestos (Tanda 8): ¿a dónde te mando? ... Recomiendo: bandeja de revisión en Cobranzas con aviso claro — pero es tu operación, decidilo vos."* → Gastón la firmó junto con las otras 3 (D1/D2/D4) el mismo día. **No se repregunta.**
2. **Patrón de referencia YA construido en el MISMO componente** — `DeshacerMultaEmitidaInline.jsx`, caso "saldo a favor aplicado" (2026-07-16, P5=A): mensaje real del servidor tal cual + botón `Link` con `ArrowRight` que lleva a donde vive la resolución real (`"Ir a la cuenta del cliente"`). Detección **por código** (`error.payload.invariantCode`), nunca por texto — mismo patrón se aplica acá.
3. **"Ningún mensaje deriva a un rol que el usuario ya es"** (2026-07-08): el texto actual, *"Este caso lo tiene que revisar una persona"*, es exactamente ese error — Gastón, que ES el que está mirando la pantalla, no sabe si "la persona" es él mismo.
4. **La bandeja "Cobranzas" que pide D3 YA EXISTE:** el menú "Cobranza y Facturación" → `/facturacion` tiene la solapa **"Comprobantes por resolver"** (fusión de multas/cargos + NC por revisar, 2026-07-10), que es exactamente una bandeja de revisión de back-office dentro de Cobranzas, ya con permiso `cobranzas.invoice_annul`. **No se construye una bandeja nueva.**

**Conclusión T8:** cubierto por la D3 firmada + patrón ya construido en el mismo componente + destino ya existente. Sin preguntas nuevas.

## El caso puntual: `INV-UNDO-MANUAL`

`BookingCancellationService.cs:8427-8429` — cuando la Nota de Débito de la multa tiene impuestos provinciales (IIBB) asociados, `DeshacerMultaEmitidaInline` no puede resolverse solo (revertir los renglones pisaría los tributos sin reversarlos: fuga fiscal). Hoy tira:

```csharp
throw new BusinessInvariantViolationException(
    "El comprobante de esta multa tiene impuestos asociados. Este caso lo tiene que revisar una persona.",
    invariantCode: "INV-UNDO-MANUAL");
```

El `code` **ya existe** en el backend (no hace falta agregarlo). Falta: (a) mejorar el texto de la cola del mensaje (sigue siendo el fallback seguro si en algún otro lugar no se mapea por código) y (b) que el front, al detectar este código, agregue el botón de camino.

## Spec de pantalla

### Estado HOY

```
┌ Deshacer el comprobante de la multa ─────────────────── [x] ┐
│  [ ... motivo, confirmación en dos pasos ... ]                │
│                                                                  │
│  ⚠ El comprobante de esta multa tiene impuestos asociados.     │
│    Este caso lo tiene que revisar una persona.                 │
└─────────────────────────────────────────────────────────────┘
   ↑ no dice quién, ni desde dónde — callejón sin salida
```

### Estado NUEVO (mismo formato que el caso "saldo a favor aplicado" ya construido)

```
┌ Deshacer el comprobante de la multa ─────────────────── [x] ┐
│  [ ... motivo, confirmación en dos pasos ... ]                │
│                                                                  │
│  ⚠ El comprobante de esta multa tiene impuestos provinciales   │
│    incluidos. No se puede deshacer solo: hace falta que        │
│    alguien de Cobranzas y Facturación lo revise a mano.        │
│                                                                  │
│    [ Ir a Cobranzas y Facturación → ]                          │
└─────────────────────────────────────────────────────────────┘
```

- **Backend:** el texto pasa de *"Este caso lo tiene que revisar una persona"* a *"No se puede deshacer solo: hace falta que alguien de Cobranzas y Facturación lo revise a mano."* — ya no manda a un rol sin nombre, dice el área concreta. El `invariantCode: "INV-UNDO-MANUAL"` se mantiene igual (es el hook que ya usa el patrón de detección).
- **Front:** se agrega `esErrorRevisionManualAlDeshacerMulta(error)` en `undoDebitNoteLogic.js` — mismo molde que `esErrorSaldoAplicadoAlDeshacerMulta`, comparando `error?.payload?.invariantCode === "INV-UNDO-MANUAL"`.
- **El botón "Ir a Cobranzas y Facturación"** es un `Link` (mismo componente `react-router` que ya usa el caso de saldo aplicado) a `/facturacion?tab=comprobantes` — la solapa "Comprobantes por resolver" que ya existe. Mismo ícono `ArrowRight`, mismo estilo de botón secundario dentro del cartel de error.
- **El mensaje del servidor se muestra TAL CUAL** (no se reescribe en el front) — mismo criterio que el resto de los casos de este panel.
- **Todo lo demás del panel sigue igual:** motivo obligatorio, confirmación en dos pasos, permiso Admin-only — nada de esto se toca.

## Nota de backlog (no bloquea T8): la bandeja no lista este caso todavía como fila propia

"Comprobantes por resolver" hoy tiene DOS fuentes de filas (multas/cargos pendientes de emitir, y NC por revisar). **Este caso — una multa YA emitida cuyo "Deshacer" quedó trabado por impuestos — no es ninguna de esas dos**, así que no hay hoy una fila específica esperando a alguien de Cobranzas cuando llega a esa pantalla. El botón cumple literalmente lo que pide D3 ("aviso claro + enlace"), pero **NO promete que la reserva vaya a aparecer listada ahí** — eso exigiría un tercer tipo de fila con su propio read-model de backend, que el plan no pidió construir en esta tanda (T8 es "dar un camino", no "construir la resolución completa"). Si este caso empieza a repetirse, sumar una tercera fuente de filas a "Comprobantes por resolver" (mismo patrón que las otras dos) es la mejora natural — pero es una tanda aparte, con su propio backend.

## Checklist de estados (T8)

| Estado | Qué se ve |
|---|---|
| Deshacer sin impuestos asociados | Sin cambios (funciona como siempre). |
| Deshacer con impuestos asociados (`INV-UNDO-MANUAL`) | Mensaje mejorado (área concreta, no "una persona") + botón "Ir a Cobranzas y Facturación". |
| Otro motivo de bloqueo (multi-operador, `INV-UNDO-MULTIOP`) | Sin cambios en esta tanda — fuera del alcance pedido por el plan (solo B2d/`INV-UNDO-MANUAL`). |

## Qué NO hay que hacer (T8)

- NO construir una bandeja nueva.
- NO prometer que el caso aparece listado en "Comprobantes por resolver" (todavía no tiene fila propia — ver nota de backlog).
- NO reescribir el resto del panel de `DeshacerMultaEmitidaInline` (motivo, confirmación en dos pasos, permiso Admin) — nada de eso cambia.
- NO tocar el caso `INV-UNDO-MULTIOP` (multi-operador): mismo texto genérico "una persona" que tiene hoy, fuera de esta tanda.

---

# TANDA 9 — "Ver PDF"/"Enviar" de la devolución (T5 fiscal) da un camino

## Qué cubre la guía (citado)

1. **Ficha en línea → error en cartel dentro de la ficha, NUNCA toast** — regla Ronda 2 (2026-06-06) + su aplicación explícita en T1 de este mismo plan (2026-07-18): *"el mensaje va en un cartel dentro de la ficha, NO en un toast: un toast se pierde justo cuando el vendedor necesita leer con calma qué corregir."* `PartialCreditNoteEmissionPanel` es exactamente ese tipo de ficha (sección `DEVOLUCIÓN · SERVICIO ANULADO`, embebida en la reserva) — hoy sus errores de `pdf()`/`send()` usan `showError` (toast), que es la excepción a corregir.
2. **Patrón de cartel de error YA construido en el MISMO componente:** `guardMessage` (línea 168 del archivo) ya es un `<p role="alert">` en rojo que se muestra dentro del panel — hoy solo lo usa `emit()`. Se reusa el MISMO state para `pdf()` y `send()`, no se inventa un cartel nuevo.
3. **Reintentar en el mismo botón** — regla repetida en toda la guía (Ronda 2 y en cada tanda de T1-T2).
4. **Cada rechazo dice qué hacer ahora, con el detalle real cuando hay** — regla transversal del plan.

**Conclusión T9:** cubierto. Se reusa el `guardMessage` que el propio componente ya tiene.

## El agujero real

```js
const pdf = async () => {
  if (!creditNote?.publicId) return;
  try {
    const response = await api.get(`/invoices/${creditNote.publicId}/pdf`, { responseType: "blob" });
    // ...
  } catch {                                          // ← el error NI SIQUIERA se captura en una variable
    showError("No se pudo abrir la nota de crédito."); // ← toast genérico, se pierde a los 4 segundos
  }
};

const send = async () => {
  // ...
  try {
    await cancellationsApi.sendPartialCreditNote(cancellation.publicId);
    showSuccess("Nota de crédito enviada al cliente.");
  } catch {
    showError("No se pudo enviar la nota de crédito. Intentá de nuevo."); // mismo problema
  } finally {
    setSending(false);
  }
};
```

En un flujo de "hay que mandarle la devolución al cliente ya" (urgencia alta, plata de por medio), el usuario ve un globito que desaparece a los 4 segundos, sin el motivo real y sin quedar registrado en la pantalla para poder reintentar con calma.

## Spec de pantalla

### Estado HOY

```
[ 👁 Ver PDF ]  [ ✉ Enviar al cliente ]

  (click Ver PDF, falla)  → toast 4s: "No se pudo abrir la nota de crédito."
                              (se esfuma; nada queda en pantalla)
```

### Estado NUEVO (mismo cartel `guardMessage` que ya usa `emit()`, reusado acá)

```
[ 👁 Ver PDF ]  [ ✉ Enviar al cliente ]

  ⚠ No pudimos abrir la nota de crédito. Volvé a intentarlo apretando
    "Ver PDF" de nuevo.
```

- **Se reusa el state `guardMessage` ya existente** (mismo `<p role="alert">` en rojo, línea 168): `pdf()` y `send()` limpian `guardMessage` al arrancar y lo setean en el `catch`, igual que ya hace `emit()`.
- **El texto usa el detalle real cuando el backend lo manda** (vía `getApiErrorMessage(error, fallback)`, el mismo helper que usan T1/T3/T6). Si no hay detalle recuperable (error de red, respuesta sin cuerpo legible), cae al texto fallback:
  - `pdf()`: *"No pudimos abrir la nota de crédito. Volvé a intentarlo apretando 'Ver PDF' de nuevo."*
  - `send()` (fallo del envío): *"No pudimos enviar la nota de crédito. Volvé a intentarlo apretando 'Enviar al cliente' de nuevo."*
- **El aviso previo de `send()` que ya existe se mantiene, pero también pasa a `guardMessage` en vez de toast** (coherencia: nada de toast en esta ficha, ni para el pre-chequeo ni para el error real): *"La reserva no tiene un cliente con contacto para enviar la devolución."*
- **Reintentar es apretar el MISMO botón de nuevo** ("Ver PDF" / "Enviar al cliente") — no se agrega un botón "Reintentar" aparte, mismo criterio que T1 y T5 de este documento.
- **Loading state nuevo en "Ver PDF"** (hoy no tiene ninguno, a diferencia de "Enviar al cliente" que ya usa `sending`): mientras se pide el blob, el botón se deshabilita para evitar doble clic (mismo patrón que `sending` en `send()`). Texto del botón puede quedar igual (ícono `Eye`) con el estado disabled visual estándar de la app.
- **El éxito sigue como está:** `pdf()` abre la pestaña nueva sin cartel adicional; `send()` sigue mostrando `showSuccess` (el toast de ÉXITO SÍ es el patrón normal en toda la app — la regla "nada de toast" es específicamente para errores en fichas en línea, no para confirmaciones de éxito).

## Checklist de estados (T9)

| Estado | Qué se ve |
|---|---|
| Ver PDF, éxito | Se abre la pestaña nueva con el PDF. Sin cambios. |
| Ver PDF, falla | Cartel `guardMessage` con el detalle real o el fallback + botón "Ver PDF" sigue activo para reintentar. |
| Enviar, falta cliente/nota de crédito (pre-chequeo) | Cartel `guardMessage` (antes era toast) con el mismo texto de siempre. |
| Enviar, éxito | `showSuccess` (toast), como hoy. |
| Enviar, falla en el servidor | Cartel `guardMessage` con el detalle real o el fallback + botón "Enviar al cliente" sigue activo para reintentar. |

## Qué NO hay que hacer (T9)

- NO usar `showError` (toast) en ningún camino de error de este panel.
- NO crear un cartel de error nuevo: se reusa `guardMessage` tal cual está.
- NO agregar un botón "Reintentar" separado: el mismo botón de la acción reintenta.
- NO inventar un mensaje genérico fijo cuando el backend manda un detalle real: se usa `getApiErrorMessage` para mostrarlo.

---

# PREGUNTAS PARA GASTON

**Ninguna pregunta bloqueante.**

Las cinco tandas quedan resueltas con la guía + las decisiones ya firmadas (D1-D4, 2026-07-18) + patrones ya construidos en el propio código:

- **T5** reusa el bloque `bloqueadoPorDeuda` que `EmitirFacturaInline.jsx` ya tiene, extendido al caso "no facturable por estado", con la constante limpia que el backend ya tiene lista.
- **T6** aplica el precedente 2026-06-13 ("apagado + explicación al lado") a nivel de fila de pago, reusando los 5 mensajes reales que los guards del backend ya devuelven.
- **T7** aplica el MISMO precedente 2026-06-13 a nivel de fila de servicio (resolviendo el choque entre "tooltip" del plan y la regla dura "nada de tooltip" de la guía), y sirve el botón "Emitir factura" que Gastón ya aprobó en D1. El caso "sin cliente" queda documentado como límite conocido (no hay pantalla para asignar cliente en el producto hoy), sin inventar una.
- **T8** aplica literalmente la D3 firmada, apuntando al ÚNICO lugar que hoy es "bandeja de revisión en Cobranzas" (`/facturacion?tab=comprobantes`, ya existente), con el mismo patrón de botón que `DeshacerMultaEmitidaInline` ya usa para el caso de saldo a favor aplicado.
- **T9** reusa el cartel de error (`guardMessage`) que el propio panel ya tiene para `emit()`, extendiéndolo a `pdf()` y `send()`.

Dos notas quedaron marcadas como **backlog** (no como preguntas que frenen esta tanda): la bandeja "Comprobantes por resolver" no tiene todavía una fila propia para el caso de T8, y no existe una pantalla para asignar cliente a una reserva existente (caso "sin cliente" de T7). Si al implementar aparece algo que estas notas no contemplaron, se trae de vuelta antes de decidir solo.
