/**
 * Tests de lógica pura para el cruce servicio <-> estado de pago al operador.
 *
 * Cómo correr: node --test src/features/reservas/lib/supplierPaymentStatusLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    buscarEstadoPagoServicio,
    resolverEtiquetaEstadoPago,
    puedenVerseMontos,
} from "./supplierPaymentStatusLogic.js";

// ─── buscarEstadoPagoServicio ─────────────────────────────────────────────────

test("buscarEstadoPagoServicio retorna el item correcto por recordKind + servicePublicId", () => {
    const statusDto = {
        amountsVisible: true,
        services: [
            { recordKind: "flight", servicePublicId: "aaaa-1111", status: "unpaid", netCost: 500 },
            { recordKind: "hotel", servicePublicId: "bbbb-2222", status: "paid", netCost: 300 },
        ],
    };

    const resultado = buscarEstadoPagoServicio("flight", "aaaa-1111", statusDto);

    assert.equal(resultado.status, "unpaid");
    assert.equal(resultado.netCost, 500);
});

test("buscarEstadoPagoServicio es case-insensitive en recordKind y publicId", () => {
    const statusDto = {
        amountsVisible: true,
        services: [
            { recordKind: "hotel", servicePublicId: "BBBB-2222", status: "partial" },
        ],
    };

    // El frontend pasa minúsculas, el backend puede devolver cualquier casing
    const resultado = buscarEstadoPagoServicio("HOTEL", "bbbb-2222", statusDto);
    assert.equal(resultado.status, "partial");
});

test("buscarEstadoPagoServicio retorna null si no encuentra el par", () => {
    const statusDto = {
        amountsVisible: true,
        services: [
            { recordKind: "flight", servicePublicId: "aaaa-1111", status: "paid" },
        ],
    };

    const resultado = buscarEstadoPagoServicio("hotel", "aaaa-1111", statusDto);
    assert.equal(resultado, null);
});

test("buscarEstadoPagoServicio retorna null si statusDto es null", () => {
    const resultado = buscarEstadoPagoServicio("hotel", "bbbb-2222", null);
    assert.equal(resultado, null);
});

test("buscarEstadoPagoServicio retorna null si recordKind es null", () => {
    const statusDto = { amountsVisible: true, services: [] };
    const resultado = buscarEstadoPagoServicio(null, "bbbb-2222", statusDto);
    assert.equal(resultado, null);
});

test("buscarEstadoPagoServicio retorna null si servicePublicId es null", () => {
    const statusDto = { amountsVisible: true, services: [] };
    const resultado = buscarEstadoPagoServicio("hotel", null, statusDto);
    assert.equal(resultado, null);
});

test("buscarEstadoPagoServicio tolera services: undefined en el DTO", () => {
    const statusDto = { amountsVisible: false };  // sin campo "services"
    const resultado = buscarEstadoPagoServicio("hotel", "bbbb-2222", statusDto);
    assert.equal(resultado, null);
});

// ─── resolverEtiquetaEstadoPago ───────────────────────────────────────────────

test("resolverEtiquetaEstadoPago 'paid' devuelve variante pagado", () => {
    const resultado = resolverEtiquetaEstadoPago("paid");
    assert.equal(resultado.variante, "pagado");
    assert.ok(resultado.texto.length > 0);
});

test("resolverEtiquetaEstadoPago 'partial' devuelve variante parcial", () => {
    const resultado = resolverEtiquetaEstadoPago("partial");
    assert.equal(resultado.variante, "parcial");
    assert.ok(resultado.texto.length > 0);
});

test("resolverEtiquetaEstadoPago 'unpaid' devuelve variante impago", () => {
    const resultado = resolverEtiquetaEstadoPago("unpaid");
    assert.equal(resultado.variante, "impago");
    assert.ok(resultado.texto.length > 0);
});

test("resolverEtiquetaEstadoPago status desconocido retorna null", () => {
    assert.equal(resolverEtiquetaEstadoPago("unknown_value"), null);
});

test("resolverEtiquetaEstadoPago null retorna null", () => {
    assert.equal(resolverEtiquetaEstadoPago(null), null);
});

test("resolverEtiquetaEstadoPago undefined retorna null", () => {
    assert.equal(resolverEtiquetaEstadoPago(undefined), null);
});

// ─── puedenVerseMontos ────────────────────────────────────────────────────────

test("puedenVerseMontos retorna true cuando amountsVisible es true", () => {
    const dto = { amountsVisible: true, services: [] };
    assert.equal(puedenVerseMontos(dto), true);
});

test("puedenVerseMontos retorna false cuando amountsVisible es false", () => {
    const dto = { amountsVisible: false, services: [] };
    assert.equal(puedenVerseMontos(dto), false);
});

test("puedenVerseMontos retorna false si el DTO es null", () => {
    assert.equal(puedenVerseMontos(null), false);
});

test("puedenVerseMontos retorna false si el DTO es undefined", () => {
    assert.equal(puedenVerseMontos(undefined), false);
});

// ─── integración: buscar + etiquetar ─────────────────────────────────────────

test("flujo completo: buscar el servicio y resolver su etiqueta de estado", () => {
    const statusDto = {
        amountsVisible: true,
        services: [
            {
                recordKind: "hotel",
                servicePublicId: "hotel-abc-123",
                status: "partial",
                netCost: 1000,
                paidToOperator: 400,
                outstandingToOperator: 600,
            },
        ],
    };

    const servicio = buscarEstadoPagoServicio("hotel", "hotel-abc-123", statusDto);
    assert.ok(servicio, "Debe encontrar el servicio");

    const etiqueta = resolverEtiquetaEstadoPago(servicio.status);
    assert.equal(etiqueta.variante, "parcial");
    assert.equal(puedenVerseMontos(statusDto), true);
    // Con amountsVisible=true los montos deben estar disponibles (no enmascarados)
    assert.equal(servicio.netCost, 1000);
    assert.equal(servicio.outstandingToOperator, 600);
});

test("flujo completo: sin permiso de costos los montos llegan en 0 y amountsVisible=false", () => {
    const statusDto = {
        amountsVisible: false,
        services: [
            {
                recordKind: "flight",
                servicePublicId: "flight-xyz-456",
                status: "unpaid",
                netCost: 0,           // enmascarado por el backend
                paidToOperator: 0,    // enmascarado
                outstandingToOperator: 0, // enmascarado
            },
        ],
    };

    const servicio = buscarEstadoPagoServicio("flight", "flight-xyz-456", statusDto);
    const etiqueta = resolverEtiquetaEstadoPago(servicio.status);

    // El estado se muestra siempre
    assert.equal(etiqueta.variante, "impago");
    // Los montos NO se deben mostrar
    assert.equal(puedenVerseMontos(statusDto), false);
});
