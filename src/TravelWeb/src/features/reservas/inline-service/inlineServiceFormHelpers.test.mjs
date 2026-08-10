import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    resolverCamposALimpiarAlCrearNuevo,
    aplicarInterpretacionComoSugerencia,
    resolverNombreEnCasillero,
    resolverPatchDeVentaDelCatalogo,
    resolverOperadorSugeridoParaProductoNuevo,
} from "./inlineServiceFormHelpers.js";

// ─── resolverCamposALimpiarAlCrearNuevo (Bug #28, Tanda 4, 2026-07-24) ────────────────
// "Crear nuevo" en el buscador solo debe limpiar los campos que TODAVÍA son sugerencias
// sin tocar; lo que el usuario tipeó a mano se preserva.

describe("resolverCamposALimpiarAlCrearNuevo", () => {
    const valoresPorDefecto = { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" };

    it("form recién abierto (todo sugerido en false, nunca hubo selección): preserva TODO lo que el usuario tipeó a mano", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "5000", unitSalePrice: "7000", currency: "USD" };
        const camposSugeridos = { supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposSugeridos, valoresPorDefecto);

        assert.deepEqual(resultado, valoresActuales);
    });

    it("todos los campos siguen siendo sugerencia sin tocar (venían de elegir OTRO producto): se limpian todos", () => {
        const valoresActuales = { supplierId: "sup-viejo", unitNetCost: "5000", unitSalePrice: "7000", currency: "USD" };
        const camposSugeridos = { supplierId: true, unitNetCost: true, unitSalePrice: true, currency: true };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposSugeridos, valoresPorDefecto);

        assert.deepEqual(resultado, valoresPorDefecto);
    });

    it("mezcla: el usuario editó el costo a mano pero dejó el resto como vino de la sugerencia", () => {
        const valoresActuales = { supplierId: "sup-viejo", unitNetCost: "9999", unitSalePrice: "7000", currency: "USD" };
        // El usuario tocó unitNetCost (quedó en false); el resto sigue en true (sugerido, sin tocar)
        const camposSugeridos = { supplierId: true, unitNetCost: false, unitSalePrice: true, currency: true };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposSugeridos, valoresPorDefecto);

        // El campo tocado a mano se preserva tal cual
        assert.equal(resultado.unitNetCost, "9999");
        // Los que seguían sugeridos se limpian a su valor por defecto
        assert.equal(resultado.supplierId, "");
        assert.equal(resultado.unitSalePrice, "");
        assert.equal(resultado.currency, "ARS");
    });

    it("camposSugeridos sin registro para un campo (undefined): se trata como sugerido, se limpia (opción segura)", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "1000", unitSalePrice: "2000", currency: "USD" };
        const camposSugeridos = {}; // ningún campo tiene registro

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposSugeridos, valoresPorDefecto);

        assert.deepEqual(resultado, valoresPorDefecto);
    });

    it("camposSugeridos null/undefined no rompe: se limpian todos los campos (opción segura)", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "1000", unitSalePrice: "2000", currency: "USD" };

        assert.deepEqual(resolverCamposALimpiarAlCrearNuevo(valoresActuales, null, valoresPorDefecto), valoresPorDefecto);
        assert.deepEqual(resolverCamposALimpiarAlCrearNuevo(valoresActuales, undefined, valoresPorDefecto), valoresPorDefecto);
    });

    it("respeta las claves de netCost/salePrice sin el prefijo 'unit' (Aéreo/Traslado usan otros nombres)", () => {
        const valoresPorDefectoVuelo = { supplierId: "", netCost: "", salePrice: "", currency: "ARS" };
        const valoresActuales = { supplierId: "sup-1", netCost: "1500", salePrice: "2500", currency: "USD" };
        const camposSugeridos = { supplierId: false, netCost: false, salePrice: false, currency: false };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposSugeridos, valoresPorDefectoVuelo);

        assert.deepEqual(resultado, valoresActuales);
    });

    it("el resultado SOLO trae las claves de valoresPorDefecto (no arrastra campos ajenos del form)", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "1000", unitSalePrice: "2000", currency: "USD", hotelName: "Hotel X" };
        const camposSugeridos = { supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposSugeridos, valoresPorDefecto);

        assert.deepEqual(Object.keys(resultado).sort(), Object.keys(valoresPorDefecto).sort());
    });
});

// ─── aplicarInterpretacionComoSugerencia (D13, spec FIRMADA 2026-08-10) ───────────────
// El "hack" de la frase completa: lo que el motor entendió (operador, fechas) se agrega
// como sugerencia amarilla, sin pisar nunca lo que la venta real del producto ya trajo.

