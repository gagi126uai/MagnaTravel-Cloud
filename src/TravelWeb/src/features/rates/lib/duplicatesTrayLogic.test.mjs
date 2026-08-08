import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { quitarCandidatoResuelto, puedeDeshacerse, marcarComoDeshecha } from "./duplicatesTrayLogic.js";

const GROUPS_EJEMPLO = [
  {
    survivorPublicId: "sheraton",
    candidates: [{ ratePublicId: "cand-1" }, { ratePublicId: "cand-2" }],
  },
  {
    survivorPublicId: "maitei",
    candidates: [{ ratePublicId: "cand-3" }],
  },
];

describe("quitarCandidatoResuelto", () => {
  it("saca solo el candidato resuelto, deja el resto del grupo intacto", () => {
    const resultado = quitarCandidatoResuelto(GROUPS_EJEMPLO, "sheraton", "cand-1");
    const grupoSheraton = resultado.find((group) => group.survivorPublicId === "sheraton");
    assert.deepEqual(grupoSheraton.candidates, [{ ratePublicId: "cand-2" }]);
    // El otro grupo no se toca.
    const grupoMaitei = resultado.find((group) => group.survivorPublicId === "maitei");
    assert.equal(grupoMaitei.candidates.length, 1);
  });

  it("si el grupo se queda sin candidatos, el grupo entero desaparece de la bandeja", () => {
    const resultado = quitarCandidatoResuelto(GROUPS_EJEMPLO, "maitei", "cand-3");
    assert.equal(resultado.some((group) => group.survivorPublicId === "maitei"), false);
    // El otro grupo sigue estando.
    assert.equal(resultado.some((group) => group.survivorPublicId === "sheraton"), true);
  });
});

describe("puedeDeshacerse", () => {
  it("refleja canUndo tal cual lo manda el motor", () => {
    assert.equal(puedeDeshacerse({ canUndo: true }), true);
    assert.equal(puedeDeshacerse({ canUndo: false }), false);
    assert.equal(puedeDeshacerse(null), false);
  });
});

describe("marcarComoDeshecha", () => {
  it("apaga canUndo de la línea deshecha, sin borrar la fila", () => {
    const actions = [
      { publicId: "a1", canUndo: true },
      { publicId: "a2", canUndo: true },
    ];
    const resultado = marcarComoDeshecha(actions, "a1");
    assert.equal(resultado.find((a) => a.publicId === "a1").canUndo, false);
    // La otra línea sigue con Deshacer disponible.
    assert.equal(resultado.find((a) => a.publicId === "a2").canUndo, true);
    // Sigue habiendo 2 líneas (nada se borra).
    assert.equal(resultado.length, 2);
  });
});
