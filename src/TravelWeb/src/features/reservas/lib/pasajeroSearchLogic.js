/**
 * Lógica pura de la búsqueda de pasajeros históricos de la agencia.
 *
 * Está separada del componente (sin JSX, sin hooks) para que los tests
 * puedan importarla directamente con Node sin transpiler.
 *
 * Se usa desde PassengerFormModal cuando el usuario tipea en nombre o documento.
 */

/** Cantidad mínima de caracteres para disparar la búsqueda. */
export const MINIMO_CHARS_BUSQUEDA = 3;

/**
 * Decide si el texto ingresado cumple el umbral mínimo para hacer una búsqueda.
 *
 * @param {string} texto - Lo que escribió el usuario en nombre o documento.
 * @returns {boolean}
 */
export function cumpleUmbralBusqueda(texto) {
    return typeof texto === "string" && texto.trim().length >= MINIMO_CHARS_BUSQUEDA;
}

/**
 * Construye la URL de búsqueda según el campo que disparó la búsqueda.
 *
 * El backend acepta cualquier combinación de parámetros; sin parámetros devuelve [].
 * fullName y documentNumber/documentType se envían por separado para que el backend
 * pueda rankear mejor (el campo nombre pesa distinto que el número de documento).
 *
 * @param {"name" | "document"} campo - Qué campo del form disparó la búsqueda.
 * @param {{ fullName: string, documentType: string, documentNumber: string }} formData
 * @param {number} take - Máximo de resultados a pedir (sugerimos 5).
 * @returns {string} path relativo para api.get(...)
 */
export function construirUrlBusquedaHistorica(campo, formData, take = 5) {
    const params = new URLSearchParams();

    if (campo === "name" && formData.fullName) {
        params.set("fullName", formData.fullName.trim());
    }

    if (campo === "document" && formData.documentNumber) {
        params.set("documentNumber", formData.documentNumber.trim());
        if (formData.documentType) {
            params.set("documentType", formData.documentType);
        }
    }

    params.set("take", String(take));

    return `/passengers/search-similar?${params.toString()}`;
}

/**
 * Convierte una sugerencia del backend al formato del formulario del modal.
 *
 * El backend devuelve:
 *   { fullName, documentType, documentNumber, birthDate, nationality, gender,
 *     phone, email, passportExpiry, usageCount, score }
 *
 * Los campos del form son los mismos pero el modal tiene "passportExpiry" mapeado
 * como campo extra (por ahora no tiene input visible, pero lo guardamos).
 *
 * @param {object} sugerencia - Un ítem de la respuesta del backend.
 * @returns {object} Campos listos para setFormData en el modal.
 */
export function mapearSugerenciaAlForm(sugerencia) {
    return {
        fullName: sugerencia.fullName || "",
        documentType: sugerencia.documentType || "DNI",
        documentNumber: sugerencia.documentNumber || "",
        // El backend devuelve birthDate como ISO string o null
        birthDate: sugerencia.birthDate ? sugerencia.birthDate.split("T")[0] : "",
        nationality: sugerencia.nationality || "",
        gender: sugerencia.gender || "M",
        phone: sugerencia.phone || "",
        email: sugerencia.email || "",
        // Las notas no se copian desde el histórico (son propias de cada viaje)
        notes: "",
    };
}

/**
 * Decide si la sugerencia elegida ya está cargada como pasajero en la reserva actual.
 *
 * La detección se hace por tipo+número de documento (combinación más confiable).
 * Si el modal no tiene la lista de pasajeros de la reserva, devuelve false
 * y la validación queda a cargo del backend.
 *
 * @param {object} sugerencia - La sugerencia que eligió el usuario.
 * @param {Array} pasajerosExistentes - Pasajeros ya cargados en la reserva.
 * @returns {boolean}
 */
export function esDuplicadoEnReserva(sugerencia, pasajerosExistentes) {
    if (!Array.isArray(pasajerosExistentes) || pasajerosExistentes.length === 0) {
        return false;
    }

    const docNuevo = (sugerencia.documentNumber || "").trim().toLowerCase();
    const tipoNuevo = (sugerencia.documentType || "").trim().toLowerCase();

    if (!docNuevo) return false;

    return pasajerosExistentes.some((pax) => {
        const docExistente = (pax.documentNumber || "").trim().toLowerCase();
        const tipoExistente = (pax.documentType || "").trim().toLowerCase();
        return docExistente === docNuevo && tipoExistente === tipoNuevo;
    });
}

/**
 * Formatea el texto secundario de una sugerencia en el dropdown.
 *
 * Ej: "DNI 30123456 · viajó en 3 reservas"
 *
 * @param {object} sugerencia
 * @returns {string}
 */
export function formatearSubtituloSugerencia(sugerencia) {
    const partes = [];

    if (sugerencia.documentType && sugerencia.documentNumber) {
        partes.push(`${sugerencia.documentType} ${sugerencia.documentNumber}`);
    }

    if (typeof sugerencia.usageCount === "number" && sugerencia.usageCount > 0) {
        const reservas = sugerencia.usageCount === 1 ? "reserva" : "reservas";
        partes.push(`viajó en ${sugerencia.usageCount} ${reservas}`);
    }

    return partes.join(" · ");
}
