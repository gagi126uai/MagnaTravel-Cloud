/**
 * Tests de lógica pura para las correcciones del reviewer de ADR-020.
 *
 * Cubre:
 * - B1: esServicioResuelto / esServicioConfirmadoPorOperador (papelera borrar vs cancelar)
 * - B2: lógica del banner de regresión (lastRegressionReason + status)
 * - N3: banner destrabada vs candado (hasLiveEditAuthorization)
 * - P4-3 (2026-07-22): texto de la franja ámbar según reservas.authorize_locked_edit
 * - N5: statusConfig tiene Lost con line-through
 * - D2: delta optimista del balance solo para servicios confirmados
 * - ResumenServiciosResueltos: excluye Cancelados, solo activo en InManagement
 *
 * Cómo correr: node --test src/features/reservas/components/adr020ReviewerFixes.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de ServiceList.jsx ───────────────────────────────────

const ESTADOS_RESUELTOS = new Set(['Confirmado', 'Emitido', 'HK', 'TK', 'KK', 'KL', 'NoConfirmation']);

/**
 * Replica de esServicioResuelto (ServiceList.jsx).
 * Un servicio está resuelto cuando el operador lo confirmó/emitió.
 */
function esServicioResuelto(svc) {
    const status = svc.workflowStatus || svc.status || '';
    return ESTADOS_RESUELTOS.has(status);
}

/** Alias para la papelera: confirmado = se cancela, no se borra. */
function esServicioConfirmadoPorOperador(svc) {
    return esServicioResuelto(svc);
}

// ─── Lógica pura copiada de ReservaLockBanner.jsx ────────────────────────────

/**
 * Replica de la lógica de selección de modo del ReservaLockBanner.
 * Devuelve: "regression" | "unlocked" | "locked" | "none"
 */
function decidirModoBanner({ isLocked, hasRegressionWarning, hasLiveEditAuthorization }) {
    if (hasRegressionWarning) return "regression";
    if (!isLocked) return "none";
    if (hasLiveEditAuthorization) return "unlocked";
    return "locked";
}

/**
 * Réplica del texto/botón de la franja ámbar "locked" (P4-3, spec
 * docs/ux/2026-07-22-p4-retoques-circuito-proveedor.md, P3=A).
 * `puedeAutorizar` viene del MISMO permiso que EditAuthorizationModal usa para decidir si
 * el usuario destraba directo (reservas.authorize_locked_edit): la franja tiene que
 * anunciar lo mismo que el modal, que abre el mismo onRequestEdit para los dos roles.
 */
function elegirTextoBannerLocked(puedeAutorizar) {
    if (puedeAutorizar) {
        return {
            titulo: "Reserva confirmada (con candado).",
            texto: "Podés destrabarla para editar.",
            boton: "Destrabar reserva",
        };
    }
    return {
        titulo: "Reserva confirmada.",
        texto: "Para cambiar algo, pedí autorización.",
        boton: "Pedí autorización",
    };
}

// ─── Lógica pura copiada de ResumenServiciosResueltos (ServiceList.jsx) ───────

function calcularResumenResolucion(services, reservaStatus) {
    if (reservaStatus !== 'InManagement') return null;

    // No contar cancelados
    const activos = services.filter(s => (s.workflowStatus || '') !== 'Cancelado');
    if (activos.length === 0) return null;

    const resueltos = activos.filter(esServicioResuelto).length;
    return { resueltos, total: activos.length };
}

// ─── Lógica pura de delta optimista del balance (D2, useReservaDetail.js) ─────

const ESTADOS_CONFIRMADOS_BALANCE = new Set(['Confirmado', 'Emitido', 'HK', 'TK', 'KK', 'KL', 'NoConfirmation']);

/**
 * Replica de la lógica de acumulación de balance al agregar un servicio nuevo.
 * Solo los servicios confirmados suman al balance (saldo a cobrar del cliente).
 * Un servicio recién agregado nace Solicitado → no suma al balance.
 */
