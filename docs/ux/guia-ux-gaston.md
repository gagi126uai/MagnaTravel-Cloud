# Guía de UX/UI de MagnaTravel — la palabra de Gastón

> **Qué es esto:** la única fuente de verdad sobre cómo se ve y cómo se usa el frontend.
> Cada regla acá salió de una respuesta de Gastón (dueño del producto), con fecha.
> **Nadie diseña nada que no esté cubierto acá: si falta, se le pregunta a Gastón primero.**
> La mantiene el agente `ux-ui-disenador`.

---

## Reglas generales (valen para toda la app)

- **(2026-06-05) Basta de formularios "aclarativos".** Nada de cartelitos explicativos, leyendas largas ni "(opcional)" repartidos por el formulario: confunden al usuario. El formulario muestra solo lo imprescindible; lo secundario va escondido detrás de un "Más detalles". Si un campo necesita explicación, el diseño está mal.
- **(2026-06-05) Lo "opcional" no se decide solo.** Qué campo es obligatorio y cuál no lo define el experto de dominio + Gastón, nunca el programador. Referencia validada: para cargar un servicio lo imprescindible es operador, fechas, pasajeros, costo, venta y moneda; el resto (confirmación del operador, régimen, etc.) puede ir después.

## Formularios

- **(2026-06-05) Sacar "Guardar esta tarifa para reusar" del formulario de servicio.** Gastón lo pidió antes y seguía estando (ServiceFormModal ~2554). Validado con experto de dominio: administrar el tarifario es trabajo de back-office, no algo que se hace en medio de una venta. Como mucho, un atajo discreto DESPUÉS de guardar; nunca un bloque dentro del form.
- **(2026-06-05) Conservar el conteo y cálculo de noches.** Entrada/salida → noches calculadas solas → noches × tarifa = total. A Gastón esa parte le gusta y se mantiene en cualquier rediseño.

## Listados y tablas

- **(2026-06-09) Total al pie de una lista con varias monedas: una línea de TOTAL por moneda.** Cuando una lista (ej. los servicios de una reserva) mezcla pesos y dólares, al pie va UNA sola línea de total con cada moneda separada por un punto medio: `TOTAL: $ 205.000 · US$ 450`. Nunca se suman pesos y dólares en un único número. Si la lista es de una sola moneda, el total se ve como hasta ahora (un solo número). Decisión de multimoneda, ver la sección "Multimoneda" más abajo (P2).

## Botones y acciones

- **(2026-06-08) Los iconitos de acción llevan la PALABRA al lado, SIEMPRE visible.** Nada de tooltip ni "apoyar el mouse para enterarse". Cada icono de acción muestra su texto pegado al lado, a la vista, en todo momento. Vale para los iconos de acción de la cabecera de la reserva y para los iconos de cada servicio de la lista. (Decisión de Gastón sobre la opción "Palabra siempre al lado".)
- **(2026-06-08) Wording de las acciones de la cabecera de la reserva** (cada una aparece solo en el/los estados donde corresponde; la palabra acompaña al icono que esté visible en cada estado):
  - **Perdida** (icono ⊗) — solo en Cotización / Presupuesto.
  - **Cancelar** (icono ⊘) — en En gestión / Confirmada / En viaje / A liquidar (estados con proceso fiscal).
  - **Volver atrás** (icono ↩) — donde se permite regresar de etapa.
  - **Eliminar** (icono 🗑) — solo en Cotización / Presupuesto (sin pagos).
  - **Archivar** (icono 🗄) — siempre que se pueda archivar.
- **(2026-06-08) Wording de los iconos de cada servicio de la lista:**
  - **Editar** (lápiz) — texto fijo "Editar".
  - **Tacho con texto dinámico:** "Borrar" si el servicio NO está confirmado por el operador (era un borrador, sin compromiso); "Cancelar" si YA está confirmado por el operador (hubo compromiso real, puede haber penalidad/plata del cliente). Esto respeta la decisión #9 del ciclo de vida (borrar vs. cancelar).
- **(2026-06-08) Estos textos visibles NO reemplazan la accesibilidad:** el icono mantiene además su etiqueta para lectores de pantalla. El texto que ve el vendedor y la etiqueta accesible dicen lo mismo.

## Ventanas emergentes y avisos

**Regla general: "arriba la foto, abajo solo lo que hay que hacer" (2026-07-05, respuestas 1C/2B/3A/4B/5A).**

> **Origen:** la ficha de reserva (`ReservaDetailPage`) había llegado a apilar hasta 5 banners
> full-width + badges del header que decían lo mismo dos veces (chip Y banner para el mismo dato).
> Gastón aprobó un diseño concreto para ordenar eso, respondiendo por opciones (1C, 2B, 3A, 4B, 5A).

