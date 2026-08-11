import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  debeDispararDedupMatch,
  hayParecidoFuerte,
  esRespuestaUtilizable,
  mergearCandidatosDedup,
  resolverTextoDeCrear,
  resolverListaParaMostrar,
  contarOpcionesNavegables,
  busquedaLocalDebil,
  pareceLineaCompleta,
  extraerInterpretacionParaPrecarga,
  esDudaDeProducto,
  debeMostrarDuda,
  dudaDeProductoLocal,
} from "./productDedupMatchLogic.js";

describe("debeDispararDedupMatch", () => {
  it("menos de 2 palabras: no dispara", () => {
    assert.equal(debeDispararDedupMatch(""), false);
    assert.equal(debeDispararDedupMatch("sheraton"), false);
  });

  it("2 palabras o más: dispara (umbral más bajo que la vieja línea inteligente)", () => {
    assert.equal(debeDispararDedupMatch("sheraton iguazu"), true);
    assert.equal(debeDispararDedupMatch("  sheraton   iguazu  doble  "), true);
  });

  it("texto vacío/null/undefined no revienta", () => {
    assert.equal(debeDispararDedupMatch(null), false);
    assert.equal(debeDispararDedupMatch(undefined), false);
  });
});

describe("hayParecidoFuerte", () => {
  const UMBRAL = 0.65;

  it("sin resultados: no hay parecido fuerte", () => {
    assert.equal(hayParecidoFuerte([], UMBRAL), false);
    assert.equal(hayParecidoFuerte(null, UMBRAL), false);
  });

  it("primer resultado sin score (backend no lo mandó): cuenta como fuerte, igual que el resaltado del dropdown", () => {
    assert.equal(hayParecidoFuerte([{ ratePublicId: "r1" }], UMBRAL), true);
  });

  it("primer resultado con score arriba del umbral: fuerte", () => {
    assert.equal(hayParecidoFuerte([{ ratePublicId: "r1", score: 0.9 }], UMBRAL), true);
  });

  it("primer resultado con score abajo del umbral: NO es fuerte (acá dispara el matcher)", () => {
    assert.equal(hayParecidoFuerte([{ ratePublicId: "r1", score: 0.2 }], UMBRAL), false);
  });

  it("solo mira el PRIMER resultado (el resto no importa para esta decisión)", () => {
    const resultados = [{ ratePublicId: "r1", score: 0.1 }, { ratePublicId: "r2", score: 0.99 }];
    assert.equal(hayParecidoFuerte(resultados, UMBRAL), false);
  });
});

describe("esRespuestaUtilizable (degradación total)", () => {
  it("interpreted:true → utilizable", () => {
    assert.equal(esRespuestaUtilizable({ interpreted: true }), true);
  });

  it("interpreted:false, null, undefined, forma rara → NO utilizable, sin distinción entre casos", () => {
    assert.equal(esRespuestaUtilizable({ interpreted: false }), false);
    assert.equal(esRespuestaUtilizable(null), false);
    assert.equal(esRespuestaUtilizable(undefined), false);
    assert.equal(esRespuestaUtilizable({}), false);
  });
});

