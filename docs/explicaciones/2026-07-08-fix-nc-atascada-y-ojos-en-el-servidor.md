# 2026-07-08 (madrugada) — Fix de la anulación atascada + ojos en el servidor

Continuación directa de `2026-07-07-nc-usd-atascada-y-auditoria-multas.md`: Gastón
pasó el texto del error y con eso se destrabó todo.

## 1. Herramienta nueva: diagnóstico del VPS desde GitHub (solo lectura)

`.github/workflows/ops-diagnostico.yml` (`fece305` + fix base64 `45f769a`):
workflow manual que usa la MISMA llave SSH del deploy para traer del servidor,
en modo 100% solo-lectura: estado de contenedores/disco/memoria, logs de
`travel_api` / `travel_worker` (con filtro), y consultas SQL SELECT/WITH con
doble candado (guard en bash + `default_transaction_read_only=on` de Postgres).
Las consultas viajan en base64 porque el transporte ssh come las comillas dobles.
Requisitos que aparecieron en el camino: el token de `gh` necesitó el scope
`workflow` (Gastón lo autorizó con `gh auth refresh -s workflow`) y git quedó
usando `gh` como credential helper (`gh auth setup-git`).

ADVERTENCIA operativa: la salida queda en los logs de Actions (repo privado);
no pedir columnas con PII si no hace falta.

## 2. El diagnóstico real de la anulación USD atascada

Con logs del worker + SQL de producción quedó la película completa:

1. **00:44** — Factura C Nº 45 en dólares (id 63) emitida OK contra AFIP
   (homologación), CAE presente, TC manual 1500.
2. **00:47** — Gastón anula. El job crea la NC (id 64, `Resultado=PENDING`) y
   justo AFIP queda inalcanzable (DNS: "Name or service not known
   (wsaahomo.afip.gov.ar:443)"). La NC queda PENDING con ese texto en
   Observaciones; la factura queda `AnnulmentStatus=Failed`.
3. **01:34** — Reintento automático de Hangfire. El guard "Avoid double
   processing" solo mira NC con `Resultado="A"` → no ve la PENDING → llama
   `CreatePendingInvoice` de nuevo → Postgres la rechaza con **23505 unique
   `UX_Invoices_OnePendingPerReserva`** (índice sano haciendo su trabajo).
4. **Cascada**: la entidad del INSERT fallido queda trackeada en el DbContext
   (contexto "envenenado") → el best-effort de marcar Failed re-intenta el mismo
   INSERT y explota ("No se pudo persistir AnnulmentStatus=Failed para Invoice
   63"), y `CreateNotification` también → throw → Hangfire reintenta → loop.
5. **Bonus fuga**: el aviso al usuario interpolaba `ex.Message` crudo — a Gastón
   le llegó el hostname interno de AFIP en la campanita.

## 3. El fix (`ecbdc0b`)

En `InvoiceService.ProcessAnnulmentJob`, tres patas + un endurecimiento:

- **F1 — Reintento idempotente**: antes de crear la NC, busca una NC PENDING
  previa del mismo original y la RETOMA. Con dos candados del reviewer para
  jamás agarrar el comprobante equivocado: `TipoComprobante == cbteTipo`
  esperado (excluye las ND de multa) e `IdempotencyKey == null` (excluye las NC
  parciales, que graban su huella ANTES de ir a AFIP; la total nunca la setea).
  El switch de `cbteTipo` se movió antes del reuso.
- **F2 — "AFIP no respondió" ≠ rechazo**: `Resultado=PENDING` ya no marca
  Failed-como-rechazo ni llama `OnArcaFailedAsync` (el BC seguía esperando y
  pasaba a rechazado por un corte de red). Ahora lanza
  `InvoiceAnnulmentPendingRetryException` → Hangfire reintenta → F1 retoma.
  La rama "R" (rechazo real de ARCA) quedó intacta. El manejo de resultado se
  extrajo a `HandleCreditNoteAnnulmentResultAsync` (compartido).
- **F3 — Despoisonar el contexto**: `ChangeTracker.Clear()` al inicio del catch
  (marcar Failed y crear el aviso necesitan tracker limpio) + el aviso de error
  técnico ya no interpola `ex.Message`: copy fijo "Error técnico al anular: no
  se pudo conectar con AFIP. Se reintentará automáticamente."

**Anti-doble-emisión (confirmado por el reviewer de seguridad)**: la protección
real contra emitir DOS NC ante AFIP vive en `ProcessInvoiceJob` (idemKey
determinística + stale-key recovery que adopta el CAE ya emitido sin re-POST).
El fix la usa, no la debilita.

**Gates**: backend APROBADO (endurecimiento F1 aplicado y re-verificado),
security-data-risk APROBADO (0 bloqueantes), data-exposure APROBADO.
Suite Unit 3214/3214 (6 tests nuevos).

## 4. Cómo se destraba la reserva de Gastón

Con el fix deployado: en la ficha de la reserva atascada, **"Reintentar"** una
vez → el job retoma la NC 64 pendiente → AFIP (ya accesible) la aprueba → la
anulación se completa sola (bridge + estados). Si AFIP volviera a no responder,
ahora reintenta sin romperse.

## 5. Hallazgos anotados para después (no de esta madrugada)

- El servidor apunta a **AFIP homologación** (wsaahomo) — los comprobantes del
  dogfood NO son fiscales reales. Confirmar con Gastón que es intencional y
  planificar el switch a producción para el "listo para vender".
- Rama "AFIP RECHAZADO" del catch muestra `ex.Message` sin sanear — hoy
  inalcanzable, endurecer cuando se toque esa zona (anotado por data-exposure).
- Copy "Se reintentará automáticamente" queda impreciso si Hangfire agota los
  10 reintentos (la anulación sigue visible en la bandeja de fallidas y es
  re-disparable a mano; solo es un matiz de texto).
- Pendientes de la noche anterior: 3 preguntas de multas + desde qué pantalla
  factura (moneda automática) — siguen esperando respuesta de Gastón.
