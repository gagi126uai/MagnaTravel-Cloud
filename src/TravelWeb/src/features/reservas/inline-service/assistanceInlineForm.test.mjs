/**
 * Tests de lógica pura de la ficha de carga en línea de Asistencia.
 *
 * Cubre: cálculo de días de vigencia, cálculo de totales (pax × días),
 * dos caminos (rateId vs newCatalogProduct), enmascarado de costo,
 * validación de campos obligatorios.
 *
 * Cómo correr: node --test src/features/reservas/inline-service/assistanceInlineForm.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de AssistanceInlineForm / ServiceInlineCard ─────────

function redondearDinero(valor) {
    return Math.round((Number(valor) || 0) * 100) / 100;
}

/**
 * Calcula días de vigencia entre dos fechas.
 * +1 porque el día de inicio también cuenta (sale día 1, llega día 8 = 8 días).
 */
function calcularDiasVigencia(validFrom, validTo) {
    if (!validFrom || !validTo) return 0;
    const inicio = new Date(validFrom);
    const fin = new Date(validTo);
    const dias = Math.ceil((fin - inicio) / (1000 * 60 * 60 * 24)) + 1;
    return dias > 0 ? dias : 0;
}

/**
 * Calcula los totales de asistencia.
 * Total = precio por persona × pasajeros × días.
 */
function calcularTotalesAsistencia({ unitSalePrice, unitNetCost, passengers, validFrom, validTo, canSeeCost }) {
    const dias = calcularDiasVigencia(validFrom, validTo);
    const pasajeros = Math.max(Number(passengers) || 1, 1);
    const factorTotal = Math.max(dias, 0) * pasajeros;
    const ventaTotal = redondearDinero((Number(unitSalePrice) || 0) * factorTotal);
    const costoTotal = canSeeCost ? redondearDinero((Number(unitNetCost) || 0) * factorTotal) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { dias, pasajeros, factorTotal, ventaTotal, costoTotal, ganancia };
}

/**
 * Validación del form de Asistencia.
 * Devuelve null si es válido, o string con el mensaje de error.
 */
function validarFormAsistencia(form) {
    if (!form.planName?.trim()) return "Escribí el plan o cobertura.";
    if (!form.validFrom) return "Elegí la fecha de inicio de vigencia.";
    if (!form.validTo) return "Elegí la fecha de fin de vigencia.";
    if (!form.unitSalePrice || Number(form.unitSalePrice) <= 0) return "Ingresá el precio de venta por persona/día.";
    if (!form.newCatalogProduct && !form.supplierId) return "Elegí el proveedor.";
    if (form.newCatalogProduct) {
        if (!form.newCatalogProduct.name?.trim()) return "Ingresá el nombre del plan nuevo.";
        if (!form.newCatalogProduct.supplierPublicId) return "Elegí el proveedor del plan nuevo.";
    }
    return null;
}

/**
 * Construye el payload de la asistencia para el backend.
 * ADR-018: la identidad de la asistencia va en planType (ya nullable en AssistanceBooking).
 */
function buildAssistancePayload(form, canSeeCost) {
    const payload = {
        // ADR-018: identidad en planType, no en description
        planType: form.planName?.trim() || "",
        validFrom: form.validFrom ? `${form.validFrom}T00:00:00.000Z` : null,
        validTo: form.validTo ? `${form.validTo}T00:00:00.000Z` : null,
        adults: form.passengers ? Number(form.passengers) : 1,
        supplierId: form.supplierId || null,
        netCost: canSeeCost ? redondearDinero(Number(form.unitNetCost) || 0) : 0,
        salePrice: redondearDinero(Number(form.unitSalePrice) || 0),
        currency: form.currency || "ARS",
        policyNumber: form.voucherNumbers || null,
    };
    if (form.rateId) {
        payload.rateId = form.rateId;
    } else if (form.newCatalogProduct) {
        payload.newCatalogProduct = { ...form.newCatalogProduct };
        payload.supplierId = form.newCatalogProduct.supplierPublicId || null;
    }
    return payload;
}

// ─── Tests: cálculo de días de vigencia ──────────────────────────────────────

test("calcularDiasVigencia: 8 días entre el 12/08 y el 19/08 (sale el 12, llega el 19)", () => {
    // Sale el 12 de agosto, llega el 19 → 8 días de cobertura (día de salida incluido)
    assert.equal(calcularDiasVigencia("2026-08-12", "2026-08-19"), 8);
});

