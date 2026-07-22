# Huecos del RUBRO en la Constitución del producto — propuestas sin firmar

**Fecha:** 2026-07-22 · **Autor:** experto de dominio agencia de viajes · **Estado:** PROPUESTAS. Nada de acá está firmado. Gastón decide regla por regla.

> **Qué es esto.** La Constitución v1 (`2026-07-22-constitucion-producto-v1.md`) cubre muy bien
> pantallas, plata/fiscal, técnica y proceso. Lo que le falta son las reglas del **oficio de vender
> viajes**: documentos del pasajero, fechas límite del operador, vouchers antes del viaje, el viaje en
> curso, la post-venta, las vigencias de tarifa, cuándo se gana la comisión de verdad, y cómo trabajan
> los mayoristas argentinos. Este documento propone esas reglas para que Gastón las apruebe, las cambie
> o las descarte.
>
> **Cómo leer cada regla:** enunciado en criollo · por qué (ejemplo del mostrador) · prioridad.
> **ALTA** = si falta, puede costar plata o dejar un pasajero varado. **MEDIA** = madurez del producto.
> **BAJA** = pulido.
>
> **Aclaraciones honestas:**
> - **No invento obligaciones fiscales.** Lo fiscal (facturación del operador, IVA, notas) lo firma
>   `travel-agency-accountant-argentina` / `arca-tax-expert-argentina`. Acá marco solo el **hecho de
>   negocio**; donde hay borde fiscal lo señalo con "→ validar con contador".
> - **Varias de estas cosas ya tienen un campo en el código** (según mi memoria de dominio del
>   2026-06-17, sin re-verificar hoy): `Passenger.PassportExpiry`, `FlightSegment.TicketingDeadline`,
>   `HotelBooking.OperatorPaymentDeadline`, `CommissionAccrual`. Cuando es así lo digo: **existe el dato,
>   falta la REGLA transversal** que lo convierta en un freno o una verdad, no solo en una alarma suelta.
> - No repito nada que la Constitución ya tenga (cancelar≠anular, multas, saldo a favor, snapshot fiscal,
>   prepago puro, etc.) ni los vacíos ya listados (V-1 a V-7).

---

## PRIORIDAD ALTA — si falta, se pierde plata o queda un pasajero varado

**A-1 · La fecha de emisión del operador (time limit) es una verdad que vence, no una alarma.**
Cuando pasa el time limit sin emitir/pagar, el operador o la aerolínea **dan de baja el cupo o el
precio** por su cuenta. La reserva puede estar "muerta" aunque el sistema la muestre viva.
*Por qué (mostrador):* vendés un aéreo con time limit el viernes 18hs; el lunes el cliente viene a
pagar y el localizador ya no existe: la aerolínea lo canceló y ahora la tarifa subió $200.000.
*Propuesta de regla:* pasado el time limit sin emitir, la reserva entra en un estado de riesgo visible
("time limit vencido — puede haberse caído en el operador") y no se la trata como firme hasta
reconfirmar con el operador. **Existe el campo `TicketingDeadline` con alarma; falta la regla de que el
vencimiento cambia la realidad de la reserva.** **ALTA.**

**A-2 · No se confirma un viaje internacional sin chequear la vigencia del pasaporte contra la fecha de regreso.**
Muchísimos destinos exigen pasaporte válido **6 meses después** del regreso; si no, la aerolínea niega
el embarque en el mostrador del aeropuerto. *Por qué:* familia a Cancún, pasaporte del nene vence en 4
meses; migraciones/aerolínea no lo dejan subir y el viaje se cae el mismo día. *Propuesta de regla:*
antes de dar por lista una reserva internacional, el sistema compara `vencimiento de pasaporte` vs
`fecha de regreso + margen configurable (default 6 meses)` y **avisa/frena**. El margen es configurable
por destino/operador. **Existe `PassportExpiry` con alarma de vigencia; falta la regla que la ate a la
fecha de viaje y al margen del destino.** **ALTA.**

