/**
 * Tests de lógica pura para cuentas bancarias.
 *
 * Cubre: OWNER_TYPE, ACCOUNT_TYPE, maskCbu, maskAlias, validarCbu,
 *        validarFormularioCuenta, construirPayloadCuentaBancaria,
 *        resolverCuentaPrincipalPorMoneda.
 *
 * Correr: node --test src/features/bank-accounts/lib/bankAccountLogic.test.mjs
 * (o via npm test cuando se agrega el glob al script de package.json)
 *
 * Patrón del proyecto: funciones importadas directamente desde el módulo .js.
 */

import { describe, it } from "node:test";
import assert from "node:assert/strict";
import {
    OWNER_TYPE,
    ACCOUNT_TYPE,
    maskCbu,
    maskAlias,
    validarCbu,
    validarFormularioCuenta,
    construirPayloadCuentaBancaria,
    resolverCuentaPrincipalPorMoneda,
    clasificarErrorCuenta,
} from "./bankAccountLogic.js";

// ─── OWNER_TYPE / ACCOUNT_TYPE ────────────────────────────────────────────────

describe("OWNER_TYPE", () => {
    it("Agency es 0 (valor del enum del backend)", () => {
        assert.equal(OWNER_TYPE.Agency, 0);
    });

    it("Customer es 1", () => {
        assert.equal(OWNER_TYPE.Customer, 1);
    });

    it("Supplier es 2", () => {
        assert.equal(OWNER_TYPE.Supplier, 2);
    });
});

describe("ACCOUNT_TYPE", () => {
    it("CajaAhorro es 0 (valor del enum del backend)", () => {
        assert.equal(ACCOUNT_TYPE.CajaAhorro, 0);
    });

    it("CuentaCorriente es 1", () => {
        assert.equal(ACCOUNT_TYPE.CuentaCorriente, 1);
    });
});

// ─── maskCbu ─────────────────────────────────────────────────────────────────

describe("maskCbu", () => {
    it("enmascara un CBU de 22 dígitos mostrando los últimos 4", () => {
        assert.equal(maskCbu("0720002088000027993280"), "····3280");
    });

    it("retorna cadena vacía si cbu es null", () => {
        assert.equal(maskCbu(null), "");
    });

    it("retorna cadena vacía si cbu es undefined", () => {
        assert.equal(maskCbu(undefined), "");
    });

    it("retorna el valor completo si tiene 4 dígitos o menos", () => {
        assert.equal(maskCbu("1234"), "1234");
        assert.equal(maskCbu("12"), "12");
    });

    it("ignora espacios al inicio y fin", () => {
        assert.equal(maskCbu("  0720002088000027993280  "), "····3280");
    });
});

// ─── maskAlias ───────────────────────────────────────────────────────────────

describe("maskAlias", () => {
    it("muestra solo el último segmento cuando hay puntos", () => {
        assert.equal(maskAlias("magna.viajes.sa"), "····.sa");
    });

    it("funciona con alias de un solo punto", () => {
        assert.equal(maskAlias("magna.viajes"), "····.viajes");
    });

    it("muestra los últimos 8 chars si no hay punto", () => {
        // "magnaviajes123" tiene 14 chars → últimos 8 = "iajes123"
        const result = maskAlias("magnaviajes123");
        assert.ok(result.startsWith("····"), "debe empezar con ····");
        assert.equal(result, "····iajes123");
    });

    it("retorna cadena vacía si alias es null", () => {
        assert.equal(maskAlias(null), "");
    });

    it("retorna cadena vacía si alias es undefined", () => {
        assert.equal(maskAlias(undefined), "");
    });

    it("retorna el valor completo si tiene 8 chars o menos sin puntos", () => {
        assert.equal(maskAlias("corto"), "corto");
    });
});

// ─── validarCbu ──────────────────────────────────────────────────────────────

describe("validarCbu", () => {
    it("retorna null si el CBU está vacío (es opcional)", () => {
        assert.equal(validarCbu(""), null);
        assert.equal(validarCbu(null), null);
        assert.equal(validarCbu(undefined), null);
    });

    it("retorna null si el CBU tiene exactamente 22 dígitos", () => {
        assert.equal(validarCbu("0720002088000027993280"), null);
    });

    it("retorna mensaje de error si tiene menos de 22 dígitos", () => {
        const error = validarCbu("072000208800002799328");
        assert.ok(typeof error === "string", "debe retornar string");
        assert.ok(error.includes("22"), "el mensaje debe mencionar 22 dígitos");
    });

    it("retorna mensaje de error si tiene más de 22 dígitos", () => {
        const error = validarCbu("07200020880000279932801");
        assert.ok(typeof error === "string");
    });

    it("retorna mensaje de error si contiene letras", () => {
        const error = validarCbu("072000208800002799328X");
        assert.ok(typeof error === "string");
    });
});

// ─── validarFormularioCuenta ─────────────────────────────────────────────────