test("calcularDiasVigencia: misma fecha → 1 día (el seguro cubre ese día)", () => {
    assert.equal(calcularDiasVigencia("2026-08-12", "2026-08-12"), 1);
});

test("calcularDiasVigencia: fecha final anterior a la inicial → 0 días", () => {
    assert.equal(calcularDiasVigencia("2026-08-19", "2026-08-12"), 0);
});

test("calcularDiasVigencia: fecha vacía → 0 días", () => {
    assert.equal(calcularDiasVigencia("", "2026-08-19"), 0);
    assert.equal(calcularDiasVigencia("2026-08-12", ""), 0);
});

// ─── Tests: cálculo de totales ────────────────────────────────────────────────

test("calcularTotalesAsistencia: 2 pax × 8 días × $50/día = $800 de venta", () => {
    const { ventaTotal, dias, factorTotal } = calcularTotalesAsistencia({
        unitSalePrice: 50,
        unitNetCost: 40,
        passengers: 2,
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        canSeeCost: true,
    });
    assert.equal(dias, 8);
    assert.equal(factorTotal, 16); // 2 × 8
    assert.equal(ventaTotal, 800);
});

test("calcularTotalesAsistencia: ganancia = (venta − costo) × pax × días (con permiso)", () => {
    const { ganancia } = calcularTotalesAsistencia({
        unitSalePrice: 50,
        unitNetCost: 40,
        passengers: 2,
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        canSeeCost: true,
    });
    // (50 - 40) × 2 × 8 = 160
    assert.equal(ganancia, 160);
});

test("calcularTotalesAsistencia: passengers vacío o 0 → default 1 pasajero", () => {
    const { pasajeros: paxVacio } = calcularTotalesAsistencia({
        unitSalePrice: 50, unitNetCost: 0, passengers: "",
        validFrom: "2026-08-12", validTo: "2026-08-19", canSeeCost: true,
    });
    assert.equal(paxVacio, 1);
});

test("calcularTotalesAsistencia: sin fechas → factorTotal 0 → ventaTotal 0", () => {
    const { dias, ventaTotal } = calcularTotalesAsistencia({
        unitSalePrice: 50, unitNetCost: 40, passengers: 2,
        validFrom: "", validTo: "", canSeeCost: true,
    });
    assert.equal(dias, 0);
    assert.equal(ventaTotal, 0);
});

test("calcularTotalesAsistencia: sin permiso → costo null, ganancia null (nunca $0)", () => {
    const { costoTotal, ganancia } = calcularTotalesAsistencia({
        unitSalePrice: 50,
        unitNetCost: 40,
        passengers: 2,
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        canSeeCost: false,
    });
    assert.equal(costoTotal, null);
    assert.equal(ganancia, null);
});

test("calcularTotalesAsistencia: sin permiso → venta sigue visible", () => {
    const { ventaTotal } = calcularTotalesAsistencia({
        unitSalePrice: 50,
        unitNetCost: 40,
        passengers: 2,
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        canSeeCost: false,
    });
    assert.equal(ventaTotal, 800);
});

// ─── Tests: validación del formulario ────────────────────────────────────────

