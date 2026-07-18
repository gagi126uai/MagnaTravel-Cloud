/**
 * Tests de lógica pura para la Tanda 4 del modelo de estados derivados (2026-07-17),
 * spec docs/ux/2026-07-17-t4-estados-derivados-ficha-reserva.md, Puntos 1 y 4.
 *
 * Cubre, para la fila de un servicio ANULADO dentro de ServiceList.jsx:
 *   - Punto 1: los importes (Costo neto / Precio venta) se tachan con el MISMO
 *     estilo que ya usa el nombre del servicio (regla 6 del modelo de estados).
 *   - Punto 4: el badge "Operador impago"/"pagado" se apaga en un anulado, y en su
 *     lugar aparece la etiqueta "Con multa" (ámbar) / "✓ Multa cobrada" (gris) según
 *     `svc.cancellationPenaltyState` (campo nuevo que manda el backend por servicio).
 *
 * Réplicas de ServiceList.jsx / CancellationPenaltyLabel.jsx (JSX, no importable
 * directo por node --test; mismo patrón que servicioAnuladoGuards.test.mjs).
 *
 * Cómo correr: node --test src/features/reservas/components/t4TachadoYMultaPorServicio.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de esServicioAnulado (ServiceList.jsx) ───────────────────────────

function esServicioAnulado(svc) {
    return (svc.workflowStatus || svc.status) === 'Cancelado';
}

// ─── Réplica de las clases de las celdas Costo neto / Precio venta (ServiceList.jsx) ──
// Mismo patrón visual que ya usa el nombre del servicio anulado: line-through +
// text-slate-400 (Regla 6 del modelo de estados, 2026-07-17). En vivo, cada celda
// conserva el estilo que ya tenía (Costo neto gris tenue; Precio venta en negrita).

const TACHADO = 'line-through text-slate-400 dark:text-slate-500';

function claseCostoNeto(svc) {
    return esServicioAnulado(svc) ? TACHADO : 'text-slate-500';
}

function claseSalePrice(svc) {
    return esServicioAnulado(svc) ? TACHADO : 'font-bold text-slate-900 dark:text-white';
}

test("Costo neto: servicio anulado queda tachado (mismo estilo que el nombre)", () => {
    assert.equal(claseCostoNeto({ workflowStatus: 'Cancelado' }), TACHADO);
});

test("Costo neto: servicio vivo NO se tacha", () => {
    assert.equal(claseCostoNeto({ workflowStatus: 'Confirmado' }), 'text-slate-500');
});

test("Precio venta: servicio anulado queda tachado y pierde el font-bold del vivo", () => {
    const clase = claseSalePrice({ workflowStatus: 'Cancelado' });
    assert.equal(clase, TACHADO);
    assert.ok(!clase.includes('font-bold'), 'no debe conservar el énfasis fuerte del importe vivo');
});

test("Precio venta: servicio vivo mantiene el estilo fuerte de siempre", () => {
    assert.equal(claseSalePrice({ workflowStatus: 'Confirmado' }), 'font-bold text-slate-900 dark:text-white');
});

// ─── Fix B1 (review frontend 2026-07-17): rama de costo CONFIRMABLE ───────────
// ServiceList.jsx tiene DOS ramas para la celda de costo:
//   1) mostrarCosto && puedeConfirmarCosto && !isGeneric -> <CostConfirmCell> (interactiva).
//   2) mostrarCosto (sola) -> número simple (la rama que M3 ya cubría arriba).
// ANTES del fix B1, la rama (1) NO tenía tachado — un servicio anulado con el flag de catálogo
// ON + permiso de costos + tipo específico (hotel/aéreo/traslado/paquete/asistencia) mostraba el
// costo SIN tachar, un desvío de la spec Punto 1 ("sin excepción"). El fix envuelve esa celda con
// el MISMO condicional de tachado, sin tocar CostConfirmCell (que sigue permitiendo confirmar
// costo sobre un anulado — decisión del dueño ya documentada en ese componente, fuera de alcance
// de esta tanda).

function usaRamaCostoConfirmable(mostrarCosto, puedeConfirmarCosto, isGeneric) {
    return mostrarCosto && puedeConfirmarCosto && !isGeneric;
}

function claseCeldaCostoConfirmable(svc) {
    return esServicioAnulado(svc) ? TACHADO : '';
}

test("Rama costo confirmable: se activa con flag+permiso+tipo específico (no genérico)", () => {
    assert.equal(usaRamaCostoConfirmable(true, true, false), true);
    assert.equal(usaRamaCostoConfirmable(true, true, true), false); // genérico -> rama simple
    assert.equal(usaRamaCostoConfirmable(true, false, false), false); // sin flag/permiso -> rama simple
    assert.equal(usaRamaCostoConfirmable(false, true, false), false); // sin ver costo -> ninguna rama
});

test("B1: rama costo confirmable + servicio anulado -> la celda que la envuelve queda tachada", () => {
    // Este es EXACTAMENTE el caso que se escapaba antes del fix: usuario con permiso de costos +
    // flag de catálogo ON, mirando un servicio anulado de tipo específico.
    const svc = { workflowStatus: 'Cancelado', recordKind: 'hotel' };
    assert.ok(usaRamaCostoConfirmable(true, true, false));
    assert.equal(claseCeldaCostoConfirmable(svc), TACHADO);
});

test("Rama costo confirmable + servicio VIVO -> sin tachado (CostConfirmCell se ve igual que siempre)", () => {
    const svc = { workflowStatus: 'Confirmado', recordKind: 'hotel' };
    assert.equal(claseCeldaCostoConfirmable(svc), '');
});

test("Mobile: el <span> que envuelve CostConfirmCellMobile usa el mismo criterio que desktop", () => {
    // ServiceList.jsx envuelve <CostConfirmCellMobile> en un <span> con esta MISMA clase condicional
    // (mismo helper claseCeldaCostoConfirmable — no hay una versión mobile distinta del criterio).
    assert.equal(claseCeldaCostoConfirmable({ workflowStatus: 'Cancelado' }), TACHADO);
    assert.equal(claseCeldaCostoConfirmable({ workflowStatus: 'Confirmado' }), '');
});

// ─── Réplica de CancellationPenaltyLabel.jsx ──────────────────────────────────

function textoEtiquetaMulta(cancellationPenaltyState) {
    if (cancellationPenaltyState === 'Pending') return 'Con multa';
    if (cancellationPenaltyState === 'Collected') return '✓ Multa cobrada';
    return null;
}

function toneEtiquetaMulta(cancellationPenaltyState) {
    if (cancellationPenaltyState === 'Pending') return 'amber';
    if (cancellationPenaltyState === 'Collected') return 'gray';
    return null;
}

test("Con multa: cancellationPenaltyState 'Pending' -> texto 'Con multa', tono ámbar", () => {
    assert.equal(textoEtiquetaMulta('Pending'), 'Con multa');
    assert.equal(toneEtiquetaMulta('Pending'), 'amber');
});

test("Multa cobrada: cancellationPenaltyState 'Collected' -> texto '✓ Multa cobrada', tono gris (NO desaparece)", () => {
    assert.equal(textoEtiquetaMulta('Collected'), '✓ Multa cobrada');
    assert.equal(toneEtiquetaMulta('Collected'), 'gray');
});

test("Sin multa: cancellationPenaltyState null/undefined -> no se muestra nada", () => {
    assert.equal(textoEtiquetaMulta(null), null);
    assert.equal(textoEtiquetaMulta(undefined), null);
});

test("La etiqueta de multa NUNCA muestra un monto (regla P4=B, 2026-06-21, reusada acá)", () => {
    // Los dos textos posibles son literales fijos, sin interpolación de números.
    assert.ok(!/[0-9]/.test(textoEtiquetaMulta('Pending')));
    assert.ok(!/[0-9]/.test(textoEtiquetaMulta('Collected')));
});

// ─── Réplica del gating: badge de operador vs. etiqueta de multa (ServiceList.jsx) ──
// Regla 6/7 del modelo (2026-07-17): en un servicio ANULADO el badge de pago al
// operador se apaga SIEMPRE (ya no reporta "impago"/"pagado"); en su lugar, si dejó
// multa, aparece la etiqueta nueva. Un servicio VIVO sigue mostrando el badge de
// siempre y NUNCA la etiqueta de multa (esa es exclusiva de anulados).

function muestraBadgeOperador(svc) {
    return !esServicioAnulado(svc);
}

function muestraEtiquetaMulta(svc) {
    return esServicioAnulado(svc);
}

test("Servicio anulado: el badge 'Operador impago/pagado' NO se muestra", () => {
    const svc = { workflowStatus: 'Cancelado' };
    assert.equal(muestraBadgeOperador(svc), false);
});

test("Servicio anulado CON cargo real de operador (T2): igual se apaga el badge de operador (P4 spec)", () => {
    // Aunque T2 pudo dejar un cargo real de operador imputado a este servicio, el badge
    // de "pago al operador" sigue siendo exclusivo de servicios VIVOS: la spec T4 dice
    // "NO se muestra en un servicio anulado" sin excepción — el aviso de esa multa lo
    // da la etiqueta nueva "Con multa", no el badge viejo.
    const svc = { workflowStatus: 'Cancelado' };
    assert.equal(muestraBadgeOperador(svc), false);
    assert.equal(muestraEtiquetaMulta(svc), true);
});

test("Servicio vivo: se muestra el badge de operador, nunca la etiqueta de multa", () => {
    const svc = { workflowStatus: 'Confirmado' };
    assert.equal(muestraBadgeOperador(svc), true);
    assert.equal(muestraEtiquetaMulta(svc), false);
});

// ─── Punto 4: sin avisos de "Próximos inicios" sobre un anulado (ya vigente, blindado) ──
// UpcomingStartPill.jsx ya devuelve "—" (o null en mobile) cuando workflowStatus es
// "Cancelado" — este test documenta el contrato para que T4 no lo regresione.

function muestraUpcomingStartPill(svc) {
    return svc.workflowStatus !== 'Cancelado';
}

test("Sin aviso de próximo inicio sobre un servicio anulado", () => {
    assert.equal(muestraUpcomingStartPill({ workflowStatus: 'Cancelado' }), false);
});

test("El aviso de próximo inicio sigue disponible sobre un servicio vivo", () => {
    assert.equal(muestraUpcomingStartPill({ workflowStatus: 'Confirmado' }), true);
});