- **(2026-07-05, respuesta 1C — "mixto")** La ficha muestra, debajo del header, una **tira de avisos**
  con dos categorías, nunca mezcladas:
  - **ACCIONABLES**: piden que el vendedor haga algo AHORA (ej. "Dar OK" a un cambio de precio, "Pedí
    autorización" para editar). **Siempre visibles**, nunca plegados.
  - **INFORMATIVOS**: no piden ninguna acción inmediata (ej. "3 servicios sin confirmar", "capacidad
    excedida"). Van **plegados por defecto** en una barra **"N avisos más [Ver ▾]"**. Al hacer clic se
    despliegan en la misma página (sin modal), con un chevron que rota. El plegado **no se persiste**:
    arranca cerrado en cada carga de la ficha (más simple, y el vendedor lo abre en dos clics si hace
    falta). **Si hay un solo aviso informativo, se muestra DIRECTO, sin el plegado** — pedirle un clic
    extra para ver un único aviso es más fricción que mostrarlo de una.
- **(2026-07-05, respuesta 2B — "sin duplicados chip-vs-banner")** Ningún dato de la reserva se dice
  DOS VECES entre un chip del header y un banner de la ficha. Se **eliminan** dos banners que quedaban
  duplicados con su chip equivalente (misma condición de encendido, verificada antes de borrar):
  - El banner rojo **"No puede viajar todavía: hay saldo pendiente del cliente"** — queda SOLO el chip
    rojo **"Debe — no viaja"** del header (`ReservaStatusChips`), que ya decidía con la misma condición.
    **Esto CAMBIA lo decidido el 2026-06-21** (sección ADR-037, punto 3), que pedía cartel **+** chip: a
    partir de acá, **solo el chip**.
  - El banner ámbar **"🔧 En corrección — pendiente revisar fechas"** — queda SOLO el chip **"En
    corrección"** del header, misma condición exacta (`reserva.isUnderCorrection === true`). **Esto
    CAMBIA lo decidido el 2026-06-22** (sección "Tanda 2 — Sacar de viaje"), que agregaba chip **y**
    cartel: a partir de acá, **solo el chip** (el texto completo del aviso pasa a vivir en el
    title/tooltip del chip).
- **(2026-07-05, respuesta 3A — "accionable grande")** El banner ADR-027 **"Se editaron precios o
  costos..."** (con el detalle de cambios y el botón **"Dar OK"**) **queda exactamente como estaba**:
  grande, siempre visible, arriba de la franja del candado. Es el aviso más importante de la tira
  (afecta el saldo a cobrar) y no compite con nada más.
- **(2026-07-05, respuesta 4B — "candado a una línea")** La franja del candado (`ReservaLockBanner`) se
  achica a **una línea fina**: ícono + texto corto ("Reserva confirmada. Para cambiar algo, pedí
  autorización.") + botón **"Pedí autorización"** a la derecha. Las otras dos variantes del mismo
  componente (regresión naranja, destrabada verde) pasan al mismo formato de una línea, con su mismo
  contenido esencial — sigue valiendo la prioridad regresión > destrabada > candado.
- **(2026-07-05, respuesta 5A — "informativos plegados")** Van DENTRO del plegado "N avisos más": el
  aviso de **servicios sin confirmar** (`UnconfirmedServicesBanner`) y el de **capacidad excedida**
  (`CapacityWarning`). Los carteles de **estado terminal / en viaje / pregunta de multa** (Perdida,
  Anulada, Finalizada, En viaje, esperando reembolso) **NO** se pliegan: son "la foto" del estado, son
  mutuamente excluyentes entre sí y van primero en el orden visual. Los avisos de guía de flujo en
  etapas tempranas (Cotización, Presupuesto, franja de nombres de pasajeros en En gestión) tampoco se
  pliegan: son orientación normal del flujo, no alertas.
- **Orden visual resultante (de arriba hacia abajo, cuando aplican):**
  1. Carteles de estado terminal / en viaje / pregunta de multa (sin cambios de contenido).
  2. Banner "con cambios" + "Dar OK" (accionable, grande — respuesta 3A).
  3. Franja del candado en una línea (accionable — respuesta 4B).
  4. Barra plegada "N avisos más" (informativos — respuesta 5A). Si no hay ninguno, la barra no aparece.
- **Implementación de referencia:** la decisión de qué aviso es informativo vive en un helper puro
  (`avisosFicha.js`) para que el contador de la barra y el propio aviso nunca diverjan entre sí.

## Navegación

- **(2026-07-08) FIN DE LAS BANDEJAS POR TIPO DE COMPROBANTE — un solo monitor de excepciones fuera
  del menú de vender.** Origen: Gastón, probando en vivo, "no hay ningún lado donde ver las multas".
  La investigación del erp-systems-expert validó su reclamo: tener TRES puertas de menú distintas por
  tipo de comprobante (NC por revisar / ND por revisar / Reconciliación NC) es un anti-patrón; los ERP
  reales usan **UN monitor de excepciones** (lo que quedó trabado con AFIP), fuera del flujo de vender,
  + estado visible en el propio documento + auto-reintento. Decidido por Gastón ("dale") el 2026-07-08:
  - **DESAPARECEN del módulo VENTAS** las entradas **"NC por revisar"** y **"Reconciliación NC"**.
    (La "ND por revisar" que se había propuesto horas antes NO llega a existir: era una cuarta puerta.)
  - **ENTRA UNA sola entrada: "Pendientes con AFIP"** (nombre aprobado por Gastón), en el módulo
    **GESTIÓN** (el de back-office/administración: Aprobaciones, Mis solicitudes, Comisiones,
    Administración, Configuración). NO va en un módulo de vender: es una pantalla de vigilancia de
    back-office, no una acción de venta.
  - **Visible para quien pueda ver AL MENOS una de las tres solapas** (`cobranzas.invoice_annul` O
    `cobranzas.view_all` O `approvals.review`). Adentro, cada solapa respeta su propio permiso.
  - Es un **contenedor liviano con solapas** que por ahora muestra las tres pantallas existentes tal
    cual (multas y cargos / notas de crédito / recibos por regularizar). La fusión REAL en una sola
    lista de excepciones con auto-reintento queda para la fase 2 (rediseño). Esto de esta noche es solo
    juntar las tres puertas en una y sacarlas del camino de vender.

## Monitor "Pendientes con AFIP" y el aviso de multa trabada en la ficha (2026-07-08)

> La multa del operador (que en este producto SIEMPRE se traslada 1:1 al cliente) se resuelve desde la
> ficha con los botones "Sí cobró / No cobró". Cuando Gastón elige "Sí cobró" se emite una nota de
> débito; si esa nota de débito se traba (queda en revisión o falla), ANTES la multa desaparecía de la
> ficha (caía en el cartel simple "Reserva anulada — solo lectura") y no había dónde retomarla. Estas
> decisiones tapan ese agujero y viven junto al nuevo monitor.

- **(2026-07-08) Una nota de débito de multa que quedó trabada se ve TAMBIÉN en la ficha de la
  reserva anulada, con un cartel accionable y un botón para ir a resolverla.** Gastón lo eligió
  explícitamente ("en la ficha también", contra "solo en administración"). Es la excepción a la
  "pantalla muerta" de la anulada, igual que el paso de la multa (familia de decisiones del 2026-07-04).
- **(2026-07-08 — CERRADO) El cartel lo ve solo quien puede resolverlo** (`cobranzas.invoice_annul`,
  back-office fiscal). Un vendedor común sin ese permiso NO lo ve — sigue viendo el cartel simple
  "Reserva anulada — solo lectura", como hasta ahora. (Gastón aprobó el paquete con la recomendación A:
  "quien puede resolver", no "solo Admin".)
- **(2026-07-08) Color del cartel: naranja** (el de "algo se trabó, hay que hacer algo"), distinto del
  rosa de "anulada / solo lectura" (estado terminal, sin acción). Coherente con la franja naranja de
  "anulación en revisión".
- **(2026-07-08) La acción es un botón "Ir a resolver" que lleva al monitor "Pendientes con AFIP" con la
  solapa de multas ya abierta.** No se arma un resolver-en-línea nuevo en la ficha: el monitor ya tiene
  los botones para confirmar el monto o reintentar la emisión.
  **⚠️ SUPERSEDED esa misma madrugada — ver abajo "El paso de multa vive en la ficha".**

## El paso de multa vive en la ficha; las bandejas son listas pasivas (2026-07-08, madrugada)

> Origen: Gastón chocó contra el flujo actual. La bandeja le abrió un formulario que le pedía un "tipo de
> cargo" que no entiende, solo aceptaba pesos con su multa en dólares, y le respondió "Este cargo ya fue
> confirmado... hablá con administración" — **a él, que ES la administración**. Dijo: "ni sé para qué
> sirve esa bandeja y para qué la quiere un usuario". Diseño de fondo CERRADO por él.

**Principios cerrados (no reabrir):**
1. **El producto contempla todo, pero la complejidad se esconde con defaults; jamás se pregunta.** El
   "tipo de cargo" NO se pregunta nunca en el camino normal: por dentro va el default = **multa del
   operador trasladada 1:1 al cliente**. La capacidad "cargo propio de la agencia" existe, pero como
   **acción secundaria separada y clara, fuera del camino normal**.
2. **La resolución vive en la FICHA**, con la única acción que aplica al estado real. Las **bandejas son
   listas pasivas**: cada fila = link a la ficha, SIN botones de acción propios.
3. **Ningún mensaje deriva a un rol que el usuario ya es** (nada de "hablá con administración").

- **(2026-07-08) UNA sola experiencia para la multa, venga de donde venga: la de la ficha**
  (`ConfirmarMultaOperadorInline` — monto + moneda **precargada de la factura de la reserva**, editable;
  SIN pregunta de tipo de cargo). El modal de la bandeja (`ConfirmPenaltyModal`, que pedía tipo de cargo y
  solo pesos) **se saca del camino normal**.
- **(2026-07-08) La moneda de la multa manda la moneda de la nota de débito.** Se elimina la letra chica
  que decía que "la moneda elegida es solo para registrar" y que la ND salía en otra moneda: la ND al
  cliente sale en la MISMA moneda que se registró (era la causa del bloqueo "moneda DOL").
- **(2026-07-08) Cada estado trabado se resuelve EN la ficha con UNA acción puntual**, en criollo, sin
  mecánica interna: "Emitir la nota de débito ahora" / "Corregir monto y moneda" (un solo paso que
  reemplaza el circuito waive→deshacer→re-confirmar) / "Reintentar". Un cartel por estado.
- **(2026-07-08) La solapa "Multas y cargos" es una lista pasiva:** columnas mínimas (reserva · qué falta
  en criollo · hace cuánto), cada fila linkea a la ficha. Se le sacan los botones de acción y el "tipo de
  cargo". Nunca muestra el texto crudo del error de AFIP: muestra "qué falta" en criollo.
- **(2026-07-08) "Cobrar un cargo propio de la agencia" es una acción secundaria** en la sección de
  facturación de la ficha (link discreto "Cobrar un cargo de la agencia…"), gateada por su permiso. NO
  vive en la bandeja ni en el camino normal.

## Colores y estilo

_(pendiente)_

## Textos

_(pendiente — coordinar con el agente `ux-ui-travel-retail`, que cuida que la app no hable en jerga técnica)_

---

# Ciclo de vida de la reserva (REDISEÑO — decisiones de Gastón, 2026-06-07)

> Sesión de preguntas estructuradas (12 + 6 de dominio). Reemplaza el modelo anterior
> (Budget→Sold→Confirmed con flag EnableSoldToSettleStates): "Vendida" MUERE.
> Pendiente de diseño técnico (ADR-020) e implementación. Cada punto es decisión textual del dueño.

**El ciclo:**
```
COTIZACIÓN (borrador interno del vendedor)
   ↓ botón [Pasar a presupuesto]
PRESUPUESTO (lo recibe el cliente) ──→ PERDIDO (no compró; queda en historial)
   ↓ botón [El cliente aceptó]   (la plata viene después, el "sí" alcanza)
EN GESTIÓN (se solicitan los servicios a los operadores)
   ↓ AUTOMÁTICA al quedar TODOS los servicios resueltos
CONFIRMADA 🔒
   ↓ automática el día del viaje
EN VIAJE → FINALIZADA          └→ [Apartar para liquidar] (manual, opcional, se mantiene)
CANCELADA: desde cualquier etapa (cotiz/presup → Perdido sin proceso de plata;
           en gestión/confirmada → proceso completo de penalidades y reembolsos).
```

- **Nacimiento: SIEMPRE como cotización**, sin excepciones. NUNCA se puede crear una reserva directamente Confirmada (bug actual señalado por Gastón). Avanzar es rápido; saltear no se puede.
- **Cotización y presupuesto son DOS pasos distintos**: cotización = números rápidos/borrador (capaz ni se manda); presupuesto = documento armado que el cliente recibe y evalúa.
- **Presupuesto → En gestión: cuando el cliente dice "sí"** (botón explícito). La plata no define este paso.
- **CONFIRMADA = todos los servicios RESUELTOS** (definición por tipo, opción B del experto de dominio):
  - Aéreo = ticket EMITIDO (el PNR confirmado NO alcanza — time limit, tarifa no garantizada).
  - Hotel / Paquete = confirmado por el operador.
  - Asistencia = voucher emitido.
  - Traslado = confirmado por el operador O marcado a mano "no requiere confirmación" (la marca la pone CUALQUIER VENDEDOR, con registro de quién y cuándo — destraba el "traslado mudo").
- **El paso a Confirmada es AUTOMÁTICO** (al resolverse el último servicio) y la VUELTA también: si se agrega un servicio nuevo solicitado, o un operador cancela/reprograma un servicio, el file **vuelve solo a En gestión + aviso fuerte al vendedor** (el candado se abre porque ya no está todo asegurado).
- **Confirmación CON CAMBIOS (otra habitación/otro precio): vuelve al cliente.** El servicio queda solicitado con la propuesta; recién cuando el cliente acepta pasa a confirmado con los valores nuevos (porque su saldo cambia).
- **PLATA: el saldo del cliente nace POR SERVICIO CONFIRMADO.** Un servicio solicitado NO genera deuda; al confirmarse, su precio de venta suma al saldo a cobrar. (Bug actual señalado: "en pendiente aparece dinero cuando está solicitado".) La fecha de confirmación de CADA servicio se guarda siempre (las penalidades de cancelación corren por servicio desde esa fecha).
- **Edición: LIBRE hasta Confirmada** (cotización/presupuesto/en gestión: cualquier vendedor en sus reservas toca servicios, precios, fechas). **Confirmada = candado: bloqueada salvo autorización explícita** (queda registrado quién autorizó, qué cambió y por qué; vale para todos, admin incluido).
- **"En gestión" muestra el detalle por servicio de un vistazo** (hotel ✔, aéreo pendiente de emitir, traslado mudo) — no hace falta estado "confirmada parcial".

**Borrar vs. cancelar un servicio (aprobado por Gastón 2026-06-07 — "dale y terminalo"):**
- **Manda EL SERVICIO, no la etapa de la reserva.** Servicio que todavía NO confirmó el operador → se BORRA del todo, libre (era un borrador, sin compromiso con nadie). Servicio YA confirmado por el operador → NO se borra: se CANCELA (queda en la reserva tachado, con quién y cuándo — hubo compromiso real, puede haber penalidad o plata del cliente). Su monto se resta solo de la deuda del cliente.
- Si la reserva está Confirmada (candado), tanto borrar como cancelar piden autorización registrada primero.
- "Vendida" muere con el rediseño; la traba actual "no se puede eliminar el servicio: la reserva está en estado 'Sold'" desaparece de raíz. El rediseño se construye DIRECTO, SIN llave nueva (regla de Gastón 2026-06-07: "basta de llaves, esto es un producto").

**Pantallas del ciclo de vida — 10 decisiones de UI (aprobadas por Gastón 2026-06-08, "dale, me cierra todo"):**
1. **Candado de una Confirmada:** candadito 🔒 al lado del estado **+** franja explicativa arriba ("🔒 Reserva confirmada. Para cambiar algo, pedí autorización.").
2. **Editar una reserva trabada:** el vendedor común ve "Pedile a un administrador que la destrabe"; el **admin** escribe el motivo y la destraba **entera por 30 minutos** (no servicio por servicio).
3. **Botones de "resolver" servicio:** en la misma fila del servicio, a la derecha. Textos: aéreo = "Marcar emitido"; traslado = "No requiere confirmación".
4. **"En gestión" de un vistazo:** resumen arriba ("1 de 3 servicios resueltos") + pelotita de color por fila (🟢 resuelto / 🟡 pendiente con la palabra de qué falta).
5. **Dos números de plata:** "SALDO A COBRAR" grande (solo lo confirmado) y debajo, chiquito, "de $X presupuestado".
6. **Regresión automática a En gestión:** franja naranja en la reserva **+** aviso en la campanita, con el motivo ("El operador canceló el aéreo").
7. **Marcar "Perdido":** botón discreto + confirmación "¿Seguro?" + campo de motivo **opcional** (queda en historial).
8. **(Negocio) Cancelación que vino del operador NO pide autorización de candado:** registrar que el operador canceló un servicio se puede hacer sin destrabar (es informar algo que pasó afuera, no una decisión de la agencia). La autorización se pide solo si después se quiere cambiar otra cosa.
9. **Papelera de un servicio:** el sistema decide solo — no confirmado → "¿Borrar?" (desaparece); confirmado → "¿Cancelar?" (queda tachado), con motivo **opcional**.
10. **Colores de las 3 etapas nuevas:** Cotización = gris claro (borrador); En gestión = celeste/cian (en movimiento); Perdido = gris oscuro/tachado. Las etapas que ya existían no cambian de color.

# Reglas por pantalla

## Carga de servicios de una reserva (ServiceFormModal)

**Estado: RECHAZADA por Gastón (2026-06-05).** La versión "elegir producto primero" en todos los tipos no le gustó, y **el modal como formato también está rechazado** ("me parece horrible el modal"). Quiere algo moderno y funcional.

Decisiones ya tomadas (2026-06-05, elegidas por Gastón sobre los dibujos de `docs/ux/mockups/2026-06-05-agregar-servicio.html`):
- **El modal se reemplaza por la PROPUESTA C: carga en línea.** La ficha de carga se abre debajo de la lista de servicios de la reserva (sin ventana, sin cambiar de página); al guardar se convierte en una fila más de la lista.
- Se mantiene el conteo/cálculo de noches (entrada/salida → noches solas → noches × tarifa = total).
- **Hotel: la tarifa se carga POR NOCHE** y el sistema multiplica. (No total de estadía.)
- **Avisos de fechas límite: SÍ, los dos** — fecha límite de pago/seña al operador y fecha límite de emisión del aéreo. El sistema debe avisar.
- Se elimina "Guardar esta tarifa para reusar".
- Solo campos imprescindibles a la vista (operador, fechas, pax, costo, venta, moneda); lo demás detrás de "Más detalles". Sin textos aclarativos ni "(opcional)".

**EL CAMBIO DE LÓGICA que Gastón había pedido y no se hizo (2026-06-05, ya identificado):**
**El tarifario se arma solo a base de las reservas.** Nadie carga el tarifario aparte ("a la gente le da paja"): el vendedor escribe en un buscador inteligente; si el producto existe lo elige, y si NO existe lo crea desde el mismo lugar donde agrega el servicio (con su operador y datos), todo en la misma operación de venta. Respuestas de Gastón ese día:
- **Qué queda guardado:** el producto (ej. hotel) + operador + **precio de referencia editable** (sugerencia para la próxima vez, los precios cambian). No tarifa firme.
- **Alcance: TODOS los tipos** (hotel, aéreo, traslado, paquete, asistencia) desde el arranque.
- **Duplicados:** "el sistema tiene que ser tan inteligente y hacer lo imposible para evitar duplicados" (textual). Diseño: búsqueda tolerante a errores de tipeo, mostrar parecidos SIEMPRE antes de permitir crear, crear-nuevo como última opción, y pantalla de administración para revisar/unir duplicados que se cuelen.
- **Permisos:** cualquier vendedor crea al vuelo, pero lo nuevo queda marcado "creado en venta" para revisión posterior. No frena la venta.
- Al elegir un producto existente, el sistema precarga operador y precio de la última venta como sugerencia visible (editable, marcada en amarillo).

Dibujo fino de todo esto: `docs/ux/mockups/2026-06-05-agregar-servicio-detalle-C.html` (4 momentos + tabla de campos por tipo).

**✅ APROBADO POR GASTÓN (2026-06-05): "Sí, me encantó."** Ese dibujo es la especificación. Los implementadores lo siguen al pie de la letra; cualquier desvío necesario (por costo técnico o regla de negocio) se le repregunta a Gastón ANTES de desviarse, nunca se decide solo.

Decisiones adicionales de Gastón (2026-06-05, ronda arquitectura):
- **Precios en el buscador para quien NO tiene permiso de ver costos:** se le muestra el precio de **VENTA** de la última vez (nunca el costo; tampoco dejarlo sin precio). Quien sí tiene permiso ve el costo como en el dibujo.
- **Avisos de fechas límite:** los ve **cada vendedor para SUS reservas**; el admin ve todos. (Hoy la campanita era solo admin — esto cambia.)
- **Fuga vieja de costos: taparla.** La búsqueda del tarifario actual mostraba el costo a cualquier usuario logueado; quien no tiene permiso de ver costos no los ve en NINGUNA búsqueda.
- **Precio de referencia de hotel: por noche, POR HABITACIÓN.** El sistema recuerda el valor de una habitación una noche y multiplica por noches × habitaciones en la próxima venta.
- **Moneda del producto creado en venta: debe soportar tanto pesos como dólares** (textual). El producto nace con la moneda de esa venta, sea ARS o USD; nada de asumir dólares por defecto.
- **Ciudad OBLIGATORIA al crear un hotel desde la venta.** Es el arma principal contra duplicados ("Maitei Posadas" ≠ "Maitei Gesell").
- **Costo cuando vende un usuario que NO puede ver costos:** el sistema lo completa solo por detrás (última venta / tarifario), y quedan marcados "a confirmar" **solo los casos dudosos**: producto nuevo sin costo conocido, o costo que viene de una venta muy vieja (umbral sugerido ~60 días, ajustable). Los demás pasan derecho. Hasta confirmar, un caso dudoso no actualiza las sugerencias. (Gastón: "me gusta lo recomendado pero con mejor vuelta de rosca" → eligió esta variante.)
- **Buscador del catálogo (2026-06-05): lo usa cualquier usuario logueado.** Todos pueden buscar y ver nombre/ciudad/operador/precio de venta; el costo sigue tapado para quien no tiene `cobranzas.see_cost` (consistente con el buscador de tarifas actual).
- **Cuenta corriente del proveedor (2026-06-05): tapar montos sin permiso.** El vendedor sigue viendo la lista de proveedores y los servicios, pero saldos y costos solo con permiso `cobranzas.see_cost`. Regla general derivada: **quien no tiene permiso de ver costos no ve montos de costo/deuda EN NINGUNA pantalla** (tarifario, cotizaciones, cuenta corriente, avisos).
- **Costos negativos: BLOQUEADOS (2026-06-05).** El sistema rechaza cualquier costo (neto/impuesto) menor a cero, tanto al cargar como al confirmar. Protege ganancia y deuda al operador.
- **Confirmar costo en CERO: avisar antes (2026-06-05).** Si alguien con permiso confirma un costo en 0, el sistema pregunta "¿seguro? va a quedar costo 0 como sugerencia para todos" antes de guardar. (El 0 confirmado igual vale si lo aceptás — D8c — pero con aviso.)
- **Confirmar costo se permite aunque la reserva esté facturada (2026-06-05).** "Confirmar costo" solo corrige el costo interno (deuda al operador + tu ganancia); nunca toca la factura del cliente (que es por precio de venta). Por eso NO lo frena la inmutabilidad post-factura/voucher. (Distinto del tema general de inmutabilidad de la reserva, que sigue pendiente.)
- **UI del "costo a confirmar" (Q4, respondida 2026-06-05):** (a) el vendedor que lo generó **no ve nada** — la etiqueta ámbar la ven solo quienes pueden ver costos; (b) **la campanita avisa** "tenés N costos a confirmar" a quienes ven costos; (c) se confirma con **botón explícito "Confirmar costo"** (confirmás o corregís el número; imposible confirmar sin querer). Nada de confirmación implícita al guardar.

## Ficha de carga en línea F2 — decisiones de detalle (2026-06-06)

Ronda 1:
- **Editar un servicio de la reserva → desde la MISMA ficha en línea** (se abre con los datos puestos, lo corregís ahí). Una sola forma para crear y editar.
- **TEMA APARTE pendiente (Gastón: "habría que ver cómo hacer"):** "actualización de precios" es distinto del editar normal — charlarlo con Gastón antes de tocarlo. NO resolver solo.
- **Al abrir la ficha: TODOS los campos a la vista** desde el arranque (buscador + fechas + costo/venta + moneda visibles). NO revelado progresivo (eso era del form rechazado). Al elegir/crear un producto se completan operador y precio (sugerencias en amarillo) o aparece el recuadro de "producto nuevo".
- **Botón "Confirmar costo": en la misma fila** del servicio, al lado del costo (solo quien ve costos).
- **Etiqueta "costo a confirmar": pegada al costo**, texto corto "A confirmar", ámbar (solo quien ve costos).

Ronda 4 (2026-06-06):
- **Paquete: agregar campo "Fecha de fin"** (junto a "Salida"). Sin fecha de fin el sistema no sabe cuándo termina el viaje (se auto-cerraría o marcaría vencido antes de tiempo). Con la fecha de fin, el cierre automático funciona bien. (El backend igual deja EndDate opcional por seguridad/otros caminos, pero el form lo pide.)

Ronda 3 (identidad de los servicios no-Hotel, 2026-06-06):
- **Vuelo / Traslado / Paquete se identifican con UN SOLO CAMPO de búsqueda** (Ruta/aerolínea · Trayecto · Nombre del paquete), no con campos estructurados separados (origen/destino/aerolínea/nº de vuelo) como la pantalla vieja. Más simple, fiel al dibujo. Lo fino va en "Más detalles". El sistema resuelve por detrás los datos que necesita (sin sumar campos al vendedor). Asistencia: el plan también es un solo campo.
- **Consecuencia técnica (para el equipo):** los campos estructurados que hoy el backend exige (FlightSegment Origin/Destination/AirlineCode/FlightNumber/CabinClass; TransferBooking PickupLocation/DropoffLocation/VehicleType; PackageBooking Destination/EndDate) deben volverse OPCIONALES para el camino del catálogo, y el nombre del producto (texto del buscador) pasa a ser la identidad visible de la fila. Diseño a cargo del architect.

Ronda 5 (fila del servicio guardado en la lista — etiquetas y Confirmar costo, 2026-06-06):
- **La etiqueta de fecha límite se muestra SIEMPRE que la fecha esté cargada**, sin importar si el servicio está Solicitado o Confirmado. En servicios CANCELADOS no aparece. Si ya se señó/emitió y la etiqueta molesta, se edita el servicio y se borra la fecha — no hay lógica oculta que la apague sola.
- **Etiqueta de fecha límite VENCIDA: en rojo, avisando que ya pasó.** Texto: "Venció señar el 10/07" / "Venció emitir el 15/07" (fecha en formato dd/MM). Mientras no venció, sigue la etiqueta ámbar del dibujo aprobado ("⏰ Señar antes del 10/07" / "⏰ Emitir antes del 15/07").
- **Confirmar costo: corrección EN LA MISMA FILA, nada se abre encima.** Al apretar "Confirmar costo", el costo de la fila se vuelve un casillero editable con el número actual ya puesto, más un casillero de impuesto al lado, y botones Confirmar / Cancelar en la misma línea. La ventana que frena al confirmar $0 ("¿Seguro?" · "Va a quedar costo $0 como sugerencia para todos." · Volver / Sí, confirmar) sigue aplicando sobre el valor final del casillero.
- ~~**Etiqueta violeta "creado en venta": CON el tipo de servicio.** Texto: "Hotel creado en venta" (la versión del dibujo aprobado, no la corta). Mismo patrón para los demás tipos: "Aéreo creado en venta", "Traslado creado en venta", "Paquete creado en venta", "Asistencia creada en venta".~~ **DEROGADO (Gastón, 2026-06-08):** "saca eso de Creado en venta, no tiene sentido que un usuario cualquiera vea eso, si fue creado en el tarifario o en la venta no es necesario mostrarlo." La etiqueta se eliminó de la lista de servicios (desktop y mobile).

Ronda 8 (avisos de fechas límite: AUTOMÁTICOS, 2026-06-06):
- **Los avisos de fechas límite pasan a ser AUTOMÁTICOS para TODAS las reservas** (textual: "quiero que tenga el mecanismo que está hoy en configuración para todas las reservas"). Funcionan como las "Alertas por reservas próximas con deuda" que ya existen: el sistema avisa solo, X días antes del inicio de cada servicio, sin que nadie cargue fechas a mano. Los días de anticipación se manejan desde Configuración ("Días de anticipación del aviso", ya construido).
- **El campo "Fecha límite" cargado a mano DESAPARECE de la ficha** (opción elegida explícitamente sobre la alternativa "las dos cosas"; la opción elegida decía: "el campo Fecha límite desaparece de la ficha").
- Detalles finos (qué fecha dispara por tipo, textos, qué pasa con las etiquetas de la fila, solapamiento con "reservas próximas con deuda") → resueltos en Ronda 9.

Ronda 9 (avisos automáticos, detalles finos — respuestas de Gastón, 2026-06-06):
- **UN aviso POR RESERVA** en la campanita (no por servicio): aparece cuando se acerca el primer servicio que empieza. Entrás a la reserva y ahí ves el detalle.
- **El aviso se apaga A MANO con un botón "Listo"** en el propio aviso ("ya lo gestioné, no me lo muestres más"). Cancelado nunca avisa (Ronda 5 sigue). ⚠️ Implica guardar el estado "aviso descartado" — diseño por el architect antes de construir.
- **Texto del aviso: fecha + cuenta regresiva juntas**: "⏰ Empieza el 12/06 (en 5 días)" (ámbar); el día que llega: "Empieza HOY 12/06" (rojo).
- **La columna "Avisos" de la fila QUEDA, calculada sola**: la etiqueta aparece únicamente cuando el servicio está dentro de los X días (mismo texto que el aviso); el resto del tiempo "—". Es POR SERVICIO (la campanita es por reserva).
- **Nombre: "Próximos inicios"**. Título de sección en campanita: PRÓXIMOS INICIOS. Llave en Configuración: "Avisos de próximos inicios" (reemplaza "Avisos de fechas límite (señar y emitir)", que quedó viejo); descripción nueva: "La campanita avisa unos días antes de que empiece cada reserva. Cada vendedor ve las suyas; los admins, todas." + casillero "Días de anticipación del aviso" igual.
- **El "Listo" apaga el aviso PARA TODOS** (es una tarea de la reserva: alguien la gestionó y listo). El sistema registra quién lo apagó y cuándo. Si después cambia la fecha del primer servicio, el aviso reaparece para todos.
- **Los PRESUPUESTOS NO avisan**: solo reservas vendidas/confirmadas entran a Próximos inicios (cambio respecto del mecanismo anterior, que incluía presupuestos — decisión explícita).
- **Segunda línea del aviso: Reserva + titular** ("Reserva R-1042 · Fam. García").
- **El aviso rojo "Empieza HOY" se ve durante TODO el día de inicio**, aunque el sistema ya haya pasado la reserva a "En viaje" a la medianoche (proceso automático). Desaparece al día siguiente o con "Listo". Es el último llamado de atención del día D.
- **La etiqueta de la fila es INFORMACIÓN, no aviso: aparece siempre que el servicio empiece pronto**, sin importar el estado de la reserva (presupuesto, en viaje — sirve p.ej. para apurar la venta de un presupuesto). Nunca en servicios cancelados. La campanita SÍ mantiene el criterio estricto (solo vendidas/confirmadas).

Ronda 10 (botón "Listo" de Próximos inicios, 2026-06-07):
- **El "Listo" es un BOTÓN con la palabra "Listo", siempre visible**, a la derecha de cada aviso de la campanita (elegido sobre la tilde-al-pasar-el-mouse y el texto-chiquito). Sin ventana de confirmación. El click en "Listo" no navega a la reserva; el click en el resto del aviso sí.

Vencimientos — confirmación de la regla y vencimiento de pasaporte (2026-06-13):
- **(2026-06-13) Las fechas de PAGO AL OPERADOR y de EMISIÓN del aéreo (time-limit) siguen siendo AUTOMÁTICAS. NO se vuelve a poner el campo "Fecha límite" a mano para esos dos.** Esto CONFIRMA y NO revierte lo decidido en la Ronda 8 (2026-06-06): el aviso sale solo por "Próximos inicios" (la campanita avisa X días antes del inicio de cada servicio, con los "Días de anticipación" de Configuración), sin que nadie cargue fechas a mano. El campo manual de fecha límite quedó eliminado de la ficha de carga y así se mantiene.
- **(2026-06-13) El VENCIMIENTO DE PASAPORTE del pasajero SÍ se carga a mano**, en los datos de cada pasajero (es un dato nuevo y no choca con lo anterior, porque no es una fecha de gestión interna de la reserva sino un dato propio del pasajero). La campanita muestra los pasaportes por vencer como una **sección propia** (aparte de "Próximos inicios" y de "Costos a confirmar"; misma idea de secciones apiladas de la Ronda 6).

Ronda 7 (obligatoriedad heredada de la pantalla vieja — probando en vivo, 2026-06-06):
- **REGLA GENERAL nueva de Gastón (textual): "que no asuma nada, nada que yo no le pida; que me pregunte así como lo hace el ux/ui; que no ponga campos que nadie le pidió y si eso lo hizo el backend o el arquitecto, que me pregunten primero".** La obligatoriedad de un campo es una decisión de producto de Gastón, NUNCA del código viejo ni de un implementador. Si una regla heredada choca con la ficha nueva, se le pregunta antes.
- **Hotel: Régimen y Tipo de habitación A LA VISTA y OBLIGATORIOS** en la ficha principal (salen de "Más detalles"), como desplegables con las mismas opciones de siempre (Régimen: Solo Alojamiento/Desayuno/Media Pensión/Pensión Completa/All Inclusive, default Desayuno; Habitación: Single/Doble/Triple/Cuádruple/Familiar, default Doble).
- **Aéreo: "Cabina" va en "Más detalles", OPCIONAL** (desplegable con Sin especificar/Economy/Premium/Business/Primera). El sistema deja de exigirla.
- **Traslado: "Tipo de vehículo" va en "Más detalles", OPCIONAL** (texto libre). El sistema deja de exigirlo.
- **"Más detalles" queda CERRADA por defecto** (se abre con un click), en todas las fichas.

Ronda 6 (campanita de avisos + llaves en Configuración, 2026-06-06):
- **Campanita: secciones apiladas en la misma ventanita.** Primero "Fechas límite", después "Tenés N costos a confirmar", abajo las notificaciones de siempre. Sin pestañas. Cada sección aparece solo si tiene avisos; sin avisos, la campanita se ve igual que hoy. Tocar un aviso lleva derecho a esa reserva.
- **El numerito de la campanita SUMA TODO**: avisos de fechas límite + costos a confirmar + notificaciones sin leer. Un solo número. (Los avisos de Cobranzas — salida próxima con deuda / deuda con proveedores — NO entran en este numerito: viven en sus tarjetas de Cobranzas.)
- **Llaves nuevas en Configuración → Funciones avanzadas** (textos aprobados tal cual): "Tarifario que se arma solo desde las ventas" (descripción: "Al cargar un servicio, el vendedor busca el producto y, si no existe, lo crea ahí mismo. Queda guardado con su operador y un precio de referencia para la próxima venta. Apagado, todo sigue como hasta ahora.") y "Avisos de fechas límite (señar y emitir)" (descripción: "La campanita avisa cuando se acerca o ya pasó una fecha límite de seña al operador o de emisión de un aéreo. Cada vendedor ve los avisos de sus reservas; los admins ven todos.") con el casillero "Días de anticipación del aviso: [7]" pegado a su llave.
- **La llave del tarifario se prende DIRECTO, sin ventanita** (como "Ciclo extendido"; la ventana de "¿seguro?" queda solo para lo fiscal). El "Guardar configuración" de la pantalla sigue siendo el paso final.

Ronda 2:
- **Si falla Guardar:** la ficha queda abierta con TODO lo cargado intacto + cartel rojo arriba de los botones ("No se pudo guardar. Revisá la conexión y probá de nuevo"); reintenta en el mismo botón. (Nunca se pierde lo cargado.)
- **Aviso de confirmar costo en CERO: ventana que frena** ("¿Seguro? Va a quedar costo $0 como sugerencia para todos" · Volver / Sí, confirmar). No cartel pasivo.
- **Buscador sin resultados: directo a crear** ("No encontramos '{texto}' en tu tarifario" + botón crear). Mientras busca, mostrar un "Buscando…" sutil (estándar).
- **Hotel: agregar campo "Cantidad de habitaciones"** a la vista (al lado de Noches). Total = noches × habitaciones × precio/noche. Coherente con "precio por noche POR HABITACIÓN" (el dibujo no lo mostraba; Gastón confirmó agregarlo 2026-06-06).
- **"Más detalles" por tipo: confirmado tal cual** — Hotel: Régimen · Tipo de habitación · Confirmación operador · Fecha límite de seña · Dirección. Aéreo: PNR · Nº ticket · Horarios/escalas · Equipaje. Traslado: Nº vuelo · Horario · Confirmación. Paquete: Qué incluye · Nº file. Asistencia: Nº voucher por pax · Upgrades. (Ajustable si después falta/sobra algo.)

## Estado de los servicios de una reserva — wording y reglas (2026-06-08)

- **(2026-06-08) Badge "En espera" vs. "Solicitado":** el badge de estado del servicio muestra textos distintos según la etapa de la reserva:
  - En **Cotización** y **Presupuesto**: muestra **"En espera"** (el operador todavía no sabe que existe este servicio; no se le pidió nada).
  - En **En gestión** y etapas posteriores: muestra **"Solicitado"** (ya se gestionó la solicitud al operador).
  - "Confirmado" y "Cancelado" (estados concretos que vienen del backend) siempre se muestran tal cual, sin importar la etapa.
  - Cualquier otro valor que venga del backend (ej. "Emitido", "HK") se muestra tal cual.

## Pasajeros de una reserva — reglas de negocio (2026-06-08)

- **(2026-06-08) Una reserva NUNCA puede tener 0 pasajeros:** no se puede avanzar de etapa (ej. de Presupuesto a En gestión) si la composición declarada es 0 adultos + 0 menores + 0 infantes. El sistema bloquea el botón y muestra aviso claro ("Tiene que haber al menos 1 pasajero"). El usuario debe ajustar la composición antes de continuar.

## Pasajeros: cantidad en el presupuesto, nombres por servicio (2026-06-15)

> Cambio de flujo decidido por Gastón + experto de dominio: el presupuesto pide solo la
> CANTIDAD de pasajeros; los NOMBRES se cargan más adelante, recién cuando hace falta para
> emitir/confirmar cada servicio. Estas decisiones REEMPLAZAN el comportamiento viejo del
> modal "Pasar a En gestión", que obligaba a cargar nombre + documento de todos al avanzar.
> Sigue valiendo la regla "nunca 0 pasajeros" (2026-06-08).

- **(2026-06-15) En el presupuesto se carga SOLO la cantidad, en tres casilleros: adultos / menores / infantes.** Son los tres casilleros que ya existen. Nada de nombres en esta etapa (P1).

- **(2026-06-15) Si la cantidad quedó en 0, el botón "El cliente aceptó" queda APAGADO** (gris, no se puede tocar) con el cartelito "Tiene que haber al menos 1 pasajero". El vendedor corrige la cantidad y el botón se prende solo (P2). (Refuerza la regla 2026-06-08.)

- **(2026-06-15) "El cliente aceptó" pasa DERECHO a En gestión, sin ventana.** Con cantidad correcta (≥1) y al menos un servicio cargado, al apretar el botón la reserva pasa a En gestión sin abrir ninguna ventana de nombres. **El modal de nombres al avanzar MUERE** (coherente con "el modal me parece horrible"). En su lugar, la reserva queda con un **cartelito recordatorio arriba** que dice: "Cargá los nombres de los pasajeros antes de emitir cada servicio." (P3).

- **(2026-06-15) Los nombres se cargan en DOS lugares (P4):**
  - **(a)** En la solapa **"Pasajeros"** de la reserva (la que ya existe) — es el lugar principal y tranquilo para cargarlos.
  - **(b)** Como **red de seguridad**, al ir a emitir/confirmar un servicio que los necesita: aparece un **mini-formulario EN LÍNEA** (debajo del servicio, dentro de la página, NUNCA una ventana flotante) que pide solo los nombres que falten. Mismo dato, dos puertas de entrada.

- **(2026-06-15) Aéreo: para emitir exige NOMBRE + DOCUMENTO de TODOS los pasajeros.** Si faltan, el botón "Marcar emitido" queda APAGADO con el cartelito "Cargá los nombres primero". El mini-formulario en línea (P4b) es donde el vendedor los carga; al completarlos, el botón se prende (P5). El aéreo es el ÚNICO servicio que exige todos los nombres + documento antes de avanzar.

- **(2026-06-15) Hotel y traslado se confirman con SOLO el TITULAR cargado.** No exigen todos los nombres como el aéreo (P6).

- **(2026-06-15) Los pasajeros van a TODOS los servicios automáticamente.** No hay paso de "elegir a mano quién va en cada servicio": todos los pasajeros declarados quedan en todos los servicios solos (P7).

- **(2026-06-15) Si se agrega un pasajero nuevo más tarde, se suma SOLO a todos los servicios.** Sin paso manual de asignación (P8).

- **(2026-06-15) La solapa "Pasajeros" con cantidad declarada pero sin nombres muestra renglones vacíos, uno por pasajero declarado:** "Adulto 1 — sin cargar", "Menor 1 — sin cargar", etc., cada uno con un botón [Cargar] (P9).

- **(2026-06-15) Contador "X de N nombres cargados".** Se muestra en dos lados: en la solapa Pasajeros y arriba de la reserva (junto al cartelito recordatorio de P3) (P10).

## Pasajeros: "solo para algunos" por servicio + autocompletado de la cantidad (2026-06-15, tarde)

> Segunda ronda del mismo día, DESPUÉS de cerrar el flujo de nombres diferidos de arriba.
> Gastón pidió dos cosas: (1) poder decir que un servicio es "solo para algunos" pasajeros
> (no todos), y (2) que el sistema sugiera la cantidad de pasajeros mirando los servicios.
> Todo EN LÍNEA, nada de ventanas flotantes (regla dura de siempre).

**Cómo queda P7/P8 (lo de la mañana) — IMPORTANTE, NO es una contradicción:**
- **El default NO cambia: todos los pasajeros van en todos los servicios, con CERO clics.** Eso de la mañana (P7/P8) sigue intacto: el vendedor no tiene que asignar a nadie a mano. Si no toca nada, el servicio es para todos.
- **"Solo para algunos" es una EXCEPCIÓN opcional y escondida.** Aparece como un control discreto en cada servicio; solo el que quiere acotar lo abre. El que no lo usa, ni se entera: su flujo es el de la mañana.
- En palabras simples: la regla sigue siendo "todos a todos"; ahora además SE PUEDE, si hace falta, decir que un servicio puntual es para 2 de los 3 pasajeros.

**1) Marcar "solo para algunos" en un servicio (P1 = sí, escondido y default todos):**
- **(2026-06-15) En la fila de cada servicio hay un control "Para: Todos".** Es discreto, no grita. Mientras dice "Para: Todos", el servicio es para todos los pasajeros (el default de siempre).
- **(2026-06-15) Al tocar "Para: Todos" se despliega EN LÍNEA, debajo, la lista de pasajeros de la reserva con tildes** para elegir quiénes van en ese servicio. **Por defecto vienen TODOS tildados** (destildar es la acción de acotar). Nada de ventana flotante: se abre dentro de la página, como todo lo demás.
- **(2026-06-15) Si TODAVÍA no hay nombres cargados, el control dice "Para: Todos — cargá los nombres para elegir"** y NO deja acotar: no se puede elegir "solo algunos" hasta que existan los pasajeros con nombre. (No se acota por cantidad/tipo: se acota por pasajero concreto, así que primero tienen que existir.)
- **(2026-06-15) Cuando el servicio quedó acotado, el control muestra el conteo del set: "Para: 2 de 3".** Si está en todos, dice "Para: Todos" (no muestra número).

**2) Qué nombres pide cada servicio según su set (modelo cerrado en ADR-031 v2.1):**
- **(2026-06-15) El subconjunto de un servicio lo determinan SOLO las tildes (asignaciones explícitas).** Servicio sin tocar = para TODOS. La cantidad propia del servicio (ej. "2 adultos + 1 menor" del hotel) NO achica a quién se le piden nombres; esa cantidad propia solo sirve para la sugerencia del total de la reserva (ver punto 3).
- **(2026-06-15) Los nombres que pide un servicio para emitir/confirmar son los de SU set:** si está en "Para: Todos", pide los de todos los pasajeros; si está acotado, pide solo los de los pasajeros tildados. Ejemplos: excursión con 2 adultos tildados → pide 2 nombres; aéreo "Para: Todos" en una reserva de 3 → pide 3. El mini-formulario en línea de nombres (el de la mañana, P4b) trabaja sobre ESE set, no sobre todos.

**3) Sugerencia de cantidad de pasajeros desde los servicios (P3 = solo avisa, no pisa):**
- **(2026-06-15) El sistema sugiere la cantidad mirando los servicios, pero NUNCA la pisa.** Cuando deduce una composición a partir de los servicios cargados, muestra una franja tipo "💡 Por los servicios, parece que viajan 2 adultos + 1 menor" con un botón "Usar". El que decide es el vendedor: confirma con "Usar" o lo deja como está y ajusta a mano.
- **(2026-06-15) "Usar" completa los casilleros de cantidad con lo sugerido.** No se autollena solo, ni siquiera si la cantidad está en 0: siempre hace falta que el vendedor toque "Usar". Lo que ya cargó a mano no se sobrescribe sin que él lo pida.
- **(2026-06-15) La franja de sugerencia NO aparece si la cantidad ya coincide con lo que sugieren los servicios** (no molesta cuando no hay nada que sugerir).

