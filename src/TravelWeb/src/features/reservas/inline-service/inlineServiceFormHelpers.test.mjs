import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    resolverCamposALimpiarAlCrearNuevo,
    aplicarInterpretacionComoSugerencia,
    resolverNombreEnCasillero,
    resolverPatchDeVentaDelCatalogo,
    resolverOperadorSugeridoParaProductoNuevo,
    resolverRateIdDeEdicion,
    resolverTocadosAManoTrasLimpiarOrigen,
} from "./inlineServiceFormHelpers.js";

// ─── resolverRateIdDeEdicion (fix #3, auditoría 2026-08-10, GRAVE) ────────────────
// El DTO real expone `ratePublicId`, no `rateId` — leer el nombre equivocado dejaba
// `form.rateId` siempre null al editar, y el candado anti-clobber del backend revertía
// en silencio los cambios de RoomType/MealPlan/Operador/Nombre/Ciudad.

describe("resolverRateIdDeEdicion", () => {
    it("usa ratePublicId (el campo real del DTO)", () => {
        assert.equal(resolverRateIdDeEdicion({ ratePublicId: "rate-123" }), "rate-123");
    });

    it("sin ratePublicId, cae a rateId (red de contención por si aparece un DTO legacy)", () => {
        assert.equal(resolverRateIdDeEdicion({ rateId: "rate-legacy" }), "rate-legacy");
    });

    it("ratePublicId gana si los dos vinieran presentes", () => {
        assert.equal(resolverRateIdDeEdicion({ ratePublicId: "rate-nuevo", rateId: "rate-viejo" }), "rate-nuevo");
    });

    it("ninguno de los dos: null (servicio sin producto vinculado, ej. cargado a mano)", () => {
        assert.equal(resolverRateIdDeEdicion({}), null);
    });

    it("serviceToEdit null/undefined (alta nueva, no edición): null, no revienta", () => {
        assert.equal(resolverRateIdDeEdicion(null), null);
        assert.equal(resolverRateIdDeEdicion(undefined), null);
    });
});

// ─── resolverCamposALimpiarAlCrearNuevo (Bug #28, Tanda 4, 2026-07-24) ────────────────
// "Crear nuevo" en el buscador solo debe limpiar los campos que el vendedor NUNCA tocó
// a mano. Desde la regresión #1+#6 (re-review 2026-08-10), la señal de "tocado" es
// `camposTocadosAMano` (true = el vendedor lo tipeó/eligió), NO `camposSugeridos` (el
// amarillo, que se apaga con cualquier tecleo del buscador y ya no sirve para esto).

