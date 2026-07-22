# Huecos de la Constitución vs. ERPs maduros — propuestas para firmar

**Fecha:** 2026-07-22 · **Autor:** experto en sistemas ERP (lente: ciclo de vida del documento y mecánica operativa, no autoridad fiscal).

> **Qué es esto.** Un análisis de lo que le **falta** a la Constitución del producto (v1,
> `2026-07-22-constitucion-producto-v1.md`) para que MagnaTravel sea un ERP **completo y vendible**
> en su categoría: el circuito de una agencia minorista de venta (presupuesto → reserva →
> confirmación → facturación → cobranza → cierre), las compras al operador, y las
> cancelaciones/reembolsos.
>
> **TODAS las reglas de acá son PROPUESTAS SIN FIRMAR (E-1, E-2…).** Ninguna es una decisión tomada.
> Gastón decide una por una, igual que las reglas de la Constitución. Lo que él apruebe pasa a la
> Constitución con su número definitivo; lo que rechace, se descarta o se convierte en "vacío conocido".
>
> **No repito reglas que la Constitución ya tiene.** Cuando algo ya está resuelto por código o por una
> regla firmada, lo aclaro en vez de proponerlo de nuevo.
>
> **Método.** Contrasté la Constitución contra el código real del repo y contra cómo lo resuelven ERPs
> conocidos (SAP Business One, Dynamics 365 Business Central, Odoo, NetSuite). Marco qué **verifiqué en
> el código** y qué es **criterio de ERP**.

---

## Lo que YA está (para no proponerlo de nuevo)

Antes de la lista, aclaro tres cosas que **sí existen** en el código hoy, para no pedir lo que ya está:

- **Rastro de cambios (audit):** existe una tabla de auditoría y un servicio que la escribe
  (`AuditLog.cs`, `AuditService.cs`), con dos categorías (movimientos operativos vs. de sistema) y se
  graba en la misma transacción que el cambio. **Lo que falta no es "tener audit", es garantizar que
  cubra TODO, que no se pueda tocar, que guarde el motivo, y que el usuario pueda verlo por documento**
  → eso es la propuesta E-5, no "crear auditoría de cero".
- **Numeración de documentos:** existe un contador por tipo de documento y por año
  (`BusinessSequence.cs`); la numeración fiscal la da AFIP. **Lo que falta es la regla transversal** de
  que ningún número se saltea ni se reusa, y que un documento anulado se queda con su número → E-6.
- **Choque de dos usuarios a la vez (concurrencia):** el código ya tiene control optimista de Postgres
  (`UseXminAsConcurrencyToken`) en las entidades de plata, así que **no hay pisadas silenciosas** a
  nivel base de datos. Esto ya está anotado como **vacío V-1** en la Constitución. No lo propongo de
  nuevo; solo sugiero (al final, nota N-1) darle forma de regla de negocio a "quién gana y cómo se le
  avisa al que perdió".

---

## PRIORIDAD ALTA — su ausencia puede costar plata o datos

### E-1 · Cierre de mes: lo cerrado no se toca nunca más
Una vez que se "cierra" un mes (porque el contador ya presentó impuestos con esos números), **nadie
puede crear, editar ni anular nada con fecha dentro de ese mes cerrado**. Queda congelado.

- **Por qué.** Hoy no existe ningún cierre de período en el código (lo verifiqué: no hay nada parecido
  a "fecha de bloqueo" ni "período contable"). Ejemplo cotidiano: el contador presentó el IVA de marzo;
  en mayo un vendedor anula por error una factura de marzo. Ahora los números del sistema **ya no
  coinciden con lo que se presentó a AFIP**, y no hay forma de saber que se rompió. Todos los ERPs seri­os
  tienen esto: SAP Business One usa "Posting Periods" con estado (abierto/cerrado); Dynamics 365 BC tiene
  "Allow Posting From/To" y "Inventory Periods"; Odoo tiene "Lock Date" (fecha de bloqueo contable);
  NetSuite tiene "Accounting Period Close". Es de las cosas más básicas de un ERP y es de las **más caras
  cuando falta**.
