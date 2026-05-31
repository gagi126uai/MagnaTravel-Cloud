# Rediseño de los estados de la Reserva: "Vendida" y "A liquidar" (2026-05-30)

> Para Gastón, explicado fácil. Esta sesión cambiamos **cómo etiquetamos en qué etapa está cada reserva**, para que se parezca a cómo trabaja de verdad una agencia. Todo quedó **detrás de una llave (flag) apagada**: mientras esté apagada, no cambia NADA de lo que ves hoy.

## El problema

Pensá la reserva como una **carpeta de un viaje** que va pasando por etapas. Hasta hoy las etapas eran:

**Presupuesto → Confirmada → En viaje → Finalizada**

El lío: la etapa **"Confirmada" hacía de tres cosas a la vez** (la confirmó el operador, le cobraste, le diste los vouchers) y no se veía en cuál de esas estaba realmente. Y faltaban dos momentos que cualquier agencia maneja:
1. **"Vendida"**: el cliente ya te compró, pero el operador todavía no te confirmó. Hoy eso estaba escondido.
2. **"A liquidar"**: el viaje terminó, pero te falta cerrar cuentas/comisiones con el operador.

## Qué hicimos

Cambiamos el ciclo para que tenga **6 etapas** (lo elegiste vos, mirando cómo lo hacen otros sistemas del mercado):

```
Presupuesto → Vendida → Confirmada → En viaje → A liquidar → Finalizada
```

- **Vendida** (NUEVO): el cliente compró, esperás que el operador confirme.
- **Confirmada**: ahora significa solo "el operador confirmó".
- **A liquidar** (NUEVO, **OPCIONAL**): un desvío a mano para las reservas que necesitás cerrar cuentas con el operador *después* del viaje. **No es obligatorio**: por default el viaje termina y la reserva va directo a Finalizada. Solo apartás a "A liquidar" la que vos quieras (porque con ese operador arreglás la plata después). El robot automático nunca mete a nadie ahí. (Ajuste 2026-05-31: el dueño aclaró que el momento de pagar al operador depende de cada uno, así que esta etapa quedó opcional, no fija.)

También movimos dos controles a donde tienen sentido:
- La **carga de pasajeros** ahora se pide al **Vender** (antes al Confirmar).
- El chequeo de **"servicios confirmados con el operador"** ahora es el paso **Vendida → Confirmada**.

## La llave de seguridad (lo más importante)

Todo esto está detrás de una **llave llamada `EnableSoldToSettleStates`, que viene APAGADA**. Con la llave apagada:
- La app se ve y funciona **exactamente igual que hoy** (Presupuesto → Confirmada → En viaje → Finalizada).
- Ninguna reserva entra a los estados nuevos.

Lo verificaron **dos revisores** (uno de backend, uno de riesgo/seguridad): con la llave apagada es "idéntico a hoy". Recién cuando prendas la llave aparece la cadena nueva.

## Cómo quedó (esta sesión)

- **Backend** (el motor): los 2 estados nuevos, las reglas de qué se puede pasar a qué, el robot que mueve las carpetas, y un **candado** en la base de datos para que solo acepte estados válidos.
- **Frontend** (las pantallas): los botones nuevos ("Vender", "Confirmar con operador", "Marcar a liquidar", "Finalizar"), las pestañas "Vendidas" y "A liquidar", y los carteles de colores de cada estado. Todo aparece solo con la llave prendida.
- **Un candado fiscal**: con la llave prendida, **no se puede facturar una reserva "Vendida"** (porque el operador todavía no confirmó — facturar antes sería un riesgo con AFIP).
- **Auditoría**: ahora queda registrado quién y cuándo movió una carpeta por la cadena nueva.

## Lo que FALTA antes de poder prender la llave (NO es ahora)

1. **Barrido de reportes (Fase D)**: decidir bien cómo cuentan las "Vendidas" y "A liquidar" en los números de ventas/deudas. Hoy entran solas; hay que dejarlo explícito y probado.
2. **Firma del contador**: ¿una "Vendida" (que el operador no confirmó) ya cuenta como venta? ¿una "A liquidar" sigue siendo plata por cobrar? Eso lo decide un contador, no nosotros.
3. **Correr los tests en el servidor (VPS)**: acá no hay base de datos para probar; los tests de verdad corren allá.
4. **Detalle de pantalla**: unificar la lectura de la llave para que no haya un parpadeo al abrir (menor).

## Para el deploy (cuando toque)

En el servidor: `git pull` + `bash scripts/ops/deploy.sh`. Esto aplica una **migración nueva** (`ReservaSoldToSettleStates`) que solo **agrega** (la columna de la llave + amplía el candado de estados de 7 a 9 valores). No borra nada. Se suma a las migraciones que ya estaban pendientes de aplicar.

> Recordá: aunque deployes, **la llave arranca apagada** → no cambia nada hasta que vos la prendas (y antes hay que cerrar los 4 puntos de arriba).