# Multimoneda — pesos y dólares (decisiones de Gastón, 2026-06-09)

> Sesión de 8 preguntas con dibujos. El sistema empieza a mostrar dos monedas (pesos ARS y dólares USD)
> en todas las pantallas de plata. Gastón eligió todas las opciones recomendadas.
> Diseño técnico que lo sostiene: `docs/architecture/adr/ADR-021-multimoneda-por-reserva.md`.
>
> **Tres reglas duras que NO se rompen en ninguna pantalla (vienen del negocio/contador, no son de UX):**
> 1. **Las monedas van SIEMPRE separadas. NUNCA se suman ni se convierten a una sola "moneda base"** en las pantallas que usa el vendedor (pesos por un lado, dólares por el otro).
> 2. **El sistema NUNCA muestra "diferencia de cambio"** en ninguna pantalla. Esa palabra no aparece.
> 3. **Si una reserva (o un cliente, o un proveedor) maneja UNA sola moneda, se ve EXACTAMENTE como hoy.** La segunda moneda solo aparece cuando de verdad hay dos. No se mete ruido de doble moneda donde hay una.

- **(2026-06-09) Saldo de una reserva con dos monedas: dos saldos lado a lado (P1).** Cuando la reserva tiene plata en pesos y en dólares, la cabecera muestra DOS saldos uno al lado del otro: pesos a la izquierda, dólares a la derecha. Cada uno con su "de $X presupuestado" chiquito debajo (igual que la regla #5 del ciclo de vida, pero duplicada por moneda). Si la reserva es de una sola moneda, se ve un solo saldo como hasta hoy.

- **(2026-06-09) Subtotal de la lista de servicios: total por moneda al pie (P2).** Al pie de la lista de servicios de la reserva va una línea de TOTAL con cada moneda separada: `TOTAL: $ 205.000 · US$ 450`. Si todos los servicios son de la misma moneda, un solo número como hoy. (También quedó como regla general en "Listados y tablas".)

- **(2026-06-09) La ficha de cobro se abre DENTRO de la página, no en ventana encima (P3).** El cobro se carga en línea, debajo, igual que la carga de servicios (propuesta C aprobada). **Esto reemplaza la ventana de pago actual.** Coherente con "el modal me parece horrible": nada de ventana que se abre encima para cobrar.

- **(2026-06-09) Cobro cruzado (pagar en una moneda contra una deuda en otra): el recuadro de tipo de cambio aparece SOLO cuando cruza (P4).** Si el vendedor cobra en la MISMA moneda que el saldo al que imputa, no aparece nada raro (se cobra como siempre). Si la moneda del pago es DISTINTA de la moneda del saldo al que imputa, recién ahí aparece un recuadro con: tipo de cambio (1 US$ = $___), de dónde sale ese tipo de cambio (la fuente), la fecha, y la línea "Se cancelan US$ 100 de la deuda" (texto ajustable si Gastón después pide otro). **Los tres datos —tipo de cambio, fuente y fecha— son obligatorios cuando el pago cruza de moneda.** Cada pago es de una sola moneda contra un solo saldo; si el cliente paga mitad y mitad, son dos cobros.

- **(2026-06-09) Cuenta corriente del cliente: dos saldos arriba + una sola lista con la moneda en cada fila (P5).** Arriba, dos saldos: "DEBE EN $" y "DEBE EN US$". Abajo, una sola lista de movimientos (no dos listas), y cada fila dice en qué moneda fue. Si el cliente solo tuvo una moneda, se ve un solo saldo arriba y la lista de siempre.

- **(2026-06-09) Caja / arqueo: dos cajas separadas (P6).** Una "CAJA EN PESOS" y una "CAJA EN DÓLARES", cada una con su Entró / Salió y su total propio. Nunca un total mezclado. La caja registra lo que REALMENTE entró en cada moneda (en un cobro cruzado, entra lo que el cliente pagó de verdad; el saldo de la otra moneda baja por el equivalente, pero la caja no inventa pesos que no entraron).

- **(2026-06-09) Tablero financiero / reportes: dos columnas lado a lado (P7).** En cada tarjeta del tablero, una columna de pesos y una de dólares, una al lado de la otra. Los rankings de "quién debe más" son DOS listas separadas, una por moneda (no una lista mezclada).

- **(2026-06-09) Cuenta corriente del proveedor: mismo formato que la del cliente (P8).** Dos saldos arriba ("LE DEBO EN $" y "LE DEBO EN US$") + una sola lista de movimientos con la moneda en cada fila. Si el proveedor es de una sola moneda, se ve como hoy. Recordar la regla previa (2026-06-05): sin permiso de ver costos, los montos de deuda al proveedor van tapados.

- **(2026-06-09) Enmascarado de costos sigue valiendo en todo lo multimoneda.** Quien no tiene el permiso `cobranzas.see_cost` no ve costos ni deuda al operador en ninguna de estas pantallas, en ninguna de las dos monedas (regla general 2026-06-05). Lo que el cliente debe y lo que el cliente pagó SÍ se ve sin ese permiso.

## Multimoneda — corrección de mockups y decisiones finas (2026-06-10)

> Gastón revisó los dibujos del 2026-06-09 y avisó que "mezclaban las pantallas" y que varias no se parecían a su sistema real. **Las TRES reglas duras de arriba quedan intactas.** Lo que se corrige acá es CÓMO se dibujó cada pantalla: los mockups del 2026-06-09 (y el primer rehecho `v2`) habían inventado layouts que NO son los de la app. Se rehicieron calcando las pantallas reales del código. Mockups finales: `docs/ux/mockups/2026-06-10-multimoneda-v3-pantallas-reales.html` (pantallas 1/4/5/6) + `2026-06-10-multimoneda-v2.html` (pantallas 2/3, aprobadas).

- **(2026-06-10) Pantallas APROBADAS por Gastón:** la de **cobro** (solapa Estado de Cuenta: historial con columna Moneda + ficha de cobro en línea, caso normal y caso cruzado) y la **cuenta corriente del cliente** (dos saldos arriba "Debe en $/US$" + lista única con la moneda en cada fila). Quedan como en el mockup v2.

- **(2026-06-10) Las layouts P6/P7/P8 del 2026-06-09 quedan CORREGIDAS — eran de dibujos que no eran la app real.** Lo correcto, calcado de las pantallas reales, es:
  - **Caja (antes "dos cajas separadas"):** la pantalla real son **3 números arriba** (Ingresos del mes · Egresos del mes · Resultado de caja del mes) + una **lista de movimientos** (cobranzas entran, pagos a proveedor salen). Multimoneda = cada uno de los 3 números muestra pesos y dólares por separado (nunca sumados) + cada movimiento de la lista lleva su moneda + un filtro nuevo "Moneda" en la barra. NO son dos cajas.
  - **Cuenta corriente del proveedor (antes "dos saldos arriba como el cliente"):** la pantalla real son **5 tarjetitas** (Servicios, Pagos, Total Compras, Total Pagado, Saldo Pendiente con fondo rojo) + dos tablas (Servicios Comprados, Historial de Pagos). Multimoneda = las 3 tarjetas de plata (Compras, Pagado, Saldo) muestran las dos monedas; las tablas llevan el cartelito $/US$ por monto + columna Moneda en pagos. (El enmascarado de costos sin permiso sigue valiendo.)
  - **Reportes (antes "dos columnas por tarjeta"):** la pantalla real (solapa **Finanzas y Deudas**) son 4 tarjetas (Cobros, Pagos, Flujo Neto, Deuda Clientes) + dos listas, **Cuentas por Cobrar** y **Cuentas por Pagar**. Multimoneda = cada tarjeta muestra las dos monedas (una sobre otra) y en cada lista el monto lleva su $/US$. Las listas Cobrar/Pagar ya existen separadas; la moneda es una dimensión más dentro de cada una.
  - **Franja de la reserva:** la pantalla real son **3 números limpios** (Saldo a Cobrar · Recaudado · Inversión-solo-admin). Multimoneda = dentro de cada número aparecen las dos cifras, una arriba de la otra. Sin recuadros de comparación ni "antes/después" (eso fue invento del mockup, descartado).
  - **PENDIENTE:** estas 4 (reserva, proveedor, caja, reportes) esperan el OK final de Gastón sobre el mockup v3 antes de construir.

- **(2026-06-10) Decisiones finas confirmadas por Gastón:**
  - **Palabra "cobro" en todos lados** (unificar; hoy conviven "cobranza" y "pago"). Botón "Registrar cobro" abre la ficha; botón "Confirmar" guarda.
  - **Cobro cruzado en el historial: UNA sola fila** (el importe real que entró, con el detalle de a qué saldo imputó dentro de la misma fila). No dos filas.
  - **Fila de cuenta corriente con dos monedas: un renglón por moneda dentro de la misma fila** (no las dos pegadas en línea).
  - **Factura del aéreo en dólares: se emite y se muestra en dólares (US$)**, no el equivalente en pesos. **OJO: toca lo fiscal — confirmar con el contador antes de construir.**

## Multimoneda — OK de construcción y 4 decisiones finas de UI (2026-06-11)

> Gastón dio el **OK final del mockup v3** para construir las pantallas **1 (reserva), 5 (caja) y 6 (reportes)**. La **pantalla 4 (cuenta corriente del proveedor) queda postergada** (ver `docs/architecture/adr/ADR-021-POSTERGADO-pantalla-proveedor.md`). Backend (capas 4/5/6) ya construido y verde. Spec de frontend: `docs/ux/specs/2026-06-11-spec-frontend-multimoneda-1-5-6.md`.

- **(2026-06-11) Fila de "Total" al pie de la lista de servicios: SOLO si la reserva tiene 2 monedas.** Con una sola moneda no aparece (se ve igual que hoy).
- **(2026-06-11) Selectores nuevos del cobro ("Moneda del cobro" e "Imputar a"): se ESCONDEN en reservas de una sola moneda.** Aparecen solo cuando hay dos monedas (donde puede haber cobro cruzado). Con una sola moneda, el cobro se ve igual que hoy.
- **(2026-06-11) Editar un cobro cruzado ya cargado: solo NOTAS y MÉTODO de pago.** El monto, la moneda y el tipo de cambio quedan fijos (el backend ya los bloquea); si están mal, se anula y se rehace.
- **(2026-06-11) Filtro "Moneda" en la caja: SIEMPRE visible** (decisión de Gastón, distinta del default sugerido). Está siempre, aunque no haya movimientos en dólares.

## Tanda de frontend de las 6 features (2026-06-13) — se aprueba PANTALLA POR PANTALLA

> Gastón pidió ir "una por una": ve cada pantalla con su boceto y la aprueba antes de construirla. Acá se anotan las decisiones a medida que las va aprobando.

- **(2026-06-13) Caja — "este mes" con flechas.** Arriba de los 3 números de la Caja se agrega un encabezado de mes con flechas: `◀  Junio 2026  ▶`. Arranca SIEMPRE en el mes actual. ◀ retrocede un mes, ▶ avanza. **La flecha ▶ queda DESHABILITADA (gris) en el mes actual: no se puede ir a meses futuros** (no hay plata futura en la caja). Hacia atrás se navega libre. Los 3 números (Ingresos/Egresos/Resultado del mes) y la lista de movimientos pasan a ser del mes elegido; sigue valiendo la regla dura de no mezclar monedas. Reusa el componente de navegación de mes que ya usan otras pantallas (trae un botón "Hoy" para volver rápido al mes actual; OK de Gastón pendiente de confirmar verbalmente, es coherencia con el resto del sistema). No se agrega calendario ni rango de fechas.

- **(2026-06-13) Cancelación — pantallas (aprobado por Gastón).** Decisiones:
  - **Contador "N de M servicios cancelados"**: línea chiquita gris debajo del estado de la reserva, aparece SOLO si hay alguno cancelado. No cambia el estado ni el color de la reserva (es dato calculado). El total al pie NO cuenta el servicio cancelado.
  - **Servicio cancelado TACHADO** en la lista, con motivo + quién + cuándo (refuerza decisión #9 del ciclo de vida; antes solo había badge, ahora además el nombre tachado).
  - **Bloqueo por factura/voucher vivo**: si el servicio tiene factura con CAE o voucher emitido, NO se puede cancelar. Ventanita clara con el motivo + número de factura + botón "Ir a la factura" para anularla. Mismo formato para el caso del voucher.
  - **Cancelar todo y cancelar suelto CONVIVEN**: sigue el botón "Cancelar" de la franja (cancela toda la reserva) Y se puede cancelar servicios sueltos desde la lista.
  - **Cancelar varios = EN LÍNEA, debajo de la lista** (no ventana, coherente con la carga de servicios y el cobro). Tildás los servicios (de cualquier operador), ves el total a devolver al cliente POR MONEDA (nunca mezclado), un solo motivo para la tanda, confirmás una vez. Los servicios bloqueados (factura/voucher vivo) aparecen VISIBLES con el casillero apagado y la explicación al lado.
  - **Bandeja "Notas de crédito por revisar" = dentro de Cobranza y Facturación.** PENDIENTE DE BACKEND: hoy no hay un listado estructurado de cancelaciones esperando NC (la parcial solo deja rastro en el log de auditoría; `debit-notes/pending` es para notas de DÉBITO). Antes de construir la bandeja hay que agregar la lista real en backend. Las otras 3 piezas se construyen ya.

## Tanda de frontend — elecciones de Gastón (2026-06-13, mockup `2026-06-13-tanda-front-opciones.html`)

- **(2026-06-13) Comisiones de vendedor.** Interruptor en Configuración con **un % general único para todos** (no por vendedor). Pantalla NUEVA propia "Comisiones": navegador de mes (flechas, como Caja) + lista de vendedores con su total del mes → al tocar, detalle reserva por reserva. **La ve SOLO el dueño/admin** (los vendedores NO ven comisiones).
- **(2026-06-13) Deuda al operador por reserva.** Dentro de la cuenta del operador: total arriba + lista de reservas con lo que se le debe de cada una; **anticipos a cuenta como fila aparte que RESTA**. Sin permiso ver-costos → tapado. Nunca mezclar monedas.
- **(2026-06-13) Factura desde servicios.** EN LÍNEA debajo (no ventana), renglones precargados desde los servicios confirmados, **franja amarilla arriba** si el total no coincide con lo vendido (no bloquea, se factura igual).
- **(2026-06-13) "Confirmada con cambios".** Etiqueta "Con cambios" al lado del estado + franja amarilla que **muestra qué cambió** + botón "Dar OK". El OK lo da **solo un administrador**.
- **(2026-06-13) Saldo a favor del cliente.** El cartel de saldo cambia a "A FAVOR EN $" en verde + **botón "Usar saldo a favor"** que abre el flujo de uso (aplicar a otra reserva / devolver). Un renglón por moneda.
- **(2026-06-13) Crear presupuesto desde lead.** Botón en la ficha del lead (ya existe) + **agregar botón rápido en la LISTA de leads**. El lead se marca **Ganado cuando el cliente ACEPTA el presupuesto** (la reserva pasa a firme), NO al crear el presupuesto.

## Botones por estado, cobro en moneda real, cancelar en línea y reabrir para facturar (ADR-035, 2026-06-19)

> Sesión de 8 preguntas con dibujos sobre el detalle de la reserva (solapa Estado de Cuenta y
> acciones de cabecera). Gastón eligió todas las opciones A. El backend ya expone, por reserva:
> `Capabilities` (un `{ allowed, reason }` por cada acción), `RequiresInvoiceAnnulmentToCancel`
> (sí/no hay factura emitida que anular), `MonedaPrincipal` (la moneda donde está el grueso del
> saldo) y `porMoneda` (un saldo por cada moneda que la reserva realmente usa).

**A) Botones apagados: SOLO gris, sin texto de motivo debajo (actualizado 2026-06-19 feedback).**
- **(Feedback 2026-06-19 — reemplaza la regla anterior)** Cuando una acción no se puede hacer, el botón va **GRIS** (apagado, no se puede tocar). **NO hay texto de motivo debajo de cada botón.** En cambio, hay **UN ÚNICO CARTEL** arriba que explica el estado de la reserva para los estados terminales.
- Carteles por estado terminal (van en la franja de arriba de la pantalla de reserva):
  - **Perdida:** "Reserva perdida — solo lectura."
  - **Cancelada:** "Reserva cancelada — solo lectura."
  - **Finalizada:** ~~"Reserva finalizada — solo lectura. Reabrila para facturar."~~ **⚠️ SUPERSEDIDO por ADR-037 (2026-06-21):** el cartel ahora dice solo **"Reserva finalizada — solo lectura."** y se factura DIRECTO desde Finalizada (ya no se reabre ni se destraba). Ver sección ADR-037 al final.
  - **Esperando reembolso:** "Anulada, esperando el reembolso del operador — solo lectura." (corregido 2026-07-04: antes decía "Cancelada"; se alinea a ADR-036 punto 1 — el usuario ve "Anulada", nunca "Cancelada", para deshacer el viaje).
- Los estados activos (Budget, InManagement, etc.) conservan sus carteles orientativos de siempre.
- El texto "Tiene que haber al menos 1 pasajero" debajo de "El cliente aceptó" **sí se mantiene** — es un requisito previo de acción, no un motivo de bloqueo de estado.

**A-bis) Botón primario de avance integrado en la fila (feedback 2026-06-19).**
- El botón primario de avance (ej. "El cliente aceptó", "Pasar a presupuesto") **va en la misma fila** que el resto de los botones de acción. NO flota suelto arriba como un bloque independiente.
- Todos los botones tienen la misma altura y alineación.