- **Prioridad: ALTA** (integridad fiscal; una vez presentado a AFIP, editar hacia atrás es un problema real).

### E-2 · Deudas vencidas a la vista (aging de clientes y operadores)
El sistema tiene que mostrar, por cliente y por operador, **cuánto se debe y desde hace cuánto** (al día,
vencido hace 30, 60, 90 días). No alcanza con "cuánto debe en total".

- **Por qué.** Hoy la posición de deuda existe pero **sin tramos de vencimiento** (lo verifiqué: no hay
  aging real, solo alertas de fechas límite sueltas). Ejemplo: un cliente quedó debiendo saldo de un
  viaje ya realizado; sin una lista de "vencidos hace 60 días" nadie lo llama y la plata no entra. Del
  lado de compras es igual: saber qué le vence pagar al operador esta semana. Todos los ERPs traen el
  "Aging Report" / "Customer Ageing" como reporte de fábrica (SAP B1 "Ageing", BC "Aged Receivables/
  Payables", Odoo "Aged Partner Balance", NetSuite "A/R Aging"). Sin esto, la cobranza se hace de memoria.
- **Prioridad: ALTA** (plata que no se cobra a tiempo es plata que se pierde).

### E-3 · Un cliente / un operador = un solo legajo (sin duplicados, con fusión)
No puede haber "Juan Pérez" cargado tres veces. El sistema **avisa cuando se está por duplicar** (mismo
CUIT/documento) y ofrece una forma de **fusionar dos fichas en una** sin perder su historia ni su plata.

- **Por qué.** Hoy no existe ninguna fusión de clientes ni de operadores (lo verifiqué: no hay merge en
  el código). Ejemplo cotidiano: el mismo cliente se carga dos veces con el nombre escrito distinto; su
  saldo queda **partido en dos fichas**, una figura debiendo y la otra a favor, y el estado de cuenta
  miente. Con operadores es peor: la deuda a un mismo operador aparece dividida y nunca se ve el total
  real. Todos los ERPs tienen dedupe + merge de "Business Partners" (BC "Merge Duplicates", NetSuite y
  Odoo tienen asistente de fusión de contactos). Cuando el producto se venda a agencias con varios
  empleados cargando datos, **los duplicados aparecen solos**.
- **Prioridad: ALTA** (saldos partidos = plata mal contada y decisiones sobre datos falsos).

### E-4 · Cada acción sensible pide su permiso; no todos pueden todo
El sistema tiene que poder decir "este puede vender pero **no puede anular**", "este ve costos, este no",
"este cobra pero **no da descuentos grandes**". Las acciones que mueven plata o rompen reglas
(anular, reembolsar, editar un costo, dar un descuento fuera de tope) van **atadas a un permiso**, no
disponibles para cualquiera.

- **Por qué.** Hoy en la práctica una sola persona hace todo, y la Constitución misma dice que "al vender,
  no". El armazón de permisos existe a medias en el código pero **el control de descuentos está muerto**
  (verificado: el permiso y el tope existen, pero ninguna pantalla los usa y no hay campo de descuento).
  Ejemplo: una agencia con tres vendedores necesita que solo el dueño anule reservas; si todos pueden
  anular, cualquiera borra una venta firme. Esto es la **segregación de funciones** (SoD), columna de
  cualquier ERP vendido a más de una persona. No hace falta implementarlo entero hoy, pero **la regla
  transversal tiene que existir** para que ninguna obra nueva nazca asumiendo "todos son administradores".
- **Prioridad: ALTA** (para el objetivo de vender el producto; sin esto no es vendible a una agencia con empleados).

### E-5 · Rastro completo, que no se toca, y que el usuario puede ver
**Todo** cambio de plata, de estado o de dato importante deja registro con **quién, cuándo, qué y por qué**;
ese registro **no se puede editar ni borrar** (solo se agrega); y cada documento (reserva, factura, cobro,
nota) muestra su propia **historia en criollo** al usuario.

- **Por qué.** La infraestructura de auditoría ya existe (verificado: `AuditLog` + `AuditService`), pero
  hoy **depende de que cada obra se acuerde de escribirla** (no es automática ni garantizada para todo),
  la tabla es editable como cualquier otra (no es "solo agregar"), no siempre guarda el **motivo**, y el
  usuario no tiene una línea de tiempo por documento. La Constitución ya pide dejar rastro (F-6, PR-12);
  esta propuesta lo **eleva a garantía**: cobertura universal + no modificable + motivo + visible.
  Ejemplo: "¿quién anuló esta reserva y por qué?" tiene que responderse solo, sin pedirle a un
  programador que mire la base. En los ERPs esto es el "Change Log" (BC), el "Audit Trail" (NetSuite,
  inviolable) o el historial de cada documento en Odoo (el "chatter").
- **Prioridad: ALTA** (sin rastro confiable e inviolable no hay defensa ante un reclamo ni ante una inspección).

### E-6 · Cada documento tiene un número único, sin saltos y que nunca se reusa
Toda factura, nota, cobro y comprobante lleva un **número propio, correlativo, sin agujeros**. Si un
documento se anula, **se queda con su número** (no se recicla, no se "corre" el resto). El número que ve
el usuario es humano y estable.

- **Por qué.** El contador de numeración existe (verificado: `BusinessSequence`), pero **no hay una regla
  transversal firmada** de "sin saltos / sin reúso / el anulado conserva su número". Ejemplo: si al anular
  una factura el número se libera y se lo queda otra, ante AFIP quedan dos documentos con el mismo número
  o un salto inexplicable en la secuencia. Todos los ERPs garantizan numeración correlativa sin huecos
  para documentos legales (es requisito fiscal, no una comodidad). Del lado AFIP ya lo maneja el organismo;
  esta regla cubre **también los documentos internos** (recibos, órdenes, vouchers).
- **Prioridad: ALTA** (requisito fiscal y de trazabilidad; un salto de numeración es una observación de inspección).

### E-7 · El sistema le entrega al contador un paquete cerrado y que no cambia
El sistema tiene que poder **exportar, por período, todo lo que el contador necesita** (ventas, compras,
IVA, cobros, pagos) en un paquete que, una vez cerrado el mes (E-1), **da siempre lo mismo**.

- **Por qué.** Hoy no existe ninguna exportación contable (verificado: no hay libro IVA ventas/compras ni
  export). Ejemplo cotidiano: cada mes el contador pide "pasame las ventas y las compras"; si eso se arma
  a mano en Excel, hay errores y se pierde tiempo, y peor: si los números cambian después de exportados,
  ya no coinciden con lo presentado. Todos los ERPs exportan a la contabilidad (BC y NetSuite tienen
  contabilidad adentro; Odoo y SAP exportan asientos y libros de IVA). MagnaTravel es "de caja" (no lleva
  contabilidad completa, verificado), así que la salida mínima vendible es **el paquete para el contador**.
  *(El detalle fiscal exacto de cada libro lo define el contador / los agentes fiscales; acá la regla es
  operativa: que el paquete exista, sea por período y sea inmutable.)*
- **Prioridad: ALTA** (sin esto, toda agencia con contador —o sea, todas— tiene que reprocesar a mano).

---

## PRIORIDAD MEDIA — madurez del producto

### E-8 · Cuatro ojos: lo excepcional lo autoriza un segundo
Las excepciones (un descuento por encima del tope, anular algo ya cobrado, un reembolso grande) **las pide
uno y las aprueba otro**, con umbral configurable (ej. "descuentos de más del 10% los aprueba el dueño").

- **Por qué.** El armazón de aprobaciones existe pero está **casi todo muerto** (verificado: hay tipos de
  aprobación definidos, pero solo se usan unos pocos; el de descuento nunca se dispara). Ejemplo: un
  vendedor quiere hacer un 30% de descuento; en vez de poder solo, queda "pendiente de aprobación" hasta
  que el dueño lo autoriza. Es el "approval workflow" estándar de los ERPs (BC "Approval Workflows",
  NetSuite "Approval Routing", Odoo "Studio approvals"). Va de la mano de E-4 (permisos).
- **Prioridad: MEDIA** (importante al vender a equipos; hoy con un solo usuario no muerde).

### E-9 · Un documento emitido no se edita: se corrige con otro documento enganchado
La Constitución ya dice que el **libro de caja** es inmutable (F-6). Esta propuesta lo extiende a **todos**
los documentos emitidos (factura, recibo, voucher, nota): una vez emitido no se edita; si hay un error, se
emite un **documento de corrección enganchado al original**, y la cadena original→corrección se ve entera.

- **Por qué.** F-6 habla del libro de caja; no hay una regla transversal que diga "ningún documento emitido
  se edita destructivamente". Ejemplo: se facturó con un dato mal; en vez de "editar la factura" (que
  fiscalmente no existe), se emite una nota que la corrige y las dos quedan visibles y vinculadas. Es el
  principio de "no destructive edits" de todo ERP fiscal (Odoo bloquea la edición de una factura validada;
  NetSuite y BC no dejan tocar un documento posteado). Refuerza F-4 (snapshot) y F-8 (factura con vida propia).