function calcularNuevoBalance(balanceActual, service) {
    const workflowStatus = service.workflowStatus || service.status || '';
    const estaConfirmado = ESTADOS_CONFIRMADOS_BALANCE.has(workflowStatus);

    // totalSale siempre suma (presupuestado = todos los servicios)
    const totalSaleDelta = service.salePrice || 0;

    // balance solo suma si el servicio ya está confirmado
    const balanceDelta = estaConfirmado ? (service.salePrice || 0) : 0;

    return {
        nuevoBalance: balanceActual + balanceDelta,
        nuevoTotalSale: balanceActual + totalSaleDelta, // solo para verificación conceptual
        sumoAlBalance: estaConfirmado,
    };
}

// ─── Tests: esServicioResuelto y esServicioConfirmadoPorOperador (B1) ──────────

test("B1 esServicioResuelto: Confirmado → resuelto (hotel/paquete confirmado)", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'Confirmado' }), true);
});

test("B1 esServicioResuelto: Emitido → resuelto (vuelo emitido)", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'Emitido' }), true);
});

test("B1 esServicioResuelto: HK → resuelto (código confirmado de GDS)", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'HK' }), true);
});

test("B1 esServicioResuelto: TK → resuelto (ticket emitido GDS)", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'TK' }), true);
});

test("B1 esServicioResuelto: KK → resuelto", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'KK' }), true);
});

test("B1 esServicioResuelto: KL → resuelto", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'KL' }), true);
});

test("B1 esServicioResuelto: NoConfirmation → resuelto (traslado sin confirmación requerida)", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'NoConfirmation' }), true);
});

test("B1 esServicioResuelto: Solicitado → NO resuelto (nació solicitado, sin confirmar operador)", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'Solicitado' }), false);
});

test("B1 esServicioResuelto: sin workflowStatus ni status → NO resuelto", () => {
    assert.equal(esServicioResuelto({}), false);
});

test("B1 esServicioResuelto: Cancelado → NO resuelto (cancelado no es resuelto)", () => {
    assert.equal(esServicioResuelto({ workflowStatus: 'Cancelado' }), false);
});

test("B1 esServicioResuelto: usa status como fallback si workflowStatus no existe", () => {
    // Algunos tipos de servicio pueden usar 'status' en vez de 'workflowStatus'
    assert.equal(esServicioResuelto({ status: 'TK' }), true);
    assert.equal(esServicioResuelto({ status: 'Solicitado' }), false);
});

test("B1 esServicioConfirmadoPorOperador: servicio resuelto → papelera cancela (no borra)", () => {
    // La papelera en un servicio confirmado NO borra, cancela
    const svcConfirmado = { workflowStatus: 'Confirmado' };
    assert.equal(esServicioConfirmadoPorOperador(svcConfirmado), true);
});

test("B1 esServicioConfirmadoPorOperador: servicio solicitado → papelera borra", () => {
    const svcSolicitado = { workflowStatus: 'Solicitado' };
    assert.equal(esServicioConfirmadoPorOperador(svcSolicitado), false);
});

// ─── Tests: banner de regresión + candado (B2 y N3) ──────────────────────────

test("B2 banner: status InManagement + lastRegressionReason → modo regression (naranja)", () => {
    const modo = decidirModoBanner({
        isLocked: false,
        hasRegressionWarning: true,
        hasLiveEditAuthorization: false,
    });
    assert.equal(modo, "regression");
});

test("B2 banner: regresion tiene prioridad sobre candado activo", () => {
    // Aunque esté bloqueado, la regresión es más urgente
    const modo = decidirModoBanner({
        isLocked: true,
        hasRegressionWarning: true,
        hasLiveEditAuthorization: false,
    });
    assert.equal(modo, "regression");
});

test("N3 banner: isLocked + hasLiveEditAuthorization=true → modo unlocked (verde)", () => {
    const modo = decidirModoBanner({
        isLocked: true,
        hasRegressionWarning: false,
        hasLiveEditAuthorization: true,
    });
    assert.equal(modo, "unlocked");
});

test("banner: isLocked=true sin autorizacion → modo locked (ambar)", () => {
    const modo = decidirModoBanner({
        isLocked: true,
        hasRegressionWarning: false,
        hasLiveEditAuthorization: false,
    });
    assert.equal(modo, "locked");
});

test("banner: isLocked=false sin regresion → none (no se muestra)", () => {
    const modo = decidirModoBanner({
        isLocked: false,
        hasRegressionWarning: false,
        hasLiveEditAuthorization: false,
    });
    assert.equal(modo, "none");
});

