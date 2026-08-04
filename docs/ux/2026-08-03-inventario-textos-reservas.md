# Inventario de textos visibles — sección Reservas (2026-08-03)

Auditoría de **voz y copy** de todo lo que ve el agente de viajes en Reservas.
Alcance barrido (inventario, no muestreo):

- `src/TravelWeb/src/features/reservas/**` (70 archivos no-test, ~21.900 líneas)
- `src/TravelWeb/src/components/` usados por Reservas: `CreateReservaModal.jsx`,
  `PassengerFormModal.jsx`, `CartelEmergente.jsx`, `ConfirmModal.jsx`,
  `ReservaDocumentsTab.jsx`, `ReservaTimeline.jsx`, `ReservaVoucherTab.jsx`,
  `ServiceFormModal.jsx`
- Textos del **backend** que se pintan en esas pantallas:
  `TravelApi.Domain/Reservations/*` (capacidades ADR-035, preflight, reglas nominales),
  `TravelApi.Domain/Entities/Reserva.cs`,
  `TravelApi.Infrastructure/Services/ReservaService.cs`,
  `BookingService*.cs`, `BookingCancellationService.cs`

## Voz oficial (firmada por el dueño, 2026-08-03)

Una sola voz: **voseo rioplatense** (`cargá`, `agregá`, `revisá`, `elegí`, `tenés`).
Prohibido: tuteo (`agrega`, `revisa`, `selecciona`), españolismos (`añadir`, `vale`,
`haz clic`), jerga técnica en pantalla, la leyenda `(Opcional)`, Mayúsculas De Título
Innecesarias y signos de interrogación sin `¿` de apertura.

---

## Totales por categoría

| # | Categoría | FRONT | API | Total |
|---|---|---:|---:|---:|
| 1 | Tilde faltante | 71 | 21 | **92** |
| 2 | Plural/singular mal resuelto | 5 | 3 | **8** |
| 3 | Voz equivocada (tuteo / españolismo / usted) | 38 | 11 | **49** |
| 4 | Leyenda prohibida u ortotipografía | 51 | 1 | **52** |
| 5 | Jerga técnica o texto fuera del idioma del negocio | 24 | 5 | **29** |
| | **TOTAL** | **189** | **41** | **230** |

Hay solapamiento: un mismo texto puede aparecer en 2 categorías (p. ej.
`"Indica un motivo de la edicion"` = tuteo + tilde). Cada fila lo indica.

---

## Glosario de producto aplicado en las reescrituras

| Concepto | Se dice | Nunca |
|---|---|---|
| Reserva | **Reserva** | file, carpeta, booking |
| Estados | Cotización · Presupuesto · **En gestión** · Confirmada · En viaje · Finalizada · **Perdida** · Anulada · Esperando reembolso · **Archivada** | Budget, InManagement, Confirmed, Traveling, Lost, "Perdido", "Archivado" |
| Proveedor del servicio | **Operador** (en el flujo) / menú "Proveedores" | mayorista, supplier |
| Deshacer con plata devuelta | **Anular** (NC total + ND de multa) | "cancelar" en ese sentido |
| Terminar de pagar el total | **Cancelar** (abonar) | — |
| Multa del operador | **Multa del operador** | penalidad, pass-through |
| Nota de crédito / débito | **Nota de crédito** (a favor del cliente) / **Nota de débito** (en contra) | NC/ND sueltas sin explicar |
| Tipo de cambio | **Tipo de cambio** | TC |
| Emisión saltando el bloqueo de deuda | **Emitir igual (excepción)** | override, force issue |
| Documento del viaje | **Documento** / **Voucher** | adjunto, attachment |
| Cerrar un panel/modal | **Volver** | Cancelar (choca con "Cancelar reserva") |

---

## Categoría 1 — Tilde faltante

### 1.A · FRONT