- **Prioridad: MEDIA** (mayormente ya se respeta en la práctica; falta como regla escrita y universal).

### E-10 · Cada movimiento entre monedas guarda el cambio que usó, y ese cambio no se toca
Cuando un cliente paga en pesos una venta en dólares (o al revés), el sistema **graba el tipo de cambio que
usó en ese momento** y lo deja **congelado** para siempre en ese movimiento.

- **Por qué.** El código ya imputa entre monedas (existe el monto imputado), pero la regla transversal de
  "guardá y congelá el TC de cada movimiento" no está escrita. Ejemplo: hoy el cliente paga US$100 a $1.000;
  si mañana el dólar sube y alguien recalcula, ese cobro **no puede cambiar de valor**: se hizo a $1.000 y
  punto. La Constitución ya prohíbe *mostrar* "diferencia de cambio" al usuario (P-3), y el *tratamiento
  fiscal* del cruce es el vacío V-4; esta regla es la parte **operativa** distinta: que el número quede
  clavado. Todos los ERPs multimoneda registran el "exchange rate" por transacción y no lo recalculan hacia
  atrás. *(De qué fuente sale el TC —BNA— y su impacto fiscal: eso es fiscal/V-4, no esta regla.)*
- **Prioridad: MEDIA** (si no se congela, los cobros viejos "cambian de valor" solos y la caja no cierra).