**A-3 · Los datos que la aerolínea/operador exige para viajar son un checklist obligatorio y el nombre va EXACTO al pasaporte.**
Cada producto pide un mínimo distinto (nombre tal cual pasaporte, fecha de nacimiento, tipo y número de
documento, nacionalidad; a veces visa/APIS). Un nombre mal tipeado = reemisión paga o embarque negado.
*Por qué:* cargaste "Maria" sin acento y el pasaporte dice "María"; algunas aerolíneas cobran la
corrección como reemisión, otras directamente no embarcan. *Propuesta de regla:* una reserva no llega a
"lista para viajar" sin el checklist de datos del pasajero **completo según el tipo de producto**, y el
nombre se carga como figura en el documento de viaje. **ALTA.**

**A-4 · No se viaja sin la confirmación/voucher del operador guardada (localizador, PNR, nro de confirmación de hotel).**
La confirmación del operador es la prueba de que el servicio existe. Sin ella, el pasajero llega al hotel
y no tiene reserva. *Por qué:* el cliente llega a Bariloche a las 23hs, el hotel no lo encuentra porque
nunca guardaste el número de confirmación que mandó el mayorista. *Propuesta de regla:* una reserva no
alcanza el estado "lista para viajar" sin el identificador de confirmación del operador cargado por cada
servicio (PNR/localizador aéreo, nro de confirmación de hotel, voucher de traslado). Esto es distinto del
voucher que la agencia le da al cliente. **ALTA.**

**A-5 · "En firme" (confirmado por el operador) no es lo mismo que "on request" (a la espera), aunque el cliente ya haya pagado.**
Mucho producto mayorista se vende "a confirmar": el operador todavía no aseguró el cupo. Cobrarle al
cliente como firme algo que el operador aún no confirmó es venderle humo. *Por qué:* cobrás el hotel
completo, pero estaba "on request"; a los dos días el operador contesta "sin disponibilidad" y tenés que
devolver todo y conseguir otra cosa más cara. *Propuesta de regla:* el sistema distingue servicio **en
firme** vs **on request**; una reserva con servicios on request no se muestra como confirmada, y el gate
de cobro (F-10) contempla el aviso de que hay algo sin confirmar. **ALTA.**

**A-6 · La penalidad de cancelación del operador es escalonada por fecha, no un número fijo.**
Cuanto más cerca del viaje se cancela, más alta la multa (ej. 30 días antes 10%, 7 días antes 50%, no-show
100%). *Por qué:* el cliente pregunta "¿cuánto pierdo si cancelo hoy?"; la respuesta depende de cuántos
días faltan para el viaje según el cuadro del operador, no de un porcentaje único. *Propuesta de regla:*
la multa por anulación se calcula contra el **cuadro de penalidades del operador vigente a la fecha de la
anulación**, configurable por operador/producto; el sistema no asume una multa fija. Se apoya en F-12
(traslado 1:1) pero agrega que el **monto** depende de la fecha. → borde fiscal en cómo se documenta:
validar con contador. **ALTA.**

**A-7 · Cambiar la fecha / reprogramar / reemitir es una operación propia, NO cancelar y volver a cargar.**
Es el evento post-venta más común. Hoy solo existe cancelar. Rehacer la reserva pierde la historia, el
localizador, la comisión y la trazabilidad de la plata. *Por qué:* el cliente adelanta el viaje una semana;
la aerolínea cobra diferencia de tarifa + penalidad de cambio, pero es el MISMO ticket. Si cancelás y
recreás, perdés el rastro de qué se pagó y de la comisión ya devengada. *Propuesta de regla:* existe una
operación "cambio de fecha/reemisión" que conserva el legajo, encadena el servicio viejo con el nuevo,
soporta una diferencia de tarifa + cargo de cambio, y mantiene la comisión y el rastro de plata. → borde
fiscal (nota de débito por la diferencia): validar con contador. **ALTA.**

