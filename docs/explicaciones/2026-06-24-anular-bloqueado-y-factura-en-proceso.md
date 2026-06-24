# 2026-06-24 — Dos arreglos: no se podía anular + factura encolada sin feedback

## 1. No se podía anular una reserva con factura (botón escondido)

### Síntoma
En una reserva con factura emitida (o con cobros), el botón "Anular reserva" no
aparecía / estaba deshabilitado. Justo el caso que hay que poder anular para que salga
la Nota de Crédito (y después la de Débito por la multa).

### Causa
El botón "Anular reserva" abre el flujo FORMAL de anulación (emite la NC; el panel hasta
muestra el cartel ámbar "tiene factura → se emite NC"). Pero la regla de ADR-036 apagaba
la capacidad `canCancel` cuando la reserva tenía "plata viva" (factura con CAE o cobros),
con la idea de impedir la "baja simple". Como el **único** botón es el anular formal, la
regla escondía la única salida: el sistema decía "hay que anularla" pero no daba el botón.

El guard real que impide la baja simple vive aparte (`ReservaService` línea 3600-3623) y
**no se tocó**. El flujo de anular formal (`BookingCancellationService.DraftAsync`) tiene
sus propios controles y no usa `canCancel`.

### Arreglo
Backend: capacidad propia `CanAnnul` en `ReservaCapabilityPolicy` (anular formal = deshacer
con plata viva emitiendo NC). Misma matriz de estados terminales que `CanCancel`; en estados
vivos es `true` si hay factura con CAE o cobros. Es el complemento de `CanCancel` (que da
false con plata viva). Se expone en `ReservaDto.Capabilities.CanAnnul`.

Frontend (`ReservaDetailPage.jsx`): el botón "Anular reserva" se muestra si
`canCancel.allowed || canAnnul.allowed`. Robusto: no depende del texto del motivo.

El backend revalida la anulación real aparte (`DraftAsync` exige factura activa; el guard de
baja simple sigue en `ReservaService`). `CanAnnul` es solo compuerta de UI.

> Nota: la primera versión (commit `8b441bf`) usaba un match por el texto del motivo de
> `canCancel`; este commit lo reemplaza por la capacidad dedicada.

## 2. Factura encolada sin aviso → riesgo de facturar dos veces

### Síntoma
Al emitir una factura, queda "encolada" esperando el CAE de AFIP/ARCA. Mientras tanto el
botón "Emitir factura" seguía visible (el estado de facturación solo cuenta facturas con
CAE), así que el usuario podía no darse cuenta y volver a emitir. El backend ya lo frena
con un 409, pero recién después de que el usuario llenó y mandó el formulario de nuevo.

### Arreglo
- Backend (`ReservaDto.HasInvoiceInProgress` + cálculo en `ReservaService`): expone si hay
  una factura EN PROCESO. Espejo EXACTO del guard de `InvoiceService.CreatePendingInvoice`
  (`Resultado=="PENDING" && AnnulmentStatus != Succeeded`).
- Frontend (`ReservaDetailPage.jsx`): mientras hay una en proceso, en vez del botón "Emitir
  factura" se muestra un cartel no clickeable **"Factura en proceso (esperando AFIP/ARCA)"**.

## Verificación
- Backend build 0 errores; 232 unit (capacidades + facturación + ADR-035/036) verdes.
- Frontend build de Vite OK.
- Sin migración. Sin cambios en lógica fiscal ni en guards de escritura.

## Pendiente (gate de Gastón)
- Desplegar para probar: anular reserva facturada → NC → confirmar multa → ND.
