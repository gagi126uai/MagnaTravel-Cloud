/**
 * Editor inline del código de confirmación del proveedor (PNR para vuelos,
 * ConfirmationNumber para el resto) en la columna CÓDIGO de "Servicios comprados".
 * Click para editar, Enter/blur para guardar, Esc para cancelar.
 *
 * Extraído de `SupplierAccountPage.jsx` (Tanda T5, 2026-08-18) — mismo código, sin
 * reescribir nada — para que la fila de escritorio (`PurchasedServiceRow`) pueda
 * importarlo sin crear un import circular con la página.
 */

import { useEffect, useState } from "react";
import { Check, X } from "lucide-react";
import { api } from "../../../api";
import { showSuccess, showError } from "../../../alerts";
import { getApiErrorMessage } from "../../../lib/errors";
import { STATUS_ENDPOINT_BY_TYPE } from "../lib/purchasedServiceStatusEndpoints";

export function ServiceConfirmationEditor({ service, onUpdated, canEdit }) {
    const endpoint = STATUS_ENDPOINT_BY_TYPE[service.type];
    const [editing, setEditing] = useState(false);
    const [value, setValue] = useState(service.confirmation || "");
    const [saving, setSaving] = useState(false);

    // Mantener sincronizado si cambia el dato externo (refresh)
    useEffect(() => {
        if (!editing) setValue(service.confirmation || "");
    }, [service.confirmation, editing]);

    if (!endpoint || !canEdit) {
        return <span className="font-mono text-xs">{service.confirmation || "-"}</span>;
    }

    const save = async () => {
        const trimmed = value.trim();
        const previous = service.confirmation || "";
        if (trimmed === previous) {
            setEditing(false);
            return;
        }
        setSaving(true);
        try {
            await api.patch(`/${endpoint}/${service.publicId}/status`, {
                status: service.status || "Solicitado",
                confirmationNumber: trimmed,
            });
            showSuccess(trimmed ? `Codigo guardado: ${trimmed}` : "Codigo eliminado");
            setEditing(false);
            if (onUpdated) onUpdated();
        } catch (error) {
            // getApiErrorMessage normaliza el error y evita strings en inglés del runtime.
            showError(getApiErrorMessage(error, "No se pudo guardar el código."), "No se pudo guardar el código");
        } finally {
            setSaving(false);
        }
    };

    const cancel = () => {
        setValue(service.confirmation || "");
        setEditing(false);
    };

    if (!editing) {
        return (
            <button
                type="button"
                onClick={() => setEditing(true)}
                className="font-mono text-xs text-left hover:bg-accent px-1.5 py-0.5 rounded transition-colors"
                title="Editar codigo de confirmacion"
            >
                {service.confirmation || <span className="text-muted-foreground italic">(agregar)</span>}
            </button>
        );
    }

    return (
        <div className="inline-flex items-center gap-1">
            <input
                type="text"
                autoFocus
                value={value}
                disabled={saving}
                onChange={(e) => setValue(e.target.value)}
                onKeyDown={(e) => {
                    if (e.key === "Enter") save();
                    if (e.key === "Escape") cancel();
                }}
                placeholder="Codigo..."
                className="rounded border border-input bg-background px-1.5 py-0.5 text-xs font-mono w-28 focus:outline-none focus:ring-1 focus:ring-ring"
            />
            <button
                type="button"
                onClick={save}
                disabled={saving}
                className="rounded p-0.5 text-emerald-600 hover:bg-emerald-50 disabled:opacity-50"
                title="Guardar"
            >
                <Check className="h-3 w-3" />
            </button>
            <button
                type="button"
                onClick={cancel}
                disabled={saving}
                className="rounded p-0.5 text-slate-500 hover:bg-slate-100 disabled:opacity-50"
                title="Cancelar"
            >
                <X className="h-3 w-3" />
            </button>
        </div>
    );
}
