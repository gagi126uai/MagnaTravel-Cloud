# 2026-08-22 — Los banners naranjas se van a la campanita, y el PUT del operador ya no pisa nada

## Qué pidió Gastón

Dos cosas en esta tanda:

1. **"No quiero ver más los banners naranjas"** (por ejemplo: "LA RESERVA 2026-1051
   SALE EL 28/08/2026 Y TODAVÍA TIENE $1.200,00 SIN COBRAR"). Los quiere como
   notificación en la campanita, no como cartel gigante arriba de todo.
2. Cerrar los pendientes "no te olvides" anotados en memoria. Al contrastar con el
   código, 3 de los 4 ya estaban resueltos por trabajos anteriores (redondeos de
   centavos y PUT del operador por `95775546` del 05/08; inmutabilidad de reserva
   por ADR-020 de junio). Lo único vivo destapó dos campos hermanos sin proteger.

## Qué se hizo

### 1. Seis avisos de negocio bajaron del banner a la campanita

La regla ya estaba firmada el 19/08 ("avisos que trabajan, no que gritan"): el
banner naranja global queda reservado a caídas del sistema. Pero seguían gritando
seis avisos de negocio. El mecanismo es uno solo: toda notificación va a la
campanita, y si tiene prioridad "Urgent" ADEMÁS sale como banner. El arreglo fue
bajar la prioridad a "Normal" en los seis lugares que las crean:

1. Reserva que sale pronto con plata sin cobrar (`OperationalFinanceMonitorService`)
2. Resumen diario del vigía de coherencia de anuladas (`CoherenceWatchdogJob`)
3. Aprobación de NC parcial con reintentos agotados (`PartialCreditNoteBridgeReconciliationJob`)
4. NC parcial trabada sin respuesta de ARCA (`PartialCreditNotePostingReconciliationJob`)
5. Cancelación esperando resolución hace días (`PartialCreditNoteReviewAlertJob`)
6. "Revisá la reserva: cambió algo de los servicios" (`ReservaAutoStateService`)

Además:

- **Ahora se puede navegar desde la campanita**: el resolver de destinos
  (`NotificationTargetUrlResolver`) aprendió 3 tipos nuevos y arma el enlace a
  `/reservas/{id-público}` (nunca IDs internos). Dos tipos quedan sin enlace a
  propósito porque no existe pantalla de destino (documentado en el código).
- **Migración de datos** (`BackfillUrgentToNormalOperationalNotifications`): los
  avisos de estos tipos que ya estaban guardados como "Urgent" y siguen vivos se
  bajan a "Normal" en el próximo deploy, así los banners viejos desaparecen de
  una. No toca avisos leídos/descartados/resueltos.
- **Limpieza de un texto con jerga** (hallazgo del gate de exposición de datos):
  el aviso del punto 3 mostraba el error técnico crudo y nombres internos. Ahora
  dice "La aprobación de la nota de crédito de la reserva X no se pudo completar
  después de varios intentos. Avisale al soporte técnico para destrabarla." y el
  detalle técnico va solo al log del servidor.
- El componente del banner queda instalado para futuras caídas reales del sistema.

### 2. El PUT del operador ya no pisa dos campos más

El arreglo del 05/08 protegía tres campos del formulario del operador contra el
"PUT parcial que pisa" (si un cliente HTTP no manda el campo, el sistema
preservaba el valor en vez de resetearlo). Quedaban dos hermanos sin proteger:

- **Plazo de pago por defecto** (`DefaultPaymentTermDays`): omitirlo lo borraba.
  Ojo: mandar `null` explícito SIGUE borrando el plazo — eso es a propósito.
- **Activo** (`IsActive`): omitirlo REACTIVABA en silencio un proveedor dado de
  baja. Ese era el riesgo feo.

Se extendió el mismo guard de inspección del JSON crudo (`JsonTieneCampo`) y se
agregaron 5 tests HTTP espejo de los existentes.

## Reviews

Backend, seguridad y exposición de datos: **aprobadas sin bloqueos**. Los tres
retoques no bloqueantes que dejaron (tests del resolver, comentario
desactualizado, texto con jerga) se aplicaron en la misma tanda. Tests: 5609/5609
de Unit verdes pre-retoques + 12/12 en las suites tocadas post-retoques.

## Qué se VE distinto y qué NO

- **SE VE**: desaparecen los banners naranjas de avisos de negocio (los vivos se
  bajan con la migración). Los mismos avisos llegan a la campanita, la mayoría
  ahora con enlace directo a la reserva.
- **NO SE VE**: el guard del PUT del operador (protección interna contra clientes
  HTTP que manden datos incompletos) y la limpieza del texto técnico (solo se
  nota si el bridge de NC parcial vuelve a fallar).

## En paralelo (no incluido en este commit)

Quedó firmada por Gastón la obra "multa del operador mayor que lo facturado"
(arreglo completo: comparar contra todo lo facturado de la reserva + ND asociada
a varias facturas + tres salidas). Diseño técnico y spec UX escritos y en
revisión de arquitectura; investigación fiscal (ARCA no topea la ND — el candado
era nuestro) y validación contra ERPs reales (el circuito es patrón estándar)
terminadas. Se implementa en la próxima tanda.