describe("resolverCamposALimpiarAlCrearNuevo", () => {
    const valoresPorDefecto = { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" };

    it("form recién abierto (todo tocado a mano): preserva TODO lo que el usuario tipeó", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "5000", unitSalePrice: "7000", currency: "USD" };
        const camposTocadosAMano = { supplierId: true, unitNetCost: true, unitSalePrice: true, currency: true };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposTocadosAMano, valoresPorDefecto);

        assert.deepEqual(resultado, valoresActuales);
    });

    it("ningún campo fue tocado a mano (vino de elegir OTRO producto): se limpian todos", () => {
        const valoresActuales = { supplierId: "sup-viejo", unitNetCost: "5000", unitSalePrice: "7000", currency: "USD" };
        const camposTocadosAMano = { supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposTocadosAMano, valoresPorDefecto);

        assert.deepEqual(resultado, valoresPorDefecto);
    });

    it("mezcla: el usuario editó el costo a mano pero dejó el resto como vino de la sugerencia", () => {
        const valoresActuales = { supplierId: "sup-viejo", unitNetCost: "9999", unitSalePrice: "7000", currency: "USD" };
        // El usuario tocó unitNetCost (quedó en true); el resto nunca lo tocó (false = no tocado)
        const camposTocadosAMano = { supplierId: false, unitNetCost: true, unitSalePrice: false, currency: false };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposTocadosAMano, valoresPorDefecto);

        // El campo tocado a mano se preserva tal cual
        assert.equal(resultado.unitNetCost, "9999");
        // Los que NO fueron tocados se limpian a su valor por defecto
        assert.equal(resultado.supplierId, "");
        assert.equal(resultado.unitSalePrice, "");
        assert.equal(resultado.currency, "ARS");
    });

    it("camposTocadosAMano sin registro para un campo (undefined): se trata como NO tocado, se limpia (opción segura)", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "1000", unitSalePrice: "2000", currency: "USD" };
        const camposTocadosAMano = {}; // ningún campo tiene registro

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposTocadosAMano, valoresPorDefecto);

        assert.deepEqual(resultado, valoresPorDefecto);
    });

    it("camposTocadosAMano null/undefined no rompe: se limpian todos los campos (opción segura)", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "1000", unitSalePrice: "2000", currency: "USD" };

        assert.deepEqual(resolverCamposALimpiarAlCrearNuevo(valoresActuales, null, valoresPorDefecto), valoresPorDefecto);
        assert.deepEqual(resolverCamposALimpiarAlCrearNuevo(valoresActuales, undefined, valoresPorDefecto), valoresPorDefecto);
    });

    it("respeta las claves de netCost/salePrice sin el prefijo 'unit' (Aéreo/Traslado usan otros nombres)", () => {
        const valoresPorDefectoVuelo = { supplierId: "", netCost: "", salePrice: "", currency: "ARS" };
        const valoresActuales = { supplierId: "sup-1", netCost: "1500", salePrice: "2500", currency: "USD" };
        const camposTocadosAMano = { supplierId: true, netCost: true, salePrice: true, currency: true };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposTocadosAMano, valoresPorDefectoVuelo);

        assert.deepEqual(resultado, valoresActuales);
    });

    it("el resultado SOLO trae las claves de valoresPorDefecto (no arrastra campos ajenos del form)", () => {
        const valoresActuales = { supplierId: "sup-1", unitNetCost: "1000", unitSalePrice: "2000", currency: "USD", hotelName: "Hotel X" };
        const camposTocadosAMano = { supplierId: true, unitNetCost: true, unitSalePrice: true, currency: true };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresActuales, camposTocadosAMano, valoresPorDefecto);

        assert.deepEqual(Object.keys(resultado).sort(), Object.keys(valoresPorDefecto).sort());
    });

    it("fix #11 (auditoría 2026-08-10): limpieza del form de ORIGEN al saltar de solapa — mismo criterio, incluye fechas", () => {
        // ServiceInlineCard.limpiarBusquedaDelFormOrigen reusa esta MISMA función con el
        // set completo de campos de Hotel (incluidas las fechas D13): un campo que NUNCA
        // fue tocado a mano (vino de la selección que se deshace) se limpia; uno tipeado
        // a mano por el vendedor en esa solapa se queda intacto.
        const valoresPorDefectoHotel = { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS", checkIn: "", checkOut: "" };
        const valoresDelOrigen = {
            supplierId: "sup-viejo", unitNetCost: "500", unitSalePrice: "999", currency: "USD",
            checkIn: "2026-03-01", checkOut: "2026-03-10",
        };
        // El vendedor tipeó a mano el operador y checkIn; el resto nunca lo tocó (vino
        // de la selección que se está deshaciendo con el salto de solapa).
        const camposTocadosDelOrigen = { supplierId: true, unitNetCost: false, unitSalePrice: false, currency: false, checkIn: true, checkOut: false };

        const resultado = resolverCamposALimpiarAlCrearNuevo(valoresDelOrigen, camposTocadosDelOrigen, valoresPorDefectoHotel);

        // Tipeado a mano: se queda igual.
        assert.equal(resultado.supplierId, "sup-viejo");
        assert.equal(resultado.checkIn, "2026-03-01");
        // Nunca tocado (vino de la selección que se deshace): se limpia a su default.
        assert.equal(resultado.unitNetCost, "");
        assert.equal(resultado.unitSalePrice, "");
        assert.equal(resultado.currency, "ARS");
        assert.equal(resultado.checkOut, "");
    });
});

// ─── aplicarInterpretacionComoSugerencia (D13, spec FIRMADA 2026-08-10) ───────────────
// El "hack" de la frase completa: lo que el motor entendió (operador, fechas) se agrega
// como sugerencia amarilla. Desde la regresión #1+#6, "libre" ya no es solo "vacío": es
// "vacío O no tocado a mano" (`camposTocadosAMano`) — así una NUEVA selección/frase puede
// reemplazar lo que vino de una selección ANTERIOR, sin tocar nunca lo tipeado a mano.

