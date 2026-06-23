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

_(pendiente)_

## Navegación

_(pendiente)_

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
  - **Esperando reembolso:** "Cancelada, esperando el reembolso del operador — solo lectura."
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
- **(2026-06-22)** Queda registrado en el historial como **corrección** (distinto de un "volver atrás" normal): quién, cuándo y por qué.