**A-ter) Solo lectura de pasajeros y fechas en estados terminales (feedback 2026-06-19).**
- En estados terminales (Lost/Cancelled/Closed/AwaitingRefund): el botón "Editar fechas" desaparece, y los botones de agregar/editar/borrar pasajero también desaparecen. La lista de pasajeros es informativa.
- Se controla con `capabilities.canEditPassengers.allowed` y `capabilities.canEditReservaData.allowed` del backend.

**A-cuater) Servicios en estado coherente con la reserva (feedback 2026-06-19).**
- Si la reserva es **Perdida (Lost)**, todos los servicios muestran "Anulado" en su badge de estado.
- Si la reserva es **Cancelada (Cancelled)**, todos los servicios muestran "Cancelado".
- Esto es SOLO presentación (display-derived): no se mutan los datos del backend.

**A-quinque) Diferenciar estado operativo de estado de pago (feedback 2026-06-19).**
- El badge de estado operativo (Presupuesto, En gestión, Confirmada…) es EL ESTADO de la reserva.
- Los chips de pago (Pagada, Saldo pendiente, Vencida con deuda) son secundarios: más chicos y llevan el prefijo "Pago:" en gris para que no parezcan un segundo estado operativo.

**B) Cobro: arranca en una sola moneda, con link "pagar en otra moneda" (P2/P3/P4 = A).**
- **(2026-06-19) La ficha de cobro arranca mostrando UNA sola línea con la moneda principal:** "Cobrás en US$ — saldo US$ X" (la moneda y el saldo salen de `MonedaPrincipal` y de `porMoneda`). Debajo, un **link chico "pagar en otra moneda"**. Mientras el vendedor no lo toca, cobra en la moneda principal sin ver ningún selector (caso normal, lo más común).
- **(2026-06-19) Al tocar "pagar en otra moneda", AHÍ MISMO (sin ventana) aparecen** los selectores de "Moneda del cobro" e "Imputar a"; y si el cobro cruza de moneda, además el recuadro de tipo de cambio (lo de siempre: TC + fuente + fecha, los tres obligatorios). Todo en línea, debajo, dentro de la misma ficha. Nada de ventana flotante.
- **(2026-06-19) El saldo muestra SOLO las monedas que la reserva realmente usa.** Si la reserva solo tiene pesos, no aparece "US$ 0" fantasma; si solo tiene dólares, no aparece "$ 0". Solo se listan las monedas con saldo real (`porMoneda`).

**C) Cancelar toda la reserva: pasa a EN LÍNEA, con cartel según haya o no factura (P5/P6/P7 = A).**
- **(2026-06-19) Cancelar TODA la reserva deja de ser ventana flotante y se carga EN LÍNEA**, debajo, igual que el cobro y la carga de servicios (coherente con "el modal me parece horrible"). El modal de cancelación muere como ventana.
- **(2026-06-19) Si la reserva NO tiene factura emitida** (`RequiresInvoiceAnnulmentToCancel = false`): cartel **VERDE** "Esta reserva no tiene factura emitida, se cancela directo, sin nota de crédito." + **motivo obligatorio**.
- **(2026-06-19) Si la reserva SÍ tiene factura emitida** (`RequiresInvoiceAnnulmentToCancel = true`): cartel **ÁMBAR** "Esta reserva tiene factura emitida, al cancelar se emite la nota de crédito en AFIP/ARCA para anularla." + **motivo obligatorio**.
- El motivo obligatorio sigue las reglas que ya tenía la cancelación (mínimo de caracteres, etc.); lo nuevo es el cartel de color según haya factura y que todo va en línea.

**D) Reabrir una reserva Finalizada para facturar (P8 = A).**
- **(2026-06-19) Botón "Reabrir para facturar"** (nombre confirmado por Gastón) entre las acciones de cabecera de la reserva, **en la fila de acciones (junto a "Volver atrás" y "Archivar")**.
- **(Feedback 2026-06-19) SOLO aparece cuando la reserva está Finalizada Y NO tiene factura con CAE vivo** (`requiresInvoiceAnnulmentToCancel = false`). Si ya tiene factura emitida, no tiene sentido reabrir para facturar.
- **(2026-06-19) Al tocarlo pide MOTIVO OBLIGATORIO** antes de reabrir. Es una acción sensible (devuelve una reserva ya cerrada al circuito de facturación), por eso siempre queda registrado el motivo.
- **⚠️ ACTUALIZADO por ADR-036 (2026-06-21, P1=B):** la mecánica cambió. "Reabrir para facturar" YA NO manda la reserva al estado "A liquidar" (ese estado se eliminó). Ahora la reserva **se queda en Finalizada pero se DESTRABA para facturar**, igual que cuando se destraba una Confirmada bajo candado: se factura sin cambiar de estado. Ver la sección ADR-036 más abajo para el detalle.

## Prepago puro: anular vs cancelar, estados de solo lectura, aviso de operador y "no viaja si debe" (ADR-036, 2026-06-21)

> Sesión de 6 preguntas con dibujos sobre el ciclo de la reserva, después del trabajo de backend
> de ADR-036 ("Prepago puro"). Gastón pasó 7 decisiones base (vocabulario Cancelar/Anular, "En viaje"
> de solo lectura, pantalla muerta de Anulada/Perdida, solo se anula —no se da de baja— una reserva con
> plata viva, no viaja si el cliente debe, el operador impago es aviso que no traba) y respondió las 6
> preguntas abiertas. El backend ya expone, por reserva: `Capabilities` (un `{ allowed, reason }` por
> acción, ADR-035), el motivo por el que no puede pasar a "En viaje" (sin montos de costo), el estado de
> pago al operador por servicio, y `requiresInvoiceAnnulmentToCancel`.

**0) "A liquidar" deja de existir como estado — sacarlo de TODA la UI (decisión base 1).**
- **(2026-06-21)** El estado "A liquidar" (ToSettle) se elimina por completo de lo que ve el usuario: se saca la **pestaña "A liquidar"** del listado de reservas, su **chip/badge violeta**, el **contador** de la pestaña, los **filtros** que lo nombran y cualquier **cartel** que lo mencione. No queda ningún rastro de "A liquidar" / "Apartar para liquidar" / "Marcar liquidada" en pantalla.
- Donde antes un cartel o botón mandaba "a liquidar", ahora la acción es la de ADR-036 que corresponda (ver punto 6 para reabrir-Finalizada).

**1) Vocabulario CANCELAR vs ANULAR — regla dura de palabras (decisión base 2).**
- **(2026-06-21)** En este producto las dos palabras significan cosas distintas y NO son intercambiables:
  - **"Cancelar" = el cliente paga el total / saldar.** (Es el sentido de "cancelar una deuda".)
  - **"Anular" = dejar sin efecto el viaje / deshacer la reserva.**
- **(2026-06-21)** Donde la UI hoy dice "Cancelar / Cancelada" con el sentido de DESHACER el viaje, debe pasar a decir **"Anular / Anulada"**. El "Cancelar" que solo cierra una ficha o un panel (el botón de descartar) NO se toca: ahí "Cancelar" sigue significando "salir sin guardar".
- **(2026-06-21)** Es un cambio LUGAR POR LUGAR con criterio, NO un reemplazo ciego de todas las palabras "cancelar".

**2) "En viaje" = solo lectura, con cartel chico arriba (decisión base 3 + P2-bis).**
- **(2026-06-21)** Cuando la reserva está "En viaje", la pantalla es de solo lectura: no se edita, no se cobra, no se factura, no se anula (el backend ya apaga estas acciones).
- **(2026-06-21, P2-bis)** Arriba va un **cartel chico** de solo lectura: **"✈️ Reserva en viaje — solo lectura."** (mismo lugar y estilo de los carteles terminales de ADR-035; un solo cartel arriba, nunca mensajitos por botón).

**3) Pantalla muerta de Anulada y Perdida: CON cartel chico de solo lectura, SIN botones ni mensajitos (decisión base 4 + P2).**
- **(2026-06-21, P2)** En "Anulada" y "Perdida" la pantalla es solo información: **SÍ va el cartel chico de "solo lectura" arriba** (el cartel de estado de ADR-035 se mantiene), pero **se sacan los botones de acción y los mensajitos por botón**. Lo que se elimina son los botones y los textos de motivo pegados a cada botón, NO el cartel de estado de arriba.
- Carteles (los mismos de ADR-035, se conservan): **"Reserva perdida — solo lectura."** y **"Reserva cancelada — solo lectura."** (este último corresponde al estado interno Cancelled; ver punto 1: la palabra que ve el usuario para deshacer es "Anular/Anulada").

**4) Una reserva con plata viva solo se ANULA — el botón "Eliminar" no aparece (decisión base 5 + P3=A).**
- **(2026-06-21, P3=A)** En una reserva con plata viva (con pagos / proceso fiscal), **el botón "Eliminar" simplemente NO aparece**; solo se ofrece **"Anular"**. No se pone ninguna línea extra de explicación en el cartel de arriba ("en ningún lado fijo").
- **(2026-06-21, P3=A)** La explicación de por qué no se puede dar de baja simple aparece **recién al abrir el panel de "Anular"** (dentro del panel, no antes). Mientras la reserva está en pantalla, el vendedor solo ve "Anular" disponible y "Eliminar" ausente, sin texto aclaratorio suelto.

**5) Aviso de operador impago: etiqueta chica al lado de CADA servicio (decisión base 7 + P4=B).**
- **(2026-06-21, P4=B)** El aviso de pago al operador es una **etiqueta chica al lado de CADA servicio**, NO una franja general arriba. Dos estados:
  - Operador sin pagar: **"⚠️ Operador impago"** (ámbar).
  - Operador pagado: **"✔ Operador pagado"** (verde).
- **(2026-06-21, P4=B)** La etiqueta NO traba nada (el operador impago es solo un aviso, no impide viajar). Es estado, no acción.
- **(2026-06-21, P4=B — regla de costos)** La etiqueta **NUNCA muestra montos**. Es solo el estado pagado/impago, sin cifras. Así puede mostrarse también a quien no tiene permiso de ver costos (`cobranzas.see_cost`) sin filtrar plata, consistente con la regla general 2026-06-05 ("sin permiso de ver costos, no se muestran montos en ninguna pantalla"). La etiqueta de estado SÍ se ve para todos; los montos de deuda al operador siguen tapados donde corresponda.
- **(2026-06-21)** Esta etiqueta se conecta con una feature que viene después (un casillero "pagado al operador" por servicio); por ahora se diseña SOLO la etiqueta de estado, leyendo lo que el backend ya expone por servicio.

**6) Reabrir una Finalizada para facturar = se DESTRABA, NO cambia de estado (P1=B). ⚠️ SUPERSEDIDO por ADR-037 (2026-06-21): ya NO se reabre ni se destraba — se factura DIRECTO desde Finalizada. Ver la sección ADR-037 al final.**
- **(2026-06-21, P1=B)** El botón "Reabrir para facturar" (de ADR-035) ahora **destraba la reserva Finalizada para poder facturar, SIN cambiarla de estado**: se queda "Finalizada" pero abierta para emitir la factura, igual que cuando un administrador destraba una Confirmada bajo candado. **NO pasa a "A liquidar"** (ese estado ya no existe, punto 0).
- **(2026-06-21, P1=B)** El cartel de Finalizada queda: **"Reserva finalizada — solo lectura. Reabrila para facturar."** (sin la palabra "A liquidar"). Sigue valiendo: solo aparece la opción si NO tiene factura con CAE vivo, y pide motivo obligatorio al destrabar.
- **⚠️ CONTRADICCIÓN RESUELTA CON LA DECISIÓN MÁS NUEVA:** la guía de ADR-035 (2026-06-19) mandaba "Reabrir para facturar" → estado "A liquidar". La decisión de Gastón de hoy (P1=B + decisión base 1) **reemplaza** eso: ya no hay estado "A liquidar" y la reserva se destraba sin moverse de Finalizada. Vale ADR-036. (Nota de Gastón: en paralelo un experto ERP está estudiando desacoplar facturación/cobranza del ciclo de estados; por ahora se construye esta opción de "destrabar".)

**7) "No viaja porque el cliente debe": cartel arriba + chip rojo de pago, enganchado a la config de aviso de cobro que YA EXISTE (decisión base 6 + P5=B).**
- **(2026-06-21, P5=B)** Para pasar a "En viaje" el cliente tiene que estar 100% pagado. Si debe, **la reserva se queda en "Confirmada"** (no pasa a "En viaje") hasta que se cobre; cuando se cobra el total, recién ahí pasa a "En viaje".
- **(2026-06-21, P5=B)** Mientras debe, se muestra: un **cartel chico arriba** explicando que no puede viajar porque hay saldo pendiente, **+ un chip de pago rojo "Debe — no viaja"** (chip de pago secundario, con el prefijo "Pago:" en gris como manda ADR-035, distinto del badge de estado operativo). El cartel y el chip explican el porqué **sin mostrar montos de costo** (puede mostrar lo que el cliente debe, que no es costo; nunca costo ni deuda al operador).
- **(2026-06-21, P5=B — IMPORTANTE para el implementador)** Este aviso **se engancha con la configuración existente** de "cuántos días antes avisar sobre algo no cobrado al 100%". **NO se inventa un parámetro nuevo.** La config existente es **"Alertas por reservas próximas con deuda"** con su casillero **"Días previos para alertar"**, en Configuración → Operativa/Cobranzas/Facturación (`OperationalFinanceSettingsTab.jsx`; campos `enableUpcomingUnpaidReservationNotifications` y `upcomingUnpaidReservationAlertDays`, endpoint `/settings/operational-finance`). El cartel/chip de "Debe — no viaja" se apoya en ese mismo umbral de días, no en uno nuevo.