describe("aplicarInterpretacionComoSugerencia", () => {
    const FORM_VACIO = { supplierId: "", checkIn: "", checkOut: "" };
    const NADA_TOCADO = { supplierId: false, checkIn: false, checkOut: false };

    it("sin interpretación (motor no entendió/no disparó): no agrega nada", () => {
        const resultado = aplicarInterpretacionComoSugerencia(null, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO, camposTocadosAMano: NADA_TOCADO });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
    });

    it("con operador y fechas, form vacío (sin venta que ya trajera operador): agrega los tres", () => {
        const interpretacion = {
            supplier: { supplierPublicId: "sup-1", name: "Delfos" },
            dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" },
        };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO, camposTocadosAMano: NADA_TOCADO });
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
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO, camposTocadosAMano: NADA_TOCADO });
        assert.equal(resultado.patch.supplierId, undefined);
        assert.equal(resultado.sugeridos.supplierId, undefined);
    });

    it("fix C-5(a): el vendedor YA tiene un operador tocado a mano en el form — la interpretación NO lo pisa", () => {
        const interpretacion = { supplier: { supplierPublicId: "sup-1", name: "Delfos" }, dates: null };
        const formConOperadorAMano = { supplierId: "sup-manual", checkIn: "", checkOut: "" };
        const tocados = { supplierId: true, checkIn: false, checkOut: false };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: formConOperadorAMano, camposTocadosAMano: tocados });
        assert.equal(resultado.patch.supplierId, undefined);
        assert.equal(resultado.sugeridos.supplierId, undefined);
    });

    it("fix regresión #1+#6: el operador tiene VALOR pero NO fue tocado a mano (vino de otra selección) — SÍ se reemplaza", () => {
        // Repro exacto del caso de la re-review: eligió Hotel A (operador queda con
        // valor, pero `camposTocadosAMano.supplierId` sigue en false porque el vendedor
        // nunca tocó el select a mano), después la frase de Hotel B trae otro operador.
        const interpretacion = { supplier: { supplierPublicId: "sup-B", name: "Ola" }, dates: null };
        const formConOperadorDeOtraSeleccion = { supplierId: "sup-A", checkIn: "", checkOut: "" };
        const tocados = { supplierId: false, checkIn: false, checkOut: false }; // nunca tocado a mano
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: formConOperadorDeOtraSeleccion, camposTocadosAMano: tocados });
        assert.equal(resultado.patch.supplierId, "sup-B");
        assert.equal(resultado.sugeridos.supplierId, true);
    });

    it("fix C-5(a): el vendedor YA tiene checkIn tocado a mano — la interpretación NO lo pisa, pero checkOut (vacío) sí se completa", () => {
        const interpretacion = { supplier: null, dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" } };
        const formConFechaAMano = { supplierId: "", checkIn: "2026-03-01", checkOut: "" };
        const tocados = { supplierId: false, checkIn: true, checkOut: false };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: formConFechaAMano, camposTocadosAMano: tocados });
        assert.equal(resultado.patch.checkIn, undefined);
        assert.equal(resultado.sugeridos.checkIn, undefined);
        // checkOut SÍ estaba vacío: se completa igual
        assert.equal(resultado.patch.checkOut, "2026-02-15");
        assert.equal(resultado.sugeridos.checkOut, true);
    });

    it("fix C-5(a): las DOS fechas tocadas a mano — la interpretación no toca ninguna", () => {
        const interpretacion = { supplier: null, dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" } };
        const formConAmbasFechas = { supplierId: "", checkIn: "2026-03-01", checkOut: "2026-03-05" };
        const tocados = { supplierId: false, checkIn: true, checkOut: true };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: formConAmbasFechas, camposTocadosAMano: tocados });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
    });

    it("Traslado: un solo campo de fecha (sin 'hasta') — 'to' de la interpretación se ignora", () => {
        const interpretacion = { supplier: null, dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" } };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["pickupDate"], formActual: { supplierId: "", pickupDate: "" }, camposTocadosAMano: { supplierId: false, pickupDate: false } });
        assert.deepEqual(resultado.patch, { pickupDate: "2026-02-10" });
        assert.deepEqual(resultado.sugeridos, { pickupDate: true });
    });

    it("solo 'to' sin 'from': se aplica el campo que corresponde nomás", () => {
        const interpretacion = { supplier: null, dates: { from: null, to: "2026-02-15T00:00:00Z" } };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO, camposTocadosAMano: NADA_TOCADO });
        assert.deepEqual(resultado.patch, { checkOut: "2026-02-15" });
        assert.deepEqual(resultado.sugeridos, { checkOut: true });
    });

    it("interpretación sin nada utilizable (supplier y dates en null): no agrega nada", () => {
        const resultado = aplicarInterpretacionComoSugerencia({ supplier: null, dates: null }, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: FORM_VACIO, camposTocadosAMano: NADA_TOCADO });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
    });

    it("formActual/camposTocadosAMano null/undefined no revientan: se tratan como vacíos/no tocados", () => {
        const interpretacion = { supplier: { supplierPublicId: "sup-1", name: "Delfos" }, dates: null };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacion, { yaHaySupplierDeLaVenta: false, camposFecha: ["checkIn", "checkOut"], formActual: null, camposTocadosAMano: null });
        assert.equal(resultado.patch.supplierId, "sup-1");
    });
});

