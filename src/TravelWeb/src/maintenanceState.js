import { useSyncExternalStore } from "react";

/**
 * Estado global "el sistema está en mantenimiento" (obra 2026-07-27, "Restaurar todo").
 * Mismo patrón que auth.js: un store fuera de React (a nivel de módulo) + useSyncExternalStore,
 * para que CUALQUIER parte de la app pueda prenderlo — incluido api.js, que no es un
 * componente y no puede usar Context/Provider.
 *
 * Se prende de DOS formas (ver App.jsx y RestoreBackupFicha.jsx):
 *  a) El admin que ejecuta "Restaurar todo" lo prende de entrada, apenas confirma la acción
 *     (no espera a que la API le devuelva un 503 para recién ahí avisar).
 *  b) api.js lo prende automáticamente cuando CUALQUIER pedido a la API responde 503 con
 *     code "MAINTENANCE" (para cualquier OTRO usuario que esté usando el sistema mientras
 *     dura la restauración).
 *
 * awaitingLocalResult: distingue el caso (a) del caso (b). Cuando es true, significa que
 * ESTA MISMA pestaña ya tiene un pedido en vuelo (el propio POST de "Restaurar todo") que
 * va a traer el resumen de qué se restauró apenas el motor responda. Por eso, si el sondeo
 * de MaintenanceScreen.jsx detecta que el sistema volvió ANTES que esa respuesta, NO hace
 * un reload duro (eso perdería el resumen que se está por mostrar): solo apaga el cartel y
 * deja que el pedido en vuelo termine su propio flujo. En cambio, para cualquier OTRO
 * usuario que llegó acá porque un pedido suyo chocó con el 503 (caso b), no hay ningún
 * resumen que mostrar — ahí sí conviene recargar entero apenas el sistema vuelve, para no
 * quedarse con pantallas armadas con datos de antes de la restauración.
 *
 * fechaResguardo (rediseño 2026-07-30 §4.5): SOLO quien dispara "Volver a esta copia" en
 * esta pestaña conoce, de antemano, la fecha del resguardo elegido (la vio en la lista antes
 * de tocar el botón) — por eso solo el caso (a) la manda. Cualquier OTRO usuario que cae acá
 * por el caso (b) nunca la tiene: MaintenanceScreen.jsx muestra un título genérico en ese
 * caso, en vez de inventar o adivinar una fecha que no le consta.
 *
 * pedidoLocalPerdido (fix bug real, plan tanda F): cuando el propio POST de "Volver a esta
 * copia" se corta por un timeout de proxy (nginx del host, ver dangerRestoreLogic.js) o un
 * corte de red, RestoreBackupFicha.jsx NUNCA va a recibir el resumen de éxito (la promesa del
 * fetch ya rechazó, no hay reintento) — aunque la restauración haya terminado bien de fondo.
 * En ese caso, aunque `awaitingLocalResult` sea true, esta pestaña YA NO tiene forma de
 * mostrar el resumen prometido: hay que tratarla como al usuario pasivo (reload duro apenas
 * el sistema vuelva) en vez de solo apagar el cartel y dejar la SPA con datos viejos.
 */

const listeners = new Set();

let maintenanceState = {
  active: false,
  awaitingLocalResult: false,
  fechaResguardo: null,
  pedidoLocalPerdido: false,
};

function emitChange() {
  for (const listener of listeners) {
    listener();
  }
}

function subscribe(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

function getSnapshot() {
  return maintenanceState;
}

export function useMaintenanceState() {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot);
}

export function activateMaintenance({ awaitingLocalResult = false, fechaResguardo = null } = {}) {
  maintenanceState = {
    active: true,
    // OR, nunca sobreescribir con AND: si esta pestaña ya estaba esperando el resumen de
    // SU propio pedido (awaitingLocalResult=true), un aviso genérico de OTRO pedido que
    // chocó con el 503 en el medio (ej. un fetch de background de otra pantalla abierta)
    // no puede "bajar" esa bandera y hacer que después se dispare un reload de más.
    awaitingLocalResult: maintenanceState.awaitingLocalResult || awaitingLocalResult,
    // Mismo criterio: no pisar con null una fecha que ya se había guardado.
    fechaResguardo: fechaResguardo || maintenanceState.fechaResguardo,
    // OJO: acá NO se reinicia la bandera, se conserva el valor que ya tenía (mismo criterio
    // que las dos de arriba). Solo se limpia en deactivateMaintenance() (el sistema volvió
    // y se hizo un reload duro) o al recargar la pestaña. activateMaintenance() nunca la pone
    // en true por su cuenta: eso lo hace únicamente marcarPedidoLocalPerdido(), llamada por
    // RestoreBackupFicha.jsx ante el corte de proxy/red.
    pedidoLocalPerdido: maintenanceState.pedidoLocalPerdido,
  };
  emitChange();
}

/**
 * Fix bug real (plan tanda F): marca que el pedido de "Volver a esta copia" de ESTA pestaña
 * se perdió por un corte de proxy/red (ver debeSeguirEsperandoTrasErrorDeRestoreTotal en
 * dangerRestoreLogic.js) — la promesa del fetch ya rechazó y no hay reintento, así que
 * RestoreBackupFicha.jsx nunca va a poder mostrar el resumen de éxito aunque la restauración
 * haya terminado bien de fondo. MaintenanceScreen.jsx usa esta bandera para decidir: cuando
 * el sistema vuelva, hacer un reload duro (como al usuario pasivo) en vez de solo apagar el
 * cartel y dejar la pantalla con datos de la base vieja.
 */
export function marcarPedidoLocalPerdido() {
  maintenanceState = { ...maintenanceState, pedidoLocalPerdido: true };
  emitChange();
}

export function deactivateMaintenance() {
  maintenanceState = { active: false, awaitingLocalResult: false, fechaResguardo: null, pedidoLocalPerdido: false };
  emitChange();
}

export function isMaintenanceActive() {
  return maintenanceState.active;
}
