/**
 * Tests de lógica pura para getApiErrorMessage / normalizeMessage en errors.js.
 *
 * Foco: verificar que errores de transporte de red y bare HTTP statusText
 * se convierten al genérico en español, y que los mensajes del servidor
 * en español pasan sin cambios.
 *
 * Cómo correr:
 *   node --test src/lib/errors.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";
import { getApiErrorMessage, normalizeMessage, SPANISH_NETWORK_GENERIC } from "./errors.js";

// ─── Errores de transporte (red) ──────────────────────────────────────────────

test("getApiErrorMessage — 'Failed to fetch' → genérico español", () => {
    const error = { message: "Failed to fetch" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'Load failed' (Safari) → genérico español", () => {
    const error = { message: "Load failed" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'Network request failed' → genérico español", () => {
    const error = { message: "Network request failed" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — TypeError con 'Failed to fetch' → genérico español", () => {
    const error = new TypeError("Failed to fetch");
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

// ─── Bare HTTP statusText sin payload del servidor ────────────────────────────

test("getApiErrorMessage — 'Internal Server Error' sin payload → genérico español", () => {
    const error = { message: "Internal Server Error" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'Request failed' sin payload → genérico español", () => {
    const error = { message: "Request failed" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

// ─── Mensajes del servidor en español → pasan intactos ───────────────────────

test("getApiErrorMessage — payload.message en español → pasa sin cambios", () => {
    const error = {
        payload: { message: "El operador no existe o fue eliminado." },
        message: "Internal Server Error",
    };
    assert.strictEqual(
        getApiErrorMessage(error, "Fallback"),
        "El operador no existe o fue eliminado."
    );
});

test("getApiErrorMessage — payload.message en español (monto inválido) → pasa sin cambios", () => {
    const error = {
        payload: { message: "El monto debe ser mayor a cero." },
        message: "Bad Request",
    };
    assert.strictEqual(
        getApiErrorMessage(error, "Fallback"),
        "El monto debe ser mayor a cero."
    );
});

test("getApiErrorMessage — payload con errors de validación → pasa sin cambios", () => {
    // Ejemplo: 400 con errors de campo típicos de ASP.NET
    const error = {
        payload: { errors: { Amount: ["El monto es requerido."] } },
        message: "Bad Request",
    };
    assert.strictEqual(
        getApiErrorMessage(error, "Fallback"),
        "El monto es requerido."
    );
});

// ─── Errores con mensajes reales (no transporte) → pasan intactos ────────────

test("getApiErrorMessage — Error con mensaje propio en español → pasa sin cambios", () => {
    const error = new Error("Fecha de salida inválida.");
    assert.strictEqual(
        getApiErrorMessage(error, "Fallback"),
        "Fecha de salida inválida."
    );
});

test("getApiErrorMessage — mensaje del servidor en inglés pero no statusText → pasa sin cambios", () => {
    // Un mensaje del servidor que contiene palabras inglesas pero no es un bare statusText.
    // No debemos censurarlo: es información potencialmente útil para el soporte.
    const error = {
        payload: { message: "Cannot cancel: voucher already issued" },
        message: "Bad Request",
    };
    assert.strictEqual(
        getApiErrorMessage(error, "Fallback"),
        "Cannot cancel: voucher already issued"
    );
});

// ─── Casos vacíos / nulos → usa el fallback ──────────────────────────────────

test("getApiErrorMessage — error nulo → devuelve el fallback", () => {
    assert.strictEqual(getApiErrorMessage(null, "Fallback específico"), "Fallback específico");
});

test("getApiErrorMessage — error sin message ni payload → devuelve fallback", () => {
    assert.strictEqual(getApiErrorMessage({}, "Sin datos"), "Sin datos");
});

// ─── Nuevos HTTP statusTexts bare (E1 + hardening) ───────────────────────────
// Verificar que los statusTexts que el browser asigna al campo message cuando el
// servidor devuelve body vacío sean interceptados antes de llegar al usuario.

test("getApiErrorMessage — 'Not Found' sin payload → genérico español", () => {
    const error = { message: "Not Found" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'Forbidden' sin payload → genérico español", () => {
    const error = { message: "Forbidden" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'Unauthorized' sin payload → genérico español", () => {
    const error = { message: "Unauthorized" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'Bad Request' sin payload → genérico español", () => {
    const error = { message: "Bad Request" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'Too Many Requests' sin payload → genérico español", () => {
    const error = { message: "Too Many Requests" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — case-insensitive: 'not found' → genérico español", () => {
    // El browser puede enviar el statusText en minúsculas según el entorno
    const error = { message: "not found" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

test("getApiErrorMessage — 'forbidden' minúsculas → genérico español", () => {
    const error = { message: "forbidden" };
    assert.strictEqual(getApiErrorMessage(error, "Fallback"), SPANISH_NETWORK_GENERIC);
});

// Payload.message en español pasa intacto AUNQUE el message (statusText) sea un bare statusText.
// Esto asegura que el mensaje real del servidor no se reemplaza por el genérico.

test("hardening — 'Not Found' como statusText pero payload en español → payload pasa intacto", () => {
    const error = {
        payload: { message: "El comprobante con ese ID no existe." },
        message: "Not Found",
    };
    assert.strictEqual(
        getApiErrorMessage(error, "Fallback"),
        "El comprobante con ese ID no existe."
    );
});

test("hardening — 'Forbidden' como statusText pero payload en español → payload pasa intacto", () => {
    const error = {
        payload: { message: "No tenés permiso para ver este comprobante." },
        message: "Forbidden",
    };
    assert.strictEqual(
        getApiErrorMessage(error, "Fallback"),
        "No tenés permiso para ver este comprobante."
    );
});

// ─── normalizeMessage — integración ──────────────────────────────────────────

test("normalizeMessage — string 'Failed to fetch' → genérico español", () => {
    assert.strictEqual(normalizeMessage("Failed to fetch"), SPANISH_NETWORK_GENERIC);
});

test("normalizeMessage — string en español → pasa sin cambios", () => {
    const msg = "La reserva ya fue cancelada.";
    assert.strictEqual(normalizeMessage(msg), msg);
});

test("normalizeMessage — null → devuelve fallback", () => {
    assert.strictEqual(normalizeMessage(null, "Fallback"), "Fallback");
});

test("normalizeMessage — comparación case-insensitive de transporte", () => {
    // El runtime puede enviar el mensaje con capitalización variada
    assert.strictEqual(normalizeMessage("FAILED TO FETCH"), SPANISH_NETWORK_GENERIC);
    assert.strictEqual(normalizeMessage("Failed To Fetch"), SPANISH_NETWORK_GENERIC);
});

test("normalizeMessage — mensaje que contiene 'fetch' como substring → NO reemplaza", () => {
    // "fetch" como substring NO debe reemplazarse; solo coincidencia exacta.
    const msg = "No se pudo hacer fetch del tarifario.";
    assert.strictEqual(normalizeMessage(msg), msg);
});
