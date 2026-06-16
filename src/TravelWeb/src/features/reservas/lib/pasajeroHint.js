/**
 * Lógica pura para calcular el "hint" de pasajeros por tipo de servicio.
 *
 * Estas funciones replican en el frontend la MISMA regla que el backend usa
 * para aceptar o rechazar la resolución/emisión de un servicio. No son la
 * autoridad (el backend siempre re-valida), son una PISTA para habilitar/
 * deshabilitar botones y mostrar el mini-formulario inline antes de intentar.
 *
 * Contratos (del spec ADR-031, 2026-06-15):
 *   - Aéreo: N pasajeros con fullName + documentNumber no vacíos.
 *   - Hotel / Traslado: 1er pasajero (titular) con fullName no vacío.
 *   - Asistencia: N pasajeros con fullName + documentNumber + birthDate.
 *   - Paquete / Genérico: N pasajeros con fullName.
 *
 * N = cantidad declarada = adultCount + childCount + infantCount.
 * "pasajeros" son los que ya existen en reserva.passengers (cargados con nombre).
 */

/**
 * Devuelve el total de pasajeros declarados para la reserva.
 *
 * @param {object} reserva - objeto de reserva con adultCount, childCount, infantCount
 * @returns {number}
 */
export function calcularTotalPasajerosDeclarados(reserva) {
    return (reserva?.adultCount || 0) + (reserva?.childCount || 0) + (reserva?.infantCount || 0);
}

/**
 * Verifica si un pasajero tiene nombre cargado.
 * Mínimo: fullName con al menos 1 carácter no-espacio.
 *
 * @param {object} pasajero
 * @returns {boolean}
 */
function tieneNombre(pasajero) {
    return Boolean(pasajero?.fullName?.trim());
}

/**
 * Verifica si un pasajero tiene documento cargado (tipo y número).
 *
 * @param {object} pasajero
 * @returns {boolean}
 */
function tieneDocumento(pasajero) {
    return Boolean(pasajero?.documentNumber?.trim());
}

/**
 * Verifica si un pasajero tiene fecha de nacimiento cargada.
 *
 * @param {object} pasajero
 * @returns {boolean}
 */
function tieneFechaNacimiento(pasajero) {
    return Boolean(pasajero?.birthDate);
}

/**
 * Calcula el hint para un servicio AÉREO.
 *
 * Regla: TODOS los N pasajeros declarados deben tener fullName + documentNumber.
 * Si faltan pasajeros o no tienen nombre/documento → el botón "Marcar emitido" se apaga.
 *
 * @param {object[]} passengers - lista de pasajeros de la reserva (ya cargados)
 * @param {number} totalDeclarado - adultCount + childCount + infantCount
 * @returns {{ listo: boolean, faltanNombres: number, faltanDocumentos: number }}
 */
export function calcularHintAereo(passengers, totalDeclarado) {
    const lista = passengers || [];

    // Si no se declararon pasajeros, el hint dice "no listo" (regla nunca-0 pax).
    if (totalDeclarado === 0) {
        return { listo: false, faltanNombres: 0, faltanDocumentos: 0 };
    }

    // Contamos cuántos de los N declarados tienen nombre y documento.
    // Si hay más pasajeros cargados que los declarados, tomamos solo los N primeros
    // (los extras no generan errores en el backend, pero el hint es conservador).
    const pasajerosActivos = lista.slice(0, totalDeclarado);
    const conNombre = pasajerosActivos.filter(tieneNombre).length;
    const conDocumento = pasajerosActivos.filter(tieneDocumento).length;
    const faltanNombres = totalDeclarado - conNombre;
    const faltanDocumentos = totalDeclarado - conDocumento;

    return {
        listo: faltanNombres === 0 && faltanDocumentos === 0 && lista.length >= totalDeclarado,
        faltanNombres,
        faltanDocumentos,
    };
}

/**
 * Calcula el hint para un servicio de HOTEL o TRASLADO.
 *
 * Regla: solo exige al TITULAR (primer pasajero en la lista) con fullName cargado.
 *
 * @param {object[]} passengers - lista de pasajeros de la reserva
 * @returns {{ listo: boolean, faltaTitular: boolean }}
 */
