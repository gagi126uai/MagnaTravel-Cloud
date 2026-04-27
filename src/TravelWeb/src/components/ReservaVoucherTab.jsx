import { useCallback, useEffect, useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, Download, FilePlus2, FileText, Loader2, UploadCloud } from "lucide-react";
import { toast } from "sonner";
import { api } from "../api";
import { getApiErrorMessage } from "../lib/errors";
import { getPublicId } from "../lib/publicIds";

const VOUCHER_SCOPES = [
  { value: "ReservaCompleta", label: "Reserva completa" },
  { value: "TodosLosPasajeros", label: "Todos los pasajeros" },
  { value: "PasajerosSeleccionados", label: "Pasajeros seleccionados" },
];

function getPassengerName(passenger) {
  return passenger?.fullName || passenger?.FullName || passenger?.name || passenger?.Name || "Pasajero";
}

function getPassengerId(passenger) {
  return getPublicId(passenger);
}

function formatDateTime(value) {
  if (!value) return "-";
  return `${new Date(value).toLocaleDateString()} ${new Date(value).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
}

function formatMoney(value) {
  return Number(value || 0).toLocaleString("es-AR", { style: "currency", currency: "ARS" });
}

function formatStatus(status) {
  switch (status) {
    case "Draft":
      return "Generado";
    case "Issued":
      return "Emitido";
    case "UploadedExternal":
      return "Cargado externo";
    case "Revoked":
      return "Revocado";
    default:
      return status || "Sin estado";
  }
}

function formatScope(scope) {
  return VOUCHER_SCOPES.find((item) => item.value === scope)?.label || scope || "Reserva completa";
}

function normalizeVoucher(item) {
  return {
    publicId: item.publicId || item.PublicId,
    source: item.source || item.Source,
    status: item.status || item.Status,
    scope: item.scope || item.Scope,
    fileName: item.fileName || item.FileName || "voucher.pdf",
    externalOrigin: item.externalOrigin || item.ExternalOrigin,
    isEnabledForSending: item.isEnabledForSending ?? item.IsEnabledForSending ?? false,
    canSend: item.canSend ?? item.CanSend ?? false,
    reservationHasOutstandingBalance: item.reservationHasOutstandingBalance ?? item.ReservationHasOutstandingBalance ?? false,
    outstandingBalance: item.outstandingBalance ?? item.OutstandingBalance ?? 0,
    createdByUserName: item.createdByUserName || item.CreatedByUserName,
    createdAt: item.createdAt || item.CreatedAt,
    issuedByUserName: item.issuedByUserName || item.IssuedByUserName,
    issuedAt: item.issuedAt || item.IssuedAt,
    wasExceptionalIssue: item.wasExceptionalIssue ?? item.WasExceptionalIssue ?? false,
    exceptionalReason: item.exceptionalReason || item.ExceptionalReason,
    authorizedBySuperiorUserName: item.authorizedBySuperiorUserName || item.AuthorizedBySuperiorUserName,
    passengerPublicIds: item.passengerPublicIds || item.PassengerPublicIds || [],
    passengerNames: item.passengerNames || item.PassengerNames || [],
  };
}

function downloadBlob(blob, fileName) {
  const url = window.URL.createObjectURL(blob);
  const link = document.createElement("a");
  link.href = url;
  link.setAttribute("download", fileName);
  document.body.appendChild(link);
  link.click();
  link.remove();
  window.setTimeout(() => window.URL.revokeObjectURL(url), 1000);
}

function ScopeSelector({ label, value, selectedPassengerIds, passengers, onScopeChange, onPassengersChange }) {
  const togglePassenger = (passengerId) => {
    onPassengersChange(
      selectedPassengerIds.includes(passengerId)
        ? selectedPassengerIds.filter((id) => id !== passengerId)
        : [...selectedPassengerIds, passengerId]
    );
  };

  return (
    <div className="space-y-3">
      <label className="text-[11px] font-black uppercase tracking-widest text-slate-400">{label}</label>
      <select
        value={value}
        onChange={(event) => onScopeChange(event.target.value)}
        className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
      >
        {VOUCHER_SCOPES.map((scope) => (
          <option key={scope.value} value={scope.value}>
            {scope.label}
          </option>
        ))}
      </select>

      {value === "PasajerosSeleccionados" ? (
        <div className="grid gap-2 sm:grid-cols-2">
          {passengers.length === 0 ? (
            <div className="rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-semibold text-amber-800 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200">
              Esta reserva no tiene pasajeros cargados.
            </div>
          ) : (
            passengers.map((passenger) => {
              const passengerId = getPassengerId(passenger);
              return (
                <label
                  key={passengerId}
                  className="flex items-center gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2 text-sm font-semibold text-slate-700 dark:border-slate-700 dark:bg-slate-900/60 dark:text-slate-200"
                >
                  <input
                    type="checkbox"
                    checked={selectedPassengerIds.includes(passengerId)}
                    onChange={() => togglePassenger(passengerId)}
                    className="h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                  />
                  <span className="truncate">{getPassengerName(passenger)}</span>
                </label>
              );
            })
          )}
        </div>
      ) : null}
    </div>
  );
}

export function ReservaVoucherTab({ reservaId, reserva }) {
  const passengers = useMemo(() => (Array.isArray(reserva?.passengers) ? reserva.passengers : []), [reserva]);
  const [vouchers, setVouchers] = useState([]);
  const [loading, setLoading] = useState(true);
  const [generating, setGenerating] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [issuingId, setIssuingId] = useState(null);
  const [downloadingId, setDownloadingId] = useState(null);
  const [generateScope, setGenerateScope] = useState("ReservaCompleta");
  const [generatePassengerIds, setGeneratePassengerIds] = useState([]);
  const [uploadScope, setUploadScope] = useState("ReservaCompleta");
  const [uploadPassengerIds, setUploadPassengerIds] = useState([]);
  const [externalOrigin, setExternalOrigin] = useState("Operador externo");
  const [externalFile, setExternalFile] = useState(null);
  const [issueReason, setIssueReason] = useState("");
  const [exceptionalReason, setExceptionalReason] = useState("");
  const [authorizedBySuperiorUserId, setAuthorizedBySuperiorUserId] = useState("");

  const outstandingBalance = Number(reserva?.balance ?? 0) > 0;

  const fetchVouchers = useCallback(async () => {
    if (!reservaId) return;
    try {
      setLoading(true);
      const data = await api.get(`/reservas/${reservaId}/vouchers`);
      setVouchers(Array.isArray(data) ? data.map(normalizeVoucher) : []);
    } catch (error) {
      console.error("Error loading vouchers:", error);
      toast.error(getApiErrorMessage(error, "No se pudieron cargar los vouchers."));
    } finally {
      setLoading(false);
    }
  }, [reservaId]);

  useEffect(() => {
    fetchVouchers();
  }, [fetchVouchers]);

  const validateScope = (scope, passengerIds) => {
    if (scope === "PasajerosSeleccionados" && passengerIds.length === 0) {
      toast.error("Selecciona al menos un pasajero para este alcance.");
      return false;
    }

    if (scope === "TodosLosPasajeros" && passengers.length === 0) {
      toast.error("La reserva no tiene pasajeros para asociar.");
      return false;
    }

    return true;
  };

  const handleGenerate = async () => {
    if (!validateScope(generateScope, generatePassengerIds)) return;

    try {
      setGenerating(true);
      await api.post(`/reservas/${reservaId}/vouchers/generate`, {
        scope: generateScope,
        passengerIds: generateScope === "PasajerosSeleccionados" ? generatePassengerIds : [],
      });
      toast.success("Voucher generado como borrador.");
      await fetchVouchers();
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo generar el voucher."));
    } finally {
      setGenerating(false);
    }
  };

  const handleUploadExternal = async () => {
    if (!externalFile) {
      toast.error("Selecciona el archivo del voucher externo.");
      return;
    }

    if (!externalOrigin.trim()) {
      toast.error("Indica el origen del voucher externo.");
      return;
    }

    if (!validateScope(uploadScope, uploadPassengerIds)) return;

    const formData = new FormData();
    formData.append("file", externalFile);
    formData.append("scope", uploadScope);
    formData.append("externalOrigin", externalOrigin.trim());
    if (uploadScope === "PasajerosSeleccionados") {
      uploadPassengerIds.forEach((passengerId) => formData.append("passengerIds", passengerId));
    }

    try {
      setUploading(true);
      await api.post(`/reservas/${reservaId}/vouchers/external`, formData);
      toast.success("Voucher externo cargado.");
      setExternalFile(null);
      const input = document.getElementById(`externalVoucherInput-${reservaId}`);
      if (input) input.value = "";
      await fetchVouchers();
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo cargar el voucher externo."));
    } finally {
      setUploading(false);
    }
  };

  const handleIssue = async (voucher) => {
    if (outstandingBalance && exceptionalReason.trim().length < 10) {
      toast.error("Para emitir con saldo pendiente, indica un motivo excepcional de al menos 10 caracteres.");
      return;
    }

    try {
      setIssuingId(voucher.publicId);
      await api.post(`/vouchers/${voucher.publicId}/issue`, {
        reason: issueReason.trim() || null,
        exceptionalReason: outstandingBalance ? exceptionalReason.trim() : null,
        authorizedBySuperiorUserId: outstandingBalance ? authorizedBySuperiorUserId.trim() || null : null,
      });
      toast.success("Voucher emitido correctamente.");
      await fetchVouchers();
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo emitir el voucher."));
    } finally {
      setIssuingId(null);
    }
  };

  const handleDownload = async (voucher) => {
    try {
      setDownloadingId(voucher.publicId);
      const blob = await api.get(`/vouchers/${voucher.publicId}/download`, { responseType: "blob" });
      downloadBlob(blob, voucher.fileName || `voucher-${reservaId}.pdf`);
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo descargar el voucher."));
    } finally {
      setDownloadingId(null);
    }
  };

  return (
    <div className="space-y-6">
      {outstandingBalance ? (
        <div className="flex items-start gap-3 rounded-xl border border-amber-200 bg-amber-50 px-4 py-3 text-amber-900 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200">
          <AlertTriangle className="mt-0.5 h-5 w-5 flex-shrink-0" />
          <div>
            <div className="text-sm font-black">La reserva tiene saldo pendiente: {formatMoney(reserva?.balance)}</div>
            <div className="mt-1 text-xs font-medium">
              La emision normal queda bloqueada. Para emitir, se requiere usuario Admin o superior autorizante y motivo auditado.
            </div>
          </div>
        </div>
      ) : null}

      <div className="grid gap-5 xl:grid-cols-2">
        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <div className="mb-4 flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-indigo-100 text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
              <FilePlus2 className="h-5 w-5" />
            </div>
            <div>
              <h3 className="text-sm font-black text-slate-900 dark:text-white">Generar voucher</h3>
              <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">Crea un borrador asociado a la reserva o pasajeros.</p>
            </div>
          </div>

          <div className="space-y-4">
            <ScopeSelector
              label="Alcance del voucher"
              value={generateScope}
              selectedPassengerIds={generatePassengerIds}
              passengers={passengers}
              onScopeChange={setGenerateScope}
              onPassengersChange={setGeneratePassengerIds}
            />
            <button
              type="button"
              onClick={handleGenerate}
              disabled={generating}
              className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-indigo-600 px-4 py-3 text-sm font-black text-white transition hover:bg-indigo-700 disabled:opacity-60"
            >
              {generating ? <Loader2 className="h-4 w-4 animate-spin" /> : <FilePlus2 className="h-4 w-4" />}
              Generar voucher
            </button>
          </div>
        </div>

        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <div className="mb-4 flex items-center gap-3">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300">
              <UploadCloud className="h-5 w-5" />
            </div>
            <div>
              <h3 className="text-sm font-black text-slate-900 dark:text-white">Cargar voucher externo</h3>
              <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">Registra archivos entregados por operadores.</p>
            </div>
          </div>

          <div className="space-y-4">
            <ScopeSelector
              label="Alcance del archivo externo"
              value={uploadScope}
              selectedPassengerIds={uploadPassengerIds}
              passengers={passengers}
              onScopeChange={setUploadScope}
              onPassengersChange={setUploadPassengerIds}
            />
            <div className="grid gap-3 sm:grid-cols-[1fr,1.2fr]">
              <input
                value={externalOrigin}
                onChange={(event) => setExternalOrigin(event.target.value)}
                className="rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-emerald-500 focus:ring-2 focus:ring-emerald-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
                placeholder="Operador externo"
              />
              <input
                id={`externalVoucherInput-${reservaId}`}
                type="file"
                onChange={(event) => setExternalFile(event.target.files?.[0] || null)}
                accept="image/*,application/pdf,.doc,.docx,.xls,.xlsx"
                className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 file:mr-3 file:rounded-lg file:border-0 file:bg-slate-100 file:px-3 file:py-1.5 file:text-xs file:font-bold file:text-slate-700 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 dark:file:bg-slate-800 dark:file:text-slate-200"
              />
            </div>
            <button
              type="button"
              onClick={handleUploadExternal}
              disabled={uploading}
              className="inline-flex w-full items-center justify-center gap-2 rounded-xl bg-emerald-600 px-4 py-3 text-sm font-black text-white transition hover:bg-emerald-700 disabled:opacity-60"
            >
              {uploading ? <Loader2 className="h-4 w-4 animate-spin" /> : <UploadCloud className="h-4 w-4" />}
              Cargar voucher externo
            </button>
          </div>
        </div>
      </div>

      {outstandingBalance ? (
        <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <h3 className="text-sm font-black text-slate-900 dark:text-white">Autorizacion excepcional para emision</h3>
          <div className="mt-4 grid gap-3 lg:grid-cols-[1.2fr,1fr]">
            <textarea
              value={exceptionalReason}
              onChange={(event) => setExceptionalReason(event.target.value)}
              rows={3}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-amber-500 focus:ring-2 focus:ring-amber-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
              placeholder="Motivo obligatorio para emitir con saldo pendiente"
            />
            <input
              value={authorizedBySuperiorUserId}
              onChange={(event) => setAuthorizedBySuperiorUserId(event.target.value)}
              className="rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-amber-500 focus:ring-2 focus:ring-amber-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
              placeholder="ID publico del superior autorizante (si no sos Admin)"
            />
          </div>
        </div>
      ) : null}

      <div className="rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
        <div className="flex flex-col gap-3 border-b border-slate-100 px-5 py-4 dark:border-slate-800 sm:flex-row sm:items-center sm:justify-between">
          <div>
            <h3 className="text-sm font-black text-slate-900 dark:text-white">Vouchers de la reserva</h3>
            <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">Generados, emitidos o cargados desde operador externo.</p>
          </div>
          <input
            value={issueReason}
            onChange={(event) => setIssueReason(event.target.value)}
            className="rounded-xl border border-slate-200 bg-white px-3 py-2 text-sm font-semibold text-slate-700 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200 sm:max-w-xs"
            placeholder="Motivo opcional de emision"
          />
        </div>

        {loading ? (
          <div className="flex justify-center p-10">
            <Loader2 className="h-8 w-8 animate-spin text-indigo-500" />
          </div>
        ) : vouchers.length === 0 ? (
          <div className="py-12 text-center text-sm text-slate-500">
            <FileText className="mx-auto mb-3 h-10 w-10 text-slate-300" />
            No hay vouchers para esta reserva.
          </div>
        ) : (
          <div className="divide-y divide-slate-100 dark:divide-slate-800">
            {vouchers.map((voucher) => (
              <div key={voucher.publicId} className="p-5">
                <div className="flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
                  <div className="min-w-0 space-y-2">
                    <div className="flex flex-wrap items-center gap-2">
                      <span className="rounded-full bg-slate-100 px-2.5 py-1 text-[10px] font-black uppercase tracking-widest text-slate-600 dark:bg-slate-800 dark:text-slate-300">
                        {formatStatus(voucher.status)}
                      </span>
                      <span className="rounded-full bg-indigo-50 px-2.5 py-1 text-[10px] font-black uppercase tracking-widest text-indigo-700 dark:bg-indigo-900/30 dark:text-indigo-300">
                        {formatScope(voucher.scope)}
                      </span>
                      {voucher.canSend ? (
                        <span className="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-2.5 py-1 text-[10px] font-black uppercase tracking-widest text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300">
                          <CheckCircle2 className="h-3 w-3" />
                          Enviable
                        </span>
                      ) : null}
                      {voucher.wasExceptionalIssue ? (
                        <span className="rounded-full bg-amber-50 px-2.5 py-1 text-[10px] font-black uppercase tracking-widest text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
                          Excepcional
                        </span>
                      ) : null}
                    </div>
                    <div className="truncate text-sm font-black text-slate-900 dark:text-white">{voucher.fileName}</div>
                    <div className="flex flex-wrap gap-x-4 gap-y-1 text-xs font-semibold text-slate-500 dark:text-slate-400">
                      <span>Creado: {formatDateTime(voucher.createdAt)}</span>
                      {voucher.createdByUserName ? <span>Por: {voucher.createdByUserName}</span> : null}
                      {voucher.issuedAt ? <span>Emitido: {formatDateTime(voucher.issuedAt)}</span> : null}
                      {voucher.issuedByUserName ? <span>Emisor: {voucher.issuedByUserName}</span> : null}
                      {voucher.externalOrigin ? <span>Origen: {voucher.externalOrigin}</span> : null}
                    </div>
                    {voucher.passengerNames.length > 0 ? (
                      <div className="text-xs font-semibold text-slate-600 dark:text-slate-300">
                        Pasajeros: {voucher.passengerNames.join(", ")}
                      </div>
                    ) : null}
                    {voucher.exceptionalReason ? (
                      <div className="rounded-xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs font-semibold text-amber-900 dark:border-amber-900/40 dark:bg-amber-900/20 dark:text-amber-200">
                        Motivo excepcional: {voucher.exceptionalReason}
                        {voucher.authorizedBySuperiorUserName ? ` - Autorizo: ${voucher.authorizedBySuperiorUserName}` : ""}
                      </div>
                    ) : null}
                  </div>

                  <div className="flex flex-wrap gap-2 lg:justify-end">
                    <button
                      type="button"
                      onClick={() => handleDownload(voucher)}
                      disabled={downloadingId === voucher.publicId}
                      className="inline-flex items-center gap-2 rounded-xl border border-slate-200 px-3 py-2 text-sm font-bold text-slate-700 transition hover:bg-slate-50 disabled:opacity-60 dark:border-slate-700 dark:text-slate-200 dark:hover:bg-slate-800"
                    >
                      {downloadingId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <Download className="h-4 w-4" />}
                      Descargar
                    </button>
                    {voucher.status === "Draft" ? (
                      <button
                        type="button"
                        onClick={() => handleIssue(voucher)}
                        disabled={issuingId === voucher.publicId}
                        className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-3 py-2 text-sm font-bold text-white transition hover:bg-slate-800 disabled:opacity-60 dark:bg-slate-100 dark:text-slate-900 dark:hover:bg-white"
                      >
                        {issuingId === voucher.publicId ? <Loader2 className="h-4 w-4 animate-spin" /> : <CheckCircle2 className="h-4 w-4" />}
                        Emitir
                      </button>
                    ) : null}
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