**A-8 · El no-show es un desenlace propio, distinto de anular.**
El pasajero no se presenta y no avisó. No es "cancelar" (no abona el total voluntariamente) ni "anular"
(no hay pedido de dejar sin efecto): casi siempre es **pérdida total, sin reintegro**, y a veces gatilla
cobros extra del operador. *Por qué:* el cliente no apareció en el aeropuerto; el hotel cobra la primera
noche igual y el aéreo se pierde entero. Tratarlo como "anular con saldo a favor" le devolvería plata que
no corresponde. *Propuesta de regla:* el no-show es un estado terminal con su propio tratamiento de plata
(default: sin reintegro, con posibles cargos del operador), configurable por producto. → borde fiscal:
validar con contador. **ALTA.**

---

## PRIORIDAD MEDIA — madurez del producto

**A-9 · El pasajero es una ficha reutilizable del cliente, no se re-tipea en cada reserva.**
Hoy el pasajero cuelga de la reserva y se reescribe nombre + documento + pasaporte cada vez. Eso multiplica
el riesgo de A-3 (un typo distinto por reserva). *Por qué:* el cliente que viaja tres veces por año te
obliga a recargar su pasaporte cada vez, y una de esas veces lo tipeás mal. *Propuesta de regla:* la
identidad y los documentos del pasajero viven a nivel cliente/persona, con historial, y se reutilizan al
armar una reserva. **MEDIA.**

**A-10 · Un reclamo de reembolso sabe CONTRA QUIÉN se reclama: aerolínea, operador o seguro.**
En post-venta, quién debe la plata cambia todo: un reintegro de aerolínea tarda meses y va por otro canal
que un reintegro del operador mayorista, y un siniestro lo paga el seguro. *Por qué:* al cliente le
cancelaron el vuelo la aerolínea; ese reintegro no lo maneja tu operador, lo reclama la aerolínea y puede
tardar 6 meses. Si lo cargás como "el operador debe", nunca cierra la cuenta. *Propuesta de regla:* todo
reclamo/reembolso de post-venta registra el **responsable** (aerolínea / operador / seguro / agencia),
porque el plazo y la recuperabilidad dependen de eso. Se conecta con el circuito de reembolso del operador
ya existente, agregando el caso "no es el operador". **MEDIA.**

**A-11 · La comisión del vendedor se gana de verdad cuando la venta es firme, está cobrada y sobrevivió al riesgo; se pierde o se reduce si el viaje se cae.**
No se gana en la cotización ni en la seña. Y si el operador después cancela, hay no-show o se anula, la
comisión ya "ganada" tiene que retroceder. *Por qué:* le liquidás la comisión al vendedor por una venta
de marzo; en febrero el operador canceló el hotel y hubo que devolver todo. Esa comisión no era real.
*Propuesta de regla:* la comisión devenga solo sobre venta firme y cobrada, y **se revierte/ajusta** si el
viaje no se concreta (cancelación del operador, no-show, anulación), con tope cero (nunca negativa contra el
vendedor). **Existe `CommissionAccrual` (devenga con saldo ≤ 0, tope cero); falta la regla de reversa
cuando el viaje se cae después de devengada.** → borde contable: validar con contador. **MEDIA.**

**A-12 · Cada operador mayorista tiene un modelo de facturación que define quién le factura al pasajero y cómo se reconoce la comisión.**
En Argentina conviven dos esquemas: (a) el operador te factura a vos y vos le facturás al pasajero
(reseller); (b) el operador le factura al pasajero y vos cobrás comisión (intermediario). Cambia qué
comprobante emitís y sobre qué base ganás. *Por qué:* con un operador ganás por markup sobre lo que
comprás; con otro ganás una comisión que te liquida el propio operador. Si el sistema asume uno solo, con
la mitad de tus proveedores factura mal. *Propuesta de regla:* el modelo de facturación es **configurable
por operador** (intermediario vs reseller) y determina el circuito de comprobantes y de comisión. →
claramente fiscal: lo firma `travel-agency-accountant-argentina`. **MEDIA (alta si se suma un operador que
trabaja al revés del default).**