### E-11 · Toda plata que entra tiene un camino de salida definido (nada sin salida)
Por diseño, **cualquier cobro tiene que poder devolverse o acreditarse** por un camino que exista en alguna
pantalla. Ninguna operación de plata puede quedar "atrapada" sin forma de deshacerse o reembolsarse.

- **Por qué.** Las auditorías del proyecto ya encontraron callejones sin salida reales (ej. un reembolso al
  operador que el backend sabe deshacer pero ninguna pantalla lo llama). Ejemplo: se cobró de más y no hay
  botón en ningún lado para devolverlo; la plata queda "colgada". Es el principio de que **todo débito tiene
  su contra-crédito** posible. La Constitución tiene F-6 (tachar, no borrar) y F-11 (saldo a favor), pero no
  una regla de **completitud**: "por cada entrada, una salida posible". Ata con la "caza de callejones sin
  salida" que ya está en la memoria del proyecto.
- **Prioridad: MEDIA** (evita que la plata quede trabada y que el usuario tenga que pedir ayuda técnica).

### E-12 · Doble click no cobra dos veces (protección contra repetidos)
Si el usuario aprieta dos veces "Cobrar" o "Facturar", o se corta internet y reintenta, el sistema **no crea
dos cobros ni dos facturas**: reconoce que es el mismo pedido y lo hace una sola vez.

