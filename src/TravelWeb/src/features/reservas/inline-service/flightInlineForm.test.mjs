/**
 * Tests de lógica pura de la ficha de carga en línea de Aéreo.
 *
 * Cubre: cálculo de totales, dos caminos (rateId vs newCatalogProduct),
 * enmascarado de costo, validación de campos obligatorios.
 *
 * Cómo correr: node --test src/features/reservas/inline-service/flightInlineForm.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de FlightInlineForm / ServiceInlineCard ─────────────

function redondearDinero(valor) {
    return Math.round((Number(valor) || 0) * 100) / 100;
}

/**
 * Calcula los totales del vuelo.
 * Aéreo: precio total directo (sin multiplicar por días ni pasajeros).
 */
function calcularTotalesVuelo({ salePrice, netCost, canSeeCost }) {
    const ventaTotal = redondearDinero(Number(salePrice) || 0);
    const costoTotal = canSeeCost ? redondearDinero(Number(netCost) || 0) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { ventaTotal, costoTotal, ganancia };
}

/**
 * Validación del form de Aéreo.
 * Devuelve null si es válido, o string con el mensaje de error.
 */
function validarFormVuelo(form) {
    if (!form.routeName?.trim()) return "Escribí la ruta o aerolínea.";
    if (!form.departureDate) return "Elegí la fecha de ida.";
    if (!form.salePrice || Number(form.salePrice) <= 0) return "Ingresá el precio de venta.";
    if (!form.newCatalogProduct && !form.supplierId) return "Elegí el operador o consolidador.";
    if (form.newCatalogProduct) {
        if (!form.newCatalogProduct.name?.trim()) return "Ingresá el nombre de la ruta nueva.";
        if (!form.newCatalogProduct.supplierPublicId) return "Elegí el operador del vuelo nuevo.";
    }
    return null;
}

/**
 * Construye el payload del vuelo para el backend.
 * ADR-018: la identidad del vuelo va en productName (no en description).
 * El estado interno usa emissionDeadline; el payload envía ticketingDeadline.
 */
function buildFlightPayload(form, canSeeCost) {
    const payload = {
        // ADR-018: identidad en productName, no en description
        productName: form.routeName?.trim() || "",
        departureTime: form.departureDate ? `${form.departureDate}T00:00:00` : null,
        arrivalTime: form.returnDate ? `${form.returnDate}T00:00:00` : null,
        passengerCount: form.passengers ? Number(form.passengers) : null,
        supplierId: form.supplierId || null,
        netCost: canSeeCost ? redondearDinero(Number(form.netCost) || 0) : 0,
        salePrice: redondearDinero(Number(form.salePrice) || 0),
        currency: form.currency || "ARS",
        // El backend espera ticketingDeadline (no emissionDeadline)
        ticketingDeadline: form.emissionDeadline || null,
        pnr: form.pnr || null,
        // cabinClass: null cuando no se eligió (""); el backend lo acepta como opcional.
        cabinClass: form.cabinClass || null,
    };
    if (form.rateId) {
        payload.rateId = form.rateId;
    } else if (form.newCatalogProduct) {
        payload.newCatalogProduct = { ...form.newCatalogProduct };
        payload.supplierId = form.newCatalogProduct.supplierPublicId || null;
    }
    return payload;
}

/**
 * Simula el builder de estado de edición para Aéreo.
 * ADR-018: lee productName como fuente primaria de la identidad.
 * cabinClass: lee del backend con fallback "" (Sin especificar).
 */
function buildFlightFormInitial(serviceToEdit) {
    if (!serviceToEdit) return { routeName: "", rateId: null, newCatalogProduct: null, cabinClass: "" };
    return {
        routeName: serviceToEdit.productName || serviceToEdit.description || serviceToEdit.name || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
        // Round-trip: el backend devuelve cabinClass en FlightSegmentDto; fallback "" (Sin especificar).
        cabinClass: serviceToEdit.cabinClass || "",
    };
}

// ─── Tests: cálculo de totales (precio total directo) ────────────────────────

test("calcularTotalesVuelo: precio de venta se pasa directo sin multiplicar", () => {
    const { ventaTotal } = calcularTotalesVuelo({
        salePrice: 1800000,
        netCost: 1500000,
        canSeeCost: true,
    });
    assert.equal(ventaTotal, 1800000);
});

