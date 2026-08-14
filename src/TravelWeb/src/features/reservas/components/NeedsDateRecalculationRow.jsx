import { useState } from 'react';
import { api } from '../../../api';
import { showSuccess, showError } from '../../../alerts';
import { getApiErrorMessage } from '../../../lib/errors';
import { AvisoFila } from './AvisoFila';

/**
 * Renglón ámbar "Falta revisar las fechas del viaje" + botón "Volver a calcular
 * las fechas" (ADR-053, spec UX 2026-08-13, P6 — respuesta FIRMADA del dueño,
 * opción B: el viejo chip chico "En corrección" se retira del todo y este
 * renglón pasa a ser la ÚNICA forma de ver el estado — P-16, un dato no se dice
 * dos veces).
 *
 * Aparece SOLO cuando hace falta (P7, opción A): reserva.isUnderCorrection===true,
 * hoy eso pasa después de "Sacar de viaje". El resto del tiempo no se renderiza
 * nada — no vive escondido en el "⋯" (P7 lo descarta a propósito).
 *
 * "Volver a calcular" es una acción SEGURA (no destruye nada: solo pide que el
 * motor rearme Salida/Regreso desde los servicios de ahora) → sin "¿Seguro?"
 * (P-14 es para acciones destructivas, esta no lo es).
 */
export function NeedsDateRecalculationRow({ reserva, publicId, onRecalculated }) {
    const [recalculando, setRecalculando] = useState(false);

    if (!reserva?.isUnderCorrection) return null;

    const handleRecalcular = async () => {
        // Guarda anti doble-click: mientras la llamada está en curso, un segundo
        // click no dispara un segundo POST (mismo criterio que el resto de la ficha).
        if (recalculando) return;
        setRecalculando(true);
        try {
            await api.post(`/reservas/${publicId}/recalculate-dates`);
            showSuccess('Listo, fechas actualizadas.');
            // El padre refetchea la ficha completa: así el renglón desaparece solo
            // cuando el backend confirma que NeedsDateRecalculation ya se apagó.
            onRecalculated?.();
        } catch (error) {
            showError(getApiErrorMessage(error, 'No se pudieron recalcular las fechas. Probá de nuevo.'));
        } finally {
            setRecalculando(false);
        }
    };

    return (
        <AvisoFila
            variante="accion"
            dataTestId="aviso-volver-a-calcular-fechas"
            textoBoton={recalculando ? 'Calculando…' : 'Volver a calcular las fechas'}
            onClickBoton={handleRecalcular}
            botonDeshabilitado={recalculando}
        >
            Falta revisar las fechas del viaje.
        </AvisoFila>
    );
}
