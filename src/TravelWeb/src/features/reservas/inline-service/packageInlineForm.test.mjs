/**
 * Tests de lógica pura de la ficha de carga en línea de Paquete.
 *
 * Cubre: cálculo de totales (precio/persona × pasajeros), dos caminos,
 * enmascarado de costo, validación de campos obligatorios.
 *
 * Cómo correr: node --test src/features/reservas/inline-service/packageInlineForm.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de PackageInlineForm / ServiceInlineCard ────────────

function redondearDinero(valor) {
    return Math.round((Number(valor) || 0) * 100) / 100;
}

/**
 * Calcula los totales del paquete.
 * Total = precio por persona × pasajeros.
 */
function calcularTotalesPaquete({ unitSalePrice, unitNetCost, passengers, canSeeCost }) {
    const pasajeros = Math.max(Number(passengers) || 1, 1);
    const ventaTotal = redondearDinero((Number(unitSalePrice) || 0) * pasajeros);
    const costoTotal = canSeeCost ? redondearDinero((Number(unitNetCost) || 0) * pasajeros) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { pasajeros, ventaTotal, costoTotal, ganancia };
}

/**
 * Validación del form de Paquete.
 * Devuelve null si es válido, o string con el mensaje de error.
 */
function validarFormPaquete(form) {
    if (!form.packageName?.trim()) return "Escribí el nombre del paquete.";
    if (!form.startDate) return "Elegí la fecha de salida.";
    if (!form.unitSalePrice || Number(form.unitSalePrice) <= 0) return "Ingresá el precio de venta por persona.";
    if (!form.newCatalogProduct && !form.supplierId) return "Elegí el operador.";
    if (form.newCatalogProduct) {
        if (!form.newCatalogProduct.name?.trim()) return "Ingresá el nombre del paquete nuevo.";
        if (!form.newCatalogProduct.supplierPublicId) return "Elegí el operador del paquete nuevo.";
    }
    return null;
}

/**
 * Construye el payload del paquete para el backend.
 * ADR-018: la identidad del paquete va en packageName (campo pre-existente en PackageBooking).
 * endDate es OPCIONAL: si el form no lo tiene, se omite y el backend coalesce a startDate.
 * F2: operatorPaymentDeadline eliminado del payload (el aviso viene del backend por firstStartDate).
 * Nota:
 *   - roomBase ("double"/"triple"/etc) → campo occupancyBase del backend (PackageBookingDto)
 */
function buildPackagePayload(form, canSeeCost) {
    const pasajeros = Math.max(Number(form.passengers) || 1, 1);
    const salePriceTotal = redondearDinero((Number(form.unitSalePrice) || 0) * pasajeros);
    const netCostTotal = redondearDinero((Number(form.unitNetCost) || 0) * pasajeros);

    const payload = {
        // ADR-018: identidad en packageName, no en description
        packageName: form.packageName?.trim() || "",
        // endDate es OPCIONAL en ADR-018; si no viene, se omite (null)
        // Fecha de pared sin conversión UTC, igual que Hotel/Vuelo (bug fechas corridas 2026-07-16).
        endDate: form.endDate ? `${form.endDate}T00:00:00` : null,
        startDate: form.startDate ? `${form.startDate}T00:00:00` : null,
        adults: pasajeros,
        supplierId: form.supplierId || null,
        netCost: canSeeCost ? netCostTotal : 0,
        salePrice: salePriceTotal,
        currency: form.currency || "ARS",
        itinerary: form.itinerary || null,
        confirmationNumber: form.fileNumber || null,
        // occupancyBase: "double", "triple", etc. — campo propio del backend
        occupancyBase: form.roomBase || null,
        // operatorPaymentDeadline eliminado en F2 (Próximos Inicios)
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

test("calcularTotalesPaquete: 4 pax × $250.000/persona = $1.000.000 de venta", () => {
    const { ventaTotal, pasajeros } = calcularTotalesPaquete({
        unitSalePrice: 250000,
        unitNetCost: 200000,
        passengers: 4,
        canSeeCost: true,
    });
    assert.equal(pasajeros, 4);
    assert.equal(ventaTotal, 1000000);
});

test("calcularTotalesPaquete: ganancia = (venta − costo) × pax (con permiso)", () => {
    const { ganancia } = calcularTotalesPaquete({
        unitSalePrice: 250000,
        unitNetCost: 200000,
        passengers: 4,
        canSeeCost: true,
    });
    // (250.000 - 200.000) × 4 = 200.000
    assert.equal(ganancia, 200000);
});

test("calcularTotalesPaquete: passengers vacío o 0 → default 1 pasajero", () => {
    const { pasajeros: paxVacio } = calcularTotalesPaquete({
        unitSalePrice: 250000, unitNetCost: 0, passengers: "", canSeeCost: true,
    });
    assert.equal(paxVacio, 1);

    const { pasajeros: paxCero } = calcularTotalesPaquete({
        unitSalePrice: 250000, unitNetCost: 0, passengers: 0, canSeeCost: true,
    });
    assert.equal(paxCero, 1);
});

test("calcularTotalesPaquete: sin permiso → costo null, ganancia null (nunca $0)", () => {
    const { costoTotal, ganancia } = calcularTotalesPaquete({
        unitSalePrice: 250000,
        unitNetCost: 200000,
        passengers: 2,
        canSeeCost: false,
    });
    assert.equal(costoTotal, null);
    assert.equal(ganancia, null);
});

test("calcularTotalesPaquete: sin permiso → venta sigue visible", () => {
    const { ventaTotal } = calcularTotalesPaquete({
        unitSalePrice: 250000,
        unitNetCost: 200000,
        passengers: 3,
        canSeeCost: false,
    });
    assert.equal(ventaTotal, 750000);
});

// ─── Tests: validación del formulario ────────────────────────────────────────

test("validarFormPaquete: form completo con supplierId → válido", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        unitSalePrice: 250000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    assert.equal(validarFormPaquete(form), null);
});

test("validarFormPaquete: sin nombre del paquete → error", () => {
    const form = {
        packageName: "",
        startDate: "2026-08-12",
        unitSalePrice: 250000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormPaquete(form);
    assert.ok(error);
    assert.match(error, /paquete/i);
});

test("validarFormPaquete: sin fecha de salida → error", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "",
        unitSalePrice: 250000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormPaquete(form);
    assert.match(error, /fecha/i);
});

test("validarFormPaquete: sin precio de venta → error", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        unitSalePrice: 0,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormPaquete(form);
    assert.match(error, /venta/i);
});

