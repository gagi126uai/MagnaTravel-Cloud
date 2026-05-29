# 🧪 Guía: probar facturación en dólares en homologación (ARCA) — 2026-05-29

> Para Gaston. Paso a paso. Esto se hace en el **ambiente de prueba** de ARCA (homologación), NUNCA directo en producción.

## ¿Qué vamos a hacer y por qué?

Ya está todo el código para facturar en dólares. Pero **antes de usarlo en serio**, hay que confirmar que ARCA lo acepta. ARCA tiene una "cancha de práctica" (homologación): le mandás facturas de mentira y te dice si están bien formadas (te da un número de autorización de prueba, el **CAE**). Si la factura en dólares vuelve **aprobada con CAE** en la práctica, recién ahí la prendemos en producción.

**Ejemplo:** antes de jugar el partido oficial, entrenás en una cancha igual para ver que la pelota entra. Si entra en la práctica, jugás el oficial tranquilo.

## ⚠️ Importante antes de empezar

- Hacé esto en una **base de datos de prueba / staging**, NO en la de producción. (Vas a prender un interruptor y crear facturas de prueba.)
- Asegurate de que el ambiente apunte a **homologación**, no a producción: en la configuración de AFIP, `IsProduction` tiene que estar en **false** (es el toggle que ya tenés). Con eso, el sistema pega contra las URLs de prueba de ARCA (`wswhomo`), no las reales.
- Necesitás tener cargado el **certificado de homologación** de AFIP y el punto de venta de prueba habilitado para factura electrónica (WSFEv1). Esto ya lo tenés porque venís facturando.

## Pasos

### 1. Preparar la base de prueba
- Aplicá la migración nueva en la base de prueba (agrega las columnas de multimoneda):
  - La migración se llama `Adr012_M1_AddMultiCurrencyInvoicing`. Se aplica con el proceso de deploy/migraciones que ya usás (`dotnet ... --migrate-only` o como lo tengas armado).
- Confirmá que `IsProduction = false` (ambiente de prueba).

### 2. Prender el interruptor de multimoneda (solo en la base de prueba)
```sql
UPDATE "OperationalFinanceSettings" SET "EnableMultiCurrencyInvoicing" = true;
```
- Reiniciá la API para que tome el cambio.

### 3. Crear el escenario y emitir
1. Entrá al sistema (apuntando a la base de prueba).
2. Creá (o usá) una reserva de prueba.
3. Andá a **crear factura**. Ahora debería aparecer el **selector de moneda**.
4. Elegí **Dólares (USD)**.
5. Cargá el **tipo de cambio**: usá el **dólar vendedor divisa del Banco Nación del día hábil anterior** (ese es el que pide ARCA — NO el dólar billete). Escribí en la **justificación** de dónde lo sacaste (ej: "BNA vendedor divisa 28/05, $1.234,56").
6. Emití la factura.

### 4. Verificar el resultado
- **Si ARCA devuelve CAE aprobado** → 🎉 la factura en dólares funciona. La factura queda con moneda `DOL`, su cotización, y `CanMisMonExt = N`.
- **Si ARCA la rechaza** → copiame el **mensaje de error exacto** que devuelve ARCA. Lo más probable, si rebota, es:
  - El **orden del campo `CanMisMonExt`** dentro del comprobante (lo pusimos después de la cotización y antes de la condición de IVA; si ARCA lo quiere en otro lado, lo movemos — es un ajuste de 1 línea).
  - Algún otro campo que ARCA pida y que confirmemos con el mensaje de error.
  - Que el tipo de cambio cargado no coincida con el oficial (más relevante si algún día usás `CanMisMonExt = S`).

### 5. Recién después: producción
- Cuando la factura en dólares vuelva **aprobada en homologación**, recién ahí se prende en producción (mismo `UPDATE` pero en la base real + `IsProduction = true`).
- **Antes de producción**, idealmente: que un contador te firme que el tipo de cambio que usás (vendedor divisa BNA día hábil anterior) es el correcto. (Está en las preguntas para el contador que te pasé.)

## Qué NO probar todavía (porque no está listo)
- **Notas de crédito / débito TOTALES en dólares**: hoy están bloqueadas a propósito (es la fase siguiente). Si anulás una factura en dólares, el sistema todavía te lo va a frenar — es esperado.
- **Tarifario en dólares**: la propagación de precios en dólares desde el tarifario es fase posterior.

## Resumen de lo que ya está hecho (código)
- Selector de moneda en crear factura (gateado por el interruptor).
- La factura toma la moneda + tipo de cambio + justificación y los congela.
- El sistema traduce "USD" al código de ARCA ("DOL") solo.
- Se manda el campo `CanMisMonExt` (en "N" = cobrás en pesos) para comprobantes en dólares.
- Todo con el interruptor **apagado** por defecto → producción hoy sigue facturando en pesos, igual que siempre.

## Si algo sale mal
Copiá y pegá el error de ARCA (o el log de la API) y lo vemos juntos. El ajuste más probable (orden del campo) es de minutos.