| # | Archivo:línea | Texto ACTUAL | Texto PROPUESTO | Notas |
|---|---|---|---|---|
| 1 | `features/reservas/lib/reservaStatusLabels.js:21` | `Cotizacion` | `Cotización` | ⚠ tests |
| 2 | `features/reservas/lib/reservaStatusLabels.js:23` | `En gestion` | `En gestión` | ⚠ tests |
| 3 | `features/reservas/pages/ReservasPage.jsx:51` | `En gestion` | `En gestión` | label de pestaña |
| 4 | `features/reservas/components/ServiceList.jsx:239` | `En gestion` | `En gestión` | |
| 5 | `features/reservas/components/ServiceList.jsx:681` | `En gestion` | `En gestión` | |
| 6 | `features/reservas/components/ServiceList.jsx:186` | `no requiere confirmacion` | `no requiere confirmación` | |
| 7 | `features/reservas/components/ServiceList.jsx:706` | `Todos listos — la reserva se va a confirmar automaticamente.` | `Todos listos — la reserva se confirma sola.` | + cat 5 |
| 8 | `features/reservas/components/ServiceList.jsx:1205` | `La reserva sigue visible, pero una o mas listas de servicios no se pudieron refrescar.` | `La reserva se ve bien, pero algunos servicios no se pudieron actualizar. Recargá la pantalla.` | + cat 5 |
| 9 | `features/reservas/components/CapacityWarning.jsx:22` | `Atencion: hay …` | `Atención: hay …` | + cat 2 |
| 10 | `features/reservas/components/ReservaHeader.jsx:244` | `El viaje todavia no termino` | `El viaje todavía no terminó` | |
| 11 | `features/reservas/components/ReservaHeader.jsx:272` | `Esta reserva tiene el candado activo. Para editar necesitas autorizacion.` | `Reserva con candado. Para editarla, pedí autorización.` | + cat 3 |
| 12 | `features/reservas/components/ReservaHeader.jsx:466` | `El cliente acepto el presupuesto — empieza la gestion con los operadores` | `El cliente aceptó el presupuesto — arranca la gestión con los operadores` | |
| 13 | `features/reservas/components/ReservaLockBanner.jsx:17` | `Pedir autorizacion` | `Pedir autorización` | |
| 14 | `features/reservas/components/ReservaLockBanner.jsx:24` | `pedi autorizacion` | `pedí autorización` | |
| 15 | `features/reservas/components/RevertStatusModal.jsx:57` | `No se pudieron cargar las opciones de reversion.` | `No se pudieron cargar las opciones. Probá de nuevo.` | |
| 16 | `features/reservas/components/RevertStatusModal.jsx:233` | `Motivo de la reversion...` | `Motivo del cambio de estado...` | + cat 4 |
| 17 | `features/reservas/components/RevertStatusModal.jsx:194` | `No sos admin. Necesitas autorizacion de un supervisor para revertir el estado.` | `Para cambiar el estado necesitás que te autorice un supervisor.` | + cat 3 |
| 18 | `features/reservas/components/MarkLostModal.jsx:71` | `como Perdida. Podes revertirla despues si el cliente vuelve.` | `como Perdida. Si el cliente vuelve, la podés reactivar después.` | |
| 19 | `features/reservas/components/MarkLostModal.jsx:84` | `¿Por que no compro? (puede dejarse en blanco)` | `¿Por qué no compró? (podés dejarlo en blanco)` | |
| 20 | `features/reservas/components/EditReservaDatesModal.jsx:107` | `Estas fechas tambien se recalculan cuando agregas o editas servicios. Editarlas aca tiene sentido cuando los servicios no tienen fechas claras o queres un override manual.` | `Estas fechas se recalculan solas cuando cargás o editás servicios. Editalas acá si los servicios todavía no tienen fecha o querés fijarlas a mano.` | + cat 3 + cat 5 |
| 21 | `features/reservas/components/EditReservaDatesModal.jsx:147` | `Sugerido del ultimo servicio cargado` | `Sugerido del último servicio cargado` | |
| 22 | `features/reservas/pages/ReservaDetailPage.jsx:816` | `Confirmar accion` | `Confirmar` | + cat 4 |
| 23 | `features/reservas/pages/ReservaDetailPage.jsx:817` | `Estas seguro?` | `¿Seguro que querés seguir?` | + cat 4 |
| 24 | `features/reservas/pages/ReservaDetailPage.jsx:993` | `Esta accion marcara el comprobante como anulado. El pago sigue vigente.` | `El comprobante queda anulado. El pago del cliente sigue registrado.` | |
| 25 | `features/reservas/pages/ReservaDetailPage.jsx:994` | `Si, anular` | `Sí, anular` | |
| 26 | `features/reservas/pages/ReservaDetailPage.jsx:1227` | `No se pudo cargar la informacion. Verifica que la URL sea correcta.` | `No pudimos abrir esta reserva. Volvé al listado y entrá de nuevo.` | + cat 3 + cat 5 |
| 27 | `features/reservas/pages/ReservaDetailPage.jsx:1256` | `Accion irreversible. Solo aplicable a reservas sin pagos.` | `No se puede deshacer. Solo se eliminan reservas sin cobros.` | |
| 28 | `features/reservas/pages/ReservaDetailPage.jsx:1264` | `El estado pasara a 'Archivado'.` | `La reserva pasa a Archivada y queda solo para consulta.` | + cat 5 (label real = "Archivada") |
| 29 | `features/reservas/pages/ReservaDetailPage.jsx:1984` | `Cotizacion.` | `Cotización.` | |
| 30 | `features/reservas/pages/ReservaDetailPage.jsx:2065` | `Esta reserva conserva la trazabilidad de la gestion comercial que la genero.` | `Esta reserva guarda el rastro de la gestión comercial que la originó.` | + cat 5 |
| 31 | `features/reservas/pages/ReservaDetailPage.jsx:2082` | `Abrir cotizacion origen` | `Abrir la cotización de origen` | |
| 32 | `features/reservas/pages/ReservaDetailPage.jsx:2137` | `El cliente acepto` | `El cliente aceptó` | |
| 33 | `features/reservas/pages/ReservaDetailPage.jsx:2301` | `Estas seguro de eliminar este pasajero de la reserva?` | `¿Seguro que querés sacar a este pasajero de la reserva?` | + cat 4 |
| 34 | `features/reservas/lib/reservationServiceModel.js:240` | `No se encontro el identificador publico del servicio.` | `No pudimos identificar el servicio. Recargá la pantalla y probá de nuevo.` | + cat 5 |
| 35 | `features/reservas/inline-service/FlightInlineForm.jsx:197` | `serviceType="Aereo"` | **NO TOCAR** (clave interna) | valor persistido |
| 36 | `features/reservas/inline-service/ServiceInlineCard.jsx:49` | `{ id: "Aereo", label: "Aéreo" }` | ya correcto | referencia |
| 37 | `components/ServiceFormModal.jsx:44` | `{ value: "Aereo", label: "Aereo" }` | label → `Aéreo` (value queda `Aereo`) | |
| 38 | `components/ServiceFormModal.jsx:336` | `Codigo Aerolinea` | `Código de aerolínea` | + cat 4 |
| 39 | `components/ServiceFormModal.jsx:347` | `Nombre Aerolinea` | `Nombre de la aerolínea` | + cat 4 |
| 40 | `components/ServiceFormModal.jsx:350` | `Aerolineas Argentinas...` | `Aerolíneas Argentinas...` | placeholder |
| 41 | `components/ServiceFormModal.jsx:362` | `Numero de Vuelo` | `Número de vuelo` | + cat 4 |
| 42 | `components/ServiceFormModal.jsx:473` | `Numero Confirmacion` | `Número de confirmación` | + cat 4 |
| 43 | `components/ServiceFormModal.jsx:484` | `Numero Ticket` | `Número de ticket` | + cat 4 |
| 44 | `components/ServiceFormModal.jsx:774` | `Habitacion Estandar` | `Habitación estándar` | fallback visible |
| 45 | `components/ServiceFormModal.jsx:845` | `Dias` | `Días` | |
| 46 | `components/ServiceFormModal.jsx:874` | `Pais` | `País` | |
| 47 | `components/ServiceFormModal.jsx:877` | `Argentina, Mexico...` | `Argentina, México...` | placeholder |
| 48 | `components/ServiceFormModal.jsx:885` | `Direccion` | `Dirección` | |
| 49 | `components/ServiceFormModal.jsx:918` | `Tipo de Habitacion` | `Tipo de habitación` | + cat 4 |
| 50 | `components/ServiceFormModal.jsx:943-947` | labels `Media Pension`, `Pension Completa` | labels `Media pensión`, `Pensión completa` (values quedan igual) | ⚠ tests fijan los VALUES |
| 51 | `components/ServiceFormModal.jsx:1127` | `Numero Confirmacion` | `Número de confirmación` | + cat 4 |
| 52 | `components/ServiceFormModal.jsx:1294` | `Cancun, Mexico` | `Cancún, México` | placeholder |
| 53 | `components/ServiceFormModal.jsx:1345` | `Numero Confirmacion` | `Número de confirmación` | + cat 4 |
| 54 | `components/ServiceFormModal.jsx:1386` | `Itinerario / Descripcion` | `Itinerario / descripción` | + cat 4 |
| 55 | `components/ServiceFormModal.jsx:1390` | `Dia 1: Llegada y traslado al hotel. Dia 2: Tour por la ciudad...` | `Día 1: llegada y traslado al hotel. Día 2: tour por la ciudad...` | |
| 56 | `components/ServiceFormModal.jsx:1633` | `Nro. Confirmacion` | `Nro. de confirmación` | + cat 4 |
| 57 | `components/ServiceFormModal.jsx:1753` | `Descripcion *` | `Descripción *` | |
| 58 | `components/ServiceFormModal.jsx:1767` | `Confirmacion` | `Confirmación` | |
| 59 | `components/ServiceFormModal.jsx:694` | `Escribis el hotel: si esta en tu tarifario aparecen las opciones (por …)` | `Escribís el hotel: si está en tu tarifario aparecen las opciones (por …)` | |
| 60 | `components/ServiceFormModal.jsx:797` | `No esta en tu tarifario. Segui cargando los datos (ciudad, fechas, precio): se agrega como hotel nuevo.` | `No está en tu tarifario. Seguí cargando los datos (ciudad, fechas, precio) y se da de alta como hotel nuevo.` | |
| 61 | `components/ServiceFormModal.jsx:1690` | `Busca en el tarifario segun el tipo de servicio elegido (ej. "Excursion").` | `Buscá en tu tarifario según el tipo de servicio elegido (ej. "Excursión").` | + cat 3 |
| 62 | `components/ServiceFormModal.jsx:2424` | `Habitacion "${roomType \|\| "Estandar"}" agregada correctamente.` | `Habitación "${roomType \|\| "estándar"}" agregada.` | |
| 63 | `components/ServiceFormModal.jsx:2534` | `Reserva en estado <b>{reservaStatus}</b>. La edicion economica queda bloqueada.` | ver cat 5 #1 | |
| 64 | `components/CreateReservaModal.jsx:148` | `Se usara para facturacion y contacto.` | `Es a quién le facturamos y a quién contactamos.` | + cat 3 |
| 65 | `components/PassengerFormModal.jsx:452` | `Numero de documento *` | `Número de documento *` | |
| 66 | `components/PassengerFormModal.jsx:580` | `Telefono` | `Teléfono` | |
| 67 | `components/PassengerFormModal.jsx:292` | `showWarning("Ingresa al menos 3 caracteres.", "Padron AFIP")` | `showWarning("Escribí al menos 3 caracteres.", "Padrón AFIP")` | + cat 3 |
| 68 | `components/PassengerFormModal.jsx:303` | título `Padron AFIP` | `Padrón AFIP` | |
| 69 | `components/ReservaDocumentsTab.jsx:500` | `No hay documentos cargados todavia.` | `Todavía no hay documentos cargados.` | |
| 70 | `components/ReservaDocumentsTab.jsx:384` | `Se eliminara "${fileName}" permanentemente.` | `Se elimina "${fileName}" para siempre.` | |
| 71 | `components/ReservaDocumentsTab.jsx:387` | `Si, eliminar` | `Sí, eliminar` | |
| 72 | `components/ReservaDocumentsTab.jsx:335` | `El archivo es demasiado grande (max 25 MB).` | `El archivo pesa más de 25 MB.` | |
| 73 | `components/ReservaVoucherTab.jsx:476` | `Indica un motivo de anulacion de al menos 10 caracteres.` | `Escribí un motivo de al menos 10 caracteres.` | + cat 3 |
| 74 | `components/ReservaVoucherTab.jsx:735` | `Estos documentos estan anulados y se conservan solo como trazabilidad. No se pueden emitir, aprobar, rechazar ni enviar.` | `Estos documentos están anulados y quedan solo como registro. No se pueden emitir, aprobar, rechazar ni enviar.` | + cat 5 |
| 75 | `components/ReservaVoucherTab.jsx:752` | `Los documentos anulados se mostraran aca cuando existan.` | `Acá van a aparecer los documentos que anules.` | |
| 76 | `components/ReservaVoucherTab.jsx:1163` | `El documento quedara trazable como anulado.` | `El documento queda anulado, con registro de quién y cuándo.` | + cat 5 |
| 77 | `components/ReservaVoucherTab.jsx:1165` | `No se podra emitir, aprobar, rechazar ni enviar. El historial conservara quien lo anulo y por que.` | `No se va a poder emitir, aprobar, rechazar ni enviar. Queda registrado quién lo anuló y por qué.` | |
| 78 | `components/ReservaVoucherTab.jsx:1171` | `Motivo de Anulacion` | `Motivo de la anulación` | + cat 4 |
| 79 | `components/ReservaVoucherTab.jsx:1178` | `Ej. Se genero con datos incorrectos, se subio el archivo equivocado...` | `Ej.: se generó con datos incorrectos, se subió el archivo equivocado...` | |
| 80 | `components/ReservaVoucherTab.jsx:754` | `Revisa la solapa Anulados para ver documentos historicos.` | `Mirá la solapa Anulados para ver los que diste de baja.` | + cat 3 |
| 81 | `features/reservas/components/ReservaTable.jsx:40` | `Intenta ajustar los filtros de busqueda.` | `Probá cambiando los filtros.` | + cat 3 |
| 82 | `features/reservas/components/ReservaMobileList.jsx:14` | `Intenta ajustar los filtros de busqueda.` | `Probá cambiando los filtros.` | + cat 3 |

