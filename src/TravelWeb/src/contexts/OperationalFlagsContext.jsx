/**
 * Contexto global para los flags operativos del sistema.
 *
 * Problema que resuelve: si cada componente llama a useOperationalFlags()
 * por separado, cada uno hace su propio fetch a /afip/settings y arranca
 * con flags en false (default). Cuando la respuesta llega, el layout "salta"
 * del comportamiento base al extendido — ese es el parpadeo.
 *
 * Solucion: un unico fetch compartido para toda la zona autenticada.
 * El provider va DENTRO de PrivateRoute (no en main.jsx) porque
 * el endpoint de flags requiere que el usuario ya este logueado.
 *
 * Fuente de datos: GET /settings/operational-flags ([Authorize] plano, solo booleanos).
 * ANTES leia /afip/settings, que (a) no proyectaba enableCatalogFindOrCreate y
 * (b) exige permiso de facturacion -> los vendedores recibian 403 y quedaban
 * con todos los flags en false. Bug encontrado por Gaston el 2026-06-06.
 *
 * Garantia de fallback: si el fetch falla (red caida, 403, timeout),
 * los flags quedan en false y la UI cae al comportamiento base sin romperse.
 * Esto es intencional: preferimos el layout base a un crash.
 */

import { createContext, useContext, useState, useEffect } from "react";
import { api } from "../api";

// Valores default seguros: si el contexto se usa fuera del provider, no rompe.
// Cada flag arranca en false (comportamiento base conservador).
const defaultFlags = {
    // ADR-012: facturacion multimoneda (USD/ARS en el modal de factura).
    enableMultiCurrencyInvoicing: false,
    // ADR-013: emision de Nota de Debito fiscal en cancelaciones con penalidad.
    enableCancellationDebitNote: false,
    // ADR-017: tarifario find-or-create desde la venta (ficha inline ServiceInlineCard).
    enableCatalogFindOrCreate: false,
    // ADR-019: avisos "Proximos inicios" (campanita + columna en ServiceList).
    enableServiceDeadlineAlerts: false,
    // NOTA: enableSoldToSettleStates fue eliminado en ADR-020.
    // El ciclo "Vendida" murio y el nuevo ciclo es directo sin flags.
};

const OperationalFlagsContext = createContext(undefined);

export function OperationalFlagsProvider({ children }) {
    const [flags, setFlags] = useState(defaultFlags);
    // Arrancamos en true para que los consumidores sepan que todavia no llegaron los datos.
    // Esto evita que el layout "decida" basado en false mientras el fetch esta en vuelo.
    const [loadingFlags, setLoadingFlags] = useState(true);

    useEffect(() => {
        // useEffect con dependencia vacia: corre una sola vez al montar el provider.
        // El provider vive dentro de PrivateRoute, asi que monta cuando el usuario
        // entra a la zona autenticada y desmonta cuando cierra sesion.
        let cancelled = false;

        const fetchFlags = async () => {
            try {
                // Endpoint liviano de solo-booleanos, accesible para CUALQUIER usuario
                // logueado (los vendedores no tienen permiso de facturación y por eso
                // /afip/settings les daba 403 → flags siempre false).
                const data = await api.get("/settings/operational-flags");
                if (!cancelled && data) {
                    // ADR-020: enableSoldToSettleStates fue eliminado del backend.
                    // El ciclo es unico y directo, sin flags de ciclo.
                    setFlags({
                        enableMultiCurrencyInvoicing: Boolean(data.enableMultiCurrencyInvoicing),
                        enableCancellationDebitNote: Boolean(data.enableCancellationDebitNote),
                        enableCatalogFindOrCreate: Boolean(data.enableCatalogFindOrCreate),
                        enableServiceDeadlineAlerts: Boolean(data.enableServiceDeadlineAlerts),
                    });
                }
            } catch {
                // Si falla, los flags quedan en false (comportamiento base, sin romperse).
                // No logueamos el error para no llenar la consola en ambientes sin el endpoint.
            } finally {
                // Importante: siempre marcamos como "terminado de cargar" aunque falle.
                // Si no, los consumidores quedarian bloqueados esperando indefinidamente.
                if (!cancelled) setLoadingFlags(false);
            }
        };

        fetchFlags();

        // Cleanup: si el componente se desmonta antes de que termine el fetch,
        // cancelamos para no actualizar estado en un componente ya muerto.
        return () => {
            cancelled = true;
        };
    }, []);

    return (
        <OperationalFlagsContext.Provider value={{ flags, loadingFlags }}>
            {children}
        </OperationalFlagsContext.Provider>
    );
}

/**
 * Hook para leer los flags operativos desde cualquier componente.
 *
 * Mantiene el mismo nombre y la misma forma de retorno que el hook viejo
 * ({ flags, loadingFlags }) para que los call-sites no necesiten cambios.
 *
 * Si se llama fuera del OperationalFlagsProvider (error de configuracion),
 * devuelve defaults seguros en lugar de tirar — evita crashear la app entera
 * por un error de wiring. En desarrollo esto seria visible como un comportamiento
 * "siempre false" que indicaria que falta montar el provider.
 */
export function useOperationalFlags() {
    const context = useContext(OperationalFlagsContext);

    // Fuera del provider: devolvemos defaults. loadingFlags=false para que
    // los consumidores que esperan "loading antes de mostrar" no queden bloqueados.
    // No tiramos un error (crashearia toda la app por un error de wiring), pero
    // en desarrollo dejamos un aviso: si esto aparece, falta montar el provider y
    // el componente quedaria "siempre OFF" en silencio (bug invisible en prod).
    if (context === undefined) {
        if (import.meta.env.DEV) {
            console.warn(
                "useOperationalFlags() se llamo fuera de <OperationalFlagsProvider>. " +
                "Se usan defaults (todos los flags en false). Verifica el arbol de providers."
            );
        }
        return { flags: defaultFlags, loadingFlags: false };
    }

    return context;
}
