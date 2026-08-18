/**
 * Lógica pura del formulario en línea de pasajero (PasajeroInlineForm).
 *
 * Separada del componente (sin JSX, sin hooks) para poder testearla directo con
 * Node, mismo criterio que pasajeroSearchLogic.js — ver ese archivo para el
 * patrón de tests.
 */

// Campos que viven detrás de "+ Más detalles" cuando el formulario tiene las
// funciones completas (prop conFuncionesCompletas=true en PasajeroInlineForm).
const CAMPOS_MAS_DETALLES = [
    "birthDate",
    "passportExpiry",
    "documentExpiry",
    "nationality",
    "gender",
    "phone",
    "email",
    "notes",
];

/**
 * Decide si la sección "+ Más detalles" arranca abierta o plegada al editar un
 * pasajero ya cargado.
 *
 * Regla (spec T5, sección 3.1): plegada por defecto, salvo que el pasajero que
 * se está editando ya tenga alguno de esos datos cargados — no tiene sentido
 * esconder un dato que la persona ya completó antes.
 *
 * "gender" queda afuera de este chequeo a propósito: el backend siempre le pone
 * un valor por defecto (M/F/X), así que TODO pasajero "tiene género" aunque
 * nadie lo haya elegido a mano. Si lo contáramos, la sección arrancaría abierta
 * para cualquier pasajero editado, rompiendo la idea de "plegado salvo que haya
 * datos".
 *
 * @param {object|null} passengerToEdit - pasajero que se está editando, o null si es alta.
 * @returns {boolean}
 */
export function debeAbrirMasDetallesPorDefecto(passengerToEdit) {
    if (!passengerToEdit) return false;

    return CAMPOS_MAS_DETALLES
        .filter((campo) => campo !== "gender")
        .some((campo) => Boolean(passengerToEdit[campo]));
}

/**
 * Arma el payload que se manda al backend al guardar un pasajero (POST o PUT).
 *
 * Con conFuncionesCompletas=true, los campos de "+ Más detalles" viajan desde el
 * formulario (lo que el usuario cargó en esta sesión, incluida la sección
 * plegada). Sin la prop (uso histórico desde ServiceList, red de seguridad al
 * resolver un servicio), esos campos ni siquiera están en pantalla — se
 * preservan tal cual del pasajero existente para no pisarlos con null al
 * guardar solo nombre/documento.
 *
 * @param {object} params
 * @param {object} params.form - estado del formulario (fullName, documentType, documentNumber, birthDate, y
 *   si conFuncionesCompletas: passportExpiry, documentExpiry, nationality, gender, phone, email, notes).
 * @param {boolean} params.conFuncionesCompletas
 * @param {object|null} params.passengerToEdit - pasajero existente (null si es alta), usado como fuente de
 *   los campos "de más detalles" cuando conFuncionesCompletas=false.
 * @returns {object} payload listo para api.post/api.put
 */
export function construirPayloadPasajero({ form, conFuncionesCompletas, passengerToEdit }) {
    const payloadBase = {
        fullName: form.fullName.trim(),
        documentType: form.documentType,
        documentNumber: form.documentNumber.trim() || null,
        birthDate: form.birthDate || null,
    };

    if (conFuncionesCompletas) {
        return {
            ...payloadBase,
            passportExpiry: form.passportExpiry || null,
            documentExpiry: form.documentExpiry || null,
            nationality: form.nationality.trim() || null,
            gender: form.gender || null,
            phone: form.phone.trim() || null,
            email: form.email.trim() || null,
            notes: form.notes.trim() || null,
        };
    }

    // Modo reducido (comportamiento histórico del inline, ver ServiceList.jsx): estos
    // campos no están en pantalla, así que se preservan del pasajero existente en vez
    // de mandarse null (evita borrar un dato que se cargó antes desde el modal/formulario
    // completo).
    return {
        ...payloadBase,
        nationality: passengerToEdit?.nationality || null,
        phone: passengerToEdit?.phone || null,
        email: passengerToEdit?.email || null,
        gender: passengerToEdit?.gender || null,
        notes: passengerToEdit?.notes || null,
    };
}