// ─── resolverPatchDeVentaDelCatalogo (auditoría 2026-08-10: regla transversal #1/#2/#7) ─
// Fix REGRESIÓN #1+#6 (re-review, mismo día): la primera versión usaba `camposSugeridos`
// (el amarillo) para decidir "reemplazable" — pero `camposSugeridos` se apaga con
// cualquier tecleo del buscador (#6) y dejó de ser señal confiable de "esto es del
// vendedor". Ahora usa `camposTocadosAMano`, un estado APARTE que solo se prende en el
// onChange real de cada campo.

describe("resolverPatchDeVentaDelCatalogo", () => {
    const sale = { supplierPublicId: "sup-1", supplierName: "Delfos", salePrice: 100, netCost: 60, currency: "USD" };

    it("form vacío (nada tipeado, nada tocado): se completa todo", () => {
        const formVacio = { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formVacio, camposTocadosAMano: {},
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, {
            supplierId: "sup-1", supplierName: "Delfos",
            unitSalePrice: "100", unitNetCost: "60", currency: "USD",
        });
        assert.deepEqual(resultado.sugeridos, { supplierId: true, unitSalePrice: true, unitNetCost: true, currency: true });
    });

    it("fix #1 (bug reportado por Gastón): el form tiene datos con VALOR pero NO tocados a mano (de una selección anterior) — SÍ se reemplazan", () => {
        const formConSeleccionVieja = { supplierId: "sup-viejo", unitSalePrice: "999", unitNetCost: "500", currency: "ARS" };
        const nadaTocado = { supplierId: false, unitSalePrice: false, unitNetCost: false, currency: false };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formConSeleccionVieja, camposTocadosAMano: nadaTocado,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, {
            supplierId: "sup-1", supplierName: "Delfos",
            unitSalePrice: "100", unitNetCost: "60", currency: "USD",
        });
    });

    it("fix #1 (bug reportado por Gastón): el form tiene datos TOCADOS A MANO — NUNCA se pisan", () => {
        const formConDatosAMano = { supplierId: "sup-manual", unitSalePrice: "999", unitNetCost: "500", currency: "ARS" };
        const todoTocado = { supplierId: true, unitSalePrice: true, unitNetCost: true, currency: true };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formConDatosAMano, camposTocadosAMano: todoTocado,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, {});
        assert.deepEqual(resultado.sugeridos, {});
    });

    it("fix #2 (bug reportado por Gastón): la venta NO trae operador (producto sin ventas registradas) — NUNCA se escribe/vacía el operador", () => {
        const saleSinOperador = { supplierPublicId: null, supplierName: null, salePrice: 100, netCost: 60, currency: "USD" };
        const formConOperadorAMano = { supplierId: "sup-manual", unitSalePrice: "", unitNetCost: "", currency: "" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale: saleSinOperador, canSeeCost: true, formActual: formConOperadorAMano, camposTocadosAMano: { supplierId: true },
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.supplierId, undefined);
        assert.equal(resultado.patch.supplierName, undefined);
        assert.equal(resultado.sugeridos.supplierId, undefined);
        // El resto (precio/costo/moneda) sí se completa: el bug era específico del operador.
        assert.equal(resultado.patch.unitSalePrice, "100");
    });

    it("fix #2: incluso con el campo de operador VACÍO, la venta sin operador no escribe nada ahí (no fuerza '' explícito)", () => {
        const saleSinOperador = { supplierPublicId: null, salePrice: 100, netCost: 60, currency: "USD" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale: saleSinOperador, canSeeCost: true, formActual: { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" }, camposTocadosAMano: {},
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.supplierId, undefined);
    });

    it("fix #7: la venta con precio 0/vacío (rateFallback sin precio curado) NUNCA escribe '0' ni toca la moneda", () => {
        const saleSinPrecio = { supplierPublicId: "sup-1", supplierName: "Delfos", salePrice: 0, netCost: 0, currency: "USD" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale: saleSinPrecio, canSeeCost: true, formActual: { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "ARS" }, camposTocadosAMano: {},
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.unitSalePrice, undefined);
        assert.equal(resultado.patch.currency, undefined);
        assert.equal(resultado.patch.unitNetCost, undefined);
        // El operador sí se completa: el bug era específico del precio/moneda en 0.
        assert.equal(resultado.patch.supplierId, "sup-1");
    });

    it("fix #7: si sale.currency falta, TAMPOCO se escribe el precio (borde de la re-review: sin moneda no hay plata)", () => {
        const saleSinMoneda = { supplierPublicId: "sup-1", salePrice: 100, netCost: 60, currency: null };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale: saleSinMoneda, canSeeCost: true, formActual: { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" }, camposTocadosAMano: {},
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.unitSalePrice, undefined);
        assert.equal(resultado.patch.currency, undefined);
        assert.equal(resultado.sugeridos.unitSalePrice, undefined);
        assert.equal(resultado.sugeridos.currency, undefined);
        // El operador (independiente de precio/moneda) sí se completa.
        assert.equal(resultado.patch.supplierId, "sup-1");
    });

    it("mezcla: solo el precio de venta NO estaba tocado a mano; el resto tocado queda intacto — moneda viaja con el precio", () => {
        const formMixto = { supplierId: "sup-manual", unitSalePrice: "80", unitNetCost: "500", currency: "ARS" };
        const soloVentaNoTocada = { supplierId: true, unitSalePrice: false, unitNetCost: true, currency: true };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: formMixto, camposTocadosAMano: soloVentaNoTocada,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, { unitSalePrice: "100", currency: "USD" });
        assert.deepEqual(resultado.sugeridos, { unitSalePrice: true, currency: true });
    });

    it("sin permiso de ver costos (canSeeCost=false): el campo de costo nunca se toca", () => {
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: false, formActual: { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" }, camposTocadosAMano: {},
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.unitNetCost, undefined);
        assert.equal(resultado.sugeridos.unitNetCost, undefined);
    });

    it("Aéreo/Traslado: nombres de campo netCost/salePrice (sin prefijo 'unit')", () => {
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: { supplierId: "", salePrice: "", netCost: "", currency: "" }, camposTocadosAMano: {},
            campoVenta: "salePrice", campoCosto: "netCost",
        });
        assert.equal(resultado.patch.salePrice, "100");
        assert.equal(resultado.patch.netCost, "60");
    });

    it("sale sin datos (rateFallback totalmente vacío): no escribe nada (ni operador, ni precio, ni moneda)", () => {
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale: {}, canSeeCost: true, formActual: { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" }, camposTocadosAMano: {},
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.deepEqual(resultado.patch, {});
        assert.deepEqual(resultado.sugeridos, {});
    });

    it("camposTocadosAMano/formActual null/undefined no revientan: se tratan como vacíos/no tocados", () => {
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: null, camposTocadosAMano: null,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        assert.equal(resultado.patch.supplierId, "sup-1");
    });
});