describe("validarFormularioCuenta", () => {
    it("retorna null si tiene CBU + titular + moneda", () => {
        assert.equal(
            validarFormularioCuenta({
                cbu: "0720002088000027993280",
                alias: "",
                holderName: "Magna Travel SA",
                currency: "ARS",
            }),
            null
        );
    });

    it("retorna null si tiene alias + titular + moneda (sin CBU)", () => {
        assert.equal(
            validarFormularioCuenta({
                cbu: "",
                alias: "magna.viajes.sa",
                holderName: "Magna Travel SA",
                currency: "ARS",
            }),
            null
        );
    });

    it("retorna error si no hay ni CBU ni alias", () => {
        const error = validarFormularioCuenta({
            cbu: "",
            alias: "",
            holderName: "Magna Travel SA",
            currency: "ARS",
        });
        assert.ok(typeof error === "string");
        assert.ok(error.toLowerCase().includes("cbu") || error.toLowerCase().includes("alias"));
    });

    it("retorna error si falta el titular", () => {
        const error = validarFormularioCuenta({
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "",
            currency: "ARS",
        });
        assert.ok(typeof error === "string");
        assert.ok(error.toLowerCase().includes("titular"));
    });

    it("retorna error si falta la moneda", () => {
        const error = validarFormularioCuenta({
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "Magna Travel SA",
            currency: "",
        });
        assert.ok(typeof error === "string");
        assert.ok(error.toLowerCase().includes("moneda"));
    });

    it("retorna error de CBU si se ingresó un CBU con formato incorrecto", () => {
        const error = validarFormularioCuenta({
            cbu: "1234",
            alias: "",
            holderName: "Magna Travel SA",
            currency: "ARS",
        });
        assert.ok(typeof error === "string");
        assert.ok(error.includes("22"));
    });
});

// ─── construirPayloadCuentaBancaria ──────────────────────────────────────────

describe("construirPayloadCuentaBancaria", () => {
    it("convierte strings vacíos a null en campos opcionales", () => {
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Supplier",
            ownerId: "abc123",
            bank: "",
            accountType: "",   // sin especificar → null
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "Proveedor SA",
            holderTaxId: "",
            currency: "ARS",
            notes: "",
            isPrimary: false,
        });

        assert.equal(payload.bank, null);
        assert.equal(payload.accountType, null);      // "" → null
        assert.equal(payload.alias, null);
        assert.equal(payload.holderTaxId, null);
        assert.equal(payload.notes, null);
        assert.equal(payload.cbu, "0720002088000027993280");
        assert.equal(payload.holderName, "Proveedor SA");
        assert.equal(payload.currency, "ARS");
        assert.equal(payload.isPrimary, false);
    });

    it("convierte ownerType 'Supplier' al entero 2 (enum backend)", () => {
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Supplier",
            ownerId: "prv-001",
            bank: "HSBC",
            accountType: "",
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "Proveedor SA",
            holderTaxId: "",
            currency: "USD",
            notes: "",
            isPrimary: false,
        });

        assert.equal(payload.ownerType, 2);
    });

    it("convierte ownerType 'Agency' al entero 0", () => {
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Agency",
            ownerId: 0,
            bank: "",
            accountType: "",
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "Magna Travel",
            holderTaxId: "",
            currency: "ARS",
            notes: "",
            isPrimary: false,
        });

        assert.equal(payload.ownerType, 0);
    });

    it("convierte ownerType 'Customer' al entero 1", () => {
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Customer",
            ownerId: "cli-999",
            bank: "",
            accountType: "",
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "Juan Perez",
            holderTaxId: "",
            currency: "ARS",
            notes: "",
            isPrimary: false,
        });

        assert.equal(payload.ownerType, 1);
    });

    it("convierte accountType '0' (select value Caja Ahorro) al entero 0", () => {
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Agency",
            ownerId: 0,
            bank: "Banco Nación",
            accountType: "0",   // string del <select>
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "Magna Travel",
            holderTaxId: "",
            currency: "ARS",
            notes: "",
            isPrimary: false,
        });

        assert.equal(payload.accountType, 0);
        assert.strictEqual(typeof payload.accountType, "number");
    });

    it("convierte accountType '1' (select value Cuenta Corriente) al entero 1", () => {
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Agency",
            ownerId: 0,
            bank: "Banco Galicia",
            accountType: "1",   // string del <select>
            cbu: "0720002088000027993280",
            alias: "",
            holderName: "Magna Travel",
            holderTaxId: "",
            currency: "ARS",
            notes: "",
            isPrimary: false,
        });

        assert.equal(payload.accountType, 1);
        assert.strictEqual(typeof payload.accountType, "number");
    });

    it("elimina espacios internos del CBU antes de enviarlo al backend", () => {
        // Un cajero que tipea el CBU con espacios no debe generar un dato inválido en el servidor.
        // validarCbu acepta espacios (los ignora para validar), pero el payload debe llegar limpio.
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Agency",
            ownerId: 0,
            bank: "",
            accountType: "",
            cbu: "0720 0020 8800 0027 9932 80",  // CBU con espacios
            alias: "",
            holderName: "Magna Travel",
            holderTaxId: "",
            currency: "ARS",
            notes: "",
            isPrimary: false,
        });

        assert.equal(payload.cbu, "0720002088000027993280");
    });

    it("preserva isPrimary=true como booleano y otros campos con valor", () => {
        const payload = construirPayloadCuentaBancaria({
            ownerType: "Agency",
            ownerId: 0,
            bank: "Banco Nación",
            accountType: "0",
            cbu: "0720002088000027993280",
            alias: "magna.viajes",
            holderName: "Magna Travel SA",
            holderTaxId: "30-12345678-9",
            currency: "ARS",
            notes: "Cuenta principal pesos",
            isPrimary: true,
        });

        assert.equal(payload.isPrimary, true);
        assert.equal(payload.bank, "Banco Nación");
        assert.equal(payload.holderTaxId, "30-12345678-9");
        assert.equal(payload.notes, "Cuenta principal pesos");
    });
});

