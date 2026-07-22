# Constitución del producto MagnaTravel — v1

**Fecha:** 2026-07-22 · **Estado:** ✅ **FIRMADA COMPLETA por Gastón el
2026-07-22** ("Todo lo que me dijiste en esos estándares está perfecto, tomo
a todos"): las 62 reglas de este documento + las 18 propuestas del experto
ERP (E-1..E-18, `2026-07-22-huecos-expertos-erp.md`) + las 20 del experto del
rubro (A-1..A-20, `2026-07-22-huecos-expertos-rubro.md`). Las E-* y A-*
quedan incorporadas a la constitución con sus prefijos.

> **⚠ VIGENTE vs OBJETIVO — para no mentir.** Las reglas P/F/T/PR describen
> cómo el sistema YA funciona (o debe funcionar desde hoy): se controlan en
> cada obra desde ahora. Las E-* y A-* son reglas OBJETIVO firmadas: el
> sistema HOY no las cumple todas — cada una necesita su obra. El orden de
> esas obras se decide con Gastón (prioridad natural: las ALTA primero).
> Mientras una regla objetivo no esté construida, ninguna obra nueva puede
> CONTRADECIRLA (p.ej. no diseñar algo que impida el cierre de mes E-1).

> **Qué es esto.** La fuente única de las reglas transversales del producto: las que valen
> en TODAS las pantallas, en TODA la plata, en TODO el código y en TODA obra, sin importar
> el tema puntual. Cada regla salió de una decisión real ya tomada por Gastón (dueño del
> producto, no programador); al lado va la fuente y la fecha. **Acá NO hay reglas inventadas.**
>
> **Lo que no está acá se le pregunta a Gastón.** Si aparece un caso que ninguna regla cubre,
> se le pregunta a él con opciones + una recomendación, y su respuesta se convierte en una
> regla nueva de este documento. Nadie decide solo un vacío.
>
> **Cómo se usa (el "Spec-Driven" del proyecto):**
> 1. Todo encargo a un agente incluye, en el brief, las reglas que aplican por número (ej.
>    "respetá P-3, F-2 y T-1").
> 2. Todo reviewer verifica el trabajo contra los números de regla, no contra su criterio.
> 3. Ninguna obra se construye sin una spec previa que cite qué reglas aplica y cómo.
> 4. Los detalles de UNA sola pantalla NO viven acá: viven en su spec (`docs/ux/…`). Acá
>    viven solo las reglas que se repiten en todos lados.
>
> Las reglas marcadas con ⭐ son las que los documentos de incidentes muestran como las más
> violadas o las más caras cuando se rompen. Prioridad de control.
>
> **Cobertura de fuentes.** `docs/ux/guia-ux-gaston.md` fue barrida COMPLETA (las 1811 líneas) el
> 2026-07-22 para extraer toda regla transversal firmada; los detalles de una sola pantalla se
> dejaron en su spec, no acá. También se destilaron las specs firmadas de `docs/ux/`, los ADR de
> `docs/architecture/adr/`, los cierres de `docs/explicaciones/` (julio 2026) y el routing de
> `.claude/CLAUDE.md`.

---

## CAPA 1 — PANTALLAS (lo que ve y toca el usuario)

**P-1 ⭐ · Cancelar ≠ Anular; nunca jerga.** "Cancelar" = el cliente abona el total. "Anular" =
dejar sin efecto (nota de crédito + nota de débito por multa). Prohibido en toda la UfI:
anglicismos, nombres de código, nombres de estado interno (ej. `InManagement`), enums como
número, IDs/GUIDs a la vista, y palabras que un no-programador no entienda. Se habla en términos
del negocio. *(guia-ux-gaston; MEMORY vocabulario cancelar-vs-anular; incidente enum crudo
'InManagement' en T5, 2026-07-20.)*

**P-2 · Plata y fechas en formato argentino.** Miles con punto, decimales con coma, fechas
dd/MM. Nunca formato técnico ni ISO a la vista. *(guia-ux-gaston multimoneda 2026-06-09;
saneamiento formato es-AR.)*

**P-3 ⭐ · Un solo saldo por moneda; las monedas nunca se suman.** Toda pantalla de plata muestra
pesos y dólares por separado (`TOTAL: $ 205.000 · US$ 450`). Jamás un número mezclado, jamás
"moneda base", jamás la palabra "diferencia de cambio". Si hay una sola moneda, se ve como
siempre (un número). *(guia-ux-gaston multimoneda 2026-06-09, tres reglas duras; incidente
moneda fantasma 2026-07-09.)*

**P-4 ⭐ · Los avisos largos de bloqueo van a UNA ventana emergente única.** Cuando el motor
rechaza o pide confirmar una acción que el usuario disparó con un click, y el mensaje es largo,
sale en una ventana emergente, siempre la misma (dos trajes: rojo = freno, ámbar = confirmá).
Todas iguales, para no romper la estética. *(spec 2026-07-22-tratamiento-unico-avisos-bloqueo,
firmada P1/P2/P3.)*

**P-5 · Las fichas de trabajo van EN LÍNEA, nunca en ventana flotante.** Cargar un servicio,
cargar un cobro, editar en la fila: todo se abre dentro de la página, debajo, nunca una ventana
encima. "El modal me parece horrible." La ventana emergente (P-4) es solo para frenos/confirmaciones,
no para trabajar. *(guia-ux-gaston multimoneda P3 2026-06-09; ADR-035; carga de servicios propuesta C.)*

**P-6 · El toast (globito que se va solo) es SOLO para el éxito.** Un error en una ficha en línea
va en un cartel dentro de la ficha (rojo, arriba de los botones), que se queda a la vista mientras
el usuario corrige. Un error nunca se muestra en un toast que desaparece a los 4 segundos.
*(guia-ux-gaston Ronda 2 2026-06-05; T1 y T9 del contrato pantalla-motor.)*

**P-7 · Si falla guardar, no se pierde nada.** La ficha queda abierta con todo lo cargado
intacto + cartel rojo arriba de los botones; se reintenta en el mismo botón. *(guia-ux-gaston
Ronda 2 2026-06-05.)*

**P-8 · Los casos especiales se detectan por código estructurado, nunca por el texto del mensaje.**
El front decide qué botón/camino mostrar mirando un código estable que manda el motor (ej.
`invariantCode`), jamás comparando el texto del mensaje. *(T7/T8 contrato pantalla-motor;
DeshacerMultaEmitidaInline patrón por `invariantCode`.)*

**P-9 · Botón que el motor no permite: se apaga con motivo O se esconde, según el criterio Fase 4.**
Acción ya cumplida o que estructuralmente no aplica todavía → el botón NO aparece. Acción vedada por
candado/permiso → botón gris + motivo al lado, siempre a la vista (nunca tooltip). *(fase4-pantalla-obedece-backend;
regla unificada 2026-06-26; ADR-036 P3=A esconder "Eliminar" con plata viva.)*

**P-10 · La palabra siempre al lado del ícono, a la vista.** Cada ícono de acción muestra su texto
pegado, sin depender de apoyar el mouse. Nada vive solo en un tooltip. *(guia-ux-gaston 2026-06-08.)*

**P-11 ⭐ · Ningún mensaje deja al usuario sin salida ni lo deriva a un rol que él ya es.** Nada de
"hablá con administración" cuando el usuario ES la administración. Cada rechazo dice qué hacer AHORA,
en criollo, y si hay un camino para resolverlo, el botón para ir vive en el mismo lugar donde está
parado. *(guia-ux-gaston 2026-07-08 principios cerrados; T2/T7/T8 contrato pantalla-motor.)*

**P-12 · Las bandejas son listas pasivas; la resolución vive en la ficha.** Cada fila de una bandeja
es un link a la ficha, sin botones de acción propios. La acción que resuelve el caso está en la ficha,
con la única opción que aplica al estado real. *(guia-ux-gaston 2026-07-08 "el paso de multa vive en
la ficha".)*

**P-13 · El texto del mensaje de rechazo se muestra TAL CUAL lo manda el motor.** El front nunca lo
reescribe, ni lo resume, ni le agrega jerga. Muestra los saltos de línea del motor. *(spec avisos-bloqueo
2026-07-22 §3.2; T1 contrato pantalla-motor "no reescribir mensajes que ya están bien".)*

**P-14 · Toda acción destructiva confirma antes.** Borrar, eliminar, anular: siempre un "¿Seguro?"
con el foco arrancando en la opción segura. *(guia-ux-gaston Ronda 2; spec avisos-bloqueo 2026-07-22.)*

**P-15 · Nada de cartelitos aclarativos en los formularios.** Si un campo necesita una leyenda para
entenderse, el diseño está mal. Lo secundario va detrás de "Más detalles". (Excepciones acotadas y
firmadas: la línea fiscal de la multa, y las ayuditas del paso "devoluciones viejas".) *(guia-ux-gaston
2026-06-05, con excepciones 2026-07-14 y 2026-07-17.)*

**P-16 · Un dato no se dice dos veces.** Ningún dato de la reserva aparece a la vez en un chip del
header y en un banner de la ficha. Una condición, una superficie. *(guia-ux-gaston 2026-07-05, respuesta 2B.)*

**P-17 ⭐ · Voz de los avisos (campanita, notificaciones, carteles).** El sujeto es la reserva
(F-2026-xxxx) o la persona, nunca "el sistema/el proceso/el job". Prohibido nombrar la maquinaria
interna ("chequeo nocturno", "revisión manual", "anulación automática"). En los AVISOS no van
términos fiscales ni leyes: "nota de crédito" se dice "devolución", sin "CAE" ni "RG"; el término
fiscal vive SOLO en las pantallas de facturación. Corto, en rioplatense (vos); el detalle técnico
va al log, jamás a la campanita. *(guia-ux-gaston 2026-07-08, las 8 reglas de voz.)*

**P-18 · Los documentos ya emitidos siempre se pueden ver, reimprimir y descargar, aún en solo
lectura.** En estados congelados (En viaje, Finalizada, Perdida, Anulada, Esperando reembolso) se
puede ver/descargar/reimprimir facturas, recibos y vouchers ya emitidos. Lo único que se apaga es
emitir un comprobante nuevo, anularlo o cobrar. *(guia-ux-gaston 2026-06-22, "estados congelados".)*

**P-19 · "Pedí autorización" solo donde el candado se puede destrabar.** El affordance de override
aparece únicamente en Confirmada (candado destrabable con permiso). En estados inmutables por
diseño (En viaje, Finalizada) NO se ofrece destrabar: va el cartel de solo lectura y nada más. No
se muestra un botón que promete algo que no existe. *(guia-ux-gaston 2026-06-22.)*

**P-20 · Aviso suave informa y deja seguir; freno duro bloquea. No se confunden.** Un aviso de
posible error (dólar lejos del oficial, plazo fiscal vencido, total que no cuadra con lo vendido)
avisa pero deja guardar. Un freno duro (regla de negocio incumplida) no deja seguir hasta corregir.
El umbral de un aviso suave lo fija dominio/negocio, nunca el front; hasta que esté fijado, el aviso
queda apagado y no bloquea. *(guia-ux-gaston 2026-07-13, 2026-07-14, 2026-07-15.)*

**P-21 · El sistema SUGIERE, no decide.** Una sugerencia resalta un camino o precarga un valor
(marcado, editable), pero nunca completa montos sola, nunca pisa lo que el usuario ya cargó, y nunca
ejecuta la acción por él. En plata/fiscal la decisión siempre es del usuario. *(guia-ux-gaston
2026-06-15 sugerencia de pasajeros, 2026-07-14 comportamiento con multas.)*

---

## CAPA 2 — PLATA Y FISCAL (la verdad del dinero)

**F-1 ⭐ · Una sola regla por entidad; todas las pantallas obedecen al motor.** Existe UNA función de
dominio que decide "qué es esta reserva / este cobro / esta factura", calculada desde sus partes
(servicios + comprobantes + plata). Ninguna pantalla inventa su propia versión: los chips solo pintan
lo que el motor dice. Si dos superficies muestran cosas distintas, es un bug, no dos verdades.
*(modelo-estados-derivados BORRADOR 2026-07-17, reglas 1 y 8; incidente F-2026-1046 "tres mentiras
en una pantalla".)*

**F-2 ⭐ · La cabecera se calcula desde las líneas y los comprobantes; el estado se recalcula en el
momento, siempre.** Si todos los servicios quedan anulados, la reserva deja sola de decir "Confirmada".
El estado sale correcto en cada cambio, en la misma transacción — NO hay pasada nocturna que lo arregle
después. *(modelo-estados-derivados BORRADOR 2026-07-17, reglas 2 y 9 corregida por Gastón.)*

**F-3 · "Hubo comprobante" no se borra porque después se acreditó.** El eje de facturación distingue
"nunca se facturó" de "se facturó y se devolvió" (factura + NC = "Facturada y devuelta", jamás "Sin
facturar"). *(modelo-estados-derivados BORRADOR 2026-07-17, regla 5; ADR-037 carril derivado.)*

**F-4 · Snapshot fiscal al momento del evento, calculado en el servidor.** La condición fiscal, la
moneda y los montos que definen un comprobante se congelan cuando ocurre el hecho, del lado del servidor,
nunca reconstruidos después ni confiados al front. *(ADR-024 spec fiscal; plan anulaciones 2026-07-17
"snapshot fiscal server-side".)*

**F-5 ⭐ · Toda operación de plata multi-paso es atómica.** Cobrar, anular, emitir notas, mover estado:
todo lo que toca dinero corre en una sola transacción. Plata y estado se actualizan en un solo golpe;
nunca un commit parcial. *(modelo-estados-derivados 5 tandas: "plata y estado en un solo golpe";
incidente reconciliador con commit parcial cazado 2026-07-22.)*

**F-6 · Lo que registra plata deja rastro; nada se borra, se tacha.** Un cobro anulado, un servicio
cancelado, una nota emitida: quedan visibles, tachados, con quién y cuándo. El libro de caja es
inmutable; una reversa es un contra-asiento, no un borrado. *(ADR-022 libro inmutable; ADR-023;
guia-ux-gaston ciclo de vida "queda tachado".)*

**F-7 · Prepago puro: el cliente paga el 100% antes del viaje.** No existe "A liquidar" ni etapa de
liquidación posterior. "En viaje" y "Finalizada" son solo lectura total (no se edita ni con autorización,
no se cobra, no se factura). *(ADR-036, decisiones 1 y 2.)*

**F-8 · La factura es un documento con vida propia, desacoplado del estado.** Se puede facturar en
cualquier estado firme no anulado (Confirmada, En viaje, Finalizada); no hay que "reabrir para facturar".
El estado de facturación es un carril derivado, no un dato suelto. *(ADR-037, decisiones 1 y 2.)*

**F-9 · Cobrable = venta firme + saldo real, no una lista de estados.** Se puede cobrar mientras haya
deuda real sobre una venta firme; la cobrabilidad no se ata al string del estado. *(ADR-033.)*

**F-10 · Gate de pago: cliente DURO, operador AVISO.** Al cliente no se le deja saltear el cobro donde
la regla lo exige (freno duro); en la deuda al operador es un aviso, no un freno. *(ADR-036 gate de pago;
MEMORY adr037/adr036 2026-06-21.)*

**F-11 · Plata anulada → saldo a favor reutilizable.** La plata de una reserva anulada no se pierde: queda
como saldo a favor del cliente, reutilizable. El circuito de anulación (saldo a favor / multa) siempre se
muestra; nunca "Sin movimientos" sobre una reserva sin efecto. *(ADR-036; modelo-estados-derivados regla 4.)*

**F-12 · La multa del operador se traslada 1:1 al cliente, en la moneda de la factura.** El default no se
pregunta nunca; la moneda de la multa manda la moneda de la nota de débito. El "cargo propio de la agencia"
existe como acción secundaria separada. *(guia-ux-gaston 2026-07-08; ADR-044 multas por operador.)*

**F-13 · Un servicio anulado no genera avisos de cobro ni de deuda al operador** (salvo su propia multa);
sus importes se muestran tachados/históricos. "Operador impago" existe solo sobre servicios vivos o multas
reales. *(modelo-estados-derivados BORRADOR 2026-07-17, reglas 6 y 7.)*

**F-14 · Sin permiso de ver costos, no se ven montos de costo/deuda en NINGUNA pantalla.** Tarifario,
cotizaciones, cuenta corriente del proveedor, avisos: los costos y saldos al operador van tapados para
quien no tiene el permiso. *(guia-ux-gaston 2026-06-05, regla general derivada.)*

**F-15 · La validación fiscal/contable final la da un contador; el sistema no es la autoridad.** Los criterios
fiscales sensibles se investigan y se le informan a Gastón, pero el producto no se presenta como autoridad
legal/tributaria. *(CLAUDE.md regla de validación profesional; MEMORY contador ya no se gatea.)*

**F-16 · Toda corrección o excepción sobre algo firme pide permiso elevado + motivo obligatorio + auditoría.**
Destrabar una Confirmada, sacar de viaje, reabrir, deshacer un comprobante, corregir un CUIT ya usado: son
acciones de último recurso, discretas (nunca un botón normal), con permiso elevado, motivo obligatorio y
rastro de quién/cuándo/por qué. *(guia-ux-gaston 2026-06-08 #2, 2026-06-22 "Sacar de viaje", 2026-07-14
"Deshacer multa emitida".)*

---

## CAPA 3 — TÉCNICA (cómo se construye para que lo de arriba sea verdad)

**T-1 ⭐ · Un rechazo de negocio es una excepción tipada con Message en criollo + Code estable.** El motor
lanza el mensaje ya listo para el usuario y un código que no cambia. El controller devuelve `{message, code}`.
El front nunca arma el texto ni interpreta el motivo por el string. *(T1-T9 contrato pantalla-motor;
BusinessInvariantViolationException con invariantCode.)*

**T-2 ⭐ · Los catch anchos jamás ecoan el mensaje de una excepción de framework/DB.** Si el motor no mandó
un mensaje seguro, el usuario ve un fallback limpio, nunca un stack, ni un error de Postgres, ni un texto de
.NET. *(data-exposure gate CLAUDE.md; incidente enum crudo interpolado en InvoiceService.)*

**T-3 · Los guards viven en el motor; la pantalla refleja, nunca decide.** La primera compuerta de todo
camino que escribe es la política del dominio (capacidades por estado, guards de mutación). El front puede
apagar un botón para adelantar el aviso, pero la verdad la impone el backend. *(ADR-036 política de
capacidades; T5/T6/T7 contrato pantalla-motor.)*

**T-4 · El formato es-AR sale de un helper único.** Toda la plata se formatea con el mismo helper
(`formatCurrency` con moneda / `CurrencyDisplayFormat`); nunca formateo suelto por pantalla. Un DTO de plata
siempre lleva su moneda. *(saneamiento es-AR; ADR-023 incidente `FinanceHistoryItemDto` sin Currency →
todo se mostraba como ARS.)*

**T-5 · Los nombres internos jamás aparecen en una respuesta de API ni en un texto de usuario.** Ni clases,
ni tablas, ni campos, ni enums como número, ni IDs técnicos. La respuesta expone solo lo que la pantalla
necesita. *(data-exposure gate CLAUDE.md; P-1.)*

**T-6 · Los tests fijan los textos que ve el usuario.** El texto criollo de un rechazo o de un cartel se
cubre con un test, para que no se rompa sin querer al refactorizar. *(dotnet-fullstack-standards; contrato
pantalla-motor "tests fijan los textos".)*

**T-7 · Un solo escritor por estado derivado.** El único que escribe el estado de la reserva es el motor
existente, siempre post-mutación, en la misma transacción. Nadie más setea el estado a mano. *(modelo-estados-derivados
BORRADOR 2026-07-17 "principio de implementación".)*

**T-8 · Se preserva compatibilidad de datos y de API; las migraciones son alto riesgo.** No se rompe un
contrato ni un dato existente sin migración. El SQL crudo se valida contra PROD antes de pushear (nombres
reales: Reserva→TravelFiles, ReservaId→TravelFileId). *(dotnet-fullstack-standards; MEMORY validar SQL crudo
2026-07-09; lección bootstrapper 42701.)*

**T-9 · Endpoints finos; la regla de negocio vive en el servicio de dominio.** El controller valida el borde
y delega; no esconde reglas en queries sueltas. Transacciones con límites explícitos, sin N+1. *(dotnet-fullstack-standards.)*

**T-10 · Autorización y pertenencia se chequean en el servidor, aunque el front ya valide.** Nunca se saltea
auth; passenger/payment/provider/invoice son datos sensibles y no se loguean. *(dotnet-fullstack-standards seguridad.)*

**T-11 · Sin llaves nuevas (feature flags).** Las features salen directas. No se esconde una obra a medio hacer
detrás de una llave. *(MEMORY basta-de-llaves; ADR-023 "sin feature flags".)*

**T-12 · Toda integración con proveedor externo (ARCA/AFIP) diseña para el fallo.** Timeout, reintento,
idempotencia, mapeo de error a criollo, y un monitor de excepciones para lo que quedó trabado — fuera del
camino de vender. El error crudo del proveedor nunca llega al usuario. Las operaciones asíncronas no atrapan
al usuario esperando: confirman en el acto, lo dejan seguir trabajando y el resultado llega después.
*(architecture-standards integración; guia-ux-gaston "Pendientes con AFIP" 2026-07-08; emisión asíncrona H2
2026-06-24 y anulación multi-factura 2026-07-01.)*

**T-13 · El front no deduce datos derivados a mano; los recibe calculados del motor.** El motivo de un $0,
una fecha de vencimiento, un plazo fiscal, una conversión de moneda, un saldo, el estado de un servicio: los
calcula el backend y el front solo los pinta. Nunca se reconstruye un dato restando montos ni interpretando
strings en la pantalla. *(guia-ux-gaston 2026-07-03, reafirmada 2026-07-14, 2026-07-15, 2026-07-16.)*

---

## CAPA 4 — PROCESO (cómo se decide y se entrega)

**PR-1 ⭐ · El ciclo de toda obra:** spec que cita las reglas que aplica → firma de Gastón si toca pantalla,
plata o negocio → un solo agente mutando código a la vez → reviews obligatorias → E2E real con la app
corriendo → CI verde → deploy → checklist de prueba para Gastón → cierre con memoria + doc. *(CLAUDE.md
routing; MEMORY reglas operativas.)*

**PR-2 ⭐ · Ninguna decisión de pantalla, plata, fiscal o negocio se toma sola: se le pregunta a Gastón
ANTES, con opciones + una recomendación única.** El agente propone el diseño completo contado en criollo;
él valida. No cuestionarios largos: una recomendación clara. *(MEMORY auto-mode-pero-preguntar; guiar-no-cuestionarios;
gate UX CLAUDE.md.)*

**PR-3 ⭐ · Verificado de verdad vs no verificado, con esas palabras.** Nunca se dice "andá, probalo" si nadie
corrió la app real. Cada cierre reporta explícitamente qué se probó end-to-end a mano y qué no. Reviews verdes
y suites verdes NO son "verificado end-to-end". *(MEMORY no-complacencia 2026-07-17; lección TDZ; modelo-estados
5 tandas "verificado de verdad vs no verificado".)*

**PR-4 · Reviews obligatorias por riesgo.** Funcional (senior + reviewer) siempre; data-exposure siempre en
todo cambio con superficie visible o de respuesta; security cuando toca plata, permisos, o datos sensibles;
contador cuando toca criterio fiscal. Un bloqueante se arregla y se re-revisa antes de mergear. *(CLAUDE.md
gates; MEMORY data-exposure obligatorio.)*

**PR-5 · Un solo agente muta el mismo checkout a la vez.** Dos en paralelo se pisan. Serie o worktree. *(MEMORY
regla operativa 0, incidente 2026-07-15.)*

**PR-6 · El fix nunca se logra relajando la regla ni el test.** Los seeds de tests de integración se cargan con
datos reales; el CI corre Postgres real como juez final. Si el CI se pone rojo por cambio de comportamiento, se
arreglan los datos, no el criterio. *(MEMORY regla operativa 4; modelo-estados "el CI encontró un bug real".)*

**PR-7 · Deuda cero como criterio de cierre.** Toda deuda técnica anotada tiene dueño y se salda antes de
arrancar features nuevas. Los seguimientos se anotan con su caso, no se pierden. *(RUMBO 2026-07-17 "modelo de
estados coherente antes que features"; caza sistemática de errores.)*

**PR-8 · Nunca estimar tiempos.** Se dice QUÉ y en qué ORDEN, jamás cuánto (ni horas, ni "rápido/lento").
*(MEMORY nunca-estimar-tiempos.)*

**PR-9 · Se habla en fácil.** Toda respuesta a Gastón va en criollo, sin jerga ni nombres de código ni
anglicismos, con un ejemplo cotidiano antes de la pregunta. Lo técnico vive en commits y docs, nunca en la
respuesta a él. *(MEMORY hablar-en-facil, prioridad #1.)*

**PR-10 · No se reabren decisiones cerradas.** Antes de preguntar algo fiscal o de negocio, se hace recall y se
confirma lo ya decidido. *(MEMORY no-reabrir-decisiones-cerradas.)*

**PR-11 · El diseño contempla las tres condiciones fiscales (RI / Mono / Exento).** Nunca se baja el alcance con
"hoy es Mono". La complejidad se esconde con defaults, no preguntándole al usuario "¿vos usás X?". *(MEMORY
multi-condición-fiscal; defaults-no-preguntas.)*

**PR-12 · Cada cambio de estado o de plata queda en el rastro** (quién, cuándo, por qué), aunque lo dispare el
sistema solo. *(modelo-estados-derivados BORRADOR 2026-07-17, regla 10.)*

---

## VACÍOS CONOCIDOS (hoy no hay regla firmada; preguntar a Gastón cuando aparezcan)

Estos temas NO tienen todavía una regla transversal firmada. Cuando una obra los toque, se le pregunta a Gastón
con opciones + recomendación, y la respuesta entra como regla nueva. No se decide solo ninguno.

- **V-1 · Matriz de candado con reserva "En curso" / concurrencia multi-usuario.** El modelo asume mono-usuario;
  dos usuarios tocando la misma reserva a la vez (dos reembolsos solapados, dos anulaciones) no tiene regla cerrada.
  *(seguimiento modelo-estados 5 tandas; ADR-043 anti-deadlock parcial.)*

- **V-2 · Factura-ancla / vínculo factura↔servicio.** Hoy no existe vínculo entre una factura y el servicio que
  cubre; "facturado total" se mide por monto, no por servicio. Si el negocio necesita medir por servicio, es
  decisión nueva. *(ADR-037 decisión H1 "no existe vínculo factura↔servicio".)*

- **V-3 · Asignar/cambiar el cliente de una reserva ya creada.** No existe pantalla para esto; el caso "sin cliente
  asignado" al anular queda sin camino. *(T7 contrato pantalla-motor, nota de backlog.)*

- **V-4 · Cruce de monedas con impacto fiscal (IVA sobre diferencia de cambio).** El display multimoneda está
  resuelto, pero el tratamiento fiscal del cruce (cargos del operador, IVA dif. cambio) tiene gate de contador
  pendiente. *(seguimientos plan anulaciones 2026-07-17; modelo-estados 5 tandas.)*

- **V-5 · Adelanto a cuenta del cliente (dinero sin reserva asignada).** Decidido: se trata DESPUÉS del norte. La
  regla de cómo convive con "prepago puro" no está cerrada. *(MEMORY adelanto-a-cuenta 2026-07-04.)*

- **V-6 · Neteo fase 2 y "Deshacer" basado en ND.** El circuito de anulaciones tiene una fase 2 (neteo, deshacer
  por nota de débito) todavía no diseñada. *(seguimientos plan anulaciones 2026-07-17.)*

- **V-7 · Colores y textos globales de la app.** Las secciones "Colores y estilo" y "Textos" de la guía de UX
  están marcadas pendientes. *(guia-ux-gaston, secciones pendientes.)*

---

*Este documento se mantiene vivo: cada decisión nueva de Gastón que sea transversal se agrega acá con su fuente
y fecha, y cada vacío que se resuelve pasa de la última sección a la capa que corresponda.*
