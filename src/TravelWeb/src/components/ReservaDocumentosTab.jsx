import { ReservaVoucherTab } from "./ReservaVoucherTab";
import { ReservaDocumentsTab } from "./ReservaDocumentsTab";

/**
 * Solapa única "Documentos" de la ficha de una reserva.
 *
 * P12 (Tanda 3 del rediseño de Reservas, 2026-08-03, maqueta firmada sección 10):
 * antes había DOS solapas para lo mismo — "Vouchers" (que por dentro se llamaba
 * "Documentación") y "Documentos" (la zona de archivos sueltos). El vendedor no
 * distingue "esto lo emitió el sistema" de "esto lo subí yo": piensa "los papeles
 * de esta reserva". Este componente junta los dos bloques bajo una sola solapa,
 * cada uno con su propio título — "Vouchers del viaje" y "Archivos de la reserva".
 *
 * El contenido y la lógica de cada bloque son EXACTAMENTE los de las solapas
 * viejas (ReservaVoucherTab / ReservaDocumentsTab): esto es solo la reubicación,
 * no se tocó ningún endpoint ni ninguna regla de negocio.
 */
export function ReservaDocumentosTab({ reservaId, reserva, soloLectura, canEmitVoucher, canUploadDocument }) {
  return (
    <div className="space-y-10">
      <ReservaVoucherTab
        reservaId={reservaId}
        reserva={reserva}
        soloLectura={soloLectura}
        canEmitVoucher={canEmitVoucher}
      />

      {/* Separador entre los dos bloques (maqueta: "Vouchers del viaje" arriba,
          "Archivos de la reserva" abajo, ambos dentro de la misma solapa). */}
      <div className="border-t border-slate-200 pt-8 dark:border-slate-800">
        <ReservaDocumentsTab reservaId={reservaId} canUploadDocument={canUploadDocument} />
      </div>
    </div>
  );
}
