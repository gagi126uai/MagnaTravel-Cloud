/**
 * Guardián de codificación (2026-07-20): si algún archivo fuente queda con
 * texto doblemente codificado (mojibake: "AÃ©reo" en vez de "Aéreo"), este
 * test FALLA y el CI frena antes de que llegue a la pantalla del usuario.
 *
 * Cubre el frontend entero y los proyectos backend (mensajes en español que
 * viajan a la UI). Corre con Node puro: node --test src/lib/noMojibake.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const AQUI = dirname(fileURLToPath(import.meta.url));
const RAICES = [
  resolve(AQUI, ".."), // src/TravelWeb/src entero
  resolve(AQUI, "../../../TravelApi"),
  resolve(AQUI, "../../../TravelApi.Domain"),
  resolve(AQUI, "../../../TravelApi.Application"),
  resolve(AQUI, "../../../TravelApi.Infrastructure"),
];
const EXTENSIONES = [".js", ".jsx", ".mjs", ".cs"];
const IGNORAR = new Set(["node_modules", "bin", "obj", "dist", "Migrations"]);

// Secuencias que solo aparecen cuando UTF-8 se leyó como Latin-1/cp1252.
// "Ã" seguido de vocal acentuada rota, "â€" (comillas/guiones rotos), "Â" antes
// de ¿/¡/·. Ningún texto legítimo en español las contiene.
const MOJIBAKE = /Ã[¡©­³ºÁ‰"'“”±¼]|â€|Â[¿¡·°]/;

function* archivos(dir) {
  let entradas;
  try { entradas = readdirSync(dir); } catch { return; }
  for (const nombre of entradas) {
    if (IGNORAR.has(nombre)) continue;
    const ruta = join(dir, nombre);
    let info;
    try { info = statSync(ruta); } catch { continue; }
    if (info.isDirectory()) yield* archivos(ruta);
    else if (EXTENSIONES.some((e) => nombre.endsWith(e))) yield ruta;
  }
}

test("ningún archivo fuente tiene texto con codificación rota (mojibake)", () => {
  const rotos = [];
  for (const raiz of RAICES) {
    for (const ruta of archivos(raiz)) {
      // Este mismo archivo contiene las secuencias como patrón: se excluye.
      if (ruta.endsWith("noMojibake.test.mjs")) continue;
      const contenido = readFileSync(ruta, "utf-8");
      const m = contenido.match(MOJIBAKE);
      if (m) rotos.push(`${ruta} → "...${contenido.slice(Math.max(0, m.index - 20), m.index + 20)}..."`);
    }
  }
  assert.deepEqual(rotos, [], `Archivos con texto roto:\n${rotos.join("\n")}`);
});