// ─── resolverCuentaPrincipalPorMoneda ────────────────────────────────────────

describe("resolverCuentaPrincipalPorMoneda", () => {
    const cuentas = [
        { publicId: "a1", currency: "ARS", isPrimary: false, isActive: true },
        { publicId: "a2", currency: "ARS", isPrimary: true, isActive: true },
        { publicId: "u1", currency: "USD", isPrimary: true, isActive: true },
        { publicId: "u2", currency: "USD", isPrimary: false, isActive: false }, // inactiva
    ];

    it("retorna la cuenta principal de la moneda indicada", () => {
        const cuenta = resolverCuentaPrincipalPorMoneda(cuentas, "ARS");
        assert.equal(cuenta?.publicId, "a2");
    });

    it("retorna la cuenta activa de USD (ignora la inactiva)", () => {
        const cuenta = resolverCuentaPrincipalPorMoneda(cuentas, "USD");
        assert.equal(cuenta?.publicId, "u1");
    });

    it("retorna null si no hay cuentas de esa moneda", () => {
        const cuenta = resolverCuentaPrincipalPorMoneda(cuentas, "EUR");
        assert.equal(cuenta, null);
    });

    it("retorna null si el array está vacío", () => {
        const cuenta = resolverCuentaPrincipalPorMoneda([], "ARS");
        assert.equal(cuenta, null);
    });

    it("retorna null si cuentas es null", () => {
        const cuenta = resolverCuentaPrincipalPorMoneda(null, "ARS");
        assert.equal(cuenta, null);
    });

    it("retorna la primera de la moneda si ninguna es principal", () => {
        const sinPrincipal = [
            { publicId: "x1", currency: "ARS", isPrimary: false, isActive: true },
            { publicId: "x2", currency: "ARS", isPrimary: false, isActive: true },
        ];
        const cuenta = resolverCuentaPrincipalPorMoneda(sinPrincipal, "ARS");
        assert.equal(cuenta?.publicId, "x1");
    });
});

// ─── clasificarErrorCuenta ───────────────────────────────────────────────────

describe("clasificarErrorCuenta", () => {
    // Caso crítico: un cajero que cobra puede NO tener el permiso configuracion.view.
    // Si el backend devuelve 403, reintentar no sirve de nada: la UI debe desaparecer
    // en silencio para no bloquear el flujo de cobro ni alarmar al usuario.
    it("clasifica HTTP 403 como sin_permiso (no mostrar error rojo)", () => {
        const err = { status: 403 };
        assert.equal(clasificarErrorCuenta(err), "sin_permiso");
    });

    it("clasifica HTTP 500 como recuperable (mostrar Reintentar)", () => {
        const err = { status: 500 };
        assert.equal(clasificarErrorCuenta(err), "recuperable");
    });

    it("clasifica HTTP 503 como recuperable (servidor temporalmente no disponible)", () => {
        const err = { status: 503 };
        assert.equal(clasificarErrorCuenta(err), "recuperable");
    });

    it("clasifica error de red sin status como recuperable", () => {
        const err = new Error("Failed to fetch");
        assert.equal(clasificarErrorCuenta(err), "recuperable");
    });

    it("clasifica null como recuperable (no lanza excepción)", () => {
        assert.equal(clasificarErrorCuenta(null), "recuperable");
    });

    it("clasifica undefined como recuperable (no lanza excepción)", () => {
        assert.equal(clasificarErrorCuenta(undefined), "recuperable");
    });

    it("clasifica HTTP 404 como recuperable (no confundir con falta de permiso)", () => {
        // 404 = recurso no encontrado, no es un problema de permiso. Se muestra Reintentar.
        const err = { status: 404 };
        assert.equal(clasificarErrorCuenta(err), "recuperable");
    });
});