describe("aplicarInterpretacionComoSugerencia", () => {
    const FORM_VACIO = { supplierId: "", checkIn: "", checkOut: "" };

    it("sin interpretación (motor no entendió/no disparó): no agrega nada", () => {
        const resultado = aplicarInterpretacionComoSugerencia(null, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
    });

    it("con operador y fechas, form vacío (sin venta que ya trajera operador): agrega los tres", () => {
        const interpretacion = {
            supplier: { supplierPublicId: "sup-1", name: "Delfos" },
            dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" },
        };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO });
        assert.deepEqual(resultado.patch, {
            supplierId: "sup-1",
            supplierName: "Delfos",
            checkIn: "2026-02-10",
            checkOut: "2026-02-15",
        });
        assert.deepEqual(resultado.sugeridos, { supplierId: true, checkIn: true, checkOut: true });
    });

    it("la venta real YA trajo operador (yaHaySupplierDeLaVenta=true): NUNCA pisa, aunque la interpretación tenga uno", () => {
        const interpretacion = { supplier: { supplierPublicId: "sup-1", name: "Delfos" }, dates: null };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO });
        assert.equal(resultado.patch.supplierId, undefined);
        assert.equal(resultado.sugeridos.supplierId, undefined);
    });

    it("fix C-5(a): el vendedor YA tiene un operador tipeado a mano en el form — la interpretación NO lo pisa", () => {
        const interpretacion = { supplier: { supplierPublicId: "sup-1", name: "Delfos" }, dates: null };
        const formConOperadorAMano = { supplierId: "sup-manual", checkIn: "", checkOut: "" };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: formConOperadorAMano });
        assert.equal(resultado.patch.supplierId, undefined);
        assert.equal(resultado.sugeridos.supplierId, undefined);
    });

    it("fix C-5(a): el vendedor YA tiene checkIn tipeado a mano — la interpretación NO lo pisa, pero checkOut (vacío) sí se completa", () => {
        const interpretacion = { supplier: null, dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" } };
        const formConFechaAMano = { supplierId: "", checkIn: "2026-03-01", checkOut: "" };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: formConFechaAMano });
        assert.equal(resultado.patch.checkIn, undefined);
        assert.equal(resultado.sugeridos.checkIn, undefined);
        assert.equal(resultado.patch.checkOut, "2026-02-15");
        assert.equal(resultado.sugeridos.checkOut, true);
    });

    it("fix C-5(a): las DOS fechas ya tipeadas a mano — la interpretación no toca ninguna", () => {
        const interpretacion = { supplier: null, dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" } };
        const formConAmbasFechas = { supplierId: "", checkIn: "2026-03-01", checkOut: "2026-03-05" };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: formConAmbasFechas });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
    });

    it("Traslado: un solo campo de fecha (sin 'hasta') — 'to' de la interpretación se ignora", () => {
        const interpretacion = { supplier: null, dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" } };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["pickupDate"], formActual: { supplierId: "", pickupDate: "" } });
        assert.deepEqual(resultado.patch, { pickupDate: "2026-02-10" });
        assert.deepEqual(resultado.sugeridos, { pickupDate: true });
    });

    it("solo 'to' sin 'from': se aplica el campo que corresponde nomás", () => {
        const interpretacion = { supplier: null, dates: { from: null, to: "2026-02-15T00:00:00Z" } };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO });
        assert.deepEqual(resultado.patch, { checkOut: "2026-02-15" });
        assert.deepEqual(resultado.sugeridos, { checkOut: true });
    });

    it("interpretación sin nada utilizable (supplier y dates en null): no agrega nada", () => {
        const resultado = aplicarInterpretacionComoSugerencia({ supplier: null, dates: null }, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
    });

    it("formActual null/undefined no revienta: se trata como form vacío", () => {
        const interpretacion = { supplier: { supplierPublicId: "sup-1", name: "Delfos" }, dates: null };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: null });
        assert.equal(resultado.patch.supplierId, "sup-1");
    });
});

// ─── resolverPatchDeVentaDelCatalogo (fix C-5(b), review 2026-08-10) ──────────────────
// Camino normal: la venta real SIEMPRE pisa (comportamiento de siempre). Camino de
// selección PENDIENTE (salto de solapa): solo escribe lo que está VACÍO en el form.