**8) Servicios de una reserva deshecha = "Anulado" (decisión base 6-servicios + P6=A).**
- **(2026-06-21, P6=A)** Cuando la reserva quedó deshecha, **todos sus servicios muestran "Anulado"** en su badge de estado, tanto si la reserva está **Perdida** como si está **Anulada (Cancelled)**. Un solo verbo, coherente con que la reserva se "Anula".
- **(2026-06-21)** Esto **reemplaza** la regla A-cuater de ADR-035 (2026-06-19), que mostraba "Anulado" para Perdida y "Cancelado" para Cancelled. Ahora ambas muestran **"Anulado"**. Sigue siendo SOLO presentación (no muta los datos del backend).

## Facturación desacoplada del estado: chip de facturación, se factura directo, "Debe — no viaja" con ventana (ADR-037, 2026-06-21)

> Después del trabajo de backend de ADR-037 ("Desacople de facturación", estilo SAP/Odoo),
> la facturación dejó de depender del estado de la reserva. El backend ya expone, por reserva:
> `invoicingStatus` (NotInvoiced / PartiallyInvoiced / FullyInvoiced), la capacidad de "Facturar"
> habilitada en Confirmada, En viaje y Finalizada, y `isWithinUnpaidAlertWindow` (bool) para el
> aviso "Debe — no viaja". Esta sección SUPERSEDE el P1=B de ADR-036 (ver punto 2).

**1) Chip de FACTURACIÓN al lado del chip de COBRO (mismo tratamiento de "plata", no es estado operativo).**
- **(2026-06-21)** Se agrega un **chip informativo de facturación** que sale de `invoicingStatus`. Va **al lado del chip de cobro** ("Pago:"), con el **mismo tratamiento secundario**: chico, con prefijo gris (igual que "Pago:"), para que NO parezca un segundo estado operativo de la reserva (regla ADR-035 A-quinque). El estado operativo sigue siendo SOLO el badge grande.
- **(2026-06-21)** Implementado en la **ficha de la reserva** (en la cabecera, pegado a los chips de pago, dentro de `ReservaStatusChips`). _Pendiente (follow-up): mostrarlo también en el **listado** de reservas — el backend ya expone `invoicingStatus` en el DTO del listado, pero la lista no usa hoy esos chips de plata; agregarlo ahí es una entrega aparte._
- **(2026-06-21 — RESPUESTA DE GASTON):** el chip se muestra **SIEMPRE, en todos los estados** (incluso Cotización / Presupuesto, donde dirá "Sin facturar"). Gaston eligió mostrarlo siempre (respondió "2B"), no esconderlo en etapas tempranas. **Wording final (respuesta "1A"):** `Sin facturar` / `Facturada en parte` / `Facturada total`. _(Esto cierra la PREGUNTA que estaba BLOQUEADA: el chip es siempre visible y con esos textos.)_

**2) Se factura DIRECTO desde Finalizada — muere "Reabrir para facturar" (SUPERSEDE P1=B de ADR-036).**
- **(2026-06-21)** Como la facturación se desacopló del estado, **ya no hace falta reabrir ni destrabar nada para facturar**. El backend habilita "Facturar" en **Confirmada, En viaje y Finalizada**.
- **(2026-06-21)** Se **elimina el botón "Reabrir para facturar"** de la cabecera de la reserva (el de ADR-035/036). Ya no se reabre ni se destraba.
- **(2026-06-21)** En una reserva **Finalizada**, el botón **"Facturar" aparece habilitado** (la capacidad la da el backend). El usuario factura y listo, sin pasar por ningún estado intermedio.
- **(2026-06-21)** El **cartel de Finalizada** deja de invitar a reabrir. Pasa a decir: **"Reserva finalizada — solo lectura."** (se le saca "Reabrila para facturar"), porque facturar ya no es "reabrir": es una acción normal disponible. _(El cartel de "solo lectura" se mantiene para el resto de las acciones; lo único que cambia es que ya no menciona reabrir.)_
- **⚠️ SUPERSEDE el P1=B de ADR-036 (2026-06-21):** aquella decisión decía "Reabrir para facturar destraba la Finalizada sin cambiarla de estado". Con ADR-037 **ya no hay reabrir ni destrabar para facturar**: se factura directo desde Finalizada. El experto ERP que ADR-036 mencionaba "estudiando desacoplar facturación del ciclo de estados" entregó justamente esto. Vale ADR-037.

**3) "Debe — no viaja" SOLO dentro de la ventana de días (ajuste del front de ADR-036).**
- **(2026-06-21)** El **chip rojo "Debe — no viaja"** y el **cartel de arriba** del mismo tema (ADR-036 punto 7) ahora se muestran **SOLO cuando `isWithinUnpaidAlertWindow` es true** — es decir, cuando la salida está dentro de la ventana de días configurada en "Alertas por reservas próximas con deuda" Y hay deuda del cliente. **Ya NO se muestra para toda reserva Confirmada con deuda.**
- **⚠️ CAMBIO 2026-07-05 (respuesta 2B, sección "Ventanas emergentes y avisos"):** el **cartel de arriba** se ELIMINA por quedar duplicado con el chip (misma condición). A partir de acá, **"Debe — no viaja" se ve SOLO como chip** en el header.
- **(2026-06-21)** Esto completa lo que ADR-036 dejó pendiente: aquella regla se apoyaba en la config existente de días de aviso, pero el dato no llegaba al front. Ahora `isWithinUnpaidAlertWindow` lo trae resuelto del backend (que ya cruza la config con la fecha de salida). El front solo lo lee.
- **(2026-06-21)** Sigue valiendo todo lo demás de ADR-036 punto 7: el chip lleva el prefijo "Pago:" en gris (no es estado operativo), no muestra montos de costo ni deuda al operador (puede mostrar lo que el cliente debe), y la reserva sigue sin pasar a "En viaje" hasta cobrarse el total. Lo único que cambia es **CUÁNDO se muestra el aviso**: dentro de la ventana, no siempre.

## Estados congelados: ver/reimprimir comprobantes, sacar "pedir autorización" en viaje, no viajar vacío (2026-06-22)

> **Origen:** revisión de Gastón viendo el sistema desplegado tras ADR-036/037, juzgada contra
> estándares ERP (SAP/Oracle/Dynamics/Odoo/NetSuite) y criterio de agencia. Tres decisiones
> cerradas por Gastón el 2026-06-22 (respondió por opciones). NO reabrir.

**1) En estados congelados, los comprobantes se pueden VER y REIMPRIMIR (pero NO emitir nuevos).**
- **(2026-06-22 — RESPUESTA DE GASTON: "Solo ver y reimprimir")** En "En viaje", "Perdida", "Anulada", **"Anulada esperando el reembolso del operador"** (PendingOperatorRefund — agregado 2026-06-22 por respuesta de Gastón: es solo lectura, va igual que las demás congeladas) y cuando la venta está **totalmente facturada**, la pantalla de solo-lectura **debe seguir permitiendo las operaciones de DOCUMENTO** sobre lo ya emitido: **ver y reimprimir/descargar** vouchers, ver/descargar el **PDF de la factura**, y ver/descargar el **PDF de un recibo ya emitido**. Estas acciones NO cambian la venta ni mueven plata.
- **(2026-06-22)** Lo que SÍ queda apagado en esos estados congelados: **emitir un recibo nuevo** ("Emitir comprobante"), **anular un comprobante** ("Anular comprobante"), registrar cobros, editar, facturar nuevo (lo de facturar sigue su propia regla de ADR-037). O sea: ver/reimprimir SÍ; emitir/anular/cobrar NO.
- **(2026-06-22)** Motivo (verificado en código y con experto ERP): el motor por dentro YA permitía estas reimpresiones; el "bloqueo total" que se veía era de la **pantalla**, que escondía todo por mostrar la reserva como solo-lectura. Es el patrón ERP correcto (reimprimir documentos posteados es siempre posible); solo faltaba exponerlo en la UI.
- **(2026-06-22)** Aplica igual a los tres "lugares de comprobante": solapa **Estado de Cuenta** (recibos de pago + PDF factura) y solapa **Vouchers** (reimpresión de voucher). En todos: botones de **Ver/Descargar/Reimprimir** visibles; botones de **Emitir/Anular** ocultos en estado congelado.

**2) El botón "pedir autorización para editar" NO aparece en "En viaje" (ni en estados inmutables por diseño).**
- **(2026-06-22)** En "En viaje" la reserva es inmutable **aun con autorización** (no es un candado destrababl). Por eso el affordance "Pedí autorización" **se saca** ahí: prometía algo que no existe.
- **(2026-06-22)** Regla general: el botón "Pedí autorización" / la franja ámbar de candado **solo se muestra en "Confirmada"** (estado bloqueado-pero-destrabable-con-permiso). En "En viaje" y "Finalizada" (inmutables por diseño) NO se ofrece destrabar; va el cartel chico de solo-lectura y nada más. Confirmado por experto ERP: ocultar affordances de "override" cuando el estado es inmutable por diseño es buena práctica.

**3) Una reserva NO puede estar "En viaje" sin servicios (no se viaja vacío).**
- **(2026-06-22 — RESPUESTA DE GASTON: "Cerrarlas + impedir nuevas")** Invariante de negocio: una reserva sin ningún servicio cargado **no puede pasar a "En viaje"** (el sistema lo impide de ahora en más). Una reserva vacía no tiene viaje que cumplir.
- **(2026-06-22)** Las reservas que ya quedaron atascadas en "En viaje" vacías (datos viejos) se **cierran** (saneamiento una sola vez, con registro de auditoría). Esto es backend; no cambia ninguna pantalla, solo limpia datos inconsistentes.
- **(2026-06-22)** Viajes de **solo ida** NO son "vacíos": tienen al menos un servicio (p. ej. el aéreo de ida). El fin del viaje se deriva de la fecha del último servicio cargado (ya está implementado), así que un solo-ida cierra solo sin necesitar fecha de regreso. No se toca.

## Limpieza de "En viaje" (solo lectura de verdad) + chip de 3 ejes + Estado de Cuenta como resumen con saldo (2026-06-22, 2da tanda)

> **Origen:** Gastón mandó una captura de una reserva "En viaje" (paga + facturada total) donde la pantalla decía "solo lectura" pero seguía mostrando botones de escritura, un chip mal rotulado, y un "Volver atrás". Además planteó que el "Estado de Cuenta" no está como un ERP serio. Todo juzgado con experto ERP. Decisiones de Gastón 2026-06-22. NO reabrir.

**1) "En viaje" (y estados de solo lectura) = botonera realmente limpia.**
- **(2026-06-22)** En la solapa **Servicios**, cuando la reserva está en solo lectura (En viaje / Finalizada / Perdida / Anulada / Esperando reembolso), se OCULTAN los botones de escritura: **"Agregar Servicio", "Cancelar varios"**, y por cada servicio **"Editar" y "Cancelar"**. Quedan visibles los datos y lo de solo-lectura (ver/estado). Se gobierna por las capabilities del backend (canEditServices / canCancel), no por re-derivar en el front.
- **(2026-06-22)** En la **cabecera**, en "En viaje": se SACAN **"Volver atrás"** (revertir estado), **"Archivar"** y **"Anular"** (gris). Confirmado por experto ERP: un documento in-transit no se "des-confirma" con un botón libre, no se archiva en curso, y en viaje no se anula. Si alguna vez hay que revertir por error, va por un camino controlado (permiso + motivo + auditoría), NO un botón normal. (Backend: verificar/ tapar que el candado de factura viva apague el revert.)
- **(2026-06-22)** Queda en "En viaje" solo lo que se puede hacer de verdad: ver/reimprimir documentos, **"Cerrar reserva"** cuando el viaje terminó, y facturar si falta (ADR-037). Más el cartel chico de solo-lectura.

**2) El chip de plata separa TRES ejes — nunca los mezcla.**
- **(2026-06-22)** Un rótulo = un solo eje. Se separan: **Pago:** (Pendiente / Parcial / **Pagada**) · **Factura:** (Sin facturar / en parte / **Facturada total**). Lo que hoy es "Pago: En curso" está MAL (mezcla pago con viaje): "En curso/En viaje" es eje VIAJE, no PAGO. Una reserva paga que viaja muestra **Pago: Pagada**.
- **(2026-06-22 — refinamiento por review)** El eje **Viaje** NO repite el cartel/badge grande de estado: si el badge ya dice "EN VIAJE", NO se pone un chip "Viaje: En viaje" (redundante). El eje Viaje **solo** se muestra para el caso de ALERTA que el badge no comunica: **"Vencida con deuda"** (el viaje terminó pero quedó saldo). Una reserva en viaje paga y facturada muestra solo **Pago: Pagada · Factura: Facturada total** (sin chip de viaje, porque el badge ya dice EN VIAJE).

**3) "Estado de Cuenta" = resumen de cuenta con SALDO CORRIENTE (no dos listas sueltas).**
- **(2026-06-22 — RESPUESTA DE GASTON)** La solapa se convierte en un **resumen tipo extracto bancario**: una sola **línea de tiempo cronológica** donde cada **factura / nota de débito SUMA** (cargo) y cada **cobro / nota de crédito RESTA** (abono), mostrando el **saldo después de cada movimiento** y el saldo final. Respeta multimoneda (saldo corriente por moneda, nunca sumando monedas distintas).
- **(2026-06-22)** Se agrega en la cabecera el **eje de facturación** (cuánto facturado vs cuánto falta facturar — el dato ya existe en el backend) separado del eje de cobranza.
- **(2026-06-22)** Se muestra el **saldo a favor del cliente** desde la reserva y un **enlace a la cuenta corriente del cliente** (la pantalla a nivel cliente ya existe).
- **(2026-06-22 — RESPUESTA DE GASTON: vencimientos = NO)** NO se agregan cuotas/vencimientos con fecha. Sigue valiendo la regla del candado: si no pagó todo, no viaja. (Se puede agregar más adelante si hace falta.)
- **(2026-06-22 — RESPUESTA DE GASTON: margen = SÍ)** Para quien tiene permiso de costos, además del **costo/inversión** se muestra el **margen (ganancia = venta − costo)**. A quien NO ve costos, no se le muestra ni costo ni margen (regla de costos intacta).
- **(2026-06-22)** Se mantiene lo que ya estaba bien: saldo separado por moneda, NC/ND ligadas a su factura origen, ocultar costo sin permiso, saldo a favor reutilizable del cliente.

## Tanda 2 — "Sacar de viaje" (corrección de entrada errónea a En viaje) (2026-06-22)

> **Origen:** auditoría integral de reservas (experto ERP). "En viaje" quedó como callejón sin salida: si una reserva entró por error (fecha mal cargada / el viaje no salió), no se podía corregir. Diseño del software-architect, decisiones tomadas con su recomendación. NO reabrir.

- **(2026-06-22)** Se agrega una acción de EXCEPCIÓN "**Sacar de viaje**" que devuelve una reserva de "En viaje" a "Confirmada" cuando entró por error. NO es un botón normal: es una corrección con permiso elevado, motivo obligatorio y auditoría.
- **(2026-06-22)** **Solo Admin** (permiso nuevo `reservas.correct_traveling`, igual perfil que los otros "levantar candado fuerte"). Va discreto (no junto a los botones normales), visible solo para Admin y solo si la reserva está En viaje y NO facturada.
- **(2026-06-22)** **Bloqueado si la reserva tiene factura con CAE vivo**: ahí no se saca de viaje, se corrige por Nota de Crédito/ajuste (mismo candado fiscal que ya existe).
- **(2026-06-22)** El modal pide: **motivo obligatorio** (siempre, hasta para Admin) + (si hiciera falta autorizante, mismo patrón que el "volver atrás"). Un cartel explica la consecuencia y recuerda: **"si la fecha estaba mal cargada, después de sacarla de viaje hay que corregir la fecha del servicio; si no, el sistema podría volver a ponerla en viaje"** (la fecha de salida sale de los servicios, no se escribe a mano).
- **(2026-06-22)** Para que el sistema NO la vuelva a meter en viaje esa misma noche, la reserva queda con una **marca "En corrección"** (chip/cartel visible) que la congela para el proceso automático hasta que se corrija la fecha del servicio o avance de estado. Mientras tanto se ve claramente que está "En corrección — pendiente revisar fechas".
  - **⚠️ CAMBIO 2026-07-05 (respuesta 2B, sección "Ventanas emergentes y avisos"):** el cartel se ELIMINA por quedar duplicado con el chip (misma condición exacta). A partir de acá, **"En corrección" se ve SOLO como chip** en el header; el texto completo pasa al title/tooltip del chip.
- **(2026-06-22)** Queda registrado en el historial como **corrección** (distinto de un "volver atrás" normal): quién, cuándo y por qué.

## Tanda 3 — Pagado al operador por servicio: registrar desde la ficha del proveedor (2026-06-23, respuesta de Gastón)

- **(2026-06-23 — RESPUESTA DE GASTON: "Desde la ficha del proveedor")** El pago al operador imputado a un SERVICIO se registra **desde la ficha/página del proveedor** (igual que los pagos a proveedor que ya existen), agregando un **selector opcional para imputar el pago a un servicio de una reserva** (elegir reserva → servicio de ESE proveedor). El backend ya lo soporta (ServiceRecordKind + ServicePublicId).
- **(2026-06-23)** La **solapa Servicios de la reserva MUESTRA** el estado **pagado / parcial / impago** al operador por cada servicio (etiqueta, consume el endpoint nuevo). El **estado se ve para todos**; los **montos** (costo / pagado / saldo) solo con permiso de costos (`cobranzas.see_cost`), igual que ADR-036 P4=B. No se registra el pago desde la solapa Servicios (eso es en la ficha del proveedor); la solapa solo muestra el estado.

## Tanda 3 — Reprogramar viaje + Enviar voucher + Pasajero reutilizable (2026-06-23, respuestas de Gastón)

**1) Reprogramar viaje = por NUEVA FECHA DE SALIDA.** El usuario pone la **nueva fecha de salida** y el sistema corre TODO el viaje a esa fecha (mantiene la duración: mueve todas las fechas de todos los servicios el mismo delta). El backend ya soporta `newStartDate`. Botón "Reprogramar viaje" visible cuando se pueden editar servicios (capability `canEditServices`); si está facturada/voucherizada, el backend lo frena (mensaje claro). Es distinto de "Editar fechas" (que es override manual de la cabecera).

**2) Enviar voucher al pasajero = BOTÓN en la solapa Vouchers + FRENO si el operador no confirmó.** (a) Agregar "Enviar al pasajero" en cada voucher de la solapa Vouchers de la reserva, reusando el envío por WhatsApp que YA existe (`MessageService.SendVoucherMessageAsync`). (b) ENDURECER el gate: no se emite/envía un voucher si el operador NO confirmó el servicio (excepción posible con supervisor, como ya existe para el saldo impago). El envío usa el teléfono del pasajero/cliente; si no hay teléfono, avisar claro.

**3) Pasajero reutilizable = BUSCAR por documento/nombre al cargar.** Al agregar un pasajero (en `PassengerFormModal` / mini-form), si el usuario escribe el documento o el nombre, **sugerir pasajeros que ya viajaron antes** (búsqueda en la base, no solo padrón AFIP) y **autocompletar** al elegir uno. Dedup suave (avisar si el mismo documento ya está en la reserva). No hace falta vincular Passenger↔Customer en esta etapa (solo búsqueda + autocompletar).

## Emisión de la factura de venta: confirmar antes, claridad mientras AFIP procesa, resultado claro (H2, 2026-06-24, respuestas de Gastón)

> **Origen:** Gastón reportó "al emitir la factura no se ve de forma clara". La emisión es ASÍNCRONA:
> la factura se envía a AFIP/ARCA y el número oficial (CAE) llega DESPUÉS (hay una espera). Hoy el éxito
> era un cartel con jerga ("Comprobante AFIP encolado correctamente") que cerraba todo, sin paso de
> confirmación previo, sin estado claro de "en camino" y sin un resultado final visible. Sesión de 5
> preguntas con dibujos; Gastón eligió las opciones de abajo. Componente: `EmitirFacturaInline.jsx`
> (ficha EN LÍNEA, ya aprobada 2026-06-13; esto agrega el flujo alrededor de la emisión).

- **(2026-06-24, P1=A) Cartel de confirmación ANTES de emitir.** Al apretar "Emitir factura" (el botón final de la ficha), antes de mandarla a AFIP aparece un cartel que resume **a quién se factura** y **por cuánto total**, y pide confirmar. **Texto fiscal correcto (verificado contra ARCA — NO usar "no se puede borrar"):** "Una vez emitida no se puede eliminar; solo se corrige o anula con una Nota de Crédito." Botones: "Volver" / "Sí, emitir". Es el último paso antes de algo irreversible.

