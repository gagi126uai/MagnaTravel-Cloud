import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    calcularExcedente,
    construirConfirmacionSobrecobroCliente,
    construirConfirmacionSobrepagoProveedor,
} from "./overpaymentConfirmLogic.js";
import { formatCurrency } from "./utils.js";

// ─── calcularExcedente (Bug #3, Tanda 4, 2026-07-24) ─────────────────────────

describe("calcularExcedente", () => {
    it("monto menor a la deuda: no hay excedente", () => {
        assert.equal(calcularExcedente(500, 1000), 0);
    });

    it("monto igual a la deuda (límite exacto): no hay excedente", () => {
        assert.equal(calcularExcedente(1000, 1000), 0);
    });

    it("monto mayor a la deuda: el excedente es la diferencia exacta", () => {
        assert.equal(calcularExcedente(1200, 1000), 200);
    });

    it("un resto de redondeo menor a medio centavo NO cuenta como excedente", () => {
        assert.equal(calcularExcedente(1000.003, 1000), 0);
    });

    it("un excedente de un centavo entero SÍ cuenta", () => {
        // No comparamos con equal exacto por los restos típicos de coma flotante
        // (1000.01 - 1000 en JS no da exactamente 0.01).
        const excedente = calcularExcedente(1000.01, 1000);
        assert.ok(Math.abs(excedente - 0.01) < 1e-9, `esperaba ~0.01, dio ${excedente}`);
    });

    it("deuda null (sin dato para comparar): no rompe, devuelve 0", () => {
        assert.equal(calcularExcedente(1000, null), 0);
    });

    it("deuda undefined: no rompe, devuelve 0", () => {
        assert.equal(calcularExcedente(1000, undefined), 0);
    });

    it("monto no numérico: no rompe, devuelve 0", () => {
        assert.equal(calcularExcedente("abc", 1000), 0);
    });

    it("acepta strings numéricos (vienen de inputs HTML)", () => {
        assert.equal(calcularExcedente("1200", "1000"), 200);
    });

    it("deuda 0 (reserva ya saldada): cualquier monto positivo es 100% excedente", () => {
        assert.equal(calcularExcedente(500, 0), 500);
    });

    it("deuda negativa (ya había saldo a favor): el excedente crece en consecuencia", () => {
        assert.equal(calcularExcedente(500, -100), 600);
    });
});

// ─── construirConfirmacionSobrecobroCliente ──────────────────────────────────

describe("construirConfirmacionSobrecobroCliente", () => {
    it("arma el texto con el excedente formateado en la moneda indicada", () => {
        const confirmacion = construirConfirmacionSobrecobroCliente({ excedente: 500, moneda: "ARS" });
        assert.match(confirmacion.text, new RegExp(formatCurrency(500, "ARS").replace(/[.*+?^${}()|[\]\\]/g, "\\$&")));
        assert.match(confirmacion.text, /saldo a favor del cliente/);
    });

    it("usa la moneda correcta en dólares", () => {
        const confirmacion = construirConfirmacionSobrecobroCliente({ excedente: 50, moneda: "USD" });
        assert.ok(confirmacion.text.includes(formatCurrency(50, "USD")));
    });

    it("siempre pide confirmación explícita (nunca autoconfirma)", () => {
        const confirmacion = construirConfirmacionSobrecobroCliente({ excedente: 100, moneda: "ARS" });
        assert.equal(typeof confirmacion.title, "string");
        assert.equal(typeof confirmacion.confirmText, "string");
        assert.ok(confirmacion.title.length > 0);
    });
});

// ─── construirConfirmacionSobrepagoProveedor ─────────────────────────────────

describe("construirConfirmacionSobrepagoProveedor", () => {
    it("arma el texto con el excedente formateado en la moneda indicada", () => {
        const confirmacion = construirConfirmacionSobrepagoProveedor({ excedente: 300, moneda: "ARS" });
        assert.ok(confirmacion.text.includes(formatCurrency(300, "ARS")));
        assert.match(confirmacion.text, /a favor nuestro con el operador/);
    });

    it("usa la moneda correcta en dólares", () => {
        const confirmacion = construirConfirmacionSobrepagoProveedor({ excedente: 25, moneda: "USD" });
        assert.ok(confirmacion.text.includes(formatCurrency(25, "USD")));
    });

    it("el texto del proveedor es distinto al del cliente (no se confunde a quién le sobra la plata)", () => {
        const cliente = construirConfirmacionSobrecobroCliente({ excedente: 100, moneda: "ARS" });
        const proveedor = construirConfirmacionSobrepagoProveedor({ excedente: 100, moneda: "ARS" });
        assert.notEqual(cliente.text, proveedor.text);
    });
});
