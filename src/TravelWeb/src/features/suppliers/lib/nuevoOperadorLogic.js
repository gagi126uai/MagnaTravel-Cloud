/**
 * Lógica pura para el alta de un operador nuevo.
 *
 * Separada del componente para facilitar el testeo sin montar React.
 * Cubre: validación del formulario y construcción del payload para POST /suppliers.
 */

/**
 * Opciones de condición fiscal del operador.
 * Las claves son los valores que espera el backend; los labels son lo que ve el usuario.
 *
 * Regla: nunca exponer los valores internos (IVA_RESP_INSCRIPTO, etc.) directamente.
 * El usuario ve "Resp. Inscripto", nunca "IVA_RESP_INSCRIPTO".
 */
export const TAX_CONDITION_OPTIONS = [
    { value: "IVA_RESP_INSCRIPTO", label: "Resp. Inscripto" },
    { value: "MONOTRIBUTISTA", label: "Monotributista" },
    { value: "IVA_EXENTO", label: "Exento" },
    { value: "CONSUMIDOR_FINAL", label: "Cons. Final" },
];

/**
 * Opciones de moneda por defecto del operador.
 * "ARS" = Pesos ($), "USD" = Dólares (US$).
 *
 * Regla multimoneda dura: nunca sumarlas ni convertirlas.
 * Son dos carriles independientes en la cuenta corriente.
 */
export const CURRENCY_OPTIONS = [
    { value: "ARS", label: "Pesos ($)" },
    { value: "USD", label: "Dólares (US$)" },
];

/**
 * Estado inicial vacío del formulario de alta.
 * La moneda arranca en ARS (Pesos) por decisión del dueño (spec P9=A, 2026-06-28).
 */
export const FORM_INICIAL = {
    name: "",
    defaultCurrency: "ARS",
    taxId: "",
    taxCondition: "",
    contactName: "",
    phone: "",
    email: "",
    address: "",
};

/**
 * Valida el formulario antes de intentar crear el operador.
 *
 * Reglas de obligatoriedad (spec 2026-06-28):
 *   - Razón social y moneda por defecto son siempre obligatorias.
 *   - CUIT y condición fiscal son obligatorias SALVO que el toggle
 *     "datos fiscales pendientes" esté activo (fiscalDataPending = true).
 *     Cuando el toggle está activo, el operador queda fiscalmente incompleto;
 *     el freno duro al primer pago lo pone el backend, no esta pantalla.
 *
 * @param {object} formData - campos del formulario
 * @param {boolean} fiscalDataPending - true si el toggle "datos fiscales pendientes" está activo
 * @returns {string|null} - mensaje de error en español rioplatense, o null si el form es válido
 */
export function validarNuevoOperador(formData, fiscalDataPending) {
    if (!formData.name?.trim()) {
        return "La razón social es obligatoria.";
    }

    if (!formData.defaultCurrency) {
        return "La moneda por defecto es obligatoria.";
    }

    // Con datos fiscales pendientes activo, CUIT y condición no frenan el guardado.
    // El operador queda marcado como fiscalmente incompleto y el backend
    // bloqueará el primer pago hasta que se completen.
    if (!fiscalDataPending) {
        if (!formData.taxId?.trim()) {
            return 'El CUIT es obligatorio. Si no lo tenés a mano, tildá "Datos fiscales pendientes".';
        }
        if (!formData.taxCondition) {
            return 'La condición fiscal es obligatoria. Si no la tenés a mano, tildá "Datos fiscales pendientes".';
        }
    }

    return null;
}

/**
 * Construye el payload para POST /suppliers.
 *
 * Solo incluye los campos no vacíos para no enviar strings vacíos al backend.
 * El toggle "datos fiscales pendientes" solo afecta la validación del front;
 * si el usuario escribió algo en CUIT o condición, se envía igual.
 *
 * @param {object} formData - campos del formulario (ya validados)
 * @returns {object} - objeto listo para enviar como body del POST
 */
export function construirPayloadNuevoOperador(formData) {
    const payload = {
        name: formData.name.trim(),
        defaultCurrency: formData.defaultCurrency,
    };

    if (formData.taxId?.trim()) {
        payload.taxId = formData.taxId.trim();
    }
    if (formData.taxCondition) {
        payload.taxCondition = formData.taxCondition;
    }
    if (formData.contactName?.trim()) {
        payload.contactName = formData.contactName.trim();
    }
    if (formData.phone?.trim()) {
        payload.phone = formData.phone.trim();
    }
    if (formData.email?.trim()) {
        payload.email = formData.email.trim();
    }
    if (formData.address?.trim()) {
        payload.address = formData.address.trim();
    }

    return payload;
}
