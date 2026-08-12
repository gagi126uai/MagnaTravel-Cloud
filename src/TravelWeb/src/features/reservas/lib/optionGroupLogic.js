/**
 * Reglas de "Opciones A/B/C" del lado del frontend (spec docs/ux/2026-08-12-spec-pdf-presupuesto-ui.md,
 * §3). Un servicio puede marcarse como ALTERNATIVA de otro ya cargado (ej. "Hotel Riu Cancún" vs
 * "Hotel Barceló Cancún" para el mismo tramo) — mientras haya 2 o más alternativas VIVAS con el mismo
 * `optionGroup`, ese grupo queda AMBIGUO: no sabemos todavía cuál eligió el cliente.
 *
 * Estas funciones son PURAS (sin fetch, sin estado) a propósito: las usan tanto
 * `ServiceInlineCard.jsx` (armar el grupo/letra al marcar un servicio como alternativa) como
 * `ServiceList.jsx` (chip "OPCIÓN A/B/C" + banner + acción "Elegir esta opción"). Mismo criterio de
 * "grupo ambiguo" que el motor (`OptionGroupRules.cs`, backend) — si algún día se desalinean, el chip
 * podría mostrar algo distinto de lo que el motor realmente bloquea.
 */

import { getReservationServicePublicId } from "./reservationServiceModel.js";

const LETRAS = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

/** Letra por posición (0 → "A", 1 → "B", ...). Más allá de la Z (rarísimo) usa el número. */
export function letraDeOpcionPorIndice(indice) {
  return LETRAS[indice] || String(indice + 1);
}

/** Recorta espacios; vacío/null → "" (nunca null, para poder comparar con seguridad). */
export function normalizarOptionGroup(texto) {
  if (!texto) return "";
  return String(texto).trim();
}

function claveDeGrupo(texto) {
  return normalizarOptionGroup(texto).toLowerCase();
}

/**
 * Mismo criterio que `esServicioAnulado` de ServiceList.jsx: un servicio ANULADO deja de "competir"
 * por su grupo (igual que en el motor, WorkflowStatusHelper.CountsForQuotedTotal). Se duplica acá
 * (una línea) en vez de importar de ServiceList.jsx para no crear una dependencia lib → componente.
 */
export function esServicioVivoParaOpciones(servicio) {
  return (servicio?.workflowStatus || servicio?.status) !== "Cancelado";
}

/** Clave única de un servicio normalizado, para usar como `value` de un <option> o de un Map. */
export function construirClaveServicio(servicio) {
  return `${servicio?.recordKind || ""}|${getReservationServicePublicId(servicio)}`;
}

/**
 * Agrupa los servicios VIVOS que tienen `optionGroup` cargado. Devuelve un Map cuya clave es el
 * nombre del grupo en minúscula (comparación case-insensitive, igual que el backend) y cuyo valor es
 * `{ nombreVisible, miembros }` — `nombreVisible` es el texto TAL CUAL está guardado (respeta
 * mayúsculas), `miembros` es la lista de servicios normalizados de ese grupo.
 */
export function agruparServiciosPorOpcion(servicios) {
  const grupos = new Map();
  for (const servicio of servicios || []) {
    if (!esServicioVivoParaOpciones(servicio)) continue;
    const nombreVisible = normalizarOptionGroup(servicio.optionGroup);
    if (!nombreVisible) continue;
    const clave = claveDeGrupo(nombreVisible);
    if (!grupos.has(clave)) {
      grupos.set(clave, { nombreVisible, miembros: [] });
    }
    grupos.get(clave).miembros.push(servicio);
  }
  return grupos;
}

/**
 * Solo los grupos AMBIGUOS (2+ alternativas vivas) — un grupo con 1 sola alternativa ya no es una
 * "opción pendiente", es EL servicio (ver spec §3.2, último párrafo).
 */
export function obtenerGruposDeOpcionesPendientes(servicios) {
  const pendientes = new Map();
  for (const [clave, grupo] of agruparServiciosPorOpcion(servicios)) {
    if (grupo.miembros.length > 1) pendientes.set(clave, grupo);
  }
  return pendientes;
}

/** El grupo pendiente (2+ miembros) al que pertenece este servicio, o `null` si no está en ninguno. */
export function grupoPendienteDeServicio(servicio, gruposPendientes) {
  const clave = claveDeGrupo(servicio?.optionGroup);
  if (!clave) return null;
  return gruposPendientes.get(clave) || null;
}

/**
 * Letra que le corresponde a este servicio dentro de su grupo. Preferimos `optionLabel` (lo que
 * guardó el backend al crear/actualizar el servicio) — solo si faltara (dato viejo/incompleto)
 * calculamos una letra de respaldo según la posición dentro del grupo, para no dejar el chip vacío.
 */
export function letraDeOpcion(servicio, grupo) {
  const letraGuardada = (servicio?.optionLabel || "").trim().toUpperCase();
  if (letraGuardada) return letraGuardada;
  const posicion = (grupo?.miembros || []).findIndex(
    (miembro) => construirClaveServicio(miembro) === construirClaveServicio(servicio)
  );
  return posicion >= 0 ? letraDeOpcionPorIndice(posicion) : "?";
}

