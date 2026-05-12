/**
 * Tests para la lógica de backoff de useInvoicePolling.
 * Testean el algoritmo puro (getPollingInterval, shouldStopPolling)
 * extraído a funciones puras para poder testear sin DOM ni React.
 */
import test from "node:test";
import assert from "node:assert/strict";

// ─── Lógica pura extraída del hook ───────────────────────────────────────────

const PHASE_FAST_INTERVAL = 3_000;
const PHASE_SLOW_INTERVAL = 10_000;
const PHASE_FAST_DURATION = 30_000;
const MAX_DURATION        = 120_000;

/** Retorna el intervalo en ms para el próximo tick dado el tiempo transcurrido. */
function getPollingInterval(elapsedMs, { fastInterval = PHASE_FAST_INTERVAL, slowInterval = PHASE_SLOW_INTERVAL, fastDuration = PHASE_FAST_DURATION } = {}) {
  return elapsedMs < fastDuration ? fastInterval : slowInterval;
}

/** Retorna true cuando el polling debe detenerse. */
function shouldStopPolling(elapsedMs, { maxDuration = MAX_DURATION } = {}) {
  return elapsedMs >= maxDuration;
}

/** Detecta si la lista de items tiene alguno en estado transitorio. */
function hasPendingItems(items) {
  return items.some(
    (item) =>
      item?.fiscalStatus === "in_progress" ||
      item?.annulmentStatus === "Pending" ||
      item?.resultado === "PENDING"
  );
}

// ─── Tests ───────────────────────────────────────────────────────────────────

test("hasPendingItems: detecta fiscalStatus in_progress", () => {
  const items = [
    { fiscalStatus: "ready" },
    { fiscalStatus: "in_progress" },
  ];
  assert.equal(hasPendingItems(items), true);
});

test("hasPendingItems: detecta annulmentStatus Pending", () => {
  const items = [
    { resultado: "A", annulmentStatus: "Pending" },
    { resultado: "A", annulmentStatus: "None" },
  ];
  assert.equal(hasPendingItems(items), true);
});

test("hasPendingItems: detecta resultado PENDING (MovementsTimeline)", () => {
  const items = [{ resultado: "PENDING" }];
  assert.equal(hasPendingItems(items), true);
});

test("hasPendingItems: lista sin items transitorio → false", () => {
  const items = [
    { fiscalStatus: "ready", annulmentStatus: "None", resultado: "A" },
    { fiscalStatus: "blocked", annulmentStatus: "Succeeded", resultado: "R" },
  ];
  assert.equal(hasPendingItems(items), false);
});

test("hasPendingItems: lista vacía → false", () => {
  assert.equal(hasPendingItems([]), false);
});

// Backoff: fase rápida

test("getPollingInterval: t=0 → fase rápida (3 s)", () => {
  assert.equal(getPollingInterval(0), PHASE_FAST_INTERVAL);
});

test("getPollingInterval: t=29999 ms → aún fase rápida", () => {
  assert.equal(getPollingInterval(29_999), PHASE_FAST_INTERVAL);
});

test("getPollingInterval: t=30000 ms → fase lenta (10 s)", () => {
  assert.equal(getPollingInterval(30_000), PHASE_SLOW_INTERVAL);
});

test("getPollingInterval: t=119999 ms → aún fase lenta", () => {
  assert.equal(getPollingInterval(119_999), PHASE_SLOW_INTERVAL);
});

// Stop

test("shouldStopPolling: t < 120 s → continuar", () => {
  assert.equal(shouldStopPolling(119_999), false);
});

test("shouldStopPolling: t = 120 s → stop", () => {
  assert.equal(shouldStopPolling(120_000), true);
});

test("shouldStopPolling: t > 120 s → stop", () => {
  assert.equal(shouldStopPolling(200_000), true);
});

// Opciones custom

test("getPollingInterval: opciones custom respetadas", () => {
  assert.equal(getPollingInterval(5_000, { fastInterval: 1_000, slowInterval: 5_000, fastDuration: 10_000 }), 1_000);
  assert.equal(getPollingInterval(15_000, { fastInterval: 1_000, slowInterval: 5_000, fastDuration: 10_000 }), 5_000);
});

test("shouldStopPolling: maxDuration custom respetado", () => {
  assert.equal(shouldStopPolling(30_000, { maxDuration: 60_000 }), false);
  assert.equal(shouldStopPolling(60_000, { maxDuration: 60_000 }), true);
});
