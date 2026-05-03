import React from 'react';
import { AlertTriangle } from "lucide-react";

export function CapacityWarning({ paxCount, capacity }) {
    // Backwards compatibility: capacity puede ser un objeto {hotel, transfer, package, total} o un numero plano (legacy hotelCapacity)
    const cap = typeof capacity === "number"
        ? { hotel: capacity, transfer: 0, package: 0, total: capacity }
        : (capacity || { hotel: 0, transfer: 0, package: 0, total: 0 });

    if (paxCount <= 0 || cap.total <= 0 || paxCount <= cap.total) {
        return null;
    }

    const detalle = [];
    if (cap.hotel > 0 && paxCount > cap.hotel) detalle.push(`hotel para ${cap.hotel}`);
    if (cap.transfer > 0 && paxCount > cap.transfer) detalle.push(`transfer para ${cap.transfer}`);
    if (cap.package > 0 && paxCount > cap.package) detalle.push(`paquete para ${cap.package}`);

    return (
        <div className="bg-yellow-50 border-l-4 border-yellow-400 p-4 mb-4 dark:bg-yellow-950/20 dark:border-yellow-700">
            <div className="flex">
                <div className="flex-shrink-0">
                    <AlertTriangle className="h-5 w-5 text-yellow-400" aria-hidden="true" />
                </div>
                <div className="ml-3">
                    <p className="text-sm text-yellow-700 dark:text-yellow-300">
                        Atencion: hay <strong>{paxCount}</strong> pasajeros cargados pero los servicios contratados solo soportan <strong>{cap.total}</strong>{detalle.length > 0 ? ` (${detalle.join(", ")})` : ""}.
                        <br /><span className="text-xs opacity-75">Ajusta la capacidad de los servicios o agrega uno nuevo antes de continuar.</span>
                    </p>
                </div>
            </div>
        </div>
    );
}