- **Por qué.** Para AFIP ya hay protección de repetidos (verificado: existe una clave de idempotencia para
  ARCA). Pero para las acciones internas de plata disparadas por el usuario, la regla no está escrita.
  Ejemplo cotidiano: el vendedor hace doble click porque "no cargó", y quedan dos recibos por el mismo cobro.
  Los ERPs protegen las acciones de plata con idempotencia / bloqueo de doble envío. La Constitución tiene
  F-5 (atómico) y T-12 (idempotencia con proveedores externos); esto lo lleva a **las acciones de plata del
  propio usuario**.
- **Prioridad: MEDIA** (con un usuario apurado pasa seguido; ensucia la caja).

### E-13 · A un cliente/operador con historia no se lo borra: se lo desactiva
Un cliente, operador o proveedor que **ya tiene reservas, cobros o facturas** no se puede eliminar. Se
**desactiva** (deja de aparecer para elegir en algo nuevo) pero su historia queda intacta.

- **Por qué.** No verifiqué una regla que impida borrar un maestro con historia. Ejemplo: alguien borra un
  operador viejo "para limpiar" y se lleva puestas las facturas y deudas que le colgaban. Todos los ERPs
  usan "activo/inactivo" en vez de borrar maestros (BC "Blocked", NetSuite "Inactive", Odoo "Archived"). Es
  primo hermano de E-3 (fusión) y del vacío V-3 (reasignar cliente).
- **Prioridad: MEDIA** (previene pérdida de historia; hoy con pocos datos el riesgo es bajo).

### E-14 · Copia de seguridad y cuánto tiempo se guarda cada cosa
Tiene que haber una regla escrita de **con qué frecuencia se respalda la base**, cómo se prueba que el
respaldo sirve, y **cuántos años se guardan los documentos fiscales** (en Argentina hay plazos legales).

- **Por qué.** Es en parte tema de infraestructura, pero como **regla de producto** no está firmada.
  Ejemplo: se rompe el servidor y la última copia buena es de hace un mes → se pierde un mes de ventas y
  cobros. Y del lado fiscal, los comprobantes hay que conservarlos por años; borrar por "limpiar" puede ser
  ilegal. Los ERPs en la nube dan respaldo gestionado + políticas de retención; acá conviene dejarlo escrito
  aunque la ejecución la haga la parte de infraestructura.
- **Prioridad: MEDIA** (riesgo de pérdida de datos = ALTA si no existe ningún respaldo; MEDIA si infraestructura ya respalda pero no está escrito).

### E-15 · Fotografía del día: arqueo de caja y posición al cierre
Al cerrar el día o el mes, el sistema tiene que poder mostrar **cuánta plata debería haber en cada caja/banco
y en cada moneda**, para compararla con lo que hay de verdad (el arqueo).

- **Por qué.** El libro de caja existe (verificado), pero no verifiqué un **arqueo** formal (saldo esperado
  por caja y moneda a una fecha) para contrastar contra el efectivo real. Ejemplo cotidiano: a fin del día
  la cajera cuenta la plata y la compara con lo que dice el sistema; si no hay pantalla que diga "deberías
  tener $X en pesos y US$Y", el descuadre no se detecta. Todos los ERPs con caja traen arqueo / cash count
  (Odoo POS "cash control", NetSuite/BC reconciliación de caja/banco). Ata con E-2 (posición) pero mirando
  la caja, no las deudas.
- **Prioridad: MEDIA** (con una sola persona y poco volumen se lleva de memoria; al crecer es indispensable).

---

## PRIORIDAD BAJA — pulido y madurez lejana

### E-16 · Vencimiento y condición de pago en la factura
Cada factura/venta puede llevar una **fecha de vencimiento** y una condición ("contado", "a 30 días") que
alimente el aging (E-2) y el estado de cuenta.

- **Por qué.** La factura hoy no tiene vencimiento (verificado en el mapa del proyecto). Con "prepago puro"
  (F-7, el cliente paga el 100% antes de viajar) el vencimiento casi siempre es "ya", así que **hoy muerde
  poco**. Pero si alguna vez se vende con financiación o a empresas con crédito, hace falta. Es estándar en
  todo ERP ("Payment Terms").
