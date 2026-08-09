import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  debeDispararInterpretacion,
  esRespuestaUtilizable,
  soloFecha,
  construirPatchDeProducto,
  construirPatchDeResto,
  construirOverrideBuscador,
  puedeMostrarDuda,
  resolverRespuestaDuda,
  construirPatchDeSeleccionManual,
  debeResetearTocadoTrasSeleccion,
  DOUBT_FIELD,
} from "./serviceLineInterpretationLogic.js";
import { resolverCamposAlCambiarVariante } from "./variantPriceSuggestionLogic.js";

describe("debeDispararInterpretacion", () => {
  it("menos de 3 palabras: no dispara (no vale la pena gastar cuota del motor)", () => {
    assert.equal(debeDispararInterpretacion(""), false);
    assert.equal(debeDispararInterpretacion("sheraton"), false);
    assert.equal(debeDispararInterpretacion("sheraton iguazu"), false);
  });

  it("3 palabras o más: dispara", () => {
    assert.equal(debeDispararInterpretacion("sheraton iguazu doble"), true);
    assert.equal(debeDispararInterpretacion("  sheraton   iguazu   doble  "), true);
  });

  it("texto vacío o null no revienta", () => {
    assert.equal(debeDispararInterpretacion(null), false);
    assert.equal(debeDispararInterpretacion(undefined), false);
  });
});

describe("esRespuestaUtilizable (degradación total §3.5)", () => {
  it("interpreted:true → utilizable", () => {
    assert.equal(esRespuestaUtilizable({ interpreted: true }), true);
  });

  it("interpreted:false, null, undefined, respuesta rara → NO utilizable, sin distinción entre casos", () => {
    assert.equal(esRespuestaUtilizable({ interpreted: false }), false);
    assert.equal(esRespuestaUtilizable(null), false);
    assert.equal(esRespuestaUtilizable(undefined), false);
    assert.equal(esRespuestaUtilizable({}), false);
    assert.equal(esRespuestaUtilizable("no soy un objeto válido"), false);
  });
});

describe("soloFecha", () => {
  it("recorta la parte de fecha de un datetime ISO", () => {
    assert.equal(soloFecha("2026-09-12T00:00:00Z"), "2026-09-12");
  });

  it("vacío o null → string vacío", () => {
    assert.equal(soloFecha(null), "");
    assert.equal(soloFecha(""), "");
  });
});

describe("construirPatchDeProducto (Momento 3)", () => {
  const dto = {
    interpreted: true,
    product: { ratePublicId: "rate-1", name: "Sheraton Iguazú", subtitle: "Puerto Iguazú", confidence: "alta" },
  };

  it("producto reconocido y todavía sin resolver: arma el patch con nombre, ciudad y rateId", () => {
    const patch = construirPatchDeProducto({ dto, serviceType: "Hotel", productoYaResuelto: false });
    assert.deepEqual(patch, {
      hotelName: "Sheraton Iguazú",
      rateId: "rate-1",
      newCatalogProduct: null,
      city: "Puerto Iguazú",
    });
  });

  it("producto YA resuelto (rateId o newCatalogProduct ya elegidos a mano): no pisa nada", () => {
    assert.equal(construirPatchDeProducto({ dto, serviceType: "Hotel", productoYaResuelto: true }), null);
  });

  it("sin dto.product (Momento 4, solo parecidos): no arma patch de producto", () => {
    const sinProducto = { interpreted: true, product: null, productCandidates: [{ name: "Amerian" }] };
    assert.equal(construirPatchDeProducto({ dto: sinProducto, serviceType: "Hotel", productoYaResuelto: false }), null);
  });

  it("Aéreo no tiene columna de ciudad: el patch no trae ese campo", () => {
    const patch = construirPatchDeProducto({ dto, serviceType: "Aereo", productoYaResuelto: false });
    assert.deepEqual(patch, { routeName: "Sheraton Iguazú", rateId: "rate-1", newCatalogProduct: null });
  });
});

