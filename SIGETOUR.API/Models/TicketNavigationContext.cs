using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace SIGETOUR.API.Models
{
    /// <summary>
    /// Contexto tipado de navegación para el ticket POS térmico.
    /// Garantiza:
    /// 1. Conservación estricta de la operación seleccionada (receiptId, cashTransactionId o paymentId) al cambiar el formato de ancho (80mm vs 58mm).
    /// 2. Preservación del token criptográfico de consulta únicamente para peticiones autorizadas de la reserva.
    /// 3. Prevención de fugas de tokens hacia destinos externos o no autorizados.
    /// </summary>
    public class TicketNavigationContext
    {
        public Guid RouteId { get; set; }
        public Guid BookingId { get; set; }
        public Guid? ReceiptId { get; set; }
        public Guid? CashTransactionId { get; set; }
        public Guid? PaymentId { get; set; }
        public string Size { get; set; } = "80";
        public string? Token { get; set; }
        public bool IsTokenAuthorized { get; set; }

        public string GetUrlForSize(IUrlHelper urlHelper, string targetSize)
        {
            var cleanSize = (targetSize == "58") ? "58" : "80";
            var routeValues = new RouteValueDictionary
            {
                ["size"] = cleanSize
            };

            // Si se accedió por un comprobante directo en la ruta:
            if (ReceiptId.HasValue && ReceiptId.Value != Guid.Empty && RouteId == ReceiptId.Value)
            {
                routeValues["id"] = ReceiptId.Value;
            }
            else
            {
                routeValues["id"] = BookingId != Guid.Empty ? BookingId : RouteId;

                // Conservar la operación seleccionada exactamente
                if (CashTransactionId.HasValue && CashTransactionId.Value != Guid.Empty)
                {
                    routeValues["cashTransactionId"] = CashTransactionId.Value;
                }
                else if (PaymentId.HasValue && PaymentId.Value != Guid.Empty)
                {
                    routeValues["paymentId"] = PaymentId.Value;
                }
                else if (ReceiptId.HasValue && ReceiptId.Value != Guid.Empty)
                {
                    routeValues["receiptId"] = ReceiptId.Value;
                }
            }

            // Propagar el token de acceso únicamente si fue validado positivamente para esta reserva
            if (IsTokenAuthorized && !string.IsNullOrWhiteSpace(Token))
            {
                routeValues["token"] = Token.Trim();
            }

            return urlHelper.Action("Ticket", "Booking", routeValues) ?? $"?size={cleanSize}";
        }

        public string GetUrlForOperation(IUrlHelper urlHelper, BookingOperationOptionViewModel op)
        {
            var routeValues = new RouteValueDictionary
            {
                ["size"] = (Size == "58") ? "58" : "80"
            };

            if (op.ReceiptId.HasValue && op.ReceiptId.Value != Guid.Empty)
            {
                routeValues["id"] = op.ReceiptId.Value;
            }
            else if (op.CashTransactionId.HasValue && op.CashTransactionId.Value != Guid.Empty)
            {
                routeValues["id"] = BookingId;
                routeValues["cashTransactionId"] = op.CashTransactionId.Value;
            }
            else if (op.CulqiPaymentId.HasValue && op.CulqiPaymentId.Value != Guid.Empty)
            {
                routeValues["id"] = BookingId;
                routeValues["paymentId"] = op.CulqiPaymentId.Value;
            }
            else
            {
                routeValues["id"] = BookingId;
            }

            // Propagar el token de acceso al destino interno de la misma reserva
            if (IsTokenAuthorized && !string.IsNullOrWhiteSpace(Token))
            {
                routeValues["token"] = Token.Trim();
            }

            return urlHelper.Action("Ticket", "Booking", routeValues) ?? "#";
        }

        public string GetUrlForVoucher(IUrlHelper urlHelper)
        {
            var routeValues = new RouteValueDictionary
            {
                ["id"] = BookingId != Guid.Empty ? BookingId : RouteId
            };

            if (IsTokenAuthorized && !string.IsNullOrWhiteSpace(Token))
            {
                routeValues["token"] = Token.Trim();
            }

            return urlHelper.Action("Voucher", "Booking", routeValues) ?? $"/Booking/Voucher/{BookingId}";
        }
    }
}