// ─── Tests de SECUENCIA (regresión #1+#6, re-review 2026-08-10) ───────────────────────
// No son réplicas de los de arriba: encadenan varias llamadas, tal cual las encadenaría
// un form real (tipear → elegir → tipear → elegir), para probar el flujo completo tal
// como lo vive el vendedor — no solo la función en aislamiento.

describe("Secuencia: elegir A → escribir buscando B → elegir B reemplaza la plata de A", () => {
    it("reproduce el bug de la re-review y confirma el fix", () => {
        const saleA = { supplierPublicId: "sup-A", supplierName: "Ola Mayorista", salePrice: 100, netCost: 60, currency: "USD" };
        const saleB = { supplierPublicId: "sup-B", supplierName: "Delfos", salePrice: 250, netCost: 150, currency: "ARS" };

        // Paso 1: form vacío, nada tocado a mano — arranca la ficha.
        let form = { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "" };
        let camposTocadosAMano = {};

        // Paso 2: el vendedor ELIGE Hotel A. `resolverPatchDeVentaDelCatalogo` completa
        // todo (nada tocado, nada bloquea). En el form real, `camposTocadosAMano` NO se
        // toca acá (elegir un producto no es "tocar a mano" ningún campo puntual).
        const resultadoA = resolverPatchDeVentaDelCatalogo({
            sale: saleA, canSeeCost: true, formActual: form, camposTocadosAMano,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        form = { ...form, ...resultadoA.patch };
        assert.equal(form.supplierId, "sup-A");
        assert.equal(form.unitSalePrice, "100");
        assert.equal(form.currency, "USD");

        // Paso 3: el vendedor escribe en el buscador buscando OTRO hotel (Hotel B). Esto
        // NO es tocar a mano el operador/precio/moneda — `camposTocadosAMano` sigue
        // exactamente igual que antes (vacío): el bug #1+#6 pasaba por acá, cuando el
        // código viejo usaba `camposSugeridos` (que #6 SÍ apaga al tipear en el buscador).
        // `camposTocadosAMano` no se toca — nada cambia acá.

        // Paso 4: el vendedor ELIGE Hotel B. Como ningún campo fue tocado a mano, TODO
        // se reemplaza con la plata de B — el bug real hacía que esto NO pasara.
        const resultadoB = resolverPatchDeVentaDelCatalogo({
            sale: saleB, canSeeCost: true, formActual: form, camposTocadosAMano,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        form = { ...form, ...resultadoB.patch };

        assert.equal(form.supplierId, "sup-B", "el operador tiene que ser el de B, no el de A");
        assert.equal(form.unitSalePrice, "250", "el precio tiene que ser el de B, no el de A");
        assert.equal(form.unitNetCost, "150");
        assert.equal(form.currency, "ARS", "la moneda tiene que ser la de B, no la de A (USD)");
        assert.deepEqual(resultadoB.sugeridos, { supplierId: true, unitSalePrice: true, unitNetCost: true, currency: true });
    });
});

describe("Secuencia: tocar el precio a mano ANTES de elegir un producto — la selección no lo pisa", () => {
    it("el precio tocado a mano sobrevive a elegir un producto nuevo", () => {
        const sale = { supplierPublicId: "sup-1", supplierName: "Delfos", salePrice: 100, netCost: 60, currency: "USD" };

        // El vendedor tipeó el precio de venta A MANO en el casillero (onChange real del
        // campo, no del buscador) — eso prende camposTocadosAMano.unitSalePrice.
        const form = { supplierId: "", unitSalePrice: "48000", unitNetCost: "", currency: "" };
        const camposTocadosAMano = { supplierId: false, unitSalePrice: true, unitNetCost: false, currency: false };

        // Ahora elige un producto del buscador — el precio tocado a mano NO se pisa,
        // pero el operador (nunca tocado) sí se completa con el de la venta real.
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: form, camposTocadosAMano,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });

        assert.equal(resultado.patch.unitSalePrice, undefined, "el precio tocado a mano no se pisa");
        assert.equal(resultado.patch.currency, undefined, "la moneda viaja con el precio: si el precio no se toca, la moneda tampoco");
        assert.equal(resultado.patch.supplierId, "sup-1", "el operador SÍ se completa: nunca fue tocado a mano");
    });
});

// ─── Fix residual ítem A (re-review 2026-08-10) ───────────────────────────────────
// `handleSelectExisting`/`handleCreateNew` sembraban `precioTocadoPorElUsuario` en
// `false` FIJO, sin mirar `camposTocadosAMano` — así que un costo corregido a mano
// (que SÍ sobrevivía en `form` gracias a `resolverPatchDeVentaDelCatalogo`) igual
// quedaba "libre" para la sugerencia por habitación/cabina/vehículo, que lo pisaba
// 300ms después. La corrección real vive en los 3 forms con variante (no en un helper
// puro — es un simple `=== true` sobre el mapa persistente), pero acá dejamos
// documentado con un test el criterio que debe seguir esa siembra.
describe("Fix residual ítem A: sembrar precioTocadoPorElUsuario desde camposTocadosAMano (no en false fijo)", () => {
    it("si el campo de la variante ya estaba tocado a mano, el flag sembrado tiene que ser true", () => {
        const camposTocadosAMano = { supplierId: false, unitNetCost: true, unitSalePrice: false, currency: false };
        const campoPrecioVariante = "unitNetCost"; // ej: HotelInlineForm con permiso de costos
        const flagSembrado = camposTocadosAMano[campoPrecioVariante] === true;
        assert.equal(flagSembrado, true, "el costo tocado a mano tiene que seguir protegido tras elegir el producto");
    });

    it("si el campo de la variante NUNCA fue tocado, el flag sembrado tiene que ser false (el sistema puede acomodarlo)", () => {
        const camposTocadosAMano = { supplierId: false, unitNetCost: false, unitSalePrice: false, currency: false };
        const campoPrecioVariante = "unitNetCost";
        const flagSembrado = camposTocadosAMano[campoPrecioVariante] === true;
        assert.equal(flagSembrado, false);
    });

    it("la moneda se siembra por su propia clave `currency`, no la del campo de precio", () => {
        const camposTocadosAMano = { supplierId: false, unitNetCost: false, unitSalePrice: false, currency: true };
        const flagMonedaSembrado = camposTocadosAMano.currency === true;
        assert.equal(flagMonedaSembrado, true, "la moneda tocada a mano tiene que seguir protegida aunque el precio no lo esté");
    });
});

describe("Secuencia: tocar el costo a mano → elegir otro producto → la sugerencia por variante NO lo pisa", () => {
    it("encadena resolverPatchDeVentaDelCatalogo + la siembra correcta de precioTocadoPorElUsuario", () => {
        // Paso 1: el vendedor tipeó el costo a mano en el casillero.
        let form = { supplierId: "", unitNetCost: "55000", unitSalePrice: "", currency: "" };
        const camposTocadosAMano = { supplierId: false, unitNetCost: true, unitSalePrice: false, currency: false };

        // Paso 2: elige un producto del buscador — el costo tocado a mano sobrevive
        // (mismo mecanismo que la Secuencia de arriba).
        const sale = { supplierPublicId: "sup-9", supplierName: "Ola Mayorista", salePrice: 900, netCost: 700, currency: "USD" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: true, formActual: form, camposTocadosAMano,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });
        form = { ...form, ...resultado.patch };
        assert.equal(form.unitNetCost, "55000", "el costo tocado a mano no se pisó al elegir el producto");

        // Paso 3: el fix del ítem A siembra precioTocadoPorElUsuario en TRUE (porque
        // camposTocadosAMano.unitNetCost seguía en true) — así que si la sugerencia por
        // habitación llega 300ms después, resolverCamposAlCambiarVariante (otro archivo)
        // la va a bloquear. Documentamos acá el valor que debe sembrarse.
        const campoPrecioVariante = "unitNetCost";
        const precioTocadoSembrado = camposTocadosAMano[campoPrecioVariante] === true;
        assert.equal(precioTocadoSembrado, true, "sin este fix, se sembraba false fijo y la variante pisaba el costo 300ms después");
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
            yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: formVacio, camposTocadosAMano: { checkIn: false, checkOut: false },
        });
        assert.deepEqual(resultado.patch, { checkIn: "2026-02-10", checkOut: "2026-02-15" });
        assert.deepEqual(resultado.sugeridos, { checkIn: true, checkOut: true });
        // El operador del recuadro nuevo sale por la otra pieza, no por acá:
        assert.equal(resolverOperadorSugeridoParaProductoNuevo(interpretacionCompleta), "sup-delfos");
    });

    it("el vendedor YA tocó una fecha a mano ANTES de crear nuevo: esa no se pisa", () => {
        const formConFechaAMano = { supplierId: "", checkIn: "2026-05-01", checkOut: "" };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacionCompleta, {
            yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: formConFechaAMano, camposTocadosAMano: { checkIn: true, checkOut: false },
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
            yaHaySupplierDeLaVenta: true, camposFecha: ["checkIn", "checkOut"], formActual: formVacio, camposTocadosAMano: {},
        });
        assert.deepEqual(resultado, { patch: {}, sugeridos: {} });
        assert.equal(resolverOperadorSugeridoParaProductoNuevo(null), "");
    });

    it("Traslado (un solo campo de fecha, sin 'hasta'): se completa igual que en handleSelectExisting", () => {
        const formVacio = { supplierId: "", pickupDate: "" };
        const resultado = aplicarInterpretacionComoSugerencia(interpretacionCompleta, {
            yaHaySupplierDeLaVenta: true, camposFecha: ["pickupDate"], formActual: formVacio, camposTocadosAMano: { pickupDate: false },
        });
        assert.deepEqual(resultado.patch, { pickupDate: "2026-02-10" });
        assert.deepEqual(resultado.sugeridos, { pickupDate: true });
    });
});