describe("construirPatchDeResto — Hotel", () => {
  const dtoCompleto = {
    interpreted: true,
    supplier: { supplierPublicId: "sup-1", name: "Ola Mayorista", confidence: "alta" },
    variant: { roomType: "Doble", mealPlan: "Desayuno", roomCategory: "Superior", confidence: "alta" },
    price: { amount: 48, currency: "USD", priceUnit: "noche_habitacion", priceUnitLabel: "por noche" },
    dates: { from: "2026-09-12T00:00:00Z", to: "2026-09-15T00:00:00Z" },
  };

  it("nada tocado + con permiso de costos: arma TODO el patch y lo marca sugerido", () => {
    const { patch, camposSugeridos } = construirPatchDeResto({
      dto: dtoCompleto, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set(),
    });
    assert.equal(patch.supplierId, "sup-1");
    assert.equal(patch.supplierName, "Ola Mayorista");
    assert.equal(patch.roomType, "Doble");
    assert.equal(patch.mealPlan, "Desayuno");
    assert.equal(patch.roomCategory, "Superior");
    assert.equal(patch.unitNetCost, "48");
    assert.equal(patch.currency, "USD");
    assert.equal(patch.checkIn, "2026-09-12");
    assert.equal(patch.checkOut, "2026-09-15");
    assert.deepEqual(
      camposSugeridos.sort(),
      ["checkIn", "checkOut", "currency", "mealPlan", "roomCategory", "roomType", "supplierId", "unitNetCost"].sort()
    );
  });

  it("sin permiso de ver costos: NUNCA precarga el costo (F-14/M-27), aunque el DTO lo traiga", () => {
    const { patch, camposSugeridos } = construirPatchDeResto({
      dto: dtoCompleto, serviceType: "Hotel", canSeeCost: false, camposTocados: new Set(),
    });
    assert.equal(patch.unitNetCost, undefined);
    assert.equal(patch.currency, undefined);
    assert.equal(camposSugeridos.includes("unitNetCost"), false);
    // El resto (operador, variante, fechas) sí se sigue completando
    assert.equal(patch.supplierId, "sup-1");
    assert.equal(patch.roomType, "Doble");
  });

  it("V10=A: un campo tocado por el vendedor queda AFUERA del patch aunque el motor lo traiga", () => {
    const tocados = new Set(["roomType", "checkIn"]);
    const { patch, camposSugeridos } = construirPatchDeResto({
      dto: dtoCompleto, serviceType: "Hotel", canSeeCost: true, camposTocados: tocados,
    });
    assert.equal(patch.roomType, undefined);
    assert.equal(patch.checkIn, undefined);
    assert.equal(camposSugeridos.includes("roomType"), false);
    assert.equal(camposSugeridos.includes("checkIn"), false);
    // Los no tocados se siguen completando normalmente
    assert.equal(patch.mealPlan, "Desayuno");
    assert.equal(patch.checkOut, "2026-09-15");
  });

  it("sin ningún dato en la respuesta: patch vacío, no revienta", () => {
    const { patch, camposSugeridos } = construirPatchDeResto({
      dto: { interpreted: true }, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set(),
    });
    assert.deepEqual(patch, {});
    assert.deepEqual(camposSugeridos, []);
  });

  it("dto null no revienta", () => {
    const { patch, camposSugeridos } = construirPatchDeResto({
      dto: null, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set(),
    });
    assert.deepEqual(patch, {});
    assert.deepEqual(camposSugeridos, []);
  });
});

describe("construirPatchDeResto — Paquete y Asistencia (sin variante, V2=todos con esa excepción)", () => {
  const dto = {
    interpreted: true,
    supplier: { supplierPublicId: "sup-2", name: "Julia Tours" },
    variant: { roomType: "Doble" }, // si el motor mandara algo acá, se ignora: no hay campo destino
    price: { amount: 900, currency: "ARS" },
    dates: { from: "2026-10-01T00:00:00Z", to: "2026-10-08T00:00:00Z" },
  };

  it("Paquete: no hay variantFields, así que variant no genera ningún campo", () => {
    const { patch } = construirPatchDeResto({ dto, serviceType: "Paquete", canSeeCost: true, camposTocados: new Set() });
    assert.equal(patch.roomType, undefined);
    assert.equal(patch.startDate, "2026-10-01");
    assert.equal(patch.endDate, "2026-10-08");
    assert.equal(patch.unitNetCost, "900");
  });

  it("Asistencia: mismo criterio, usa validFrom/validTo", () => {
    const { patch } = construirPatchDeResto({ dto, serviceType: "Asistencia", canSeeCost: true, camposTocados: new Set() });
    assert.equal(patch.validFrom, "2026-10-01");
    assert.equal(patch.validTo, "2026-10-08");
  });
});

