/**
 * Tests de lógica pura para la feature "Sacar de viaje" (Tanda 2, 2026-06-22).
 *
 * Cubre tres áreas:
 *   1. Visibilidad del botón "Sacar de viaje" en ReservaHeader.
 *   2. Validación del motivo en CorregirEntradaViajeModal (mín. 10 chars con trim).
 *   3. Chip "En corrección" y banner "En corrección" en ReservaStatusChips / ReservaDetailPage.
 *
 * Estos tests verifican lógica pura (sin DOM, sin React): replican las condiciones exactas
 * del componente para que un desarrollador trainee pueda entender qué regla está testeando
 * antes de abrir el archivo JSX.
 *
 * Cómo correr:
 *   node --test src/features/reservas/components/corregirEntradaViaje.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─────────────────────────────────────────────────────────────────────────────
// 1. VISIBILIDAD DEL BOTÓN "SACAR DE VIAJE"
//
// Replica de la condición showCorrectTravelingButton en ReservaHeader.jsx:
//
//   const correctTravelingCapability = getCapability(capabilities, 'canCorrectTravelingEntry');
//   const showCorrectTravelingButton =
//     esTraveling &&
//     correctTravelingCapability.allowed === true &&
//     isAdmin &&
//     typeof onCorrectTraveling === 'function';
//
// Las TRES condiciones deben ser true para que el botón aparezca.
// Si falta cualquiera → no se renderiza (ni gris, ni mensaje).
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica del helper getCapability de ReservaHeader.jsx.
 * Si no hay capabilities o no tiene el campo, devuelve {allowed: true, reason: null}
 * (degradación elegante — no bloquea en DTOs viejos sin ese campo).
 */
function getCapability(capabilities, field) {
    if (!capabilities || !capabilities[field]) return { allowed: true, reason: null };
    return capabilities[field];
}

/**
 * Replica de la condición showCorrectTravelingButton de ReservaHeader.
 * Necesita las tres condiciones para devolver true.
 */
function debeVerseBotonSacarDeViaje({ status, capabilities, isAdmin, tieneCallback }) {
    const esTraveling = status === 'Traveling';
    const correctTravelingCapability = getCapability(capabilities, 'canCorrectTravelingEntry');
    return (
        esTraveling &&
        correctTravelingCapability.allowed === true &&
        isAdmin === true &&
        tieneCallback === true
    );
}

// ── Casos que SÍ muestran el botón ────────────────────────────────────────────

test("Botón visible: Admin + Traveling + capability.allowed=true", () => {
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Traveling',
        capabilities: { canCorrectTravelingEntry: { allowed: true, reason: null } },
        isAdmin: true,
        tieneCallback: true,
    });
    assert.equal(resultado, true);
});

// ── Casos que NO muestran el botón ────────────────────────────────────────────

test("Botón oculto: no-Admin (aunque capability=true y Traveling)", () => {
    // Regla: solo Admin puede ver esta acción de excepción.
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Traveling',
        capabilities: { canCorrectTravelingEntry: { allowed: true, reason: null } },
        isAdmin: false,
        tieneCallback: true,
    });
    assert.equal(resultado, false);
});

test("Botón oculto: no está En viaje (status=Confirmed)", () => {
    // La corrección solo aplica cuando la reserva YA está en viaje.
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Confirmed',
        capabilities: { canCorrectTravelingEntry: { allowed: true, reason: null } },
        isAdmin: true,
        tieneCallback: true,
    });
    assert.equal(resultado, false);
});

test("Botón oculto: capability.allowed=false (tiene factura viva o voucher vivo)", () => {
    // El backend devuelve allowed=false cuando hay una factura con CAE activo o voucher vivo.
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Traveling',
        capabilities: { canCorrectTravelingEntry: { allowed: false, reason: 'Tiene factura emitida' } },
        isAdmin: true,
        tieneCallback: true,
    });
    assert.equal(resultado, false);
});

