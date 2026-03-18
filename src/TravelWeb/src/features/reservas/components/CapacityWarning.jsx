import React from 'react';
import { AlertTriangle } from "lucide-react";

export function CapacityWarning({ paxCount, hotelCapacity }) {
    if (paxCount > 0 && hotelCapacity > 0 && paxCount > hotelCapacity) {
        return (
            <div className="bg-yellow-50 border-l-4 border-yellow-400 p-4 mb-4">
                <div className="flex">
                    <div className="flex-shrink-0">
                        <AlertTriangle className="h-5 w-5 text-yellow-400" aria-hidden="true" />
                    </div>
                    <div className="ml-3">
                        <p className="text-sm text-yellow-700">
                            Atención: Hay <strong>{paxCount}</strong> pasajeros cargados pero la capacidad hotelera estimada es de <strong>{hotelCapacity}</strong> plazas.
                            <br /><span className="text-xs opacity-75">Verifique la distribución de habitaciones.</span>
                        </p>
                    </div>
                </div>
            </div>
        );
    }
    return null;
}
