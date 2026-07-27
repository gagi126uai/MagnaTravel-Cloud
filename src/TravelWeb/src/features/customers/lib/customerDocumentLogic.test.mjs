/**
 * Tests del casillero de documento unificado del alta de cliente (P1, 2026-07-25).
 *
 * Cómo correr: node --test src/features/customers/lib/customerDocumentLogic.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

import {
    DOCUMENT_TYPE_OPTIONS,
    tipoDocumentoTieneBusquedaAfip,
    esTipoDocumentoFiscal,
    construirEstadoInicialDocumento,
    construirPayloadDocumento,
    aplicarResultadoAfip,
    obtenerDocumentoAlternativo,
    describirDocumentoAlternativo,
} from "./customerDocumentLogic.js";

// ─── DOCUMENT_TYPE_OPTIONS ──────────────────────────────────────────────────────

test("DOCUMENT_TYPE_OPTIONS: los 5 tipos del mockup firmado, en orden", () => {
    assert.deepEqual(
        DOCUMENT_TYPE_OPTIONS.map((o) => o.value),
        ["CUIT", "CUIL", "DNI", "Pasaporte", "Otro"]
    );
});

// ─── tipoDocumentoTieneBusquedaAfip ──────────────────────────────────────────────

test("CUIT/CUIL/DNI -> tienen lupita AFIP", () => {
    assert.equal(tipoDocumentoTieneBusquedaAfip("CUIT"), true);
    assert.equal(tipoDocumentoTieneBusquedaAfip("CUIL"), true);
    assert.equal(tipoDocumentoTieneBusquedaAfip("DNI"), true);
});

test("Pasaporte/Otro -> SIN lupita AFIP", () => {
    assert.equal(tipoDocumentoTieneBusquedaAfip("Pasaporte"), false);
    assert.equal(tipoDocumentoTieneBusquedaAfip("Otro"), false);
});

// ─── esTipoDocumentoFiscal ───────────────────────────────────────────────────────

test("CUIT y CUIL son fiscales (van a taxId)", () => {
    assert.equal(esTipoDocumentoFiscal("CUIT"), true);
    assert.equal(esTipoDocumentoFiscal("CUIL"), true);
});

test("DNI/Pasaporte/Otro NO son fiscales", () => {
    assert.equal(esTipoDocumentoFiscal("DNI"), false);
    assert.equal(esTipoDocumentoFiscal("Pasaporte"), false);
    assert.equal(esTipoDocumentoFiscal("Otro"), false);
});

// ─── construirEstadoInicialDocumento ────────────────────────────────────────────

test("cliente nuevo (sin datos) -> arranca en DNI vacío", () => {
    assert.deepEqual(construirEstadoInicialDocumento(null), { tipoDocumento: "DNI", numeroDocumento: "" });
    assert.deepEqual(construirEstadoInicialDocumento(undefined), { tipoDocumento: "DNI", numeroDocumento: "" });
});

// Este es el fixture EXACTO del hallazgo B2 (revisión 2026-07-27): un cliente con CUIT
// Y un DNI viejo cargado. El casillero único solo puede mostrar UNO de los dos a la vez
// (acá arranca en CUIT), así que si el usuario guarda sin tocar nada, el DNI que no se
// ve en pantalla no puede perderse — ver los tests de round-trip de
// construirPayloadDocumento más abajo ("documentoFueTocado=false") para la otra mitad
// de esta regla (esta prueba solo cubre la pantalla INICIAL, no el guardado).
test("cliente con taxId cargado -> arranca en CUIT con ese número (taxId manda)", () => {
    const resultado = construirEstadoInicialDocumento({ taxId: "20304050607", documentType: "DNI", documentNumber: "30405060" });
    assert.deepEqual(resultado, { tipoDocumento: "CUIT", numeroDocumento: "20304050607" });
});

test("cliente con taxId + documentType=CUIL -> respeta CUIL (no fuerza CUIT)", () => {
    const resultado = construirEstadoInicialDocumento({ taxId: "20304050607", documentType: "CUIL" });
    assert.deepEqual(resultado, { tipoDocumento: "CUIL", numeroDocumento: "20304050607" });
});

test("cliente sin taxId pero con documentType/documentNumber -> usa esos", () => {
    const resultado = construirEstadoInicialDocumento({ documentType: "Pasaporte", documentNumber: "AB123456" });
    assert.deepEqual(resultado, { tipoDocumento: "Pasaporte", numeroDocumento: "AB123456" });
});

test("cliente sin ningún dato de documento -> DNI vacío (default legacy)", () => {
    const resultado = construirEstadoInicialDocumento({ fullName: "Juan Pérez" });
    assert.deepEqual(resultado, { tipoDocumento: "DNI", numeroDocumento: "" });
});

// ─── construirPayloadDocumento ───────────────────────────────────────────────────
//
// Desde la revisión 2026-07-27 (hallazgo B2), construirPayloadDocumento necesita saber
// si el usuario TOCÓ el casillero (documentoFueTocado) y el cliente ORIGINAL (para poder
// preservar lo que no se ve en pantalla). Los tests de acá abajo simulan el ALTA (o una
// edición donde el usuario sí cambió el casillero): documentoFueTocado=true, sin
// clienteOriginal — mismo comportamiento que tenía esta función antes de la B2.

test("casillero vacío -> los 3 campos viajan null", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "DNI", numeroDocumento: "", documentoFueTocado: true });
    assert.deepEqual(payload, { documentType: null, documentNumber: null, taxId: null });
});

test("casillero vacío con solo espacios -> también viaja null (trim)", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "DNI", numeroDocumento: "   ", documentoFueTocado: true });
    assert.deepEqual(payload, { documentType: null, documentNumber: null, taxId: null });
});

// CUIT/CUIL en ALTA (sin clienteOriginal, nada que proteger): documentType/documentNumber
// SÍ se llenan con el mismo número que taxId — esto es lo que revive el guard de
// duplicados del motor (H3). Si acá también los vaciáramos, el alta con CUIT (el caso
// más común) dejaría MUERTO ese guard de nuevo.
test("CUIT cargado (tocado, alta) -> documentType+documentNumber+taxId, los 3 con el mismo número (revive el guard H3)", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "CUIT", numeroDocumento: "20-30405060-7", documentoFueTocado: true });
    assert.deepEqual(payload, {
        documentType: "CUIT",
        documentNumber: "20-30405060-7",
        taxId: "20-30405060-7",
    });
});

test("CUIL cargado (tocado, alta) -> también manda documentType+documentNumber (mismo criterio, revive H3)", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "CUIL", numeroDocumento: "27-12345678-3", documentoFueTocado: true });
    assert.deepEqual(payload, {
        documentType: "CUIL",
        documentNumber: "27-12345678-3",
        taxId: "27-12345678-3",
    });
});

test("DNI cargado (tocado, alta) -> documentType+documentNumber, taxId queda null (no hay CUIT original)", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "DNI", numeroDocumento: "30405060", documentoFueTocado: true });
    assert.deepEqual(payload, {
        documentType: "DNI",
        documentNumber: "30405060",
        taxId: null,
    });
});

test("Pasaporte cargado (tocado, alta) -> documentType+documentNumber, sin taxId", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "Pasaporte", numeroDocumento: "AB123456", documentoFueTocado: true });
    assert.deepEqual(payload, {
        documentType: "Pasaporte",
        documentNumber: "AB123456",
        taxId: null,
    });
});

test("Otro cargado (tocado, alta) -> documentType+documentNumber, sin taxId", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "Otro", numeroDocumento: "X-9999", documentoFueTocado: true });
    assert.deepEqual(payload, {
        documentType: "Otro",
        documentNumber: "X-9999",
        taxId: null,
    });
});

test("número con espacios al borde -> se recorta (trim) antes de mandar", () => {
    const payload = construirPayloadDocumento({ tipoDocumento: "DNI", numeroDocumento: "  30405060  ", documentoFueTocado: true });
    assert.equal(payload.documentNumber, "30405060");
});

// ─── construirPayloadDocumento — round-trip B2 (no pisar lo que no se ve) ────────

test("B2 (a): cliente con CUIT+DNI, casillero NO tocado -> el payload preserva los 2 (documentType y taxId intactos)", () => {
    // Mismo fixture que "cliente con taxId cargado -> arranca en CUIT" de arriba: el
    // casillero muestra CUIT, pero el cliente real tiene un DNI viejo cargado también.
    const clienteOriginal = { taxId: "20304050607", documentType: "DNI", documentNumber: "30405060" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "CUIT",
        numeroDocumento: "20304050607",
        documentoFueTocado: false, // el usuario abrió la ficha y guardó sin tocar el casillero
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: "DNI",
        documentNumber: "30405060",
        taxId: "20304050607",
    });
});

test("B2 (b): cliente con CUIT+DNI, cambia SOLO el número de CUIT -> taxId nuevo, documentType/documentNumber vacíos", () => {
    const clienteOriginal = { taxId: "20304050607", documentType: "DNI", documentNumber: "30405060" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "CUIT",
        numeroDocumento: "20999999996", // el usuario corrigió el número de CUIT
        documentoFueTocado: true,
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: null, // vacío a propósito: el motor preserva el DNI guardado
        documentNumber: null,
        taxId: "20999999996",
    });
});

test("B2/H3: cliente que YA tenía CUIT (sin DNI legacy), edita el número -> documentType/documentNumber SÍ se llenan (nada que proteger, revive el guard)", () => {
    const clienteOriginal = { taxId: "20304050607", documentType: "CUIT", documentNumber: "20304050607" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "CUIT",
        numeroDocumento: "20999999996",
        documentoFueTocado: true,
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: "CUIT",
        documentNumber: "20999999996",
        taxId: "20999999996",
    });
});

test("B2/H3: cliente con CUIL cargado, cambia a CUIT con otro número -> revive el guard (el CUIL original no es un DNI/pasaporte que proteger)", () => {
    const clienteOriginal = { taxId: "27111222338", documentType: "CUIL", documentNumber: "27111222338" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "CUIT",
        numeroDocumento: "20304050607",
        documentoFueTocado: true,
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: "CUIT",
        documentNumber: "20304050607",
        taxId: "20304050607",
    });
});

test("B2 (c): cliente con pasaporte+CUIT, cambia a Pasaporte con otro número -> el taxId original viaja intacto", () => {
    const clienteOriginal = { taxId: "20304050607", documentType: "Pasaporte", documentNumber: "AB123456" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "Pasaporte",
        numeroDocumento: "CD654321", // pasaporte nuevo
        documentoFueTocado: true,
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: "Pasaporte",
        documentNumber: "CD654321",
        taxId: "20304050607", // el motor pisa taxId incondicional: hay que reenviarlo igual
    });
});

test("B2: caso borde — cliente legacy con documentType=CUIT y taxId vacío, casillero NO tocado -> se reenvía tal cual (no dispara el guard fiscal del motor)", () => {
    const clienteOriginal = { taxId: null, documentType: "CUIT", documentNumber: "20304050607" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "CUIT",
        numeroDocumento: "20304050607",
        documentoFueTocado: false, // ej: el usuario solo cambió el teléfono
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: "CUIT",
        documentNumber: "20304050607",
        taxId: null,
    });
});

// BL-1 (revisión 2026-07-27): clientes legacy guardados por la versión ANTERIOR del
// modal (antes del casillero único) quedaron con taxId="" en la base, no con taxId=null.
// Antes del fix, `clienteOriginal?.taxId || null` convertía esa cadena vacía en null, y
// el motor compara taxId con Ordinal.Equals -> null !== "" disparaba un taxIdChanged
// falso con solo tocar el teléfono (409 si había factura con CAE, o auditoría fantasma).
test("BL-1 (a): cliente legacy con taxId=\"\" (no null) + DNI, casillero NO tocado -> el payload reenvía taxId=\"\" tal cual, NO null", () => {
    const clienteOriginal = { taxId: "", documentType: "DNI", documentNumber: "30405060" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "DNI",
        numeroDocumento: "30405060",
        documentoFueTocado: false, // ej: el usuario solo cambió el teléfono
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: "DNI",
        documentNumber: "30405060",
        taxId: "", // antes del fix esto llegaba en null y disparaba el 409 falso
    });
});

test("BL-1 (b): cliente legacy con taxId=\"\", elige un tipo NO fiscal (Pasaporte) -> taxId reenviado sigue siendo \"\", no null", () => {
    const clienteOriginal = { taxId: "", documentType: "DNI", documentNumber: "30405060" };
    const payload = construirPayloadDocumento({
        tipoDocumento: "Pasaporte",
        numeroDocumento: "AB999999",
        documentoFueTocado: true, // cambió tipo y número, pero no tocó el CUIT (no tenía)
        clienteOriginal,
    });
    assert.deepEqual(payload, {
        documentType: "Pasaporte",
        documentNumber: "AB999999",
        taxId: "",
    });
});

test("alta (sin clienteOriginal), casillero no tocado y vacío -> los 3 campos null (comportamiento de siempre)", () => {
    const payload = construirPayloadDocumento({
        tipoDocumento: "DNI",
        numeroDocumento: "",
        documentoFueTocado: false,
        clienteOriginal: null,
    });
    assert.deepEqual(payload, { documentType: null, documentNumber: null, taxId: null });
});

// ─── aplicarResultadoAfip (hallazgo B1) ──────────────────────────────────────────

test("B1: casillero en DNI, elige un resultado de AFIP -> el tipo pasa a CUIT (lo que vino del padrón es un CUIT/CUIL)", () => {
    const resultado = aplicarResultadoAfip(
        { tipoDocumento: "DNI", numeroDocumento: "30405060" },
        { id: "20304050607" }
    );
    assert.deepEqual(resultado, { tipoDocumento: "CUIT", numeroDocumento: "20304050607" });
});

test("B1: casillero en CUIL, elige un resultado de AFIP -> se queda en CUIL (no lo fuerza a CUIT)", () => {
    const resultado = aplicarResultadoAfip(
        { tipoDocumento: "CUIL", numeroDocumento: "27111222338" },
        { id: "27999888776" }
    );
    assert.deepEqual(resultado, { tipoDocumento: "CUIL", numeroDocumento: "27999888776" });
});

test("B1: casillero en Pasaporte, elige un resultado de AFIP -> también pasa a CUIT", () => {
    const resultado = aplicarResultadoAfip(
        { tipoDocumento: "Pasaporte", numeroDocumento: "AB123456" },
        { id: "20111222339" }
    );
    assert.deepEqual(resultado, { tipoDocumento: "CUIT", numeroDocumento: "20111222339" });
});

test("B1: resultado de AFIP sin id -> conserva el número que ya había en el casillero", () => {
    const resultado = aplicarResultadoAfip(
        { tipoDocumento: "CUIT", numeroDocumento: "20304050607" },
        {}
    );
    assert.deepEqual(resultado, { tipoDocumento: "CUIT", numeroDocumento: "20304050607" });
});

// ─── obtenerDocumentoAlternativo (Obra 3, ficha del cliente unificada, 2026-07-27) ───

test("cliente con CUIT y un DNI viejo guardado aparte -> devuelve el DNI (fixture real del hallazgo B2)", () => {
    const resultado = obtenerDocumentoAlternativo({
        taxId: "20304050607",
        documentType: "DNI",
        documentNumber: "36053656",
    });
    assert.deepEqual(resultado, { tipoDocumento: "DNI", numeroDocumento: "36053656" });
});

test("cliente con CUIT y un Pasaporte guardado aparte -> devuelve el Pasaporte", () => {
    const resultado = obtenerDocumentoAlternativo({
        taxId: "20304050607",
        documentType: "Pasaporte",
        documentNumber: "AB123456",
    });
    assert.deepEqual(resultado, { tipoDocumento: "Pasaporte", numeroDocumento: "AB123456" });
});

test("cliente con solo CUIT (sin otro documento guardado) -> no hay nada que mostrar aparte", () => {
    assert.equal(obtenerDocumentoAlternativo({ taxId: "20304050607" }), null);
});

test("cliente con solo DNI (sin taxId) -> no hay nada que mostrar aparte (no existe un CUIT oculto)", () => {
    assert.equal(obtenerDocumentoAlternativo({ documentType: "DNI", documentNumber: "30405060" }), null);
});

test("cliente con CUIT y documentType=CUIT/CUIL igual (mismo dato, no un segundo documento) -> no hay nada que mostrar", () => {
    assert.equal(
        obtenerDocumentoAlternativo({ taxId: "20304050607", documentType: "CUIT", documentNumber: "20304050607" }),
        null
    );
    assert.equal(
        obtenerDocumentoAlternativo({ taxId: "27111222338", documentType: "CUIL", documentNumber: "27111222338" }),
        null
    );
});

// Fix bloqueante del reviewer (2026-07-27): antes, con documentType FISCAL, la función
// asumía "mismo dato que el taxId" sin comparar los números, y este caso quedaba oculto.
test("cliente legacy con documentType=CUIT pero un NÚMERO distinto del taxId vigente -> SÍ se muestra (dato inconsistente real, no se esconde)", () => {
    const resultado = obtenerDocumentoAlternativo({
        taxId: "20111222333",
        documentType: "CUIT",
        documentNumber: "20999888777",
    });
    assert.deepEqual(resultado, { tipoDocumento: "CUIT", numeroDocumento: "20999888777" });
});

test("mismo caso con CUIL (número distinto del taxId vigente) -> también se muestra", () => {
    const resultado = obtenerDocumentoAlternativo({
        taxId: "20111222333",
        documentType: "CUIL",
        documentNumber: "27999888776",
    });
    assert.deepEqual(resultado, { tipoDocumento: "CUIL", numeroDocumento: "27999888776" });
});

// Fix bloqueante del reviewer (2026-07-27, verificación visual en PROD — caso real
// "JAIR", uno de los 5 clientes legacy con CUIT+DNI): documentType NULL en la BD (nunca
// se llegó a cargar el tipo), pero taxId + documentNumber son números REALES y
// distintos. Antes, la guarda `if (!taxId || !documentType || !documentNumber)` cortaba
// camino apenas veía documentType null y devolvía null SIN mirar el número — el DNI
// quedaba invisible en toda la ficha, contra la firma ("no se esconde ningún documento").
test("caso real JAIR: taxId=20360536565, documentNumber=36053656, documentType=NULL -> el alternativo SÍ aparece (tipo null)", () => {
    const resultado = obtenerDocumentoAlternativo({
        taxId: "20360536565",
        documentType: null,
        documentNumber: "36053656",
    });
    assert.deepEqual(resultado, { tipoDocumento: null, numeroDocumento: "36053656" });
});

test("inverso del caso JAIR: documentNumber IGUAL al taxId (sin guiones) y documentType null -> no hay nada que mostrar (no repetir el mismo número)", () => {
    assert.equal(
        obtenerDocumentoAlternativo({ taxId: "20360536565", documentType: null, documentNumber: "20360536565" }),
        null
    );
});

test("los números son el mismo documento aunque uno tenga guiones y el otro no -> no hay nada que mostrar (normalización)", () => {
    assert.equal(
        obtenerDocumentoAlternativo({ taxId: "20-36053656-5", documentType: "CUIT", documentNumber: "20360536565" }),
        null
    );
    assert.equal(
        obtenerDocumentoAlternativo({ taxId: "20360536565", documentType: null, documentNumber: "20-36053656-5" }),
        null
    );
});

test("cliente con documentType null pero SIN documentNumber -> no hay nada que mostrar", () => {
    assert.equal(obtenerDocumentoAlternativo({ taxId: "20360536565", documentType: null, documentNumber: "" }), null);
    assert.equal(obtenerDocumentoAlternativo({ taxId: "20360536565", documentType: null }), null);
});

test("cliente null/undefined -> no rompe, no hay nada que mostrar", () => {
    assert.equal(obtenerDocumentoAlternativo(null), null);
    assert.equal(obtenerDocumentoAlternativo(undefined), null);
});

// ─── describirDocumentoAlternativo (fix del reviewer, 2026-07-27) ────────────────

test("tipo 'Otro' -> frase legible 'otro documento {numero}', NUNCA 'Otro {numero}' crudo", () => {
    assert.equal(
        describirDocumentoAlternativo({ tipoDocumento: "Otro", numeroDocumento: "AB123" }),
        "otro documento AB123"
    );
});

test("tipo DNI/Pasaporte/CUIT/CUIL -> el tipo se muestra tal cual", () => {
    assert.equal(describirDocumentoAlternativo({ tipoDocumento: "DNI", numeroDocumento: "36053656" }), "DNI 36053656");
    assert.equal(describirDocumentoAlternativo({ tipoDocumento: "Pasaporte", numeroDocumento: "AB123456" }), "Pasaporte AB123456");
    assert.equal(describirDocumentoAlternativo({ tipoDocumento: "CUIT", numeroDocumento: "20999888777" }), "CUIT 20999888777");
});

test("caso real JAIR: tipoDocumento null -> frase genérica 'un documento {numero}', NUNCA 'null 36053656'", () => {
    assert.equal(
        describirDocumentoAlternativo({ tipoDocumento: null, numeroDocumento: "36053656" }),
        "un documento 36053656"
    );
});

test("sin documento alternativo (null/undefined) -> cadena vacía, no rompe", () => {
    assert.equal(describirDocumentoAlternativo(null), "");
    assert.equal(describirDocumentoAlternativo(undefined), "");
});

test("cliente legacy con taxId=\"\" (string vacío) -> no hay nada que mostrar (mismo criterio que taxId ausente)", () => {
    assert.equal(
        obtenerDocumentoAlternativo({ taxId: "", documentType: "DNI", documentNumber: "30405060" }),
        null
    );
});
