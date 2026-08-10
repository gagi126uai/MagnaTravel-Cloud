import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  esResultadoDeOtroTipo,
  particionarPorTipo,
  filtrarPorTipoActivo,
  debeAplicarSeleccionPendiente,
  priorizarPorOperadorElegido,
} from "./crossTypeSearchLogic.js";

describe("esResultadoDeOtroTipo", () => {
  it("mismo tipo que la solapa activa: NO es de otro tipo", () => {
    assert.equal(esResultadoDeOtroTipo({ serviceType: "Hotel" }, "Hotel"), false);
  });

  it("tipo distinto: SÍ es de otro tipo", () => {
    assert.equal(esResultadoDeOtroTipo({ serviceType: "Traslado" }, "Hotel"), true);
  });

  it("resultado sin serviceType (dato faltante): se trata como del tipo activo", () => {
    assert.equal(esResultadoDeOtroTipo({}, "Hotel"), false);
    assert.equal(esResultadoDeOtroTipo(null, "Hotel"), false);
  });

  it("sin serviceType activo: nunca marca nada como de otro tipo", () => {
    assert.equal(esResultadoDeOtroTipo({ serviceType: "Hotel" }, ""), false);
    assert.equal(esResultadoDeOtroTipo({ serviceType: "Hotel" }, null), false);
  });
});

describe("particionarPorTipo (D9: partición dura)", () => {
  it("primero el tipo activo (en su orden), después el resto (en su orden)", () => {
    const resultados = [
      { ratePublicId: "r1", serviceType: "Hotel", name: "Sheraton Iguazú" },
      { ratePublicId: "r2", serviceType: "Traslado", name: "Sheraton – Aeropuerto" },
      { ratePublicId: "r3", serviceType: "Hotel", name: "Sheraton Buenos Aires" },
      { ratePublicId: "r4", serviceType: "Aereo", name: "AEP-IGR" },
    ];
    const resultado = particionarPorTipo(resultados, "Hotel");
    assert.deepEqual(
      resultado.map((r) => r.ratePublicId),
      ["r1", "r3", "r2", "r4"]
    );
  });

  it("ninguna fila del tipo activo: las de otro tipo quedan arriba porque son las únicas", () => {
    const resultados = [
      { ratePublicId: "r1", serviceType: "Traslado" },
      { ratePublicId: "r2", serviceType: "Aereo" },
    ];
    const resultado = particionarPorTipo(resultados, "Hotel");
    assert.deepEqual(resultado.map((r) => r.ratePublicId), ["r1", "r2"]);
  });

  it("todas del tipo activo: el orden no cambia", () => {
    const resultados = [
      { ratePublicId: "r1", serviceType: "Hotel" },
      { ratePublicId: "r2", serviceType: "Hotel" },
    ];
    assert.deepEqual(particionarPorTipo(resultados, "Hotel"), resultados);
  });

  it("lista vacía/null no revienta", () => {
    assert.deepEqual(particionarPorTipo([], "Hotel"), []);
    assert.deepEqual(particionarPorTipo(null, "Hotel"), []);
  });

  it("fix #1: con supplierIdElegido, prioriza DENTRO de cada bloque de tipo sin romper la partición D9", () => {
    const resultados = [
      { ratePublicId: "r1", serviceType: "Hotel", lastSale: { supplierPublicId: "sup-ola" } },
      { ratePublicId: "r2", serviceType: "Hotel", lastSale: { supplierPublicId: "sup-delfos" } },
      { ratePublicId: "r3", serviceType: "Traslado", lastSale: { supplierPublicId: "sup-delfos" } },
      { ratePublicId: "r4", serviceType: "Traslado", lastSale: { supplierPublicId: "sup-ola" } },
    ];
    const resultado = particionarPorTipo(resultados, "Hotel", "sup-delfos");
    // Bloque Hotel primero (D9 intacto): r2 (delfos) antes que r1 (otro operador).
    // Bloque Traslado después: r3 (delfos) antes que r4 (otro operador) — pero SIGUE
    // abajo del bloque Hotel entero, nunca se cuela arriba por tener el operador.
    assert.deepEqual(resultado.map((r) => r.ratePublicId), ["r2", "r1", "r3", "r4"]);
  });

  it("sin supplierIdElegido: el orden es igual que antes (sin reordenar por operador)", () => {
    const resultados = [
      { ratePublicId: "r1", serviceType: "Hotel", lastSale: { supplierPublicId: "sup-ola" } },
      { ratePublicId: "r2", serviceType: "Hotel", lastSale: { supplierPublicId: "sup-delfos" } },
    ];
    assert.deepEqual(particionarPorTipo(resultados, "Hotel").map((r) => r.ratePublicId), ["r1", "r2"]);
  });
});