describe("resolverPatchDeVentaDelCatalogo", () => {
    const sale = { supplierPublicId: "sup-1", supplierName: "Delfos", salePrice: 100, netCost: 60, currency: "USD" };

    it("camino NORMAL (esSeleccionPendiente=false): pisa TODO, aunque el form ya tuviera algo cargado", () => {
        const formConDatosViejos = { supplierId: "sup-viejo", unitSalePrice: "999", unitNetCost: "500", currency: "ARS" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formConDatosViejos, esSeleccionPendiente: false,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, {
            supplierId: "sup-1", supplierName: "Delfos",
            unitSalePrice: "100", unitNetCost: "60", currency: "USD",
        });
        assert.deepEqual(resultado.sugeridos, { supplierId: true, unitSalePrice: true, unitNetCost: true, currency: true });
    });

    it("camino de selección PENDIENTE: el form YA tiene TODO cargado a mano — no pisa nada", () => {
        const formConDatosAMano = { supplierId: "sup-manual", unitSalePrice: "999", unitNetCost: "500", currency: "ARS" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formConDatosAMano, esSeleccionPendiente: true,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, {});
        assert.deepEqual(resultado.sugeridos, {});
    });

    it("camino de selección PENDIENTE: form vacío — se completa todo igual que el camino normal", () => {
        const formVacio = { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formVacio, esSeleccionPendiente: true,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, {
            supplierId: "sup-1", supplierName: "Delfos",
            unitSalePrice: "100", unitNetCost: "60", currency: "USD",
        });
    });

    it("camino de selección PENDIENTE: mezcla — solo el precio de venta estaba vacío; la MONEDA viaja pegada al precio (fix moneda fantasma)", () => {
        // Repro exacto del bug: currency arranca "ARS" de fábrica en los 5
        // buildXFormInitial() (nunca vacía, a diferencia del precio que arranca "") —
        // antes del fix, `libre("currency")` daba false SIEMPRE en este camino y la
        // moneda real de la venta (USD) nunca se escribía.
        const formMixto = { supplierId: "sup-manual", unitSalePrice: "", unitNetCost: "500", currency: "ARS" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formMixto, esSeleccionPendiente: true,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, { unitSalePrice: "100", currency: "USD" });
        assert.deepEqual(resultado.sugeridos, { unitSalePrice: true, currency: true });
    });

    it("camino de selección PENDIENTE: el precio de venta YA estaba tipeado a mano — ni el precio NI la moneda se tocan", () => {
        const formConPrecioAMano = { supplierId: "", unitSalePrice: "80", unitNetCost: "", currency: "ARS" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formConPrecioAMano, esSeleccionPendiente: true,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.unitSalePrice, undefined);
        assert.equal(resultado.patch.currency, undefined);
        assert.equal(resultado.sugeridos.unitSalePrice, undefined);
        assert.equal(resultado.sugeridos.currency, undefined);
        // El costo SÍ estaba vacío: eso no depende de la moneda, se completa igual.
        assert.equal(resultado.patch.unitNetCost, "60");
    });

    it("sin permiso de ver costos (canSeeCost=false): el campo de costo nunca se toca, ni en el camino normal", () => {
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: false, formActual: { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" }, esSeleccionPendiente: false,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.unitNetCost, undefined);
        assert.equal(resultado.sugeridos.unitNetCost, undefined);
    });

    it("Aéreo/Traslado: nombres de campo netCost/salePrice (sin prefijo 'unit')", () => {
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: { supplierId: "", salePrice: "", netCost: "", currency: "" }, esSeleccionPendiente: false,
            campoVenta: "salePrice", campoCosto: "netCost",
        });
        assert.equal(resultado.patch.salePrice, "100");
        assert.equal(resultado.patch.netCost, "60");
    });

    it("sale sin datos (rateFallback vacío): patch queda con valores por defecto ('' / 'ARS'), sugeridos en false", () => {
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale: {}, canSeeCost: true, formActual: { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" }, esSeleccionPendiente: false,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, { supplierId: "", supplierName: null, unitSalePrice: "", unitNetCost: "", currency: "ARS" });
        assert.deepEqual(resultado.sugeridos, { supplierId: false, unitSalePrice: false, unitNetCost: false, currency: false });
    });
});

// ─── resolverNombreEnCasillero (D13: nunca la frase entera queda como nombre) ─────────

describe("resolverNombreEnCasillero", () => {
    it("con resultado elegido: usa su nombre limpio, no el texto tipeado", () => {
        const resultado = resolverNombreEnCasillero({ name: "Llao Llao" }, "llao llao del 10/02 al 15/02 con delfos");
        assert.equal(resultado, "Llao Llao");
    });

    it("resultado sin name (forma rara del backend): cae al texto actual", () => {
        assert.equal(resolverNombreEnCasillero({}, "sheraton"), "sheraton");
    });

    it("sin resultado (null/undefined): cae al texto actual", () => {
        assert.equal(resolverNombreEnCasillero(null, "sheraton"), "sheraton");
        assert.equal(resolverNombreEnCasillero(undefined, "sheraton"), "sheraton");
    });

    it("ni resultado ni texto actual: string vacío, nunca undefined", () => {
        assert.equal(resolverNombreEnCasillero(null, null), "");
    });
});

