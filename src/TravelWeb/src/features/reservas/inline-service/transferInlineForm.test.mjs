/**
 * Tests de lógica pura de la ficha de carga en línea de Traslado.
 *
 * Cubre: cálculo de totales, dos caminos (rateId vs newCatalogProduct),
 * enmascarado de costo, validación de campos obligatorios.
 *
 * Cómo correr: node --test src/features/reservas/inline-service/transferInlineForm.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de TransferInlineForm / ServiceInlineCard ──────────

function redondearDinero(valor) {
    return Math.round((Number(valor) || 0) * 100) / 100;
}

/**
 * Calcula los totales del traslado.
 * Traslado: precio total directo (privado = precio cerrado; compartido = total también).
 */
function calcularTotalesTraslado({ salePrice, netCost, canSeeCost }) {
    const ventaTotal = redondearDinero(Number(salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { ventaTotal, costoTotal, ganancia };
}

/**
 * Validación del form de Traslado.
 * Devuelve null si es válido, o string con el mensaje de error.
 */
function validarFormTraslado(form) {
    if (!form.routeName?.trim()) return "Escribí el trayecto del traslado.";
    if (!form.pickupDate) return "Elegí la fecha del traslado.";
    if (!form.salePrice || Number(form.salePrice) <= 0) return "Ingresá el precio de venta.";
    if (!form.newCatalogProduct && !form.supplierId) return "Elegí el operador.";
    if (form.newCatalogProduct) {
        if (!form.newCatalogProduct.name?.trim()) return "Ingresá el nombre del trayecto nuevo.";
        if (!form.newCatalogProduct.supplierPublicId) return "Elegí el operador del traslado nuevo.";
    }
    return null;
}

/**
 * Construye el payload del traslado para el backend.
 * ADR-018: la identidad del traslado va en productName (no en description).
 * Nota:
 *   - movementType ("in"/"out") → campo direction del backend (TransferBookingDto)
 *   - transferType ("private"/"shared") → campo serviceMode del backend (TransferBookingDto)
 *   - ninguno de los dos va en notes
 */
function buildTransferPayload(form, canSeeCost) {
    const payload = {
        // ADR-018: identidad en productName, no en description
        productName: form.routeName?.trim() || "",
        pickupDateTime: form.pickupDate
            ? `${form.pickupDate}T${form.pickupTime || "00:00"}:00`
            : null,
        passengers: form.passengers ? Number(form.passengers) : null,
        supplierId: form.supplierId || null,
        netCost: canSeeCost ? redondearDinero(Number(form.netCost) || 0) : 0,
        salePrice: redondearDinero(Number(form.salePrice) || 0),
        currency: form.currency || "ARS",
        flightNumber: form.associatedFlightNumber || null,
        // direction: "in" (llegada) o "out" (salida) — campo propio del backend
        direction: form.movementType || null,
        // serviceMode: "private" o "shared" — campo propio del backend
        serviceMode: form.transferType || null,
        // vehicleType: texto libre opcional (Van, Sedan, etc.) — campo propio, NO es serviceMode
        vehicleType: form.vehicleType || null,
    };
    if (form.rateId) {
        payload.rateId = form.rateId;
    } else if (form.newCatalogProduct) {
        payload.newCatalogProduct = { ...form.newCatalogProduct };
        payload.supplierId = form.newCatalogProduct.supplierPublicId || null;
    }
    return payload;
}

// ─── Tests: cálculo de totales ────────────────────────────────────────────────

test("calcularTotalesTraslado: precio de venta se pasa directo sin multiplicar", () => {
    const { ventaTotal } = calcularTotalesTraslado({
        salePrice: 85000,
        netCost: 70000,
        canSeeCost: true,
    });
    assert.equal(ventaTotal, 85000);
});

test("calcularTotalesTraslado: ganancia = venta − costo (con permiso)", () => {
    const { ganancia } = calcularTotalesTraslado({
        salePrice: 85000,
        netCost: 70000,
        canSeeCost: true,
    });
    assert.equal(ganancia, 15000);
});

test("calcularTotalesTraslado: sin permiso → costo null, ganancia null", () => {
    const { costoTotal, ganancia } = calcularTotalesTraslado({
        salePrice: 85000,
        netCost: 70000,
        canSeeCost: false,
    });
    assert.equal(costoTotal, null);
    assert.equal(ganancia, null);
});

test("calcularTotalesTraslado: sin permiso → venta sigue visible", () => {
    const { ventaTotal } = calcularTotalesTraslado({
        salePrice: 85000,
        netCost: 70000,
        canSeeCost: false,
    });
    assert.equal(ventaTotal, 85000);
});

// ─── Tests: validación del formulario ────────────────────────────────────────

test("validarFormTraslado: form completo con supplierId → válido", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        salePrice: 85000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    assert.equal(validarFormTraslado(form), null);
});

test("validarFormTraslado: sin trayecto → error", () => {
    const form = {
        routeName: "",
        pickupDate: "2026-08-12",
        salePrice: 85000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormTraslado(form);
    assert.ok(error);
    assert.match(error, /trayecto/i);
});

test("validarFormTraslado: sin fecha → error", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "",
        salePrice: 85000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormTraslado(form);
    assert.match(error, /fecha/i);
});

test("validarFormTraslado: sin precio de venta → error", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        salePrice: 0,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormTraslado(form);
    assert.match(error, /venta/i);
});