describe("construirPatchDeResto — Traslado sin fecha de fin", () => {
  it("dateToField es null: aunque el motor mande dates.to, no hay dónde ponerlo", () => {
    const dto = { interpreted: true, dates: { from: "2026-09-12T00:00:00Z", to: "2026-09-15T00:00:00Z" } };
    const { patch, camposSugeridos } = construirPatchDeResto({
      dto, serviceType: "Traslado", canSeeCost: true, camposTocados: new Set(),
    });
    assert.equal(patch.pickupDate, "2026-09-12");
    assert.equal(patch.pickupDateTo, undefined);
    assert.equal(camposSugeridos.length, 1);
  });
});

describe("construirOverrideBuscador (Momento 4)", () => {
  it("sin producto directo pero con parecidos: arma el override con productSearchText", () => {
    const dto = {
      interpreted: true,
      product: null,
      productCandidates: [{ ratePublicId: "r1", name: "Amerian Posadas" }],
      productSearchText: "Amerian Posadas",
    };
    const override = construirOverrideBuscador({ dto, productoYaResuelto: false });
    assert.deepEqual(override, { candidates: dto.productCandidates, createText: "Amerian Posadas" });
  });

  it("con match directo (Momento 3): no hace falta override, se devuelve null", () => {
    const dto = { interpreted: true, product: { ratePublicId: "r1", name: "X" }, productCandidates: [] };
    assert.equal(construirOverrideBuscador({ dto, productoYaResuelto: false }), null);
  });

  it("sin candidatos: null (nada que ofrecer)", () => {
    const dto = { interpreted: true, product: null, productCandidates: [] };
    assert.equal(construirOverrideBuscador({ dto, productoYaResuelto: false }), null);
  });

  it("producto ya resuelto por otra vía: no hace falta mostrar override aunque haya candidatos", () => {
    const dto = { interpreted: true, product: null, productCandidates: [{ name: "X" }] };
    assert.equal(construirOverrideBuscador({ dto, productoYaResuelto: true }), null);
  });
});

describe("puedeMostrarDuda", () => {
  it("duda de precio: solo se muestra si el form tiene permiso de ver costos", () => {
    const doubt = { code: "precioPorNoche", field: DOUBT_FIELD.PRICE, question: "¿48 es por noche?" };
    assert.equal(puedeMostrarDuda({ doubt, serviceType: "Hotel", canSeeCost: true }), true);
    assert.equal(puedeMostrarDuda({ doubt, serviceType: "Hotel", canSeeCost: false }), false);
  });

  it("duda de operador: siempre visible (el campo Operador existe en todos los tipos)", () => {
    const doubt = { code: "operadorAmbiguo", field: DOUBT_FIELD.SUPPLIER, question: "¿es Ola?" };
    assert.equal(puedeMostrarDuda({ doubt, serviceType: "Paquete", canSeeCost: false }), true);
  });

  it("duda de fechas: visible si el tipo tiene campo de fecha (todos los tipos lo tienen)", () => {
    const doubt = { code: "anioDeFechas", field: DOUBT_FIELD.DATES, question: "¿es 2026?" };
    assert.equal(puedeMostrarDuda({ doubt, serviceType: "Traslado", canSeeCost: false }), true);
  });

  it("sin duda: false", () => {
    assert.equal(puedeMostrarDuda({ doubt: null, serviceType: "Hotel", canSeeCost: true }), false);
  });
});