test("validarFormPaquete: sin operador en camino existente → error de operador", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        unitSalePrice: 250000,
        supplierId: "",
        newCatalogProduct: null,
    };
    const error = validarFormPaquete(form);
    assert.match(error, /operador/i);
});

// ─── Tests: payload — rateId vs newCatalogProduct ────────────────────────────

test("buildPackagePayload: con rateId → incluye rateId; salePrice = por persona × pax", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        passengers: 3,
        supplierId: "supplier-1",
        unitNetCost: 200000,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-package-1",
        fileNumber: "PKG-001",
        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.rateId, "rate-package-1");
    assert.equal(payload.newCatalogProduct, undefined);
    // 3 pax × $250.000 = $750.000
    assert.equal(payload.salePrice, 750000);
    assert.equal(payload.netCost, 600000);
    assert.equal(payload.adults, 3);
    assert.equal(payload.confirmationNumber, "PKG-001");
});

test("buildPackagePayload: con newCatalogProduct → sin rateId; supplierId de newCatalogProduct", () => {
    const form = {
        packageName: "Iguazú nuevo",
        startDate: "2026-08-12",
        passengers: 2,
        supplierId: "",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: null,
        newCatalogProduct: { name: "Iguazú nuevo", supplierPublicId: "supplier-4" },
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.rateId, undefined);
    assert.ok(payload.newCatalogProduct);
    assert.equal(payload.supplierId, "supplier-4");
});

test("buildPackagePayload: sin permiso → netCost = 0", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 200000,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, false);
    assert.equal(payload.netCost, 0);
    // La venta sigue siendo 2 × $250.000 = $500.000
    assert.equal(payload.salePrice, 500000);
});

// ─── Tests: occupancyBase va como campo propio ────────────────────────────────

test("buildPackagePayload: occupancyBase='double' llega como campo propio, no en notes", () => {
    // El select de "Base" usa value="double" (valor backend).
    // El estado lo guarda en roomBase, el payload lo envía como occupancyBase.
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "double",
        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.occupancyBase, "double");
    // notes NO debe contener el valor de la base
    assert.notEqual(payload.notes, "double");
});

test("buildPackagePayload: occupancyBase='triple' llega como campo propio", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        passengers: 3,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "triple",
        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.occupancyBase, "triple");
});

test("buildPackagePayload: roomBase vacío → occupancyBase null", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "",
        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.occupancyBase, null);
});

