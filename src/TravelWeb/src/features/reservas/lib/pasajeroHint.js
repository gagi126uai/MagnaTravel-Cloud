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
 * Alias legible de calcularHintHotelTraslado para el gate de titular (H7, 2026-07-25):
 * "El cliente aceptó" en ReservaHeader y su validación defensiva en
 * handleConfirmReservation (ReservaDetailPage) solo necesitan esta única pregunta —
 * "¿falta el titular con nombre?" — no el objeto completo de hint por tipo de servicio.
 * Nombrar la función por lo que decide (en vez de por el helper interno que reusa)
 * hace que el gate se entienda leyendo el nombre, sin tener que ir a leer el docstring
 * de calcularHintHotelTraslado. Mismo criterio exacto, cero lógica nueva.
 *
 * @param {object[]} passengers - lista de pasajeros de la reserva
 * @returns {boolean} true si falta el titular con nombre cargado
 */
export function faltaTitularConNombre(passengers) {
    return calcularHintHotelTraslado(passengers).faltaTitular;
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
 * Fix bug reviewer (2026-08, plan tanda F): decide el candado de "Marcar confirmado" para
 * PAQUETE y ASISTENCIA a partir de la coverage REAL del motor (ServiceNominalCoverageDto,
 * ver useServiceNominalCoverage.js), no del cálculo local declarado/cargado de este archivo.
 *
 * Motivo del cambio: calcularHintAsistencia y calcularHintPaqueteGenerico exigían
 * `lista.length >= totalDeclarado` (TODOS los pasajeros DECLARADOS de la reserva cargados),
 * pero el motor (PassengerNominalRules.EnsureCovered, backend) valida el SET RESUELTO por
 * asignaciones — que puede ser más chico que lo declarado (ej: 2 declarados, el servicio
 * está asignado solo al titular). Con el cálculo local, el botón quedaba escondido en casos
 * que el motor aceptaba sin problema. La coverage del backend es la MISMA fuente que ya usa
 * el control "Para: Todos"/"Para: X de N" y el mini-formulario de nombres faltantes — así
 * front y motor nunca discrepan (regla T-13 de la constitución: no duplicar reglas de negocio
 * en el frontend cuando el backend ya expone la verdad).
 *
 * Bug fix P-11 (re-review, plan tanda Q): si el GET de nominal-coverage FALLÓ (no está en
 * loading, terminó en error de red/servidor), coverage queda en `null` para siempre y con
 * la lógica original el casillero se veía mudo (sin botón ni motivo) — el servicio quedaba
 * inconfirmable desde la ficha sin que nadie entienda por qué. Con `huboErrorDeCoverage=true`
 * volvemos al comportamiento reactivo de antes de esta obra: mostramos el botón igual, y el
 * motor valida al hacer clic (rechaza con su propio mensaje si de verdad falta un dato).
 *
 * @param {object|null} coverage - ServiceNominalCoverageDto del backend, o null si aún no llegó
 * @param {boolean} [huboErrorDeCoverage=false] - true si el GET de coverage terminó en error
 *   (no confundir con loading: mientras está cargando, coverage también es null pero este
 *   flag debe quedar en false).
 * @returns {{ mostrarBoton: boolean, texto: string|null }}
 *   mostrarBoton=true            → coverage completa, o el GET falló (fallback reactivo):
 *                                   mostrar el botón de resolver.
 *   mostrarBoton=false,texto=null → coverage todavía no llegó (loading, sin error): no mostrar
 *                                   nada todavía, para no arriesgar un motivo que después cambie.
 *   mostrarBoton=false,texto=... → falta algo: el texto es EXACTAMENTE el que lanzaría el
 *                                   motor al intentar resolver (mismo mensaje, sin adivinar).
 */
export function calcularCandadoPorCoverage(coverage, huboErrorDeCoverage = false) {
    if (!coverage) {
        if (huboErrorDeCoverage) {
            return { mostrarBoton: true, texto: null };
        }
        return { mostrarBoton: false, texto: null };
    }

    if (coverage.isComplete) {
        return { mostrarBoton: true, texto: null };
    }

    return {
        mostrarBoton: false,
        texto: coverage.missingMessage || "Faltan datos de pasajeros para confirmar el servicio.",
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