// ─── Fix residual ítem B (re-review 2026-08-10) ───────────────────────────────────
// `limpiarBusquedaDelFormOrigen` (ServiceInlineCard.jsx) apagaba TODO `camposTocadosAMano`
// del origen al saltar de solapa (D3), aunque el valor tipeado a mano sobreviviera en el
// form vía `resolverCamposALimpiarAlCrearNuevo` — al volver y elegir un producto, ese
// valor quedaba "libre" y se pisaba en silencio.

describe("resolverTocadosAManoTrasLimpiarOrigen", () => {
    it("un campo tocado a mano mantiene su bandera en true (protegido) aunque cambie el contexto", () => {
        const camposTocadosAMano = { supplierId: false, unitSalePrice: true, unitNetCost: false, currency: false };
        const camposLimpios = { supplierId: "", unitSalePrice: "48000", unitNetCost: "", currency: "ARS" };
        const resultado = resolverTocadosAManoTrasLimpiarOrigen(camposTocadosAMano, camposLimpios);
        assert.equal(resultado.unitSalePrice, true, "el precio tipeado a mano sigue protegido tras el salto de solapa");
    });

    it("un campo NUNCA tocado a mano (volvió al default) queda en false", () => {
        const camposTocadosAMano = { supplierId: false, unitSalePrice: false, unitNetCost: false, currency: false };
        const camposLimpios = { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "ARS" };
        const resultado = resolverTocadosAManoTrasLimpiarOrigen(camposTocadosAMano, camposLimpios);
        assert.equal(resultado.unitSalePrice, false);
    });

    it("mezcla: algunos campos tocados a mano, otros no — cada uno se resuelve por separado", () => {
        const camposTocadosAMano = { supplierId: true, unitSalePrice: false, unitNetCost: true, currency: false };
        const camposLimpios = { supplierId: "sup-1", unitSalePrice: "", unitNetCost: "55000", currency: "ARS" };
        const resultado = resolverTocadosAManoTrasLimpiarOrigen(camposTocadosAMano, camposLimpios);
        assert.deepEqual(resultado, { supplierId: true, unitSalePrice: false, unitNetCost: true, currency: false });
    });

    it("camposTocadosAMano vacío/undefined: todo queda en false, no revienta", () => {
        const camposLimpios = { supplierId: "", unitSalePrice: "", unitNetCost: "", currency: "ARS" };
        assert.deepEqual(resolverTocadosAManoTrasLimpiarOrigen({}, camposLimpios), { supplierId: false, unitSalePrice: false, unitNetCost: false, currency: false });
        assert.deepEqual(resolverTocadosAManoTrasLimpiarOrigen(undefined, camposLimpios), { supplierId: false, unitSalePrice: false, unitNetCost: false, currency: false });
    });
});