> **Falsos positivos verificados (NO tocar):** `Acciones`, `opciones`, `razones`,
> `condiciones`, `emisiones` (llanas terminadas en -s: no llevan tilde).
> `value="Aereo"`, `value="Media Pension"`, `value="Deposito"` son **datos
> persistidos**: solo se corrige el `label` visible.

### 1.B · API (backend)

| # | Archivo:línea | Texto ACTUAL | Texto PROPUESTO |
|---|---|---|---|
| 83 | `TravelApi.Domain/Entities/Reserva.cs:457` | `No se puede registrar un cobro en este estado de la reserva. Pasala a En gestion primero.` | `No se puede registrar un cobro en este estado. Pasá la reserva a En gestión primero.` ⚠ tests |
| 84 | `TravelApi.Domain/Reservations/ReservaCapabilities.cs:140` | `El voucher se puede emitir recien desde Confirmada en adelante.` | `El voucher se emite recién desde Confirmada en adelante.` |
| 85 | `TravelApi.Domain/Reservations/ReservaCapabilities.cs:151` | `La reserva esta en viaje: no se cancela; corregí por nota de crédito/ajuste.` | `La reserva está en viaje: no se anula. Corregí con una nota de crédito o un ajuste.` |
| 86 | `TravelApi.Domain/Reservations/ServiceResolutionRules.cs:119` | `un aereo (sin emitir)` | `un aéreo (sin emitir)` |
| 87 | `TravelApi.Infrastructure/Services/ReservaService.cs:683` | `Las cantidades de pasajeros solo se pueden editar en Cotizacion o Presupuesto. Si ya pasó a En gestion, cargá los pasajeros nominales.` | `Las cantidades se editan en Cotización o Presupuesto. Si la reserva ya está En gestión, cargá los pasajeros con nombre.` |
| 88 | `…/ReservaService.cs:1090` | `Asignacion no encontrada` | `No encontramos esa asignación de pasajeros.` |
| 89 | `…/ReservaService.cs:1234` | `No tenes permiso para cancelar reservas.` | `No tenés permiso para cancelar reservas.` |
| 90 | `…/ReservaService.cs:1245` | `Cancelar una reserva con cobros o facturas asociadas requiere autorizacion adicional.` | `Cancelar una reserva con cobros o facturas necesita autorización de un supervisor.` |
| 91 | `…/ReservaService.cs:1310` | `No tenes permiso para anular reservas.` | `No tenés permiso para anular reservas.` |
| 92 | `…/ReservaService.cs:1321` | `Anular una reserva con cobros o facturas asociadas requiere autorizacion adicional.` | `Anular una reserva con cobros o facturas necesita autorización de un supervisor.` |
| 93 | `…/ReservaService.cs:1916` | `Una reserva Perdida solo puede volver a '{legalTarget}' (el estado desde el que se perdio).` | ~~`Una reserva Perdida solo vuelve a {legalTarget}, el estado en el que estaba cuando se perdió.`~~ **CORREGIDO por `backend-dotnet-reviewer` (P-1, 2026-08-04):** `legalTarget` es el valor CRUDO del enum de estado (ej. `Budget`), no una palabra de negocio — interpolarlo repetía el mismo leak del hallazgo #2. Texto final: `Una reserva Perdida solo vuelve al estado en el que estaba cuando se perdió. Elegí la opción que ofrece la pantalla.` (la pantalla ya publica ese destino en `AllowedTargets`, `GetRevertOptionsAsync`). |
| 94 | `…/ReservaService.cs:1835` | `…Si necesitas anular, emiti una Nota de Credito primero.` | `…Si la querés anular, emití primero una nota de crédito.` |
| 95 | `…/ReservaService.cs:1955` | `Necesitas autorizacion de un supervisor para revertir el estado de la reserva. Selecciona un supervisor en el formulario.` | `Para cambiar el estado necesitás que te autorice un supervisor. Elegí uno en el formulario.` |
| 96 | `…/ReservaService.cs:1957` | `Indica un motivo de la reversion (al menos 10 caracteres).` | `Escribí el motivo del cambio de estado (al menos 10 caracteres).` |
| 97 | `…/ReservaService.cs:1961` | `El supervisor seleccionado no existe o esta inactivo.` | `Ese supervisor no existe o está dado de baja.` |
| 98 | `…/ReservaService.cs:2447` | `La reserva no esta bajo candado: todavia se puede editar libremente, no necesita autorizacion.` | `Esta reserva no tiene candado: se puede editar sin pedir autorización.` |
| 99 | `…/ReservaService.cs:2451` | `Indica un motivo de la edicion (al menos 10 caracteres).` | `Escribí el motivo del cambio (al menos 10 caracteres).` |
| 100 | `…/ReservaService.cs:2470` | `Necesitas que alguien con permiso autorice la edicion de una reserva confirmada. Selecciona un autorizante.` | `Para editar una reserva confirmada necesitás que alguien la autorice. Elegí quién.` |
| 101 | `…/ReservaService.cs:2474` | `El autorizante seleccionado no existe o esta inactivo.` | `Esa persona no existe o está dada de baja.` |
| 102 | `…/ReservaService.cs:5359` | `La reserva tiene facturas con CAE vigentes. Debe anularlas (se emitira Nota de Credito) antes de cancelar la reserva.` | `La reserva tiene facturas vivas. Anulalas primero (se emite una nota de crédito) y después cancelá la reserva.` |
| 103 | `…/ReservaService.cs:5806` | `No se puede volver a Presupuesto porque hay facturas emitidas. Debes anularlas primero (Nota de Credito).` | `No se puede volver a Presupuesto: hay facturas emitidas. Anulalas primero con una nota de crédito.` |

