import { test } from "node:test";
import assert from "node:assert/strict";
import {
  FACTURACION_TAB_TODOS,
  FACTURACION_TAB_COMPROBANTES,
  FACTURACION_TAB_RECIBOS,
  puedeVerTabTodos,
  puedeVerTabComprobantes,
  puedeVerFuenteMultas,
  puedeVerFuenteNotasCredito,
  puedeVerTabRecibos,
  getAllowedFacturacionTabs,
  resolveInitialFacturacionTab,
} from "./facturacionTabs.js";

// Helper para simular hasPermission con un set de permisos concedidos.
function permisosDe(...concedidos) {
  return (permission) => concedidos.includes(permission);
}

test("puedeVerTabTodos: requiere cobranzas.view_all", () => {
  assert.equal(puedeVerTabTodos(permisosDe("cobranzas.view_all")), true);
  assert.equal(puedeVerTabTodos(permisosDe("cobranzas.invoice_annul")), false);
});

test("puedeVerTabComprobantes: OR de invoice_annul y view_all (fix F1)", () => {
  assert.equal(puedeVerTabComprobantes(permisosDe("cobranzas.invoice_annul")), true, "Vendedor con solo invoice_annul debe ver la solapa");
  assert.equal(puedeVerTabComprobantes(permisosDe("cobranzas.view_all")), true);
  assert.equal(puedeVerTabComprobantes(permisosDe("cobranzas.invoice_annul", "cobranzas.view_all")), true);
  assert.equal(puedeVerTabComprobantes(permisosDe("approvals.review")), false, "sin ninguno de los dos, no ve la solapa");
});

test("puedeVerFuenteMultas / puedeVerFuenteNotasCredito: cada fuente respeta SU propio permiso (evita 403)", () => {
  const soloInvoiceAnnul = permisosDe("cobranzas.invoice_annul");
  assert.equal(puedeVerFuenteMultas(soloInvoiceAnnul), true);
  assert.equal(puedeVerFuenteNotasCredito(soloInvoiceAnnul), false, "sin view_all, NO se debe fetchear NC (403 documentado)");

  const soloViewAll = permisosDe("cobranzas.view_all");
  assert.equal(puedeVerFuenteMultas(soloViewAll), false, "sin invoice_annul, NO se debe fetchear multas (403 documentado)");
  assert.equal(puedeVerFuenteNotasCredito(soloViewAll), true);
});

test("puedeVerTabRecibos: requiere approvals.review", () => {
  assert.equal(puedeVerTabRecibos(permisosDe("approvals.review")), true);
  assert.equal(puedeVerTabRecibos(permisosDe("cobranzas.view_all")), false);
});

test("getAllowedFacturacionTabs: Vendedor con SOLO invoice_annul ve 'Comprobantes por resolver' pero NO 'Todos' ni 'Recibos'", () => {
  const tabs = getAllowedFacturacionTabs(permisosDe("cobranzas.invoice_annul"));
  assert.deepEqual(tabs.map((t) => t.key), [FACTURACION_TAB_COMPROBANTES]);
});

test("getAllowedFacturacionTabs: revisor con SOLO approvals.review ve únicamente 'Recibos'", () => {
  const tabs = getAllowedFacturacionTabs(permisosDe("approvals.review"));
  assert.deepEqual(tabs.map((t) => t.key), [FACTURACION_TAB_RECIBOS]);
});

test("getAllowedFacturacionTabs: usuario con view_all + approvals.review ve las 3, en el orden fijo", () => {
  const tabs = getAllowedFacturacionTabs(permisosDe("cobranzas.view_all", "approvals.review"));
  assert.deepEqual(tabs.map((t) => t.key), [
    FACTURACION_TAB_TODOS,
    FACTURACION_TAB_COMPROBANTES,
    FACTURACION_TAB_RECIBOS,
  ]);
});

test("getAllowedFacturacionTabs: sin ningún permiso → lista vacía", () => {
  assert.deepEqual(getAllowedFacturacionTabs(permisosDe()), []);
});

test("resolveInitialFacturacionTab: sin ?tab=, arranca en la primera permitida según el orden fijo", () => {
  assert.equal(
    resolveInitialFacturacionTab(null, permisosDe("cobranzas.view_all", "approvals.review")),
    FACTURACION_TAB_TODOS
  );
  assert.equal(
    resolveInitialFacturacionTab(null, permisosDe("cobranzas.invoice_annul")),
    FACTURACION_TAB_COMPROBANTES
  );
  assert.equal(
    resolveInitialFacturacionTab(null, permisosDe("approvals.review")),
    FACTURACION_TAB_RECIBOS
  );
});

test("resolveInitialFacturacionTab: ?tab= válido y permitido, se respeta", () => {
  assert.equal(
    resolveInitialFacturacionTab(FACTURACION_TAB_RECIBOS, permisosDe("cobranzas.view_all", "approvals.review")),
    FACTURACION_TAB_RECIBOS
  );
});

test("resolveInitialFacturacionTab: ?tab= pedido sin permiso → cae con gracia a la primera permitida (nunca 403 fantasma)", () => {
  // Vendedor con solo invoice_annul pide ?tab=todos (no tiene view_all) → cae a comprobantes.
  assert.equal(
    resolveInitialFacturacionTab(FACTURACION_TAB_TODOS, permisosDe("cobranzas.invoice_annul")),
    FACTURACION_TAB_COMPROBANTES
  );
});

test("resolveInitialFacturacionTab: sin ningún permiso → null (resguardo; el guard de la ruta ya debería haber cortado antes)", () => {
  assert.equal(resolveInitialFacturacionTab(null, permisosDe()), null);
});
