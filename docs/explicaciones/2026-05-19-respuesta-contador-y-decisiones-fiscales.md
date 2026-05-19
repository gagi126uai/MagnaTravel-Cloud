# Sesion 2026-05-19 - Respuesta del contador y decisiones fiscales

Explicacion nivel trainee/junior de lo que paso en la sesion del 19 de mayo,
donde el contador volvio 2 veces con condiciones para firmar el modulo de
cancelacion. Esta sesion **no toco codigo** - fue todo gestion de decisiones
fiscales y planificacion.

## TL;DR

El contador respondio 2 veces al pedido de signoff del modulo de cancelacion.
La 2da respuesta dio luz verde al desarrollo con 2 correcciones criticas y
una lista exhaustiva de casos que requieren revision manual. **Quedan 5
preguntas de politica fiscal de Hotel** que Gaston tiene que responder antes
de arrancar a codear.

## Que paso (con ejemplo agencia)

Imagina la situacion: el modulo de cancelacion esta listo tecnico (94 tests
verdes), pero el contador es el guardian fiscal de la agencia. Antes de
"prender" el modulo en produccion, tiene que firmar que el comportamiento es
correcto fiscalmente.

Mandamos un primer mensaje pidiendo firma. El contador respondio con 9
condiciones especificas. La mas importante: el sistema solo soportaba NC
**total** y en turismo lo normal es NC **parcial** (cliente cancela, hotel
retiene penalidad, NC fiscal es por lo que efectivamente se devuelve).

Mandamos segundo mensaje aceptando las 9 condiciones. El contador respondio
con 2 correcciones nuevas:

1. **NC fiscal a ARCA**: nuestra trazabilidad interna no es suficiente. El
   request a ARCA debe llevar el comprobante asociado explicitamente.
2. **Calculo de NC parcial**: NO se calcula por lo que devuelve el operador,
   sino por la liquidacion fiscal de cancelacion del cliente.

Verificamos en codigo: el `AfipService.cs` YA envia `<CbtesAsoc>` a ARCA con
tipo, punto de venta y numero del comprobante original. La correccion 1 ya
estaba cubierta sin que el contador lo supiera.

La correccion 2 si requiere trabajo nuevo: hay que modelar la liquidacion
fiscal de cancelacion como un calculo aparte del reintegro del operador.

## Las decisiones que tomo Gaston hoy

1. **Solo Hotel por ahora**: la politica de NC parcial se modela solo para
   Hotel. Vuelo, Paquete, Traslado, Asistencia quedan "no soportados" en el
   flujo nuevo. Acota el scope para avanzar rapido.

2. **T0 para los 15 dias = "acuerdo de devolucion con el operador"**: el
   contador exige que la NC se emita dentro de 15 dias del "hecho
   documentable". Gaston eligio que ese hecho sea el momento en que el
   operador confirma cuanto va a devolver. Es el mas conservador (da mas
   tiempo).

3. **Bandeja de pendientes con bloqueo previo**: cuando un caso dispara
   revision manual, la NC NO se emite hasta aprobacion. Estados nuevos:
   `RequiresManualReview` -> `ManualReviewPending` -> `ManualReviewApproved`
   (recien ahi emite) o `ManualReviewRejected`.

4. **Umbrales parametrizados**: $500k auto, $500k-$2M admin reforzada, >$2M
   manual contable. Pero NO hardcoded - van a `OperationalFinanceSettings`
   porque la inflacion los desactualiza rapido.

## Las 15+ reglas de revision manual

El contador listo todos los casos que requieren revision manual obligatoria.
Los agrupo:

**Tiempo:**
- Mas de 15 dias desde acuerdo de devolucion.
- Mas de 90 dias desde la factura original.

**Tipo fiscal:**
- Moneda extranjera (USD, EUR).
- Factura A o tratamiento impositivo complejo (RI con IVA, percepciones).
- Importe entre $500k y $2M (admin reforzada).
- Importe > $2M (manual contable).

**Politica comercial:**
- NC parcial (siempre).
- Penalidad aplicada por la agencia.
- Fee de agencia retenido.
- Diferencia entre lo cobrado y lo facturado.

**Pagos:**
- Pagos en cuotas.
- Pagos mixtos (transferencia + tarjeta + efectivo + dolar).

**Complejidad:**
- Mas de 1 factura vinculada a la reserva.
- Mas de 1 NC previa sobre la misma factura.
- Cliente con saldo a favor en vez de reintegro.
- Operador no confirmo devolucion.
- ARCA devuelve observaciones, rechazo o inconsistencias.

## Las 5 preguntas pendientes para Gaston

**Estas son DECISIONES DE NEGOCIO**, no tecnicas. Bloquean el arranque de la
Fase 0 (documentar politica de NC para Hotel):

1. ¿La agencia factura el total al cliente, o solo su comision?
2. ¿Hay tabla de penalidades por dias de antelacion (30/15/0%), o cada
   cancelacion es manual?
3. El fee de agencia en cancelacion: ¿se devuelve, se retiene, o depende?
4. ¿Hay importes "no reintegrables" por contrato (gestion, seguro, etc.)?
5. La NC fiscal: ¿anula la factura original completa o solo la parte
   devuelta?

## Plan FC1.3 - lo que viene

| Fase | Que hace | Estimacion |
|---|---|---|
| 0 | Documentar politica de NC para Hotel | 1 sesion (despues de tener las 5 respuestas) |
| 1 | Backend: parametrizar umbrales + estados ManualReview en BC | 1 sesion |
| 2 | Backend: NC parcial + 15 dias + sistema reglas | 2 sesiones |
| 3 | Frontend: modal aprobacion + bandeja pendientes | 1-2 sesiones |
| 4 | 3 casos de prueba completos (con ARCA homologacion) | 1 sesion |
| 5 | Firma contador + prender feature flag | gestion humana |

Total estimado: 6-8 sesiones de trabajo, mas la gestion humana (firma del
contador, alineacion con la agencia para las politicas).

## Archivos creados/modificados hoy

- `docs/operations/2026-05-19-respuesta-contador-y-plan-NC-parcial.md` -
  mapeo 9 condiciones del contador vs codigo (round 1).
- Este documento.

## Lo que NO se hizo hoy

- No se toco codigo del repo.
- No se corrieron tests.
- No se mandaron mensajes (los mensajes para el contador quedaron escritos
  en el chat, no en archivos, segun nueva regla de Gaston).

## Pendientes para la proxima sesion

1. Responder las 5 preguntas de politica de Hotel (decision humana, no
   tecnica).
2. Una vez respondidas, arrancar Fase 0 con `backend-dotnet-senior`.
3. Alternativa si las preguntas no estan: arrancar Fase 1 (backend
   parametrizar) que NO depende de las politicas.

## Commits del dia

- `020e953` - docs respuesta contador + plan NC parcial.

Solo 1 commit hoy (sesion de gestion + decisiones, sin codigo nuevo).