describe("resolverRespuestaDuda", () => {
  it("respuesta 'Sí': no vacía nada, el amarillo queda como está", () => {
    const doubt = { code: "precioPorNoche", field: DOUBT_FIELD.PRICE, question: "¿48 es por noche?" };
    const resultado = resolverRespuestaDuda({ doubt, respuestaEsSi: true, serviceType: "Hotel" });
    assert.deepEqual(resultado, { camposAVaciar: [], campoParaEnfocar: null });
  });

  it("respuesta 'No' de precio: vacía el campo de costo de ESE tipo y enfoca ahí", () => {
    const doubt = { code: "precioPorNoche", field: DOUBT_FIELD.PRICE, question: "¿48 es por noche?" };
    const resultado = resolverRespuestaDuda({ doubt, respuestaEsSi: false, serviceType: "Hotel" });
    assert.deepEqual(resultado, { camposAVaciar: ["unitNetCost"], campoParaEnfocar: "unitNetCost" });
  });

  it("respuesta 'No' de operador: vacía supplierId", () => {
    const doubt = { code: "operadorAmbiguo", field: DOUBT_FIELD.SUPPLIER, question: "¿es Ola?" };
    const resultado = resolverRespuestaDuda({ doubt, respuestaEsSi: false, serviceType: "Aereo" });
    assert.deepEqual(resultado, { camposAVaciar: ["supplierId"], campoParaEnfocar: "supplierId" });
  });

  it("respuesta 'No' de fechas: vacía las dos puntas (no se puede saber cuál estaba mal)", () => {
    const doubt = { code: "anioDeFechas", field: DOUBT_FIELD.DATES, question: "¿es 2026?" };
    const resultado = resolverRespuestaDuda({ doubt, respuestaEsSi: false, serviceType: "Hotel" });
    assert.deepEqual(resultado, { camposAVaciar: ["checkIn", "checkOut"], campoParaEnfocar: "checkIn" });
  });

  it("respuesta 'No' de fechas en un tipo sin fecha de fin (Traslado): vacía solo la que existe", () => {
    const doubt = { code: "anioDeFechas", field: DOUBT_FIELD.DATES, question: "¿es 2026?" };
    const resultado = resolverRespuestaDuda({ doubt, respuestaEsSi: false, serviceType: "Traslado" });
    assert.deepEqual(resultado, { camposAVaciar: ["pickupDate"], campoParaEnfocar: "pickupDate" });
  });

  it("sin duda: no revienta", () => {
    assert.deepEqual(
      resolverRespuestaDuda({ doubt: null, respuestaEsSi: false, serviceType: "Hotel" }),
      { camposAVaciar: [], campoParaEnfocar: null }
    );
  });
});

