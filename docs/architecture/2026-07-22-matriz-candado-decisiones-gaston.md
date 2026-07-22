# Matriz del candado — decisiones firmadas por Gaston (2026-07-22)

> Origen: probada en PROD (reserva 1052) + auditoría completa pantalla-vs-motor
> de la ficha de reserva en Confirmada+candado. Gaston firmó las 4 decisiones
> (eligió la recomendada en todas). Esta matriz es la fuente de verdad para la
> obra "candado coherente" y entra a la Constitución del producto.

## Decisiones firmadas

**C1 (P1=A) — Botones de edición con candado: grises + destrabar al tocar.**
Editar fechas, Reprogramar viaje, Agregar/Editar/Borrar servicio, editar
identidad cargada y eliminar pasajero: con la reserva Confirmada sin
autorización viva se muestran APAGADOS con candadito; al tocarlos se abre la
ventana de destrabar (EditAuthorizationModal, motivo + 30 minutos). Con
autorización viva se prenden normales (la señal hasLiveEditAuthorization ya
viaja en el DTO; hoy ningún botón la lee).

**C2 (P2=A) — Anular servicio / Anular varios pasan BAJO CANDADO.**
Cierra el bypass real: BookingCancellationService.CancelServiceAsync no
consulta ReservaLockGuard (la constante ServiceCancelled del catálogo quedó
sin cablear; el comentario de la decisión #8 en BookingService.cs:2415-2416
prometía lo contrario). Con candado piden destrabar primero (mismo
tratamiento C1); destrabada, corren los frenos fiscales de siempre.

**C3 (P3=A) — Anular la RESERVA entera sigue disponible sin candado.**
Es un circuito propio con frenos fiscales propios (factura/NC/R1); en los ERP
anular un documento confirmado es una operación con autorización propia, no
una edición. El candado protege EDICIONES.

**C4 (P4=A) — Zonas grises: confirmar costo BAJO candado; documentos y
reparto de pasajeros LIBRES.** "Confirmar costo" toca plata de una reserva
bloqueada → pide destrabar. Adjuntos y asignación de pasajeros a servicios
son trabajo operativo diario sin plata → libres, y la decisión queda escrita
(corregir la doc de ReservaLockGuard.cs:10-12 que menciona adjuntos como
cubiertos: la doc miente, el código queda como está).

## Quedan como están (fundamento documentado, ratificado por C3)

Cobrar, facturar, emitir voucher, emitir/anular comprobantes (trabajo normal
de una Confirmada; ADR-033/036/037) · marcar confirmado/emitido por el
operador (decisión #8 ADR-020: refleja lo que pasó afuera) · alta de pasajero
y completar identidad vacía (exención anti-callejón documentada).

## Duplicados (a resolver con gate UX, decisión de forma pendiente)

"Anular" ×2 (header + barra Estado de Cuenta, mismo data-testid) · papelera
por fila vs "Anular varios" (mismo endpoint en loop) · tres botones con la
palabra "Anular" a centímetros · "Editar fechas" vs "Reprogramar" juntos ·
candado anunciado por 3 canales.

## Referencias técnicas (de la auditoría)

Candado: ReservaLockGuard.cs:34-37 · capacidades sin candado:
ReservaCapabilities.cs (Confirmed → todo Yes) · bypass anulación:
BookingCancellationService.CancelServiceAsync:402 y
CancelarVariosServiciosInline.jsx:205-232 · constante huérfana:
ReservaEditAuthorization.cs:106 · confirm-cost sin gates:
BookingService.ConfirmCost.cs · autorización: ReservaService.cs:1888-1968
(30 min, por reserva, permiso reservas.authorize_locked_edit).