describe("Secuencia: tocar el precio a mano → saltar de solapa y volver → el precio sigue protegido al elegir un producto", () => {
    it("encadena resolverCamposALimpiarAlCrearNuevo + resolverTocadosAManoTrasLimpiarOrigen + resolverPatchDeVentaDelCatalogo", () => {
        const valoresPorDefecto = { supplierId: "", unitNetCost: "", unitSalePrice: "", currency: "ARS" };

        // Paso 1: el vendedor tipeó el precio de venta a mano en la solapa Hotel, ANTES
        // de elegir ningún hotel del buscador (podría pasar, ej. calcula el precio de
        // memoria antes de buscar el producto).
        let formHotel = { supplierId: "", unitNetCost: "", unitSalePrice: "48000", currency: "ARS" };
        let camposTocadosAManoHotel = { supplierId: false, unitNetCost: false, unitSalePrice: true, currency: false };

        // Paso 2: el vendedor elige, en OTRA solapa (Vuelo), un resultado de tipo Hotel
        // desde el buscador cross-tipo (D3) — la ficha salta de solapa y limpia el
        // origen (acá el origen es Vuelo, pero simulamos el mismo mecanismo sobre Hotel
        // para no repetir 2 sets de datos: lo que importa es la secuencia del ORIGEN).
        // `resolverCamposALimpiarAlCrearNuevo` preserva el valor tocado a mano:
        const camposLimpios = resolverCamposALimpiarAlCrearNuevo(
            { supplierId: formHotel.supplierId, unitNetCost: formHotel.unitNetCost, unitSalePrice: formHotel.unitSalePrice, currency: formHotel.currency },
            camposTocadosAManoHotel,
            valoresPorDefecto
        );
        assert.equal(camposLimpios.unitSalePrice, "48000", "el valor tipeado a mano sobrevive al salto de solapa");

        // El form del origen se actualiza con camposLimpios (simulando el setFormHotel real):
        formHotel = { ...formHotel, ...camposLimpios };

        // Y la bandera de "tocado a mano" se resuelve con el fix del ítem B — NO se apaga
        // entera, solo lo que efectivamente volvió al default:
        camposTocadosAManoHotel = resolverTocadosAManoTrasLimpiarOrigen(camposTocadosAManoHotel, camposLimpios);
        assert.equal(camposTocadosAManoHotel.unitSalePrice, true, "sin este fix, acá quedaba en false y el precio quedaba desprotegido");

        // Paso 3: el vendedor vuelve a la solapa Hotel más tarde y elige un producto del
        // buscador — el precio tipeado a mano en el Paso 1 NO se tiene que pisar.
        const sale = { supplierPublicId: "sup-7", supplierName: "Aviatur", salePrice: 500, netCost: 300, currency: "USD" };
        const resultado = resolverPatchDeVentaDelCatalogo({
            sale, canSeeCost: false, formActual: formHotel, camposTocadosAMano: camposTocadosAManoHotel,
            campoVenta: "unitSalePrice", campoCosto: "unitNetCost",
        });

        assert.equal(resultado.patch.unitSalePrice, undefined, "el precio tipeado a mano sigue protegido tras el salto de solapa");
        assert.equal(resultado.patch.currency, undefined, "la moneda tampoco se toca: viaja pegada al precio protegido");
        assert.equal(resultado.patch.supplierId, "sup-7", "el operador (nunca tocado) sí se completa con el de la venta real");
    });
});