- **(2026-06-24, P2=A+B) Mientras AFIP procesa: texto en criollo + indicador de progreso, juntos.** Apenas se manda, se muestra (en el mismo lugar, sin cerrar de golpe ni dejar un toast suelto): un **spinner/indicador de progreso** + el texto **"Estamos emitiendo la factura en AFIP. En unos instantes vas a ver el número."** Se elimina el viejo "Comprobante AFIP encolado correctamente" (jerga). Este es el estado **PROCESANDO**. **⚠️ EVOLUCIONADO el 2026-07-08 — ver abajo: el estado PROCESANDO ya NO atrapa al usuario esperando.**

- **(2026-07-08) El "en proceso" NO atrapa al usuario: confirmación inmediata + puede seguir trabajando.** Palabra textual de Gastón (probando en vivo, con AFIP caída y 30 minutos mirando el spinner): *"se queda ahí esperando la factura como un tarado y da a entender que el sistema es lento, no puede dar esa sensación."* **Esto CAMBIA el P2/P3 del 2026-06-24 en una cosa:** apenas AFIP acepta el envío (la factura queda encolada), la ficha da una **confirmación inmediata** de que quedó en proceso y **libera al usuario en el acto** — nunca más un spinner a pantalla completa sin salida.
  - **Confirmación inmediata (verde, apenas se manda):** **"La factura quedó en proceso en AFIP. Podés seguir trabajando: te avisamos apenas salga."** (copy PENDIENTE de confirmar por Gastón — Q1 del 2026-07-08; ver nota de la campanita).
  - **Botón para irse desde el segundo cero:** el usuario puede cerrar la ficha al instante (**"Listo" / "Seguir trabajando"**). No hay estado sin botón de salida.
  - **Se mantiene lo bueno del caso rápido:** si el CAE llega en segundos mientras la confirmación sigue en pantalla, esta se transforma sola (sin refrescar) en el cartel verde de ÉXITO con el número — igual que el P3. La diferencia es que **ahora es opcional mirarlo**, no una espera obligada.
  - **Dónde queda visible el "en proceso" tras cerrar:** en la barra de acciones de la ficha ya aparece el pill ámbar **"Factura en proceso (esperando AFIP/ARCA)"** (mientras `hasInvoiceInProgress`), y el resultado final cae en el Estado de Cuenta cuando llega el CAE. Ese pill es el ancla del estado; no hace falta que el usuario espere en la ficha de emisión.
  - **Cómo se entera cuando sale (campanita):** para que el **"te avisamos apenas salga"** sea real, hace falta que el sistema mande un aviso a la campanita cuando la factura obtiene el número (o si AFIP la rechaza). **Hoy ese aviso NO existe todavía** (es una pieza de motor pendiente, no de pantalla). Si esa pieza no está lista, el copy no debe prometer el aviso: usar **"La factura quedó en proceso en AFIP. Podés seguir trabajando: apenas salga la vas a ver en el Estado de Cuenta."** (Decisión de cuál copy va = Q2 del 2026-07-08.)

- **(2026-06-24, P3=A+C) Cuando AFIP devuelve el número: éxito verde + auto-actualización en pantalla.** Cuando llega el CAE, el estado "Estamos emitiendo…" se transforma **solo, sin refrescar la página**, en el resultado emitido, y se muestra un **cartel verde de éxito** con el dato concreto: **tipo + número de factura** (ej. "✔ ¡Factura emitida! Factura B 0001-00012345"). El front consulta el estado del backend para saber cuándo pasó de procesando a emitida. Este es el estado **ÉXITO**.

- **(2026-06-24, P4=A) Si AFIP RECHAZA: cartel rojo con el motivo de AFIP + "Corregir y reintentar".** Si AFIP rechaza (CUIT inválido, datos no aceptados, etc.), se muestra un **cartel rojo** con el **motivo que devuelve AFIP** (tal cual lo da AFIP, para que se entienda qué corregir) y un botón **"Corregir y reintentar"** que vuelve a la ficha con los datos cargados intactos para corregir y reenviar. La factura NO salió. Este es el estado **RECHAZO**. (Sigue valiendo la regla de Ronda 2 2026-06-06: nunca se pierde lo cargado ante un error.)

- **(2026-06-24, P5=A+C) Factura ya emitida, desde la reserva: Ver/Descargar PDF + Enviar al cliente + número y CAE a la vista.** En la línea de la factura en el Estado de Cuenta (y donde se muestre el comprobante), se ve el **número de factura y el CAE bien a la vista**, con acciones **"Ver/Descargar PDF"** y **"Enviar al cliente"** (reusa el envío que ya existe para vouchers). Coherente con la regla 2026-06-22 (en estados congelados se puede ver/reimprimir/descargar el PDF; lo nuevo es agregar "Enviar al cliente" y mostrar número+CAE en la línea).

- **(2026-06-24) Los TRES estados visuales del resultado de emisión** (PROCESANDO / ÉXITO / RECHAZO) se diseñan contemplando la espera asíncrona. El backend expone el estado que el front consulta: en proceso / emitida (número+CAE) / rechazada (motivo). Sigue valiendo: todo EN LÍNEA, nada de ventana flotante (salvo el cartel de confirmación de P1, que es el "¿seguro?" antes de algo irreversible, mismo patrón que el "¿confirmás costo $0?" y el de la Nota de Débito fiscal).

## Configuración: caducidad automática de presupuestos y cotizaciones (G6, 2026-06-24, respuestas de Gastón)

> **Origen:** nueva regla de negocio — los Presupuestos y las Cotizaciones que no avanzan caducan a los X
> días y pasan solos a "Perdido". Los días son configurables por plataforma y POR SEPARADO para presupuesto
> y para cotización (ej. presupuesto 7 días, cotización 20 días). Sesión de 4 preguntas; Gastón eligió las
> opciones de abajo. Vive en Configuración → "Operativa, Cobranzas y Facturación" (`OperationalFinanceSettingsTab.jsx`),
> como un bloque más, con el mismo aspecto de los otros casilleros de días que ya hay.

- **(2026-06-24, P6=B) SIN interruptor: solo los dos casilleros de días.** El bloque no tiene toggle de encender/apagar. Son directamente dos casilleros numéricos. (El "apagado" se hace poniendo 0 en cada uno — ver P8.)

- **(2026-06-24, P7=A) Textos exactos de los casilleros:** **"Caducar cotización a los [ ] días"** y **"Caducar presupuesto a los [ ] días"**.

- **(2026-06-24, P8=A) 0 = no caduca nunca**, con un textito al lado que lo aclara: **"0 = no caduca nunca"**. Cada casillero es independiente (se puede tener cotización que no caduca y presupuesto que sí, o al revés).

- **(2026-06-24, P9=A) La campanita avisa ANTES de que caduque.** Aviso de "por caducar" en la campanita: **"El presupuesto de Fam. García vence en N días"** (y lo equivalente para cotización). El umbral de cuántos días antes avisar lo define el backend; el texto del aviso es el de acá. **CONFIRMADO por Gastón:** este aviso de "por caducar" es **APARTE** del aviso de fecha de viaje ("Próximos inicios") y **NO contradice** la regla 2026-06-06 ("los presupuestos no entran a Próximos inicios"): aquella es sobre el inicio del viaje; esta es sobre el vencimiento del presupuesto/cotización. Son dos secciones distintas de la campanita.

## Chip del eje Pago sin movimientos = "Sin movimientos" (2026-06-24, respuesta de Gastón)

- **(2026-06-24)** El rótulo del chip del eje **Pago** cuando la reserva **no tiene cargos ni cobros** (ningún movimiento de plata) es **"Sin movimientos"** (NO "Sin cobros"). Se suma a los rótulos del eje Pago ya definidos (Pendiente / Parcial / Pagada — guía 2026-06-22). Mantiene el prefijo "Pago:" en gris (regla ADR-035 A-quinque: chip secundario, no estado operativo).

## Anular reserva: UN solo botón que SIEMPRE funciona, con cartel correcto por caso (2026-06-25, respuestas de Gastón)

> **Origen:** Gastón reportó que "deshacer una reserva" era un caos. El botón "Anular" siempre iba por
> el camino de la Nota de Crédito (que exige factura): en una reserva sin factura mostraba un cartel verde
> que prometía "se cancela directo" y después fallaba con "contactá a administración" — un callejón sin
> salida. Además el mismo botón se llamaba "Anular" en el encabezado y "Cancelar reserva" en otra solapa.
> La lógica de negocio (4 casos) ya estaba decidida; acá se cierra CÓMO se ve y se confirma. Sesión de 4
> preguntas; Gastón aceptó las recomendadas (P1-A + P3-A combinados, P2-A, P4-A). NO reabrir.
> Refuerza el vocabulario duro Anular/Anulada (ADR-036 punto 1) y el panel en línea (ADR-035 C).
> Componente real: `CancelarReservaInline.jsx` (ya existe, ya en línea, ya se llama "Anular reserva").

- **(2026-06-25) UN solo botón, una sola palabra: "Anular reserva", en todos lados.** Se resuelve la inconsistencia "Anular" (encabezado) vs "Cancelar reserva" (solapa) a favor de **"Anular reserva"** en cualquier lugar donde la acción signifique deshacer el viaje. (Coherente con ADR-036 punto 1: "Anular" = deshacer el viaje; "Cancelar" = saldar/pagar el total. El "Cancelar" que solo cierra un panel sin guardar NO se toca.)

- **(2026-06-25) Los 4 casos y el cartel EXACTO de confirmación de cada uno** (el cartel NUNCA promete algo que después no pasa):
  - **Caso 1 — Pre-venta (Cotización / Presupuesto):** NO usa el botón "Anular". Usa el botón **"Perdida"** (icono ⊗) que ya existe en pre-venta: se marca **Perdido**, sin comprobante, con confirmación "¿Seguro?" + motivo opcional (regla del ciclo de vida + punto 7).
  - **Caso 2 — En firme, SIN factura, SIN cobros:** **Cartel VERDE.** Texto: **"Esta reserva no tiene factura emitida, se anula directo, sin nota de crédito."** (P2-A: el verbo pasa de "se cancela" a "se anula").
  - **Caso 3 — En firme, SIN factura, CON cobros (la plata queda como saldo a favor):** **Cartel CELESTE.** Texto: **"Esta reserva no tiene factura, pero el cliente ya pagó ($ 150.000 · US$ 200). Al anular, esos montos quedan como SALDO A FAVOR del cliente, para usar en otra reserva."** (P1-A + P3-A combinados: avisa que la plata NO se pierde **y muestra el monto, separado por moneda**, nunca sumado). Si la reserva es de una sola moneda, se muestra ese único monto.
  - **Caso 4 — Con factura emitida:** **Cartel ÁMBAR.** Texto: **"Esta reserva tiene factura emitida, al anular se emite la nota de crédito en AFIP/ARCA para anularla."** (P2-A: el verbo pasa de "al cancelar" a "al anular").

- **(2026-06-25) Mensaje de ÉXITO por caso** (tras confirmar):
  - Caso 2 (baja directa): **"Reserva anulada."**
  - Caso 3 (saldo a favor): **"Reserva anulada. Lo cobrado quedó como saldo a favor del cliente."** (P4-A: le confirma DÓNDE quedó la plata).
  - Caso 4 (con factura): **"Reserva anulada. La nota de crédito se está generando."** (ya existía, se mantiene).

- **(2026-06-25) Todo lo demás ya estaba decidido y se mantiene:** panel **EN LÍNEA** (nunca ventana flotante), **motivo obligatorio** (mín. 10 caracteres) — ADR-035 C; en reserva con plata viva el botón **"Eliminar" no aparece**, solo "Anular", y la explicación va recién al abrir el panel — ADR-036 P3=A; tras anular, la reserva queda en estado terminal de **solo lectura** con cartel chico arriba y sus servicios muestran **"Anulado"** — ADR-036 puntos 3 y 8.

- **(2026-06-25) Bloqueos legítimos (qué se muestra si NO se puede anular):**
  - **Más de una factura emitida:** ~~no se puede anular toda la reserva de una…~~ **DEROGADO (2026-07-01):** SÍ se puede anular; al confirmar sale **una nota de crédito por cada factura, cada una en su moneda**. Ver la sección propia **"Anular una reserva con VARIAS facturas en distintas monedas (2026-07-01)"** más abajo. (Se elimina el viejo mensaje que mandaba a una "solapa Facturas" que no existe.)
  - **En viaje:** el botón "Anular" NO aparece (regla 2026-06-22, 2da tanda punto 1). Si entró por error, va por "Sacar de viaje" (Admin).
  - **Ya terminal (Perdida / Anulada / Esperando reembolso):** el botón "Anular" NO aparece; pantalla de solo lectura.

- **(2026-06-25 — dependencia técnica, NO es decisión de UX):** para mostrar el cartel correcto, el front necesita distinguir el caso 3 del caso 2 cuando NO hay factura → el backend debe exponer en el DTO de la reserva un dato tipo **"tiene cobros sin factura"** (bool) **+ el monto cobrado por moneda** (el backend ya lo acumula en `PorMoneda`). El campo `requiresInvoiceAnnulmentToCancel` que ya existe sigue marcando el caso 4. Esto solo habilita elegir el cartel; no cambia ninguna decisión de UX.

## Anular una reserva con VARIAS facturas en distintas monedas (2026-07-01, respuestas de Gastón)

> **Origen:** hasta hoy, una reserva con más de una factura NO se podía anular (cartel de freno que
> mandaba a una "solapa Facturas" inexistente). Se saca ese bloqueo: ahora al anular sale **una nota de
> crédito por CADA factura, cada una en su moneda** (ej. una NC en $ y otra en US$). Es asincrónico contra
> AFIP/ARCA (tarda unos segundos por cada una) y **todo-o-nada a nivel ESTADO**: la reserva queda Anulada
> solo cuando salieron TODAS; si una sale y otra falla, queda **"En revisión"** (la que salió NO se
> revierte) y hay que **reintentar** la que falta. Sesión de 7 preguntas; Gastón eligió TODAS las
> recomendadas (P1-A … P7-A). Se apoya en el molde asíncrono de H2 (2026-06-24) y en las reglas duras de
> multimoneda (2026-06-09). Componente: `CancelarReservaInline.jsx`. Spec: `docs/ux/2026-07-01-anulacion-multifactura.md`.

- **(2026-07-01, P1=A) Aviso previo con la lista de facturas.** Cuando la reserva tiene 2+ facturas, el panel de anular muestra un cartel **ámbar** que anticipa cuántas notas de crédito van a salir y en qué moneda, **más la lista de cada factura con su monto**. Texto: **"Esta reserva tiene N facturas emitidas (una en $ y una en US$). Al anular se emite una nota de crédito por cada factura, cada una en su moneda."** + lista `· Factura B 0001-00012345 — $ 150.000` / `· Factura B 0001-00012346 — US$ 200`. (Reemplaza el viejo cartel de freno. Multimoneda dura: los montos NUNCA se suman.)

- **(2026-07-01, P2=A) Confirmación extra "¿Seguro?" antes de largar las notas.** Como son varias notas de crédito irreversibles, tras escribir el motivo y apretar "Anular reserva" aparece un último cartel de confirmación: **"¿Seguro? Se van a emitir N notas de crédito en AFIP (una en $ y una en US$). Una vez emitidas no se pueden deshacer."** Botones: **[Volver]** / **[Sí, anular]**. (Mismo patrón del "¿seguro?" de emitir factura, H2 2026-06-24; la anulación de UNA sola factura NO tiene este paso, solo la multi-factura.)

- **(2026-07-01, P3=A) Mientras salen (una por una): avance por nota.** Estado **PROCESANDO** con texto en criollo + una **listita que muestra el avance de cada nota**: la ya emitida con tilde ✔, la que está saliendo con relojito ⏳, y un contador **"1 de N"**. Texto: **"Estamos emitiendo las notas de crédito en AFIP. En unos instantes vas a ver el resultado."** Se auto-actualiza sin refrescar la página (el front consulta el estado del backend, igual que H2).

- **(2026-07-01, P4=A) Éxito total: detalle por moneda + saldo a favor en la MONEDA DE CADA FACTURA.** Cuando salieron TODAS, cartel **verde**: **"✔ Reserva anulada. Se emitieron N notas de crédito (una en $ y una en US$)."** Si el cliente había pagado, se agrega la línea del **saldo a favor separado por moneda**, y **la moneda del saldo a favor es la de la FACTURA anulada, no la de lo que el cliente pagó** (decisión de negocio cerrada 2026-07-01): ej. **"Lo cobrado quedó como saldo a favor del cliente: US$ 200."** Si hay saldo en dos monedas: `$ 150.000 · US$ 200`. (No se explica ninguna ley en pantalla; solo se muestra el saldo a favor por moneda.)

- **(2026-07-01, P5=A) Falla parcial → "En revisión", con detalle de cuál salió y cuál no.** Si una nota salió y otra no, la reserva NO queda anulada del todo: queda **"En revisión"** y la nota que ya salió **NO se deshace**. Cartel **naranja**: **"La reserva quedó EN REVISIÓN: una nota de crédito salió bien y la otra no. La que salió no se deshace."** + la lista mostrando **cuál salió** (✔ "Nota de crédito en $ — emitida") y **cuál no** (✗ "Nota de crédito en US$ — no salió"), con el **motivo que devuelve AFIP tal cual** debajo de la que falló ("Motivo de AFIP: «…»", igual que el rechazo de emisión H2). Botón: **[Reintentar la que falta]**. El reintento es **seguro (idempotente): no re-emite la que ya salió ni duplica** (garantía del backend).

- **(2026-07-01, P6=A) Al cerrar y volver a entrar en "En revisión": franja arriba + botón a la vista.** La anulación a medias tiene que cantar apenas se abre la reserva: **franja naranja arriba** **"En revisión — anulación a medias, falta emitir N nota(s) de crédito."** con el botón **[Reintentar anulación]** a la vista. La reserva queda de **solo lectura** salvo ese botón. (Coherente con la franja del candado 2026-06-08 y el chip "En corrección" de "Sacar de viaje" 2026-06-22.)

- **(2026-07-01, P7=A) Quién puede reintentar: cualquier vendedor que ya podía anular esa reserva.** Reintentar no es deshacer nada, es completar lo que ya empezó; no se restringe a admin.

- **(2026-07-01) Lo demás se mantiene:** panel EN LÍNEA (nunca ventana flotante, salvo el "¿Seguro?" de P2), motivo obligatorio (mín. 10 caracteres), nada de jerga/IDs/códigos internos/texto crudo de error, y **ningún mensaje vuelve a nombrar la "solapa Facturas"** (no existe). Si la reserva es de una sola factura/moneda, sigue el flujo de la sección 2026-06-25 (Caso 4), sin nada de esto.

- **(2026-07-01 — dependencia técnica, NO es decisión de UX):** el DTO debe exponer la **lista de facturas con su moneda y monto** (para el aviso de P1) y el **estado de progreso/resultado de cada nota de crédito** (procesando / emitida / rechazada + motivo de AFIP) que el front consulta para pintar PROCESANDO/ÉXITO/FALLA PARCIAL (mismo patrón que `GET /invoices/reserva/{id}/fiscal-status` de H2). El reintento debe ser **idempotente** en el backend.

## Fase 4 — "La pantalla obedece al backend": KPI del mes, botón ya-cumplido, aviso de Factura A (2026-06-26, respuestas de Gastón)

> **Origen:** Fase 4 de la auditoría integral de reservas. El front se inventaba reglas en vez de leer
> la verdad del backend (capacidades + datos por moneda). La mayor parte fue corrección sin cambio
> visible (leer `invoicingStatus`, `porMoneda`, capacidades de anular/eliminar, nunca mezclar monedas);
> tres puntos sí eran decisión de UX y Gastón los respondió. Sesión de 3 preguntas; Gastón eligió las
> tres recomendadas. NO reabrir.

**1) KPI "Cobrado este mes" = solo plata nueva real; el saldo a favor aplicado va aparte y chiquito (P1=B).**
- **(2026-06-26)** El número grande de "Cobrado este mes" muestra **solo la plata nueva que de verdad entró**, separada por moneda (nunca sumando monedas). **El saldo a favor que se aplicó de una reserva a otra NO suma al número grande** (no es plata nueva, es plata que ya estaba). Coherente con la regla de Caja 2026-06-09 ("la caja no inventa pesos que no entraron").
- **(2026-06-26)** Para que nada parezca "perdido", **debajo del número grande va una línea chica**: **"+ $ X aplicados de saldo a favor"** (con su monto por moneda, igual criterio multimoneda). Esa línea solo aparece si en el mes hubo saldo a favor aplicado; si no hubo, no se muestra.

