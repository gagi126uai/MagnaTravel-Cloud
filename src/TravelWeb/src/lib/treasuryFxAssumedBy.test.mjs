import { test } from "node:test";
import assert from "node:assert/strict";
import {
  TREASURY_FX_ASSUMED_BY,
  OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA,
  OPCIONES_ASUME_AJUSTE_DOLAR_OPERADOR,
  HEREDA_CONFIGURACION_GENERAL,
  valorSelectDesdeOverride,
  overrideDesdeValorSelect,
} from "./treasuryFxAssumedBy.js";

test("TREASURY_FX_ASSUMED_BY: Client=0, Agency=1 (verificado en TreasuryFxAssumedBy.cs)", () => {
  assert.equal(TREASURY_FX_ASSUMED_BY.Client, 0);
  assert.equal(TREASURY_FX_ASSUMED_BY.Agency, 1);
});

test("OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA: 2 opciones, 'El cliente' primero (default)", () => {
  assert.equal(OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA.length, 2);
  assert.equal(OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA[0].label, "El cliente");
  assert.equal(OPCIONES_ASUME_AJUSTE_DOLAR_AGENCIA[0].value, TREASURY_FX_ASSUMED_BY.Client);
});

test("OPCIONES_ASUME_AJUSTE_DOLAR_OPERADOR: 3 opciones, 'Como la configuración general' primero (default invisible)", () => {
  assert.equal(OPCIONES_ASUME_AJUSTE_DOLAR_OPERADOR.length, 3);
  assert.equal(OPCIONES_ASUME_AJUSTE_DOLAR_OPERADOR[0].value, HEREDA_CONFIGURACION_GENERAL);
});

test("valorSelectDesdeOverride: null/undefined → hereda la configuración general", () => {
  assert.equal(valorSelectDesdeOverride(null), HEREDA_CONFIGURACION_GENERAL);
  assert.equal(valorSelectDesdeOverride(undefined), HEREDA_CONFIGURACION_GENERAL);
});

test("valorSelectDesdeOverride: 0 y 1 mapean a Client/Agency explícitos", () => {
  assert.equal(valorSelectDesdeOverride(0), TREASURY_FX_ASSUMED_BY.Client);
  assert.equal(valorSelectDesdeOverride(1), TREASURY_FX_ASSUMED_BY.Agency);
});

test("overrideDesdeValorSelect: 'heredaConfiguracionGeneral' → null (sin excepción)", () => {
  assert.equal(overrideDesdeValorSelect(HEREDA_CONFIGURACION_GENERAL), null);
});

test("overrideDesdeValorSelect: valor explícito → el INT correspondiente", () => {
  assert.equal(overrideDesdeValorSelect(TREASURY_FX_ASSUMED_BY.Agency), 1);
  assert.equal(overrideDesdeValorSelect(TREASURY_FX_ASSUMED_BY.Client), 0);
});

test("round-trip: valorSelectDesdeOverride + overrideDesdeValorSelect son inversas", () => {
  for (const original of [null, 0, 1]) {
    const select = valorSelectDesdeOverride(original);
    assert.equal(overrideDesdeValorSelect(select), original);
  }
});