describe("priorizarPorOperadorElegido (fix #1, auditoría 2026-08-10)", () => {
  it("pone primero las filas con ESE operador, sin filtrar las demás", () => {
    const resultados = [
      { ratePublicId: "r1", lastSale: { supplierPublicId: "sup-ola" } },
      { ratePublicId: "r2", lastSale: { supplierPublicId: "sup-delfos" } },
      { ratePublicId: "r3", lastSale: { supplierPublicId: "sup-delfos" } },
    ];
    const resultado = priorizarPorOperadorElegido(resultados, "sup-delfos");
    assert.deepEqual(resultado.map((r) => r.ratePublicId), ["r2", "r3", "r1"]);
  });

  it("preserva el orden relativo DENTRO de cada uno de los dos grupos", () => {
    const resultados = [
      { ratePublicId: "a", lastSale: { supplierPublicId: "otro" } },
      { ratePublicId: "b", lastSale: { supplierPublicId: "sup-delfos" } },
      { ratePublicId: "c", lastSale: { supplierPublicId: "otro" } },
      { ratePublicId: "d", lastSale: { supplierPublicId: "sup-delfos" } },
    ];
    const resultado = priorizarPorOperadorElegido(resultados, "sup-delfos");
    assert.deepEqual(resultado.map((r) => r.ratePublicId), ["b", "d", "a", "c"]);
  });

  it("sin supplierIdElegido: no reordena nada (mismo array)", () => {
    const resultados = [{ ratePublicId: "r1", lastSale: { supplierPublicId: "sup-ola" } }];
    assert.deepEqual(priorizarPorOperadorElegido(resultados, null), resultados);
    assert.deepEqual(priorizarPorOperadorElegido(resultados, undefined), resultados);
    assert.deepEqual(priorizarPorOperadorElegido(resultados, ""), resultados);
  });

  it("resultado sin lastSale (rateFallback, nunca se vendió): no matchea, queda en el segundo grupo", () => {
    const resultados = [{ ratePublicId: "r1" }, { ratePublicId: "r2", lastSale: { supplierPublicId: "sup-delfos" } }];
    const resultado = priorizarPorOperadorElegido(resultados, "sup-delfos");
    assert.deepEqual(resultado.map((r) => r.ratePublicId), ["r2", "r1"]);
  });

  it("ningún resultado matchea: el orden original queda igual", () => {
    const resultados = [{ ratePublicId: "r1", lastSale: { supplierPublicId: "sup-a" } }, { ratePublicId: "r2", lastSale: { supplierPublicId: "sup-b" } }];
    assert.deepEqual(priorizarPorOperadorElegido(resultados, "sup-z").map((r) => r.ratePublicId), ["r1", "r2"]);
  });

  it("lista vacía/null no revienta", () => {
    assert.deepEqual(priorizarPorOperadorElegido([], "sup-1"), []);
    assert.deepEqual(priorizarPorOperadorElegido(null, "sup-1"), []);
  });
});

describe("filtrarPorTipoActivo (D6: editando, el buscador sigue limitado a su tipo)", () => {
  it("saca las filas de otro tipo, deja las del tipo activo", () => {
    const resultados = [
      { ratePublicId: "r1", serviceType: "Hotel" },
      { ratePublicId: "r2", serviceType: "Traslado" },
      { ratePublicId: "r3", serviceType: "Hotel" },
    ];
    const resultado = filtrarPorTipoActivo(resultados, "Hotel");
    assert.deepEqual(resultado.map((r) => r.ratePublicId), ["r1", "r3"]);
  });

  it("todas de otro tipo: queda vacío (nada se ofrece para saltar durante la edición)", () => {
    const resultados = [{ ratePublicId: "r1", serviceType: "Traslado" }];
    assert.deepEqual(filtrarPorTipoActivo(resultados, "Hotel"), []);
  });

  it("lista vacía/null no revienta", () => {
    assert.deepEqual(filtrarPorTipoActivo([], "Hotel"), []);
    assert.deepEqual(filtrarPorTipoActivo(null, "Hotel"), []);
  });
});

describe("debeAplicarSeleccionPendiente (D3/D7 + idempotencia StrictMode)", () => {
  it("sin pendiente: no hay nada que aplicar", () => {
    assert.equal(
      debeAplicarSeleccionPendiente({ seleccionPendiente: null, serviceType: "Hotel", ultimaAplicada: null }),
      false
    );
  });

  it("pendiente de OTRO tipo que el de este formulario: no le corresponde", () => {
    const pendiente = { serviceType: "Traslado", result: {} };
    assert.equal(
      debeAplicarSeleccionPendiente({ seleccionPendiente: pendiente, serviceType: "Hotel", ultimaAplicada: null }),
      false
    );
  });

  it("pendiente del tipo de este formulario y todavía no aplicada: sí hay que aplicarla", () => {
    const pendiente = { serviceType: "Hotel", result: {} };
    assert.equal(
      debeAplicarSeleccionPendiente({ seleccionPendiente: pendiente, serviceType: "Hotel", ultimaAplicada: null }),
      true
    );
  });

  it("la MISMA pendiente ya fue aplicada (idempotencia, ej. doble efecto de StrictMode): no se vuelve a aplicar", () => {
    const pendiente = { serviceType: "Hotel", result: {} };
    assert.equal(
      debeAplicarSeleccionPendiente({ seleccionPendiente: pendiente, serviceType: "Hotel", ultimaAplicada: pendiente }),
      false
    );
  });

  it("una pendiente NUEVA después de haber consumido una vieja: sí se aplica de nuevo", () => {
    const vieja = { serviceType: "Hotel", result: { ratePublicId: "r1" } };
    const nueva = { serviceType: "Hotel", result: { ratePublicId: "r2" } };
    assert.equal(
      debeAplicarSeleccionPendiente({ seleccionPendiente: nueva, serviceType: "Hotel", ultimaAplicada: vieja }),
      true
    );
  });
});
