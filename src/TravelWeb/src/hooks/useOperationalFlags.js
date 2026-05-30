import { useState, useEffect } from "react";
import { api } from "../api";

/**
 * Hook que lee los flags operativos del sistema desde GET /afip/settings.
 *
 * Los flags controlan qué features estan habilitadas en la UI sin necesidad
 * de un redeploy. Se leen una vez al montar el componente que los necesita.
 *
 * Flags disponibles:
 * - enableSoldToSettleStates: activa el ciclo extendido de reservas
 *   (Budget → Sold → Confirmed → Traveling → ToSettle → Closed).
 * - enableMultiCurrencyInvoicing: activa el selector de moneda en facturas.
 *
 * Si el endpoint falla (red caida, 403, etc.) todos los flags quedan en false
 * para que la UI caiga al comportamiento base sin romperse.
 */
export function useOperationalFlags() {
    const [flags, setFlags] = useState({
        enableSoldToSettleStates: false,
        enableMultiCurrencyInvoicing: false,
    });
    const [loadingFlags, setLoadingFlags] = useState(true);

    useEffect(() => {
        // useEffect con dependencia vacia: solo corre una vez al montar.
        // Los flags no cambian durante la sesion del usuario (se aplican al recargar).
        let cancelled = false;

        const fetchFlags = async () => {
            try {
                const data = await api.get("/afip/settings");
                if (!cancelled && data) {
                    setFlags({
                        enableSoldToSettleStates: Boolean(data.enableSoldToSettleStates),
                        enableMultiCurrencyInvoicing: Boolean(data.enableMultiCurrencyInvoicing),
                    });
                }
            } catch {
                // Si falla, los flags quedan en false (comportamiento base, sin romperse).
            } finally {
                if (!cancelled) setLoadingFlags(false);
            }
        };

        fetchFlags();

        return () => { cancelled = true; };
    }, []);

    return { flags, loadingFlags };
}
