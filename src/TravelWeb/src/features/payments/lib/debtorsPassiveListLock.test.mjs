/**
 * Candado (spec 2026-08-06 §4.2/§4.3, P16=A): las dos listas de deudores de Cobranzas
 * ("Viajan pronto y deben" y "Deuda por cliente") son bandejas PASIVAS — la fila entera
 * es un link a la ficha (reserva o cliente), nunca un botón de acción ("cobrar",
 * "recordar", etc). Si algún día alguien agrega un botón acá, este test lo frena.
 *
 * Lee el código fuente real de las dos páginas (no una réplica) y verifica:
 *   1. Ningún <button> en ninguna de las dos.
 *   2. Cada fila tiene data-testid="debtor-row" + data-past-due (para que QA pueda
 *      automatizar sin depender de CSS/XPath frágil).
 *
 * Cómo correr: node --test src/features/payments/lib/debtorsPassiveListLock.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const AQUI = dirname(fileURLToPath(import.meta.url));

const PAGINAS = [
  { nombre: "Viajan pronto y deben", ruta: resolve(AQUI, "../pages/PaymentsDebtorsByDeparturePage.jsx") },
  { nombre: "Deuda por cliente", ruta: resolve(AQUI, "../pages/PaymentsDebtorsByCustomerPage.jsx") },
];

for (const { nombre, ruta } of PAGINAS) {
  const contenido = readFileSync(ruta, "utf-8");

  test(`"${nombre}": no tiene ningún <button> (lista pasiva, sin acciones por fila)`, () => {
    assert.doesNotMatch(contenido, /<button/, `Se encontró un <button> en ${nombre} — las listas de deudores son pasivas.`);
  });

  test(`"${nombre}": cada fila trae data-testid="debtor-row"`, () => {
    assert.match(contenido, /data-testid="debtor-row"/);
  });

  test(`"${nombre}": cada fila trae data-past-due (para que QA distinga vencidas sin CSS frágil)`, () => {
    assert.match(contenido, /data-past-due=/);
  });
}