**2) Regla unificada: botón de acción YA CUMPLIDA en una reserva activa = DESAPARECE (P2=A).**
- **(2026-06-26)** Cuando una acción **ya se hizo** y no queda nada por hacer en una reserva **todavía activa** (ej. "Emitir factura" en una reserva ya **Facturada total**), el botón **DESAPARECE**. El chip de al lado (ej. "Factura: Facturada total") ya explica el porqué; no hace falta un botón gris.
- **(2026-06-26 — UNIFICA la regla de botones apagados):** se distinguen dos motivos para que un botón no esté:
  - **Acción ya cumplida** (no queda nada que hacer) en reserva activa → el botón **se esconde** (no aparece).
  - **Acción bloqueada por estado terminal / permiso / candado** → sigue ADR-035 A (2026-06-19): botón **gris** sin motivo por botón + UN cartel arriba que explica el estado; y en estados de solo lectura los botones de escritura se ocultan (2026-06-22).
  - En criollo: si **ya está hecho**, se esconde; si **no se puede por el estado o el permiso**, va gris (o se oculta en solo-lectura). El "gris sin motivo" NO se usa para cosas ya cumplidas.

**3) Factura A a un cliente que no corresponde: avisar y FRENAR antes de emitir (P3=A).**
- **(2026-06-26)** Antes de emitir una **Factura A**, si el cliente **no es del tipo que corresponde** (no es Responsable Inscripto), el cartel de confirmación ("¿seguro?" de emitir, el de H2 2026-06-24) **muestra un aviso claro y NO deja seguir** hasta corregir el tipo de comprobante o la condición del cliente. No se manda a AFIP para que vuelva rechazada.
- **(2026-06-26 — alcance)** Gastón decidió SOLO el **aviso antes** (la parte de pantalla). La **regla fiscal de fondo** (qué condición de IVA habilita cada tipo de comprobante) la confirma el área contable/fiscal; el front solo muestra el aviso con el dato que el backend resuelva. El texto del aviso va en criollo, sin jerga ni códigos internos.

## Proveedores / Cuentas por Pagar — REDISEÑO COMPLETO: la cuenta del proveedor pasa a ser un EXTRACTO (2026-06-27, directiva de Gastón)

> **Origen:** Gastón vio la pantalla actual de cuenta corriente del proveedor (`SupplierAccountPage.jsx`,
> 5 tarjetas que MEZCLAN monedas + tabla de servicios + historial de pagos) y dijo textual:
> **"es horrible como está hecha eso, hay que hacer un rediseño completo"**, y que **la cuenta del
> proveedor debe seguir el MISMO patrón visual que la cuenta corriente del cliente** (el extracto / libro
> mayor estilo banco, ADR-040 / decisión 2026-06-22). Esto es una directiva DEL DUEÑO; resuelve el layout
> principal. El resto (cuentas bancarias, esperando reembolso, saldo a favor del operador) sigue abierto
> como preguntas (ver el bloque de preguntas relayado al orquestador, tanda 2026-06-27).

- **(2026-06-27 — directiva de Gastón — SUPERSEDE el formato "5 tarjetas + 2 tablas" de 2026-06-10/06-11)**
  La cuenta corriente del proveedor se rehace como **EXTRACTO tipo libro mayor / banco**, igual que la del
  cliente: **franja resumen arriba con el saldo POR MONEDA separado** ("Le debo en $" / "Le debo en US$",
  nunca sumados), y debajo **una sola línea de tiempo cronológica** donde cada **compra** (servicio comprado
  al operador) **SUMA** lo que le debo (cargo) y cada **pago / anticipo / reembolso recibido** **RESTA**
  (abono), mostrando el **saldo corriente después de cada movimiento**. Un bloque por moneda; jamás se mezclan
  pesos y dólares (regla dura multimoneda 2026-06-09/06-10). Las 5 tarjetas que hoy mezclan monedas DESAPARECEN.
- **(2026-06-27)** Se mantiene intacto: el **enmascarado de costos** (sin `cobranzas.see_cost`, los montos van
  "—" / "Sin permiso", nunca en verde) y la sección **"Deuda por expediente"** (deuda abierta reserva por
  reserva y por moneda, con anticipos que restan) que ya existe y ya cumple multimoneda. El **pago al
  proveedor pasa a EN LÍNEA** (debajo, dentro de la página), reemplazando la ventana flotante actual
  (`SupplierPaymentModal`), igual que el cobro del cliente (regla "el modal me parece horrible", 2026-06-09 P3).
- **(2026-06-27 — dependencia técnica, NO es decisión de UX)** Hoy existe el endpoint de extracto SOLO a nivel
  reserva (`/reservas/{id}/account-statement`, lado cliente). El extracto a nivel PROVEEDOR (todas sus
  compras/pagos en una línea de tiempo con saldo corriente por moneda) necesita un endpoint nuevo de backend.
  No cambia ninguna decisión de UX; solo habilita construir esta pantalla.

## Cuentas bancarias (CBU/alias) de agencia, clientes y proveedores — base decidida (2026-06-28)

> **Origen:** el backend ya construyó una entidad de cuentas bancarias polimórfica (un mismo "libro de
> cuentas" para tres dueños distintos: la AGENCIA, cada CLIENTE y cada PROVEEDOR). Gastón ya tomó las
> decisiones de fondo que están abajo (entregadas vía el orquestador como "decidido, no reabrir"). Lo que
> NO está cerrado (dónde vive cada pantalla, para qué se usan, devoluciones, cuenta principal, validación
> fina) quedó como PREGUNTAS relayadas a Gastón (tanda 2026-06-28). Endpoints listos:
> `GET /api/bank-accounts?ownerType=&ownerId=` (lista, CBU/alias TAPADOS), `GET /api/bank-accounts/{publicId}`
> (detalle, completo), `POST/PUT/DELETE`. `ownerType` = Agency / Customer / Supplier.

- **(2026-06-28 — decidido por Gastón, no reabrir) Se guardan cuentas bancarias de TRES dueños:** la
  **agencia**, cada **cliente** y cada **proveedor**. Cada dueño puede tener **VARIAS** cuentas.
- **(2026-06-28 — decidido) Datos de una cuenta:** banco, tipo de cuenta, CBU, alias, titular, CUIT, moneda.
  **Obligatorios:** (alias **O** CBU) + titular + moneda. El resto es libre (sin escribir "(opcional)" por
  todos lados — regla general 2026-06-05; los obligatorios se marcan con un asterisco y listo).
- **(2026-06-28 — decidido) El CBU y el alias se ven TAPADOS en las listas** (solo los últimos dígitos, ej.
  `CBU ····3344` / `alias ····.viajes`) y **completos en el detalle**, y para ver el número completo hace
  falta permiso. (Qué permiso exacto = pregunta fina abierta.)
- **(2026-06-28 — decidido) Al PAGAR a un proveedor se muestra su CBU/alias con un botón "Copiar"**, para
  copiar y pegar en el homebanking sin tipear (evita errores de transcripción). Va dentro de la ficha de
  pago en línea (`PagarProveedorInline`). (Cómo se elige si el proveedor tiene varias cuentas = pregunta abierta.)
- **(2026-06-28 — aplica regla dura existente) El alta/edición de una cuenta va EN LÍNEA, NUNCA en ventana
  flotante** (regla "el modal me parece horrible", 2026-06-09 P3 / ADR-035 C). La lista de cuentas del dueño
  + la ficha de alta/edición que se abre debajo, dentro de la misma página.
- **(2026-06-28 — aplica regla dura existente) Multimoneda:** cada cuenta tiene UNA moneda; las cuentas se
  listan tal cual, nunca se "suma" nada. No aplica acá el bloque de saldos por moneda (una cuenta bancaria no
  tiene saldo en el sistema), pero sí la coherencia visual $/US$.

> ⏳ PENDIENTE DE RESPUESTA DE GASTÓN (tanda 2026-06-28, ver bloque de preguntas relayado): dónde se
> administran las cuentas de cada dueño (Configuración / ficha proveedor / ficha cliente), para qué se usan
> las de la agencia y dónde las ve el cliente, si las del cliente sirven para devoluciones, si hay una cuenta
> "principal/preferida", cómo se elige la cuenta del proveedor al pagar si hay varias, y la validación fina
> (CBU de 22 dígitos: avisar o frenar) + qué permiso revela el número completo.

## Cuenta del operador — LOS DOS NÚMEROS + Circuito de cancelación (2026-07-01, respuestas de Gastón)

> **Origen:** el backend ya calcula, por operador y por moneda, "Le debo" (X), "Me tiene que devolver" (Y)
> —lo que el operador debe devolver por anulaciones ya pagadas—, el "Saldo a favor" consumible (prepago),
> y el "Circuito de cancelación" (multa retenida por el operador / reembolso recibido del operador). La ficha
> del operador (encabezado + 6 solapas) ya existe; estas decisiones definen CÓMO se muestran esos datos, que
> hoy la pantalla no muestra. Gastón respondió P1..P6.

- **(2026-07-01, P1+P3 — vista de arriba) Tres recuadros por moneda, con colores distintos.** El encabezado
  del operador muestra, POR MONEDA y en juegos separados de pesos y dólares, TRES recuadros:
  **"Le debo" (rojo)** = lo que la agencia le tiene que pagar; **"Me tiene que devolver" (naranja)** = lo que
  el operador debe devolver por viajes anulados ya pagados; **"Saldo a favor" (verde)** = plata a cuenta,
  gastable en la próxima compra. **Nunca el mismo verde para "Me tiene que devolver" y "Saldo a favor"**: son
  cosas distintas (una es plata que reclamás; la otra es plata que ya podés gastar). Pesos y dólares SIEMPRE
  separados, nunca sumados. **Sin permiso de ver costos (`cobranzas.see_cost`): los tres recuadros van en gris
  con "—", nunca en color** (no se revela plata a quien no puede verla).
- **(2026-07-01 — corrección de raíz del bug actual) Los recuadros dejan de derivar "A favor" del saldo de
  caja en negativo.** Hoy, cuando un viaje anulado ya pagado deja la caja "en negativo", el recuadro lo muestra
  como "A favor" en verde (como si fuera plata para gastar), y NO lo es. Los recuadros deben leer los tres
  campos limpios que ya manda el backend por moneda (X / Y / saldo a favor), no el saldo de caja crudo.
- **(2026-07-01, P2 — unificar con la solapa Reembolsos) "Me tiene que devolver" es el TOTAL; la solapa
  "Reembolsos" es el DETALLE de ese mismo total.** Un solo número, coherente arriba y adentro: el recuadro
  "Me tiene que devolver" de una moneda tiene que dar la MISMA cifra que la suma de lo pendiente listado en la
  solapa "Reembolsos" de esa moneda. No pueden divergir. (Riesgo técnico registrado: hoy salen de dos cálculos
  distintos; conciliarlos es tarea de backend, ver la spec del 2026-07-01.)
- **(2026-07-01, P4 — Circuito de cancelación) Tablita dentro de "Cuenta corriente", debajo del extracto,
  que arranca CERRADA y se abre con un click ("mostrar circuito de cancelación").** Aparece solo si el operador
  tuvo anulaciones en esa moneda. Un bloque por moneda. Cada línea muestra la **etiqueta en español tal cual**
  ("Multa retenida por el operador" / "Reembolso recibido del operador"), la fecha, el **número de reserva
  visible** y el monto. NUNCA códigos internos ni identificadores técnicos. Montos "—" sin permiso de costos.
- **(2026-07-01, P5 — estado vacío) Operador sin anulaciones: el bloque del circuito no aparece y
  "Me tiene que devolver" muestra $0 tranquilo, sin ningún cartel.**
- **(2026-07-01, P6=B — anotar el reembolso recibido) Botón "Registrar reembolso recibido" en "Cuenta
  corriente", al lado de "Registrar pago".** La acción se dispara desde Cuenta corriente (no desde la solapa
  Reembolsos). El botón obliga a **ELEGIR a qué reembolso pendiente se imputa** (una reserva/anulación puntual),
  no acepta un monto suelto sin destino. La solapa "Reembolsos" sigue mostrando el detalle/estado; registrar
  desde Cuenta corriente tiene que **actualizar lo que se ve en Reembolsos** (ambos lados consistentes). Va
  EN LÍNEA (ficha que se abre debajo), nunca ventana flotante.

## Cuenta del operador — la multa a la vista, chip Operador y navegación de reembolsos (2026-07-03, respuestas de Gastón)

> **Origen:** 5 decisiones de dirección de Gastón (2026-07-02) bajadas a pantalla; se cerraron con 6
> preguntas (P1=C, P2..P6=A). El "estimado" de reembolso hoy se ve pelado (no se entiende que ya tiene
> descontada la multa), la multa no tiene puente desde el lado del operador, el servicio no muestra su
> operador, el $0 confunde, y "Reembolsos operador" estaba en el menú principal. Componentes reales:
> `RegistrarReembolsoRecibidoInline.jsx`, `OperatorRefundsPendingSection.jsx`, `ServiceList.jsx`,
> `SupplierAccountPage.jsx`, `OperatorRefundsPage.jsx`. Spec: `docs/ux/2026-07-03-cuenta-operador-reembolsos-multa.md`.

- **(2026-07-03, decisión 1 + P3=A) "Registrar reembolso recibido": la multa a la vista con la cuenta completa.**
  En el renglón **elegido** del panel se muestra de dónde sale el estimado: **"Pagaste US$ 500 − Multa del
  operador US$ 100 = te devuelven US$ 400 (estimado)."** (con los montos y la moneda reales). El estimado
  sigue etiquetado "estimado" (sujeto a deducciones). Sin permiso de ver costos, el renglón muestra "—".
- **(2026-07-03, decisión 4 + P4=A) Reembolso $0: se explica el porqué, no se muestra "$0" seco.** En lugar
  del "US$ 0" va el motivo en criollo, según el caso que informe el backend: **"Todavía no le pagaste nada al
  operador por este viaje."** / **"No hay nada para devolver: la multa del operador se quedó con todo lo que
  le pagaste."** / (si aplica) **"Ya te devolvió todo por este viaje."** El front NO deduce el motivo restando
  montos; lo dice el backend.
- **(2026-07-03, decisión 2 + P2=A) La multa se GESTIONA desde la reserva; desde el operador solo hay puente.**
  En la solapa "Reembolsos" del operador, los casos con **multa sin confirmar** muestran el aviso **"Falta
  confirmar la multa de esta anulación."** + botón **"Ir a la reserva a confirmar"** (lleva a la reserva). La
  acción fiscal de confirmar la multa vive en **un solo lugar: la reserva**. No se confirma la multa desde la
  ficha del operador (no se duplica una acción fiscal).
- **(2026-07-03, decisión 3 + P5=A) Chip "Operador: X" en el renglón del servicio.** Debajo del nombre del
  servicio, un chip discreto **"Operador: Despegar"** que es **link a la ficha del operador** (`/suppliers/{id}/account`)
  **solo si el usuario puede ver proveedores**; si no tiene ese permiso, se ve el texto plano sin link. Aplica
  en desktop y mobile.
- **(2026-07-03, decisión 5 + P1=C) "Reembolsos operador" sale del menú principal, SIN vista global de
  reemplazo.** Los reembolsos se ven **operador por operador**, en la solapa "Reembolsos" de cada ficha (que ya
  existe). **Trade-off aceptado a sabiendas por Gastón (eligió C contra la recomendación):** ya no hay una
  pantalla que junte los reembolsos **vencidos de todos los operadores**; para detectarlos hay que entrar
  operador por operador. Mitigación futura POSIBLE (no diseñada, no comprometida): un aviso en las **tarjetas
  de Cobranzas** (donde ya viven avisos tipo "deuda con proveedores"), reusando ese patrón existente.
- **(2026-07-03, P6=A) La multa y "lo pagado al operador" se tapan como todo costo.** Quien no tiene
  `cobranzas.see_cost` ve "—" y el aviso único; nunca el monto de la multa ni lo pagado. Coherente con la regla
  general de enmascarado de costos (2026-06-05).
- **(2026-07-03 — dependencia técnica, NO es UX)** las decisiones 1 y 4 requieren que el backend exponga por
  caso y moneda: **lo pagado al operador**, **la multa retenida** y el **motivo del $0**, todos enmascarados por
  `cobranzas.see_cost`, con `estimado = pagado − multa`. Y la decisión 2 requiere un flag "multa pendiente de
  confirmar" por caso. Detalle en la spec del 2026-07-03.

## El paso de la multa del operador vive también en la reserva ya Anulada (2026-07-04, coherencia validada)

> **Origen:** decisión de negocio CERRADA de Gastón (2026-07-03): cuando se anula una reserva y NUNCA se le
> pagó nada al operador, la reserva se cierra sola (queda **"Anulada"**) aunque la pregunta "¿el operador
> cobra multa?" siga sin responder. Esa pregunta queda como **tarea pendiente que se responde DESPUÉS, desde
> la ficha de la reserva ya anulada**. Antes, ese paso (el bloque con los botones "Sí cobró" / "No cobró" y el
> "Deshacer") solo aparecía en el estado interno "esperando reembolso del operador". Como ahora la reserva
> puede quedar Anulada con la multa todavía sin decidir, el paso tenía que poder verse también ahí, o la tarea
> quedaba invisible. Componente reusado (sin duplicar UI): `ConfirmarMultaOperadorInline` + el bloque de
> elección en `ReservaDetailPage`. Validación de este agente, sin nuevas preguntas: todo deriva de la guía y de
> la decisión cerrada.

- **(2026-07-04) EXCEPCIÓN acotada a la "pantalla muerta" de Anulada (ADR-036 punto 3).** La regla general
  sigue firme: una reserva **Anulada** es pantalla de solo lectura, con el cartel chico arriba y **sin botones**.
  La **única excepción** es el **paso de la multa del operador** cuando quedó pendiente (o se cerró sin multa y
  se puede deshacer): ese bloque —y solo ese— sí aparece en la reserva Anulada, porque es una **tarea fiscal
  pendiente que Gastón decidió resolver desde la ficha ya anulada**. No habilita editar, cobrar ni nada más: la
  reserva sigue siendo de solo lectura en todo lo demás. Coherente con "la acción fiscal de confirmar la multa
  vive en un solo lugar: la reserva" (2026-07-03).
- **(2026-07-04) Es el MISMO bloque, no uno nuevo.** El bloque de la multa (cartel + "Sí cobró" / "No cobró" +
  "Deshacer", este último solo para administradores) es exactamente el que ya existía en "esperando reembolso".
  No se duplica pantalla ni se inventa un diseño paralelo: se muestra el mismo, gobernado por la capability y el
  resultado que expone el backend (no por re-derivar el estado en el front).
- **(2026-07-04) Título del cartel según el estado real (copy VALIDADO).** El cartel de arriba dice cosas
  distintas según lo que de verdad esté pendiente, para no mentir:
  - Si la reserva ya se cerró (**Anulada / Cancelled**) porque no había nada que reembolsar y lo único pendiente
    es la multa → **"Anulada — falta decidir la multa del operador — solo lectura."**
  - Si todavía se espera plata del operador (**esperando reembolso / PendingOperatorRefund**) → se mantiene
    **"Anulada, esperando el reembolso del operador — solo lectura."**
  - **Las dos usan "Anulada"**, nunca "Cancelada" (ADR-036 punto 1). Decir "esperando el reembolso" cuando no
    hay nada para reembolsar sería falso; por eso el título cambia. Copy en criollo, sin jerga, con el sufijo
    "— solo lectura." igual que todos los carteles terminales (ADR-035 A).
- **(2026-07-04) Cuando el paso ya se resolvió sin multa** el cartel pasa a **"Anulada — cerrada sin multa del
  operador. Solo lectura."** y desaparecen los dos botones; queda solo el enlace discreto **"Deshacer"** para
  administradores (ya estaba así; se conserva).

## Listado de reservas: pestaña "Anuladas" aparte + buscador global (2026-07-05, respuestas de Gastón)

> **Origen:** Gastón reportó que "hoy la vista las mezcla". Verificado en código: la pestaña
> "Finalizadas" traía por dentro Finalizadas (Closed) **y** Anuladas (Cancelled) juntas, y las
> "Esperando reembolso" no caían en ninguna pestaña. Además el buscador ya encontraba por número de
> reserva y por cliente, pero solo dentro de la pestaña y el período. Sesión de 5 preguntas; Gastón
> eligió todas las recomendadas (P1-A … P5-A). Spec: `docs/ux/2026-07-06-listado-finalizadas-vs-anuladas.md`
> y `docs/ux/2026-07-06-buscador-reserva-y-cliente.md`.

