import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  buildCreateSimpleProductPayload,
  validateProductNameAndCity,
  buildSupplierPriceLineText,
  buildRenameLearnedProductPayload,
  resolveSimilarProductDialogDecision,
  SIMILAR_PRODUCT_DIALOG_DECISION,
  buildRenameVariantPayload,
  buildInitialVariantCorrectionFields,
} from "./ratesLearnedProductsLogic.js";

describe("buildInitialVariantCorrectionFields", () => {
  it("precarga con la variante ACTUAL (P-21): una Triple con desayuno no arranca en Doble", () => {
    const iniciales = buildInitialVariantCorrectionFields({
      roomType: "Triple", mealPlan: "Media Pension", roomCategory: "Vista al mar", cabinClass: null, vehicleType: null,
    });
    assert.deepEqual(iniciales, {
      roomType: "Triple", mealPlan: "Media Pension", roomCategory: "Vista al mar", cabinClass: "", vehicleType: "",
    });
  });

  it("Aéreo: precarga la cabina real, no un default de hotel", () => {
    const iniciales = buildInitialVariantCorrectionFields({ cabinClass: "Business" });
    assert.equal(iniciales.cabinClass, "Business");
    assert.equal(iniciales.roomType, "Doble"); // no aplica a Aéreo, pero no debe romper
  });

  it("Traslado: precarga el vehículo real", () => {
    const iniciales = buildInitialVariantCorrectionFields({ vehicleType: "Van" });
    assert.equal(iniciales.vehicleType, "Van");
  });

  it("variante vieja sin desglose (piezas null): cae al mismo default que el resto de la app", () => {
    const iniciales = buildInitialVariantCorrectionFields(null);
    assert.deepEqual(iniciales, {
      roomType: "Doble", mealPlan: "Desayuno", roomCategory: "", cabinClass: "", vehicleType: "",
    });
  });
});

describe("buildRenameVariantPayload", () => {
  it("Hotel: manda roomType/mealPlan/roomCategory, el resto en null", () => {
    const payload = buildRenameVariantPayload({
      serviceType: "Hotel", productPublicId: "p1", currentVariantKey: "doble|desayuno",
      roomType: "Triple", mealPlan: "Media Pension", roomCategory: "  Superior  ",
    });
    assert.deepEqual(payload, {
      productPublicId: "p1", currentVariantKey: "doble|desayuno",
      roomType: "Triple", mealPlan: "Media Pension", roomCategory: "Superior",
      cabinClass: null, vehicleType: null,
    });
  });

  it("Aereo: manda solo cabinClass", () => {
    const payload = buildRenameVariantPayload({
      serviceType: "Aereo", productPublicId: "p2", currentVariantKey: "economy", cabinClass: "Business",
    });
    assert.equal(payload.cabinClass, "Business");
    assert.equal(payload.roomType, null);
    assert.equal(payload.vehicleType, null);
  });

  it("Traslado: manda solo vehicleType, recortado", () => {
    const payload = buildRenameVariantPayload({
      serviceType: "Traslado", productPublicId: "p3", currentVariantKey: "van", vehicleType: "  Sedán  ",
    });
    assert.equal(payload.vehicleType, "Sedán");
    assert.equal(payload.cabinClass, null);
  });

  it("Paquete/Asistencia: sin variante natural, todo queda en null", () => {
    const payload = buildRenameVariantPayload({ serviceType: "Paquete", productPublicId: "p4", currentVariantKey: "" });
    assert.equal(payload.roomType, null);
    assert.equal(payload.cabinClass, null);
    assert.equal(payload.vehicleType, null);
  });
});

