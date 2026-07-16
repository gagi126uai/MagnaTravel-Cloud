/**
 * Tests para la logica de construccion de payloads de penalidad.
 *
 * Testean buildPenaltyClassificationPayload como funcion pura, sin DOM ni React.
 * Corren con: node --test src/features/cancellations/lib/*.test.mjs
 *
 * Por que son importantes estos tests:
 *   Los payloads se mandan directamente al backend de AFIP/ARCA (irreversible una vez
 *   emitida la ND). Un enum incorrecto (ej. mandar "Confirmed" en lugar de 1) causa
 *   400 del backend y la emision fallida. Estos tests protegen ese mapeo.
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
  buildPenaltyClassificationPayload,
  CONCEPT_KIND,
  PENALTY_STATUS,
  DEBIT_NOTE_PURPOSE,
} from "./penaltyPayload.js";

// ============================================================================
// Seccion 1: buildPenaltyClassificationPayload — los 4 casos de la UI
// ============================================================================

test('buildPenaltyClassificationPayload("none"): sin penalidad → pass-through int 0, sin ND', () => {
  const result = buildPenaltyClassificationPayload("none", "", "", "");
  assert.equal(result.penaltyConceptKind, CONCEPT_KIND.OperatorPenaltyPassThrough);
  assert.equal(result.penaltyConceptKind, 0, "Int 0 = OperatorPenaltyPassThrough");
  assert.equal(result.penaltyStatus, null);
  assert.equal(result.debitNotePurpose, null);
  assert.equal(result.confirmedPenaltyAmount, null);
});

test('buildPenaltyClassificationPayload("operator_pass_through"): DEFAULT → pass-through int 0, sin ND', () => {
  const result = buildPenaltyClassificationPayload("operator_pass_through", "", "", "");
  assert.equal(result.penaltyConceptKind, CONCEPT_KIND.OperatorPenaltyPassThrough);
  assert.equal(result.penaltyConceptKind, 0);
  assert.equal(result.penaltyStatus, null);
  assert.equal(result.debitNotePurpose, null);
  assert.equal(result.confirmedPenaltyAmount, null);
});

test('buildPenaltyClassificationPayload: default (opcion desconocida) → identico a operator_pass_through', () => {
  const result = buildPenaltyClassificationPayload("opcion_invalida", "", "", "");
  assert.equal(result.penaltyConceptKind, 0);
  assert.equal(result.penaltyStatus, null);
  assert.equal(result.debitNotePurpose, null);
  assert.equal(result.confirmedPenaltyAmount, null);
});

test('buildPenaltyClassificationPayload("insurance"): seguro → todos los campos de clasificacion null', () => {
  const result = buildPenaltyClassificationPayload("insurance", "", "", "");
  assert.equal(result.penaltyConceptKind, null);
  assert.equal(result.penaltyStatus, null);
  assert.equal(result.debitNotePurpose, null);
  assert.equal(result.confirmedPenaltyAmount, null);
});

// ─── Caso agency_charge + Estimated (CASO DOMINANTE del negocio) ─────────────

test('buildPenaltyClassificationPayload("agency_charge") + Estimated → penaltyStatus=0, sin monto, sin ND ahora', () => {
  // El agente no sabe aun el monto: deja Estimated. La ND se difiere a ADR-014.
  const result = buildPenaltyClassificationPayload(
    "agency_charge",
    "AgencyManagementFee",
    "Estimated",
    "500"
  );
  assert.equal(result.penaltyConceptKind, CONCEPT_KIND.AgencyManagementFee);
  assert.equal(result.penaltyConceptKind, 1, "Int 1 = AgencyManagementFee");
  assert.equal(result.penaltyStatus, PENALTY_STATUS.Estimated);
  assert.equal(result.penaltyStatus, 0, "Int 0 = Estimated");
  assert.equal(result.debitNotePurpose, DEBIT_NOTE_PURPOSE.PenaltyOrCancellationCharge);
  assert.equal(result.debitNotePurpose, 0, "Int 0 = PenaltyOrCancellationCharge");
  // IMPORTANTE: el monto NO se manda cuando es Estimated — el backend no emite ND ahora.
  assert.equal(result.confirmedPenaltyAmount, null, "Estimated → confirmedPenaltyAmount debe ser null");
});

test('buildPenaltyClassificationPayload("agency_charge") + Estimated + monto vacio → confirmedPenaltyAmount null', () => {
  // El agente deja el campo vacio (tambien Estimated → null).
  const result = buildPenaltyClassificationPayload("agency_charge", "AgencyManagementFee", "Estimated", "");
  assert.equal(result.confirmedPenaltyAmount, null);
  assert.equal(result.penaltyStatus, 0);
});

// ─── Caso agency_charge + Confirmed ──────────────────────────────────────────

test('buildPenaltyClassificationPayload("agency_charge") + Confirmed → penaltyStatus=1, monto enviado', () => {
  // El agente ya confirmo el monto con el operador: emite ND en el mismo paso.
  const result = buildPenaltyClassificationPayload(
    "agency_charge",
    "AgencyCancellationFee",
    "Confirmed",
    "1500"
  );
  assert.equal(result.penaltyConceptKind, CONCEPT_KIND.AgencyCancellationFee);
  assert.equal(result.penaltyConceptKind, 2, "Int 2 = AgencyCancellationFee");
  assert.equal(result.penaltyStatus, PENALTY_STATUS.Confirmed);
  assert.equal(result.penaltyStatus, 1, "Int 1 = Confirmed");
  assert.equal(result.debitNotePurpose, 0);
  assert.equal(result.confirmedPenaltyAmount, 1500);
});

test('buildPenaltyClassificationPayload("agency_charge") + Confirmed + monto cero → confirmedPenaltyAmount null', () => {
  // Si el monto es 0 o NaN, parseFloat devuelve 0 o NaN → || null da null.
  // El backend rechazaria un monto = 0 de todas formas; el frontend ya lo bloquea en UI.
  const result = buildPenaltyClassificationPayload("agency_charge", "AgencyManagementFee", "Confirmed", "0");
  assert.equal(result.confirmedPenaltyAmount, null);
});

test('buildPenaltyClassificationPayload("agency_charge") + Confirmed + monto decimal', () => {
  const result = buildPenaltyClassificationPayload("agency_charge", "AgencyManagementFee", "Confirmed", "123.45");
  assert.equal(result.confirmedPenaltyAmount, 123.45);
});

// ─── Verificar enums INT exactos (estos son los valores que van al backend ARCA) ──

test("CONCEPT_KIND: OperatorPenaltyPassThrough = 0 (verificado en CancellationConceptKind.cs)", () => {
  assert.equal(CONCEPT_KIND.OperatorPenaltyPassThrough, 0);
});

test("CONCEPT_KIND: AgencyManagementFee = 1 (verificado en CancellationConceptKind.cs)", () => {
  assert.equal(CONCEPT_KIND.AgencyManagementFee, 1);
});

test("CONCEPT_KIND: AgencyCancellationFee = 2 (verificado en CancellationConceptKind.cs)", () => {
  assert.equal(CONCEPT_KIND.AgencyCancellationFee, 2);
});

test("PENALTY_STATUS: Estimated = 0, Confirmed = 1 (verificado en PenaltyStatus.cs)", () => {
  assert.equal(PENALTY_STATUS.Estimated, 0);
  assert.equal(PENALTY_STATUS.Confirmed, 1);
});

test("DEBIT_NOTE_PURPOSE: PenaltyOrCancellationCharge = 0 (verificado en DebitNotePurpose.cs)", () => {
  assert.equal(DEBIT_NOTE_PURPOSE.PenaltyOrCancellationCharge, 0);
});

// ============================================================================
// Tanda B (2026-07-16): se elimino la Seccion 2 (buildSnapshotData) de este archivo,
// junto con la funcion que testeaba. Esa funcion armaba el "snapshot fiscal" adivinando
// datos (agencia/operador/cliente/TC) que el backend ahora resuelve el mismo, directo de
// la base — el campo que el frontend mandaba quedo IGNORADO server-side. Ver
// BookingCancellationService.ResolveServerSideTaxIdentity.
// ============================================================================