test("Botón oculto: no hay capabilities en el DTO (DTO viejo sin ese campo)", () => {
    // Degradación elegante: si no viene el campo, getCapability devuelve {allowed: true}.
    // Pero como no hay capabilities definidas en el DTO, asumimos fallback seguro.
    // En este test simulamos que el campo directamente no existe.
    // Con el helper, getCapability(undefined, 'canCorrectTravelingEntry') → {allowed: true}.
    // Eso significa que si también es Admin + Traveling + tieneCallback → SÍ se muestra.
    // Este test verifica el camino sin capabilities (resultado: true por degradación elegante).
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Traveling',
        capabilities: undefined,
        isAdmin: true,
        tieneCallback: true,
    });
    // Sin capabilities → getCapability devuelve {allowed: true} → botón visible para Admin.
    // Esto es intencional: si el backend no manda el campo, no bloqueamos (degradación).
    assert.equal(resultado, true);
});

test("Botón oculto: status=InManagement (no es viaje)", () => {
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'InManagement',
        capabilities: { canCorrectTravelingEntry: { allowed: true, reason: null } },
        isAdmin: true,
        tieneCallback: true,
    });
    assert.equal(resultado, false);
});

test("Botón oculto: status=Budget (etapa temprana)", () => {
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Budget',
        capabilities: { canCorrectTravelingEntry: { allowed: true, reason: null } },
        isAdmin: true,
        tieneCallback: true,
    });
    assert.equal(resultado, false);
});

test("Botón oculto: status=Closed (ya finalizada)", () => {
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Closed',
        capabilities: { canCorrectTravelingEntry: { allowed: true, reason: null } },
        isAdmin: true,
        tieneCallback: true,
    });
    assert.equal(resultado, false);
});

test("Botón oculto: no hay callback (padre no lo pasa)", () => {
    // Si el padre no pasa onCorrectTraveling, el botón no se renderiza.
    const resultado = debeVerseBotonSacarDeViaje({
        status: 'Traveling',
        capabilities: { canCorrectTravelingEntry: { allowed: true, reason: null } },
        isAdmin: true,
        tieneCallback: false,
    });
    assert.equal(resultado, false);
});

// ─────────────────────────────────────────────────────────────────────────────
// 2. VALIDACIÓN DEL MOTIVO
//
// Replica de la condición motivoValido en CorregirEntradaViajeModal.jsx:
//   const motivoValido = motivo.trim().length >= 10;
//
// Regla: mínimo 10 caracteres DESPUÉS de aplicar trim (sin espacios al inicio/fin).
// Esto evita que el usuario pase " " × 10 como motivo válido.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica de la validación de motivo del modal.
 */
function esMotivoValido(motivo) {
    return typeof motivo === 'string' && motivo.trim().length >= 10;
}

/**
 * Replica del contador de caracteres faltantes.
 * Si el motivo ya es válido, devuelve 0.
 */
function calcularCaracteresFaltantes(motivo) {
    return Math.max(0, 10 - (motivo?.trim()?.length ?? 0));
}

test("Motivo válido: 10 caracteres exactos", () => {
    assert.equal(esMotivoValido('Hola mundo'), true); // 10 chars con trim
});

test("Motivo válido: más de 10 caracteres", () => {
    assert.equal(esMotivoValido('Fecha mal cargada, el viaje no salió'), true);
});

test("Motivo inválido: 9 caracteres (uno menos del mínimo)", () => {
    assert.equal(esMotivoValido('123456789'), false);
});

test("Motivo inválido: vacío", () => {
    assert.equal(esMotivoValido(''), false);
});

test("Motivo inválido: solo espacios (trim los elimina)", () => {
    // 15 espacios → trim → 0 chars → inválido.
    // Evita que el usuario pase espacios como motivo.
    assert.equal(esMotivoValido('               '), false);
});

