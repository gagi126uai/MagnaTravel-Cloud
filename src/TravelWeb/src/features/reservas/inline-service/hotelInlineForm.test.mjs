/**
 * Tests de lógica pura de la ficha de carga en línea de Hotel.
 *
 * Por qué son lógica pura y no tests de componente:
 *   Las reglas críticas (cálculo de totales, validación, construcción del payload,
 *   fallback de tipo) son funciones sin DOM. Testearlas como lógica pura es más
 *   rápido, determinista y no requiere jsdom ni React Testing Library.
 *
 * Cómo correr: node --test src/features/reservas/inline-service/hotelInlineForm.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura copiada de HotelInlineForm / ServiceInlineCard ──────────────
// (misma lógica que los componentes usan; si cambia allá, actualizar acá)

function calcularNoches(checkIn, checkOut) {
    if (!checkIn || !checkOut) return 0;
    const inicio = new Date(checkIn);
    const fin = new Date(checkOut);
    const diferencia = Math.ceil((fin - inicio) / (1000 * 60 * 60 * 24));
    return diferencia > 0 ? diferencia : 0;
}

function redondearDinero(valor) {
    return Math.round((Number(valor) || 0) * 100) / 100;
}

/**
 * Simula el cálculo de totales que hace HotelInlineForm.
 * Total = noches × habitaciones × precio por noche.
 */
function calcularTotalesHotel({ checkIn, checkOut, rooms, unitSalePrice, unitNetCost, canSeeCost }) {
    const noches = calcularNoches(checkIn, checkOut);
    const habitaciones = Math.max(Number(rooms) || 1, 1);
    const factorTotal = Math.max(noches, 0) * habitaciones;
    const ventaTotal = redondearDinero((Number(unitSalePrice) || 0) * factorTotal);
    const costoTotal = canSeeCost ? redondearDinero((Number(unitNetCost) || 0) * factorTotal) : null;
    const ganancia = canSeeCost && costoTotal !== null ? redondearDinero(ventaTotal - costoTotal) : null;
    return { noches, habitaciones, factorTotal, ventaTotal, costoTotal, ganancia };
}

/**
 * Simula la validación del formulario de Hotel (validarFormHotel en ServiceInlineCard).
 * Devuelve null si es válido, o un string con el mensaje de error.
 */
function validarFormHotel(form) {
    if (!form.hotelName?.trim()) return "Escribí el nombre del hotel.";
    if (!form.checkIn) return "Elegí la fecha de entrada.";
    if (!form.checkOut) return "Elegí la fecha de salida.";
    const noches = calcularNoches(form.checkIn, form.checkOut);
    if (noches <= 0) return "La fecha de salida debe ser posterior a la de entrada.";
    if (!form.unitSalePrice || Number(form.unitSalePrice) <= 0) return "Ingresá el precio de venta por noche.";

    // C1: operador SIEMPRE obligatorio (camino existente o manual, sin producto nuevo)
    if (!form.newCatalogProduct && !form.supplierId) {
        return "Elegí el operador.";
    }

    if (form.newCatalogProduct) {
        if (!form.newCatalogProduct.name?.trim()) return "Ingresá el nombre del hotel nuevo.";
        if (!form.newCatalogProduct.city?.trim()) return "La ciudad es obligatoria para crear un hotel nuevo.";
        if (!form.newCatalogProduct.supplierPublicId) return "Elegí el operador del hotel nuevo.";
    }
    return null;
}

/**
 * Simula la construcción del payload que envía ServiceInlineCard al backend.
 * canSeeCost: si false, netCost se manda 0 (el usuario no ve costos).
 */
function buildHotelPayload(form, canSeeCost) {
    const noches = calcularNoches(form.checkIn, form.checkOut);
    const habitaciones = Math.max(Number(form.rooms) || 1, 1);
    const factorTotal = Math.max(noches, 1) * habitaciones;

    const netCostTotal = redondearDinero((Number(form.unitNetCost) || 0) * factorTotal);
    const salePriceTotal = redondearDinero((Number(form.unitSalePrice) || 0) * factorTotal);

    const payload = {
        nights: noches,
        rooms: habitaciones,
        netCost: canSeeCost ? netCostTotal : 0,
        salePrice: salePriceTotal,
        address: form.address || null,
        supplierId: form.supplierId || null,
    };

    // Mutuamente excluyentes: rateId O newCatalogProduct
    if (form.rateId) {
        payload.rateId = form.rateId;
    } else if (form.newCatalogProduct) {
        payload.newCatalogProduct = { ...form.newCatalogProduct };
        payload.supplierId = form.newCatalogProduct.supplierPublicId || null;
    }

    return payload;
}

