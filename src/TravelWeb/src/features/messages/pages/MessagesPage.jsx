import { useEffect, useMemo, useState } from "react";
import { AlertTriangle, CheckCircle2, Loader2, MessageSquare, Phone, Search, Send, UserRound } from "lucide-react";
import { toast } from "sonner";
import { api } from "../../../api";
import { getApiErrorMessage } from "../../../lib/errors";

function normalizeVoucher(item) {
  return {
    publicId: item.publicId || item.PublicId,
    status: item.status || item.Status,
    scope: item.scope || item.Scope,
    fileName: item.fileName || item.FileName || "voucher.pdf",
    canSend: item.canSend ?? item.CanSend ?? false,
    reservationHasOutstandingBalance: item.reservationHasOutstandingBalance ?? item.ReservationHasOutstandingBalance ?? false,
    outstandingBalance: item.outstandingBalance ?? item.OutstandingBalance ?? 0,
    passengerNames: item.passengerNames || item.PassengerNames || [],
  };
}

function normalizeRecipient(item) {
  return {
    personType: item.personType || item.PersonType,
    personPublicId: item.personPublicId || item.PersonPublicId,
    reservaPublicId: item.reservaPublicId || item.ReservaPublicId,
    numeroReserva: item.numeroReserva || item.NumeroReserva,
    displayName: item.displayName || item.DisplayName,
    phone: item.phone || item.Phone,
    hasPhone: item.hasPhone ?? item.HasPhone ?? Boolean(item.phone || item.Phone),
    vouchers: (item.vouchers || item.Vouchers || []).map(normalizeVoucher),
  };
}

function formatMoney(value) {
  return Number(value || 0).toLocaleString("es-AR", { style: "currency", currency: "ARS" });
}

function formatVoucherStatus(status) {
  switch (status) {
    case "Draft":
      return "Generado";
    case "Issued":
      return "Emitido";
    case "UploadedExternal":
      return "Externo";
    default:
      return status || "Sin estado";
  }
}

function recipientKey(recipient) {
  if (!recipient) return "";
  return `${recipient.personType}:${recipient.personPublicId}:${recipient.reservaPublicId}`;
}