describe("buildCreateSimpleProductPayload", () => {
  it("hotel: manda ciudad recortada, unidad 'noche' y la variante (roomType/mealPlan/roomCategory)", () => {
    const payload = buildCreateSimpleProductPayload({
      serviceType: "Hotel",
      name: "  Maitei Posadas  ",
      city: "  Posadas  ",
      supplierId: "sup-1",
      price: "48000",
      currency: "ARS",
      roomType: "Triple",
      mealPlan: "Media Pension",
      roomCategory: "  Superior  ",
    });
    assert.deepEqual(payload, {
      serviceType: "Hotel",
      name: "Maitei Posadas",
      city: "Posadas",
      supplierId: "sup-1",
      price: 48000,
      currency: "ARS",
      priceUnit: "noche",
      createAnyway: false,
      roomType: "Triple",
      mealPlan: "Media Pension",
      roomCategory: "Superior",
      cabinClass: null,
      vehicleType: null,
    });
  });

  it("aéreo: manda cabinClass, no manda ciudad/unidad/variante de hotel", () => {
    const payload = buildCreateSimpleProductPayload({
      serviceType: "Aereo",
      name: "Buenos Aires - Miami",
      city: "esto no debería viajar",
      price: 780,
      currency: "USD",
      cabinClass: "Business",
      roomType: "esto tampoco debería viajar",
    });
    assert.equal(payload.city, null);
    assert.equal(payload.priceUnit, null);
    assert.equal(payload.cabinClass, "Business");
    assert.equal(payload.roomType, null);
    assert.equal(payload.vehicleType, null);
  });

  it("traslado: manda vehicleType recortado (texto libre con memoria)", () => {
    const payload = buildCreateSimpleProductPayload({
      serviceType: "Traslado", name: "EZE - Hotel", price: 25000, vehicleType: "  Van  ",
    });
    assert.equal(payload.vehicleType, "Van");
    assert.equal(payload.cabinClass, null);
    assert.equal(payload.roomType, null);
  });

  it("traslado: vehicleType vacío queda null (no manda string vacío)", () => {
    const payload = buildCreateSimpleProductPayload({ serviceType: "Traslado", name: "EZE - Hotel", price: 25000, vehicleType: "   " });
    assert.equal(payload.vehicleType, null);
  });

  it("paquete/asistencia: sin variante natural (V2), todos los campos de variante en null", () => {
    const payload = buildCreateSimpleProductPayload({
      serviceType: "Paquete", name: "Bariloche", price: 100000,
      roomType: "Doble", mealPlan: "Desayuno", roomCategory: "Superior", cabinClass: "Business", vehicleType: "Van",
    });
    assert.equal(payload.roomType, null);
    assert.equal(payload.mealPlan, null);
    assert.equal(payload.roomCategory, null);
    assert.equal(payload.cabinClass, null);
    assert.equal(payload.vehicleType, null);
  });

  it("sin operador ni moneda: operador null, moneda default ARS, precio 0 si no es numérico", () => {
    const payload = buildCreateSimpleProductPayload({ serviceType: "Paquete", name: "Bariloche", price: "" });
    assert.equal(payload.supplierId, null);
    assert.equal(payload.currency, "ARS");
    assert.equal(payload.price, 0);
  });

  it("segundo intento tras el freno de repetidos: createAnyway viaja en true", () => {
    const payload = buildCreateSimpleProductPayload(
      { serviceType: "Hotel", name: "Maitei Posadas", city: "Posadas", price: 48000 },
      { createAnyway: true }
    );
    assert.equal(payload.createAnyway, true);
  });
});

describe("validateProductNameAndCity", () => {
  it("nombre vacío: error", () => {
    const errores = validateProductNameAndCity({ serviceType: "Aereo", name: "  " });
    assert.equal(errores.name, "Ingresá un nombre.");
  });

  it("hotel sin ciudad: error de ciudad además del de nombre si falta", () => {
    const errores = validateProductNameAndCity({ serviceType: "Hotel", name: "Maitei", city: "" });
    assert.equal(errores.city, "Ingresá una ciudad.");
    assert.equal(errores.name, undefined);
  });

  it("no-hotel sin ciudad: no exige ciudad (no es obligatoria fuera de Hotel)", () => {
    const errores = validateProductNameAndCity({ serviceType: "Aereo", name: "AEP-MIA", city: "" });
    assert.equal(errores.city, undefined);
  });

  it("formulario completo y válido: sin errores", () => {
    const errores = validateProductNameAndCity({ serviceType: "Hotel", name: "Maitei", city: "Posadas" });
    assert.deepEqual(errores, {});
  });
});