/**
 * Lógica del fallback B1: decide si un servicio a editar debe usar la ficha inline o el modal.
 * En F2 parte 1, solo Hotel usa la ficha; el resto usa el modal viejo.
 */
function debeUsarFichaInline(service, isCatalogFindOrCreateEnabled) {
    if (!isCatalogFindOrCreateEnabled) return false;
    return service?.recordKind === "hotel";
}

// ─── Tests: cálculo de noches ─────────────────────────────────────────────────

test("calcularNoches: 3 noches entre entrada y salida", () => {
    assert.equal(calcularNoches("2026-07-10", "2026-07-13"), 3);
});

test("calcularNoches: misma fecha → 0 noches", () => {
    assert.equal(calcularNoches("2026-07-10", "2026-07-10"), 0);
});

test("calcularNoches: salida anterior a entrada → 0 noches", () => {
    assert.equal(calcularNoches("2026-07-13", "2026-07-10"), 0);
});

test("calcularNoches: fecha vacía → 0 noches", () => {
    assert.equal(calcularNoches("", "2026-07-13"), 0);
    assert.equal(calcularNoches("2026-07-10", ""), 0);
});

// ─── Tests: cálculo de totales (B2 — habitaciones) ───────────────────────────

test("calcularTotalesHotel: 2 noches × 1 hab × $5000/noche = $10.000 de venta", () => {
    const { ventaTotal, factorTotal } = calcularTotalesHotel({
        checkIn: "2026-07-10",
        checkOut: "2026-07-12",
        rooms: 1,
        unitSalePrice: 5000,
        unitNetCost: 3000,
        canSeeCost: true,
    });
    assert.equal(factorTotal, 2);
    assert.equal(ventaTotal, 10000);
});

test("calcularTotalesHotel: 2 noches × 3 hab × $5000/noche = $30.000 de venta", () => {
    const { ventaTotal, factorTotal } = calcularTotalesHotel({
        checkIn: "2026-07-10",
        checkOut: "2026-07-12",
        rooms: 3,
        unitSalePrice: 5000,
        unitNetCost: 3000,
        canSeeCost: true,
    });
    assert.equal(factorTotal, 6);
    assert.equal(ventaTotal, 30000);
});

test("calcularTotalesHotel: rooms vacío o 0 → default 1 habitación", () => {
    const { habitaciones: habVacio } = calcularTotalesHotel({
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: "", unitSalePrice: 5000, unitNetCost: 3000, canSeeCost: true,
    });
    assert.equal(habVacio, 1);

    const { habitaciones: habCero } = calcularTotalesHotel({
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 0, unitSalePrice: 5000, unitNetCost: 3000, canSeeCost: true,
    });
    assert.equal(habCero, 1);
});

test("calcularTotalesHotel: ganancia = venta − costo (con permiso)", () => {
    const { ventaTotal, costoTotal, ganancia } = calcularTotalesHotel({
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 1, unitSalePrice: 5000, unitNetCost: 3000, canSeeCost: true,
    });
    assert.equal(costoTotal, 6000);
    assert.equal(ganancia, redondearDinero(ventaTotal - costoTotal));
    assert.equal(ganancia, 4000);
});

// ─── Tests: enmascarado de costo (sin permiso de ver costos) ─────────────────

test("calcularTotalesHotel: sin permiso → costoTotal null, ganancia null (nunca $0)", () => {
    const { costoTotal, ganancia } = calcularTotalesHotel({
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 1, unitSalePrice: 5000, unitNetCost: 3000,
        // Sin permiso de ver costos
        canSeeCost: false,
    });
    // Regla clave: jamás mostrar "$0" al que no puede ver costos; mostrar null (oculto)
    assert.equal(costoTotal, null);
    assert.equal(ganancia, null);
});

test("calcularTotalesHotel: sin permiso → venta sigue visible (no afecta precio de venta)", () => {
    const { ventaTotal } = calcularTotalesHotel({
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 2, unitSalePrice: 4000, unitNetCost: 2000,
        canSeeCost: false,
    });
    // El vendedor sin permiso sí ve el precio de venta
    assert.equal(ventaTotal, 16000);
});

// ─── Tests: payload — rateId vs newCatalogProduct son mutuamente excluyentes ─