**A-13 · Una cotización tiene fecha de vencimiento; el precio no vale para siempre.**
El operador te da un precio "sujeto a disponibilidad y a confirmar al reservar". *Por qué:* cotizaste un
paquete hace tres semanas; el cliente vuelve decidido y el precio subió $150.000 o cambió el tipo de
cambio. Vender a la cotización vieja te come el margen. *Propuesta de regla:* toda cotización lleva una
**validez** y, pasada, exige reconfirmar precio con el operador antes de convertirla en reserva firme.
**MEDIA.**

**A-14 · Las tarifas tienen vigencia por fecha de viaje (temporada); usar una tarifa fuera de su ventana está mal.**
El mismo hotel cuesta distinto en temporada alta, baja y feriados largos. Una tarifa guardada está atada a
un rango de fechas de viaje. *Por qué:* aplicás la tarifa de mayo a un viaje de enero (temporada alta
Costa) y facturás $80.000 de menos por habitación. *Propuesta de regla:* las tarifas cargan una **ventana
de vigencia**; el sistema no deja (o avisa) usar una tarifa para una fecha de viaje fuera de su rango.
**MEDIA.**

**A-15 · Antes del viaje, el cliente recibe un paquete de documentos consolidado, no vouchers sueltos.**
Itinerario completo del legajo + todos los vouchers + datos de contacto de emergencia, en un solo entregable.
*Por qué:* el cliente viaja con vouchers sueltos de hotel, traslado y excursión, se olvida el del traslado,
y llega a Ezeiza sin saber quién lo pasa a buscar. *Propuesta de regla:* existe un "paquete de viaje" del
legajo (itinerario + vouchers + contactos), y ese es el entregable previo al viaje, no el voucher por
servicio suelto. **MEDIA.**

**A-16 · Mientras el pasajero viaja, tiene que haber un canal de contacto/emergencia de la agencia.**
Durante el viaje pasan cosas (vuelo cancelado, hotel que no lo encuentra, pérdida de conexión) y la agencia
es el primer llamado. *Por qué:* domingo a la noche, al cliente le cancelaron el vuelo de conexión en San
Pablo y necesita que alguien de la agencia lo reubique; si no hay un contacto y no está guardado a qué
teléfono llamar, queda tirado. *Propuesta de regla:* el legajo lleva un contacto de emergencia (de ida y
de vuelta: cómo la agencia contacta al pasajero y cómo el pasajero contacta a la agencia 24hs), como parte
del deliverable de viaje. **MEDIA.**

**A-17 · Requisitos especiales por destino/pasajero se marcan antes del viaje: visa, vacunas, autorización de menores.**
Cada destino tiene sus reglas (visa para EE.UU., fiebre amarilla para algunos países, permiso notarial para
un menor que viaja sin ambos padres). *Por qué:* el menor viaja con la mamá a Brasil sin el papá; sin la
autorización notarial, migraciones argentina no lo deja salir del país. *Propuesta de regla:* checklist de
**requisitos especiales configurable por destino/tipo de pasajero** (visa, vacuna, autorización de menor),
que debe estar resuelto antes de "listo para viajar". **MEDIA.**

---

## PRIORIDAD BAJA — pulido / producto para vender a otras agencias

**A-18 · El seguro de asistencia al viajero es un producto vendible con vigencia propia, y a veces es obligatorio del destino.**
Europa (Schengen) exige seguro con cobertura mínima; sin él, niegan el ingreso. Hoy el seguro aparece solo
como concepto no-reintegrable suelto. *Por qué:* vendés Europa sin asistencia y el cliente no puede
demostrar cobertura en migraciones de España. *Propuesta de regla:* la asistencia al viajero es un producto
con fechas de cobertura propias, marcable como **obligatorio según destino**. **BAJA (sube a MEDIA si se
vende Europa seguido).**

