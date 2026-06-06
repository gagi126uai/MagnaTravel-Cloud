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