describe("buildSupplierPriceLineText", () => {
  it("arma operador · precio con unidad · fecha", () => {
    const texto = buildSupplierPriceLineText({
      supplierName: "Ola Mayorista",
      price: 48,
      currency: "USD",
      priceUnitLabel: "por noche",
      priceDate: "2026-05-22T00:00:00Z",
    });
    assert.match(texto, /Ola Mayorista/);
    assert.match(texto, /por noche/);
    assert.match(texto, /22\/05\/2026/);
  });

  it("sin unidad (no aplica al tipo): no agrega espacio de más", () => {
    const texto = buildSupplierPriceLineText({
      supplierName: "Aeromundo",
      price: 780,
      currency: "USD",
      priceUnitLabel: "",
      priceDate: "2026-06-14T00:00:00Z",
    });
    assert.equal(texto, "Aeromundo · US$780,00 · 14/06/2026");
  });

  it("sin fecha: arma la línea igual, sin esa parte", () => {
    const texto = buildSupplierPriceLineText({
      supplierName: "Ñandú Turismo",
      price: 410000,
      currency: "ARS",
      priceUnitLabel: "",
      priceDate: null,
    });
    assert.match(texto, /^Ñandú Turismo · \$.?410\.000,00$/);
  });

  it("sin datos: no rompe, devuelve string vacío", () => {
    assert.equal(buildSupplierPriceLineText(null), "");
  });
});

describe("buildRenameLearnedProductPayload", () => {
  it("hotel: manda city/newCity recortados", () => {
    const payload = buildRenameLearnedProductPayload({
      serviceType: "Hotel",
      currentName: "Maitei Posada",
      currentCity: "  Posadas  ",
      newName: "  Maitei Posadas  ",
      newCity: " Posadas, Misiones ",
    });
    assert.deepEqual(payload, {
      serviceType: "Hotel",
      name: "Maitei Posada",
      city: "Posadas",
      newName: "Maitei Posadas",
      newCity: "Posadas, Misiones",
    });
  });

  it("no-hotel: city y newCity van null (la ciudad no es parte de la identidad)", () => {
    const payload = buildRenameLearnedProductPayload({
      serviceType: "Aereo",
      currentName: "AEP-MIA",
      currentCity: "no debería viajar",
      newName: "AEP-MIA LATAM",
      newCity: "tampoco esto",
    });
    assert.equal(payload.city, null);
    assert.equal(payload.newCity, null);
  });
});

describe("resolveSimilarProductDialogDecision", () => {
  it("isConfirmed (botón 'Usar existente') → UseExisting", () => {
    const decision = resolveSimilarProductDialogDecision({ isConfirmed: true, isDenied: false, isDismissed: false });
    assert.equal(decision, SIMILAR_PRODUCT_DIALOG_DECISION.UseExisting);
  });

  it("isDenied (botón 'Crear uno nuevo igual') → CreateNewAnyway", () => {
    const decision = resolveSimilarProductDialogDecision({ isConfirmed: false, isDenied: true, isDismissed: false });
    assert.equal(decision, SIMILAR_PRODUCT_DIALOG_DECISION.CreateNewAnyway);
  });

  it("isDismissed (ESC/X/click afuera) → Dismissed, NUNCA crea nada (bug 2026-08-07)", () => {
    const decision = resolveSimilarProductDialogDecision({ isConfirmed: false, isDenied: false, isDismissed: true });
    assert.equal(decision, SIMILAR_PRODUCT_DIALOG_DECISION.Dismissed);
  });

  it("resultado vacío/undefined/null (defensivo) → Dismissed, no explota", () => {
    assert.equal(resolveSimilarProductDialogDecision({}), SIMILAR_PRODUCT_DIALOG_DECISION.Dismissed);
    assert.equal(resolveSimilarProductDialogDecision(undefined), SIMILAR_PRODUCT_DIALOG_DECISION.Dismissed);
    assert.equal(resolveSimilarProductDialogDecision(null), SIMILAR_PRODUCT_DIALOG_DECISION.Dismissed);
  });

  it("isConfirmed tiene prioridad si por algún bug de SweetAlert vinieran dos flags en true", () => {
    const decision = resolveSimilarProductDialogDecision({ isConfirmed: true, isDenied: true });
    assert.equal(decision, SIMILAR_PRODUCT_DIALOG_DECISION.UseExisting);
  });
});