export function calcularHintHotelTraslado(passengers) {
    const lista = passengers || [];
    const titular = lista[0];

    return {
        listo: Boolean(titular && tieneNombre(titular)),
        faltaTitular: !titular || !tieneNombre(titular),
    };
}

/**
 * Calcula el hint para un servicio de ASISTENCIA.
 *
 * Regla: TODOS los N pasajeros declarados deben tener fullName + documentNumber + birthDate.
 * La asistencia necesita la fecha de nacimiento para la póliza.
 *
 * @param {object[]} passengers - lista de pasajeros
 * @param {number} totalDeclarado
 * @returns {{ listo: boolean, faltanNombres: number, faltanDocumentos: number, faltanFechas: number }}
 */
export function calcularHintAsistencia(passengers, totalDeclarado) {
    const lista = passengers || [];

    if (totalDeclarado === 0) {
        return { listo: false, faltanNombres: 0, faltanDocumentos: 0, faltanFechas: 0 };
    }

    const pasajerosActivos = lista.slice(0, totalDeclarado);
    const faltanNombres = totalDeclarado - pasajerosActivos.filter(tieneNombre).length;
    const faltanDocumentos = totalDeclarado - pasajerosActivos.filter(tieneDocumento).length;
    const faltanFechas = totalDeclarado - pasajerosActivos.filter(tieneFechaNacimiento).length;

    return {
        listo: faltanNombres === 0 && faltanDocumentos === 0 && faltanFechas === 0 && lista.length >= totalDeclarado,
        faltanNombres,
        faltanDocumentos,
        faltanFechas,
    };
}

/**
 * Calcula el hint para un servicio de PAQUETE o GENÉRICO.
 *
 * Regla: TODOS los N pasajeros declarados deben tener fullName cargado.
 * No exige documento ni fecha de nacimiento para estos tipos.
 *
 * @param {object[]} passengers - lista de pasajeros
 * @param {number} totalDeclarado
 * @returns {{ listo: boolean, faltanNombres: number }}
 */
export function calcularHintPaqueteGenerico(passengers, totalDeclarado) {
    const lista = passengers || [];

    if (totalDeclarado === 0) {
        return { listo: false, faltanNombres: 0 };
    }

    const pasajerosActivos = lista.slice(0, totalDeclarado);
    const faltanNombres = totalDeclarado - pasajerosActivos.filter(tieneNombre).length;

    return {
        listo: faltanNombres === 0 && lista.length >= totalDeclarado,
        faltanNombres,
    };
}

/**
 * Calcula el hint correcto según el tipo de servicio (recordKind).
 *
 * Punto de entrada unificado para el ServiceList y los mini-formularios.
 * Devuelve { listo: boolean, ...detalle } donde el detalle varía por tipo.
 *
 * Recordkinds soportados: "flight", "hotel", "transfer", "assistance", "package", "generic".
 * Si el recordKind es desconocido, asume "no listo" de forma conservadora.
 *
 * @param {string} recordKind - tipo del servicio normalizado
 * @param {object[]} passengers - pasajeros cargados en la reserva
 * @param {object} reserva - objeto reserva (para adultCount/childCount/infantCount)
 * @returns {{ listo: boolean, [key: string]: any }}
 */
export function calcularHintPorTipo(recordKind, passengers, reserva) {
    const totalDeclarado = calcularTotalPasajerosDeclarados(reserva);

    switch (recordKind) {
        case "flight":
            return calcularHintAereo(passengers, totalDeclarado);
        case "hotel":
        case "transfer":
            return calcularHintHotelTraslado(passengers);
        case "assistance":
            return calcularHintAsistencia(passengers, totalDeclarado);
        case "package":
        case "generic":
            return calcularHintPaqueteGenerico(passengers, totalDeclarado);
        default:
            // Tipo desconocido: conservador — no habilitar.
            return { listo: false };
    }
}