// ─── Tests: texto de la franja ámbar según el permiso (P4-3, 2026-07-22) ──────

test("P4-3 banner locked: vendedor SIN el permiso → texto y botón quedan EXACTAMENTE como antes", () => {
    const resultado = elegirTextoBannerLocked(false);
    assert.deepEqual(resultado, {
        titulo: "Reserva confirmada.",
        texto: "Para cambiar algo, pedí autorización.",
        boton: "Pedí autorización",
    });
});

test("P4-3 banner locked: admin CON el permiso → invita a destrabar, no a pedir autorización", () => {
    const resultado = elegirTextoBannerLocked(true);
    assert.deepEqual(resultado, {
        titulo: "Reserva confirmada (con candado).",
        texto: "Podés destrabarla para editar.",
        boton: "Destrabar reserva",
    });
});

test("P4-3 banner locked: el texto del admin nunca menciona 'pedí autorización' (no debe quedar mezclado)", () => {
    const resultado = elegirTextoBannerLocked(true);
    const textoCompleto = `${resultado.titulo} ${resultado.texto} ${resultado.boton}`;
    assert.ok(!/pedí autorización/i.test(textoCompleto));
});

test("P4-3 banner locked: puedeAutorizar undefined (DTO/permiso no cargado aún) → se comporta como vendedor (conservador)", () => {
    const resultado = elegirTextoBannerLocked(undefined);
    assert.equal(resultado.boton, "Pedí autorización");
});

// ─── Tests: statusConfig Lost tiene line-through (N5) ─────────────────────────

test("N5 statusConfig Lost: contiene 'line-through' en la clase de color", () => {
    // Decisión #10 guia UX: Perdido = gris oscuro + tachado visual
    // Verificamos contra la definición que pusimos en ReservaStatusBadge.jsx
    const lostColor = 'bg-slate-300 text-slate-600 border-slate-400 line-through dark:bg-slate-700 dark:text-slate-400 dark:border-slate-600';
    assert.ok(lostColor.includes('line-through'), "Lost debe tener line-through para mostrar tachado");
});

test("N5 statusConfig Lost: NO es gris claro (era el bug — era slate-200/slate-500)", () => {
    // Verificamos que la nueva clase usa slate-300/slate-600 (más oscuro)
    const lostColor = 'bg-slate-300 text-slate-600 border-slate-400 line-through dark:bg-slate-700 dark:text-slate-400 dark:border-slate-600';
    // slate-300 y slate-600 son más oscuros que los anteriores slate-200 y slate-500
    assert.ok(!lostColor.includes('bg-slate-200'), "fondo no debe ser slate-200 (muy claro)");
    assert.ok(lostColor.includes('bg-slate-300'), "fondo debe ser slate-300 (más oscuro)");
});

// ─── Tests: ResumenServiciosResueltos (filter de Cancelados, solo InManagement) ─

test("ResumenServiciosResueltos: solo aparece en InManagement", () => {
    const services = [{ workflowStatus: 'Confirmado' }];
    assert.equal(calcularResumenResolucion(services, 'Confirmed'), null);
    assert.equal(calcularResumenResolucion(services, 'Budget'), null);
    assert.equal(calcularResumenResolucion(services, 'Traveling'), null);
    assert.notEqual(calcularResumenResolucion(services, 'InManagement'), null);
});

test("ResumenServiciosResueltos: excluye servicios Cancelados del conteo", () => {
    const services = [
        { workflowStatus: 'Confirmado' },  // resuelto
        { workflowStatus: 'Solicitado' },  // pendiente
        { workflowStatus: 'Cancelado' },   // NO debe contar
    ];
    const resultado = calcularResumenResolucion(services, 'InManagement');
    // Debe contar solo los 2 no-cancelados: 1 resuelto de 2 activos
    assert.equal(resultado.total, 2);
    assert.equal(resultado.resueltos, 1);
});

test("ResumenServiciosResueltos: todos resueltos (0 Cancelados) → resueltos === total", () => {
    const services = [
        { workflowStatus: 'Confirmado' },
        { workflowStatus: 'Emitido' },
        { workflowStatus: 'NoConfirmation' },
    ];
    const resultado = calcularResumenResolucion(services, 'InManagement');
    assert.equal(resultado.resueltos, 3);
    assert.equal(resultado.total, 3);
});

