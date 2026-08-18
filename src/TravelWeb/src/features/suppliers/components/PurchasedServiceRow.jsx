/**
 * Una fila de la grilla DESKTOP de "Servicios comprados" (cuenta del operador), Tanda T5
 * (2026-08-18, spec docs/ux/2026-08-18-spec-t5-expansion-pasajero.md sección 2, respuesta
 * firmada P3=A). Antes cada fila se armaba inline adentro del `.map()` de
 * `SupplierAccountPage.jsx`; se extrajo a un componente propio porque ahora la fila puede
 * traer una SEGUNDA fila de tabla debajo (la expansión con el casillero de confirmación,
 * a todo el ancho, en vez de apretada en la columna ESTADO) — y las dos filas necesitan
 * compartir el mismo estado de "qué acción está abierta" (hook `useResolverServicioAcciones`).
 *
 * La vista MOBILE (tarjetas, `MobileRecordCard`) sigue viviendo en `SupplierAccountPage.jsx`
 * con el componente viejo `EstadoServicioCell` — no se toca, porque ahí el casillero de
 * `ResolverServicioInline` ya tiene lugar de sobra (una tarjeta ancha, no una columna).
 */

import { useState } from "react";
import { Link } from "react-router-dom";
import { DataGridCell, DataGridRow } from "../../../components/ui/DataGrid";
import { formatCurrency, formatDate } from "../../../lib/utils";
import { supplierDueState } from "../lib/supplierAging";
import {
    mapearTipoEspanolARecordKind,
    debeMostrarBotonPrimarioEnCuentaOperador,
} from "../../reservas/lib/serviceResolutionActions";
import { useResolverServicioAcciones } from "../../reservas/lib/useResolverServicioAcciones";
import { ResolverServicioBotones } from "./ResolverServicioBotones";
import { ResolverServicioCasillero } from "./ResolverServicioCasillero";
import { ServiceStatusEditor } from "./ServiceStatusEditor";
import { ServiceConfirmationEditor } from "./ServiceConfirmationEditor";

// Tipo, Descripcion, Reserva, Fecha, Vencimiento, Estado, Codigo, Costo, Venta.
const CANTIDAD_COLUMNAS = 9;

