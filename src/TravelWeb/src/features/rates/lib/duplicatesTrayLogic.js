/**
 * Lógica pura de la bandeja "Repetidos" (spec firmada 2026-08-07, §6 / V11=B) y del
 * registro "Ver qué ordenó" con Deshacer (Q3=B). Vive separada del JSX para poder
 * testearla con `node --test`.
 */

/**
 * Saca del grupo, en el front, un candidato que se acaba de resolver (unido o marcado
 * "es otro") — así la fila desaparece al toque (spec §9, "Unido OK: el renglón
 * desaparece del grupo") sin esperar a refrescar toda la bandeja contra el servidor.
 *
 * Si el grupo se queda sin candidatos, el grupo entero se saca de la lista (no tiene
 * sentido un producto "arriba" sin nadie parecido debajo).
 *
 * @param {Array<{survivorPublicId: string, candidates: Array<{ratePublicId: string}>}>} groups
 * @param {string} survivorPublicId
 * @param {string} candidateRatePublicId
 */
export function quitarCandidatoResuelto(groups, survivorPublicId, candidateRatePublicId) {
  return groups
    .map((group) => {
      if (group.survivorPublicId !== survivorPublicId) return group;
      return {
        ...group,
        candidates: group.candidates.filter((candidate) => candidate.ratePublicId !== candidateRatePublicId),
      };
    })
    .filter((group) => group.candidates.length > 0);
}

/**
 * Decide si el botón "Deshacer" de una línea del registro se puede tocar. Es un espejo
 * directo de `canUndo` (T-13: la decisión de negocio la manda el motor, el front no
 * inventa reglas de "cuánto tiempo puede pasar" o "quién puede deshacer"), pero se
 * centraliza acá para que un futuro cambio de la regla no obligue a tocar el componente.
 *
 * @param {{canUndo: boolean}} action
 */
export function puedeDeshacerse(action) {
  return Boolean(action?.canUndo);
}

/**
 * Marca una línea del registro como "ya deshecha" SIN esperar a recargar toda la lista
 * contra el servidor (mismo criterio de actualización optimista que
 * `quitarCandidatoResuelto`): apaga su `canUndo` para que el botón "Deshacer" desaparezca
 * de esa fila, pero la fila en sí queda (nada se borra del registro, 2026-08-03).
 *
 * @param {Array<{publicId: string, canUndo: boolean}>} actions
 * @param {string} actionPublicId
 */
export function marcarComoDeshecha(actions, actionPublicId) {
  return actions.map((action) =>
    action.publicId === actionPublicId ? { ...action, canUndo: false } : action
  );
}
