import test from "node:test";
import assert from "node:assert/strict";
import { supplierBalanceLines } from "./supplierBalanceView.js";

test("listado proveedor separa ARS y USD y nunca usa CurrentBalance", () => {
  const lines = supplierBalanceLines({
    currentBalance: 1250,
    amountsVisible: true,
    balancesByCurrency: [
      { currency: "USD", balance: 250 },
      { currency: "ARS", balance: 1000 },
    ],
  });

  assert.deepEqual(lines, [
    { currency: "ARS", balance: 1000 },
    { currency: "USD", balance: 250 },
  ]);
});

test("listado proveedor no inventa una moneda desde el surrogate legacy", () => {
  assert.deepEqual(supplierBalanceLines({ currentBalance: 999, amountsVisible: true }), []);
});

test("listado proveedor no devuelve montos enmascarados", () => {
  assert.deepEqual(supplierBalanceLines({
    amountsVisible: false,
    balancesByCurrency: [{ currency: "ARS", balance: 1000 }],
  }), []);
});