export function PurchasedServiceRow({ service, canEdit, onUpdated, puedeVerMontos }) {
    const recordKind = mapearTipoEspanolARecordKind(service.type);
    // La decisión de elegibilidad vive en serviceResolutionActions.js (testeada aparte):
    // requiere permiso de editar, servicio pendiente, tipo con flujo de confirmación
    // conocido y una reserva asociada (los endpoints reserva-scoped la necesitan).
    const tieneBotonPrimario = debeMostrarBotonPrimarioEnCuentaOperador({
        canEdit,
        status: service.status,
        recordKind,
        reservaPublicId: service.reservaPublicId,
    });

    // El hook se llama SIEMPRE (regla de los hooks de React), aunque este servicio no
    // tenga botón primario — en ese caso simplemente no se usa nada de lo que devuelve.
    const resolver = useResolverServicioAcciones({
        reservaId: service.reservaPublicId,
        servicePublicId: service.publicId,
        recordKind,
        onResuelto: onUpdated,
    });

    // "Corregir a mano" vive plegado por defecto adentro de la fila de expansión — mismo
    // criterio de siempre (P-9/P-10: acción secundaria discreta, no compite con la primaria).
    const [mostrarCorreccion, setMostrarCorreccion] = useState(false);

    const filaExpandidaAbierta = tieneBotonPrimario && !service.faltaTitularConNombre && Boolean(resolver.accionAbierta);
    const accionAbierta = resolver.acciones.find((accion) => accion.tipo === resolver.accionAbierta);

    // Al cerrar la fila de expansión (Cancelar), también plegamos "Corregir a mano" —
    // si no, la próxima vez que se abra esta fila aparecería directo el desplegable
    // viejo en vez del link, porque `mostrarCorreccion` vive en ESTE componente (no se
    // desmonta entre aperturas, el `key` de la fila sigue siendo el mismo servicio).
    const cerrarFilaDeExpansion = () => {
        resolver.cerrarCasillero();
        setMostrarCorreccion(false);
    };

    return (
        <>
            <DataGridRow>
                <DataGridCell>
                    <span className="rounded bg-primary/10 px-2 py-1 text-xs font-medium text-primary">
                        {service.type}
                    </span>
                </DataGridCell>
                <DataGridCell>
                    <div className="font-medium">{service.description || "-"}</div>
                    {service.fileName
                        ? <div className="text-xs text-muted-foreground">{service.fileName}</div>
                        : null}
                </DataGridCell>
                <DataGridCell>
                    {service.reservaPublicId ? (
                        <Link
                            to={`/reservas/${service.reservaPublicId}`}
                            className="font-medium text-primary hover:underline"
                        >
                            {service.numeroReserva || "Ver reserva"}
                        </Link>
                    ) : (
                        service.numeroReserva || "-"
                    )}
                </DataGridCell>
                <DataGridCell>{formatDate(service.date)}</DataGridCell>
                <DataGridCell>
                    {(() => {
                        const due = supplierDueState(service.suggestedDueDate);
                        if (!due) return <span className="text-xs text-slate-400">Sin plazo</span>;
                        const tone = due.tone === "overdue" || due.tone === "today"
                            ? "text-rose-700 bg-rose-50 dark:text-rose-300 dark:bg-rose-950/30"
                            : due.tone === "soon"
                                ? "text-amber-700 bg-amber-50 dark:text-amber-300 dark:bg-amber-950/30"
                                : "text-slate-600 bg-slate-50 dark:text-slate-300 dark:bg-slate-800";
                        return <span className={`rounded px-2 py-1 text-xs font-semibold ${tone}`}>{due.label}</span>;
                    })()}
                </DataGridCell>
                <DataGridCell>
                    {!tieneBotonPrimario ? (
                        <ServiceStatusEditor service={service} onUpdated={onUpdated} canEdit={canEdit} />
                    ) : service.faltaTitularConNombre ? (
                        // F4 (plan 2026-07-31 tarde, hueco cerrado): mismo candado pre-emptivo P-9
                        // que ya usan aéreo/traslado en ServiceList.jsx — botón apagado, texto de
                        // motivo en vez del botón (nunca un tooltip, P-9/P-10). `faltaTitularConNombre`
                        // viene YA CALCULADO del motor (T-13), el front no reevalúa nada.
                        <span
                            className="text-[11px] font-semibold text-amber-600 dark:text-amber-400"
                            data-testid={`hint-pasajeros-titular-${service.publicId}`}
                        >
                            Cargá al menos el titular primero
                        </span>
                    ) : (
                        <ResolverServicioBotones
                            acciones={resolver.acciones}
                            accionAbierta={resolver.accionAbierta}
                            guardando={resolver.guardando}
                            onAbrirCasillero={resolver.abrirCasillero}
                            onCerrarCasillero={cerrarFilaDeExpansion}
                            onEjecutarSinCasillero={resolver.ejecutarAccionSinCasillero}
                            errorSinCasillero={resolver.errorSinCasillero}
                            onCerrarErrorSinCasillero={() => resolver.setErrorSinCasillero(null)}
                            servicePublicId={service.publicId}
                        />
                    )}
                </DataGridCell>
                <DataGridCell>
                    <ServiceConfirmationEditor service={service} canEdit={canEdit} onUpdated={onUpdated} />
                </DataGridCell>
                {/* Costo: enmascarado sin permiso cobranzas.see_cost */}
                <DataGridCell align="right" className="font-mono">
                    {puedeVerMontos
                        ? formatCurrency(service.netCost, service.currency)
                        : <span className="text-muted-foreground">—</span>
                    }
                </DataGridCell>
                <DataGridCell align="right" className="font-mono">
                    {formatCurrency(service.salePrice, service.currency)}
                </DataGridCell>
            </DataGridRow>

            {/* Fila de expansión (P3=A): solo existe mientras hay una acción con casillero
                abierta para este servicio. Ocupa las 9 columnas — le da todo el ancho de la
                tabla al casillero, en vez del apretujón de antes dentro de la columna Estado. */}
            {filaExpandidaAbierta && accionAbierta && (
                <DataGridRow
                    interactive={false}
                    data-testid={`fila-expansion-resolver-${service.publicId}`}
                >
                    <DataGridCell colSpan={CANTIDAD_COLUMNAS} className="p-0">
                        <ResolverServicioCasillero
                            numero={resolver.numero}
                            onNumeroChange={resolver.setNumero}
                            guardando={resolver.guardando}
                            errorMensaje={resolver.errorMensaje}
                            mostrarCartel={resolver.mostrarCartel}
                            onCerrarCartel={() => resolver.setMostrarCartel(false)}
                            onConfirmar={() => resolver.ejecutarAccionConCasillero(accionAbierta.tipo)}
                            onCancelar={cerrarFilaDeExpansion}
                            inputRef={resolver.inputRef}
                            servicePublicId={service.publicId}
                            mostrarCorreccion={mostrarCorreccion}
                            onMostrarCorreccion={() => setMostrarCorreccion(true)}
                            service={service}
                            onUpdated={onUpdated}
                            canEdit={canEdit}
                        />
                    </DataGridCell>
                </DataGridRow>
            )}
        </>
    );
}
