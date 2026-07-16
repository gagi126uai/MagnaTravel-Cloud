/**
 * Tests de lógica pura para la "Tanda A" (2026-07-16): un servicio ya ANULADO
 * (workflowStatus "Cancelado" del backend) no debe ofrecer Editar ni Borrar/Anular,
 * y su chip de estado debe decir "Anulado" (no "Cancelado").
 *
 * Motivo del fix: esServicioResuelto() no incluía 'Cancelado', así que un servicio
 * anulado cuyo confirmedAt seguía siendo null caía en la rama "borrador" y mostraba
 * los botones Editar/Borrar — pese a que el backend (ver DeleteGuards.cs) rechaza el
 * borrado físico de un servicio anulado porque puede tener multa, nota de crédito o
 * ajuste de tipo de cambio asociados.
 *
 * Cómo correr: node --test src/features/reservas/components/servicioAnuladoGuards.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de esServicioAnulado (ServiceList.jsx) ───────────────────────────

function esServicioAnulado(svc) {
    return (svc.workflowStatus || svc.status) === 'Cancelado';
}

// ─── Réplica de la visibilidad de Editar/Borrar-Anular (ServiceList.jsx) ──────
// Un servicio anulado NUNCA muestra ninguno de los dos botones, sin importar
// los permisos del usuario (puedeEditarServicios / puedeCancelarServicios).

function mostrarBotonEditar(svc, puedeEditarServicios) {
    return !esServicioAnulado(svc) && puedeEditarServicios;
}

function mostrarBotonDestructivo(svc, puedeEditarServicios, puedeCancelarServicios) {
    if (esServicioAnulado(svc)) return false;
    const esConfirmado = svc.workflowStatus === 'Confirmado' || svc.workflowStatus === 'Emitido';
    return esConfirmado ? puedeCancelarServicios : puedeEditarServicios;
}

// ─── Réplica de etiquetaEstadoServicio, solo el caso relevante acá ────────────

function etiquetaEstadoServicioCancelado(workflowStatus, reservaStatus) {
    if (reservaStatus === 'Lost' || reservaStatus === 'Cancelled') return 'Anulado';
    if (workflowStatus === 'Cancelado') return 'Anulado';
    return workflowStatus;
}

test("esServicioAnulado: workflowStatus 'Cancelado' → true", () => {
    assert.equal(esServicioAnulado({ workflowStatus: 'Cancelado' }), true);
});

test("esServicioAnulado: usa svc.status como fallback (mismo criterio que el resto del archivo)", () => {
    assert.equal(esServicioAnulado({ status: 'Cancelado' }), true);
});

test("esServicioAnulado: cualquier otro estado → false", () => {
    assert.equal(esServicioAnulado({ workflowStatus: 'Solicitado' }), false);
    assert.equal(esServicioAnulado({ workflowStatus: 'Confirmado' }), false);
    assert.equal(esServicioAnulado({ workflowStatus: null }), false);
});

test("BUG reproducido: un servicio anulado SIN confirmar por el operador ya no cae en 'borrador'", () => {
    // Antes del fix: esServicioResuelto({workflowStatus:'Cancelado'}) era false,
    // así que el sistema lo trataba como "todavía no confirmado" y ofrecía Borrar.
    const servicioAnuladoSinConfirmar = { workflowStatus: 'Cancelado' };
    assert.equal(mostrarBotonEditar(servicioAnuladoSinConfirmar, true), false);
    assert.equal(mostrarBotonDestructivo(servicioAnuladoSinConfirmar, true, true), false);
});

test("un servicio anulado NO muestra Editar aunque el usuario tenga permiso reservas.edit", () => {
    const svc = { workflowStatus: 'Cancelado' };
    assert.equal(mostrarBotonEditar(svc, true), false);
});

test("un servicio anulado NO muestra Borrar/Anular aunque el usuario tenga los dos permisos", () => {
    const svc = { workflowStatus: 'Cancelado' };
    assert.equal(mostrarBotonDestructivo(svc, true, true), false);
});

test("un servicio NO anulado y NO confirmado sigue mostrando Editar y Borrar (comportamiento previo intacto)", () => {
    const svc = { workflowStatus: 'Solicitado' };
    assert.equal(mostrarBotonEditar(svc, true), true);
    assert.equal(mostrarBotonDestructivo(svc, true, true), true); // usa puedeEditarServicios (Borrar)
});

test("un servicio confirmado (no anulado) sigue mostrando Editar y Anular (comportamiento previo intacto)", () => {
    const svc = { workflowStatus: 'Confirmado' };
    assert.equal(mostrarBotonEditar(svc, true), true);
    assert.equal(mostrarBotonDestructivo(svc, true, true), true); // usa puedeCancelarServicios (Anular)
});

test("chip: servicio individual anulado dentro de una reserva viva dice 'Anulado' (ya no 'Cancelado')", () => {
    assert.equal(etiquetaEstadoServicioCancelado('Cancelado', 'InManagement'), 'Anulado');
    assert.equal(etiquetaEstadoServicioCancelado('Cancelado', 'Confirmed'), 'Anulado');
});

test("chip: reserva entera Lost/Cancelled sigue diciendo 'Anulado' (sin regresión del ADR-036)", () => {
    assert.equal(etiquetaEstadoServicioCancelado('Solicitado', 'Lost'), 'Anulado');
    assert.equal(etiquetaEstadoServicioCancelado('Confirmado', 'Cancelled'), 'Anulado');
});
