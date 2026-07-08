# 2026-07-08 — El paso de la multa vive en la ficha (tanda completa y deployada)

Continuación de `2026-07-08-fix-nc-atascada-y-ojos-en-el-servidor.md`. La sesión
anterior dejó esta tanda a medias (cortada por cuota, sin commitear y sin
reviews); esta sesión la terminó, la pasó por los 4 gates y la deployó.

## Qué problema resuelve (en criollo)

Gastón chocó con la multa del operador en dólares: la bandeja le abrió un
formulario que pedía un "tipo de cargo" que nadie entiende, solo aceptaba
pesos, y le contestó "hablá con administración"… a él, que ES la administración.
Y cuando una ND quedaba trabada, el cartel de la ficha lo mandaba a la bandeja,
que a su vez lo devolvía a la ficha: un loop sin salida.

## Qué se construyó

1. **La ficha muestra el paso de la multa y ofrece UNA acción por estado.**
   Regla pura de dominio (`OperatorPenaltySituationRules`, 7 estados) + read-model
   (`GetOperatorPenaltySituationAsync`) + panel nuevo en la ficha
   (`OperatorPenaltyStepPanel`):
   - ND fallida → **Reintentar**.
   - ND trabada por moneda → **Corregir monto y moneda** en UN paso (reemplaza
     el circuito waive→deshacer→re-confirmar). Endpoint nuevo
     `PATCH correct-penalty`, con permiso, auditoría (antes→después) y candados.
   - Confirmada sin ND → **Emitir ahora**.
   - Encolada → cartel informativo. Cerrada sin multa → rastro con quién y cuándo.
2. **Moneda y monto precargados de la verdad.** `elegirMonedaSugeridaParaMulta`:
   1º la moneda ya confirmada de la situación, 2º la moneda única de la factura
   emitida, 3º recién ahí el default. Nunca más USD silencioso.
3. **La bandeja de NDs es lista pasiva.** Filas = links reales a la ficha (se
   puede abrir en pestaña nueva), sin botones, sin error crudo de AFIP: dice
   "qué falta" en criollo.
4. **Voz de los avisos (8 reglas nuevas en `guia-ux-gaston.md`).** Sin "nota de
   débito"/"CAE"/"AFIP" fuera de las pantallas de facturación, "devolución" en
   vez de "nota de crédito", sin "consultá con administración", sin DOL/PES,
   siempre F-2026-xxxx.

## Lo que encontraron los reviews (y se arregló antes de deployar)

- **[Bloqueante backend] Carrera "corregir" vs "cerrar sin multa".** El chequeo
  de estado se hacía antes del lock; si un waive se colaba en el medio, la
  corrección recortaba el RefundCap de una multa ya cerrada = descuadre
  silencioso en la cuenta del operador. Fix: re-chequeo de
  `PenaltyStatus == Confirmed` DENTRO del lock, después del Reload, con la misma
  invariante (INV-CORRECT-001). El test de regresión reproduce la carrera con
  dos DbContext sobre la misma base y FALLA sin el guard.
- **[Bloqueantes frontend] La ficha no estaba cableada** (el panel y sus helpers
  eran código huérfano → el loop ficha↔bandeja seguía vivo), la moneda sugerida
  no se pasaba nunca, y un cartel nuevo mostraba "F-F-2026-xxxx". Todo cableado
  y corregido.
- **[Arranque] 5 tests viejos rotos** por NRE: el read-model nuevo se llamaba
  sin defensa contra dobles de test parciales. Fix defensivo + mocks
  actualizados.
- Barrida final de voz: se cazaron 6 "consultá con administración"/"nota de
  débito"/"pass-through" que quedaban en textos nuevos y en la ficha.

## Gates y números

- backend-dotnet-reviewer: 1 bloqueante → fix → **re-review APROBADO**.
- frontend-reviewer: BLOQUEADO (3) → fixes → **re-review APROBADO** (verificó
  TDZ y no-duplicación de carteles).
- security-data-risk-reviewer: **APROBADO** (candados de ecbdc0b y 487c7d7
  respetados; doble-CAE cerrado dentro del lock).
- data-exposure-reviewer: **APROBADO** (token de estado jamás crudo, errores
  saneados) + re-review del panel vivo APROBADO.
- Unit backend **3284/3284**, front **1841/1841**, vite build OK, chequeo
  estático TDZ limpio. Commit **`8ecfdff`**.

## Pendientes que dejaron los reviews (NO bloqueantes, anotados)

- **Consulta para el contador** (security): validez fiscal de re-emitir una ND
  tras corregir monto/moneda cuando la previa quedó rechazada sin CAE
  (asociación CbtesAsoc / RG 4540). El razonamiento técnico es sólido
  (comprobante sin CAE no duplica documento), pero es criterio de matriculado.
- `RevertedAt/RevertedByName` viajan siempre null (falta migración futura).
- El estado `Done` de la multa no pinta cartel propio (para no duplicar el chip
  "Multa por anulación pendiente de cobro" de `moneyStatus.js`) — mirarlo con
  datos reales en el dogfood.
- "pass-through" y jerga residual: quedó un barrido menor pre-existente fuera
  de esta tanda.

## Smoke en la app real

No se pudo correr navegador local (misma limitación de siempre). Mitigado con:
chequeo estático de TDZ (ESLint no-use-before-define sobre los archivos
tocados), re-review explícito de orden de declaraciones, y build de producción
OK. **El primer vistazo de Gastón a una ficha anulada con multa ES el smoke**:
mirar que el cartel del paso aparezca una sola vez y que el botón haga lo que
dice.

## El caso real BC 10 (la multa USD de Gastón)

Con esto deployado, la ficha de esa reserva debería mostrar directamente
"Corregir monto y moneda" (estado ManualReview) — ya no hace falta el circuito
waive→deshacer→re-confirmar que había quedado documentado como workaround.
Cargar USD 200 ahí es el primer test real del candado de moneda + la banda de
cotización de ARCA (validación 10119) con TC congelado.
