/**
 * Lógica PURA del estado de carga de "Ajuste por el dólar" en la ficha del operador
 * (ADR-044 T4, fix F2 del gate de frontend, 2026-07-10).
 *
 * El bug real: `SupplierInlineEditForm` buscaba el valor REAL de
 * `treasuryFxAssumedByOverride` con un fetch aparte (el overview de la cuenta no lo
 * trae). Un `finally` incondicional reactivaba el botón "Guardar" aunque el fetch
 * hubiera fallado — y como el <select> seguía en su valor inicial ("hereda la config
 * general"), guardar en ese momento pisaba con `null` una excepción real ya cargada
 * para ese operador, sin que nadie se enterara.
 *
 * Esta función es el ÚNICO lugar que decide qué hacer con el resultado del fetch —
 * el componente la llama y no repite la regla, así nunca puede volver a divergir.
 */

/**
 * Deriva el nuevo estado del campo a partir del resultado de la carga.
 *
 * Regla dura: en error, `cargandoOverride` se queda en `true` (bloquea el submit del
 * formulario ENTERO hasta que un reintento cargue el valor real). El resultado NO
 * incluye `treasuryFxOverrideSelect` en la rama de error A PROPÓSITO: el llamador no
 * debe tocar ese estado ahí — se queda con el valor que ya tenía (nunca se resetea a
 * un valor por defecto que podría no ser el real). En éxito, sí se apaga
 * `cargandoOverride` y se aplica el valor nuevo.
 *
 * @param {{ exito: boolean, selectValueNuevo?: string|number, errorMessage?: string }} resultado
 * @returns {{ cargandoOverride: boolean, errorCargaOverride: string|null, treasuryFxOverrideSelect?: string|number }}
 */
export function siguienteEstadoTreasuryFxOverride({ exito, selectValueNuevo, errorMessage }) {
  if (exito) {
    return {
      cargandoOverride: false,
      errorCargaOverride: null,
      treasuryFxOverrideSelect: selectValueNuevo,
    };
  }

  return {
    cargandoOverride: true,
    errorCargaOverride: errorMessage || "No se pudo cargar la configuración del ajuste por el dólar. Reintentá antes de guardar.",
    // Sin treasuryFxOverrideSelect: el llamador no debe pisarlo en este camino.
  };
}

/**
 * True si el formulario puede enviarse en lo que respecta a este campo puntual — con
 * la carga todavía en curso, o trabada en error, el submit del form ENTERO queda
 * bloqueado (no solo este campo): mandar el PUT sin conocer el valor real arriesga
 * pisar una excepción real con `null` (el bug de F2).
 *
 * @param {boolean} cargandoOverride
 * @returns {boolean}
 */
export function puedeGuardarConTreasuryFxOverride(cargandoOverride) {
  return !cargandoOverride;
}
