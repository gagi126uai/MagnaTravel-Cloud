import { describe, it } from "node:test";
import assert from "node:assert/strict";
import { buildFreeTextMemoryOptions } from "./freeTextWithMemoryLogic.js";

describe("buildFreeTextMemoryOptions", () => {
  it("sin texto tipeado: muestra las sugerencias conocidas, sin 'usar tal cual'", () => {
    const resultado = buildFreeTextMemoryOptions("", ["Superior", "Vista al mar"]);
    assert.deepEqual(resultado, { suggestions: ["Superior", "Vista al mar"], showUseAsIsOption: false });
  });

  it("texto nuevo (no está en las sugerencias): ofrece 'usar tal cual' al final", () => {
    const resultado = buildFreeTextMemoryOptions("sup", ["Superior", "Vista al mar"]);
    assert.equal(resultado.showUseAsIsOption, true);
    assert.deepEqual(resultado.suggestions, ["Superior", "Vista al mar"]);
  });

  it("texto que ya coincide EXACTO con una sugerencia (sin mayúsculas): no ofrece 'usar tal cual'", () => {
    const resultado = buildFreeTextMemoryOptions("superior", ["Superior", "Vista al mar"]);
    assert.equal(resultado.showUseAsIsOption, false);
  });

  it("sin sugerencias todavía (primera vez que se usa el campo): igual deja escribir libre", () => {
    const resultado = buildFreeTextMemoryOptions("Superior", []);
    assert.deepEqual(resultado, { suggestions: [], showUseAsIsOption: true });
  });
});
