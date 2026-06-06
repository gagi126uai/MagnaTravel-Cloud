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
 * GET /afip/settings requiere que el usuario ya este logueado.
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
    enableSoldToSettleStates: false,
    enableMultiCurrencyInvoicing: false,
    // ADR-013: flag de emisión de Nota de Débito fiscal en cancelaciones.
    // No se usa todavía en la UI de reservas/cancelaciones (solo en el panel de settings),
    // pero se incluye en el contexto global para que el día que un componente necesite
    // saber si la ND está activa, lo lea desde acá en lugar de hacer un fetch propio.
    enableCancellationDebitNote: false,
    // ADR-017: flag del tarifario find-or-create desde la venta.
    // OFF = render idéntico a hoy (modal viejo ServiceFormModal).
    // ON  = muestra la ficha de carga en línea (ServiceInlineCard) + buscador de catálogo.
    enableCatalogFindOrCreate: false,
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
                const data = await api.get("/afip/settings");
                if (!cancelled && data) {
                    setFlags({
                        enableSoldToSettleStates: Boolean(data.enableSoldToSettleStates),
                        enableMultiCurrencyInvoicing: Boolean(data.enableMultiCurrencyInvoicing),
                        // ADR-013: si el endpoint /afip/settings llegara a exponer este flag
                        // en el futuro, ya lo leemos. Por ahora el backend no lo proyecta
                        // en AfipSettingsResponse (solo en /settings/operational-finance),
                        // así que data.enableCancellationDebitNote será undefined → false.
                        enableCancellationDebitNote: Boolean(data.enableCancellationDebitNote),
                        // ADR-017: flag del tarifario find-or-create.
                        // Si el backend no lo expone en /afip/settings todavía,
                        // data.enableCatalogFindOrCreate será undefined → false (seguro).
                        enableCatalogFindOrCreate: Boolean(data.enableCatalogFindOrCreate),
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
