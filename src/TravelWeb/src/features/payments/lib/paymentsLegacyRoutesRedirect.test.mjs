/**
 * Candado (Tanda B0, poda de Cobranza/Facturación): las pantallas viejas
 * PaymentsCollectionsPage / PaymentsInvoicingPage / PaymentsHistoryPage ya no
 * existen. Si alguien tenía un bookmark o link externo a esas rutas, App.jsx
 * debe seguir mandándolo a la pestaña viva equivalente (nunca a un 404).
 *
 * Este test lee el App.jsx real (no una réplica) y verifica que las 3 rutas
 * viejas sigan redirigiendo. Si algún día se borran del todo esas rutas
 * (deprecación total), este test hay que borrarlo o reescribirlo a propósito,
 * no dejar que falle en silencio.
 *
 * Cómo correr: node --test src/features/payments/lib/paymentsLegacyRoutesRedirect.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const AQUI = dirname(fileURLToPath(import.meta.url));
const RUTA_APP_JSX = resolve(AQUI, "../../../App.jsx");
const contenidoAppJsx = readFileSync(RUTA_APP_JSX, "utf-8");

// Mapa ruta-vieja → ruta-viva esperado (mismo que quedó escrito en App.jsx).
const REDIRECTS_ESPERADOS = [
  { rutaVieja: "collections", rutaViva: "/payments/reservas" },
  { rutaVieja: "invoicing", rutaViva: "/payments/pending" },
  { rutaVieja: "history", rutaViva: "/payments/movements" },
];

for (const { rutaVieja, rutaViva } of REDIRECTS_ESPERADOS) {
  test(`/payments/${rutaVieja} (ruta vieja) redirige a ${rutaViva} (pestaña viva)`, () => {
    // Regex tolerante a espacios/comillas, pero exige que el mismo <Route> tenga
    // el path viejo Y el Navigate hacia la pantalla viva, no dos líneas sueltas.
    const patron = new RegExp(
      `<Route\\s+path=["']${rutaVieja}["']\\s+element=\\{<Navigate\\s+to=["']${rutaViva.replace("/", "\\/")}["']`
    );
    assert.match(
      contenidoAppJsx,
      patron,
      `No se encontró el redirect de "${rutaVieja}" hacia "${rutaViva}" en App.jsx`
    );
  });
}

test("las 3 páginas viejas ya no se importan en App.jsx (quedaron borradas, no solo sin rutear)", () => {
  const importsMuertos = [
    "PaymentsCollectionsPage",
    "PaymentsInvoicingPage",
    "PaymentsHistoryPage",
  ];
  const encontrados = importsMuertos.filter((nombre) =>
    new RegExp(`import\\s+${nombre}\\s+from`).test(contenidoAppJsx)
  );
  assert.deepEqual(encontrados, [], `App.jsx todavía importa páginas borradas: ${encontrados.join(", ")}`);
});