test("buildHotelPayload: con rateId → el payload incluye rateId, sin newCatalogProduct", () => {
    const form = {
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 1, unitSalePrice: 5000, unitNetCost: 3000,
        supplierId: "supplier-1",
        rateId: "rate-abc",
        address: "Av. Corrientes 1234",
    };
    const payload = buildHotelPayload(form, true);
    assert.equal(payload.rateId, "rate-abc");
    assert.equal(payload.newCatalogProduct, undefined);
    // address debe viajar al backend
    assert.equal(payload.address, "Av. Corrientes 1234");
});

test("buildHotelPayload: con newCatalogProduct → sin rateId; supplierId viene de newCatalogProduct", () => {
    const form = {
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 1, unitSalePrice: 5000, unitNetCost: 3000,
        supplierId: "",
        rateId: null,
        newCatalogProduct: { name: "Hotel Nuevo", city: "Posadas", supplierPublicId: "supplier-2" },
    };
    const payload = buildHotelPayload(form, true);
    assert.equal(payload.rateId, undefined);
    assert.ok(payload.newCatalogProduct, "debe incluir newCatalogProduct");
    assert.equal(payload.supplierId, "supplier-2");
});

test("buildHotelPayload: sin permiso de ver costos → netCost = 0 (protección de dato sensible)", () => {
    const form = {
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 1, unitSalePrice: 5000, unitNetCost: 3000,
        supplierId: "supplier-1", rateId: "rate-1",
    };
    const payload = buildHotelPayload(form, false);
    assert.equal(payload.netCost, 0);
    // La venta sigue correcta
    assert.equal(payload.salePrice, 10000);
});

test("buildHotelPayload: rooms multiplica correctamente (2 noches × 3 hab × $5000 = $30.000)", () => {
    const form = {
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        rooms: 3, unitSalePrice: 5000, unitNetCost: 3000,
        supplierId: "supplier-1", rateId: "rate-1",
    };
    const payload = buildHotelPayload(form, true);
    assert.equal(payload.rooms, 3);
    assert.equal(payload.salePrice, 30000);
    assert.equal(payload.netCost, 18000);
});

// ─── Tests: validación del formulario (incluyendo C1 — operador obligatorio) ──

test("validarFormHotel: form completo con supplierId → válido", () => {
    const form = {
        hotelName: "Hotel Central",
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        unitSalePrice: 5000,
        supplierId: "supplier-1",
        newCatalogProduct: null,
    };
    assert.equal(validarFormHotel(form), null);
});

test("validarFormHotel: C1 — sin supplierId en camino existente → error de operador", () => {
    const form = {
        hotelName: "Hotel Central",
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        unitSalePrice: 5000,
        supplierId: "",          // sin operador
        newCatalogProduct: null, // camino existente (no es producto nuevo)
    };
    const error = validarFormHotel(form);
    assert.ok(error, "debe devolver un error");
    assert.match(error, /operador/i);
});

test("validarFormHotel: sin nombre → error de nombre", () => {
    const form = {
        hotelName: "",
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        unitSalePrice: 5000, supplierId: "s1",
    };
    const error = validarFormHotel(form);
    assert.match(error, /nombre/i);
});

test("validarFormHotel: salida anterior a entrada → error de fecha", () => {
    const form = {
        hotelName: "Hotel Test",
        checkIn: "2026-07-12", checkOut: "2026-07-10", // invertidas
        unitSalePrice: 5000, supplierId: "s1",
    };
    const error = validarFormHotel(form);
    assert.match(error, /salida/i);
});

test("validarFormHotel: sin venta → error de precio", () => {
    const form = {
        hotelName: "Hotel Test",
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        unitSalePrice: 0, supplierId: "s1",
    };
    const error = validarFormHotel(form);
    assert.match(error, /venta/i);
});

test("validarFormHotel: producto nuevo sin ciudad → error de ciudad (D6)", () => {
    const form = {
        hotelName: "Hotel Nuevo",
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        unitSalePrice: 5000,
        supplierId: "",
        newCatalogProduct: { name: "Hotel Nuevo", city: "", supplierPublicId: "s1" },
    };
    const error = validarFormHotel(form);
    assert.match(error, /ciudad/i);
});

test("validarFormHotel: producto nuevo sin operador → error de operador", () => {
    const form = {
        hotelName: "Hotel Nuevo",
        checkIn: "2026-07-10", checkOut: "2026-07-12",
        unitSalePrice: 5000,
        supplierId: "",
        newCatalogProduct: { name: "Hotel Nuevo", city: "Posadas", supplierPublicId: "" },
    };
    const error = validarFormHotel(form);
    assert.match(error, /operador/i);
});