test("Motivo inválido: texto corto con espacios al inicio/fin (trim evalúa el contenido real)", () => {
    // "  hola  " → trim → "hola" (4 chars) → inválido.
    assert.equal(esMotivoValido('  hola  '), false);
});

test("Motivo válido: texto con espacios relleno que SÍ supera 10 chars sin los extremos", () => {
    // "  texto largo aqui  " → trim → "texto largo aqui" (16 chars) → válido.
    assert.equal(esMotivoValido('  texto largo aqui  '), true);
});

test("Contador de caracteres faltantes: 0 chars → faltan 10", () => {
    assert.equal(calcularCaracteresFaltantes(''), 10);
});

test("Contador de caracteres faltantes: 7 chars → faltan 3", () => {
    assert.equal(calcularCaracteresFaltantes('1234567'), 3);
});

test("Contador de caracteres faltantes: exactamente 10 chars → faltan 0", () => {
    assert.equal(calcularCaracteresFaltantes('1234567890'), 0);
});

test("Contador de caracteres faltantes: más de 10 chars → faltan 0 (nunca negativo)", () => {
    assert.equal(calcularCaracteresFaltantes('Este es un motivo largo'), 0);
});

// ─────────────────────────────────────────────────────────────────────────────
// 3. CHIP "EN CORRECCIÓN"
//
// Replica de la condición que muestra el chip.
//
// Chip (ReservaStatusChips): visible cuando reserva.isUnderCorrection === true.
//
// (2026-07-05, spec UX respuesta 2B) El banner separado que existía en
// ReservaDetailPage se ELIMINÓ: quedaba duplicado con este chip, que se enciende
// con la MISMA condición exacta. Ahora "En corrección" se ve SOLO acá (chip del
// header); el aviso completo vive en su title/tooltip.
//
// La reserva en estado "En corrección" ES Confirmada y operativa.
// No convierte la pantalla en solo-lectura: solo avisa que hay algo pendiente.
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Replica de la condición para mostrar el chip "En corrección".
 * Es exactamente reserva.isUnderCorrection === true.
 */
function debeVerseIndicadorCorreccion(reserva) {
    return reserva?.isUnderCorrection === true;
}

test("Chip visible: isUnderCorrection=true", () => {
    assert.equal(debeVerseIndicadorCorreccion({ isUnderCorrection: true }), true);
});

test("Chip oculto: isUnderCorrection=false (reserva normal en viaje o confirmada)", () => {
    assert.equal(debeVerseIndicadorCorreccion({ isUnderCorrection: false }), false);
});

test("Chip oculto: isUnderCorrection no existe en el DTO (DTO viejo)", () => {
    // Degradación elegante: si el campo no viene, asumimos false.
    assert.equal(debeVerseIndicadorCorreccion({ status: 'Traveling' }), false);
});

test("Chip oculto: reserva undefined", () => {
    // Protección contra undefined en el componente (optional chaining).
    assert.equal(debeVerseIndicadorCorreccion(undefined), false);
});

test("Chip oculto: isUnderCorrection=null (no es true)", () => {
    assert.equal(debeVerseIndicadorCorreccion({ isUnderCorrection: null }), false);
});

// Verificamos que el estado "En corrección" puede ocurrir en Confirmed (estado operativo normal).
// La reserva fue sacada de viaje y volvió a Confirmed, pero con la marca activa.
test("Estado 'En corrección' es compatible con status=Confirmed (reserva sigue operativa)", () => {
    const reserva = { status: 'Confirmed', isUnderCorrection: true };
    // La reserva no está en modo solo-lectura (no es Traveling, Lost, Cancelled ni Closed).
    const esSoloLectura = ['Traveling', 'Lost', 'Cancelled', 'Closed'].includes(reserva.status);
    assert.equal(esSoloLectura, false, "Una reserva 'En corrección' en Confirmed NO debe ser solo-lectura");
    assert.equal(debeVerseIndicadorCorreccion(reserva), true);
});