test("buildPackagePayload: F2 → operatorPaymentDeadline no existe más en el payload (eliminado en F2)", () => {
    // F2 (Próximos Inicios): el aviso de inicio se calcula en el backend desde firstStartDate.
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "",
        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    // En F2 ya no va operatorPaymentDeadline en el payload
    assert.equal(payload.operatorPaymentDeadline, undefined);
    assert.equal(payload.depositDeadline, undefined);
});

// ─── Tests ADR-018: identidad en packageName, endDate opcional ────────────────

test("buildPackagePayload: ADR-018 — la identidad del paquete va en packageName, NO en description", () => {
    // Regla ADR-018 §4-bis: packageName es el campo canónico de PackageBooking.
    // El campo description ya no debe aparecer en el payload.
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "",

        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.packageName, "Iguazú 7 noches");
    assert.equal(payload.description, undefined, "description NO debe aparecer en el payload de paquete");
});

test("buildPackagePayload: ADR-018 — endDate ausente → null (no 500 por campo faltante)", () => {
    // ADR-018 §3: endDate es OPCIONAL. Cuando el form no lo tiene, el payload manda null
    // y el backend coalesce EndDate a StartDate para los cálculos (Nights = 0).
    const form = {
        packageName: "Paquete corto",
        startDate: "2026-08-12",
        // endDate ausente en el form inline — campo no existe en la ficha
        passengers: 1,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 100000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "",

        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.endDate, null, "endDate debe ser null cuando el form no lo tiene");
});

test("buildPackagePayload: ADR-018 — endDate presente → se serializa correctamente", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        endDate: "2026-08-19",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "double",

        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.ok(payload.endDate?.startsWith("2026-08-19"), "endDate debe serializar la fecha correcta");
});

test("buildPackagePayload: fechas se mandan SIN sufijo Z (bug fechas corridas 2026-07-16)", () => {
    // El backend normaliza esta fecha con NormalizeCalendarDate (BookingService) tomando
    // solo el día calendario — mandar con "Z" de más solo agrega ruido al contrato.
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        endDate: "2026-08-19",
        passengers: 2,
        supplierId: "supplier-1",
        unitNetCost: 0,
        unitSalePrice: 250000,
        currency: "ARS",
        rateId: "rate-1",
        roomBase: "double",
        newCatalogProduct: null,
    };
    const payload = buildPackagePayload(form, true);
    assert.equal(payload.startDate, "2026-08-12T00:00:00");
    assert.equal(payload.endDate, "2026-08-19T00:00:00");
});

// ─── Tests ADR-018: round-trip de edición para Paquete ───────────────────────

/**
 * Simula el builder de estado de edición para Paquete.
 * ADR-018: lee packageName como fuente primaria (ya era el campo correcto).
 * Fallback a description para servicios legacy.
 * endDate: se puebla desde serviceToEdit.endDate; null/undefined → string vacío (campo opcional).
 */
function buildPackageFormInitial(serviceToEdit) {
    if (!serviceToEdit) return { packageName: "", endDate: "", rateId: null, newCatalogProduct: null };
    const pasajeros = Math.max(Number(serviceToEdit.adults) || Number(serviceToEdit.passengers) || 1, 1);
    return {
        packageName: serviceToEdit.packageName || serviceToEdit.description || serviceToEdit.name || "",
        startDate: (serviceToEdit.startDate || "").split("T")[0] || "",
        // Round-trip: poblar endDate si el backend lo devuelve; fallback a vacío para paquetes viejos.
        endDate: (serviceToEdit.endDate || "").split("T")[0] || "",
        passengers: String(pasajeros),
        supplierId: serviceToEdit.supplierId || serviceToEdit.supplierPublicId || "",
        unitNetCost: pasajeros > 0 ? String(redondearDinero((serviceToEdit.netCost || 0) / pasajeros)) : "",
        unitSalePrice: pasajeros > 0 ? String(redondearDinero((serviceToEdit.salePrice || 0) / pasajeros)) : "",
        currency: serviceToEdit.currency || "ARS",
        roomBase: serviceToEdit.occupancyBase || "",
        // F2 Próximos Inicios: operatorPaymentDeadline eliminado del form inline (fecha queda en el backend).
        itinerary: serviceToEdit.itinerary || "",
        fileNumber: serviceToEdit.fileNumber || serviceToEdit.confirmationNumber || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
    };
}

/**
 * Validación del form de Paquete — versión con regla de fin >= salida.
 * Espeja la lógica de ServiceInlineCard.validarForm para el bloque "Paquete".
 */
