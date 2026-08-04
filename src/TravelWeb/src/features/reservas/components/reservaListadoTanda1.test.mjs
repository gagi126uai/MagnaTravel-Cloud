/**
 * Tanda 1 rediseño del listado de Reservas (2026-08-04, plan B4/B9).
 *
 * ReservaTable.jsx/ReservaMobileList.jsx/ReservaStatusBadge.jsx tienen JSX, así que
 * (mismo patrón que ya usan adr035FeedbackVisual.test.mjs y candadoEdicionC1.test.mjs
 * en esta carpeta) acá se replica la lógica de decisión PURA de esos componentes,
 * sin importar el archivo .jsx.
 *
 * Cubre dos reglas de la constitución que están firmadas para el listado:
 *   - P-9/P-13⭐: el botón "Archivar" bloqueado muestra el motivo del motor escrito
 *     debajo, tal cual — a diferencia de la ficha (ReservaHeader.jsx), donde ese
 *     mismo motivo NO se muestra (decisión de 2026-06-19, sigue vigente ahí).
 *   - El candado 🔒 del chip de estado solo aparece en "Confirmada", y solo cuando
 *     el llamador lo pide explícitamente (mostrarCandado=true) — el listado de
 *     Reservas es el único que lo prende hoy.
 *
 * Cómo correr: node --test src/features/reservas/components/reservaListadoTanda1.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Replica de ReservaStatusBadge.jsx: cuándo se agrega el candado 🔒 ─────────

function resolverCandado(status, mostrarCandado) {
  return Boolean(mostrarCandado) && status === "Confirmed";
}

test("candado: mostrarCandado=true + status Confirmed -> se muestra", () => {
  assert.equal(resolverCandado("Confirmed", true), true);
});

test("candado: mostrarCandado=true + otro status -> NO se muestra (solo aplica a Confirmada)", () => {
  assert.equal(resolverCandado("Traveling", true), false);
  assert.equal(resolverCandado("Closed", true), false);
  assert.equal(resolverCandado("Budget", true), false);
});

test("candado: mostrarCandado=false (default) -> nunca se muestra, aunque el status sea Confirmed", () => {
  assert.equal(resolverCandado("Confirmed", false), false);
  assert.equal(resolverCandado("Confirmed", undefined), false);
});

// ─── Replica de ReservaTable.jsx/ReservaMobileList.jsx: motivo de Archivar ─────

/**
 * A diferencia de la ficha (ReservaHeader.jsx, feedback 2026-06-19: "sin texto de
 * motivo debajo"), el listado SÍ muestra el motivo tal cual lo manda el motor
 * (P-9/P-13⭐: un botón vedado tiene que decir por qué, a la vista, nunca solo en
 * un tooltip). Esta función replica esa decisión de render.
 */
function resolverBloqueArchivar(archiveBlockReason) {
  const canArchive = !archiveBlockReason;
  return {
    botonHabilitado: canArchive,
    muestraMotivoDebajo: !canArchive,
    textoMotivo: canArchive ? null : archiveBlockReason,
  };
}

test("motivo de archivar: sin bloqueo -> botón habilitado, sin texto debajo", () => {
  const resultado = resolverBloqueArchivar(null);
  assert.deepEqual(resultado, { botonHabilitado: true, muestraMotivoDebajo: false, textoMotivo: null });
});

test("motivo de archivar: bloqueado -> botón deshabilitado Y el motivo del motor se muestra tal cual (P-13⭐)", () => {
  const motivo = "No se puede archivar una reserva con saldo pendiente.";
  const resultado = resolverBloqueArchivar(motivo);
  assert.deepEqual(resultado, { botonHabilitado: false, muestraMotivoDebajo: true, textoMotivo: motivo });
});