/**
 * ADR-031 v2.1 — Pieza B: calcular slots faltantes sobre el SET de un servicio.
 *
 * A diferencia de calcularSlotsFaltantes (que trabaja sobre TODOS los pasajeros
 * de la reserva), esta función trabaja sobre el ServiceNominalCoverageDto del backend.
 *
 * El backend ya resolvió quiénes integran el set (hasExplicitAssignments:
 * si es false → todos; si es true → los de serviceSet[]).
 * Esta función solo convierte esa respuesta en la lista de slots que el
 * mini-formulario necesita mostrar.
 *
 * Ventaja: el front no reimplementa la lógica del set — la pide al backend.
 *
 * @param {object} coverage - ServiceNominalCoverageDto del backend
 *   coverage.serviceSet: Array<ServiceSetPassengerDto>
 *   coverage.missingMessage: string|null
 * @param {object[]} pasajerosCompletos - pasajeros completos de la reserva (con fullName, publicId, etc.)
 *   Solo se usa para acceder al objeto completo cuando el coverage da solo publicId.
 * @returns {Array<{ slot: string, passenger: object|null, index: number }>}
 */
export function calcularSlotsFaltantesDelSet(coverage, pasajerosCompletos) {
    // Si coverage aún no llegó del backend, devolvemos vacío (el componente no debe mostrar nada).
    if (!coverage || !coverage.serviceSet) return [];

    // Si el backend dice que la cobertura está completa, no hay slots que mostrar.
    if (coverage.isComplete || !coverage.missingMessage) return [];

    const pasajerosMap = new Map(
        (pasajerosCompletos || []).map(p => [
            String(p.publicId || p.PassengerPublicId || "").toLowerCase(),
            p,
        ])
    );

    // Solo nos interesan los del set que NO tienen los datos requeridos para el tipo.
    const slots = [];
    coverage.serviceSet.forEach((paxEnSet, index) => {
        if (!paxEnSet.hasRequiredDataForServiceType) {
            const publicIdKey = String(paxEnSet.passengerPublicId || "").toLowerCase();
            const pasajeroCompleto = pasajerosMap.get(publicIdKey) || null;

            // La etiqueta del slot se toma del nombre si está cargado, sino del número de orden.
            const etiqueta = paxEnSet.isLead
                ? "Titular"
                : (paxEnSet.fullName?.trim() ? paxEnSet.fullName : `Pasajero ${index + 1}`);

            slots.push({
                slot: etiqueta,
                passenger: pasajeroCompleto,
                index,
            });
        }
    });

    return slots;
}

/**
 * Calcula la composición sugerida de pasajeros mirando los servicios de la reserva.
 *
 * ADR-031 v2.1 — Pieza C: el sistema SUGIERE cuántos adultos/menores/infantes viajan
 * basándose en la información de los servicios cargados. NUNCA pisa lo que el vendedor puso.
 *
 * La fuente de verdad oficial es el TransitionReadinessDto del backend (campos
 * expectedAdults, expectedChildren, expectedInfants, ambiguousComposition).
 * Esta función adapta esos campos a un formato simple para el componente.
 *
 * @param {object|null} readiness - TransitionReadinessDto del backend, o null si no se cargó
 * @param {object} reserva - objeto reserva actual (adultCount, childCount, infantCount)
 * @returns {{ sugerida: boolean, adultos: number, menores: number, infantes: number, ambigua: boolean }|null}
 *   sugerida = false cuando la composición actual ya coincide con la sugerida (no mostrar la franja).
 *   null = no hay información del backend todavía.
 */
export function calcularSugerenciaComposicion(readiness, reserva) {
    if (!readiness) return null;

    const sugeridaAdultos = readiness.expectedAdults || 0;
    const sugeridaMenores = readiness.expectedChildren || 0;
    const sugeridaInfantes = readiness.expectedInfants || 0;

    // Si no hay ninguna sugerencia significativa (todos en 0), no mostramos la franja.
    if (sugeridaAdultos === 0 && sugeridaMenores === 0 && sugeridaInfantes === 0) return null;

    // Comparamos con lo que el vendedor ya tiene cargado.
    const actualAdultos = reserva?.adultCount || 0;
    const actualMenores = reserva?.childCount || 0;
    const actualInfantes = reserva?.infantCount || 0;

    // Si la composición actual ya coincide con la sugerida, no hay nada que sugerir.
    const yaCoincide = (
        actualAdultos === sugeridaAdultos &&
        actualMenores === sugeridaMenores &&
        actualInfantes === sugeridaInfantes
    );

    if (yaCoincide) return null;

    return {
        sugerida: true,
        adultos: sugeridaAdultos,
        menores: sugeridaMenores,
        infantes: sugeridaInfantes,
        ambigua: Boolean(readiness.ambiguousComposition),
    };
}