function validarFormPaqueteConFechaFin(form) {
    if (!form.packageName?.trim()) return "Escribí el nombre del paquete.";
    if (!form.startDate) return "Elegí la fecha de salida.";
    // endDate es opcional; solo se valida cuando el usuario la cargó.
    if (form.endDate && form.startDate && form.endDate < form.startDate) {
        return "La fecha de fin no puede ser anterior a la salida.";
    }
    if (!form.unitSalePrice || Number(form.unitSalePrice) <= 0) return "Ingresá el precio de venta por persona.";
    if (!form.newCatalogProduct && !form.supplierId) return "Elegí el operador.";
    if (form.newCatalogProduct) {
        if (!form.newCatalogProduct.name?.trim()) return "Ingresá el nombre del paquete nuevo.";
        if (!form.newCatalogProduct.supplierPublicId) return "Elegí el operador del paquete nuevo.";
    }
    return null;
}

test("buildPackageFormInitial: ADR-018 — round-trip de edición lee packageName primero", () => {
    const serviceDesdeBackend = {
        packageName: "Iguazú 7 noches",
        description: "descripcion vieja ignorada",
        rateId: "rate-package-1",
    };
    const form = buildPackageFormInitial(serviceDesdeBackend);
    assert.equal(form.packageName, "Iguazú 7 noches");
});

test("buildPackageFormInitial: ADR-018 — fallback a description para servicios legacy (packageName null)", () => {
    const serviceLegacy = {
        packageName: null,
        description: "Iguazú (legacy)",
        rateId: null,
    };
    const form = buildPackageFormInitial(serviceLegacy);
    assert.equal(form.packageName, "Iguazú (legacy)");
});

// ─── Tests: round-trip endDate ────────────────────────────────────────────────

test("buildPackageFormInitial: endDate del backend se puebla en el form (round-trip)", () => {
    // Verifica que al editar un paquete ya guardado con fecha de fin,
    // el campo del form quede con la fecha correcta (sin la parte de hora).
    const serviceDesdeBackend = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12T00:00:00.000Z",
        endDate: "2026-08-19T00:00:00.000Z",
        adults: 2,
        netCost: 400000,
        salePrice: 500000,
        currency: "ARS",
        rateId: "rate-package-1",
    };
    const form = buildPackageFormInitial(serviceDesdeBackend);
    assert.equal(form.endDate, "2026-08-19", "endDate debe quedar como date-only (sin la hora)");
});

test("buildPackageFormInitial: endDate null del backend → string vacío (paquetes viejos sin fecha de fin)", () => {
    const serviceSinFin = {
        packageName: "Paquete sin fin",
        startDate: "2026-08-12T00:00:00.000Z",
        endDate: null,
        adults: 1,
        netCost: 0,
        salePrice: 100000,
        currency: "ARS",
        rateId: "rate-1",
    };
    const form = buildPackageFormInitial(serviceSinFin);
    assert.equal(form.endDate, "", "endDate null del backend debe quedar como string vacío en el form");
});

// ─── Tests: validación fin < salida ──────────────────────────────────────────

test("validarFormPaqueteConFechaFin: fin anterior a salida → error de coherencia de fechas", () => {
    // Un usuario que ingresó la fecha de fin antes que la de salida debe ver un error claro.
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-19",
        endDate: "2026-08-12", // anterior a startDate → inválido
        unitSalePrice: 250000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormPaqueteConFechaFin(form);
    assert.ok(error, "Debe haber un error cuando fin < salida");
    assert.match(error, /fin/, "El mensaje debe mencionar 'fin'");
    assert.match(error, /salida/, "El mensaje debe mencionar 'salida'");
});

test("validarFormPaqueteConFechaFin: endDate vacío → permitido (campo opcional)", () => {
    // La fecha de fin es opcional: no cargarla no debe bloquear el guardado.
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        endDate: "", // vacío → se permite
        unitSalePrice: 250000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormPaqueteConFechaFin(form);
    assert.equal(error, null, "endDate vacío no debe generar error");
});

test("validarFormPaqueteConFechaFin: endDate igual a salida → permitido (mismo día)", () => {
    const form = {
        packageName: "Day trip",
        startDate: "2026-08-12",
        endDate: "2026-08-12", // mismo día: 0 noches, pero válido
        unitSalePrice: 50000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormPaqueteConFechaFin(form);
    assert.equal(error, null, "endDate igual a startDate debe ser válido");
});

test("validarFormPaqueteConFechaFin: endDate posterior a salida → válido", () => {
    const form = {
        packageName: "Iguazú 7 noches",
        startDate: "2026-08-12",
        endDate: "2026-08-19",
        unitSalePrice: 250000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormPaqueteConFechaFin(form);
    assert.equal(error, null, "endDate posterior a startDate debe ser válido");
});