describe("mergearCandidatosDedup", () => {
  it("agrega los candidatos que NO estaban, al final, sin reordenar lo existente", () => {
    const actuales = [{ ratePublicId: "r1", name: "Maitei" }];
    const candidatos = [{ ratePublicId: "r2", name: "Amerian" }];
    const resultado = mergearCandidatosDedup(actuales, candidatos, 8);
    assert.deepEqual(resultado, [
      { ratePublicId: "r1", name: "Maitei" },
      { ratePublicId: "r2", name: "Amerian" },
    ]);
  });

  it("no duplica un candidato que YA está en la lista (mismo ratePublicId)", () => {
    const actuales = [{ ratePublicId: "r1", name: "Maitei" }];
    const candidatos = [{ ratePublicId: "r1", name: "Maitei Posadas (motor)" }];
    const resultado = mergearCandidatosDedup(actuales, candidatos, 8);
    assert.equal(resultado.length, 1);
    assert.equal(resultado[0].name, "Maitei"); // el de la lista original gana, no se pisa
  });

  it("candidato sin ratePublicId se descarta (no hay forma segura de deduplicarlo)", () => {
    const resultado = mergearCandidatosDedup([], [{ name: "sin id" }], 8);
    assert.deepEqual(resultado, []);
  });

  it("respeta el tope máximo de filas", () => {
    const actuales = [{ ratePublicId: "r1" }, { ratePublicId: "r2" }];
    const candidatos = [{ ratePublicId: "r3" }, { ratePublicId: "r4" }];
    const resultado = mergearCandidatosDedup(actuales, candidatos, 3);
    assert.equal(resultado.length, 3);
  });

  it("listas vacías/null no revientan", () => {
    assert.deepEqual(mergearCandidatosDedup(null, null, 8), []);
    assert.deepEqual(mergearCandidatosDedup([], [], 8), []);
  });

  it("fix #10: locales YA llenan el tope (8) y el motor trajo candidatos — se reservan 2 lugares (6 locales + 2 motor)", () => {
    const actuales = Array.from({ length: 8 }, (_, i) => ({ ratePublicId: `local-${i}` }));
    const candidatos = [{ ratePublicId: "motor-1" }, { ratePublicId: "motor-2" }, { ratePublicId: "motor-3" }];
    const resultado = mergearCandidatosDedup(actuales, candidatos, 8);
    assert.equal(resultado.length, 8);
    // Los primeros 6 son locales, tal cual estaban (sin reordenar)
    assert.deepEqual(resultado.slice(0, 6).map((r) => r.ratePublicId), ["local-0", "local-1", "local-2", "local-3", "local-4", "local-5"]);
    // Los últimos 2 son del motor (el 3ro no entra: solo hay 2 lugares reservados)
    assert.deepEqual(resultado.slice(6).map((r) => r.ratePublicId), ["motor-1", "motor-2"]);
  });

  it("fix #10: locales llenan el tope pero el motor NO trajo nada — los locales ocupan el tope completo, sin cambios", () => {
    const actuales = Array.from({ length: 8 }, (_, i) => ({ ratePublicId: `local-${i}` }));
    const resultado = mergearCandidatosDedup(actuales, [], 8);
    assert.equal(resultado.length, 8);
    assert.deepEqual(resultado.map((r) => r.ratePublicId), actuales.map((r) => r.ratePublicId));
  });

  it("fix #10: pocos locales (menos que el tope) y motor con candidatos — el motor no 'roba' lugares de más, solo ocupa lo que sobra", () => {
    const actuales = [{ ratePublicId: "local-0" }, { ratePublicId: "local-1" }, { ratePublicId: "local-2" }];
    const candidatos = [{ ratePublicId: "motor-1" }, { ratePublicId: "motor-2" }, { ratePublicId: "motor-3" }, { ratePublicId: "motor-4" }, { ratePublicId: "motor-5" }];
    const resultado = mergearCandidatosDedup(actuales, candidatos, 8);
    // 3 locales + hasta 5 lugares libres para el motor (no hace falta recortar locales)
    assert.equal(resultado.length, 8);
    assert.deepEqual(resultado.slice(0, 3).map((r) => r.ratePublicId), ["local-0", "local-1", "local-2"]);
    assert.deepEqual(resultado.slice(3).map((r) => r.ratePublicId), ["motor-1", "motor-2", "motor-3", "motor-4", "motor-5"]);
  });

  it("sin tope explícito: no recorta nada", () => {
    const actuales = [{ ratePublicId: "r1" }];
    const candidatos = [{ ratePublicId: "r2" }, { ratePublicId: "r3" }];
    const resultado = mergearCandidatosDedup(actuales, candidatos);
    assert.equal(resultado.length, 3);
  });
});