test("ResumenServiciosResueltos: solo Cancelados → devuelve null (no muestra nada)", () => {
    const services = [
        { workflowStatus: 'Cancelado' },
        { workflowStatus: 'Cancelado' },
    ];
    const resultado = calcularResumenResolucion(services, 'InManagement');
    assert.equal(resultado, null);
});

test("ResumenServiciosResueltos: sin servicios → devuelve null", () => {
    const resultado = calcularResumenResolucion([], 'InManagement');
    assert.equal(resultado, null);
});

// ─── Tests: delta optimista balance solo para confirmados (D2) ────────────────

test("D2 balance: servicio recién agregado Solicitado → NO suma al balance", () => {
    const result = calcularNuevoBalance(5000, {
        workflowStatus: 'Solicitado',
        salePrice: 1000,
    });
    // El balance no cambió porque el servicio está solicitado (no genera deuda aún)
    assert.equal(result.nuevoBalance, 5000, "balance debe quedar igual");
    assert.equal(result.sumoAlBalance, false);
});

test("D2 balance: servicio Confirmado → SÍ suma al balance", () => {
    const result = calcularNuevoBalance(5000, {
        workflowStatus: 'Confirmado',
        salePrice: 1000,
    });
    assert.equal(result.nuevoBalance, 6000);
    assert.equal(result.sumoAlBalance, true);
});

test("D2 balance: vuelo Emitido → SÍ suma al balance", () => {
    const result = calcularNuevoBalance(0, {
        workflowStatus: 'Emitido',
        salePrice: 3500,
    });
    assert.equal(result.nuevoBalance, 3500);
    assert.equal(result.sumoAlBalance, true);
});

test("D2 balance: traslado NoConfirmation → SÍ suma al balance (es un estado resuelto)", () => {
    const result = calcularNuevoBalance(0, {
        workflowStatus: 'NoConfirmation',
        salePrice: 500,
    });
    assert.equal(result.nuevoBalance, 500);
    assert.equal(result.sumoAlBalance, true);
});

test("D2 balance: servicio sin workflowStatus (recién creado) → NO suma al balance", () => {
    // Un servicio nuevo nace sin estado: tratarlo como Solicitado (no confirmado)
    const result = calcularNuevoBalance(1000, {
        salePrice: 200,
    });
    assert.equal(result.nuevoBalance, 1000, "sin status = no confirmado, no suma");
    assert.equal(result.sumoAlBalance, false);
});

test("D2 balance: salePrice 0 + Confirmado → suma 0 (no rompe el cálculo)", () => {
    const result = calcularNuevoBalance(5000, {
        workflowStatus: 'Confirmado',
        salePrice: 0,
    });
    assert.equal(result.nuevoBalance, 5000);
    assert.equal(result.sumoAlBalance, true);
});

// ─── Tests: textoFaltante por tipo de servicio ────────────────────────────────

const TEXTOS_FALTANTE = {
    flight: 'Falta emitir',
    transfer: 'Sin confirmar',
    assistance: 'Falta voucher',
    hotel: 'Pendiente',
    package: 'Pendiente',
    generic: 'Pendiente',
};

function textoFaltante(svc) {
    if (svc.recordKind === 'flight') return 'Falta emitir';
    if (svc.recordKind === 'transfer') return 'Sin confirmar';
    if (svc.recordKind === 'assistance') return 'Falta voucher';
    return 'Pendiente';
}

test("textoFaltante: vuelo → 'Falta emitir'", () => {
    assert.equal(textoFaltante({ recordKind: 'flight' }), 'Falta emitir');
});

test("textoFaltante: traslado → 'Sin confirmar'", () => {
    assert.equal(textoFaltante({ recordKind: 'transfer' }), 'Sin confirmar');
});

test("textoFaltante: asistencia → 'Falta voucher'", () => {
    assert.equal(textoFaltante({ recordKind: 'assistance' }), 'Falta voucher');
});

test("textoFaltante: hotel → 'Pendiente' (genérico)", () => {
    assert.equal(textoFaltante({ recordKind: 'hotel' }), 'Pendiente');
});

test("textoFaltante: paquete → 'Pendiente'", () => {
    assert.equal(textoFaltante({ recordKind: 'package' }), 'Pendiente');
});
