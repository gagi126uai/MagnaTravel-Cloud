import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { resolverCamposALimpiarAlCrearNuevo } from "./inlineServiceFormHelpers.js";

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