---

## Categoría 2 — Plural/singular mal resuelto

| # | Archivo:línea | ACTUAL | PROPUESTO | Front/API |
|---|---|---|---|---|
| 1 | `features/reservas/pages/ReservaDetailPage.jsx:644` | `Total: {total} pasajeros` | `Total: {total} {total === 1 ? "pasajero" : "pasajeros"}` | FRONT |
| 2 | `features/reservas/pages/ReservaDetailPage.jsx:647` | `Servicios cargados esperan {expectedCapacity} pasajeros` | `Los servicios cargados esperan {n} {n === 1 ? "pasajero" : "pasajeros"}` | FRONT |
| 3 | `features/reservas/components/CapacityWarning.jsx:22` | `hay {paxCount} pasajeros cargados pero los servicios contratados solo soportan {cap.total}` | `hay {paxCount} {paxCount === 1 ? "pasajero cargado" : "pasajeros cargados"} y los servicios contratados alcanzan para {cap.total}` | FRONT |
| 4 | `features/reservas/components/CancelarVariosServiciosInline.jsx:495` | `{n} servicios anulados.` | `{n === 1 ? "1 servicio anulado." : n + " servicios anulados."}` | FRONT |
| 5 | `features/reservas/components/EmitirFacturaInline.jsx:1030` | `${n} servicios no entraron en esta factura:` | OK — ya hay rama singular en la línea 1029 | FRONT |
| 6 | `TravelApi.Domain/Reservations/PassengerNominalRules.cs:359` | `Faltan datos de {n} pasajero(s) para {actionLabel}.` | `Faltan datos de {n} {(n == 1 ? "pasajero" : "pasajeros")} para {actionLabel}.` ⚠ tests | API |
| 7 | `TravelApi.Domain/Reservations/PassengerNominalRules.cs:372` | `{verb} {fieldList} de {n} pasajero(s) para {actionLabel}.` | idem #6 ⚠ tests | API |
| 8 | `TravelApi.Infrastructure/Services/ReservaService.cs:4628` | `La reserva declara {declaredPax} pasajero(s) y ya están todos cargados.` | `La reserva declara {n} {(n == 1 ? "pasajero" : "pasajeros")} y ya están todos cargados.` | API |
| 9 | `TravelApi.Infrastructure/Services/BookingService.Reschedule.cs:51` | `Reprogramacion del viaje: {daysShift} dia(s)` | `Reprogramación del viaje: {n} {(Math.Abs(n) == 1 ? "día" : "días")}` | API |

> No incluyo los `cancelacion(es)` / `anulacion(es)` de `BookingCancellationService.cs`
> (líneas 4664, 4985, 5713): son mensajes de log de jobs, no llegan al usuario.

---

## Categoría 3 — Voz equivocada (tuteo / españolismo / usted)

### 3.A · FRONT

