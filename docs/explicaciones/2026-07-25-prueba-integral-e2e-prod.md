# 2026-07-25 — Prueba integral E2E del sistema entero contra PRODUCCIÓN

> Pedido de Gaston: "probá vos el sistema entero en el backoffice — clientes,
> reservas, operadores, facturación, cobranza, caja — todo lo que se puede hacer
> y todo lo que NO se puede (tiene que avisar que no se puede)".
> Método: navegador real (sesión de Gaston en un Chrome vivo dedicado), agentes
> QA escribiendo scripts Playwright, ejecución supervisada, verificación cruzada
> contra la base de PROD por el canal de diagnóstico (solo lectura). Datos de
> prueba con prefijo PI0724. Facturación SIEMPRE homologación.

## Resultado global

**El sistema está sano.** Los circuitos completos funcionan de punta a punta en
producción: alta de cliente → reserva con 6 tipos de servicio → pasajero →
confirmaciones con sus candados → deuda al operador → pago en 2 pasos →
cobros al cliente → sobrecobro a saldo a favor → factura C con CAE de
homologación → anulación con NC. Los "no se puede" están: candados con
mensajes de negocio claros en criollo, casi siempre en el lugar correcto.

Se encontraron y **arreglaron EN EL DÍA** 2 regresiones (hotfixes deployados y
re-verificados en PROD):

1. **Buscador global caído** (500 para toda búsqueda): un `.ToString()`
   innecesario que Postgres no traduce, metido por la Tanda 3 del mismo día.
   InMemory lo toleraba (tests verdes con el bug vivo). Red nueva: test de
   integración que compila las 3 consultas del buscador contra Postgres real.
