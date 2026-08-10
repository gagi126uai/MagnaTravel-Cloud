import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
  esResultadoDeOtroTipo,
  particionarPorTipo,
  filtrarPorTipoActivo,
  debeAplicarSeleccionPendiente,
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
