/**
 * Tests de lógica pura para calculateServiciosCanceladosResumen.
 *
 * Esta función calcula el resumen "N de M servicios cancelados" que aparece en el
 * ReservaHeader (ADR-025 DT.3.1 decisión #1).
 *
 * Se testea la lógica pura (sin DOM) para garantizar que:
 *   - Con 0 servicios cancelados no muestra nada (el header lo filtra con cancelados > 0).
 *   - Solo cuentan los servicios con proveedor o de tipo específico.
 *   - El conteo de cancelados y total son correctos.
 *
 * Cómo correr: node --test src/features/reservas/components/serviciosCanceladosResumen.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de ServiceList.jsx ───────────────────────────────────
// (Copiada en vez de importada porque el runner es Node puro sin bundler.
//  Si cambia la función original, actualizar acá también.)

function calculateServiciosCanceladosResumen(services) {
    const serviciosConProveedor = (services || []).filter(svc => {
        const tieneProveedor = Boolean(svc.supplierPublicId || svc.supplierId || svc.supplierName);
        const esTipoEspecifico = svc.recordKind && svc.recordKind !== 'generic';
        return tieneProveedor || esTipoEspecifico;
    });

    const cancelados = serviciosConProveedor.filter(
        svc => (svc.workflowStatus || svc.status) === 'Cancelado'
    ).length;

    return {
        cancelados,
        totalConProveedor: serviciosConProveedor.length,
    };
}

// ─── Tests ────────────────────────────────────────────────────────────────────

test("sin servicios → cancelados=0, total=0", () => {
    const resultado = calculateServiciosCanceladosResumen([]);
    assert.deepStrictEqual(resultado, { cancelados: 0, totalConProveedor: 0 });
});

test("null → no lanza, devuelve 0/0", () => {
    const resultado = calculateServiciosCanceladosResumen(null);
    assert.deepStrictEqual(resultado, { cancelados: 0, totalConProveedor: 0 });
});

test("ningún servicio cancelado → cancelados=0", () => {
    const services = [
        { recordKind: "hotel", workflowStatus: "Confirmado", supplierName: "Hilton" },
        { recordKind: "flight", workflowStatus: "Emitido", supplierName: "Aerolíneas" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.cancelados, 0);
    assert.equal(resultado.totalConProveedor, 2);
});

test("un servicio cancelado de 3 → cancelados=1, total=3", () => {
    const services = [
        { recordKind: "hotel", workflowStatus: "Cancelado", supplierName: "Hilton" },
        { recordKind: "flight", workflowStatus: "Emitido", supplierName: "Aerolíneas" },
        { recordKind: "transfer", workflowStatus: "Confirmado", supplierName: "Bus" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.cancelados, 1);
    assert.equal(resultado.totalConProveedor, 3);
});

test("todos cancelados → cancelados === total", () => {
    const services = [
        { recordKind: "hotel", workflowStatus: "Cancelado", supplierName: "A" },
        { recordKind: "hotel", workflowStatus: "Cancelado", supplierName: "B" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.cancelados, resultado.totalConProveedor);
    assert.equal(resultado.cancelados, 2);
});

test("servicio genérico SIN proveedor no cuenta en el total", () => {
    const services = [
        // Genérico sin proveedor: no se puede cancelar por el candado fiscal
        { recordKind: "generic", workflowStatus: "Cancelado" },
        // Hotel con proveedor: sí cuenta
        { recordKind: "hotel", workflowStatus: "Confirmado", supplierName: "Plaza" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.totalConProveedor, 1, "el genérico sin proveedor no cuenta");
    assert.equal(resultado.cancelados, 0);
});

test("servicio genérico CON proveedor sí cuenta en el total", () => {
    const services = [
        { recordKind: "generic", workflowStatus: "Cancelado", supplierName: "Proveedor genérico" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.totalConProveedor, 1);
    assert.equal(resultado.cancelados, 1);
});

test("tipo específico sin supplierName sí cuenta (es cancelable por tipo)", () => {
    // Un vuelo sin supplier poblado en el DTO sigue siendo un vuelo y cuenta
    const services = [
        { recordKind: "flight", workflowStatus: "Solicitado" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.totalConProveedor, 1);
});

test("'Cancelado' via svc.status también cuenta (campo alternativo)", () => {
    const services = [
        { recordKind: "hotel", status: "Cancelado", supplierName: "Hotel" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.cancelados, 1);
});

test("workflowStatus distinto de 'Cancelado' no cuenta como cancelado", () => {
    const services = [
        { recordKind: "hotel", workflowStatus: "Confirmado", supplierName: "Hotel" },
        { recordKind: "flight", workflowStatus: "Emitido", supplierName: "Aero" },
        { recordKind: "transfer", workflowStatus: "Solicitado", supplierName: "Bus" },
    ];
    const resultado = calculateServiciosCanceladosResumen(services);
    assert.equal(resultado.cancelados, 0);
    assert.equal(resultado.totalConProveedor, 3);
});