describe("resolverTextoDeCrear", () => {
  it("con productSearchText del motor: lo usa (nombre limpio, sin la frase completa)", () => {
    const resultado = resolverTextoDeCrear("Amerian Posadas", "hotel amerian posadas triple mp julia 91000 pesos");
    assert.equal(resultado, "Amerian Posadas");
  });

  it("sin productSearchText (motor no entendió o degradó): usa la frase tal cual escribió el vendedor", () => {
    const resultado = resolverTextoDeCrear(null, "sheraton iguazu");
    assert.equal(resultado, "sheraton iguazu");
  });

  it("productSearchText vacío o solo espacios: cae al texto original", () => {
    assert.equal(resolverTextoDeCrear("   ", "sheraton iguazu"), "sheraton iguazu");
    assert.equal(resolverTextoDeCrear("", "sheraton iguazu"), "sheraton iguazu");
  });

  it("recorta espacios de sobra en ambos casos", () => {
    assert.equal(resolverTextoDeCrear("  Amerian Posadas  ", ""), "Amerian Posadas");
    assert.equal(resolverTextoDeCrear(undefined, "  sheraton  "), "sheraton");
  });

  // ─── H-3 (2026-08-11): el motor "limpia de más" y pierde parte del nombre real ────

  it("H-3: 'hotel e3' con limpio 'hotel' (pierde 'e3', que no era fecha ni operador) — usa el CRUDO", () => {
    // El limpio solo conserva 1 de las 2 palabras significativas del crudo (50% < 60%)
    // y no es una frase con fecha/número (1 sola palabra en el limpio) — se descarta.
    const resultado = resolverTextoDeCrear("hotel", "hotel e3");
    assert.equal(resultado, "hotel e3");
  });

  it("H-3: frase completa con fechas ('...del 10/02 al 15/02') — usa el LIMPIO aunque no llegue al 60%", () => {
    // El limpio ("iberostar waves") solo conserva 2 de las ~4 palabras significativas
    // del crudo, pero es una frase (2+ palabras) y el crudo trae fechas — el motor
    // separó bien "esto es el producto" de "esto es la fecha".
    const resultado = resolverTextoDeCrear("iberostar waves", "iberostar waves del 10/02 al 15/02");
    assert.equal(resultado, "iberostar waves");
  });
});

describe("resolverListaParaMostrar (bloqueante: no sorprender a quien navega con teclado)", () => {
  const listaCongelada = [{ ratePublicId: "r1", name: "Maitei Posadas" }];
  const listaFresca = [
    { ratePublicId: "r1", name: "Maitei Posadas" },
    { ratePublicId: "r2", name: "Amerian Posadas" }, // llegó del matcher mientras navegaba
  ];

  it("keyboardIndex >= 0 (navegando): devuelve la lista CONGELADA, ignora la fresca aunque haya crecido", () => {
    const resultado = resolverListaParaMostrar({ keyboardIndex: 0, listaCongelada, listaFresca });
    assert.deepEqual(resultado, listaCongelada);
    assert.equal(resultado.length, 1);
  });

  it("keyboardIndex en cualquier posición >= 0 (no solo 0): sigue congelada", () => {
    const resultado = resolverListaParaMostrar({ keyboardIndex: 3, listaCongelada, listaFresca });
    assert.deepEqual(resultado, listaCongelada);
  });

  it("keyboardIndex -1 (cursor en el input, sin navegar): devuelve la lista FRESCA", () => {
    const resultado = resolverListaParaMostrar({ keyboardIndex: -1, listaCongelada, listaFresca });
    assert.deepEqual(resultado, listaFresca);
    assert.equal(resultado.length, 2);
  });

  it("listas null/undefined no revientan, devuelve array vacío", () => {
    assert.deepEqual(resolverListaParaMostrar({ keyboardIndex: 0, listaCongelada: null, listaFresca: null }), []);
    assert.deepEqual(resolverListaParaMostrar({ keyboardIndex: -1, listaCongelada: null, listaFresca: null }), []);
  });
});