// ─── Caso (a) del revisor funcional (bloqueante B1): V10=A en la duda ─────────────
// Reproduce el bug reportado: el vendedor escribió el costo "50" A MANO. El motor,
// interpretando la frase vieja, igual puede devolver la duda "¿48 es el precio por
// noche?" (no sabe que el vendedor ya corrigió el número en pantalla). Mostrar esa duda y
// dejar que "No" la borre destruiría el 50 que el vendedor puso a propósito.
describe("B1 — la duda NUNCA toca un campo que el vendedor ya tocó (V10=A)", () => {
  it("puedeMostrarDuda: campo de precio tocado → la duda de precio NO se ofrece", () => {
    const doubt = { code: "precioPorNoche", field: DOUBT_FIELD.PRICE, question: "¿48 es el precio por noche?" };
    const tocados = new Set(["unitNetCost"]); // el vendedor escribió 50 a mano
    assert.equal(
      puedeMostrarDuda({ doubt, serviceType: "Hotel", canSeeCost: true, camposTocados: tocados }),
      false
    );
  });

  it("puedeMostrarDuda: campo de precio SIN tocar → la duda se sigue ofreciendo normal", () => {
    const doubt = { code: "precioPorNoche", field: DOUBT_FIELD.PRICE, question: "¿48 es el precio por noche?" };
    assert.equal(
      puedeMostrarDuda({ doubt, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set() }),
      true
    );
  });

  it("puedeMostrarDuda: operador tocado → la duda de operador NO se ofrece", () => {
    const doubt = { code: "operadorAmbiguo", field: DOUBT_FIELD.SUPPLIER, question: "¿es Ola?" };
    assert.equal(
      puedeMostrarDuda({ doubt, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set(["supplierId"]) }),
      false
    );
  });

  it("puedeMostrarDuda: CUALQUIERA de las dos fechas tocada → la duda de fechas NO se ofrece", () => {
    const doubt = { code: "anioDeFechas", field: DOUBT_FIELD.DATES, question: "¿es 2026?" };
    assert.equal(
      puedeMostrarDuda({ doubt, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set(["checkOut"]) }),
      false
    );
  });

  it("resolverRespuestaDuda: defensa en profundidad — si igual se invoca sobre un campo tocado, NO lo vacía (el 50 del vendedor sobrevive)", () => {
    const doubt = { code: "precioPorNoche", field: DOUBT_FIELD.PRICE, question: "¿48 es el precio por noche?" };
    const resultado = resolverRespuestaDuda({
      doubt, respuestaEsSi: false, serviceType: "Hotel", camposTocados: new Set(["unitNetCost"]),
    });
    assert.deepEqual(resultado, { camposAVaciar: [], campoParaEnfocar: null });
  });

  it("resolverRespuestaDuda: fechas con SOLO una punta tocada — vacía nada más que la libre (nunca la tocada)", () => {
    const doubt = { code: "anioDeFechas", field: DOUBT_FIELD.DATES, question: "¿es 2026?" };
    const resultado = resolverRespuestaDuda({
      doubt, respuestaEsSi: false, serviceType: "Hotel", camposTocados: new Set(["checkIn"]),
    });
    assert.deepEqual(resultado, { camposAVaciar: ["checkOut"], campoParaEnfocar: "checkOut" });
  });

  it("resolverRespuestaDuda sigue funcionando igual que antes cuando NADA está tocado", () => {
    const doubt = { code: "operadorAmbiguo", field: DOUBT_FIELD.SUPPLIER, question: "¿es Ola?" };
    const resultado = resolverRespuestaDuda({ doubt, respuestaEsSi: false, serviceType: "Hotel", camposTocados: new Set() });
    assert.deepEqual(resultado, { camposAVaciar: ["supplierId"], campoParaEnfocar: "supplierId" });
  });
});

// ─── Caso (b) del revisor funcional (bloqueante B2): precedencia precio-de-la-frase ──
// El costo que sale de la frase ("48 usd") es un dato que el vendedor DIJO, tanto como si
// lo hubiera tecleado. El puente (useServiceLineInterpretationForForm) tiene que marcar
// `precioTocadoPorElUsuario=true` cuando construirPatchDeResto trae un costo — así la
// sugerencia POR VARIANTE (resolverCamposAlCambiarVariante, en variantPriceSuggestionLogic.js)
// nunca lo vuelve a pisar 300ms después. Este test encadena las DOS funciones puras para
// probar la precedencia de punta a punta, sin necesidad de montar ningún componente.
describe("B2 — precedencia: el precio que sale de la frase le gana a la sugerencia por variante", () => {
  it("costo de la frase (48) marcado como tocado → la sugerencia por variante (70, otra habitación) NO lo pisa", () => {
    const dto = {
      interpreted: true,
      price: { amount: 48, currency: "USD" },
    };
    const { patch, camposSugeridos } = construirPatchDeResto({
      dto, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set(),
    });
    assert.equal(patch.unitNetCost, "48");
    assert.ok(camposSugeridos.includes("unitNetCost"));

    // El puente ve que "unitNetCost" quedó sugerido por la frase → prende el flag que
    // protege a la sugerencia por variante (igual que un tecleo manual la protegería).
    const precioTocadoPorElUsuario = camposSugeridos.includes("unitNetCost");

    // 300ms después, useVariantPriceSuggestion resuelve con el precio de OTRA habitación.
    const resultadoVariante = resolverCamposAlCambiarVariante({
      estaPrecioTocado: precioTocadoPorElUsuario,
      estaMonedaTocada: camposSugeridos.includes("currency"),
      suggestion: { isSameVariant: false, price: 70, currency: "USD", suggestionText: "Doble Superior · US$ 70" },
    });
    assert.equal(resultadoVariante.debeActualizarPrecio, false, "el 48 de la frase no se pisa");
    assert.equal(resultadoVariante.debeActualizarMoneda, false);
  });

  it("SIN costo en la frase (dto.price null) → precioTocadoPorElUsuario queda false → la sugerencia por variante actúa normal", () => {
    const dto = { interpreted: true, supplier: { supplierPublicId: "sup-1", name: "Ola" } };
    const { camposSugeridos } = construirPatchDeResto({
      dto, serviceType: "Hotel", canSeeCost: true, camposTocados: new Set(),
    });
    const precioTocadoPorElUsuario = camposSugeridos.includes("unitNetCost");
    assert.equal(precioTocadoPorElUsuario, false);

    const resultadoVariante = resolverCamposAlCambiarVariante({
      estaPrecioTocado: precioTocadoPorElUsuario,
      estaMonedaTocada: false,
      suggestion: { isSameVariant: true, price: 55, currency: "USD", suggestionText: "Doble · US$ 55" },
    });
    // Sin precio en la frase, la sugerencia por variante tiene vía libre (comportamiento normal).
    assert.equal(resultadoVariante.debeActualizarPrecio, true);
    assert.equal(resultadoVariante.price, "55");
  });

  it("costo de la frase para un tipo SIN variante (Paquete) también queda protegido igual", () => {
    const dto = { interpreted: true, price: { amount: 900, currency: "ARS" } };
    const { camposSugeridos } = construirPatchDeResto({
      dto, serviceType: "Paquete", canSeeCost: true, camposTocados: new Set(),
    });
    assert.ok(camposSugeridos.includes("unitNetCost"));
  });
});

