/**
 * Candado (mismo patrón que createInvoiceModalDeprecationLock.test.mjs —
 * features/payments/lib/): este repo no tiene jsdom ni @testing-library, así que un
 * "test de componente" para JSX puro (sin lógica extraíble) se hace leyendo el código
 * fuente y verificando el CONTRATO, no renderizando.
 *
 * Bug bloqueante (revisor funcional, segunda vuelta): `ResolvedProductRow` (el renglón
 * "Producto *" del Momento 3, §3.3) NO puede volver a ser editable — si el vendedor
 * corregía el texto ahí, `rateId` seguía apuntando al producto que reconoció el motor,
 * pero el nombre que viajaba al guardar era el que el vendedor tipeó en ese renglón:
 * "identidad fantasma" que contamina la memoria del tarifario (que se guarda POR
 * rateId) con un nombre ajeno, sin ningún error visible en pantalla.
 *
 * Este candado frena que alguien le vuelva a agregar un `onChange` — a ResolvedProductRow.jsx
 * directamente, O a cualquiera de los 5 *InlineForm.jsx que lo usan — como "mejora rápida".
 *
 * Cómo correr: node --test src/features/reservas/inline-service/resolvedProductRowReadOnlyLock.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const AQUI = dirname(fileURLToPath(import.meta.url));
const ARCHIVO_COMPONENTE = join(AQUI, "ResolvedProductRow.jsx");

const FORMS_QUE_LO_USAN = [
  "HotelInlineForm.jsx",
  "FlightInlineForm.jsx",
  "TransferInlineForm.jsx",
  "PackageInlineForm.jsx",
  "AssistanceInlineForm.jsx",
];

test("ResolvedProductRow.jsx: el <input> es readOnly, no acepta onChange como prop", () => {
  const codigo = readFileSync(ARCHIVO_COMPONENTE, "utf-8");

  // El componente no declara ningún parámetro llamado onChange en su firma.
  const firmaSinOnChange = !/function ResolvedProductRow\([^)]*\bonChange\b/.test(codigo);
  assert.equal(firmaSinOnChange, true, "ResolvedProductRow no debería recibir un prop onChange");

  // El <input> tiene el atributo readOnly (solo lectura real, no un truco de CSS).
  assert.match(codigo, /readOnly/, "El <input> del renglón tiene que ser readOnly");

  // Ningún atributo onChange en TODO el archivo (ni en el <input> ni en ningún otro tag).
  assert.doesNotMatch(codigo, /onChange\s*=/, "ResolvedProductRow.jsx no puede tener ningún onChange");
});

test("ninguno de los 5 *InlineForm.jsx le pasa onChange a <ResolvedProductRow>", () => {
  const AQUI_INLINE = AQUI;
  const archivosConOnChange = [];

  for (const nombreForm of FORMS_QUE_LO_USAN) {
    const ruta = join(AQUI_INLINE, nombreForm);
    const codigo = readFileSync(ruta, "utf-8");

    // Aislamos el bloque <ResolvedProductRow ... /> (puede tener varias props en varias
    // líneas) y revisamos que NINGUNA de esas líneas sea un onChange.
    const match = codigo.match(/<ResolvedProductRow\b([\s\S]*?)\/>/);
    assert.ok(match, `${nombreForm}: no se encontró el uso de <ResolvedProductRow />`);

    const bloqueDeProps = match[1];
    if (/onChange\s*=/.test(bloqueDeProps)) {
      archivosConOnChange.push(nombreForm);
    }
  }

  assert.deepEqual(
    archivosConOnChange,
    [],
    `Estos forms le pasan onChange a ResolvedProductRow (identidad fantasma): ${archivosConOnChange.join(", ")}`
  );
});
