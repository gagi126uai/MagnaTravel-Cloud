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
 */

const listeners = new Set();

let maintenanceState = {
  active: false,
  awaitingLocalResult: false,
  fechaResguardo: null,
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
  };
  emitChange();
}

export function deactivateMaintenance() {
  maintenanceState = { active: false, awaitingLocalResult: false, fechaResguardo: null };
  emitChange();
}

export function isMaintenanceActive() {
  return maintenanceState.active;
}