// ─── Caso (c) del revisor funcional (bloqueante B3): merge al elegir un parecido ──────
describe("B3 — construirPatchDeSeleccionManual (Momento 4: elegir un parecido)", () => {
  const sale = { supplierPublicId: "sup-catalogo", supplierName: "Julia Tours", netCost: 90, salePrice: 120, currency: "ARS" };

  it("nada sugerido ni tocado todavía: aplica la venta del catálogo tal cual (comportamiento de siempre)", () => {
    const { patch, camposSugeridos } = construirPatchDeSeleccionManual({
      serviceType: "Hotel", sale, canSeeCost: true, camposActualmenteSugeridos: {}, camposTocados: new Set(),
    });
    assert.equal(patch.supplierId, "sup-catalogo");
    assert.equal(patch.unitNetCost, "90");
    assert.equal(patch.unitSalePrice, "120");
    assert.equal(patch.currency, "ARS");
    assert.deepEqual(camposSugeridos, {
      supplierId: true, unitNetCost: true, unitSalePrice: true, currency: true,
    });
  });

  it("operador YA sugerido por la línea inteligente (amarillo): NO lo pisa con el del catálogo", () => {
    const { patch, camposSugeridos } = construirPatchDeSeleccionManual({
      serviceType: "Hotel", sale, canSeeCost: true,
      camposActualmenteSugeridos: { supplierId: true }, // ya amarillo, vino de la frase
      camposTocados: new Set(),
    });
    assert.equal(patch.supplierId, undefined);
    assert.equal(camposSugeridos.supplierId, undefined);
    // El resto (que no estaba protegido) se sigue completando normal
    assert.equal(patch.unitNetCost, "90");
  });

  it("costo TOCADO por el vendedor: NO lo pisa con el del catálogo", () => {
    const { patch } = construirPatchDeSeleccionManual({
      serviceType: "Hotel", sale, canSeeCost: true,
      camposActualmenteSugeridos: {},
      camposTocados: new Set(["unitNetCost"]),
    });
    assert.equal(patch.unitNetCost, undefined);
    // Operador y venta, sin protección, se completan igual
    assert.equal(patch.supplierId, "sup-catalogo");
    assert.equal(patch.unitSalePrice, "120");
  });

  it("sin permiso de ver costos: nunca intenta pisar el costo (F-14), pase lo que pase con sugeridos/tocados", () => {
    const { patch } = construirPatchDeSeleccionManual({
      serviceType: "Hotel", sale, canSeeCost: false, camposActualmenteSugeridos: {}, camposTocados: new Set(),
    });
    assert.equal(patch.unitNetCost, undefined);
    assert.equal(patch.unitSalePrice, "120"); // venta sí es visible sin el permiso
  });

  it("Aéreo usa netCost/salePrice (no unitNetCost/unitSalePrice)", () => {
    const { patch } = construirPatchDeSeleccionManual({
      serviceType: "Aereo", sale, canSeeCost: true, camposActualmenteSugeridos: {}, camposTocados: new Set(),
    });
    assert.equal(patch.netCost, "90");
    assert.equal(patch.salePrice, "120");
  });

  it("todo protegido (sugerido Y tocado): el patch queda vacío, no revienta", () => {
    const { patch, camposSugeridos } = construirPatchDeSeleccionManual({
      serviceType: "Hotel", sale, canSeeCost: true,
      camposActualmenteSugeridos: { supplierId: true, unitSalePrice: true, currency: true },
      camposTocados: new Set(["unitNetCost"]),
    });
    assert.deepEqual(patch, {});
    assert.deepEqual(camposSugeridos, {});
  });

  it("sale null/undefined (candidato sin ventas previas): no revienta, deja los campos protegidos igual afuera", () => {
    const { patch } = construirPatchDeSeleccionManual({
      serviceType: "Hotel", sale: null, canSeeCost: true, camposActualmenteSugeridos: {}, camposTocados: new Set(),
    });
    assert.equal(patch.supplierId, "");
    assert.equal(patch.unitNetCost, "");
  });
});

