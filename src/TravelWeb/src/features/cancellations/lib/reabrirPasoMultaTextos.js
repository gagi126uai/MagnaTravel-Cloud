/**
 * Textos del "Reabrir el paso de la multa" — el panel que un ADMINISTRADOR usa para
 * deshacer un cierre "sin multa" del operador (spec
 * `docs/ux/2026-07-14-config-multas-proveedor.md`, Pieza 3, 2026-07-14).
 *
 * Por qué es un archivo aparte (y no texto suelto en el JSX): así el enlace del cartel
 * rosa "Anulada — cerrada sin multa del operador" (ReservaDetailPage.jsx) y el panel que
 * se abre al clickearlo (DeshacerCierreSinMultaInline.jsx) usan SIEMPRE la MISMA frase —
 * nunca pueden divergir entre sí — y los textos se pueden testear sin renderizar ningún
 * componente (mismo criterio que penaltyCrossCurrency.js).
 *
 * Qué cambió (auditado en código, 2026-07-14): antes el enlace decía "Deshacer: el
 * operador sí cobró una multa" — un texto que ADIVINABA el motivo (a veces el admin lo
 * reabre por otra razón, ej. una corrección administrativa) y no aclaraba que, en este
 * caso puntual, nunca hubo ningún comprobante emitido. La visibilidad admin-only YA
 * estaba resuelta antes de esta tanda (gate `isAdmin()` en ReservaDetailPage.jsx) — acá
 * SOLO se toca el texto.
 *
 * OJO — NO confundir con el OTRO "Deshacer" (ADR-044 "Deshacer una multa ya emitida",
 * 2026-07-14): ese es un cartel VERDE, para una multa que SÍ tiene un comprobante con CAE
 * emitido, con su propio texto ("Deshacer: el operador cobró mal esta multa") y su propia
 * lógica en `undoDebitNoteLogic.js` — ese NO se toca acá. Este archivo es solo para el
 * cierre "sin multa" (cartel ROSA, nunca hubo comprobante porque nunca se cobró nada).
 */

/** Enlace discreto dentro del cartel rosa "Anulada — cerrada sin multa del operador". */
export const ENLACE_REABRIR_PASO_MULTA = "Reabrir el paso de la multa";

/** Cabecera del panel que se abre al clickear el enlace de arriba. */
export const TITULO_PANEL_REABRIR_PASO_MULTA = "Reabrir el paso de la multa";

/**
 * Explicación de la consecuencia, mostrada ANTES de pedir el motivo (primera pantalla
 * del panel). Deja claro dos cosas: a qué pregunta se vuelve, y que no hay ningún
 * comprobante fiscal en juego (tranquiliza al admin sobre el riesgo real de la acción).
 */
export const EXPLICACION_REABRIR_PASO_MULTA =
  "Volvés a la pregunta '¿el operador cobró una multa?'. No se toca ningún comprobante: este cierre nunca emitió ninguno.";

/**
 * Texto de la confirmación explícita (segunda pantalla del panel, "E2" del flujo
 * original 2026-07-08) — misma idea que `EXPLICACION_REABRIR_PASO_MULTA`, pero
 * mencionando el número de reserva para que quede claro sobre QUÉ reserva se está por
 * actuar (patrón ya usado en el resto de las confirmaciones de dos pasos de la ficha).
 *
 * @param {string|number} reservaNumero
 * @returns {string}
 */
export function textoConfirmacionReabrirPasoMulta(reservaNumero) {
  return (
    `Volvés a la pregunta '¿el operador cobró una multa?' de la reserva ${reservaNumero}. ` +
    "No se toca ningún comprobante: este cierre nunca emitió ninguno."
  );
}