- **Prioridad: BAJA** (choca con prepago puro; solo relevante si el negocio suma ventas a crédito).

### E-17 · La venta se reconoce como ganada cuando el viaje ocurre (no cuando se cobra)
Contablemente, la plata cobrada por adelantado es **un adelanto**, y la venta recién se "gana" cuando el
cliente **viaja**. El sistema debería poder distinguir "cobrado pero todavía no viajado" de "ya ganado".

- **Por qué.** Hoy el sistema es "de caja" (verificado: la ganancia se cuenta contra lo cobrado, no contra
  el viaje). El estándar contable (IFRS 15) y todos los ERPs con contabilidad reconocen el ingreso de
  turismo **en la fecha de salida**, y tratan el anticipo como ingreso diferido. Esto es **materia del
  contador y de los agentes contables**, no de este lente; lo dejo anotado para que no se olvide, pero la
  decisión y el detalle fiscal no son míos.
- **Prioridad: BAJA** (madurez contable; defiere al contador; no bloquea vender v1).

### E-18 · Reportes y comprobantes exportables en formato estándar
Los reportes clave (posición, aging, arqueo, ventas del mes) y los comprobantes deberían **exportarse a
Excel/PDF** de forma uniforme, con la misma cara y el mismo formato es-AR (P-2).

- **Por qué.** Es comodidad, no plata. Ejemplo: el dueño quiere mandarle el listado de deudores al contador
  por mail. Todos los ERPs exportan a Excel/PDF de fábrica. Pulido.
- **Prioridad: BAJA** (comodidad; no bloquea nada).

---

## Notas (no son propuestas numeradas)

- **N-1 · Concurrencia multi-usuario (ya es vacío V-1).** No la propongo como regla nueva porque la
  Constitución ya la tiene como vacío conocido **y** el código ya trae control optimista de Postgres
  (`xmin`), o sea que **no hay pisadas silenciosas** a nivel base. Lo único que sugiero, cuando se aborde
  V-1, es darle forma de regla de negocio en criollo: *"si dos personas tocan la misma reserva, gana el
  primero que guarda; al segundo se le avisa 'alguien más cambió esto, mirá de nuevo' y no se pierde lo que
  cargó"* (esto último engancha con P-7 de la Constitución).

- **N-2 · Factura ↔ servicio (ya es vacío V-2).** El "facturado total se mide por monto y no por servicio"
  ya está anotado como V-2. Varias de estas propuestas (E-2 aging, E-7 export) quedarían **más finas** si
  existiera ese vínculo, pero no lo re-propongo: es decisión abierta de la Constitución.

---

## Cómo priorizaría el arranque (sugerencia, Gastón decide)

Si hubiera que ordenar (respetando "no estimar tiempos", solo el **orden**):

1. **E-1 (cierre de mes)** y **E-5 (rastro inviolable)** — son los que protegen la verdad fiscal e histórica.
2. **E-3 (sin duplicados/merge)** y **E-6 (numeración sin saltos)** — protegen que los saldos y los documentos sean confiables.
3. **E-2 (aging)** y **E-15 (arqueo)** — para que la plata se cobre y la caja cierre.
4. **E-4 + E-8 (permisos y cuatro ojos)** — el bloque que **habilita vender el producto a agencias con empleados**.
5. **E-7 (export al contador)** — para que toda agencia con contador lo pueda usar sin reprocesar a mano.
6. El resto (E-9 a E-18) — madurez y pulido, a medida que aparezcan.

---

*Recordá: TODO lo de arriba son propuestas sin firmar. Ninguna se construye hasta que vos la aprobás, una
por una, y pasa a la Constitución con su fuente y fecha. Lo que rechaces se descarta o queda como vacío
conocido. Lo fiscal/contable puntual (E-7, E-16, E-17 y el cruce de monedas de E-10) lo valida el contador
o los agentes fiscales; mi lente es el ciclo de vida y la mecánica operativa, no la autoridad tributaria.*
