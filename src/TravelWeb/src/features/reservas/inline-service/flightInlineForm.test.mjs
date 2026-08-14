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
 * F2: ticketingDeadline eliminado del payload (el aviso viene del backend por firstStartDate).
 */
function buildFlightPayload(form, canSeeCost) {
    const payload = {
        // ADR-018: identidad en productName, no en description
        productName: form.routeName?.trim() || "",
        // SEMÁNTICA INTOCABLE (obra "PDF completo", 2026-08-13): departureTime/arrivalTime
        // siguen siendo la VENTANA del viaje (fecha a medianoche), nunca un horario real.
        departureTime: form.departureDate ? `${form.departureDate}T00:00:00` : null,
        arrivalTime: form.returnDate ? `${form.returnDate}T00:00:00` : null,
        passengerCount: form.passengers ? Number(form.passengers) : null,
        supplierId: form.supplierId || null,
        netCost: canSeeCost ? redondearDinero(Number(form.netCost) || 0) : 0,
        salePrice: redondearDinero(Number(form.salePrice) || 0),
        currency: form.currency || "ARS",
        // ticketingDeadline eliminado en F2 (Próximos Inicios)
        pnr: form.pnr || null,
        // cabinClass: null cuando no se eligió (""); el backend lo acepta como opcional.
        cabinClass: form.cabinClass || null,
        // Semáforo de DNI vencido para cabotaje (2026-08-03): a diferencia de cabinClass, acá ""
        // (Sin definir) NO se manda como null. El backend interpreta null/ausente como "no tocar
        // el ámbito ya guardado"; para "volver a Sin definir" a propósito hace falta el token
        // literal "SinDefinir" (ServiceGeographicScopeText.Cleared en el backend), que SI reconoce.
        geographicScope: form.geographicScope || "SinDefinir",
        // Aeropuertos (spec 2026-08-13, §2): texto libre, "" -> null.
        origin: form.origin?.trim() || null,
        originCity: form.originCity?.trim() || null,
        destination: form.destination?.trim() || null,
        destinationCity: form.destinationCity?.trim() || null,
        // Horarios del papel (spec 2026-08-13, §1/§8.2): "HH:mm" tal cual llega del <input
        // type="time">, "" -> null. NUNCA viajan dentro de departureTime/arrivalTime.
        outboundDepartureTime: form.outboundDepartureTime || null,
        outboundArrivalTime: form.outboundArrivalTime || null,
        returnDepartureTime: form.returnDepartureTime || null,
        returnArrivalTime: form.returnArrivalTime || null,
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
    if (!serviceToEdit) return { routeName: "", rateId: null, newCatalogProduct: null, cabinClass: "", geographicScope: "" };
    return {
        routeName: serviceToEdit.productName || serviceToEdit.description || serviceToEdit.name || "",
        rateId: serviceToEdit.rateId || null,
        newCatalogProduct: null,
        // Round-trip: el backend devuelve cabinClass en FlightSegmentDto; fallback "" (Sin especificar).
        cabinClass: serviceToEdit.cabinClass || "",
        // Semáforo de DNI vencido para cabotaje: FlightSegmentDto SI expone este campo en la
        // lectura, como texto legible ("Nacional"/"Internacional") o null si nunca se definió.
        // Fallback "" (Sin definir) cuando el backend devuelve null.
        geographicScope: serviceToEdit.geographicScope || "",
        // Round-trip: aeropuertos, texto libre, fallback "".
        origin: serviceToEdit.origin || "",
        originCity: serviceToEdit.originCity || "",
        destination: serviceToEdit.destination || "",
        destinationCity: serviceToEdit.destinationCity || "",
        // Round-trip: el backend devuelve TimeOnly como "HH:mm:ss" (ej. "08:30:00"); el
        // casillero necesita "HH:mm" — se corta a los primeros 5 caracteres.
        outboundDepartureTime: (serviceToEdit.outboundDepartureTime || "").slice(0, 5),
        outboundArrivalTime: (serviceToEdit.outboundArrivalTime || "").slice(0, 5),
        returnDepartureTime: (serviceToEdit.returnDepartureTime || "").slice(0, 5),
        returnArrivalTime: (serviceToEdit.returnArrivalTime || "").slice(0, 5),
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

// ─── Tests: F2 — ticketingDeadline eliminado del payload ─────────────────────

test("buildFlightPayload: F2 → ticketingDeadline no existe más en el payload (eliminado en F2)", () => {
    // F2 (Próximos Inicios): el aviso de inicio se calcula en el backend desde firstStartDate.
    // Los campos emissionDeadline/ticketingDeadline ya no se envían desde la ficha inline.
    const form = {
        routeName: "AEP–IGR LATAM",
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
    // En F2 ya no va ticketingDeadline en el payload
    assert.equal(payload.ticketingDeadline, undefined);
    assert.equal(payload.emissionDeadline, undefined);
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

// ─── Tests: geographicScope — semáforo de DNI vencido para cabotaje (2026-08-03) ──

test("buildFlightPayload: geographicScope 'Nacional' elegido → va tal cual en el payload", () => {
    const form = {
        routeName: "AEP–COR",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 90000,
        currency: "ARS",
        rateId: "rate-1",
        geographicScope: "Nacional",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.geographicScope, "Nacional");
});

test("buildFlightPayload: geographicScope 'Sin definir' (vacío) → token 'SinDefinir' en el payload (no null)", () => {
    // El backend distingue "no mandé nada" (null → no tocar) de "elegí Sin definir a propósito"
    // (token "SinDefinir" → limpia el ámbito). El form manda siempre el token, nunca null.
    const form = {
        routeName: "AEP–IGR",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        geographicScope: "",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.geographicScope, "SinDefinir");
});

test("buildFlightPayload: estaba 'Nacional', el vendedor elige 'Sin definir' → el payload manda 'SinDefinir'", () => {
    // Caso real de esta obra: un vuelo mal marcado como Nacional se corrige a Sin definir.
    // Si acá mandáramos null, el backend lo interpretaría como "no tocar" y el vuelo quedaría
    // avisando para siempre. El token explícito es la única forma de borrar el ámbito guardado.
    const serviceDesdeBackend = { productName: "AEP–COR", geographicScope: "Nacional", rateId: "rate-1" };
    const formEditado = { ...buildFlightFormInitial(serviceDesdeBackend), geographicScope: "" };
    const payload = buildFlightPayload(formEditado, true);
    assert.equal(payload.geographicScope, "SinDefinir");
});

test("buildFlightFormInitial: geographicScope persistido se precarga en el form (cuando el backend lo devuelva)", () => {
    const serviceDesdeBackend = { productName: "AEP–COR", geographicScope: "Nacional", rateId: "rate-1" };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.geographicScope, "Nacional");
});

test("buildFlightFormInitial: sin geographicScope del backend → '' en el form (Sin definir)", () => {
    const serviceDesdeBackend = { productName: "AEP–IGR", rateId: "rate-1" };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.geographicScope, "");
});

// ─── Tests: obra "PDF completo" (2026-08-13) — aeropuertos y horarios ─────────

test("buildFlightPayload: aeropuertos/ciudad tipeados → van tal cual en el payload", () => {
    const form = {
        routeName: "AEP–MIA",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        origin: "EZE",
        originCity: "Buenos Aires",
        destination: "MIA",
        destinationCity: "Miami",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.origin, "EZE");
    assert.equal(payload.originCity, "Buenos Aires");
    assert.equal(payload.destination, "MIA");
    assert.equal(payload.destinationCity, "Miami");
});

test("buildFlightPayload: aeropuertos vacíos → null en el payload (ninguno es obligatorio)", () => {
    const form = {
        routeName: "AEP–MIA",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.origin, null);
    assert.equal(payload.originCity, null);
    assert.equal(payload.destination, null);
    assert.equal(payload.destinationCity, null);
});

test("buildFlightPayload: los 4 horarios (Sale/Llega de ida y vuelta) viajan por sus propios campos, NUNCA dentro de departureTime/arrivalTime", () => {
    // Regla dura de la obra (spec 2026-08-13, §5/§8.2, semántica intocable en FlightSegment.cs):
    // departureTime/arrivalTime son SOLO la ventana del viaje (fecha a medianoche).
    const form = {
        routeName: "AEP–MIA",
        departureDate: "2026-08-12",
        returnDate: "2026-08-19",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        outboundDepartureTime: "08:30",
        outboundArrivalTime: "11:45",
        returnDepartureTime: "19:00",
        returnArrivalTime: "23:10",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.outboundDepartureTime, "08:30");
    assert.equal(payload.outboundArrivalTime, "11:45");
    assert.equal(payload.returnDepartureTime, "19:00");
    assert.equal(payload.returnArrivalTime, "23:10");
    // La ventana del viaje sigue siendo SOLO fecha a medianoche, ningún horario la pisa.
    assert.equal(payload.departureTime, "2026-08-12T00:00:00");
    assert.equal(payload.arrivalTime, "2026-08-19T00:00:00");
});

test("buildFlightPayload: los 4 horarios vacíos → null (ninguno es obligatorio, el PDF omite la línea)", () => {
    const form = {
        routeName: "AEP–MIA",
        departureDate: "2026-08-12",
        supplierId: "supplier-1",
        netCost: 0,
        salePrice: 1800000,
        currency: "ARS",
        rateId: "rate-1",
        newCatalogProduct: null,
    };
    const payload = buildFlightPayload(form, true);
    assert.equal(payload.outboundDepartureTime, null);
    assert.equal(payload.outboundArrivalTime, null);
    assert.equal(payload.returnDepartureTime, null);
    assert.equal(payload.returnArrivalTime, null);
});

test("buildFlightFormInitial: round-trip — el backend devuelve TimeOnly 'HH:mm:ss', el form corta a 'HH:mm'", () => {
    // TimeOnly de .NET serializa con segundos (ej. "08:30:00"); el <input type=\"time\">
    // necesita exactamente 5 caracteres ("08:30") para mostrar el valor sin warning de React.
    const serviceDesdeBackend = {
        productName: "AEP–MIA",
        rateId: "rate-1",
        outboundDepartureTime: "08:30:00",
        outboundArrivalTime: "11:45:00",
        returnDepartureTime: "19:00:00",
        returnArrivalTime: "23:10:00",
    };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.outboundDepartureTime, "08:30");
    assert.equal(form.outboundArrivalTime, "11:45");
    assert.equal(form.returnDepartureTime, "19:00");
    assert.equal(form.returnArrivalTime, "23:10");
});

test("buildFlightFormInitial: sin horarios del backend → '' en el form (nunca undefined)", () => {
    const serviceDesdeBackend = { productName: "AEP–MIA", rateId: "rate-1" };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.outboundDepartureTime, "");
    assert.equal(form.outboundArrivalTime, "");
    assert.equal(form.returnDepartureTime, "");
    assert.equal(form.returnArrivalTime, "");
});

test("buildFlightFormInitial: round-trip — aeropuertos/ciudad persistidos se precargan en el form", () => {
    const serviceDesdeBackend = {
        productName: "AEP–MIA",
        rateId: "rate-1",
        origin: "EZE",
        originCity: "Buenos Aires",
        destination: "MIA",
        destinationCity: "Miami",
    };
    const form = buildFlightFormInitial(serviceDesdeBackend);
    assert.equal(form.origin, "EZE");
    assert.equal(form.originCity, "Buenos Aires");
    assert.equal(form.destination, "MIA");
    assert.equal(form.destinationCity, "Miami");
});