export default function MessagesPage() {
  const [search, setSearch] = useState("");
  const [recipients, setRecipients] = useState([]);
  const [selectedKey, setSelectedKey] = useState("");
  const [loading, setLoading] = useState(true);
  const [sendingSimple, setSendingSimple] = useState(false);
  const [sendingVoucher, setSendingVoucher] = useState(false);
  const [message, setMessage] = useState("");
  const [caption, setCaption] = useState("");
  const [selectedVoucherIds, setSelectedVoucherIds] = useState([]);
  const [exceptionalReason, setExceptionalReason] = useState("");
  const [authorizedBySuperiorUserId, setAuthorizedBySuperiorUserId] = useState("");

  const selectedRecipient = useMemo(
    () => recipients.find((recipient) => recipientKey(recipient) === selectedKey) || null,
    [recipients, selectedKey]
  );

  const selectedVouchers = useMemo(() => {
    const ids = new Set(selectedVoucherIds);
    return (selectedRecipient?.vouchers || []).filter((voucher) => ids.has(voucher.publicId));
  }, [selectedRecipient, selectedVoucherIds]);

  const selectedNeedsException = selectedVouchers.some((voucher) => voucher.reservationHasOutstandingBalance);

  const fetchRecipients = async (nextSearch = search) => {
    try {
      setLoading(true);
      const query = nextSearch.trim() ? `?search=${encodeURIComponent(nextSearch.trim())}` : "";
      const data = await api.get(`/messages/recipients${query}`);
      const normalized = Array.isArray(data) ? data.map(normalizeRecipient) : [];
      setRecipients(normalized);
      setSelectedKey((current) => {
        if (normalized.some((recipient) => recipientKey(recipient) === current)) return current;
        return normalized[0] ? recipientKey(normalized[0]) : "";
      });
    } catch (error) {
      console.error("Error loading message recipients:", error);
      toast.error(getApiErrorMessage(error, "No se pudieron cargar los destinatarios."));
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    fetchRecipients("");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    setSelectedVoucherIds([]);
    setCaption("");
    setExceptionalReason("");
    setAuthorizedBySuperiorUserId("");
  }, [selectedKey]);

  const handleSearchSubmit = (event) => {
    event.preventDefault();
    fetchRecipients(search);
  };

  const handleSendSimple = async () => {
    if (!selectedRecipient) return;
    if (!selectedRecipient.hasPhone) {
      toast.error("La persona seleccionada no tiene telefono asociado.");
      return;
    }
    if (!message.trim()) {
      toast.error("Escribe el mensaje a enviar.");
      return;
    }

    try {
      setSendingSimple(true);
      await api.post("/messages/simple", {
        personType: selectedRecipient.personType,
        personId: selectedRecipient.personPublicId,
        reservaId: selectedRecipient.reservaPublicId,
        message: message.trim(),
      });
      toast.success("Mensaje enviado por WhatsApp.");
      setMessage("");
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo enviar el mensaje."));
    } finally {
      setSendingSimple(false);
    }
  };

  const handleToggleVoucher = (voucher) => {
    setSelectedVoucherIds((current) =>
      current.includes(voucher.publicId)
        ? current.filter((id) => id !== voucher.publicId)
        : [...current, voucher.publicId]
    );
  };

  const handleSendVoucher = async () => {
    if (!selectedRecipient) return;
    if (!selectedRecipient.hasPhone) {
      toast.error("La persona seleccionada no tiene telefono asociado.");
      return;
    }
    if (selectedVoucherIds.length === 0) {
      toast.error("Selecciona al menos un voucher.");
      return;
    }
    if (selectedNeedsException && exceptionalReason.trim().length < 10) {
      toast.error("Para enviar con saldo pendiente, indica un motivo excepcional de al menos 10 caracteres.");
      return;
    }

    try {
      setSendingVoucher(true);
      await api.post("/messages/voucher", {
        personType: selectedRecipient.personType,
        personId: selectedRecipient.personPublicId,
        reservaId: selectedRecipient.reservaPublicId,
        voucherIds: selectedVoucherIds,
        caption: caption.trim() || null,
        exception: selectedNeedsException
          ? {
              exceptionalReason: exceptionalReason.trim(),
              authorizedBySuperiorUserId: authorizedBySuperiorUserId.trim() || null,
            }
          : null,
      });
      toast.success("Voucher enviado por WhatsApp.");
      setSelectedVoucherIds([]);
      setCaption("");
      setExceptionalReason("");
      setAuthorizedBySuperiorUserId("");
      fetchRecipients(search);
    } catch (error) {
      toast.error(getApiErrorMessage(error, "No se pudo enviar el voucher."));
    } finally {
      setSendingVoucher(false);
    }
  };

  return (
    <div className="mx-auto max-w-7xl space-y-6 p-4 sm:p-6 lg:p-8">
      <div className="flex flex-col gap-4 md:flex-row md:items-end md:justify-between">
        <div>
          <h1 className="text-2xl font-black text-slate-900 dark:text-white">Mensajes</h1>
          <p className="mt-1 text-sm font-medium text-slate-500 dark:text-slate-400">
            Envio de mensajes y vouchers por WhatsApp desde reservas asociadas.
          </p>
        </div>
        <form onSubmit={handleSearchSubmit} className="flex w-full gap-2 md:max-w-md">
          <div className="relative flex-1">
            <Search className="pointer-events-none absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-slate-400" />
            <input
              value={search}
              onChange={(event) => setSearch(event.target.value)}
              className="w-full rounded-xl border border-slate-200 bg-white py-2.5 pl-9 pr-3 text-sm font-semibold text-slate-700 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
              placeholder="Buscar cliente, pasajero o reserva"
            />
          </div>
          <button
            type="submit"
            className="inline-flex items-center gap-2 rounded-xl bg-slate-900 px-4 py-2.5 text-sm font-bold text-white transition hover:bg-slate-800 dark:bg-slate-100 dark:text-slate-900 dark:hover:bg-white"
          >
            <Search className="h-4 w-4" />
            Buscar
          </button>
        </form>
      </div>

      <div className="grid gap-6 lg:grid-cols-[360px,1fr]">
        <div className="rounded-xl border border-slate-200 bg-white shadow-sm dark:border-slate-800 dark:bg-slate-900">
          <div className="border-b border-slate-100 px-4 py-3 dark:border-slate-800">
            <div className="text-sm font-black text-slate-900 dark:text-white">Destinatarios</div>
            <div className="text-xs font-semibold text-slate-500 dark:text-slate-400">{recipients.length} persona(s) con reserva</div>
          </div>

          {loading ? (
            <div className="flex justify-center p-10">
              <Loader2 className="h-8 w-8 animate-spin text-indigo-500" />
            </div>
          ) : recipients.length === 0 ? (
            <div className="p-8 text-center text-sm font-medium text-slate-500">
              <UserRound className="mx-auto mb-3 h-9 w-9 text-slate-300" />
              No hay destinatarios para mostrar.
            </div>
          ) : (
            <div className="max-h-[680px] divide-y divide-slate-100 overflow-y-auto dark:divide-slate-800">
              {recipients.map((recipient) => {
                const key = recipientKey(recipient);
                const isSelected = key === selectedKey;
                return (
                  <button
                    key={key}
                    type="button"
                    onClick={() => setSelectedKey(key)}
                    className={`w-full px-4 py-3 text-left transition ${
                      isSelected ? "bg-indigo-50 dark:bg-indigo-900/20" : "hover:bg-slate-50 dark:hover:bg-slate-800/50"
                    }`}
                  >
                    <div className="flex items-start justify-between gap-3">
                      <div className="min-w-0">
                        <div className="truncate text-sm font-black text-slate-900 dark:text-white">{recipient.displayName}</div>
                        <div className="mt-0.5 text-xs font-semibold text-slate-500 dark:text-slate-400">
                          Reserva {recipient.numeroReserva || recipient.reservaPublicId}
                        </div>
                      </div>
                      <span
                        className={`inline-flex items-center gap-1 rounded-full px-2 py-1 text-[10px] font-black uppercase ${
                          recipient.hasPhone
                            ? "bg-emerald-50 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300"
                            : "bg-rose-50 text-rose-700 dark:bg-rose-900/30 dark:text-rose-300"
                        }`}
                      >
                        <Phone className="h-3 w-3" />
                        {recipient.hasPhone ? "Telefono" : "Sin telefono"}
                      </span>
                    </div>
                  </button>
                );
              })}
            </div>
          )}
        </div>

        <div className="space-y-6">
          {!selectedRecipient ? (
            <div className="rounded-xl border border-dashed border-slate-300 bg-white p-12 text-center text-slate-500 dark:border-slate-700 dark:bg-slate-900">
              <MessageSquare className="mx-auto mb-3 h-10 w-10 text-slate-300" />
              Selecciona un destinatario para enviar mensajes.
            </div>
          ) : (
            <>
              <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
                <div className="flex flex-col gap-3 md:flex-row md:items-center md:justify-between">
                  <div>
                    <div className="text-xl font-black text-slate-900 dark:text-white">{selectedRecipient.displayName}</div>
                    <div className="mt-1 flex flex-wrap gap-x-4 gap-y-1 text-sm font-semibold text-slate-500 dark:text-slate-400">
                      <span>Reserva {selectedRecipient.numeroReserva || selectedRecipient.reservaPublicId}</span>
                      <span>{String(selectedRecipient.personType).toLowerCase() === "passenger" ? "Pasajero" : "Cliente"}</span>
                      <span>{selectedRecipient.phone || "Sin telefono"}</span>
                    </div>
                  </div>
                  {selectedRecipient.hasPhone ? (
                    <span className="inline-flex items-center gap-2 rounded-full bg-emerald-50 px-3 py-1.5 text-xs font-black uppercase tracking-widest text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300">
                      <CheckCircle2 className="h-4 w-4" />
                      Listo para WhatsApp
                    </span>
                  ) : (
                    <span className="inline-flex items-center gap-2 rounded-full bg-rose-50 px-3 py-1.5 text-xs font-black uppercase tracking-widest text-rose-700 dark:bg-rose-900/30 dark:text-rose-300">
                      <AlertTriangle className="h-4 w-4" />
                      Falta telefono
                    </span>
                  )}
                </div>
              </div>

              <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
                <div className="mb-3 flex items-center gap-2">
                  <MessageSquare className="h-4 w-4 text-indigo-600" />
                  <h2 className="text-sm font-black text-slate-900 dark:text-white">Mensaje simple</h2>
                </div>
                <textarea
                  value={message}
                  onChange={(event) => setMessage(event.target.value)}
                  rows={4}
                  className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
                  placeholder="Escribe el mensaje..."
                />
                <div className="mt-3 flex justify-end">
                  <button
                    type="button"
                    onClick={handleSendSimple}
                    disabled={sendingSimple || !selectedRecipient.hasPhone}
                    className="inline-flex items-center gap-2 rounded-xl bg-indigo-600 px-4 py-2.5 text-sm font-black text-white transition hover:bg-indigo-700 disabled:opacity-60"
                  >
                    {sendingSimple ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                    Enviar mensaje
                  </button>
                </div>
              </div>

              <div className="rounded-xl border border-slate-200 bg-white p-5 shadow-sm dark:border-slate-800 dark:bg-slate-900">
                <div className="mb-4 flex flex-col gap-1">
                  <div className="flex items-center gap-2">
                    <FileIcon />
                    <h2 className="text-sm font-black text-slate-900 dark:text-white">Enviar voucher</h2>
                  </div>
                  <p className="text-xs font-semibold text-slate-500 dark:text-slate-400">
                    Solo se muestran los vouchers asociados a esta reserva y destinatario.
                  </p>
                </div>

                {selectedRecipient.vouchers.length === 0 ? (
                  <div className="rounded-xl border border-dashed border-slate-300 p-8 text-center text-sm font-medium text-slate-500 dark:border-slate-700">
                    No hay vouchers disponibles para esta persona.
                  </div>
                ) : (
                  <div className="space-y-3">
                    {selectedRecipient.vouchers.map((voucher) => (
                      <label
                        key={voucher.publicId}
                        className={`flex items-start gap-3 rounded-xl border px-4 py-3 ${
                          voucher.canSend
                            ? "cursor-pointer border-slate-200 bg-slate-50 dark:border-slate-700 dark:bg-slate-900/50"
                            : "border-slate-200 bg-slate-100 opacity-70 dark:border-slate-800 dark:bg-slate-800/60"
                        }`}
                      >
                        <input
                          type="checkbox"
                          checked={selectedVoucherIds.includes(voucher.publicId)}
                          onChange={() => handleToggleVoucher(voucher)}
                          disabled={!voucher.canSend}
                          className="mt-1 h-4 w-4 rounded border-slate-300 text-indigo-600 focus:ring-indigo-500"
                        />
                        <div className="min-w-0 flex-1">
                          <div className="flex flex-wrap items-center gap-2">
                            <span className="truncate text-sm font-black text-slate-900 dark:text-white">{voucher.fileName}</span>
                            <span className="rounded-full bg-slate-200 px-2 py-0.5 text-[10px] font-black uppercase text-slate-700 dark:bg-slate-800 dark:text-slate-300">
                              {formatVoucherStatus(voucher.status)}
                            </span>
                            {!voucher.canSend ? (
                              <span className="rounded-full bg-amber-50 px-2 py-0.5 text-[10px] font-black uppercase text-amber-700 dark:bg-amber-900/30 dark:text-amber-300">
                                No habilitado
                              </span>
                            ) : null}
                          </div>
                          <div className="mt-1 text-xs font-semibold text-slate-500 dark:text-slate-400">
                            {voucher.passengerNames.length > 0 ? `Pasajeros: ${voucher.passengerNames.join(", ")}` : "Voucher general de reserva"}
                          </div>
                          {voucher.reservationHasOutstandingBalance ? (
                            <div className="mt-2 inline-flex rounded-lg bg-amber-50 px-2 py-1 text-xs font-bold text-amber-800 dark:bg-amber-900/20 dark:text-amber-200">
                              Reserva con saldo pendiente: {formatMoney(voucher.outstandingBalance)}
                            </div>
                          ) : null}
                        </div>
                      </label>
                    ))}
                  </div>
                )}

                <div className="mt-4 space-y-3">
                  <textarea
                    value={caption}
                    onChange={(event) => setCaption(event.target.value)}
                    rows={3}
                    className="w-full rounded-xl border border-slate-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-indigo-500 focus:ring-2 focus:ring-indigo-500/20 dark:border-slate-700 dark:bg-slate-900 dark:text-slate-200"
                    placeholder="Caption opcional para el envio"
                  />

                  {selectedNeedsException ? (
                    <div className="grid gap-3 lg:grid-cols-[1.2fr,1fr]">
                      <textarea
                        value={exceptionalReason}
                        onChange={(event) => setExceptionalReason(event.target.value)}
                        rows={3}
                        className="rounded-xl border border-amber-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-amber-500 focus:ring-2 focus:ring-amber-500/20 dark:border-amber-900/40 dark:bg-slate-900 dark:text-slate-200"
                        placeholder="Motivo obligatorio para enviar con saldo pendiente"
                      />
                      <input
                        value={authorizedBySuperiorUserId}
                        onChange={(event) => setAuthorizedBySuperiorUserId(event.target.value)}
                        className="rounded-xl border border-amber-200 bg-white px-3 py-2.5 text-sm font-semibold text-slate-700 outline-none transition focus:border-amber-500 focus:ring-2 focus:ring-amber-500/20 dark:border-amber-900/40 dark:bg-slate-900 dark:text-slate-200"
                        placeholder="ID publico del superior autorizante"
                      />
                    </div>
                  ) : null}

                  <div className="flex justify-end">
                    <button
                      type="button"
                      onClick={handleSendVoucher}
                      disabled={sendingVoucher || !selectedRecipient.hasPhone || selectedVoucherIds.length === 0}
                      className="inline-flex items-center gap-2 rounded-xl bg-emerald-600 px-4 py-2.5 text-sm font-black text-white transition hover:bg-emerald-700 disabled:opacity-60"
                    >
                      {sendingVoucher ? <Loader2 className="h-4 w-4 animate-spin" /> : <Send className="h-4 w-4" />}
                      Enviar voucher
                    </button>
                  </div>
                </div>
              </div>
            </>
          )}
        </div>
      </div>
    </div>
  );
}

function FileIcon() {
  return (
    <span className="inline-flex h-5 w-5 items-center justify-center rounded bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300">
      <MessageSquare className="h-3.5 w-3.5" />
    </span>
  );
}
