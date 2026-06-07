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

_(pendiente)_

## Botones y acciones

_(pendiente)_

## Ventanas emergentes y avisos

_(pendiente)_

## Navegación

_(pendiente)_

## Colores y estilo

_(pendiente)_

## Textos

_(pendiente — coordinar con el agente `ux-ui-travel-retail`, que cuida que la app no hable en jerga técnica)_

---

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
- **Etiqueta violeta "creado en venta": CON el tipo de servicio.** Texto: "Hotel creado en venta" (la versión del dibujo aprobado, no la corta). Mismo patrón para los demás tipos: "Aéreo creado en venta", "Traslado creado en venta", "Paquete creado en venta", "Asistencia creada en venta".

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
