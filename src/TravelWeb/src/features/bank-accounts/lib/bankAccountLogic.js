/**
 * Lógica pura para cuentas bancarias (Agency / Customer / Supplier).
 *
 * Separado del componente para que sea testeable con node:test sin DOM.
 * Cubrir aquí: máscaras de CBU/alias, validaciones de formulario,
 * construcción del payload hacia el backend.
 */

/**
 * Enum OwnerType del backend — el backend NO tiene JsonStringEnumConverter,
 * espera enteros. Estas constantes son la fuente de verdad del mapping.
 * Usar siempre OWNER_TYPE.X cuando se envía ownerType al backend (body o query).
 */
export const OWNER_TYPE = {
    Agency: 0,
    Customer: 1,
    Supplier: 2,
};

/**
 * Enum AccountType del backend (enteros, NO strings).
 *   - CajaAhorro = 0
 *   - CuentaCorriente = 1
 * El select del formulario almacena el valor como string ("0"|"1"|"") para
 * compatibilidad con <select>; se convierte a int en construirPayloadCuentaBancaria.
 */
export const ACCOUNT_TYPE = {
    CajaAhorro: 0,
    CuentaCorriente: 1,
};

/**
 * Enmascara un CBU mostrando solo los últimos 4 dígitos.
 * Ejemplo: "0720002088000027993280" → "····3280"
 * Si el CBU viene vacío o null, retorna cadena vacía.
 */
export function maskCbu(cbu) {
    if (!cbu || typeof cbu !== "string") return "";
    const clean = cbu.trim();
    if (clean.length <= 4) return clean;
    return "····" + clean.slice(-4);
}

/**
 * Enmascara un alias mostrando solo el último segmento (después del último punto).
 * Si no hay punto, muestra los últimos 8 caracteres.
 * Ejemplo: "magna.viajes.sa" → "····.sa"
 *          "magnaviajes"     → "····iajes"
 *
 * Razón: los alias bancarios argentinos tienen formato palabra.palabra.palabra.
 * Mostrar solo el último segmento es suficiente para reconocerlo sin exponer el dato completo.
 */
export function maskAlias(alias) {
    if (!alias || typeof alias !== "string") return "";
    const clean = alias.trim();
    const lastDotIndex = clean.lastIndexOf(".");
    if (lastDotIndex !== -1 && lastDotIndex < clean.length - 1) {
        return "····" + clean.slice(lastDotIndex);
    }
    if (clean.length <= 8) return clean;
    return "····" + clean.slice(-8);
}

/**
 * Valida que un CBU tenga exactamente 22 dígitos numéricos.
 * Si el campo viene vacío (CBU es opcional), retorna null (sin error).
 * Retorna null si es válido, o un string con el mensaje de error.
 */
export function validarCbu(cbu) {
    if (!cbu || !cbu.trim()) return null;
    const clean = cbu.trim().replace(/\s/g, "");
    if (!/^\d{22}$/.test(clean)) {
        return "El CBU tiene que tener 22 dígitos.";
    }
    return null;
}

/**
 * Valida los campos obligatorios antes de guardar una cuenta bancaria.
 * Regla de negocio (2026-06-28): obligatorios = (alias O CBU) + titular + moneda.
 * Todo lo demás es opcional.
 *
 * Retorna null si el formulario está completo, o un string con el mensaje de error.
 */
export function validarFormularioCuenta({ cbu, alias, holderName, currency }) {
    const tieneCbu = Boolean(cbu?.trim());
    const tieneAlias = Boolean(alias?.trim());

    if (!tieneCbu && !tieneAlias) {
        return "Tenés que completar al menos el CBU o el alias.";
    }

    if (!holderName?.trim()) {
        return "El titular es obligatorio.";
    }

    if (!currency) {
        return "La moneda es obligatoria.";
    }

    // Si ingresaron CBU, validar formato de 22 dígitos (frenar el guardado)
    const errorCbu = validarCbu(cbu);
    if (errorCbu) return errorCbu;

    return null;
}

/**
 * Construye el payload para POST /api/bank-accounts o PUT /api/bank-accounts/{id}.
 *
 * Conversiones críticas (backend espera enteros, no strings):
 *   - ownerType: recibe "Agency"/"Customer"/"Supplier" → convierte a 0/1/2 con OWNER_TYPE
 *   - accountType: recibe ""/"0"/"1" del <select> → convierte a null/0/1
 *   - Strings vacíos → null para que el backend los ignore
 *   - isPrimary como boolean
 */
export function construirPayloadCuentaBancaria({
    ownerType,
    ownerId,
    bank,
    accountType,
    cbu,
    alias,
    holderName,
    holderTaxId,
    currency,
    notes,
    isPrimary,
}) {
    // ownerType: string ("Agency"|"Customer"|"Supplier") → int (0|1|2)
    const ownerTypeInt = OWNER_TYPE[ownerType] ?? 0;

    // accountType: string (""/"0"/"1") del select → int o null
    const accountTypeInt = (accountType !== "" && accountType != null)
        ? Number(accountType)
        : null;

    return {
        ownerType: ownerTypeInt,
        ownerId,
        bank: bank?.trim() || null,
        accountType: accountTypeInt,
        // Strip de espacios internos además del trim: un CBU con espacios ("0720 0020…")
        // debe llegar al backend limpio ("0720002088000027993280"). validarCbu ya acepta
        // este formato pero construirPayloadCuentaBancaria debe enviarlo limpio.
        cbu: cbu?.replace(/\s+/g, "") || null,
        alias: alias?.trim() || null,
        holderName: holderName?.trim() || null,
        holderTaxId: holderTaxId?.trim() || null,
        currency,
        notes: notes?.trim() || null,
        isPrimary: Boolean(isPrimary),
    };
}

/**
 * Clasifica el error de carga de cuentas bancarias para decidir cómo reaccionar en la UI.
 *
 * Distinción clave:
 *   "sin_permiso"  — HTTP 403: el usuario no tiene el permiso necesario para leer cuentas
 *                    (ej.: un cajero que cobra no tiene `configuracion.view`).
 *                    La UI debe desaparecer silenciosamente — NO mostrar rojo ni Reintentar,
 *                    porque reintentar no va a cambiar nada: el backend va a seguir
 *                    respondiendo 403 mientras no cambie el permiso del usuario.
 *   "recuperable"  — Red, timeout, 500, 503 u otro error transitorio.
 *                    El intento puede tener éxito si se reintenta → mostrar "Reintentar".
 *
 * @param {Error|object} err — error capturado en el catch de la llamada a la API
 * @returns {"sin_permiso"|"recuperable"}
 */
export function clasificarErrorCuenta(err) {
    if (err?.status === 403) return "sin_permiso";
    return "recuperable";
}

/**
 * Dado un array de cuentas bancarias, retorna la principal de una moneda.
 * Si no hay ninguna marcada como principal, retorna la primera de esa moneda.
 * Si no hay ninguna de esa moneda, retorna null.
 *
 * @param {Array} cuentas - lista de cuentas del backend (isActive, isPrimary, currency)
 * @param {string} currency - "ARS" | "USD"
 */
export function resolverCuentaPrincipalPorMoneda(cuentas, currency) {
    if (!Array.isArray(cuentas) || !currency) return null;
    const deEstaMoneda = cuentas.filter((c) => c.isActive !== false && c.currency === currency);
    if (deEstaMoneda.length === 0) return null;
    return deEstaMoneda.find((c) => c.isPrimary) ?? deEstaMoneda[0];
}
