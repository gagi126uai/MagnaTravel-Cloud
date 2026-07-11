# 2026-07-10/11 — La obra completa del rediseño de multas (ADR-044), ejecutada de corrido

> Sesión maratónica bajo el objetivo de Gaston: "ejecutá el plan completo optimizando tokens,
> funcional y sin errores". Este documento resume QUÉ quedó en producción, CÓMO se trabajó y
> QUÉ falta, a nivel trainee.

## Qué quedó EN PRODUCCIÓN (10 subidas, todas con deploy verde)

1. **T0 — la plata no miente** (`581a1a5`): anular una factura sin cobrar ya no inventa
   deuda (la reversión se topea a lo realmente cobrado) + reparación del dato fantasma de
   prod con backup.
2. **T1 — la multa vive en cada operador** (`3528d89`): una anulación con hotel de un
   operador y aéreo de otro = dos multas independientes, cada una con su moneda y su ciclo
   (confirmar / cerrar sin multa / deshacer), visibles en la ficha una por una.
3. **T2 — los cargos en la cuenta del operador** (`fb28a3f`): cada deducción tipificada
   (administrativo / impuesto / retención fiscal / otro) y con su forma de cobro (retenida
   del reembolso o facturada aparte con documento). La retención fiscal jamás toca al
   cliente. Extracto del operador correcto.
4. **T3a — la nota de débito multi-operador** (`2d95341`): la ND al cliente se arma con un
   renglón por cargo de TODOS los operadores; confirmación escalonada sin plata perdida;
   alícuota parametrizada sin inventar valores (Responsable Inscripto bloqueado hasta la
   firma del contador).
5. **T3b — multimoneda y multi-factura** (`5c1d39a`): elegir a qué factura va cada cargo
   (dato del vendedor, jamás adivinado), conversión de moneda con TC auditable y banda de
   sanidad, y el registro de "ajuste por el dólar" visible en el extracto.
6. **T4a — las decisiones finales de Gaston** (`ce6fccd`): TC del día en que el operador
   cobró; el comprobante del pasajero SÍ nombra al mayorista; configuración de quién asume
   el ajuste (por agencia + excepción por operador).
7. **T4 — todas las pantallas** (`f1812e4`): agregar otro cargo desde la ficha, elegir
   factura, el monitor "Comprobantes por resolver" dentro de Facturación (fin de
   "Pendientes con AFIP" — desarmada del menú con redirects), extracto con chips, config
   del ajuste. Con las 10 respuestas de UX de Gaston convertidas en reglas escritas de su guía.
8. **Seguridad de Gaston** (`8556164`, trabajo de su otra sesión, subido a su pedido tras
   verificación): CSP estricta, paneles internos a localhost, renderizador seguro del
   historial, AutoMapper 15, y una compuerta NUEVA de CI que corre los tests de integración
   contra base real antes de cada deploy (primer run: verde).
   (+ los fixes de infraestructura del 2026-07-09/10: migrador que ya no falla en silencio,
   diagnóstico del VPS con logs de migrador/web.)

## Cómo se trabajó (el método que funcionó)

Cada tanda: **especificación fiscal firmada → diseño de arquitectura → desafío adversarial
(con rechazos reales) → construcción → 4 controles (backend, frontend, riesgo de plata,
exposición de internos) → corrección de bloqueantes → re-revisión → validación de
migraciones contra producción (solo lectura) → subida → deploy verificado**.

Números del circuito: los controles encontraron y corrigieron **más de 20 bloqueantes
reales** antes de que ninguno llegara a producción (snapshot fiscal pisado entre operadores,
plata trasladada perdida en silencio, doble liquidación, doble acreditación fiscal, textos
técnicos en pantalla, roles que perdían acceso, configuración pisada con null, TC sin banda
de sanidad, migración editada in-place que hubiera tumbado el deploy, etc.). Optimización de
tokens: lectores baratos para mapear, modelo medio para construir y revisar, el potente solo
para diseñar/juzgar/orquestar; especificaciones en archivos releídos por cada agente.

## Qué falta (retomo)

1. **T5 — anulación parcial (backend)**: el diseño quedó CERRADO y doblemente desafiado
   (Addendum T5 + Revisión 2 + C1/C2 en el ADR): compuerta de 3 salidas que reemplaza el
   bloqueo total actual, monto confirmado por servicio con tope acumulativo por remanente,
   cada anulación con su propia carpeta (arregla 2 bugs latentes verificados), y los fixes
   B1/B2 de los caminos legacy. La construcción quedó EN VUELO al cierre de la sesión —
   verificar `git status` al retomar: si el trabajo del agente quedó a medias, relanzar la
   construcción desde el Addendum T5 (la spec es autosuficiente). Después: 4 controles +
   gate fiscal → migración del índice validada contra prod → subida.
2. **T5 — pantalla** de anular un servicio (tanda de UI aparte, gate de diseño con Gaston).
3. **Gates humanos del contador de Gaston** (él los lleva): alícuota IVA para RI
   pass-through; contabilización formal del ajuste por el dólar; prorrateo del pago parcial
   al anular un servicio con la factura no totalmente cobrada.
4. **Verificación visual pendiente de Gaston**: refresco forzado en la web (Ctrl+Shift+R)
   por la CSP nueva; y el checklist viejo (F-2026-1044, paso de multa, BC 10 con USD 200).
5. Seguimientos menores anotados en el ADR (lock de reversión antes de más concurrencia,
   crédito fiscal por retenciones invisible en extracto, dedupe del polling con N paneles,
   cap M2 contra saldo neto).
