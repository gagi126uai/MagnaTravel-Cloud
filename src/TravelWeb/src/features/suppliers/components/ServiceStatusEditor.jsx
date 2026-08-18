/**
 * Desplegable viejo de estado (Solicitado/Confirmado/Cancelado) de un servicio comprado.
 * Extraído de `SupplierAccountPage.jsx` (Tanda T5, 2026-08-18) — mismo código, sin
 * reescribir nada, para poder usarlo tanto desde la celda ESTADO (servicio ya
 * confirmado/cancelado, sin nada que "avanzar") como desde adentro de la fila de
 * expansión nueva (`ResolverServicioCasillero`, link "Corregir a mano").
 *
 * Sigue siendo la ÚNICA forma de CORREGIR un estado hacia atrás (ej. deshacer una
 * confirmación por error) — el flujo hacia adelante vive en `ResolverServicioInline` /
 * `ResolverServicioBotones` + `ResolverServicioCasillero`.
 */

import { useState } from "react";
import { api } from "../../../api";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { CartelEmergente, CARTEL_EMERGENTE_VARIANTES } from "../../../components/CartelEmergente";
import { CODIGO_RECHAZO_ANULAR_SERVICIO } from "../../reservas/lib/serviceCancellationGuard";
import { STATUS_ENDPOINT_BY_TYPE, STATUS_OPTIONS } from "../lib/purchasedServiceStatusEndpoints";

export function ServiceStatusEditor({ service, onUpdated, canEdit }) {
    const endpoint = STATUS_ENDPOINT_BY_TYPE[service.type];
    const [value, setValue] = useState(service.status || "Solicitado");
    const [saving, setSaving] = useState(false);
    // P1 "circuito proveedor" (2026-07-21): cuando el PATCH de "bajar el estado" choca con el
    // candado de plata (el servicio ya tiene pagos al operador que quedarían sin resolver), el
    // backend manda el MISMO code que "anular servicio" solía mandar. Guardamos acá el mensaje
    // real para mostrarlo en ventana (Aviso 1 del inventario, spec 2026-07-22: rechazo largo del
    // motor → Cartel Emergente, no un toast que desaparece solo).
    // Obra "anular sin factura" (2026-07-23): este rechazo YA NO ofrece el botón "Emitir
    // factura" — el mensaje del motor orienta a "gestioná el reembolso con el operador".
    const [bloqueoPagoSinFactura, setBloqueoPagoSinFactura] = useState(null);

    if (!endpoint || !canEdit) {
        // Servicio generico — no editable desde aca, mostramos texto plano
        return <span className="text-sm">{service.status || "-"}</span>;
    }

    const handleChange = async (e) => {
        const newStatus = e.target.value;
        if (newStatus === value) return;
        const previous = value;
        setValue(newStatus);
        setSaving(true);
        setBloqueoPagoSinFactura(null);
        try {
            await api.patch(`/${endpoint}/${service.publicId}/status`, { status: newStatus });
            showSuccess(`Estado actualizado a "${newStatus}"`);
            if (onUpdated) onUpdated();
        } catch (error) {
            // Revertir el valor optimista en la UI antes de mostrar el error.
            // Usamos getApiErrorMessage para evitar que strings de red en inglés
            // ("Failed to fetch", "Internal Server Error") lleguen al usuario.
            setValue(previous);
            const mensaje = getApiErrorMessage(error, "No se pudo actualizar el estado.");
            // Mismo code que manda "anular servicio" en otros casos (nunca se adivina el
            // motivo comparando texto libre) — acá el único code real que puede llegar de este
            // endpoint es PAGO_SIN_FACTURA (candado de "bajar estado"), así que lo comparamos
            // directo en vez de reusar el mapeo de botón (que ya no aplica: obra 2026-07-23).
            if (error?.payload?.code === CODIGO_RECHAZO_ANULAR_SERVICIO.PAGO_SIN_FACTURA) {
                // Aviso 1 del inventario (spec 2026-07-22): rechazo largo del motor → ventana
                // fija, no un toast que desaparece solo.
                setBloqueoPagoSinFactura(mensaje);
            } else {
                showError(mensaje, "No se pudo cambiar el estado");
            }
        } finally {
            setSaving(false);
        }
    };

    const colorClass = value === "Confirmado"
        ? "bg-emerald-50 text-emerald-700 border-emerald-200 dark:bg-emerald-950/30 dark:text-emerald-300 dark:border-emerald-800"
        : value === "Cancelado"
            ? "bg-rose-50 text-rose-700 border-rose-200 dark:bg-rose-950/30 dark:text-rose-300 dark:border-rose-800"
            : "bg-amber-50 text-amber-700 border-amber-200 dark:bg-amber-950/30 dark:text-amber-300 dark:border-amber-800";

    return (
        <div className="flex flex-col items-start gap-1">
            <select
                value={value}
                onChange={handleChange}
                disabled={saving}
                className={`rounded-md border text-xs font-bold px-2 py-1 ${colorClass} disabled:opacity-50`}
                title="Cambiar estado del servicio"
            >
                {STATUS_OPTIONS.map((opt) => (
                    <option key={opt} value={opt}>{opt}</option>
                ))}
            </select>
            {/* Aviso 1 del inventario (spec 2026-07-22): rechazo largo del motor → ventana
                única, no un recuadro incrustado en la fila que deformaba la tabla. Sin botón
                de camino (obra "anular sin factura", 2026-07-23): el mensaje ya orienta a
                gestionar el reembolso con el operador, "Entendido" alcanza. */}
            <CartelEmergente
                isOpen={Boolean(bloqueoPagoSinFactura)}
                variant={CARTEL_EMERGENTE_VARIANTES.BLOQUEO}
                message={bloqueoPagoSinFactura}
                onClose={() => setBloqueoPagoSinFactura(null)}
                dataTestId="status-editor-bloqueo-pago-sin-factura"
            />
        </div>
    );
}