test("validarFormAsistencia: form completo con supplierId → válido", () => {
    const form = {
        planName: "AC 150 Americas Plata",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        unitSalePrice: 50,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    assert.equal(validarFormAsistencia(form), null);
});

test("validarFormAsistencia: sin nombre del plan → error", () => {
    const form = {
        planName: "",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        unitSalePrice: 50,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormAsistencia(form);
    assert.ok(error);
    assert.match(error, /plan/i);
});

test("validarFormAsistencia: sin fecha de inicio → error", () => {
    const form = {
        planName: "AC 150",
        validFrom: "",
        validTo: "2026-08-19",
        unitSalePrice: 50,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormAsistencia(form);
    assert.match(error, /inicio/i);
});

test("validarFormAsistencia: sin fecha de fin → error", () => {
    const form = {
        planName: "AC 150",
        validFrom: "2026-08-12",
        validTo: "",
        unitSalePrice: 50,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormAsistencia(form);
    assert.match(error, /fin/i);
});

test("validarFormAsistencia: sin precio → error", () => {
    const form = {
        planName: "AC 150",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        unitSalePrice: 0,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormAsistencia(form);
    assert.match(error, /venta/i);
});

test("validarFormAsistencia: sin proveedor en camino existente → error de proveedor", () => {
    const form = {
        planName: "AC 150",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        unitSalePrice: 50,
        supplierId: "",
        newCatalogProduct: null,
    };
    const error = validarFormAsistencia(form);
    assert.match(error, /proveedor/i);
});

// ─── Tests: payload — rateId vs newCatalogProduct ────────────────────────────

test("buildAssistancePayload: con rateId → incluye rateId; voucherNumbers en policyNumber", () => {
    const form = {
        planName: "AC 150",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 40,
        unitSalePrice: 50,
        currency: "USD",
        rateId: "rate-assistance-1",
        voucherNumbers: "V-123456 · V-123457",
        newCatalogProduct: null,
    };
    const payload = buildAssistancePayload(form, true);
    assert.equal(payload.rateId, "rate-assistance-1");
    assert.equal(payload.newCatalogProduct, undefined);
    assert.equal(payload.policyNumber, "V-123456 · V-123457");
    assert.equal(payload.currency, "USD");
});

test("buildAssistancePayload: con newCatalogProduct → sin rateId; supplierId de newCatalogProduct", () => {
    const form = {
        planName: "Plan nuevo",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        passengers: 1,
        supplierId: "",
        unitNetCost: 0,
        unitSalePrice: 50,
        currency: "ARS",
        rateId: null,
        voucherNumbers: "",
        newCatalogProduct: { name: "Plan nuevo", supplierPublicId: "supplier-5" },
    };
    const payload = buildAssistancePayload(form, true);
    assert.equal(payload.rateId, undefined);
    assert.ok(payload.newCatalogProduct);
    assert.equal(payload.supplierId, "supplier-5");
});

test("buildAssistancePayload: sin permiso → netCost = 0", () => {
    const form = {
        planName: "AC 150",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 40,
        unitSalePrice: 50,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildAssistancePayload(form, false);
    assert.equal(payload.netCost, 0);
    // La venta NO se toca (la envía como precio unitario; el backend calcula)
    assert.equal(payload.salePrice, 50);
});

// ─── Tests ADR-018: identidad en planType, no en description ─────────────────

test("buildAssistancePayload: ADR-018 — la identidad de la asistencia va en planType, NO en description", () => {
    // Regla ADR-018 §4-bis: planType es el campo canónico de AssistanceBooking.
    // El campo description ya no debe aparecer en el payload.
    const form = {
        planName: "AC 150 Americas Plata",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        passengers: 1,
        supplierId: "supplier-1",
        unitNetCost: 40,
        unitSalePrice: 50,
        currency: "ARS",
        rateId: "rate-1",
        voucherNumbers: "",
        newCatalogProduct: null,
    };
    const payload = buildAssistancePayload(form, true);
    assert.equal(payload.planType, "AC 150 Americas Plata");
    assert.equal(payload.description, undefined, "description NO debe aparecer en el payload de asistencia");
});

test("buildAssistancePayload: ADR-018 — planName vacío → planType cadena vacía", () => {
    const form = {
        planName: "",
        validFrom: "2026-08-12",
        validTo: "2026-08-19",
        passengers: 1,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 50,
        currency: "ARS",
        rateId: "rate-1",
        voucherNumbers: "",
        newCatalogProduct: null,
    };
    const payload = buildAssistancePayload(form, true);
    assert.equal(payload.planType, "");
});

// ─── Tests ADR-018: round-trip de edición para Asistencia ───────────────────

/**
 * Simula el builder de estado de edición para Asistencia.
 * ADR-018: lee planType como fuente primaria de la identidad.
 */
function buildAssistanceFormInitial(serviceToEdit) {
    if (!serviceToEdit) return { planName: "", rateId: null, newCatalogProduct: null };
    return {
        planName: serviceToEdit.planType || serviceToEdit.description || serviceToEdit.planName || serviceToEdit.name || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

test("buildAssistanceFormInitial: ADR-018 — round-trip de edición lee planType primero", () => {
    const serviceDesdeBackend = {
        planType: "AC 150 Americas Plata",
        description: "descripcion vieja ignorada",
        planName: "nombre viejo ignorado",
        rateId: "rate-assistance-1",
    };
    const form = buildAssistanceFormInitial(serviceDesdeBackend);
    assert.equal(form.planName, "AC 150 Americas Plata");
});

test("buildAssistanceFormInitial: ADR-018 — fallback a description para servicios legacy (planType null)", () => {
    const serviceLegacy = {
        planType: null,
        description: "Cobertura Basica (legacy)",
        rateId: null,
    };
    const form = buildAssistanceFormInitial(serviceLegacy);
    assert.equal(form.planName, "Cobertura Basica (legacy)");
});