describe("contarOpcionesNavegables (D12: la ✨ NUNCA es una opción más)", () => {
  it("resultados + 'crear': suma los dos", () => {
    assert.equal(contarOpcionesNavegables({ cantidadResultados: 3, hayOpcionCrear: true }), 4);
  });

  it("sin opción crear: solo los resultados", () => {
    assert.equal(contarOpcionesNavegables({ cantidadResultados: 3, hayOpcionCrear: false }), 3);
  });

  it("la función ni siquiera RECIBE la duda con ✨: no hay forma de que la cuente", () => {
    // Esta prueba documenta la garantía estructural: contarOpcionesNavegables no tiene
    // ningún parámetro para la duda, así que agregarla en el dropdown (spec D12) nunca
    // puede sumar una opción de más ni correr el índice del teclado.
    assert.equal(contarOpcionesNavegables({ cantidadResultados: 0, hayOpcionCrear: true }), 1);
  });

  it("sin resultados y sin crear: cero", () => {
    assert.equal(contarOpcionesNavegables({ cantidadResultados: 0, hayOpcionCrear: false }), 0);
  });
});

describe("busquedaLocalDebil (gate D5: cuándo vale la pena llamar al motor)", () => {
  it("sin resultados locales: floja", () => {
    assert.equal(busquedaLocalDebil([]), true);
    assert.equal(busquedaLocalDebil(null), true);
  });

  it("primer resultado con score bajo (< 0.45): floja", () => {
    assert.equal(busquedaLocalDebil([{ ratePublicId: "r1", score: 0.2 }]), true);
  });

  it("primer resultado con score alto (>= 0.45): NO es floja", () => {
    assert.equal(busquedaLocalDebil([{ ratePublicId: "r1", score: 0.9 }]), false);
    assert.equal(busquedaLocalDebil([{ ratePublicId: "r1", score: 0.45 }]), false);
  });

  it("primer resultado sin score (backend no lo mandó): NO se considera floja", () => {
    assert.equal(busquedaLocalDebil([{ ratePublicId: "r1" }]), false);
  });
});

describe("pareceLineaCompleta (gate D5: ¿esto es una frase, no un nombre suelto?)", () => {
  it("frase con fecha en números y operador: sí (trae dígitos)", () => {
    assert.equal(pareceLineaCompleta("llao llao del 10/02 al 15/02 con delfos"), true);
  });

  it("una palabra sola (nombre de producto): no", () => {
    assert.equal(pareceLineaCompleta("sheraton"), false);
  });

  it("4+ palabras significativas sin dígitos ni mes: sí", () => {
    assert.equal(pareceLineaCompleta("hotel de la cañada cordoba"), true);
  });

  it("texto vacío o solo espacios: no", () => {
    assert.equal(pareceLineaCompleta(""), false);
    assert.equal(pareceLineaCompleta("   "), false);
    assert.equal(pareceLineaCompleta(null), false);
    assert.equal(pareceLineaCompleta(undefined), false);
  });

  it("menciona un mes en español, sin dígitos: sí", () => {
    assert.equal(pareceLineaCompleta("hotel sheraton febrero con delfos"), true);
  });

  it("patrón 'del ... al ...' sin dígitos (fechas en palabras): sí", () => {
    assert.equal(pareceLineaCompleta("del lunes al viernes en cordoba"), true);
  });

  it("dos palabras cortas, sin dígitos/mes/patrón: no llega al piso de 4 palabras", () => {
    assert.equal(pareceLineaCompleta("sheraton iguazu"), false);
  });
});

describe("extraerInterpretacionParaPrecarga (D13: el hack de la frase completa)", () => {
  it("con operador y fechas: devuelve los dos", () => {
    const respuesta = {
      interpreted: true,
      supplier: { supplierPublicId: "sup-1", name: "Delfos", confidence: "alta" },
      dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z", confidence: "alta" },
    };
    const resultado = extraerInterpretacionParaPrecarga(respuesta);
    assert.deepEqual(resultado, {
      supplier: { supplierPublicId: "sup-1", name: "Delfos" },
      dates: { from: "2026-02-10T00:00:00Z", to: "2026-02-15T00:00:00Z" },
    });
  });

  it("solo fecha, sin operador reconocido: supplier queda null", () => {
    const respuesta = { interpreted: true, supplier: null, dates: { from: "2026-02-10T00:00:00Z", to: null } };
    const resultado = extraerInterpretacionParaPrecarga(respuesta);
    assert.equal(resultado.supplier, null);
    assert.deepEqual(resultado.dates, { from: "2026-02-10T00:00:00Z", to: null });
  });

  it("supplier sin supplierPublicId (forma rara): se descarta, no revienta", () => {
    const respuesta = { interpreted: true, supplier: { name: "sin id" }, dates: null };
    assert.equal(extraerInterpretacionParaPrecarga(respuesta), null);
  });

  it("nada utilizable (todo null): devuelve null, no un objeto vacío", () => {
    assert.equal(extraerInterpretacionParaPrecarga({ interpreted: true, supplier: null, dates: null }), null);
  });

  it("respuesta null/undefined: no revienta, devuelve null", () => {
    assert.equal(extraerInterpretacionParaPrecarga(null), null);
    assert.equal(extraerInterpretacionParaPrecarga(undefined), null);
  });
});