test("calcularTotalesVuelo: ganancia = venta − costo (con permiso)", () => {
    const { ventaTotal, costoTotal, ganancia } = calcularTotalesVuelo({
        salePrice: 1800000,
        netCost: 1500000,
        canSeeCost: true,
    });
    assert.equal(costoTotal, 1500000);
    assert.equal(ganancia, 300000);
});

test("calcularTotalesVuelo: sin permiso → costo null, ganancia null (nunca $0)", () => {
    const { costoTotal, ganancia } = calcularTotalesVuelo({
        salePrice: 1800000,
        netCost: 1500000,
        canSeeCost: false,
    });
    assert.equal(costoTotal, null);
    assert.equal(ganancia, null);
});

test("calcularTotalesVuelo: sin permiso → venta sigue visible", () => {
    const { ventaTotal } = calcularTotalesVuelo({
        salePrice: 500000,
        netCost: 400000,
        canSeeCost: false,
    });
    assert.equal(ventaTotal, 500000);
});

test("calcularTotalesVuelo: salePrice vacío → ventaTotal 0", () => {
    const { ventaTotal } = calcularTotalesVuelo({ salePrice: "", netCost: "", canSeeCost: true });
    assert.equal(ventaTotal, 0);
});

// ─── Tests: validación del formulario ────────────────────────────────────────

test("validarFormVuelo: form completo con supplierId → válido", () => {
    const form = {
        routeName: "AEP–IGR LATAM",
        departureDate: "2026-08-12",
        salePrice: 1800000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    assert.equal(validarFormVuelo(form), null);
});

test("validarFormVuelo: sin ruta → error de ruta", () => {
    const form = {
        routeName: "",
        departureDate: "2026-08-12",
        salePrice: 1800000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormVuelo(form);
    assert.ok(error, "debe devolver un error");
    assert.match(error, /ruta/i);
});

test("validarFormVuelo: sin fecha de ida → error de fecha", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "",
        salePrice: 1800000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormVuelo(form);
    assert.match(error, /fecha/i);
});

test("validarFormVuelo: sin precio de venta → error de precio", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        salePrice: 0,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    const error = validarFormVuelo(form);
    assert.match(error, /venta/i);
});

test("validarFormVuelo: sin operador en camino existente → error de operador", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        salePrice: 1800000,
        supplierId: "",
        newCatalogProduct: null,
    };
    const error = validarFormVuelo(form);
    assert.match(error, /operador/i);
});

test("validarFormVuelo: producto nuevo sin operador → error de operador", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        salePrice: 1800000,
        supplierId: "",
        newCatalogProduct: { name: "AEP–IGR nueva", supplierPublicId: "" },
    };
    const error = validarFormVuelo(form);
    assert.match(error, /operador/i);
});

// ─── Tests: payload — rateId vs newCatalogProduct son mutuamente excluyentes ─

test("buildFlightPayload: con rateId → el payload incluye rateId, sin newCatalogProduct", () => {
    const form = {
        routeName: "AEP–IGR LATAM",
        departureDate: "2026-08-12",
        returnDate: "2026-08-19",
        passengers: 4,
        supplierId: "supplier-1",
        netCost: 1500000,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-flight-1",
        pnr: "ABC123",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.rateId, "rate-flight-1");
    assert.equal(payload.newCatalogProduct, undefined);
    assert.equal(payload.pnr, "ABC123");
});

// ─── Tests ADR-018: identidad en productName, no en description ───────────────

test("buildFlightPayload: ADR-018 — la identidad del vuelo va en productName, NO en description", () => {
    // Regla ADR-018 §4-bis: productName = texto que el vendedor vio/tipeo.
    // El backend (FlightSegment) guarda la identidad en ProductName, no en Description.
    const form = {
        routeName: "AEP–IGR LATAM",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.productName, "AEP–IGR LATAM");
    assert.equal(payload.description, undefined, "description NO debe aparecer en el payload de aéreo");
});

test("buildFlightPayload: ADR-018 — routeName vacío → productName cadena vacía (no undefined)", () => {
    const form = {
        routeName: "",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.productName, "");
});

test("buildFlightFormInitial: ADR-018 — round-trip de edición lee productName primero", () => {
    // Al editar un vuelo creado con ADR-018, el campo del buscador se puebla desde productName.
    const serviceDesdeBackend = {
        productName: "AEP–IGR LATAM",
        description: "descripcion vieja ignorada",
        rateId: "rate-1",
    };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.routeName, "AEP–IGR LATAM");
});