// ─── Caso (a) de la segunda vuelta del revisor funcional (bloqueante B2 residual) ──
// Reproduce el bug reportado: Momento 4, el precio ya venía sugerido POR LA FRASE
// (protegido, `construirPatchDeSeleccionManual` no lo pisó) — el *InlineForm no puede
// soltar el flag "tocado" acá, o la sugerencia por variante lo pisa 300ms después.
describe("debeResetearTocadoTrasSeleccion (segunda vuelta, bloqueante B2 residual)", () => {
  it("Momento 4 + campo QUEDÓ protegido (no aparece en camposSugeridosDeVenta): NO soltar el flag", () => {
    // construirPatchDeSeleccionManual no incluyó 'unitNetCost' en su retorno porque ya
    // estaba sugerido por la frase — el precio de la frase sigue en pie.
    const camposSugeridosDeVenta = { supplierId: true }; // unitNetCost NO está acá
    const resultado = debeResetearTocadoTrasSeleccion({
      fromAiOverride: true, campo: "unitNetCost", camposSugeridosDeVenta,
    });
    assert.equal(resultado, false);
  });

  it("Momento 4 + campo SÍ se pisó con la venta del catálogo: soltar el flag (vía libre normal)", () => {
    const camposSugeridosDeVenta = { supplierId: true, unitNetCost: true };
    const resultado = debeResetearTocadoTrasSeleccion({
      fromAiOverride: true, campo: "unitNetCost", camposSugeridosDeVenta,
    });
    assert.equal(resultado, true);
  });

  it("camino manual sin IA (fromAiOverride false): SIEMPRE suelta, como toda la vida", () => {
    const resultado = debeResetearTocadoTrasSeleccion({
      fromAiOverride: false, campo: "unitNetCost", camposSugeridosDeVenta: {},
    });
    assert.equal(resultado, true);
  });

  it("moneda sigue la MISMA regla que precio, de forma independiente", () => {
    const protegida = debeResetearTocadoTrasSeleccion({
      fromAiOverride: true, campo: "currency", camposSugeridosDeVenta: { unitNetCost: true }, // currency no está
    });
    assert.equal(protegida, false);

    const pisada = debeResetearTocadoTrasSeleccion({
      fromAiOverride: true, campo: "currency", camposSugeridosDeVenta: { currency: true },
    });
    assert.equal(pisada, true);
  });

  it("camposSugeridosDeVenta null/undefined no revienta (Momento 4, nada se pisó)", () => {
    const resultado = debeResetearTocadoTrasSeleccion({
      fromAiOverride: true, campo: "unitNetCost", camposSugeridosDeVenta: null,
    });
    assert.equal(resultado, false);
  });
});
