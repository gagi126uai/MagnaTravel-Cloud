/**
 * Tests de lógica pura del chip "Operador: X" de ServiceList.jsx (decisión 3, spec
 * docs/ux/2026-07-03-cuenta-operador-reembolsos-multa.md).
 *
 * Qué cubre:
 *   - Sin nombre de operador → no se muestra nada.
 *   - Con permiso de ver proveedores y publicId → link a la ficha del operador.
 *   - Sin permiso → texto plano (nunca se oculta el nombre, solo el link).
 *   - Con permiso pero sin publicId (dato legacy/incompleto) → degrada a texto plano
 *     en vez de armar un link roto (/suppliers/undefined/account).
 *
 * Por qué lógica pura:
 *   La decisión de "link vs texto vs nada" es la parte crítica (gate de permiso +
 *   defensa ante datos incompletos). Se replica sin React/JSX para poder testearla
 *   con node --test puro, mismo patrón que serviceListCostConfirm.test.mjs.
 *
 * Cómo correr: node --test src/features/reservas/components/serviceSupplierChip.test.mjs
 */

import test from "node:test";
import assert from "node:assert/strict";

// ─── Réplica de la lógica de ServiceSupplierChip (ServiceList.jsx) ───────────

/**
 * Decide qué se muestra en el chip "Operador: X" de la fila de un servicio.
 * Réplica de ServiceSupplierChip en ServiceList.jsx.
 *
 * @param {object} params
 * @param {string|null|undefined} params.supplierName
 * @param {string|null|undefined} params.supplierPublicId
 * @param {boolean} params.puedeVerProveedores
 * @returns {{ tipo: "ninguno" }|{ tipo: "link"|"texto", label: string, href?: string }}
 */
function resolverChipOperador({ supplierName, supplierPublicId, puedeVerProveedores }) {
    if (!supplierName) return { tipo: "ninguno" };

    if (puedeVerProveedores && supplierPublicId) {
        return {
            tipo: "link",
            label: `Operador: ${supplierName}`,
            href: `/suppliers/${supplierPublicId}/account`,
        };
    }

    return { tipo: "texto", label: `Operador: ${supplierName}` };
}

// ============================================================================
// Sección 1: sin nombre de operador → no se muestra nada
// ============================================================================

test("sin supplierName → tipo 'ninguno' (el servicio no tiene operador asignado)", () => {
    const r = resolverChipOperador({ supplierName: null, supplierPublicId: "abc", puedeVerProveedores: true });
    assert.equal(r.tipo, "ninguno");
});

test("supplierName vacío ('') → tipo 'ninguno'", () => {
    const r = resolverChipOperador({ supplierName: "", supplierPublicId: "abc", puedeVerProveedores: true });
    assert.equal(r.tipo, "ninguno");
});

test("supplierName undefined → tipo 'ninguno' (no rompe)", () => {
    const r = resolverChipOperador({ supplierPublicId: "abc", puedeVerProveedores: true });
    assert.equal(r.tipo, "ninguno");
});

// ============================================================================
// Sección 2: con permiso y publicId → link a la ficha del operador
// ============================================================================

test("con permiso + publicId → link a /suppliers/{id}/account", () => {
    const r = resolverChipOperador({
        supplierName: "Despegar",
        supplierPublicId: "11111111-1111-1111-1111-111111111111",
        puedeVerProveedores: true,
    });
    assert.equal(r.tipo, "link");
    assert.equal(r.href, "/suppliers/11111111-1111-1111-1111-111111111111/account");
});

test("el label del link es 'Operador: <nombre>' (copy exacto de la spec)", () => {
    const r = resolverChipOperador({
        supplierName: "Ola Mayorista",
        supplierPublicId: "abc",
        puedeVerProveedores: true,
    });
    assert.equal(r.label, "Operador: Ola Mayorista");
});

// ============================================================================
// Sección 3: sin permiso → texto plano, NUNCA se oculta el nombre
// ============================================================================

test("sin permiso de ver proveedores → tipo 'texto' (el nombre se sigue mostrando)", () => {
    const r = resolverChipOperador({
        supplierName: "Despegar",
        supplierPublicId: "abc",
        puedeVerProveedores: false,
    });
    assert.equal(r.tipo, "texto");
    assert.equal(r.label, "Operador: Despegar");
});

test("sin permiso → el resultado NUNCA trae 'href' (no hay link para navegar)", () => {
    const r = resolverChipOperador({
        supplierName: "Despegar",
        supplierPublicId: "abc",
        puedeVerProveedores: false,
    });
    assert.equal(r.href, undefined);
});

// ============================================================================
// Sección 4: con permiso pero sin publicId → degrada a texto (evita link roto)
// ============================================================================

test("con permiso pero SIN supplierPublicId → tipo 'texto' (no arma un link roto)", () => {
    const r = resolverChipOperador({
        supplierName: "Despegar",
        supplierPublicId: null,
        puedeVerProveedores: true,
    });
    assert.equal(r.tipo, "texto");
    assert.equal(r.href, undefined);
});

test("con permiso pero supplierPublicId vacío ('') → tipo 'texto'", () => {
    const r = resolverChipOperador({
        supplierName: "Despegar",
        supplierPublicId: "",
        puedeVerProveedores: true,
    });
    assert.equal(r.tipo, "texto");
});

// ============================================================================
// Sección 5: coherencia — el label es igual sea link o texto (mismo dato visible)
// ============================================================================

test("el label es idéntico con y sin permiso (solo cambia si es link, nunca el texto visible)", () => {
    const conPermiso = resolverChipOperador({
        supplierName: "Despegar",
        supplierPublicId: "abc",
        puedeVerProveedores: true,
    });
    const sinPermiso = resolverChipOperador({
        supplierName: "Despegar",
        supplierPublicId: "abc",
        puedeVerProveedores: false,
    });
    assert.equal(conPermiso.label, sinPermiso.label);
});
