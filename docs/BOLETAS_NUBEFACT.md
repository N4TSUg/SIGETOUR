# Emisión de boletas electrónicas: siguiente módulo de SIGETOUR

El repositorio ya tiene `Booking.InvoiceType` e `InvoiceNumber`, pero **no son un historial de CPE**. Una reserva puede recibir varios abonos, anulaciones o notas de crédito.

## Modelo propuesto

`ElectronicReceipt`:
- `Id: Guid` / `BookingId: Guid` / `CulqiPaymentId: Guid?`
- `Type: Boleta|Factura|NotaCredito` / `Series` / `Number` / `ProviderId`
- `Status: Pending|Submitted|Accepted|Rejected|Cancelled`
- `PdfUrl` / `XmlUrl` / `CdrUrl` / `SunatStatus`
- `IssuedAtUtc` / `LastError`
- índice único `(Series, Number, Type)` y clave idempotente por hecho imponible.

## Flujo recomendado

1. Cobro confirmado por el backend / reconciliación oficial con Culqi.
2. Crear evento interno `PaymentConfirmed` en tabla **Outbox** junto con el pago (misma transacción EF).
3. Servicio en segundo plano lee Outbox y valida datos tributarios y la política de anticipos.
4. Construir JSON NubeFact según su manual oficial (boleta `tipo_de_comprobante=2`).
5. Invocar la **ruta asignada** por NubeFact con token privado (nunca frontend).
6. Guardar número, estados, PDF, XML y CDR; exponer descarga autenticada al comprador.
7. Manejar reintentos de forma idempotente y rechazos sin emitir dos comprobantes.

**Importante:** el tratamiento tributario de adelantos y saldos en turismo debe validarse con un contador (momento de emisión, IGV, anticipos y facturas vs boletas). No es correcto emitir automáticamente una única boleta por `Booking.TotalAmount` al recibir solamente el adelanto.

## API
- Manual y ejemplo C#: https://www.nubefact.com/integracion
- Los datos `RUC emisor`, `serie`, `ruta`, `token`, `tipo de operación`, `IGV`, `catálogo de servicio`, `tipo documento cliente` y el negocio tributario deben definirse antes de activar el emisor.

Este paquete **no emite CPE reales ni de prueba** porque falta la configuración del emisor y no hay flujo tributario definido para los anticipos.