| # | Archivo:línea | ACTUAL | PROPUESTO |
|---|---|---|---|
| 1 | `components/CreateReservaModal.jsx:63` | `Por favor selecciona un cliente principal` | `Elegí el cliente de la reserva.` |
| 2 | `components/CreateReservaModal.jsx:104` | `Crea la reserva en estado Presupuesto y carga sus servicios.` | `Nace como presupuesto: después le cargás los servicios.` |
| 3 | `components/CreateReservaModal.jsx:148` | `Se usara para facturacion y contacto.` | `Es a quién le facturamos y a quién contactamos.` |
| 4 | `features/reservas/components/CapacityWarning.jsx:23` | `Ajusta la capacidad de los servicios o agrega uno nuevo antes de continuar.` | `Ajustá la capacidad de los servicios o sumá uno más antes de seguir.` |
| 5 | `features/reservas/pages/ReservaDetailPage.jsx:650` | `Agrega servicios para validar capacidad` | `Cargá servicios para chequear la capacidad` |
| 6 | `features/reservas/pages/ReservaDetailPage.jsx:1227` | `Verifica que la URL sea correcta.` | (ver cat 1 #26) |
| 7 | `features/reservas/components/ReservaHeader.jsx:272` | `Para editar necesitas autorizacion.` | `Para editarla, pedí autorización.` |
| 8 | `features/reservas/components/RevertStatusModal.jsx:194` | `No sos admin. Necesitas autorizacion de un supervisor…` | `Para cambiar el estado necesitás que te autorice un supervisor.` |
| 9 | `features/reservas/components/ReservaTable.jsx:40` | `Intenta ajustar los filtros de busqueda.` | `Probá cambiando los filtros.` |
| 10 | `features/reservas/components/ReservaMobileList.jsx:14` | `Intenta ajustar los filtros de busqueda.` | `Probá cambiando los filtros.` |
| 11 | `features/reservas/components/EditReservaDatesModal.jsx:107` | `…cuando agregas o editas servicios…` | (ver cat 1 #20) |
| 12 | `components/ReservaDocumentsTab.jsx:475` | `Haz clic o arrastra documentos aqui` | `Arrastrá los documentos acá o tocá para elegirlos` |
| 13 | `components/ReservaDocumentsTab.jsx:415` | `Descarga iniciada.` | `Descargando el documento…` |
| 14 | `components/ReservaVoucherTab.jsx:309` | `Selecciona al menos un pasajero para este alcance.` | `Elegí al menos un pasajero.` |
| 15 | `components/ReservaVoucherTab.jsx:348` | `Selecciona el archivo del documento externo.` | `Elegí el archivo que querés subir.` |
| 16 | `components/ReservaVoucherTab.jsx:352` | `Indica el origen del documento externo.` | `Escribí de dónde salió el documento.` |
| 17 | `components/ReservaVoucherTab.jsx:393` | `Para emitir con saldo pendiente, indica una justificación de al menos 10 caracteres.` | `La reserva tiene saldo pendiente: escribí el motivo (al menos 10 caracteres).` |
| 18 | `components/ReservaVoucherTab.jsx:397` | `Selecciona el supervisor que debe autorizar esta emisión.` | `Elegí quién autoriza esta emisión.` |
| 19 | `components/ReservaVoucherTab.jsx:451` | `Por favor, indica un motivo de rechazo válido.` | `Escribí por qué lo rechazás.` |
| 20 | `components/ReservaVoucherTab.jsx:476` | `Indica un motivo de anulacion de al menos 10 caracteres.` | `Escribí un motivo de al menos 10 caracteres.` |
| 21 | `components/ReservaVoucherTab.jsx:754` | `Revisa la solapa Anulados para ver documentos historicos.` | `Mirá la solapa Anulados para ver los que diste de baja.` |
| 22 | `components/ReservaVoucherTab.jsx:757` | `Añade uno usando el botón superior derecho.` | `Sumá uno con el botón de arriba a la derecha.` |
| 23 | `components/ReservaVoucherTab.jsx:689` (comentario) y **`:700`** | `Añadir Documento` | `Agregar documento` |
| 24 | `components/ReservaVoucherTab.jsx:955` | `Añadir Documento` / `Generar Documento del Sistema` / `Subir Documento Externo` | `Agregar documento` / `Generar documento` / `Subir un documento` |
| 25 | `components/ReservaVoucherTab.jsx:968` | `Crea un voucher automáticamente usando los datos de la reserva` | `Armamos el voucher con los datos de la reserva` |
| 26 | `components/ReservaVoucherTab.jsx:980` | `Carga un documento en formato PDF o imagen emitido por un tercero` | `Subí un PDF o una imagen que emitió otra empresa` |
| 27 | `components/ReservaVoucherTab.jsx:1064` | `Esta reserva tiene un saldo deudor. Debes solicitar autorización a un supervisor para emitir los documentos.` | `La reserva tiene saldo pendiente. Para emitir los documentos, pedí autorización a un supervisor.` |
| 28 | `components/ReservaVoucherTab.jsx:1087` | `Selecciona el Supervisor...` | `Elegí un supervisor...` |
| 29 | `components/ReservaVoucherTab.jsx:1129` | `Indica al vendedor por qué no autorizas la emisión...` | `Contale al vendedor por qué no lo autorizás...` |
| 30 | `components/ReservaVoucherTab.jsx:1377` | `Este formato no se puede previsualizar en el navegador. Puedes descargarlo para revisarlo.` | `Este archivo no se puede ver acá. Descargalo para revisarlo.` |
| 31 | `components/PassengerFormModal.jsx:292` | `Ingresa al menos 3 caracteres.` | `Escribí al menos 3 caracteres.` |
| 32 | `components/ServiceFormModal.jsx:87` | `Completa ${fieldLabel}.` | `Completá ${fieldLabel}.` |
| 33 | `components/ServiceFormModal.jsx:2289` | `Completa la fecha de salida.` | `Completá la fecha de salida.` |
| 34 | `components/ServiceFormModal.jsx:2290` | `Completa la fecha de llegada.` | `Completá la fecha de llegada.` |
| 35 | `components/ServiceFormModal.jsx:2312` | `Ingresa el nombre del hotel.` | `Cargá el nombre del hotel.` |
| 36 | `components/ServiceFormModal.jsx:2315` | `Ingresa la ciudad del hotel.` | `Cargá la ciudad del hotel.` |
| 37 | `components/ServiceFormModal.jsx:2345` | `Completa la fecha de pick-up.` | `Completá la fecha del pick-up.` |
| 38 | `components/ServiceFormModal.jsx:1690` | `Busca en el tarifario segun el tipo de servicio elegido…` | `Buscá en tu tarifario según el tipo de servicio elegido…` |
| 39 | `components/ServiceFormModal.jsx:263 / 997 / 1219` | `El agente escribe la aerolínea o ruta y elige del tarifario.` | `Escribí la aerolínea o la ruta y elegí del tarifario.` (3.ª persona → voseo directo) |

### 3.B · API

| # | Archivo:línea | ACTUAL | PROPUESTO |
|---|---|---|---|
| 40 | `…/ReservaService.cs:1234` | `No tenes permiso…` | `No tenés permiso…` |
| 41 | `…/ReservaService.cs:1310` | `No tenes permiso…` | `No tenés permiso…` |
| 42 | `…/ReservaService.cs:1835` | `Si necesitas anular, emiti una Nota de Credito primero.` | `Si la querés anular, emití primero una nota de crédito.` |
| 43 | `…/ReservaService.cs:1955` | `Necesitas… Selecciona un supervisor…` | ver cat 1 #95 |
| 44 | `…/ReservaService.cs:1957` | `Indica un motivo…` | `Escribí el motivo…` |
| 45 | `…/ReservaService.cs:2451` | `Indica un motivo de la edicion…` | `Escribí el motivo del cambio…` |
| 46 | `…/ReservaService.cs:2470` | `Necesitas que alguien… Selecciona un autorizante.` | ver cat 1 #100 |
| 47 | `…/ReservaService.cs:3965` | `Debe seleccionar un tipo de servicio` | `Elegí el tipo de servicio.` |
| 48 | `…/ReservaService.cs:4114` | `Debe seleccionar un tipo de servicio` | `Elegí el tipo de servicio.` |
| 49 | `…/ReservaService.cs:5359` | `Debe anularlas…` | `Anulalas primero…` |
| 50 | `…/ReservaService.cs:5806` | `Debes anularlas primero…` | `Anulalas primero…` |
| 51 | `…/ReservaService.cs:5809` | `Cancela esos servicios primero.` | `Cancelá esos servicios primero.` |

---

## Categoría 4 — Leyenda prohibida u ortotipografía

### 4.A · Leyenda `(Opcional)` — prohibida por `docs/ux/guia-ux-gaston.md`

| # | Archivo:línea | ACTUAL | PROPUESTO |
|---|---|---|---|
| 1 | `components/CreateReservaModal.jsx:155` | `Fecha de Inicio (Opcional)` | `Fecha de salida` |
| 2 | `features/reservas/components/RegistrarCobroInline.jsx:461` | `Nota (opcional)` | `Nota` |
| 3 | `features/reservas/components/RevertStatusModal.jsx:227` | `(opcional)` | *(eliminar)* |
| 4 | `features/reservas/components/RevertStatusModal.jsx:233` | `Motivo (opcional)...` | `Motivo del cambio...` |
| 5 | `features/reservas/components/ResolverServicioInline.jsx:214` | `Opcional` | *(eliminar)* |
| 6 | `features/reservas/inline-service/PackageInlineForm.jsx:286` | `Fecha de fin del paquete (opcional)` | `Fecha de fin del paquete` |
| 7 | `components/ReservaVoucherTab.jsx:1171` | `(opcional para administradores)` | *(eliminar)* |
| 8 | `components/ReservaVoucherTab.jsx:1178` | `Opcional: indica el motivo de la anulación...` | `Motivo de la anulación...` |
| 9 | `components/ServiceFormModal.jsx:157` | `Buscar en el tarifario (opcional)` | `Buscar en tu tarifario` |
| 10 | `components/ServiceFormModal.jsx:865` | `Mas detalles del hotel (opcional)` | `Más detalles del hotel` |

### 4.B · Signo `¿` de apertura faltante

| # | Archivo:línea | ACTUAL | PROPUESTO |
|---|---|---|---|
| 11 | `features/reservas/pages/ReservaDetailPage.jsx:817` | `Estas seguro?` | `¿Seguro que querés seguir?` |
| 12 | `features/reservas/pages/ReservaDetailPage.jsx:1255` | `Eliminar reserva?` | `¿Eliminar la reserva?` |
| 13 | `features/reservas/pages/ReservaDetailPage.jsx:1263` | `Archivar reserva?` | `¿Archivar la reserva?` |
| 14 | `features/reservas/pages/ReservaDetailPage.jsx:2300` | `Eliminar pasajero?` | `¿Sacar al pasajero?` |
| 15 | `features/reservas/pages/ReservaDetailPage.jsx:2301` | `Estas seguro de eliminar este pasajero de la reserva?` | `¿Seguro que querés sacar a este pasajero de la reserva?` |
| 16 | `components/ReservaDocumentsTab.jsx:383` | `Eliminar documento?` | `¿Eliminar el documento?` |

### 4.C · Mayúsculas De Título Innecesarias

Regla: **mayúscula solo en la primera palabra y en nombres propios**. Los estados de
reserva (Presupuesto, Confirmada, En viaje…) sí van con mayúscula inicial porque son
nombres del sistema de estados.

| # | Archivo:línea | ACTUAL | PROPUESTO |
|---|---|---|---|
| 17 | `components/CreateReservaModal.jsx:101` | `Nuevo Presupuesto` | `Nuevo presupuesto` |
| 18 | `components/CreateReservaModal.jsx:121` | `Cliente Principal` | `Cliente` |
| 19 | `components/CreateReservaModal.jsx:185` | `Crear Presupuesto` | `Crear presupuesto` |
| 20 | `components/PassengerFormModal.jsx:398` | `Editar Pasajero` / `Nuevo Pasajero` | `Editar pasajero` / `Nuevo pasajero` |
| 21 | `components/PassengerFormModal.jsx:613` | `Guardar Pasajero` | `Guardar pasajero` |
| 22 | `components/PassengerFormModal.jsx:441` | `Tipo documento` | `Tipo de documento` |
| 23 | `components/PassengerFormModal.jsx:548` | `Fecha nacimiento` | `Fecha de nacimiento` |
| 24 | `components/PassengerFormModal.jsx:555` | `Vencimiento pasaporte` | `Vencimiento del pasaporte` |
| 25 | `features/reservas/components/PassengerList.jsx:189` | `Pasajeros del Viaje` | `Pasajeros del viaje` |
| 26 | `features/reservas/components/ServiceList.jsx:1140` | `Servicios Contratados` | `Servicios contratados` |
| 27 | `features/reservas/components/ServiceList.jsx:1230` | `Costo Neto` | `Costo neto` |
| 28 | `features/reservas/components/ServiceList.jsx:1231` | `Precio Venta` | `Precio de venta` |
| 29 | `features/reservas/components/ServiceList.jsx:1808` | `Precio Venta` | `Precio de venta` |
| 30 | `features/reservas/components/ReservaKPIs.jsx:18` | `Reservas Activas` | `Reservas activas` |
| 31 | `features/reservas/components/ReservaKPIs.jsx:32` | `Venta Total` | `Venta total` |
| 32 | `features/reservas/components/ReservaKPIs.jsx:40` | `Rentabilidad Est.` | `Rentabilidad estimada` |
| 33 | `features/reservas/components/ReservaKPIs.jsx:48` | `Por Cobrar` | `Por cobrar` |
| 34 | `features/reservas/components/EmitirFacturaInline.jsx:847` | `Nueva Factura` | `Nueva factura` |
| 35 | `features/reservas/components/EmitirFacturaInline.jsx:248` | `Servicios Turísticos` | `Servicios turísticos` |
| 36 | `features/reservas/components/RegistrarCobroInline.jsx:49-50` | labels `Tarjeta Crédito` / `Tarjeta Débito` | `Tarjeta de crédito` / `Tarjeta de débito` (values quedan) |
| 37 | `features/reservas/pages/ReservaDetailPage.jsx:2246` | `Agregar Servicio` | `Agregar servicio` |
| 38 | `components/ServiceFormModal.jsx:2469` | `Editar Servicio` / `Agregar Servicio` | `Editar servicio` / `Agregar servicio` |
| 39 | `components/ServiceFormModal.jsx:2602` | `Agregar Otra Hab.` | `Agregar otra habitación` |
| 40 | `components/ServiceFormModal.jsx:2609` | `Guardar Servicio` | `Guardar servicio` |
| 41 | `components/ServiceFormModal.jsx:1077 / 1081` | `Fecha Pick-up` / `Hora Pick-up` | `Fecha del pick-up` / `Hora del pick-up` |
| 42 | `components/ServiceFormModal.jsx:1100 / 510` | `Cantidad Pasajeros` | `Cantidad de pasajeros` |
| 43 | `components/ServiceFormModal.jsx:1116` | `Vuelo Asociado` | `Vuelo asociado` |
| 44 | `components/ServiceFormModal.jsx:1159 / 1170` | `Fecha Retorno` / `Hora Retorno` | `Fecha de retorno` / `Hora de retorno` |
| 45 | `components/ServiceFormModal.jsx:1359` | `Servicios Incluidos` | `Servicios incluidos` |
| 46 | `components/ServiceFormModal.jsx:1557` | `Destinos Cubiertos` | `Destinos cubiertos` |
| 47 | `components/ServiceFormModal.jsx:1572 / 1587` | `Vigencia Desde` / `Vigencia Hasta` | `Vigencia desde` / `Vigencia hasta` |
| 48 | `components/ServiceFormModal.jsx:1807 / 1808` | `Costo Neto` / `Precio Venta` | `Costo neto` / `Precio de venta` |
| 49 | `components/ServiceFormModal.jsx:398 / 413` | `Ciudad Origen` / `Ciudad Destino` | `Ciudad de origen` / `Ciudad de destino` |
| 50 | `components/ServiceFormModal.jsx:428 / 432 / 443 / 447` | `Fecha Salida` / `Hora Salida` / `Fecha Llegada` / `Hora Llegada` | `Fecha de salida` / `Hora de salida` / `Fecha de llegada` / `Hora de llegada` |
| 51 | `components/ServiceFormModal.jsx:499` | `Equipaje Incluido` | `Equipaje incluido` |
| 52 | `components/ReservaVoucherTab.jsx:28 / 32` | `Pendiente Autorización` / `Cargado Externo` | `Pendiente de autorización` / `Subido a mano` |
| 53 | `components/ReservaVoucherTab.jsx:1056` | `Autorización Comercial Requerida` | `Necesitás autorización` |
| 54 | `components/ReservaVoucherTab.jsx:1109 / 1119` | `Solicitar Autorización` / `Rechazar Autorización` | `Pedir autorización` / `Rechazar` |
| 55 | `components/ReservaVoucherTab.jsx:1157 / 1196` | `Anular Documento` | `Anular documento` |
| 56 | `components/ReservaVoucherTab.jsx:1253` | `Editar Documento Externo` | `Editar el documento` |
| 57 | `components/ReservaVoucherTab.jsx:1280` | `Reemplazar Archivo` | `Reemplazar el archivo` |
| 58 | `components/ReservaVoucherTab.jsx:1313` | `Guardar Cambios` | `Guardar cambios` |
| 59 | `components/ReservaVoucherTab.jsx:967 / 979 / 998` | `Generar Sistema` / `Subir Externo` / `Origen Externo` | `Generar` / `Subir un archivo` / `De dónde salió` |
| 60 | `features/reservas/pages/ReservaDetailPage.jsx:816` | `Confirmar accion` | `Confirmar` |

---

## Categoría 5 — Jerga técnica o texto que no habla el idioma del negocio

Ordenada por gravedad. Las 5 primeras son **bloqueantes** para el gate de
`data-exposure-reviewer`: exponen internos del sistema a un usuario no programador.

| # | Archivo:línea | ACTUAL | PROPUESTO | F/API | Gravedad |
|---|---|---|---|---|---|
| 1 | `components/ServiceFormModal.jsx:2534` | `Reserva en estado <b>{reservaStatus}</b>. La edicion economica queda bloqueada.` — `reservaStatus` es el enum crudo del backend (`Traveling`, `Closed`, `Confirmed`) | `Reserva <b>{traducirEstadoReserva(reservaStatus)}</b>: los precios y costos no se pueden cambiar.` | FRONT | **Crítico** |
| 2 | `TravelApi.Infrastructure/Services/ReservaService.cs:1903-1905` | `No se puede revertir desde {Status} a {TargetStatus}. Transiciones permitidas desde {Status}: Budget, InManagement…` | `Desde este estado no se puede volver a ese otro. Elegí una de las opciones que ofrece la pantalla.` | API | **Crítico** |
| 3 | `inline-service/AssistanceInlineForm.jsx:152`, `FlightInlineForm.jsx:128`, `HotelInlineForm.jsx:211`, `PackageInlineForm.jsx:131`, `TransferInlineForm.jsx:123` | `Operador sugerido (${String(form.supplierId).slice(0, 8)}…)` — muestra un ID interno recortado | `Operador sugerido` (sin el ID) | FRONT | **Crítico** |
| 4 | `components/ReservaDocumentsTab.jsx:419` | ``Error al descargar: ${error.message \|\| "Error desconocido"}`` — filtra el mensaje crudo de JS/red | `No se pudo descargar el documento. Probá de nuevo.` | FRONT | **Crítico** |
| 5 | `features/reservas/components/ServiceList.jsx:1217` | `No hay servicios cargados en este file.` — "file" no es palabra del producto | `Todavía no hay servicios en esta reserva. Cargá el primero para armar el viaje.` | FRONT | **Crítico** |
| 6 | `features/reservas/components/EmitirFacturaInline.jsx:974` | `Motivo del override` | `¿Por qué la emitís igual?` | FRONT | Importante |
| 7 | `features/reservas/components/EmitirFacturaInline.jsx:967` | `Confirmo que se emite AFIP con deuda pendiente.` | `Confirmo que emito la factura aunque el cliente deba plata.` | FRONT | Importante |
| 8 | `features/reservas/components/EmitirFacturaInline.jsx:939` | `AFIP bloqueado por deuda` / `Emisión por excepción habilitada` | `No se puede facturar: el cliente debe plata` / `Podés facturar igual, con motivo` | FRONT | Importante |
| 9 | `features/reservas/components/EmitirFacturaInline.jsx:942` | `La reserva todavía no está cancelada económicamente.` — "cancelada" choca con el vocabulario firmado (Cancelar ≠ Anular) | `Todavía queda saldo por cobrar en esta reserva.` | FRONT | Importante |
| 10 | `features/reservas/components/EmitirFacturaInline.jsx:1467` | `Emitir por excepción` | `Emitir igual` | FRONT | Menor |
| 11 | `features/reservas/components/EmitirFacturaInline.jsx:1137` | `Justificación del TC *` | `¿De dónde sale el tipo de cambio? *` | FRONT | Importante |
| 12 | `features/reservas/components/RegistrarCobroInline.jsx:522` | `Fecha del TC` | `Fecha del tipo de cambio` | FRONT | Importante |
| 13 | `features/reservas/components/RegistrarCobroInline.jsx:43` | `BCRA mayorista A3500` | `Mayorista (BCRA)` | FRONT | Menor |
| 14 | `features/reservas/pages/ReservaDetailPage.jsx:1227` | `Verifica que la URL sea correcta.` — "URL" | `Volvé al listado y entrá de nuevo.` | FRONT | Importante |
| 15 | `features/reservas/components/EditReservaDatesModal.jsx:107` | `…o queres un override manual.` | `…o querés fijarlas a mano.` | FRONT | Importante |
| 16 | `features/reservas/lib/reservationServiceModel.js:240` | `No se encontro el identificador publico del servicio.` | `No pudimos identificar el servicio. Recargá la pantalla y probá de nuevo.` | FRONT | Importante |
| 17 | `features/reservas/components/CancelarVariosServiciosInline.jsx:213` y `:512` | `Tipo de servicio no reconocido.` | `Este servicio no se puede anular desde acá. Anulalo desde su fila.` | FRONT | Importante |
| 18 | `features/reservas/components/CancelarVariosServiciosInline.jsx:506` | `Bloqueo fiscal — ${r.mensajeError}` | `No se pudo anular — ${r.mensajeError}` | FRONT | Importante |
| 19 | `components/ReservaDocumentsTab.jsx:410` | `El archivo descargado esta vacio o corrupto.` | `El archivo llegó incompleto. Probá descargarlo de nuevo.` | FRONT | Importante |
| 20 | `components/ReservaDocumentsTab.jsx:163` | `Error al abrir PDF` | `No se pudo abrir el documento` | FRONT | Menor |
| 21 | `components/PassengerFormModal.jsx:384` | fallback `Error desconocido` | `No se pudo guardar el pasajero. Probá de nuevo.` | FRONT | Importante |
| 22 | `features/reservas/pages/ReservaDetailPage.jsx:2065` | `conserva la trazabilidad de la gestion comercial que la genero` | `guarda el rastro de la gestión comercial que la originó` | FRONT | Menor |
| 23 | `components/ReservaVoucherTab.jsx:1163` y `:735` | `quedara trazable como anulado` / `se conservan solo como trazabilidad` | `queda anulado, con registro de quién y cuándo` / `quedan solo como registro` | FRONT | Menor |
| 24 | `components/ReservaTimeline.jsx:33` y `:40` | `Cargando historial operativo...` / `No hay movimientos operativos registrados.` | `Cargando el historial…` / `Todavía no pasó nada en esta reserva.` | FRONT | Menor |
| 25 | `TravelApi.Infrastructure/Services/ReservaService.cs:950` | `ServiceType no soportado.` — nombre de campo interno | `Ese tipo de servicio no está disponible.` | API | Importante |
| 26 | `TravelApi.Infrastructure/Services/ReservaService.cs:2275` | `No se pudo identificar el saldo a favor asociado a esta anulación. Contactá a soporte técnico antes de continuar.` | `No pudimos ubicar el saldo a favor de esta anulación. Avisale al administrador antes de seguir.` | API | Importante |
| 27 | `TravelApi.Infrastructure/Services/ReservaService.cs:1920` y `:1835` | `No se puede revertir (rompe la historia fiscal).` | `No se puede volver atrás: ya hay facturas emitidas.` | API | Importante |
| 28 | `TravelApi.Infrastructure/Services/BookingCancellationService.cs:7052` | `No se pudo completar la operacion. Volve a intentar.` | `No se pudo completar. Probá de nuevo.` | API | Menor |
| 29 | `TravelApi.Infrastructure/Services/BookingCancellationService.cs:12810` | `Anulacion rechazada: EnableNewCancellationFlow=false en este ambiente.` — nombre de flag interno | `Las anulaciones no están habilitadas en este sistema. Avisale al administrador.` | API | **Crítico** si es alcanzable desde la UI — verificar con backend-senior |

### 5.bis · Inconsistencias de glosario (misma cosa, dos nombres)

| # | Dónde | Problema | Decisión propuesta |
|---|---|---|---|
| A | `lib/reservaStatusLabels.js:27` dice `Perdido`; `MarkLostModal.jsx:34/48/56` dice `Perdida`; `ReservaDetailPage.jsx:1389` dice `Reserva perdida` | tres formas del mismo estado | Canónico: **Perdida** (concuerda con "reserva") |
| B | `lib/reservaStatusLabels.js:30` dice `Archivada`; `ReservaDetailPage.jsx:1264` dice `'Archivado'` | dos formas | Canónico: **Archivada** |
| C | `AssistanceInlineForm.jsx:86/93/152/253/255` dice **Proveedor**; `Flight/Hotel/Package/TransferInlineForm` dicen **Operador** | dos nombres para lo mismo dentro del mismo formulario | Canónico en el flujo: **Operador** |
| D | `EmitirFacturaInline.jsx:942` usa "cancelada económicamente"; el vocabulario firmado reserva "cancelar" para *abonar el total* | choque de vocabulario con `anular` | Reescribir sin "cancelar" (ver #9) |
| E | `RegistrarCobroInline.jsx:437` dice `Método`; el resto de la app dice `Forma de pago` | dos nombres | Definir con el dueño — **pendiente de decisión** |

---

## Tests que fijan literales (T-6) — actualizar EN EL MISMO commit

⚠ Jamás relajar un test: si el texto cambia, se actualiza el literal esperado.

| Texto que cambia | Test que lo fija |
|---|---|
| `Cotizacion` → `Cotización` (label de estado) | `src/TravelWeb/src/features/reservas/lib/reservaStatusLabels.test.mjs:15`<br>`src/TravelWeb/src/features/reservas/components/adr035FeedbackVisual.test.mjs:345` |
| `En gestion` → `En gestión` (label de estado) | `src/TravelWeb/src/features/reservas/lib/reservaStatusLabels.test.mjs:17`<br>`src/TravelWeb/src/features/reservas/components/adr035FeedbackVisual.test.mjs:347` |
| `…Pasala a En gestion primero.` (mensaje del motor) | `src/TravelWeb/src/features/reservas/components/adr035CapabilidadesYCobro.test.mjs:852, 863, 874`<br>comentario en `src/TravelApi.Tests/Unit/Adr032CollectableStateRuleTests.cs:176` |
| `pasajero(s)` → plural resuelto | `src/TravelWeb/src/features/reservas/lib/pasajeroHint.test.mjs:414, 417` |
| `Servicios Turísticos` → `Servicios turísticos` | `src/TravelWeb/src/features/reservas/components/emitirFacturaInline.test.mjs:813` |
| `Hay 3 pasajeros cargados…; quitá los que sobren…` | `src/TravelApi.Tests/Unit/ReservaServiceTests.cs:238` — **no cambia**, ya está en voseo |
| `No se puede facturar en este estado.` | `src/TravelWeb/src/features/reservas/components/emitirFacturaInline.test.mjs:929` — **no cambia** |
| `"Añadir Documento"` (solo en comentario) | `src/TravelWeb/src/features/reservas/components/estadosCongelados.test.mjs:409` — actualizar el comentario por prolijidad |

### Literales que NO se tocan (son datos, no copy)

| Literal | Dónde se fija | Motivo |
|---|---|---|
| `"Aereo"` | `reservationServiceModel.test.mjs:67`, `productSearchField.test.mjs:194` | clave de tipo de servicio persistida — solo se corrige el **label** |
| `"Media Pension"`, `"Pension Completa"` | `hotelInlineForm.test.mjs:115, 118, 119, 588, 595, 624, 628, 630` | valores canónicos guardados en base — solo se corrige el **label** |
| `"Deposito"` | `RegistrarCobroInline.jsx:52` (`value`) | valor de método de pago persistido |
| `"Tarjeta Crédito"`, `"Tarjeta Débito"` | `RegistrarCobroInline.jsx:49-50` (`value`) | idem |

---

## Los 10 peores

1. `components/ServiceFormModal.jsx:2534` — imprime el **estado crudo en inglés** del backend (`Traveling`, `Confirmed`) dentro de un aviso al usuario.
2. `TravelApi.Infrastructure/Services/ReservaService.cs:1903-1905` — devuelve al usuario la **lista de transiciones permitidas con los nombres internos** (`Budget, InManagement, Confirmed`).
3. `inline-service/*InlineForm.jsx` (×5) — muestran un **ID interno recortado**: `Operador sugerido (a3f9b21c…)`.
4. `components/ReservaDocumentsTab.jsx:419` — filtra el **mensaje crudo del error** de JS/red al toast.
5. `features/reservas/components/ServiceList.jsx:1217` — `"No hay servicios cargados en este file."` — palabra que no existe en el producto, en un estado vacío.
6. `components/CreateReservaModal.jsx:148 y :155` — `"Se usara para facturacion y contacto."` + `"Fecha de Inicio (Opcional)"`: dos tildes, tuteo y una leyenda prohibida en la primera pantalla del flujo.
7. `features/reservas/lib/reservaStatusLabels.js:21 y :23` — `"Cotizacion"` y `"En gestion"` sin tilde: es la **fuente única** de los estados, se replica en listado, ficha, badges y dashboards.
8. `features/reservas/pages/ReservaDetailPage.jsx:644-650` — `"Total: 1 pasajeros"` + `"Agrega servicios para validar capacidad"`: plural roto y tuteo juntos, en el widget que más se usa.
9. `components/ReservaVoucherTab.jsx` — bloque completo en tuteo + españolismo: `"Añadir Documento"`, `"Añade uno…"`, `"Selecciona el Supervisor..."`, `"Indica…"`, `"Puedes descargarlo"`, más 11 títulos en Title Case.
10. `features/reservas/components/EmitirFacturaInline.jsx:939-981` — `"Motivo del override"`, `"Confirmo que se emite AFIP con deuda pendiente"`, `"AFIP bloqueado por deuda"`: la pantalla de facturación habla en idioma de programador justo donde hay riesgo fiscal real.

---

## Requiere decisión del dueño

1. **`Método` vs `Forma de pago`** (`RegistrarCobroInline.jsx:437`): elegir uno para toda la app.
2. **`Perdido` vs `Perdida`**: propongo **Perdida** (concuerda con "reserva"). Toca el label del estado, el badge y el modal.
3. **`Emitir por excepción` → `Emitir igual`**: cambia el nombre de una acción con impacto fiscal. Recomiendo `Emitir igual` y dejar el detalle en el texto de ayuda, pero es su llamado.
4. **`Rentabilidad Est.` → `Rentabilidad estimada`**: entra en el ancho del KPI, verificar visualmente.
5. **`BCRA mayorista A3500` → `Mayorista (BCRA)`**: si el contador usa "A3500" en la conversación diaria, se deja como está.

## Requiere a `frontend-senior` (cambio de estructura, no solo texto)

1. **Pluralización** en `ReservaDetailPage.jsx:644, 647` y `CapacityWarning.jsx:22`: hoy es texto plano, hay que meter el ternario.
2. **`ServiceFormModal.jsx:2534`**: importar y usar `traducirEstadoReserva` desde `features/reservas/lib/reservaStatusLabels.js` (hoy el componente no lo importa).
3. **`inline-service/*InlineForm.jsx` (×5)**: sacar el `String(form.supplierId).slice(0, 8)` del template — es cambio de expresión, no solo de literal.
4. **`ReservaDocumentsTab.jsx:419`**: reemplazar la interpolación de `error.message` por un texto fijo.
5. **Quitar `(Opcional)`** de 10 labels: revisar que la jerarquía visual siga distinguiendo obligatorio de no obligatorio sin la leyenda (el asterisco `*` ya marca los obligatorios).

## Requiere a `backend-dotnet-senior`

- Las 21 correcciones de tilde y las 11 de voz en `ReservaService.cs`,
  `ReservaCapabilities.cs`, `Reserva.cs`, `ServiceResolutionRules.cs`,
  `PassengerNominalRules.cs` y `BookingService.Reschedule.cs`.
- Verificar si `BookingCancellationService.cs:12810`
  (`EnableNewCancellationFlow=false`) es alcanzable desde la UI de Reservas.
  Si lo es, es bloqueante del gate de exposición de datos.