// ─── esDudaDeProducto (fix C-4, review 2026-08-10) ────────────────────────────────
// El motor emite 4 tipos de duda; la ✨ del desplegable es SOLO para la de PRODUCTO.
// Las otras 3 (precio/operador/fechas) tienen su propio mecanismo firmado (D12-bis).

describe("esDudaDeProducto", () => {
  it("field='producto' (productoAmbiguo): SÍ es duda de producto", () => {
    assert.equal(esDudaDeProducto({ code: "productoAmbiguo", field: "producto", question: "¿Cuál de los dos?" }), true);
  });

  it("field='precio' (precioPorNoche): NO es duda de producto", () => {
    assert.equal(esDudaDeProducto({ code: "precioPorNoche", field: "precio", question: "¿Es por noche?" }), false);
  });

  it("field='operador' (operadorAmbiguo): NO es duda de producto", () => {
    assert.equal(esDudaDeProducto({ code: "operadorAmbiguo", field: "operador", question: "¿El operador es X?" }), false);
  });

  it("field='fechas' (anioDeFechas): NO es duda de producto", () => {
    assert.equal(esDudaDeProducto({ code: "anioDeFechas", field: "fechas", question: "¿Es este año?" }), false);
  });

  it("sin duda (null/undefined): false, sin romper", () => {
    assert.equal(esDudaDeProducto(null), false);
    assert.equal(esDudaDeProducto(undefined), false);
  });

  it("duda sin field (forma rara): false", () => {
    assert.equal(esDudaDeProducto({ code: "productoAmbiguo", question: "¿Cuál?" }), false);
  });
});

// ─── dudaDeProductoLocal (H-1, 2026-08-11: duda de producto SIN el motor) ─────────
// El gate `busquedaLocalDebil` apaga el motor justo cuando la búsqueda local YA
// encontró dos resultados fuertes casi iguales — este helper arma la misma pregunta
// mirando esos 2 resultados, sin ninguna llamada extra.

