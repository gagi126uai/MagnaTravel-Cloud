import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  buildLearnedProductDisplayRows,
  pickDefaultServiceTypeTab,
  columnLabelsForServiceType,
  emptyTabMessage,
  resolveTabsForRender,
} from "./learnedProductVariantsLogic.js";

describe("buildLearnedProductDisplayRows", () => {
  it("un producto sin ninguna variante da un solo renglón vacío", () => {
    const rows = buildLearnedProductDisplayRows({ variants: [] });
    assert.equal(rows.length, 1);
    assert.equal(rows[0].supplierPrice, null);
    assert.equal(rows[0].showProductHeader, true);
  });

  it("agrupa por variante y marca el primer renglón de cada grupo", () => {
    const product = {
      variants: [
        {
          variantKey: "doble|desayuno",
          variantLabel: "Doble con desayuno",
          suppliers: [
            { supplierPublicId: "s1", supplierName: "Ola Mayorista" },
            { supplierPublicId: "s2", supplierName: "Julia Tours" },
          ],
        },
        {
          variantKey: "triple|desayuno",
          variantLabel: "Triple con desayuno",
          suppliers: [{ supplierPublicId: "s1", supplierName: "Ola Mayorista" }],
        },
      ],
    };
    const rows = buildLearnedProductDisplayRows(product);
    assert.equal(rows.length, 3);

    // Solo el primer renglón de TODO el producto muestra el nombre del hotel.
    assert.equal(rows[0].showProductHeader, true);
    assert.equal(rows[1].showProductHeader, false);
    assert.equal(rows[2].showProductHeader, false);

    // Cada variante repite su etiqueta solo en su primer operador.
    assert.equal(rows[0].showVariantLabel, true);
    assert.equal(rows[0].variantLabel, "Doble con desayuno");
    assert.equal(rows[1].showVariantLabel, false);
    assert.equal(rows[2].showVariantLabel, true);
    assert.equal(rows[2].variantLabel, "Triple con desayuno");
  });

  it("variante sin habitación cargada (V3=A): la etiqueta queda vacía, no 'Sin especificar'", () => {
    const product = {
      variants: [{ variantKey: "", variantLabel: "", suppliers: [{ supplierPublicId: "s1" }] }],
    };
    const rows = buildLearnedProductDisplayRows(product);
    assert.equal(rows[0].variantLabel, "");
  });
});

describe("pickDefaultServiceTypeTab", () => {
  it("elige la primera solapa CON productos", () => {
    const tabs = [
      { serviceType: "Hotel", count: 0 },
      { serviceType: "Aereo", count: 3 },
      { serviceType: "Paquete", count: 5 },
    ];
    assert.equal(pickDefaultServiceTypeTab(tabs), "Aereo");
  });

  it("si todas están en cero, cae en la primera igual (no deja la pantalla sin solapa activa)", () => {
    const tabs = [
      { serviceType: "Hotel", count: 0 },
      { serviceType: "Aereo", count: 0 },
    ];
    assert.equal(pickDefaultServiceTypeTab(tabs), "Hotel");
  });

  it("sin solapas (todavía no cargó la respuesta) devuelve vacío", () => {
    assert.equal(pickDefaultServiceTypeTab([]), "");
    assert.equal(pickDefaultServiceTypeTab(undefined), "");
  });
});

describe("columnLabelsForServiceType", () => {
  it("Hotel: columna HABITACIÓN", () => {
    assert.deepEqual(columnLabelsForServiceType("Hotel"), { productColumnLabel: "HOTEL", variantColumnLabel: "HABITACIÓN" });
  });
  it("Aereo: columna CABINA", () => {
    assert.deepEqual(columnLabelsForServiceType("Aereo"), { productColumnLabel: "RUTA", variantColumnLabel: "CABINA" });
  });
  it("Traslado: columna VEHÍCULO", () => {
    assert.deepEqual(columnLabelsForServiceType("Traslado"), { productColumnLabel: "TRAYECTO", variantColumnLabel: "VEHÍCULO" });
  });
  it("Paquete y Asistencia: sin columna del medio (V2: sin variante natural)", () => {
    assert.deepEqual(columnLabelsForServiceType("Paquete"), { productColumnLabel: "PRODUCTO", variantColumnLabel: null });
    assert.deepEqual(columnLabelsForServiceType("Asistencia"), { productColumnLabel: "PRODUCTO", variantColumnLabel: null });
  });
});

describe("emptyTabMessage", () => {
  it("cada tipo tiene su propio texto, en criollo y con el género correcto", () => {
    assert.equal(emptyTabMessage("Hotel"), "Todavía no vendiste ningún hotel.");
    assert.equal(emptyTabMessage("Aereo"), "Todavía no vendiste ningún aéreo.");
    assert.equal(emptyTabMessage("Traslado"), "Todavía no vendiste ningún traslado.");
    assert.equal(emptyTabMessage("Paquete"), "Todavía no vendiste ningún paquete.");
    assert.equal(emptyTabMessage("Asistencia"), "Todavía no vendiste ninguna asistencia.");
  });

  it("Excursión (V17, addendum firmado 2026-08-08): texto propio", () => {
    assert.equal(emptyTabMessage("Excursion"), "Todavía no vendiste ninguna excursión.");
  });

  it("tipo desconocido: cae al texto genérico, nunca revienta", () => {
    assert.equal(emptyTabMessage("Otro"), "Todavía no vendiste ningún producto de este tipo.");
    assert.equal(emptyTabMessage(undefined), "Todavía no vendiste ningún producto de este tipo.");
  });
});

describe("resolveTabsForRender", () => {
  it("con tabs del servidor: se pintan esas, tal cual", () => {
    const tabsDelServidor = [{ serviceType: "Hotel", label: "Hoteles", count: 3 }];
    assert.deepEqual(resolveTabsForRender(tabsDelServidor), tabsDelServidor);
  });

  it("sin tabs todavía (primer pedido falló): las SEIS fijas en 0, nunca la barra vacía", () => {
    const solapas = resolveTabsForRender([]);
    assert.equal(solapas.length, 6);
    assert.ok(solapas.every((tab) => tab.count === 0));
    assert.deepEqual(solapas.map((tab) => tab.serviceType), ["Hotel", "Aereo", "Paquete", "Traslado", "Asistencia", "Excursion"]);
  });

  it("null/undefined (defensivo): también cae a las fijas", () => {
    assert.equal(resolveTabsForRender(null).length, 6);
    assert.equal(resolveTabsForRender(undefined).length, 6);
  });
});
