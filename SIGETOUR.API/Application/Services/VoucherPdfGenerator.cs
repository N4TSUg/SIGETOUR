using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SIGETOUR.API.Core.Entities;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SIGETOUR.API.Application.Services
{
    public static class VoucherPdfGenerator
    {
        static VoucherPdfGenerator()
        {
            QuestPDF.Settings.License = LicenseType.Community;
            QuestPDF.Settings.ThrowOnMissingFontFamilies = false;
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
        }

        public static byte[] GenerateVoucherPdf(Booking booking, TourPackage? tour, string? receiptSeriesNumber = null)
        {
            var tourTitle = tour?.Title ?? "Tour Turístico Cajamarca";
            var tourDuration = tour?.Duration ?? "4 Horas (Medio Día)";

            var companionList = new List<string>();
            try
            {
                if (!string.IsNullOrWhiteSpace(booking.AdditionalPassengersJson) && booking.AdditionalPassengersJson != "[]")
                {
                    using var doc = JsonDocument.Parse(booking.AdditionalPassengersJson);
                    foreach (var elem in doc.RootElement.EnumerateArray())
                    {
                        if (elem.TryGetProperty("Name", out var nameProp))
                        {
                            var nameStr = nameProp.GetString();
                            if (!string.IsNullOrWhiteSpace(nameStr)) companionList.Add(nameStr);
                        }
                    }
                }
            }
            catch { }

            var pendingBalance = booking.TotalAmount - booking.PaidAmount;
            if (pendingBalance < 0) pendingBalance = 0;
            var isFullyPaid = booking.PaidAmount >= booking.TotalAmount && booking.TotalAmount > 0;

            var document = Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(25);
                    page.PageColor(Colors.White);
                    page.DefaultTextStyle(x => x.FontSize(10).FontColor("#0f3d3e"));

                    page.Header().Element(ComposeHeader);
                    page.Content().Element(ComposeContent);
                    page.Footer().Element(ComposeFooter);
                });
            });

            return document.GeneratePdf();

            void ComposeHeader(IContainer container)
            {
                container.BorderBottom(2).BorderColor("#008775").PaddingBottom(10).Row(row =>
                {
                    row.RelativeItem().Column(col =>
                    {
                        col.Item().Text("TURISMO CHILÓN").FontSize(20).Bold().FontColor("#008775");
                        col.Item().Text("Transporte & Turismo Cajamarca S.A.C. • RUC: 20601234567").FontSize(9).FontColor("#5e7774");
                        col.Item().Text("MINCETUR & DIRCETUR Cert. N° 4412 • Jr. 2 de Mayo 569, Cajamarca").FontSize(9).FontColor("#5e7774");
                    });

                    row.ConstantItem(180).Column(col =>
                    {
                        col.Item().AlignRight().Text("BOLETO OFICIAL DE VIAJE").FontSize(11).Bold().FontColor("#008775");
                        col.Item().AlignRight().Text($"LOCALIZADOR: {booking.ReferenceCode}").FontSize(14).Bold().FontColor("#0f3d3e");
                        col.Item().AlignRight().Text($"Emisión: {booking.BookingDate:dd/MM/yyyy HH:mm} hrs").FontSize(8).FontColor("#5e7774");
                    });
                });
            }

            void ComposeContent(IContainer container)
            {
                container.PaddingTop(15).Column(col =>
                {
                    // 1. Resumen de Salida
                    col.Item().Background("#f0fdf9").Border(1).BorderColor("#ccfbf1").Padding(12).Column(c =>
                    {
                        c.Item().Row(r =>
                        {
                            r.RelativeItem().Text(tourTitle).FontSize(15).Bold().FontColor("#008775");
                            r.ConstantItem(120).AlignRight().Text("SERVICIO CONFORT VIP").FontSize(9).Bold().FontColor("#008775");
                        });

                        c.Item().PaddingTop(8).Row(r =>
                        {
                            r.RelativeItem().Column(sub =>
                            {
                                sub.Item().Text("FECHA DEL VIAJE").FontSize(8).Bold().FontColor("#5e7774");
                                sub.Item().Text($"{booking.TravelDate:dd/MM/yyyy}").FontSize(11).Bold();
                            });
                            r.RelativeItem().Column(sub =>
                            {
                                sub.Item().Text("TURNO / HORARIO").FontSize(8).Bold().FontColor("#5e7774");
                                sub.Item().Text(string.IsNullOrWhiteSpace(booking.ShiftName) ? "Turno Mañana" : booking.ShiftName).FontSize(11).Bold();
                            });
                            r.RelativeItem().Column(sub =>
                            {
                                sub.Item().Text("DURACIÓN").FontSize(8).Bold().FontColor("#5e7774");
                                sub.Item().Text(tourDuration).FontSize(11);
                            });
                        });
                    });

                    col.Item().PaddingTop(12);

                    // 2. Unidad y Punto de Embarque
                    col.Item().Row(r =>
                    {
                        r.RelativeItem().Border(1).BorderColor("#e2e8f0").Padding(10).Column(sub =>
                        {
                            sub.Item().Text("UNIDAD & FLOTA ASIGNADA").FontSize(8).Bold().FontColor("#5e7774");
                            if (booking.AssignedVehicle != null)
                            {
                                sub.Item().Text(booking.AssignedVehicle.Model).Bold();
                                sub.Item().Text($"Placa: {booking.AssignedVehicle.LicensePlate} • Capacidad: {booking.AssignedVehicle.SeatCapacity} pax").FontSize(8).FontColor("#5e7774");
                            }
                            else
                            {
                                sub.Item().Text("Por asignar en Manifiesto de Salida").Italic().FontColor("#64748b");
                            }
                        });

                        r.ConstantItem(10);

                        r.RelativeItem().Border(1).BorderColor("#e2e8f0").Padding(10).Column(sub =>
                        {
                            sub.Item().Text("PUNTO DE EMBARQUE").FontSize(8).Bold().FontColor("#5e7774");
                            if (!string.IsNullOrWhiteSpace(booking.PickupLocation) && booking.PickupLocation != "agencia")
                            {
                                sub.Item().Text(booking.PickupLocation).Bold();
                                sub.Item().Text("Estar listo en recepción 15 min antes").FontSize(8).FontColor("#b45309");
                            }
                            else
                            {
                                sub.Item().Text("Agencia Central: Jr. 2 de Mayo 569").Bold();
                                sub.Item().Text("Presentarse 15 min antes con DNI físico").FontSize(8).FontColor("#047857");
                            }
                        });
                    });

                    col.Item().PaddingTop(12);

                    // 3. Pasajeros
                    col.Item().Border(1).BorderColor("#e2e8f0").Padding(10).Column(sub =>
                    {
                        sub.Item().Row(r =>
                        {
                            r.RelativeItem().Text($"PASAJEROS EN BOLETO ({booking.TotalPassengers} ASIENTO{(booking.TotalPassengers > 1 ? "S" : "")})").FontSize(9).Bold().FontColor("#008775");
                            r.ConstantItem(120).AlignRight().Text("CUPOS GARANTIZADOS").FontSize(8).FontColor("#047857").Bold();
                        });

                        sub.Item().PaddingTop(6).Row(r =>
                        {
                            r.RelativeItem().Text($"1. {booking.CustomerName} (Titular)").Bold();
                            r.ConstantItem(150).AlignRight().Text($"DNI: {(string.IsNullOrWhiteSpace(booking.CustomerDni) ? "Pendiente" : booking.CustomerDni)}").FontSize(9);
                        });

                        int pIdx = 2;
                        foreach (var comp in companionList)
                        {
                            sub.Item().PaddingTop(4).Row(r =>
                            {
                                r.RelativeItem().Text($"{pIdx}. {comp}");
                                r.ConstantItem(150).AlignRight().Text("DNI: Verificar al embarque").FontSize(9).FontColor("#5e7774");
                            });
                            pIdx++;
                        }
                    });

                    col.Item().PaddingTop(12);

                    // 4. Liquidación Financiera
                    col.Item().Border(1).BorderColor("#ccfbf1").Background("#f0fdf9").Padding(12).Column(sub =>
                    {
                        sub.Item().Row(r =>
                        {
                            r.RelativeItem().Column(left =>
                            {
                                left.Item().Text("ESTADO DE PAGO & LIQUIDACIÓN").FontSize(9).Bold().FontColor("#008775");
                                if (isFullyPaid)
                                {
                                    left.Item().Text("PAGADO AL 100% (CONFIRMADO)").FontSize(11).Bold().FontColor("#15803d");
                                }
                                else
                                {
                                    left.Item().Text($"SALDO PENDIENTE: S/ {pendingBalance:F2}").FontSize(11).Bold().FontColor("#b91c1c");
                                    left.Item().Text("Cancelar en counter antes de la salida").FontSize(8).FontColor("#b45309");
                                }
                                if (!string.IsNullOrWhiteSpace(receiptSeriesNumber))
                                {
                                    left.Item().PaddingTop(4).Text($"Comprobante Electrónico: {receiptSeriesNumber}").FontSize(8).Bold().FontColor("#0369a1");
                                }
                            });

                            r.ConstantItem(160).Column(right =>
                            {
                                right.Item().Row(subR =>
                                {
                                    subR.RelativeItem().Text("Tarifa Total:");
                                    subR.ConstantItem(60).AlignRight().Text($"S/ {booking.TotalAmount:F2}").Bold();
                                });
                                right.Item().Row(subR =>
                                {
                                    subR.RelativeItem().Text("Abonado:");
                                    subR.ConstantItem(60).AlignRight().Text($"S/ {booking.PaidAmount:F2}").FontColor("#15803d").Bold();
                                });
                                right.Item().Row(subR =>
                                {
                                    subR.RelativeItem().Text("Saldo:");
                                    subR.ConstantItem(60).AlignRight().Text($"S/ {pendingBalance:F2}").FontColor(pendingBalance > 0 ? "#b91c1c" : "#15803d").Bold();
                                });
                            });
                        });
                    });

                    col.Item().PaddingTop(12);

                    // 5. Recomendaciones Importantes
                    col.Item().Border(1).BorderColor("#fef3c7").Background("#fffbeb").Padding(10).Column(sub =>
                    {
                        sub.Item().Text("INFORMACIÓN IMPORTANTE PARA EL EMBARQUE:").FontSize(8).Bold().FontColor("#b45309");
                        sub.Item().Text("• Presentar este boleto digital en su teléfono móvil o impreso al momento de abordar junto con su DNI físico original.").FontSize(8).FontColor("#92400e");
                        sub.Item().Text("• La hora de presentación es con 15 minutos de anticipación estricta para el pase de lista y verificación de manifiesto MTC.").FontSize(8).FontColor("#92400e");
                        sub.Item().Text("• Toda la flota de Turismo Chilón cuenta con SOAT turístico interprovincial vigente y revisión técnica autorizada.").FontSize(8).FontColor("#92400e");
                    });
                });
            }

            void ComposeFooter(IContainer container)
            {
                container.BorderTop(1).BorderColor("#cbd5e1").PaddingTop(8).Row(row =>
                {
                    row.RelativeItem().Text("Turismo Chilón Cajamarca • Jr. 2 de Mayo 569 • Central: +51 928 876 259 / +51 944 746 364").FontSize(7).FontColor("#64748b");
                    row.ConstantItem(120).AlignRight().Text(text =>
                    {
                        text.Span("Página ");
                        text.CurrentPageNumber();
                        text.Span(" de ");
                        text.TotalPages();
                    });
                });
            }
        }
    }
}
