/**
 * Candado (mismo patrón que paymentsLegacyRoutesRedirect.test.mjs): CreateInvoiceModal
 * se borró el 2026-08-06 porque "Pendientes de facturar" dejó de abrir una ventana para
 * facturar (spec §4.4, P14=A) — emitir factura vive en línea en la ficha de la reserva
 * (EmitirFacturaInline). Si alguien lo vuelve a importar como una "solución rápida",
 * este test lo frena.
 *
 * Recorre TODO src/ (no solo App.jsx) buscando cualquier `import ... from ".../CreateInvoiceModal"`.
 *
 * Cómo correr: node --test src/features/payments/lib/createInvoiceModalDeprecationLock.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const AQUI = dirname(fileURLToPath(import.meta.url));
const RAIZ_SRC = resolve(AQUI, "../../../");
const ARCHIVO_BORRADO = resolve(RAIZ_SRC, "components/CreateInvoiceModal.jsx");
const PATRON_IMPORT = /import\s+\w+\s+from\s+["'][^"']*CreateInvoiceModal["']/;

// Recorre src/ recursivamente juntando cada archivo .jsx/.js (se salta node_modules
// por las dudas, aunque no debería aparecer dentro de src/).
function listarArchivosFuente(directorio) {
  const encontrados = [];
  for (const entrada of readdirSync(directorio)) {
    if (entrada === "node_modules") continue;
    const rutaCompleta = join(directorio, entrada);
    const info = statSync(rutaCompleta);
    if (info.isDirectory()) {
      encontrados.push(...listarArchivosFuente(rutaCompleta));
    } else if (/\.(jsx|js)$/.test(entrada) && !entrada.endsWith(".test.mjs")) {
      encontrados.push(rutaCompleta);
    }
  }
  return encontrados;
}

test("components/CreateInvoiceModal.jsx no existe más (borrado 2026-08-06)", () => {
  assert.equal(existsSync(ARCHIVO_BORRADO), false, "CreateInvoiceModal.jsx volvió a aparecer en components/");
});

test("ningún archivo de src/ importa CreateInvoiceModal", () => {
  const archivosQueImportan = [];
  for (const archivo of listarArchivosFuente(RAIZ_SRC)) {
    // Este mismo archivo de test menciona "CreateInvoiceModal" en comentarios — se excluye
    // explícitamente por nombre para no auto-detectarse.
    if (archivo.endsWith("createInvoiceModalDeprecationLock.test.mjs")) continue;
    const contenido = readFileSync(archivo, "utf-8");
    if (PATRON_IMPORT.test(contenido)) {
      archivosQueImportan.push(archivo);
    }
  }
  assert.deepEqual(archivosQueImportan, [], `Estos archivos volvieron a importar CreateInvoiceModal: ${archivosQueImportan.join(", ")}`);
});
