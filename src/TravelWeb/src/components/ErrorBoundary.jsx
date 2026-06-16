import { Component, createRef } from "react";

/**
 * Barrera de errores de la aplicacion.
 *
 * En React 18, cuando un componente lanza durante el render, React DESMONTA TODO EL
 * ARBOL y deja la pantalla en blanco. Este componente atrapa esas excepciones y
 * muestra un cartel de error amigable con opciones de recuperacion.
 *
 * Se usa en DOS niveles en App.jsx:
 *   1. EXTERNO (variant="fullscreen"): envuelve todo. Red de ultimo recurso.
 *      Si crashea, el usuario pierde sidebar/topbar pero siempre ve algo.
 *   2. INTERNO (variant="inline", default): dentro del <Layout>.
 *      Un crash de una sola pantalla muestra el cartel DENTRO del layout,
 *      conservando el menu de navegacion lateral y la topbar.
 *
 * Los ErrorBoundary solo pueden ser clases (React 18 no tiene hook equivalente).
 */
export class ErrorBoundary extends Component {
    constructor(props) {
        super(props);
        this.state = { tieneError: false, mensajeError: null };
        this.handleRecargar = this.handleRecargar.bind(this);
        this.handleVolver = this.handleVolver.bind(this);
        // Ref para mover el foco al heading cuando aparece el cartel de error.
        // Esto es necesario para lectores de pantalla: sin foco explicito, el
        // lector no anuncia el cambio de contenido automaticamente.
        this.headingRef = createRef();
    }

    /**
     * Se llama cuando un componente hijo lanza durante el render.
     * Actualizamos el estado para mostrar el fallback en el proximo render.
     */
    static getDerivedStateFromError(error) {
        return {
            tieneError: true,
            // Solo guardamos el mensaje (string) — no serializamos el objeto entero.
            mensajeError: error?.message ?? "Error inesperado",
        };
    }

    /**
     * Se llama despues de atrapar el error, con el stack completo.
     * El detalle tecnico va solo a consola, nunca a pantalla.
     */
    componentDidCatch(error, info) {
        console.error("[ErrorBoundary] Excepcion de render atrapada:", error, info);
    }

    /**
     * Cuando el fallback se monta (es decir, acabamos de atrapar un error),
     * movemos el foco al heading para que lectores de pantalla lo anuncien.
     * Usamos componentDidUpdate porque componentDidMount no corre en re-renders;
     * el boundary primero renderiza los hijos (sin error), y solo despues de
     * atrapar un error vuelve a renderizar con tieneError=true.
     */
    componentDidUpdate(prevProps, prevState) {
        // Solo movemos el foco la primera vez que aparece el cartel de error.
        const aparecioPorPrimeraVez = !prevState.tieneError && this.state.tieneError;
        if (aparecioPorPrimeraVez && this.headingRef.current) {
            this.headingRef.current.focus();
        }
    }

    handleRecargar() {
        // Recarga completa de la pagina para limpiar cualquier estado corrupto.
        window.location.reload();
    }

    handleVolver() {
        // Navegamos al dashboard con una recarga completa.
        // No hacemos setState antes porque window.location.href descarta el arbol
        // de todas formas — el setState seria codigo muerto.
        window.location.href = "/dashboard";
    }

    render() {
        if (!this.state.tieneError) {
            // Sin error: renderizamos los hijos normalmente (camino feliz).
            return this.props.children;
        }

        // variant="fullscreen" (boundary externo): ocupa toda la pantalla con fondo propio.
        // variant="inline" (boundary interno, dentro del layout): ocupa el espacio del contenido.
        // Si no se pasa variant, usamos "inline" como default seguro.
        const variant = this.props.variant ?? "inline";
        const isFullscreen = variant === "fullscreen";

        // Estilos del contenedor raiz segun el nivel del boundary.
        const contenedorClases = isFullscreen
            ? "flex min-h-screen items-center justify-center bg-slate-50 p-6 dark:bg-slate-950"
            : "flex min-h-full items-center justify-center p-6";

        return (
            /*
              role="alert" + aria-live="assertive" (implicito en alert):
              los lectores de pantalla anuncian el contenido inmediatamente cuando
              aparece, sin que el usuario tenga que navegar hasta el.
            */
            <div
                role="alert"
                data-testid="error-boundary"
                className={contenedorClases}
            >
                <div className="w-full max-w-sm space-y-6 rounded-2xl border border-slate-200 bg-white p-8 shadow-lg dark:border-slate-800 dark:bg-slate-900">

                    {/* Icono de alerta — decorativo, oculto a lectores de pantalla */}
                    <div className="flex justify-center">
                        <div className="flex h-16 w-16 items-center justify-center rounded-2xl bg-rose-50 dark:bg-rose-950/30">
                            <svg
                                xmlns="http://www.w3.org/2000/svg"
                                className="h-8 w-8 text-rose-500"
                                fill="none"
                                viewBox="0 0 24 24"
                                stroke="currentColor"
                                strokeWidth={2}
                                aria-hidden="true"
                            >
                                <path
                                    strokeLinecap="round"
                                    strokeLinejoin="round"
                                    d="M12 9v4m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z"
                                />
                            </svg>
                        </div>
                    </div>

                    {/* Mensaje principal — sin jerga tecnica, lenguaje de negocio */}
                    <div className="text-center space-y-2">
                        <h1
                            /*
                              tabIndex={-1} permite recibir foco via .focus() desde codigo
                              sin agregarlo al tab order natural del teclado.
                              El foco se mueve aqui en componentDidUpdate cuando tieneError
                              cambia de false a true.
                            */
                            tabIndex={-1}
                            ref={this.headingRef}
                            className="text-xl font-black text-slate-900 outline-none dark:text-white"
                        >
                            Algo se rompió al mostrar esta pantalla.
                        </h1>
                        <p className="text-sm text-slate-500 dark:text-slate-400">
                            Esto no afecta tus datos. Recargá la página para volver a intentarlo.
                        </p>
                    </div>

                    {/* Acciones de recuperacion */}
                    <div className="flex flex-col gap-3">
                        <button
                            type="button"
                            onClick={this.handleRecargar}
                            data-testid="error-boundary-reload"
                            className="w-full rounded-xl bg-indigo-600 px-4 py-3 text-sm font-bold text-white transition-colors hover:bg-indigo-700 focus:outline-none focus:ring-2 focus:ring-indigo-500 focus:ring-offset-2"
                        >
                            Recargar página
                        </button>
                        <button
                            type="button"
                            onClick={this.handleVolver}
                            data-testid="error-boundary-home"
                            className="w-full rounded-xl border border-slate-200 bg-white px-4 py-3 text-sm font-bold text-slate-600 transition-colors hover:bg-slate-50 dark:border-slate-700 dark:bg-slate-800 dark:text-slate-300 dark:hover:bg-slate-700 focus:outline-none focus:ring-2 focus:ring-slate-400 focus:ring-offset-2"
                        >
                            Volver al inicio
                        </button>
                    </div>
                </div>
            </div>
        );
    }
}