/** Cuenta cuántos servicios VIVOS ya pertenecen a un grupo (excluyendo, si se pide, uno puntual). */
export function contarMiembrosVivosDelGrupo(grupoTexto, servicios, publicIdAExcluir) {
  const clave = claveDeGrupo(grupoTexto);
  if (!clave) return 0;
  return (servicios || []).filter((servicio) => {
    if (publicIdAExcluir && getReservationServicePublicId(servicio) === publicIdAExcluir) return false;
    if (!esServicioVivoParaOpciones(servicio)) return false;
    return claveDeGrupo(servicio.optionGroup) === clave;
  }).length;
}

/**
 * Decisión #6 firmada (2026-08-12): cuando el vendedor marca un servicio NUEVO (o en edición) como
 * "alternativa de" un servicio YA cargado (`servicioSocio`):
 *   - Si el socio YA tiene `optionGroup`, el nuevo servicio se suma a ESE grupo con la letra siguiente.
 *   - Si el socio todavía es un servicio "normal" (sin grupo), el nombre visible del socio (tal como
 *     se ve en la lista, ej. "Hotel Riu Cancún") pasa a ser el nombre del grupo — y hay que
 *     backfillear el PROPIO socio con optionGroup + optionLabel "A" (se hace con un PUT aparte,
 *     ver ServiceInlineCard.jsx → actualizarOptionGroupDelSocio).
 *
 * Devuelve el optionGroup/optionLabel que le corresponden al servicio NUEVO, más los datos que hacen
 * falta para saber si hay que backfillear al socio.
 */
export function calcularAsignacionDeOpcion({ servicioSocio, todosLosServicios, publicIdAExcluir }) {
  const socioYaTieneGrupo = Boolean(normalizarOptionGroup(servicioSocio?.optionGroup));
  const grupoTexto = socioYaTieneGrupo
    ? normalizarOptionGroup(servicioSocio.optionGroup)
    : normalizarOptionGroup(servicioSocio?.name);

  const miembrosActuales = contarMiembrosVivosDelGrupo(grupoTexto, todosLosServicios, publicIdAExcluir);
  // Si el socio todavía no tenía grupo, lo contamos como si YA fuera el primer miembro (letra A) —
  // el PUT que se lo asigna de verdad recién se dispara después, pero necesitamos la cuenta "como si
  // ya estuviera" para no repetirle la misma letra al servicio nuevo.
  const totalConSocio = socioYaTieneGrupo ? miembrosActuales : miembrosActuales + 1;

  return {
    optionGroup: grupoTexto,
    optionLabel: letraDeOpcionPorIndice(totalConSocio),
    socioNecesitaBackfill: !socioYaTieneGrupo,
    socioOptionLabel: letraDeOpcionPorIndice(0), // El socio siempre es la primera opción del grupo nuevo: "A".
  };
}

/**
 * Mensaje del banner ámbar (spec §3.2) arriba de las filas de un grupo pendiente. Pluraliza el
 * conteo de "las otras N opciones" según cuántos miembros compiten con el ganador.
 */
export function mensajeBannerGrupoPendiente(grupo) {
  const otras = Math.max((grupo?.miembros?.length || 0) - 1, 0);
  const plural = otras === 1 ? "opción" : "opciones";
  const verbo = otras === 1 ? "se anula" : "se anulan";
  return `Elegí cuál se confirma para "${grupo?.nombreVisible || ""}" — las otras ${otras} ${plural} ${verbo}.`;
}

/** Mensaje del mini-confirm en línea (spec §3.2, "¿Esta es la que el cliente eligió?"). */
export function mensajeConfirmarEleccionDeOpcion(grupo) {
  const otras = Math.max((grupo?.miembros?.length || 0) - 1, 0);
  const plural = otras === 1 ? "opción" : "opciones";
  const verbo = otras === 1 ? "se anula" : "se anulan";
  return `¿Esta es la que el cliente eligió? Las otras ${otras} ${plural} ${verbo}.`;
}

/**
 * Detecta el rechazo puntual "queda un grupo de opciones sin resolver" que tira el motor al intentar
 * "El cliente aceptó" (ReservaService.EnsureNoAmbiguousOptionGroupsAsync, backend). El motor NO manda
 * un código de negocio para este caso (a diferencia de otros rechazos de la app) — el único dato
 * estable es el texto, que siempre arranca igual: "Elegí qué opción quedó de {grupo} antes de
 * confirmar." Si el texto del motor cambiara algún día, esta función simplemente deja de reconocerlo
 * y el aviso cae al camino genérico (toast sin botón "Ver las opciones") — degradación segura, no rompe nada.
 */
export function esRechazoPorOpcionesSinResolver(mensaje) {
  return typeof mensaje === "string" && mensaje.startsWith("Elegí qué opción quedó de");
}