describe("dudaDeProductoLocal", () => {
  it("mismo nombre + subtitles distintos: arma la pregunta con las dos ciudades", () => {
    const resultados = [
      { name: "Sheraton Iguazú", subtitle: "Puerto Iguazú" },
      { name: "Sheraton Iguazú", subtitle: "Posadas" },
    ];
    assert.deepEqual(dudaDeProductoLocal(resultados), {
      field: "producto",
      question: "¿Sheraton Iguazú de Puerto Iguazú o el de Posadas?",
    });
  });

  it("mismo nombre (con tildes/mayúsculas/espacios distintos) + mismo subtitle: no hay duda", () => {
    const resultados = [
      { name: "Sheratón Iguazú", subtitle: "Puerto Iguazú" },
      { name: "sheraton  iguazu", subtitle: "Puerto Iguazú" },
    ];
    assert.equal(dudaDeProductoLocal(resultados), null);
  });

  it("nombres distintos: no hay duda (el vendedor ya los distingue solo)", () => {
    const resultados = [
      { name: "Sheraton Iguazú", subtitle: "Puerto Iguazú" },
      { name: "Hotel Colón", subtitle: "Posadas" },
    ];
    assert.equal(dudaDeProductoLocal(resultados), null);
  });

  it("sin subtitle pero operadores distintos (lastSale.supplierName): arma la pregunta con los operadores", () => {
    const resultados = [
      { name: "Excursión Cataratas", lastSale: { supplierName: "Ola Mayorista" } },
      { name: "Excursión Cataratas", lastSale: { supplierName: "Delfos" } },
    ];
    assert.deepEqual(dudaDeProductoLocal(resultados), {
      field: "producto",
      question: "¿Excursión Cataratas de Ola Mayorista o el de Delfos?",
    });
  });

  it("mismo nombre, sin subtitle NI supplierName en ninguno de los dos: no hay lugar que distinga, no hay duda", () => {
    const resultados = [{ name: "Excursión Cataratas" }, { name: "Excursión Cataratas" }];
    assert.equal(dudaDeProductoLocal(resultados), null);
  });

  it("solo 1 resultado o lista vacía: no hay nada que comparar", () => {
    assert.equal(dudaDeProductoLocal([{ name: "Sheraton" }]), null);
    assert.equal(dudaDeProductoLocal([]), null);
    assert.equal(dudaDeProductoLocal(null), null);
  });

  it("mismo nombre, mismo subtitle en los dos, pero distinto operador: el subtitle manda (no llega a mirar el operador)", () => {
    const resultados = [
      { name: "Sheraton Iguazú", subtitle: "Puerto Iguazú", lastSale: { supplierName: "Ola Mayorista" } },
      { name: "Sheraton Iguazú", subtitle: "Puerto Iguazú", lastSale: { supplierName: "Delfos" } },
    ];
    assert.equal(dudaDeProductoLocal(resultados), null);
  });

  it("solo mira los primeros 2 resultados: un 3er resultado con el mismo nombre no cambia nada", () => {
    const resultados = [
      { name: "Sheraton Iguazú", subtitle: "Puerto Iguazú" },
      { name: "Sheraton Iguazú", subtitle: "Posadas" },
      { name: "Sheraton Iguazú", subtitle: "Corrientes" },
    ];
    assert.deepEqual(dudaDeProductoLocal(resultados), {
      field: "producto",
      question: "¿Sheraton Iguazú de Puerto Iguazú o el de Posadas?",
    });
  });
});

// ─── debeMostrarDuda (fix C-4 + C-6, review 2026-08-10) ───────────────────────────

describe("debeMostrarDuda", () => {
  const dudaDeProducto = { code: "productoAmbiguo", field: "producto", question: "¿Cuál de los dos?" };
  const dudaDePrecio = { code: "precioPorNoche", field: "precio", question: "¿Es por noche?" };

  it("duda de producto, no buscando, no descartada: se muestra", () => {
    assert.equal(debeMostrarDuda({ duda: dudaDeProducto, isSearching: false, dudaDescartada: false }), true);
  });

  it("duda de precio (no de producto): NUNCA se muestra en esta línea, aunque no esté descartada", () => {
    assert.equal(debeMostrarDuda({ duda: dudaDePrecio, isSearching: false, dudaDescartada: false }), false);
  });

  it("todavía buscando (isSearching=true): no se muestra aunque sea duda de producto", () => {
    assert.equal(debeMostrarDuda({ duda: dudaDeProducto, isSearching: true, dudaDescartada: false }), false);
  });

  it("fix C-6: duda descartada con Esc/blur: no se muestra aunque siga siendo de producto", () => {
    assert.equal(debeMostrarDuda({ duda: dudaDeProducto, isSearching: false, dudaDescartada: true }), false);
  });

  it("sin duda: no se muestra", () => {
    assert.equal(debeMostrarDuda({ duda: null, isSearching: false, dudaDescartada: false }), false);
  });

  it("fix #9: ya hay un producto vinculado (rateId seteado) — la duda queda obsoleta, no se muestra", () => {
    assert.equal(debeMostrarDuda({ duda: dudaDeProducto, isSearching: false, dudaDescartada: false, hayProductoVinculado: true }), false);
  });

  it("fix #9: sin producto vinculado (hayProductoVinculado ausente/false): se comporta como siempre", () => {
    assert.equal(debeMostrarDuda({ duda: dudaDeProducto, isSearching: false, dudaDescartada: false, hayProductoVinculado: false }), true);
  });
});