test("buildFlightFormInitial: ADR-018 — fallback a description para servicios legacy (productName null)", () => {
    // Servicios cargados antes de ADR-018 no tienen productName → se cae al description.
    const serviceLegacy = {
        productName: null,
        description: "AEP–IGR (legacy)",
        rateId: null,
    };
    const form = buildFlightFormInitial(serviceLegacy);
    assert.equal(form.routeName, "AEP–IGR (legacy)");
});

test("buildFlightPayload: con newCatalogProduct → sin rateId; supplierId viene de newCatalogProduct", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        supplierId: "",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: null,
        pnr: "",
        newCatalogProduct: { name: "AEP–IGR LATAM", supplierPublicId: "supplier-2" },
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.rateId, undefined);
    assert.ok(payload.newCatalogProduct, "debe incluir newCatalogProduct");
    assert.equal(payload.supplierId, "supplier-2");
});

test("buildFlightPayload: sin permiso → netCost = 0 (protección de dato sensible)", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 1500000,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, false);
    assert.equal(payload.netCost, 0);
    assert.equal(payload.salePrice, 1800000);
});

test("buildFlightPayload: fecha de regreso vacía → arrivalTime null", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        returnDate: "",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.arrivalTime, null);
});

// ─── Tests: nombre correcto del campo de deadline ────────────────────────────

test("buildFlightPayload: el deadline va como ticketingDeadline, NO como emissionDeadline", () => {
    // El backend (FlightSegmentDto) espera ticketingDeadline.
    // El estado interno del form lo llama emissionDeadline, pero el payload
    // debe renombrarlo al enviarlo.
    const form = {
        routeName: "AEP–IGR LATAM",
        departureDate: "2026-08-12",
        returnDate: "",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        emissionDeadline: "2026-07-15",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    // Debe aparecer con el nombre que el backend espera
    assert.equal(payload.ticketingDeadline, "2026-07-15");
    // NO debe aparecer con el nombre viejo
    assert.equal(payload.emissionDeadline, undefined);
});

test("buildFlightPayload: emissionDeadline vacío → ticketingDeadline null", () => {
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        emissionDeadline: "",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.ticketingDeadline, null);
});

// ─── Tests: cabinClass — desplegable opcional dentro de "Más detalles" ────────

test("buildFlightPayload: cabinClass elegida → va en payload con el valor exacto del select", () => {
    // Los valores del select (Economy, Premium, Business, First) son los mismos que
    // el modal viejo (ServiceFormModal:382-386) para coherencia con el backend.
    const form = {
        routeName: "AEP–IGR LATAM",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        emissionDeadline: "",
        cabinClass: "Business",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.cabinClass, "Business");
});

test("buildFlightPayload: cabinClass 'Premium' (Premium Economy) va como 'Premium' en payload", () => {
    // El select muestra "Premium Economy" al usuario pero el value es "Premium"
    // (igual que el modal viejo). El backend espera "Premium".
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        emissionDeadline: "",
        cabinClass: "Premium",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.cabinClass, "Premium");
});

test("buildFlightPayload: cabinClass vacía → null en payload (Sin especificar no se envía)", () => {
    // "" (Sin especificar) se convierte en null con || null.
    // El backend acepta null en cabinClass (campo opcional).
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        emissionDeadline: "",
        cabinClass: "",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.cabinClass, null);
});

test("buildFlightFormInitial: round-trip cabinClass persistida → se precarga en el form", () => {
    // Al editar un vuelo que tiene cabinClass guardada (ej: "First"), el select debe
    // mostrarlo seleccionado para que el vendedor lo vea y pueda corregirlo.
    const serviceDesdeBackend = {
        productName: "AEP–IGR LATAM",
        cabinClass: "First",
        rateId: "rate-1",
    };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.cabinClass, "First");
});

test("buildFlightFormInitial: round-trip cabinClass null del backend → '' en el form (no undefined)", () => {
    // Vuelos guardados antes de este campo traen cabinClass=null; debe mapearse a ""
    // para que el select tenga value controlado (no undefined que causa warning React).
    const serviceDesdeBackend = {
        productName: "AEP–IGR",
        cabinClass: null,
        rateId: null,
    };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.cabinClass, "");
});
