import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    debeAvisarCondicionFiscalOperadorDesconocida,
    TEXTO_AVISO_CONDICION_FISCAL_OPERADOR_DESCONOCIDA,
} from "./supplierTaxConditionWarning.js";

// ─── debeAvisarCondicionFiscalOperadorDesconocida (Bug #22, Tanda 4, 2026-07-24) ──────

describe("debeAvisarCondicionFiscalOperadorDesconocida", () => {
    it("supplierTaxConditionUnknown=true: hay que avisar", () => {
        assert.equal(debeAvisarCondicionFiscalOperadorDesconocida({ supplierTaxConditionUnknown: true }), true);
    });

    it("supplierTaxConditionUnknown=false: no hay que avisar", () => {
        assert.equal(debeAvisarCondicionFiscalOperadorDesconocida({ supplierTaxConditionUnknown: false }), false);
    });

    it("campo ausente (backend viejo / reserva vieja): no avisa (default seguro, sin dato sin aviso)", () => {
        assert.equal(debeAvisarCondicionFiscalOperadorDesconocida({}), false);
    });

    it("service null/undefined: no rompe, no avisa", () => {
        assert.equal(debeAvisarCondicionFiscalOperadorDesconocida(null), false);
        assert.equal(debeAvisarCondicionFiscalOperadorDesconocida(undefined), false);
    });

    it("valor truthy pero no boolean estricto (ej. string 'true'): NO avisa — solo el boolean true dispara el aviso", () => {
        assert.equal(debeAvisarCondicionFiscalOperadorDesconocida({ supplierTaxConditionUnknown: "true" }), false);
    });
});

// ─── Texto exacto del aviso ────────────────────────────────────────────────────────────

describe("TEXTO_AVISO_CONDICION_FISCAL_OPERADOR_DESCONOCIDA", () => {
    it("coincide EXACTO con el texto pedido (no se parafrasea en el camino)", () => {
        assert.equal(
            TEXTO_AVISO_CONDICION_FISCAL_OPERADOR_DESCONOCIDA,
            "Falta la condición fiscal del operador. Cargala en su ficha antes de cancelar, así la nota de crédito no se traba después."
        );
    });
});
