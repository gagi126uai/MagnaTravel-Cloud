import { test } from "node:test";
import assert from "node:assert/strict";
import { parseBasicFormatting } from "./basicFormatting.js";

test("parseBasicFormatting conserva texto y reconoce negrita e italica", () => {
  assert.deepEqual(parseBasicFormatting("Cambio de *antes* a **despues**"), [
    { style: "text", text: "Cambio de " },
    { style: "italic", text: "antes" },
    { style: "text", text: " a " },
    { style: "bold", text: "despues" },
  ]);
});

test("parseBasicFormatting mantiene HTML como texto, sin transformarlo", () => {
  const payload = '<img src=x onerror="alert(1)">';
  assert.deepEqual(parseBasicFormatting(payload), [{ style: "text", text: payload }]);
});

test("parseBasicFormatting tolera null y marcadores incompletos", () => {
  assert.deepEqual(parseBasicFormatting(null), []);
  assert.deepEqual(parseBasicFormatting("*sin cerrar"), [
    { style: "text", text: "*sin cerrar" },
  ]);
});