test("validarFormTraslado: sin operador en camino existente → error de operador", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        salePrice: 85000,
        supplierId: "",
        newCatalogProduct: null,
    };
    const error = validarFormTraslado(form);
    assert.match(error, /operador/i);
});

// ─── Tests: payload — rateId vs newCatalogProduct ────────────────────────────

test("buildTransferPayload: con rateId → incluye rateId, sin newCatalogProduct", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        pickupTime: "09:30",
        passengers: 2,
        transferType: "Privado",
        supplierId: "supplier-1",
        netCost: 70000,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-transfer-1",
        associatedFlightNumber: "AR1234",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.rateId, "rate-transfer-1");
    assert.equal(payload.newCatalogProduct, undefined);
    // El horario de búsqueda debe estar en el pickup datetime
    assert.equal(payload.pickupDateTime, "2026-08-12T09:30:00");
    assert.equal(payload.flightNumber, "AR1234");
});

test("buildTransferPayload: con newCatalogProduct → sin rateId; supplierId de newCatalogProduct", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: null,
        newCatalogProduct: { name: "EZE → Sheraton nuevo", supplierPublicId: "supplier-3" },
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.rateId, undefined);
    assert.ok(payload.newCatalogProduct);
    assert.equal(payload.supplierId, "supplier-3");
});

test("buildTransferPayload: sin permiso → netCost = 0", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 70000,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, false);
    assert.equal(payload.netCost, 0);
    assert.equal(payload.salePrice, 85000);
});

test("buildTransferPayload: fecha sin hora → hora 00:00 por defecto", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        pickupTime: "",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.pickupDateTime, "2026-08-12T00:00:00");
});

// ─── Tests: direction y serviceMode van como campos propios, no en notes ──────

test("buildTransferPayload: direction='in' (llegada) llega como campo propio, no en notes", () => {
    // El valor del select "Llegada o salida" es "in" (valor backend).
    // Debe enviarse como direction, NO pisando notes.
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "in",
        transferType: "private",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.direction, "in");
    // notes NO debe contener el valor del movementType
    assert.notEqual(payload.notes, "in");
});

test("buildTransferPayload: direction='out' (salida) llega como campo propio", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "out",
        transferType: "",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.direction, "out");
});

test("buildTransferPayload: serviceMode='private' llega como campo propio, no en notes", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "in",
        transferType: "private",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.serviceMode, "private");
    assert.notEqual(payload.notes, "private");
});

test("buildTransferPayload: serviceMode='shared' llega como campo propio", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "",
        transferType: "shared",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.serviceMode, "shared");
});

// ─── Tests ADR-018: identidad en productName, no en description ───────────────

test("buildTransferPayload: ADR-018 — la identidad del traslado va en productName, NO en description", () => {
    // Regla ADR-018 §4-bis: productName = texto que el vendedor vio/tipeo.
    // El backend (TransferBooking) guarda la identidad en ProductName, no en Description.
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "in",
        transferType: "private",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.productName, "EZE → Sheraton");
    assert.equal(payload.description, undefined, "description NO debe aparecer en el payload de traslado");
});

test("buildTransferPayload: ADR-018 — routeName vacío → productName cadena vacía", () => {
    const form = {
        routeName: "   ",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "",
        transferType: "",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.productName, "");
});

test("buildTransferPayload: sin movementType → direction null (no pisa notes con undefined)", () => {
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "",
        transferType: "",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.direction, null);
    assert.equal(payload.serviceMode, null);
});

// ─── Tests ADR-018: round-trip de edición ────────────────────────────────────

/**
 * Simula el builder de estado de edición para Traslado.
 * ADR-018: lee productName como fuente primaria de la identidad.
 */
