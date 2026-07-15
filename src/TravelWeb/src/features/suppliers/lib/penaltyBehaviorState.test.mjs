import { test } from "node:test";
import assert from "node:assert/strict";
import {
  siguienteEstadoPenaltyBehavior,
  puedeGuardarConPenaltyBehavior,
} from "./penaltyBehaviorState.js";

test("éxito apaga cargandoPenaltyBehavior, limpia el error y aplica el valor nuevo", () => {
  const siguiente = siguienteEstadoPenaltyBehavior({ exito: true, selectValueNuevo: 1 });
  assert.deepEqual(siguiente, {
    cargandoPenaltyBehavior: false,
    errorCargaPenaltyBehavior: null,
    penaltyBehaviorSelect: 1,
  });
});

test("error deja cargandoPenaltyBehavior=true (bloquea el submit) y NO toca penaltyBehaviorSelect", () => {
  const siguiente = siguienteEstadoPenaltyBehavior({ exito: false, errorMessage: "Error de red" });
  assert.equal(siguiente.cargandoPenaltyBehavior, true);
  assert.equal(siguiente.errorCargaPenaltyBehavior, "Error de red");
  assert.equal(
    "penaltyBehaviorSelect" in siguiente,
    false,
    "el llamador NO debe recibir este campo en error — así nunca lo pisa por accidente"
  );
});

test("error sin errorMessage explícito cae al texto fijo", () => {
  const siguiente = siguienteEstadoPenaltyBehavior({ exito: false });
  assert.equal(
    siguiente.errorCargaPenaltyBehavior,
    "No se pudo cargar el comportamiento con multas de este operador. Reintentá antes de guardar."
  );
});

test("error con errorMessage vacío ('') también cae al texto fijo (falsy)", () => {
  const siguiente = siguienteEstadoPenaltyBehavior({ exito: false, errorMessage: "" });
  assert.equal(
    siguiente.errorCargaPenaltyBehavior,
    "No se pudo cargar el comportamiento con multas de este operador. Reintentá antes de guardar."
  );
});

test("puedeGuardarConPenaltyBehavior: false mientras está cargando (o trabado en error, mismo flag)", () => {
  assert.equal(puedeGuardarConPenaltyBehavior(true), false);
});

test("puedeGuardarConPenaltyBehavior: true una vez resuelto con éxito", () => {
  assert.equal(puedeGuardarConPenaltyBehavior(false), true);
});

test("simulación completa: cargar → error → reintentar → éxito", () => {
  let cargando = true;
  let error = null;
  let select = 0;

  const primerIntento = siguienteEstadoPenaltyBehavior({ exito: false, errorMessage: "Network error" });
  cargando = primerIntento.cargandoPenaltyBehavior;
  error = primerIntento.errorCargaPenaltyBehavior;

  assert.equal(cargando, true, "el submit sigue bloqueado tras el fallo");
  assert.ok(error, "hay un mensaje de error visible");
  assert.equal(select, 0, "el valor no se tocó");
  assert.equal(puedeGuardarConPenaltyBehavior(cargando), false);

  // El usuario aprieta "Reintentar" y esta vez el fetch trae "casi nunca cobra" (RarelyCharges=1).
  const reintento = siguienteEstadoPenaltyBehavior({ exito: true, selectValueNuevo: 1 });
  cargando = reintento.cargandoPenaltyBehavior;
  error = reintento.errorCargaPenaltyBehavior;
  select = reintento.penaltyBehaviorSelect;

  assert.equal(cargando, false, "ya se puede guardar");
  assert.equal(error, null);
  assert.equal(select, 1, "ahora sí se aplica el valor real (RarelyCharges)");
  assert.equal(puedeGuardarConPenaltyBehavior(cargando), true);
});
