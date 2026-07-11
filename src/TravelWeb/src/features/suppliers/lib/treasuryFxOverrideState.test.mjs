import { test } from "node:test";
import assert from "node:assert/strict";
import {
  siguienteEstadoTreasuryFxOverride,
  puedeGuardarConTreasuryFxOverride,
} from "./treasuryFxOverrideState.js";

test("FIX F2: éxito apaga cargandoOverride, limpia el error y aplica el valor nuevo", () => {
  const siguiente = siguienteEstadoTreasuryFxOverride({ exito: true, selectValueNuevo: 1 });
  assert.deepEqual(siguiente, {
    cargandoOverride: false,
    errorCargaOverride: null,
    treasuryFxOverrideSelect: 1,
  });
});

test("FIX F2: error deja cargandoOverride=true (bloquea el submit) y NO toca treasuryFxOverrideSelect", () => {
  const siguiente = siguienteEstadoTreasuryFxOverride({ exito: false, errorMessage: "Error de red" });
  assert.equal(siguiente.cargandoOverride, true);
  assert.equal(siguiente.errorCargaOverride, "Error de red");
  assert.equal(
    "treasuryFxOverrideSelect" in siguiente,
    false,
    "el llamador NO debe recibir este campo en error — así nunca lo pisa por accidente"
  );
});

test("FIX F2: error sin errorMessage explícito cae al texto fijo de la spec", () => {
  const siguiente = siguienteEstadoTreasuryFxOverride({ exito: false });
  assert.equal(
    siguiente.errorCargaOverride,
    "No se pudo cargar la configuración del ajuste por el dólar. Reintentá antes de guardar."
  );
});

test("FIX F2: error con errorMessage vacío ('') también cae al texto fijo (falsy)", () => {
  const siguiente = siguienteEstadoTreasuryFxOverride({ exito: false, errorMessage: "" });
  assert.equal(
    siguiente.errorCargaOverride,
    "No se pudo cargar la configuración del ajuste por el dólar. Reintentá antes de guardar."
  );
});

test("puedeGuardarConTreasuryFxOverride: false mientras está cargando (o trabado en error, mismo flag)", () => {
  assert.equal(puedeGuardarConTreasuryFxOverride(true), false);
});

test("puedeGuardarConTreasuryFxOverride: true una vez resuelto con éxito", () => {
  assert.equal(puedeGuardarConTreasuryFxOverride(false), true);
});

test("simulación completa del camino de fetch fallido: cargar → error → reintentar → éxito", () => {
  // Simula la secuencia real: 1) falla, 2) el usuario reintenta y esta vez funciona.
  let cargandoOverride = true;
  let errorCargaOverride = null;
  let treasuryFxOverrideSelect = "heredaConfiguracionGeneral";

  // 1) Falla el primer intento.
  const primerIntento = siguienteEstadoTreasuryFxOverride({ exito: false, errorMessage: "Network error" });
  cargandoOverride = primerIntento.cargandoOverride;
  errorCargaOverride = primerIntento.errorCargaOverride;
  // treasuryFxOverrideSelect NO se toca (el componente real tampoco llamaría al setter).

  assert.equal(cargandoOverride, true, "el submit sigue bloqueado tras el fallo");
  assert.ok(errorCargaOverride, "hay un mensaje de error visible");
  assert.equal(treasuryFxOverrideSelect, "heredaConfiguracionGeneral", "el valor no se tocó");
  assert.equal(puedeGuardarConTreasuryFxOverride(cargandoOverride), false);

  // 2) El usuario aprieta "Reintentar" y esta vez el fetch trae un override real (Agency=1).
  const reintento = siguienteEstadoTreasuryFxOverride({ exito: true, selectValueNuevo: 1 });
  cargandoOverride = reintento.cargandoOverride;
  errorCargaOverride = reintento.errorCargaOverride;
  treasuryFxOverrideSelect = reintento.treasuryFxOverrideSelect;

  assert.equal(cargandoOverride, false, "ya se puede guardar");
  assert.equal(errorCargaOverride, null);
  assert.equal(treasuryFxOverrideSelect, 1, "ahora sí se aplica el valor real (Agency)");
  assert.equal(puedeGuardarConTreasuryFxOverride(cargandoOverride), true);
});