function buildTransferFormInitial(serviceToEdit) {
    if (!serviceToEdit) return { routeName: "", rateId: null, newCatalogProduct: null };
    return {
        routeName: serviceToEdit.productName || serviceToEdit.description || serviceToEdit.name || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

test("buildTransferFormInitial: ADR-018 — round-trip de edición lee productName primero", () => {
    const serviceDesdeBackend = {
        productName: "EZE → Sheraton Tigre",
        description: "descripcion vieja ignorada",
        rateId: "rate-transfer-1",
    };
    const form = buildTransferFormInitial(serviceDesdeBackend);
    assert.equal(form.routeName, "EZE → Sheraton Tigre");
});

test("buildTransferFormInitial: ADR-018 — fallback a description para servicios legacy (productName null)", () => {
    const serviceLegacy = {
        productName: null,
        description: "EZE → Hotel (legacy)",
        rateId: null,
    };
    const form = buildTransferFormInitial(serviceLegacy);
    assert.equal(form.routeName, "EZE → Hotel (legacy)");
});

// ─── Tests: vehicleType — texto libre opcional ────────────────────────────────

test("buildTransferPayload: vehicleType presente → va en payload como campo propio", () => {
    // vehicleType es texto libre (Van, Sedan, etc.) y va en su propio campo,
    // separado de serviceMode (private/shared) y de direction (in/out).
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        pickupTime: "",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "in",
        transferType: "private",
        vehicleType: "Van",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.vehicleType, "Van");
});

test("buildTransferPayload: vehicleType vacío → null en payload (no se envía string vacío)", () => {
    // Con || null: "" → null. El backend acepta null (campo opcional).
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "",
        transferType: "",
        vehicleType: "",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.vehicleType, null);
});

test("buildTransferPayload: vehicleType NO es lo mismo que serviceMode", () => {
    // serviceMode = "private"/"shared" (tipo de servicio).
    // vehicleType = "Van", "Sedan", etc. (tipo físico del vehículo).
    // Son campos independientes; no deben pisarse entre sí.
    const form = {
        routeName: "EZE → Sheraton",
        pickupDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 85000,
        currency: "ARS",
        rateId: "rate-1",
        movementType: "in",
        transferType: "private",
        vehicleType: "Sedan",
        newCatalogProduct: null,
    };
    const payload = buildTransferPayload(form, true);
    assert.equal(payload.serviceMode, "private", "serviceMode debe ser el valor del transferType");
    assert.equal(payload.vehicleType, "Sedan", "vehicleType debe ser el texto libre del vehículo");
    // Verificar que no se pisaron
    assert.notEqual(payload.serviceMode, payload.vehicleType);
});

// ─── Tests: round-trip de edición con vehicleType ────────────────────────────

/**
 * Simula el builder de estado de edición para Traslado con todos los campos nuevos.
 * vehicleType: lee del backend con fallback "" (no especificado).
 */
function buildTransferFormInitialCompleto(serviceToEdit) {
    if (!serviceToEdit) {
        return {
            routeName: "", rateId: null, newCatalogProduct: null,
            vehicleType: "",
        };
    }
    return {
        routeName: serviceToEdit.productName || serviceToEdit.description || serviceToEdit.name || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
        // Round-trip: el backend devuelve vehicleType en TransferBookingDto; fallback "" (no especificado).
        vehicleType: serviceToEdit.vehicleType || "",
    };
}

test("buildTransferFormInitial: round-trip vehicleType persistido → se precarga en el form", () => {
    // Al editar un traslado que tiene vehicleType guardado, el campo debe precargarse
    // para que el vendedor lo vea y pueda corregirlo si es necesario.
    const serviceDesdeBackend = {
        productName: "EZE → Sheraton Tigre",
        vehicleType: "Van",
        rateId: "rate-transfer-1",
    };
    const form = buildTransferFormInitialCompleto(serviceDesdeBackend);
    assert.equal(form.vehicleType, "Van");
});

test("buildTransferFormInitial: round-trip vehicleType null del backend → '' en el form (no undefined)", () => {
    // Traslados guardados antes de este campo traen vehicleType=null; debe mapearse a ""
    // para que el input tenga value controlado (no undefined que causa error React).
    const serviceDesdeBackend = {
        productName: "EZE → Hotel",
        vehicleType: null,
        rateId: null,
    };
    const form = buildTransferFormInitialCompleto(serviceDesdeBackend);
    assert.equal(form.vehicleType, "");
});
