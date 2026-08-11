/**
 * Tanda 1 rediseño del listado de Reservas (2026-08-04, plan B4/B9).
 *
 * ReservaTable.jsx/ReservaMobileList.jsx/ReservaStatusBadge.jsx tienen JSX, así que
 * (mismo patrón que ya usan adr035FeedbackVisual.test.mjs y candadoEdicionC1.test.mjs
 * en esta carpeta) acá se replica la lógica de decisión PURA de esos componentes,
 * sin importar el archivo .jsx.
 *
 * Cubre dos reglas de la constitución que están firmadas para el listado:
 *   - P-9 (enmendada 11/08/2026 tras el review B1/B2 de esta misma tanda): el botón
 *     "Archivar" bloqueado muestra el motivo del motor, pero el CÓMO depende del
 *     dispositivo — en escritorio (ReservaTable.jsx) va de globito (title nativo) al
 *     pasar el mouse, sobre un <span> que ENVUELVE al botón (los botones deshabilitados
 *     no disparan hover); en táctil (ReservaMobileList.jsx) no hay hover, así que ahí
 *     sigue ESCRITO a la vista, debajo del botón, como texto plano.
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

// ─── Replica de ReservaTable.jsx (escritorio): motivo de Archivar en TOOLTIP ───

/**
 * Escritorio (ReservaTable.jsx): el motivo del motor va en el `title` de un <span>
 * que ENVUELVE al botón (fix B1, review 11/08/2026) — un <button disabled> nunca
 * dispara hover, así que un title puesto directo en él no se vería jamás.
 */
function resolverBloqueArchivarEscritorio(archiveBlockReason) {
  const canArchive = !archiveBlockReason;
  return {
    botonHabilitado: canArchive,
    // undefined (no null) cuando SÍ se puede archivar: el envoltorio no debe mostrar
    // un tooltip vacío al pasar el mouse.
    tituloDelEnvoltorio: canArchive ? undefined : archiveBlockReason,
  };
}

test("escritorio: sin bloqueo -> botón habilitado, sin tooltip", () => {
  const resultado = resolverBloqueArchivarEscritorio(null);
  assert.deepEqual(resultado, { botonHabilitado: true, tituloDelEnvoltorio: undefined });
});

test("escritorio: bloqueado -> botón deshabilitado Y el motivo del motor queda en el title del envoltorio (tooltip)", () => {
  const motivo = "No se puede archivar una reserva con saldo pendiente.";
  const resultado = resolverBloqueArchivarEscritorio(motivo);
  assert.deepEqual(resultado, { botonHabilitado: false, tituloDelEnvoltorio: motivo });
});

// ─── Replica de ReservaMobileList.jsx (táctil): motivo de Archivar ESCRITO ─────

/**
 * Táctil (ReservaMobileList.jsx, fix B2, review 11/08/2026): sin hover, un tooltip
 * nunca se vería — el motivo sigue escrito a la vista, debajo del botón, como texto
 * plano (mismo criterio que regía desde 2026-08-04, sin cambios acá).
 */
function resolverBloqueArchivarTactil(archiveBlockReason) {
  const canArchive = !archiveBlockReason;
  return {
    botonHabilitado: canArchive,
    muestraMotivoDebajo: !canArchive,
    textoMotivo: canArchive ? null : archiveBlockReason,
  };
}

test("táctil: sin bloqueo -> botón habilitado, sin texto debajo", () => {
  const resultado = resolverBloqueArchivarTactil(null);
  assert.deepEqual(resultado, { botonHabilitado: true, muestraMotivoDebajo: false, textoMotivo: null });
});

test("táctil: bloqueado -> botón deshabilitado Y el motivo del motor se muestra escrito debajo (sin hover disponible)", () => {
  const motivo = "No se puede archivar una reserva con saldo pendiente.";
  const resultado = resolverBloqueArchivarTactil(motivo);
  assert.deepEqual(resultado, { botonHabilitado: false, muestraMotivoDebajo: true, textoMotivo: motivo });
});