// ─── D13-bis (spec FIRMADA 2026-08-10): "crear nuevo" tampoco queda pelado ────────
// Cuando la frase completa termina en "crear nuevo" porque el producto no existía
// todavía, las fechas + el operador de la frase se aplican igual que en
// handleSelectExisting — mismas dos piezas: `aplicarInterpretacionComoSugerencia`
// (fechas, con `yaHaySupplierDeLaVenta:true` porque acá no hay `sale` y el operador NO
// va al campo genérico) + `resolverOperadorSugeridoParaProductoNuevo` (operador, que va
// al recuadro del producto nuevo).

describe("resolverOperadorSugeridoParaProductoNuevo (D13-bis)", () => {
    it("interpretación con operador matcheado por el motor: lo usa", () => {
        const interpretacion = { supplier: { supplierPublicId: "sup-delfos", name: "Delfos" }, dates: null };
        assert.equal(resolverOperadorSugeridoParaProductoNuevo(interpretacion), "sup-delfos");
    });

    it("interpretación sin operador (el motor no matcheó ninguno): no inventa nada, string vacío", () => {
        assert.equal(resolverOperadorSugeridoParaProductoNuevo({ supplier: null, dates: null }), "");
    });

    it("sin interpretación (null/undefined): string vacío, no revienta", () => {
        assert.equal(resolverOperadorSugeridoParaProductoNuevo(null), "");
        assert.equal(resolverOperadorSugeridoParaProductoNuevo(undefined), "");
    });
});

describe("D13-bis: crear-nuevo con interpretación — fechas vía aplicarInterpretacionComoSugerencia", () => {
    // Repro exacto del caso de Gastón: "llao llao del 10/02 al 15/02 con delfos" sin
    // tener el Llao Llao cargado todavía → termina en "crear nuevo".
    const interpretacionCompleta = {
        supplier: { supplierPublicId: "sup-delfos", name: "Delfos" },
        dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" },
    };

    it("form VACÍO: las fechas se aplican en amarillo (mismas guardas que al elegir un producto existente)", () => {
        const formVacio = { supplierId: "", checkIn: "", checkOut: "" };
        // yaHaySupplierDeLaVenta:true a propósito (fix "crear nuevo pelado"): en el
        // camino de crear NO hay `sale`, y el operador de la interpretación no va al
        // campo genérico `supplierId` (oculto mientras se crea un producto nuevo) — va
        // aparte, al recuadro del producto nuevo, vía resolverOperadorSugeridoParaProductoNuevo.
        const resultado = aplicarInterpretacionComoSugerencia(interpretacionCompleta, {
            yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: formVacio,
        });
        assert.deepEqual(resultado.patch, { checkIn: "2026-02-10", checkOut: "2026-02-15" });
        assert.deepEqual(resultado.sugeridos, { checkIn: true, checkOut: true });
        // El operador del recuadro nuevo sale por la otra pieza, no por acá:
        assert.equal(resolverOperadorSugeridoParaProductoNuevo(interpretacionCompleta), "sup-delfos");
    });

    it("el vendedor YA tipeó una fecha a mano ANTES de crear nuevo: esa no se pisa", () => {
        const formConFechaAMano = { supplierId: "", checkIn: "2026-05-01", checkOut: "" };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacionCompleta, {
            yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: formConFechaAMano,
        });
        assert.equal(resultado.patch.checkIn, undefined);
        assert.equal(resultado.sugeridos.checkIn, undefined);
        // checkOut SÍ estaba vacío: se completa igual
        assert.equal(resultado.patch.checkOut, "2026-02-15");
        assert.equal(resultado.sugeridos.checkOut, true);
    });

    it("sin interpretación (motor no entendió/no disparó): crear-nuevo queda igual que siempre, sin agregar nada", () => {
        const formVacio = { supplierId: "", checkIn: "", checkOut: "" };
        const resultado = aplicarInterpretacionComoSugerencia(null, {
            yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: formVacio,
        });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
        assert.equal(resolverOperadorSugeridoParaProductoNuevo(null), "");
    });

    it("Traslado (un solo campo de fecha, sin 'hasta'): se completa igual que en handleSelectExisting", () => {
        const formVacio = { supplierId: "", pickupDate: "" };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacionCompleta, {
            yaHaySupplierDeLaVenta: true, camposFecha: ["pickupDate"], formActual: formVacio,
        });
        assert.deepEqual(resultado.patch, { pickupDate: "2026-02-10" });
        assert.deepEqual(resultado.sugeridos, { pickupDate: true });
    });
});