**A-19 · El producto multi-agencia necesita cosas que una unipersonal no: separación de datos por agencia, varios vendedores con atribución/split, permisos y metas por vendedor, y marca de la agencia en vouchers/facturas.**
Una agencia compradora tiene varios vendedores, no quiere que otra agencia vea sus clientes, y sus
comprobantes/vouchers llevan SU logo y SUS datos. *Por qué:* le vendés el sistema a una agencia de 4
vendedores; hoy la comisión se atribuye por un solo responsable y no hay split entre dos que cerraron la
venta juntos, ni aislamiento de datos entre agencias. *Propuesta de regla (transversal de producto):*
aislamiento de datos por agencia (multi-tenant), atribución de venta/comisión por vendedor con split
posible, permisos y metas por vendedor, y branding por agencia en documentos. Es una **dirección de
arquitectura** para el "mañana producto", no una obra inmediata. **BAJA (para la etapa producto).**

**A-20 · La agencia guarda el rastro del acuerdo comercial con cada operador (markup/comisión pactada, plazos de pago, tope de crédito).**
La relación con el mayorista tiene condiciones: cuánto te comisiona o qué markup te deja, a cuántos días le
pagás, si te da cuenta corriente y hasta qué monto. *Por qué:* con un operador tenés 30 días para pagar y
$5.000.000 de crédito; con otro pagás contra reserva. Si el sistema no lo sabe, cargás mal el margen y no
sabés cuánto podés reservar antes de que te corten. *Propuesta de regla:* cada operador guarda sus
condiciones comerciales (comisión/markup pactado, plazo de pago, tope de crédito), y esos parámetros
alimentan el margen y los avisos de deuda al operador. **BAJA.**

---

## Cómo se conecta con lo que ya está

- **A-1, A-4, A-5** endurecen el estado "lista para viajar" que hoy no tiene definición transversal firmada
  (se relaciona con el "Estado Confirmada" que quedó con preguntas abiertas a Gastón, 2026-06-07).
- **A-6, A-7, A-8, A-11** tocan el circuito de anulaciones/multas/comisión ya construido: no lo reemplazan,
  le agregan los casos del rubro que hoy no distingue (multa escalonada, cambio de fecha, no-show, reversa
  de comisión).
- **A-2, A-3, A-9, A-17** son la capa de "documentos y datos del pasajero" que la Constitución no toca.
- **A-10, A-15, A-16** son la post-venta y el viaje en curso, hoy ausentes como reglas.
- **A-12, A-13, A-14, A-20** son la relación con el operador mayorista y las vigencias comerciales.

## Qué necesita confirmación de Gastón (no lo decido yo)

1. ¿El margen de vigencia de pasaporte es 6 meses por default y configurable por destino, o lo fijamos fijo? (A-2)
2. ¿"On request" es un estado que queremos modelar ya, o hoy todo lo que vendés ya viene confirmado por el operador? (A-5)
3. ¿El no-show se trata siempre como pérdida total, o hay operadores que reintegran algo? (A-8)
4. ¿La comisión revierte automática cuando el viaje se cae, o preferís que sea un ajuste manual con aviso? (A-11)
5. ¿Trabajás hoy con algún operador en modelo "intermediario" (te factura al pasajero y te comisiona), o todos son reseller? Esto define A-12 y es fiscal.
6. Prioridad real entre A-7 (cambio de fecha) y A-9 (ficha de pasajero reutilizable): las dos las marqué como las más pedidas por el mostrador; ¿cuál primero?

---

*Todo este documento es una propuesta. Cada regla que Gastón apruebe pasa a la Constitución con su fuente y
fecha; las fiscales pasan por `travel-agency-accountant-argentina` antes de firmarse.*