2. **Aviso de fechas incoherentes mudo** (#27): el motor mandaba el aviso y la
   pantalla nunca lo mostraba — pieza front sin cablear.

## Verificaciones de producto MAYORES (las reglas firmadas, vivas en PROD)

- **ADR-036 candado de prepago**: con deuda, el botón de facturar nace apagado;
  el caso de excepción exige tildar + motivo ≥10 letras, y ese motivo queda
  AUDITADO y visible después en la fila de la factura. Exacto a lo firmado.
- **ADR-037**: la factura se habilita recién con la reserva Confirmada.
- **Letra C** para Consumidor Final (jamás A) · CAE homologación · fecha de
  emisión en día argentino · banda "SIN VALIDEZ FISCAL — HOMOLOGACIÓN" en el
  PDF · anulación con NC en segundo plano CON aviso fiscal del IVA por período.
- **Sobrecobro = aviso con confirmación** (cliente y operador), excedente a
  saldo a favor visible en la reserva y en Clientes (Tanda 4, en vivo).
- **Historial en criollo**: "Cobro registrado: $50.000,00 — Efectivo" (Tanda 5).
- **Filtros de Cobranza reales** (Pagadas / Con deuda vencida ya no son
  fantasma); "deuda vencida" excluye lo saldado, correcto.
- **Deuda al operador nace con venta firme + servicio confirmado** (gate de
  ERP correcto — los presupuestos no generan deuda).
- **La asistencia exige fecha de nacimiento del pasajero para emitirse**, y el
  motivo del rechazo SE MUESTRA (inline). El aéreo exige nombres ("Cargá los
  nombres primero"). El titular es candado para confirmar hotel/traslado.
- **Nada de plata se borra**: contra-asiento en el movimiento manual de caja
  anulado (2 filas que netean), candado "no se puede eliminar un cobro con
  recibo emitido — anulá el recibo primero".
- **404 digno** ("No encontramos esta pantalla") en vez de página en blanco.
- Formatos es-AR y hora 24 en todo el recorrido; sin stack traces ni errores
  crudos en ninguna pantalla.

## HALLAZGOS (priorizados — ninguno arreglado hoy salvo los 2 hotfixes)

### ALTOS
1. **Factura sin desglose de renglones**: la pantalla armó y mostró los 5
   renglones; la factura quedó SIN items en la base y el PDF cayó al fallback
   "Servicios Turísticos - Res F-2026-1064" por el total. Con factura real, el
   cliente recibiría el comprobante sin detalle. Investigar el camino
   POST /invoices → persistencia de InvoiceItems (¿rama de emisión por
   excepción/segundo plano? ¿Factura C?). Pistas: InvoicePdfService.cs:249
   (fallback), AfipService.cs:711-760 (construcción de items).
2. **CUIT inválido se acepta sin chistar** (alta de cliente, dígito
   verificador incorrecto) — riesgo fiscal al facturar. Ni front ni motor
   validan el checksum.

### MEDIOS
3. **Guard de documento duplicado muerto desde la pantalla**: el alta nunca
   manda documentType, y el guard del motor exige ambos campos → el freno que
   parece existir no corre jamás desde la UI (el aviso "quizás te referís a..."
   sí anda, pero no bloquea).
4. **Filas fantasma al filtrar en Caja**: el buscador de Caja renderiza filas
   que no matchean el filtro (y no cuentan en el pie). Hipótesis fuerte: keys
   de React duplicadas — el movimiento manual y su contra-asiento comparten
   sourcePublicId (MovementsTab.jsx + TreasuryService.GetMovementsAsync).
   Confirmar con repro limpio.
5. **GUID crudo visible en Caja**: "Devolucion del operador SANTA CATALINA
   VIAJES S R L (7d94dc68-...)" — ManualCashMovementBuilder.cs:120 concatena
   refund.PublicId en el texto. Gate data-exposure: reemplazar por referencia
   legible u omitir.
6. **Anular factura no pide motivo** (Sí/No pelado) mientras anular reserva
   exige ≥10 letras — dos varas de auditoría para dos anulaciones fiscales.
7. **Gate de pasajeros inconsistente**: avanzar la reserva a "En gestión"
   exige CANTIDAD de pasajeros declarada; confirmar servicios exige NOMBRE
   del titular → se puede avanzar sin nadie nombrado y chocar recién después.
   Además hotel/paquete/asistencia no muestran el candado pre-emptivo (solo
   aéreo/traslado) y el rechazo del motor queda como texto chico.
8. **Cuenta del operador: "Marcar confirmado" sin candado del titular** y el
   rechazo del motor muere mudo ahí (seguimiento N1 de la Tanda 3, ahora con
   evidencia en vivo).
9. **Descripciones con "()" y "( -> )" vacíos** en el circuito del operador:
   hotel sin ciudad → "Hotel PI0724 Palace ()", traslado sin ruta →
   "( -> )". El fix del #47 cubrió solo la asistencia; falta el sweep en
   BuildSupplierServicesQuery (hotel/traslado/vuelo) con un helper único.

### MENORES / OBSERVACIONES
10. Typo de género: "Falta **el** fecha de nacimiento..." (PassengerNominalRules.cs:351).
11. "Hotel PI0724 Hotel B": el guard anti-duplicado solo cubre nombres que
    EMPIEZAN con "Hotel".
12. Mensajes de campo obligatorio del navegador en inglés ("Please fill out
    this field") en alta de cliente y validación nativa del monto de caja —
    depende del idioma del Chrome del usuario.
13. Reserva saldada: "Registrar cobro" se OCULTA sin motivo (el motor tiene el
    mensaje listo que nunca se muestra; contradice el patrón ADR-035 "botón
    visible apagado con motivo") — refina la decisión P4 de Gaston.
14. Contra-asiento de caja sin marca visual (sin badge "Anulado" ni tachado;
    ambas filas siguen ofreciendo Editar/Anular aunque el motor los rechace).
15. El widget "Cobros Pendientes" del Dashboard mostró la reserva de prueba
    como "$212.000 PENDIENTE" cuando ya estaba sobre-cobrada (pista para un
    barrido del Dashboard; sin perseguir).
16. Dashboard: "$9205" sin separador de miles y "Saldo Pendiente $-18.460"
    en rojo sin explicación.
17. Los cobros no guardan hora real (solo fecha de negocio) — decisión de
    diseño documentada; la única hora real de Caja es el ajuste manual.
18. El buscador global no indexa nombres de hoteles/servicios (por diseño
    actual); "Todas" en Reservas no existe como pestaña (hay "Archivadas").
19. El aviso "cargá los nombres para elegir" aparece en TODAS las filas de
    servicio (¿corresponde en paquete/asistencia?).
20. Aviso ámbar de condición fiscal: verificado el positivo (operador sin
    condición → aparece) y el negativo (TEST TEST RI → no aparece).

## Datos de prueba que quedaron en PROD (limpieza pendiente, prefijo PI0724)

- Clientes: PI0724 Consumidor Final (con duplicado a propósito, saldo a favor
  $140.000), PI0724 RI SA, PI0724 Mono, PI0724 CUIT Invalido.
- Operadores: PI0724 Operador SIN Fiscal, PI0724 Operador RI.
- Reserva F-2026-1064 Confirmada con 5 servicios (todos confirmados/emitidos),
  pasajero PI0724 Pasajero Uno (nac. 01/01/1990, doc 99999001).
- Cobros: $50.000 efectivo + $30.000 transferencia (recibo RCP-2026-000025,
  candado activo) + $200.000 efectivo (sobrecobro).
- Pago a TEST TEST $100.000 (transferencia) imputado al Hotel Palace.
- Factura C 0001-00000055 (CAE 86300674744770) ANULADA con NC de homologación
  (número de NC sin capturar).
- Caja: 2 pares de ajuste manual "PI0724 ajuste de prueba" anulados (netean $0).

## Cómo se hizo (para repetirlo)

Sesión: Chrome vivo dedicado con login de Gaston (una vez), puerto de
debugging local; los scripts se CONECTAN a ese navegador (la sesión se renueva
sola — los intentos con sesión copiada mueren a los 15 min por rotación del
pase). Agentes QA escriben scripts Playwright defensivos (try/catch por caso,
señal positiva obligatoria: toast O alert inline — jamás PASS por ausencia de
error), el coordinador los ejecuta y devuelve el stdout; verificación cruzada
por SQL de solo lectura cuando la UI y la base pueden discrepar. Capturas y
scripts en el scratchpad de la sesión (shots-integral-0724/m1..m6).
