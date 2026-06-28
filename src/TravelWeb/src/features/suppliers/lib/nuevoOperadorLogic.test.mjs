/**
 * Tests de lógica pura para el alta de un operador nuevo.
 *
 * Cubre: validarNuevoOperador y construirPayloadNuevoOperador.
 * Cómo correr: node --test src/features/suppliers/lib/nuevoOperadorLogic.test.mjs
 *
 * No monta React; solo ejercita funciones puras. Si la lógica del módulo cambia,
 * actualizar estos tests también.
 */

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    validarNuevoOperador,
    construirPayloadNuevoOperador,
    FORM_INICIAL,
} from "./nuevoOperadorLogic.js";

// ─── validarNuevoOperador ────────────────────────────────────────────────────

describe("validarNuevoOperador", () => {
    // Toggle OFF = datos fiscales obligatorios

    it("retorna error cuando name esta vacio (toggle OFF)", () => {
        const form = { ...FORM_INICIAL, name: "" };
        assert.match(validarNuevoOperador(form, false), /razón social/i);
    });

    it("retorna error cuando name es solo espacios (toggle OFF)", () => {
        const form = { ...FORM_INICIAL, name: "   " };
        assert.match(validarNuevoOperador(form, false), /razón social/i);
    });

    it("retorna error cuando defaultCurrency falta (toggle OFF)", () => {
        const form = { ...FORM_INICIAL, name: "Despegar S.A.", defaultCurrency: "" };
        assert.match(validarNuevoOperador(form, false), /moneda/i);
    });

    it("retorna error cuando taxId falta (toggle OFF)", () => {
        const form = { ...FORM_INICIAL, name: "Despegar S.A.", defaultCurrency: "ARS", taxId: "" };
        assert.match(validarNuevoOperador(form, false), /CUIT/);
    });

    it("retorna error cuando taxCondition falta (toggle OFF)", () => {
        const form = {
            ...FORM_INICIAL,
            name: "Despegar S.A.",
            defaultCurrency: "ARS",
            taxId: "30-12345678-9",
            taxCondition: "",
        };
        assert.match(validarNuevoOperador(form, false), /condición fiscal/i);
    });

    it("retorna null cuando todos los obligatorios estan completos (toggle OFF)", () => {
        const form = {
            ...FORM_INICIAL,
            name: "Despegar S.A.",
            defaultCurrency: "ARS",
            taxId: "30-12345678-9",
            taxCondition: "IVA_RESP_INSCRIPTO",
        };
        assert.equal(validarNuevoOperador(form, false), null);
    });

    it("retorna null con moneda USD (toggle OFF)", () => {
        const form = {
            ...FORM_INICIAL,
            name: "Aerolíneas Arg.",
            defaultCurrency: "USD",
            taxId: "30-99999999-0",
            taxCondition: "MONOTRIBUTISTA",
        };
        assert.equal(validarNuevoOperador(form, false), null);
    });

    // Toggle ON = datos fiscales pendientes, CUIT y condición no son obligatorios

    it("retorna null cuando name y moneda presentes aunque CUIT falte (toggle ON)", () => {
        const form = { ...FORM_INICIAL, name: "Operador SRL", defaultCurrency: "ARS" };
        assert.equal(validarNuevoOperador(form, true), null);
    });

    it("retorna null cuando name y moneda presentes aunque condicion falte (toggle ON)", () => {
        const form = {
            ...FORM_INICIAL,
            name: "Operador SRL",
            defaultCurrency: "USD",
            taxId: "",
            taxCondition: "",
        };
        assert.equal(validarNuevoOperador(form, true), null);
    });

    it("sigue requiriendo name incluso con toggle ON", () => {
        const form = { ...FORM_INICIAL, name: "", defaultCurrency: "ARS" };
        assert.match(validarNuevoOperador(form, true), /razón social/i);
    });

    it("sigue requiriendo moneda incluso con toggle ON", () => {
        const form = { ...FORM_INICIAL, name: "Test SA", defaultCurrency: "" };
        assert.match(validarNuevoOperador(form, true), /moneda/i);
    });
});

// ─── construirPayloadNuevoOperador ───────────────────────────────────────────

describe("construirPayloadNuevoOperador", () => {
    it("incluye solo name y defaultCurrency cuando los campos opcionales estan vacios", () => {
        const form = { ...FORM_INICIAL, name: "Despegar S.A.", defaultCurrency: "ARS" };
        const payload = construirPayloadNuevoOperador(form);
        assert.deepEqual(payload, {
            name: "Despegar S.A.",
            defaultCurrency: "ARS",
        });
    });

    it("incluye taxId cuando esta cargado", () => {
        const form = { ...FORM_INICIAL, name: "ABC", defaultCurrency: "ARS", taxId: "30-12345678-9" };
        const payload = construirPayloadNuevoOperador(form);
        assert.equal(payload.taxId, "30-12345678-9");
    });

    it("incluye taxCondition cuando esta cargada", () => {
        const form = {
            ...FORM_INICIAL,
            name: "ABC",
            defaultCurrency: "ARS",
            taxCondition: "IVA_RESP_INSCRIPTO",
        };
        const payload = construirPayloadNuevoOperador(form);
        assert.equal(payload.taxCondition, "IVA_RESP_INSCRIPTO");
    });

    it("NO incluye taxId cuando esta vacio", () => {
        const form = { ...FORM_INICIAL, name: "ABC", defaultCurrency: "ARS", taxId: "" };
        const payload = construirPayloadNuevoOperador(form);
        assert.equal("taxId" in payload, false);
    });

    it("NO incluye taxCondition cuando esta vacia", () => {
        const form = { ...FORM_INICIAL, name: "ABC", defaultCurrency: "ARS", taxCondition: "" };
        const payload = construirPayloadNuevoOperador(form);
        assert.equal("taxCondition" in payload, false);
    });

    it("incluye todos los campos opcionales de Mas detalles cuando estan cargados", () => {
        const form = {
            name: "  Despegar Argentina S.A.  ",
            defaultCurrency: "USD",
            taxId: " 30-12345678-9 ",
            taxCondition: "IVA_RESP_INSCRIPTO",
            contactName: " Juan Pérez ",
            phone: "+54 11 1234-5678",
            email: "juan@despegar.com",
            address: "  Lavalle 123, CABA  ",
        };
        const payload = construirPayloadNuevoOperador(form);
        assert.deepEqual(payload, {
            name: "Despegar Argentina S.A.",
            defaultCurrency: "USD",
            taxId: "30-12345678-9",
            taxCondition: "IVA_RESP_INSCRIPTO",
            contactName: "Juan Pérez",
            phone: "+54 11 1234-5678",
            email: "juan@despegar.com",
            address: "Lavalle 123, CABA",
        });
    });

    it("recorta (trim) el name aunque el resto este vacio", () => {
        const form = { ...FORM_INICIAL, name: "  Operador XYZ  ", defaultCurrency: "ARS" };
        const payload = construirPayloadNuevoOperador(form);
        assert.equal(payload.name, "Operador XYZ");
    });

    it("no incluye contactName cuando solo tiene espacios", () => {
        const form = { ...FORM_INICIAL, name: "ABC", defaultCurrency: "ARS", contactName: "   " };
        const payload = construirPayloadNuevoOperador(form);
        assert.equal("contactName" in payload, false);
    });
});
