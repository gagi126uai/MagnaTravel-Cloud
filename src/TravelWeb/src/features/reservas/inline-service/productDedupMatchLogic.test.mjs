import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  debeDispararDedupMatch,
  hayParecidoFuerte,
  esRespuestaUtilizable,
  mergearCandidatosDedup,
  resolverTextoDeCrear,
  resolverListaParaMostrar,
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