- **(2026-07-05, P1=A) Las Anuladas salen a su PROPIA pestaña "Anuladas".** Se separan de las
  Finalizadas: la pestaña "Finalizadas" pasa a mostrar **solo** las reservas finalizadas de verdad
  (Closed), y se agrega una pestaña nueva **"Anuladas"** con su contador, al lado de Finalizadas,
  con la misma mecánica que el resto de las pestañas. La palabra es **"Anuladas"**, nunca "Canceladas"
  (vocabulario duro ADR-036 punto 1).
- **(2026-07-05, P2=A) La pestaña "Anuladas" incluye también las "Esperando el reembolso del operador".**
  Son anuladas con la multa del operador sin decidir; van en el mismo cajón (hoy no aparecían en
  ninguna pestaña). El contador de "Anuladas" las suma.
- **(2026-07-05, P3=A) Diferenciación visual: con el badge alcanza.** Dentro de la lista, el badge
  ya distingue **Finalizada** (gris, ✅) de **Anulada** (rojo/rosa, 🚫) y **Esperando reembolso**
  (rosa, ⏳). NO se atenúa ni se tacha la fila de la anulada: el cartelito de estado es suficiente.
  (Coherente con "las etapas que ya existían no cambian de color".)
- **(2026-07-05, P4=A) El buscador del listado busca en TODAS las reservas.** Cuando el vendedor
  escribe en el casillero de búsqueda, se busca en **cualquier estado y cualquier fecha**, ignorando
  la pestaña y el período que estén puestos. Así encontrar a un cliente o una reserva vieja no depende
  de estar parado en la pestaña correcta. (El buscador ya cruzaba número de reserva + nombre de la
  reserva + nombre del cliente/pagador; esto solo amplía el alcance.) Al limpiar la búsqueda, la lista
  vuelve a respetar la pestaña y el período elegidos.
- **(2026-07-05, P5=A) Texto del casillero de búsqueda: "Buscar por N° de reserva o cliente…"**
  (reemplaza "Buscar reservas…", para que se note que también encuentra por cliente). Un solo
  casillero, nunca dos separados.

## Servicios cancelados de la reserva: escondidos por defecto + historial de estados (2026-07-05, respuestas de Gastón)

> **Origen:** Gastón quiere trackear qué se compró / solicitó / confirmó / canceló sin que la lista
> de servicios se llene de tachados. Los cancelados ya se mostraban tachados con "Cancelado por X".
> Sesión de 3 preguntas; Gastón eligió todas las recomendadas (P6-A, P7-A, P8-A). Componente:
> `ServiceList.jsx`. Spec: `docs/ux/2026-07-06-servicios-cancelados-filtro-e-historial.md`.

- **(2026-07-05, P6=A) Los servicios cancelados van ESCONDIDOS por defecto.** La lista arranca
  mostrando solo los servicios vivos. Un control **"Ver también los cancelados (N)"** los muestra
  (y "Ocultar cancelados" los vuelve a esconder). El número N reusa el mismo conteo que el contador
  "N de M servicios cancelados" ya existente (nunca dos números que puedan diferir). Si no hay
  ningún cancelado, el control no aparece.
- **(2026-07-05, P6=A) Cuando se muestran, los cancelados se ven como hasta ahora:** tachados, con
  motivo + quién + cuándo (regla 2026-06-13, ciclo de vida #9). El total al pie sigue **sin contarlos**.
- **(2026-07-05, P7=A) El control va ARRIBA de la lista de servicios, al lado del título "Servicios".**
- **(2026-07-05, P8=A) Historial de estados por servicio: enlace "Ver historial" desplegable EN LÍNEA.**
  Cada servicio tiene un enlace chico **"Ver historial"** que, al tocarlo, despliega debajo (dentro de
  la página, NUNCA ventana flotante) la lista de pasos por los que pasó: **estado + fecha + quién**
  (ej. "04/07 Solicitado al operador — Juan", "06/07 Confirmado por el operador — Juan", "08/07
  Cancelado — María · Motivo: …"). La línea corta "Cancelado por X" de la fila **se mantiene** como
  resumen rápido; el historial es el detalle completo, opcional. (Depende de que el backend exponga
  los pasos por servicio; hoy solo hay `workflowStatus`/`cancelledAt`/`cancelledByUserName`.)

## Pasajeros: alta y edición EN LÍNEA (muere el modal) (2026-07-05, respuestas de Gastón)

> **Origen:** decisión cerrada de Gastón — sacar el modal de pasajeros (`PassengerFormModal`), todo en
> línea, coherente con "el modal me parece horrible" y con servicios/cobro/cancelación/pago a proveedor.
> Sesión de 2 preguntas; Gastón eligió las recomendadas (P9-A, P10-A). El doble autocompletado ya estaba
> decidido en la guía (2026-06-23 "Pasajero reutilizable", más arriba). Componentes: `PasajeroInlineForm.jsx`
> (a extender), `PassengerFormModal.jsx` (a jubilar como ventana). Spec:
> `docs/ux/2026-07-06-pasajeros-alta-edicion-en-linea.md`.

- **(2026-07-05) El alta y la edición de pasajeros dejan de abrir una ventana.** Se usan EN LÍNEA, con
  el mismo mini-formulario que ya existe para los renglones vacíos (`PasajeroInlineForm`), una sola
  ficha para crear y editar (mismo patrón que los servicios). El modal `PassengerFormModal` muere como
  ventana flotante.
- **(2026-07-05, P9=A) Editar un pasajero ya cargado: la FILA se abre en el lugar.** Al tocar "Editar",
  la propia fila del pasajero se vuelve editable ahí mismo (mismo patrón que "Confirmar costo en la
  misma fila"), no se abre una ficha aparte abajo. El alta de un pasajero nuevo (botón "Agregar
  Pasajero" o "[Cargar]" de un renglón vacío) abre la misma ficha vacía en línea.
- **(2026-07-05, P10=A) Qué campos se ven de entrada: SOLO nombre + tipo y N° de documento.** El resto
  (fecha de nacimiento, nacionalidad, teléfono, email, género, notas) va detrás de **"Más detalles"**,
  cerrado por defecto (regla general 2026-06-05: solo lo imprescindible a la vista; sin textos
  aclarativos ni "(opcional)"). Los obligatorios reales por tipo de servicio siguen siendo los de la
  regla 2026-06-15 (aéreo pide nombre + documento de todos; hotel/traslado, solo el titular).
- **(2026-07-05) El doble autocompletado SE CONSERVA en el inline** (ya decidido, guía 2026-06-23
  "Pasajero reutilizable"): al tipear nombre o documento, sugerir pasajeros que ya viajaron antes
  (base propia de la agencia) + el botón de lupa del padrón AFIP en el campo documento (manual, como en
  el modal), + el aviso suave de duplicado si el mismo documento ya está en la reserva. No se pierde
  nada de lo que hacía el modal.
- **(2026-07-05) En estados de solo lectura** (Perdida / Anulada / En viaje / Finalizada) los botones
  Agregar / Editar / Borrar pasajero se ocultan y la lista queda informativa (regla ADR-035 A-ter,
  2026-06-19). Sin cambios.

## Voz de los avisos y notificaciones (2026-07-08)

Regla de escritura para TODO mensaje que el sistema le muestra al usuario: avisos de la campanita,
notificaciones, textos de éxito y de error, carteles de la ficha. Nace de la auditoría de
`ux-ui-travel-retail` + 2 decisiones del dueño (ver abajo). Aplica a texto nuevo y a todo texto que se
toque.

Las 8 reglas de voz:

1. **El sujeto es la reserva o el agente, nunca "el sistema".** Se habla de la reserva (F-2026-xxxx) o
   de la persona que hizo la acción. Nada de "el proceso", "el job", "la tarea".
2. **Primero qué pasó, después qué hacer; nunca el CÓMO interno.** El usuario quiere saber el estado y
   el próximo paso, no cómo funciona por dentro.
3. **Prohibido nombrar la maquinaria.** Nada de "chequeo nocturno", "job", "se reintentará
   automáticamente" (se dice "la estamos reintentando por vos"), "error técnico", "revisión manual",
   "anulación automática".
4. **Nada de contaduría ni leyes en los avisos.** "nota de crédito" → "devolución"; "nota de débito" →
   "cargo de la agencia" o "multa"; sin "CAE", sin "RG ...". El término fiscal (nota de crédito, CAE)
   vive SOLO en las pantallas de facturación, no en la campanita.
5. **Nada de internos.** "DOL"/"PES" → "dólares"/"pesos"; NUNCA el id interno de la reserva (siempre el
   número de negocio F-2026-xxxx); sin estados crudos en inglés.
6. **Rioplatense (vos), una sola voz** en todo el producto.
7. **Corto.** El aviso es un aviso, no un informe.
8. **El detalle técnico va al log, jamás a la campanita.**

Dos decisiones del dueño que fijan el criterio (2026-07-08):

- **Los avisos dicen "devolución", no "nota de crédito".** El término fiscal queda reservado a las
  pantallas de facturación.
- **Los avisos de éxito llevan SOLO el número de reserva (F-2026-xxxx), sin número fiscal** (sin número
  de comprobante ni CAE).

Glosario de palabras que SÍ se usan con el usuario:

- **Reserva**: siempre por su número de negocio F-2026-xxxx. Nunca el id interno.
- **Anular ≠ Cancelar**: "Cancelar" = el cliente abona el total; "Anular" = dejar sin efecto el viaje.
- **Factura**: el comprobante de venta.
- **Devolución**: es la nota de crédito. El término "nota de crédito" solo aparece en las pantallas de
  facturación, nunca en los avisos.
- **Cargo de la agencia** o **Multa**: es la nota de débito.
- **Operador**: el proveedor mayorista.
- **saldo sin cobrar**: lo que el cliente todavía debe.
- **saldo a favor**: plata a favor del cliente (o del operador, según el caso).

## Multas por operador — Tanda 4 de ADR-044: otro cargo, factura destino, monitor de comprobantes, ajuste por el dólar (2026-07-10, respuestas de Gastón)

> **Origen:** Tanda 4 del rediseño integral de multas (ADR-044). El backend ya soporta multas por
> servicio/operador, más de un cargo por operador (fee + retención simultáneos, confirmado por el
> contador), factura destino cuando hay 2+ facturas activas, y el ajuste por tipo de cambio. Sesión
> de 10 preguntas; Gastón eligió todas las recomendadas (P1..P9 = A) y respondió P10 con matices.
> Spec: `docs/ux/2026-07-10-t4-multas-pantallas.md`. **NO reabrir.** Sigue valiendo todo lo anterior
> del paso de multa (2026-07-08 "el paso de multa vive en la ficha") y las 3 reglas duras de
> multimoneda (2026-06-09).

**El caso simple NO cambia (base).** Anular con UN solo cargo del operador y UNA sola factura se
sigue resolviendo con el panel de siempre (monto + moneda precargada de la factura + fecha +
referencia, SIN pregunta de tipo de cargo). Todo lo de abajo es para los casos con más de un cargo o
más de una factura, y viene escondido con defaults.

- **(2026-07-10, P1=A) "Agregar otro cargo de este operador" = link discreto, escondido.** Cuando el
  mismo operador cobra dos cosas a la vez (ej. cargo administrativo + retención fiscal), se agrega un
  segundo cargo desde un **link discreto debajo del cargo ya confirmado** ("+ Agregar otro cargo de
  este operador"). Es acción secundaria y opcional: quien no la usa ni se entera. Va EN LÍNEA, nunca
  ventana.

- **(2026-07-10, P2=A) En ese "otro cargo" SÍ se pregunta el tipo, con desplegable en español.** El
  formulario del segundo cargo tiene un **desplegable "Tipo de cargo"** con las 4 opciones en
  palabras (cargo administrativo / impuesto / retención fiscal / otro). Los tokens internos NUNCA se
  muestran crudos. **Esto NO contradice** la regla 2026-07-08 ("el tipo de cargo no se pregunta en el
  camino normal"): el camino normal sigue sin preguntarlo; el tipo solo aparece en esta acción
  secundaria.

- **(2026-07-10, P3=A) "Cómo lo cobra el operador" va detrás de "Más detalles", cerrado.** La
  elección "lo descuenta de lo que te va a devolver / te lo factura aparte" (y el documento del
  operador cuando es facturado aparte) vive dentro de "Más detalles" del formulario del otro cargo,
  cerrado por defecto.

- **(2026-07-10, P4=A) "Qué pasa con el cliente" va también en "Más detalles", con default "tal
  cual".** Las opciones "se le traslada tal cual / + un cargo de gestión (con monto) / lo absorbe la
  agencia" viven en "Más detalles", y vienen marcadas por defecto en **"se le traslada tal cual"**.

- **(2026-07-10) Recuadro de tipo de cambio SOLO si el cargo cruza de moneda.** Si el cargo está en
  una moneda distinta de la factura del cliente, aparece el recuadro de TC ya conocido (TC + fuente +
  fecha, los tres obligatorios). **El TC que manda es el del DÍA EN QUE EL OPERADOR COBRÓ la multa**
  (decisión de negocio de Gastón), no el del día de emisión del cargo al cliente. Rótulo sin jerga:
  "Tipo de cambio del día que el operador cobró". Nunca aparece la frase "diferencia de cambio".

- **(2026-07-10, P5=A) Elegir la factura destino con 2+ facturas: desplegable en el mismo panel.**
  Con UNA sola factura, el sistema la usa sola, sin mostrar nada. Con **2 o más facturas activas**,
  al confirmar la multa (o cargar otro cargo) aparece un **desplegable "¿A qué factura del cliente
  corresponde?"** con el formato ya aprobado (`· Factura B 0001-00012345 — US$ 200`). El botón de
  confirmar queda apagado hasta elegir. Términos fiscales permitidos (es facturación de la ficha).

- **(2026-07-10, P6=A) Si la multa ya salió, no se puede cambiar la factura.** En vez del
  desplegable va el cartel: **"El cargo de esta multa ya se emitió; no se puede cambiar la factura."**
  Si el cargo quedó trabado por falta de factura destino (o la factura destino se anuló), en la ficha
  aparece un cartel naranja de acción trabada con el botón **"Elegir la factura"** (mismo patrón que
  "Corregir monto y moneda").

- **(2026-07-10) El comprobante del pasajero SÍ nombra al mayorista en el renglón de la multa.**
  Decisión de negocio de Gastón: la nota de débito que recibe el pasajero **identifica al operador**
  en el renglón de la multa; no es un cargo anónimo. Es dato del comprobante (pantalla de
  facturación), donde el nombre del mayorista se permite.

- **(2026-07-10, P7=A + P8=A + P9=A) Fin de "Pendientes con AFIP": monitor "Comprobantes por
  resolver" dentro de Facturación.** La pantalla "Pendientes con AFIP" se DESARMA. **Se saca su
  entrada del menú de GESTIÓN**; todo vive dentro de la pantalla de **Facturación**. Adentro:
  - **"Comprobantes por resolver"**: UNA sola **lista pasiva** (solo para mirar; cada fila es link a
    la ficha, sin botones propios) que **funde** las multas/cargos y las notas de crédito por
    revisar. Columnas mínimas: comprobante · reserva (F-2026-xxxx) · qué falta en criollo · hace
    cuánto. En las notas de crédito, se suma el aviso de vencimiento como "vence en X días". **Nunca**
    muestra el texto crudo del error de AFIP.
  - **"Recibos por regularizar"**: queda **aparte** porque tiene **acciones reales** (no es solo un
    espejo). **Conserva su nombre.**
  - Cada lista respeta su propio permiso (`cobranzas.invoice_annul` / `cobranzas.view_all` /
    `approvals.review`), como hoy.

- **(2026-07-10) Extracto del operador: las líneas nuevas llevan el chip "Anulación".** El "cargo del
  operador facturado aparte" y el "ajuste por el dólar" son movimientos de una anulación: llevan el
  mismo chip **"Anulación"** que la multa retenida y el reembolso recibido (hoy el chip solo cubre
  esas dos; se suman los kinds nuevos). El resto del extracto (bloque por moneda, Cargo/Abono/Saldo,
  enmascarado de costos) no cambia.

- **(2026-07-10, P10) El ajuste por tipo de cambio se llama "Ajuste por el dólar" y es
  CONFIGURABLE.** La regla dura de multimoneda (la frase "diferencia de cambio" NUNCA aparece) **queda
  intacta**: la línea del extracto del operador se rotula **"Ajuste por el dólar"**. Quién lo asume es
  configurable (palabra de Gastón: "contemplá todos los casos posibles — un cliente puede hacer una
  cosa y otro otra, lo mismo con los operadores"):
  - **Configuración general de Facturación**: un renglón "Ajuste por el dólar en las multas — ¿quién
    lo asume por defecto? (•) El cliente / ( ) La agencia". Default: **El cliente**.
  - **Ficha del operador**: un campo opcional escondido "(•) Como la configuración general / ( ) Lo
    asume la agencia / ( ) Lo asume el cliente". Default invisible: **"Como la configuración
    general"** (solo se aparta el operador que lo necesite). Es config, no monto: no expone cifras.

- **(2026-07-10) Los textos del cartel multi-operador NO se cambian.** El cartel de "más de un
  operador con multa" y los carteles de acción trabada ya cumplen las 8 reglas de voz (dicen "multa",
  "a mano", "Anulada", sin jerga): se conservan tal cual.

## Corregir una multa cuando la moneda no coincide con la factura (2026-07-13, respuestas de Gastón)

> **Origen:** una multa cargada en una moneda (ej. US$ 200) distinta de la de la factura del cliente
> (ej. pesos) quedaba trabada para siempre: "Corregir monto y moneda" la volvía a trabar con el mismo
> motivo, en círculo (bug F-2026-1033). Solución: cuando las monedas no coinciden, el MISMO panel en
> línea "Corregir monto y moneda" **guía la conversión** (monto en la moneda del operador + fecha en
> que el operador cobró → el sistema sugiere el dólar del BNA de ese día → muestra el resultado ya
> convertido a la moneda de la factura). Sigue valiendo todo lo anterior del paso de multa (2026-07-08
> "el paso de multa vive en la ficha", 2026-07-10 T4) y las 3 reglas duras de multimoneda (2026-06-09).
> Spec: `docs/ux/2026-07-13-modal-conversion-multa-moneda-cruzada.md`.

- **(2026-07-13) El caso de misma moneda NO cambia.** Si la multa está en la misma moneda que la
  factura, el panel "Corregir monto y moneda" se ve y funciona EXACTAMENTE como hoy. El bloque de
  conversión SOLO aparece cuando la moneda de la multa es distinta de la de la factura (misma regla
  dura del recuadro de TC "solo si cruza", 2026-06-09 P4 / 2026-07-10).

- **(2026-07-13) El bloque de conversión tiene: fecha en que el operador cobró + tipo de cambio de
  ese día + línea de resultado**, los tres obligatorios. El vendedor carga el monto en la moneda del
  operador (no convierte a mano); el sistema muestra "→ Se le cobra al cliente $ 240.000" ya en la
  moneda de la factura. Rótulo del TC: "Tipo de cambio del día que el operador cobró" (2026-07-10).
  Nunca aparece la frase "diferencia de cambio".

- **(2026-07-13, P2=A) El dólar sugerido viene YA ESCRITO en el casillero y se pisa escribiendo
  encima.** Al elegir la fecha, el casillero de tipo de cambio arranca con el dólar oficial del BNA
  de ese día (editable); para poner otro, se escribe encima. Debajo, en gris: "Dólar oficial del BNA
  del 05/07. Si ponés otro número, lo tomamos 'a mano'." La **fuente se resuelve sola**: vale "Dólar
  oficial del BNA" mientras no se toque, y pasa a **"a mano"** apenas se cambia el número (no hay
  desplegable de fuente que preguntar). Si NO hay dólar del BNA guardado para esa fecha, el casillero
  arranca vacío con el texto "No tenemos el dólar del BNA para el 05/07. Escribí el tipo de cambio a
  mano." y no se puede guardar hasta escribir uno.

- **(2026-07-13, P1=A) Aviso suave, NO frena, si el dólar escrito está muy lejos del oficial.** Si el
  usuario pisa el sugerido con un número que se aparta bastante del dólar del BNA de ese día, aparece
  un cartelito amarillo debajo del campo ("⚠ El dólar que pusiste está muy lejos del oficial.
  Revisalo."). Es solo un aviso para atajar un error de tipeo: **se puede guardar igual.** El umbral
  de "muy lejos" es una regla de negocio nueva (hoy la pantalla de cobro no tiene este aviso): lo fija
  dominio/negocio, no lo inventa el front; hasta que esté fijado, el aviso queda apagado y no bloquea
  el resto de la pantalla.