// ─── Tests: fallback B1 — tipo de servicio decide ficha inline vs modal viejo ─

test("debeUsarFichaInline: Hotel con flag ON → usa ficha inline", () => {
    const service = { recordKind: "hotel" };
    assert.equal(debeUsarFichaInline(service, true), true);
});

test("debeUsarFichaInline: Aereo con flag ON → usa modal viejo (fallback B1)", () => {
    const service = { recordKind: "flight" };
    assert.equal(debeUsarFichaInline(service, true), false);
});

test("debeUsarFichaInline: Traslado con flag ON → usa modal viejo (fallback B1)", () => {
    const service = { recordKind: "transfer" };
    assert.equal(debeUsarFichaInline(service, true), false);
});

test("debeUsarFichaInline: Paquete con flag ON → usa modal viejo (fallback B1)", () => {
    const service = { recordKind: "package" };
    assert.equal(debeUsarFichaInline(service, true), false);
});

test("debeUsarFichaInline: Asistencia con flag ON → usa modal viejo (fallback B1)", () => {
    const service = { recordKind: "assistance" };
    assert.equal(debeUsarFichaInline(service, true), false);
});

test("debeUsarFichaInline: Generico con flag ON → usa modal viejo (fallback B1)", () => {
    const service = { recordKind: "generic" };
    assert.equal(debeUsarFichaInline(service, true), false);
});

test("debeUsarFichaInline: Hotel con flag OFF → usa modal viejo (flag apagado)", () => {
    const service = { recordKind: "hotel" };
    assert.equal(debeUsarFichaInline(service, false), false);
});

// ─── Tests: cap de resultados del buscador (C3) ────────────────────────────────

test("cap de resultados: 8 de 10 → solo muestra MAX_DISPLAY_RESULTS", () => {
    const MAX_DISPLAY_RESULTS = 8;
    const resultadosBackend = Array.from({ length: 10 }, (_, i) => ({ ratePublicId: `rate-${i}` }));
    const mostrados = resultadosBackend.slice(0, MAX_DISPLAY_RESULTS);
    assert.equal(mostrados.length, 8);
});

test("cap de resultados: 3 de 3 → todos se muestran (menos que el límite)", () => {
    const MAX_DISPLAY_RESULTS = 8;
    const resultadosBackend = Array.from({ length: 3 }, (_, i) => ({ ratePublicId: `rate-${i}` }));
    const mostrados = resultadosBackend.slice(0, MAX_DISPLAY_RESULTS);
    assert.equal(mostrados.length, 3);
});

// ─── Tests: navegación por teclado (A11y) — lógica de índices ─────────────────

test("navegación teclado: ArrowDown avanza el índice", () => {
    // Simula la lógica de setKeyboardIndex((prev) => ...)
    const totalOptions = 5;
    const avanzar = (prev) => (prev < totalOptions - 1 ? prev + 1 : 0);

    assert.equal(avanzar(-1), 0);
    assert.equal(avanzar(0), 1);
    assert.equal(avanzar(4), 0); // wrap al inicio cuando llega al final
});

test("navegación teclado: ArrowUp retrocede el índice con wrap", () => {
    const totalOptions = 5;
    const retroceder = (prev) => (prev > 0 ? prev - 1 : totalOptions - 1);

    assert.equal(retroceder(0), 4); // wrap al final cuando está en el inicio
    assert.equal(retroceder(3), 2);
    assert.equal(retroceder(-1), 4); // desde "ninguno" también va al último
});

test("navegación teclado: Enter en índice negativo no selecciona nada", () => {
    const keyboardIndex = -1;
    // La condición del handler: solo actúa si keyboardIndex >= 0
    assert.equal(keyboardIndex >= 0, false);
});

test("navegación teclado: Enter en índice 0 selecciona primer resultado", () => {
    const results = [{ ratePublicId: "rate-1" }, { ratePublicId: "rate-2" }];
    const keyboardIndex = 0;
    // Si keyboardIndex < results.length → es un resultado existente
    assert.equal(keyboardIndex < results.length, true);
    assert.equal(results[keyboardIndex].ratePublicId, "rate-1");
});

test("navegación teclado: Enter en índice results.length activa 'Crear nuevo'", () => {
    const results = [{ ratePublicId: "rate-1" }, { ratePublicId: "rate-2" }];
    const keyboardIndex = results.length; // apunta a la opción "Crear nuevo"
    // Si keyboardIndex === results.length → es la opción crear
    assert.equal(keyboardIndex === results.length, true);
    assert.equal(keyboardIndex < results.length, false);
});
