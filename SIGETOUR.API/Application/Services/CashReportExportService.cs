using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;

namespace SIGETOUR.API.Application.Services
{
    public class CashReportExportService : ICashReportExportService
    {
        private readonly AppDbContext _context;

        // Paleta institucional Turismo Chilón
        private static readonly XLColor BrandDarkTeal = XLColor.FromHtml("#004D40");
        private static readonly XLColor BrandPrimary = XLColor.FromHtml("#00685A");
        private static readonly XLColor BrandPrimaryLight = XLColor.FromHtml("#008472");
        private static readonly XLColor BrandSurface = XLColor.FromHtml("#E4FFFB");
        private static readonly XLColor BrandSurfaceMint = XLColor.FromHtml("#C1F9F3");
        private static readonly XLColor BrandZebra = XLColor.FromHtml("#F4FFFA");
        private static readonly XLColor BorderGray = XLColor.FromHtml("#BDC9C5");
        private static readonly XLColor TextDark = XLColor.FromHtml("#00201E");
        private static readonly XLColor TextMuted = XLColor.FromHtml("#5E7774");
        private static readonly XLColor AlertRed = XLColor.FromHtml("#B0251B");
        private static readonly XLColor AlertGreen = XLColor.FromHtml("#1B5E20");
        private static readonly XLColor AlertAmber = XLColor.FromHtml("#E65100");

        public CashReportExportService(AppDbContext context)
        {
            _context = context;
        }

        #region 1. REPORTE DIARIO DE CAJA
        public async Task<byte[]> GenerateDailyCashReportExcelAsync(DateTime date)
        {
            var targetDate = date.Date;
            var startUtc = DateTime.SpecifyKind(targetDate, DateTimeKind.Utc);
            var endUtc = startUtc.AddDays(1);

            // Turnos del día
            var shifts = await _context.CashRegisterShifts
                .Include(s => s.Transactions)
                    .ThenInclude(t => t.Booking)
                .Where(s => s.OpenedAt >= startUtc && s.OpenedAt < endUtc)
                .OrderBy(s => s.OpenedAt)
                .ToListAsync();

            var shiftIds = shifts.Select(s => s.Id).ToList();

            // Transacciones de caja del día
            var transactions = await _context.CashTransactions
                .Include(t => t.Shift)
                .Include(t => t.Booking)
                .Where(t => (t.CreatedAt >= startUtc && t.CreatedAt < endUtc) || shiftIds.Contains(t.ShiftId))
                .OrderBy(t => t.CreatedAt)
                .ToListAsync();

            // Movimientos de Caja Chica del día
            var pettyMovements = await _context.PettyCashMovements
                .Include(m => m.Account)
                .Where(m => m.RegisteredAt >= startUtc && m.RegisteredAt < endUtc)
                .OrderBy(m => m.RegisteredAt)
                .ToListAsync();

            // Reservas del día
            var bookings = await _context.Bookings
                .Include(b => b.Items).ThenInclude(i => i.TourPackage)
                .Where(b => (b.BookingDate >= startUtc && b.BookingDate < endUtc) || (b.TravelDate >= startUtc && b.TravelDate < endUtc))
                .OrderBy(b => b.BookingDate)
                .ToListAsync();

            using var wb = new XLWorkbook();

            // HOJA 1: RESUMEN Y ARQUEO DIARIO
            BuildDailySummarySheet(wb, targetDate, shifts, transactions, pettyMovements, bookings);

            // HOJA 2: MOVIMIENTOS CAJA GENERAL
            BuildDailyTransactionsSheet(wb, targetDate, transactions);

            // HOJA 3: GASTOS CAJA CHICA
            BuildDailyPettyCashSheet(wb, targetDate, pettyMovements);

            // HOJA 4: RESERVAS Y TICKETS COBRADOS
            BuildDailyBookingsSheet(wb, targetDate, bookings);

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private void BuildDailySummarySheet(
            XLWorkbook wb,
            DateTime date,
            List<CashRegisterShift> shifts,
            List<CashTransaction> transactions,
            List<PettyCashMovement> pettyMovements,
            List<Booking> bookings)
        {
            var ws = wb.Worksheets.Add("Resumen y Arqueo Diario");
            ws.ShowGridLines = true;

            // Encabezado Corporativo
            BuildHeaderBanner(ws, "REPORTE DIARIO CONSOLIDADO DE CAJA Y ARQUEO GENERAL", $"Fecha de Operación: {date:dd/MM/yyyy (dddd)}", 8);

            int r = 6;

            // MÉTRICAS CLAVE (KPIs FINANCIEROS)
            decimal totalInitial = shifts.Sum(s => s.InitialAmount);
            decimal totalCashIncome = transactions.Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod == "Efectivo").Sum(t => t.Amount);
            decimal totalCashExpense = transactions.Where(t => t.TransactionType == "Egreso" && t.PaymentMethod == "Efectivo").Sum(t => t.Amount);
            decimal expectedCash = totalInitial + totalCashIncome - totalCashExpense;
            decimal actualCashCounted = shifts.Where(s => s.ActualCash.HasValue).Sum(s => s.ActualCash!.Value);
            decimal totalCashDiff = shifts.Sum(s => s.CashDifference);

            decimal totalDigitalIncome = transactions.Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod != "Efectivo").Sum(t => t.Amount);
            decimal totalIncomeAll = totalCashIncome + totalDigitalIncome;
            decimal totalExpenseAll = totalCashExpense + transactions.Where(t => t.TransactionType == "Egreso" && t.PaymentMethod != "Efectivo").Sum(t => t.Amount);
            decimal totalPettySpent = pettyMovements.Where(m => m.MovementType == "Egreso").Sum(m => m.Amount);
            decimal netOperatingCashFlow = totalIncomeAll - totalExpenseAll - totalPettySpent;

            // TABLA: RESUMEN FINANCIERO CONSOLIDADO DEL DÍA
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "1. BALANCE Y MOVIMIENTO FINANCIERO DEL DÍA";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            var summaryRows = new List<(string Concepto, string Detalle, decimal Monto, string Tipo)>
            {
                ("Saldo Inicial en Gaveta (Apertura)", "Fondo base disponible al inicio de turnos", totalInitial, "BASE"),
                ("(+) Ingresos en Efectivo", "Cobros de mostrador, boletos y ventas en efectivo", totalCashIncome, "IN"),
                ("(-) Egresos / Retiros de Efectivo", "Pagos menores o retiros de gaveta en turnos", totalCashExpense, "OUT"),
                ("(=) Saldo Teórico en Efectivo", "Efectivo que debe existir físicamente en gaveta", expectedCash, "TOTAL"),
                ("(~ ) Arqueo Físico Real Contado", "Efectivo verificado en cierres de turno de caja", actualCashCounted, "ARQUEO"),
                ("(Δ) Diferencia de Arqueo (Caja)", totalCashDiff == 0 ? "Caja Cuadrada exacta (Sin faltante ni sobrante)" : (totalCashDiff > 0 ? "Sobrante de caja registrado" : "Faltante de caja registrado"), totalCashDiff, totalCashDiff >= 0 ? "DIFF_OK" : "DIFF_BAD"),
                ("(+) Ingresos Digitales y Bancarios", "Recaudación vía Yape, Plin, Transferencias BCP y Tarjetas", totalDigitalIncome, "IN_DIG"),
                ("(=) RECAUDACIÓN TOTAL DEL DÍA", "Suma total de cobros de tours y mostrador (Efectivo + Digital)", totalIncomeAll, "GRAND_TOTAL"),
                ("(-) Gastos Rendidos en Caja Chica", "Combustible, peajes, refrigerios y útiles de operación", totalPettySpent, "OUT_PETTY"),
                ("(=) FLUJO OPERATIVO NETO", "Ingresos Totales menos Egresos de Caja y Gastos de Caja Chica", netOperatingCashFlow, "NET_FLOW")
            };

            ws.Cell(r, 1).Value = "Concepto Contable";
            ws.Cell(r, 4).Value = "Descripción / Detalle";
            ws.Range(r, 1, r, 3).Merge();
            ws.Range(r, 4, r, 6).Merge();
            ws.Range(r, 7, r, 8).Merge();
            ws.Cell(r, 7).Value = "Monto (S/)";
            StyleTableHeader(ws.Range(r, 1, r, 8));
            r++;

            foreach (var item in summaryRows)
            {
                ws.Range(r, 1, r, 3).Merge();
                ws.Range(r, 4, r, 6).Merge();
                ws.Range(r, 7, r, 8).Merge();

                ws.Cell(r, 1).Value = item.Concepto;
                ws.Cell(r, 4).Value = item.Detalle;
                ws.Cell(r, 7).Value = item.Monto;
                ws.Cell(r, 7).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var rowRange = ws.Range(r, 1, r, 8);
                rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.OutsideBorderColor = BorderGray;
                rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.InsideBorderColor = BorderGray;

                if (item.Tipo == "GRAND_TOTAL" || item.Tipo == "NET_FLOW")
                {
                    rowRange.Style.Font.Bold = true;
                    rowRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                    ws.Cell(r, 7).Style.Font.FontSize = 11;
                    ws.Cell(r, 7).Style.Font.FontColor = BrandDarkTeal;
                }
                else if (item.Tipo == "DIFF_BAD")
                {
                    rowRange.Style.Font.Bold = true;
                    ws.Cell(r, 7).Style.Font.FontColor = AlertRed;
                    rowRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFEBEE");
                }
                else if (item.Tipo == "DIFF_OK" && totalCashDiff > 0)
                {
                    rowRange.Style.Font.Bold = true;
                    ws.Cell(r, 7).Style.Font.FontColor = AlertGreen;
                    rowRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E8F5E9");
                }
                else
                {
                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                }
                r++;
            }

            r += 2;

            // TABLA 2: RESUMEN DE ARQUEO DE TURNOS DE CAJA
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "2. TURNOS Y ARQUEOS DE CAJA REGISTRADOS EN LA FECHA";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            string[] shiftHeaders = { "Turno", "Cajero / Responsable", "Apertura", "Cierre", "Fondo Base (S/)", "Recaudado (S/)", "Arqueo Real (S/)", "Diferencia (S/)" };
            for (int i = 0; i < shiftHeaders.Length; i++)
            {
                ws.Cell(r, i + 1).Value = shiftHeaders[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 8));
            r++;

            if (shifts.Any())
            {
                foreach (var s in shifts)
                {
                    ws.Cell(r, 1).Value = s.ShiftNumber;
                    ws.Cell(r, 2).Value = s.OpenedByUserName;
                    ws.Cell(r, 3).Value = s.OpenedAt.ToLocalTime().ToString("HH:mm");
                    ws.Cell(r, 4).Value = s.ClosedAt.HasValue ? s.ClosedAt.Value.ToLocalTime().ToString("HH:mm") : "EN CURSO";
                    ws.Cell(r, 5).Value = s.InitialAmount;
                    ws.Cell(r, 6).Value = s.TotalIncome;
                    ws.Cell(r, 7).Value = s.ActualCash.HasValue ? s.ActualCash.Value : 0m;
                    ws.Cell(r, 8).Value = s.CashDifference;

                    ws.Cell(r, 5).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    ws.Cell(r, 7).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    ws.Cell(r, 8).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                    var rowRange = ws.Range(r, 1, r, 8);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (s.CashDifference < 0)
                    {
                        ws.Cell(r, 8).Style.Font.FontColor = AlertRed;
                        ws.Cell(r, 8).Style.Font.Bold = true;
                    }
                    else if (s.CashDifference > 0)
                    {
                        ws.Cell(r, 8).Style.Font.FontColor = AlertGreen;
                        ws.Cell(r, 8).Style.Font.Bold = true;
                    }

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }
            }
            else
            {
                ws.Range(r, 1, r, 8).Merge();
                ws.Cell(r, 1).Value = "No se abrieron turnos de caja en esta fecha.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
                r++;
            }

            r += 2;

            // TABLA 3: DESGLOSE POR MÉTODO DE PAGO
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "3. DISTRIBUCIÓN DE INGRESOS POR FORMA DE PAGO";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            ws.Range(r, 1, r, 3).Merge();
            ws.Range(r, 4, r, 5).Merge();
            ws.Range(r, 6, r, 7).Merge();
            ws.Cell(r, 1).Value = "Método de Pago";
            ws.Cell(r, 4).Value = "N° Operaciones";
            ws.Cell(r, 6).Value = "Monto Recaudado (S/)";
            ws.Cell(r, 8).Value = "% Participación";
            StyleTableHeader(ws.Range(r, 1, r, 8));
            r++;

            var incomeByMethod = transactions
                .Where(t => t.TransactionType == "Ingreso")
                .GroupBy(t => string.IsNullOrWhiteSpace(t.PaymentMethod) ? "Efectivo" : t.PaymentMethod)
                .Select(g => new { Method = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Total)
                .ToList();

            if (incomeByMethod.Any() && totalIncomeAll > 0)
            {
                foreach (var m in incomeByMethod)
                {
                    decimal pct = (m.Total / totalIncomeAll);

                    ws.Range(r, 1, r, 3).Merge();
                    ws.Range(r, 4, r, 5).Merge();
                    ws.Range(r, 6, r, 7).Merge();

                    ws.Cell(r, 1).Value = m.Method;
                    ws.Cell(r, 4).Value = m.Count;
                    ws.Cell(r, 6).Value = m.Total;
                    ws.Cell(r, 8).Value = pct;

                    ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    ws.Cell(r, 8).Style.NumberFormat.Format = "0.0%";

                    var rowRange = ws.Range(r, 1, r, 8);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                // TOTAL
                ws.Range(r, 1, r, 3).Merge();
                ws.Range(r, 4, r, 5).Merge();
                ws.Range(r, 6, r, 7).Merge();

                ws.Cell(r, 1).Value = "TOTAL CONSOLIDADO";
                ws.Cell(r, 4).Value = incomeByMethod.Sum(x => x.Count);
                ws.Cell(r, 6).Value = totalIncomeAll;
                ws.Cell(r, 8).Value = 1.0m;

                ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 8).Style.NumberFormat.Format = "0.0%";

                var totalRange = ws.Range(r, 1, r, 8);
                totalRange.Style.Font.Bold = true;
                totalRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                r++;
            }
            else
            {
                ws.Range(r, 1, r, 8).Merge();
                ws.Cell(r, 1).Value = "No se registraron ingresos en esta fecha.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
                r++;
            }

            r += 3;

            // BLOQUE DE FIRMAS DE CONTROL
            BuildSignatureBlock(ws, r, "Cajero / Responsable de Turno", "Administración / Auditoría Interna");

            ws.Columns().AdjustToContents(14, 45);
        }

        private void BuildDailyTransactionsSheet(XLWorkbook wb, DateTime date, List<CashTransaction> transactions)
        {
            var ws = wb.Worksheets.Add("Movimientos Caja General");
            ws.ShowGridLines = true;

            BuildHeaderBanner(ws, "DETALLE DE MOVIMIENTOS Y TRANSACCIONES DE CAJA GENERAL", $"Fecha: {date:dd/MM/yyyy} • Total Transacciones: {transactions.Count}", 10);

            int r = 6;
            string[] headers = { "N°", "Turno", "Hora", "Tipo", "Categoría", "Concepto / Descripción", "Referencia / Reserva", "Método Pago", "Registrado Por", "Monto (S/)" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(r, i + 1).Value = headers[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 10));
            r++;

            int startRow = r;
            if (transactions.Any())
            {
                int index = 1;
                foreach (var t in transactions)
                {
                    ws.Cell(r, 1).Value = index++;
                    ws.Cell(r, 2).Value = t.Shift?.ShiftNumber ?? "--";
                    ws.Cell(r, 3).Value = t.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
                    ws.Cell(r, 4).Value = t.TransactionType;
                    ws.Cell(r, 5).Value = t.Category;
                    ws.Cell(r, 6).Value = t.Description;
                    ws.Cell(r, 7).Value = !string.IsNullOrWhiteSpace(t.ReferenceCode) ? t.ReferenceCode : (t.Booking?.ReferenceCode ?? "--");
                    ws.Cell(r, 8).Value = t.PaymentMethod;
                    ws.Cell(r, 9).Value = t.RegisteredBy;
                    ws.Cell(r, 10).Value = t.Amount;
                    ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                    var rowRange = ws.Range(r, 1, r, 10);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (t.TransactionType == "Egreso")
                    {
                        ws.Cell(r, 4).Style.Font.FontColor = AlertRed;
                        ws.Cell(r, 4).Style.Font.Bold = true;
                        ws.Cell(r, 10).Style.Font.FontColor = AlertRed;
                    }
                    else
                    {
                        ws.Cell(r, 4).Style.Font.FontColor = AlertGreen;
                        ws.Cell(r, 4).Style.Font.Bold = true;
                    }

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                // Fila de Total
                ws.Range(r, 1, r, 9).Merge();
                ws.Cell(r, 1).Value = "TOTAL RECAUDADO (SUMA DE MOVIMIENTOS):";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(r, 10).FormulaA1 = $"=SUM(J{startRow}:J{r - 1})";
                ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var totRange = ws.Range(r, 1, r, 10);
                totRange.Style.Font.Bold = true;
                totRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                totRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                totRange.Style.Border.OutsideBorderColor = BrandDarkTeal;
            }
            else
            {
                ws.Range(r, 1, r, 10).Merge();
                ws.Cell(r, 1).Value = "No hay movimientos registrados en la fecha seleccionada.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
            }

            ws.Columns().AdjustToContents(10, 45);
        }

        private void BuildDailyPettyCashSheet(XLWorkbook wb, DateTime date, List<PettyCashMovement> pettyMovements)
        {
            var ws = wb.Worksheets.Add("Gastos Caja Chica");
            ws.ShowGridLines = true;

            BuildHeaderBanner(ws, "FONDO FIJO DE CAJA CHICA — GASTOS MENORES DEL DÍA", $"Fecha: {date:dd/MM/yyyy} • Total Rendiciones: {pettyMovements.Count}", 10);

            int r = 6;
            string[] headers = { "N°", "Hora", "Tipo", "Categoría de Gasto", "Concepto / Descripción", "Proveedor / Establecimiento", "Comprobante", "N° Doc.", "Responsable", "Importe (S/)" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(r, i + 1).Value = headers[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 10));
            r++;

            int startRow = r;
            if (pettyMovements.Any())
            {
                int index = 1;
                foreach (var m in pettyMovements)
                {
                    ws.Cell(r, 1).Value = index++;
                    ws.Cell(r, 2).Value = m.RegisteredAt.ToLocalTime().ToString("HH:mm:ss");
                    ws.Cell(r, 3).Value = m.MovementType;
                    ws.Cell(r, 4).Value = m.ExpenseCategory;
                    ws.Cell(r, 5).Value = m.Concept;
                    ws.Cell(r, 6).Value = !string.IsNullOrWhiteSpace(m.Supplier) ? m.Supplier : "Sin proveedor";
                    ws.Cell(r, 7).Value = m.VoucherType;
                    ws.Cell(r, 8).Value = !string.IsNullOrWhiteSpace(m.VoucherNumber) ? m.VoucherNumber : "--";
                    ws.Cell(r, 9).Value = m.ResponsiblePerson;
                    ws.Cell(r, 10).Value = m.Amount;
                    ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                    var rowRange = ws.Range(r, 1, r, 10);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (m.MovementType == "Reposicion")
                    {
                        ws.Cell(r, 3).Style.Font.FontColor = AlertGreen;
                        ws.Cell(r, 3).Style.Font.Bold = true;
                    }
                    else
                    {
                        ws.Cell(r, 3).Style.Font.FontColor = AlertRed;
                    }

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                // Fila de Total
                ws.Range(r, 1, r, 9).Merge();
                ws.Cell(r, 1).Value = "TOTAL GASTOS RENDIDOS EN CAJA CHICA:";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(r, 10).FormulaA1 = $"=SUM(J{startRow}:J{r - 1})";
                ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var totRange = ws.Range(r, 1, r, 10);
                totRange.Style.Font.Bold = true;
                totRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                totRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                totRange.Style.Border.OutsideBorderColor = BrandDarkTeal;
            }
            else
            {
                ws.Range(r, 1, r, 10).Merge();
                ws.Cell(r, 1).Value = "No se registraron gastos de caja chica en la fecha seleccionada.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
            }

            ws.Columns().AdjustToContents(10, 45);
        }

        private void BuildDailyBookingsSheet(XLWorkbook wb, DateTime date, List<Booking> bookings)
        {
            var ws = wb.Worksheets.Add("Reservas y Tickets Cobrados");
            ws.ShowGridLines = true;

            BuildHeaderBanner(ws, "RESERVAS DE TOURS Y PASAJEROS COBRADOS EN LA FECHA", $"Fecha: {date:dd/MM/yyyy} • Total Reservas: {bookings.Count}", 10);

            int r = 6;
            string[] headers = { "N°", "Código Boleto", "Fecha Salida", "Tour / Circuito", "Titular / Contacto", "DNI / Doc.", "Celular", "Pax", "Método Pago", "Total (S/)" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(r, i + 1).Value = headers[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 10));
            r++;

            int startRow = r;
            if (bookings.Any())
            {
                int index = 1;
                foreach (var b in bookings)
                {
                    var tour = b.Items.FirstOrDefault()?.TourPackage;
                    ws.Cell(r, 1).Value = index++;
                    ws.Cell(r, 2).Value = b.ReferenceCode;
                    ws.Cell(r, 3).Value = b.TravelDate.ToString("dd/MM/yyyy");
                    ws.Cell(r, 4).Value = tour?.Title ?? "Tour asignado";
                    ws.Cell(r, 5).Value = b.CustomerName;
                    ws.Cell(r, 6).Value = b.CustomerDni;
                    ws.Cell(r, 7).Value = b.CustomerPhone;
                    ws.Cell(r, 8).Value = b.TotalPassengers;
                    ws.Cell(r, 9).Value = !string.IsNullOrWhiteSpace(b.PaymentMethod) ? b.PaymentMethod : "Efectivo";
                    ws.Cell(r, 10).Value = b.TotalAmount;
                    ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                    var rowRange = ws.Range(r, 1, r, 10);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                // Fila de Total
                ws.Range(r, 1, r, 9).Merge();
                ws.Cell(r, 1).Value = "TOTAL RECAUDADO POR RESERVAS:";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(r, 10).FormulaA1 = $"=SUM(J{startRow}:J{r - 1})";
                ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var totRange = ws.Range(r, 1, r, 10);
                totRange.Style.Font.Bold = true;
                totRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                totRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                totRange.Style.Border.OutsideBorderColor = BrandDarkTeal;
            }
            else
            {
                ws.Range(r, 1, r, 10).Merge();
                ws.Cell(r, 1).Value = "No se registraron reservas emitidas o cobradas para esta fecha.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
            }

            ws.Columns().AdjustToContents(10, 45);
        }
        #endregion

        #region 2. REPORTE MENSUAL CONSOLIDADO DE CAJA
        public async Task<byte[]> GenerateMonthlyCashReportExcelAsync(int year, int month)
        {
            if (year < 2020 || year > 2035) year = DateTime.Today.Year;
            if (month < 1 || month > 12) month = DateTime.Today.Month;

            var startOfMonthUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endOfMonthUtc = startOfMonthUtc.AddMonths(1);
            int daysInMonth = DateTime.DaysInMonth(year, month);
            string monthName = new DateTime(year, month, 1).ToString("MMMM", new CultureInfo("es-ES"));

            // Turnos del mes
            var shifts = await _context.CashRegisterShifts
                .Include(s => s.Transactions)
                .Where(s => s.OpenedAt >= startOfMonthUtc && s.OpenedAt < endOfMonthUtc)
                .OrderBy(s => s.OpenedAt)
                .ToListAsync();

            // Transacciones de caja del mes
            var transactions = await _context.CashTransactions
                .Include(t => t.Shift)
                .Include(t => t.Booking)
                .Where(t => t.CreatedAt >= startOfMonthUtc && t.CreatedAt < endOfMonthUtc)
                .OrderBy(t => t.CreatedAt)
                .ToListAsync();

            // Movimientos de Caja Chica del mes
            var pettyMovements = await _context.PettyCashMovements
                .Include(m => m.Account)
                .Where(m => m.RegisteredAt >= startOfMonthUtc && m.RegisteredAt < endOfMonthUtc)
                .OrderBy(m => m.RegisteredAt)
                .ToListAsync();

            // Reservas del mes
            var bookings = await _context.Bookings
                .Include(b => b.Items).ThenInclude(i => i.TourPackage)
                .Where(b => b.BookingDate >= startOfMonthUtc && b.BookingDate < endOfMonthUtc)
                .OrderBy(b => b.BookingDate)
                .ToListAsync();

            using var wb = new XLWorkbook();

            // HOJA 1: CONSOLIDADO MENSUAL EJECUTIVO
            BuildMonthlySummarySheet(wb, year, month, monthName, shifts, transactions, pettyMovements, bookings);

            // HOJA 2: EVOLUCIÓN DÍA A DÍA
            BuildMonthlyDailyEvolutionSheet(wb, year, month, daysInMonth, shifts, transactions, pettyMovements);

            // HOJA 3: HISTORIAL DE TURNOS Y ARQUEOS DEL MES
            BuildMonthlyShiftsSheet(wb, monthName, year, shifts);

            // HOJA 4: DETALLE DE CAJA CHICA DEL MES
            BuildMonthlyPettyCashSheet(wb, monthName, year, pettyMovements);

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }

        private void BuildMonthlySummarySheet(
            XLWorkbook wb,
            int year,
            int month,
            string monthName,
            List<CashRegisterShift> shifts,
            List<CashTransaction> transactions,
            List<PettyCashMovement> pettyMovements,
            List<Booking> bookings)
        {
            var ws = wb.Worksheets.Add("Consolidado Ejecutivo Mensual");
            ws.ShowGridLines = true;

            BuildHeaderBanner(ws, "REPORTE MENSUAL CONSOLIDADO DE CAJA, FLUJO Y TESORERÍA", $"Período: {monthName.ToUpper()} {year} | Agencia Central Cajamarca", 8);

            int r = 6;

            decimal totalCashIncome = transactions.Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod == "Efectivo").Sum(t => t.Amount);
            decimal totalDigitalIncome = transactions.Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod != "Efectivo").Sum(t => t.Amount);
            decimal totalIncomeAll = totalCashIncome + totalDigitalIncome;

            decimal totalCashExpense = transactions.Where(t => t.TransactionType == "Egreso" && t.PaymentMethod == "Efectivo").Sum(t => t.Amount);
            decimal totalDigitalExpense = transactions.Where(t => t.TransactionType == "Egreso" && t.PaymentMethod != "Efectivo").Sum(t => t.Amount);
            decimal totalGeneralExpense = totalCashExpense + totalDigitalExpense;

            decimal totalPettySpent = pettyMovements.Where(m => m.MovementType == "Egreso").Sum(m => m.Amount);
            decimal totalPettyReplenished = pettyMovements.Where(m => m.MovementType == "Reposicion").Sum(m => m.Amount);

            decimal netFlow = totalIncomeAll - totalGeneralExpense - totalPettySpent;
            decimal totalCashDiff = shifts.Sum(s => s.CashDifference);
            int totalPassengers = bookings.Sum(b => b.TotalPassengers);

            // TABLA 1: GRANDES CIFRAS MENSUALES
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "1. RESUMEN FINANCIERO Y OPERATIVO DEL MES";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            var summaryRows = new List<(string C1, decimal V1, string C2, decimal V2, bool IsCurr)>
            {
                ("Total Ingresos Efectivo", totalCashIncome, "Total Egresos Caja General", totalGeneralExpense, true),
                ("Total Ingresos Digitales", totalDigitalIncome, "Total Gastos Menores Caja Chica", totalPettySpent, true),
                ("TOTAL RECAUDACIÓN MENSUAL", totalIncomeAll, "FLUJO FINANCIERO NETO", netFlow, true),
                ("Diferencia Neta de Arqueos", totalCashDiff, "Reposiciones a Caja Chica", totalPettyReplenished, true),
                ("Total Turnos Operados", shifts.Count, "Total Reservas Emitidas", bookings.Count, false),
                ("Total Transacciones Registradas", transactions.Count, "Total Pasajeros Atendidos (Pax)", totalPassengers, false)
            };

            foreach (var item in summaryRows)
            {
                ws.Range(r, 1, r, 2).Merge();
                ws.Range(r, 3, r, 4).Merge();
                ws.Range(r, 5, r, 6).Merge();
                ws.Range(r, 7, r, 8).Merge();

                ws.Cell(r, 1).Value = item.C1;
                ws.Cell(r, 3).Value = item.V1;
                ws.Cell(r, 5).Value = item.C2;
                ws.Cell(r, 7).Value = item.V2;

                if (item.IsCurr)
                {
                    ws.Cell(r, 3).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    ws.Cell(r, 7).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                }
                else
                {
                    ws.Cell(r, 3).Style.NumberFormat.Format = "#,##0";
                    ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0";
                }

                var rowRange = ws.Range(r, 1, r, 8);
                rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.OutsideBorderColor = BorderGray;
                rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.InsideBorderColor = BorderGray;

                if (item.C1.Contains("TOTAL") || item.C2.Contains("FLUJO"))
                {
                    rowRange.Style.Font.Bold = true;
                    rowRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                }
                else if (r % 2 == 0)
                {
                    rowRange.Style.Fill.BackgroundColor = BrandZebra;
                }
                r++;
            }

            r += 2;

            // TABLA 2: DESGLOSE MENSUAL POR FORMA DE PAGO
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "2. DISTRIBUCIÓN DE RECAUDACIÓN POR MÉTODO DE PAGO";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            ws.Range(r, 1, r, 3).Merge();
            ws.Range(r, 4, r, 5).Merge();
            ws.Range(r, 6, r, 7).Merge();
            ws.Cell(r, 1).Value = "Forma de Pago";
            ws.Cell(r, 4).Value = "N° Operaciones";
            ws.Cell(r, 6).Value = "Total Cobrado (S/)";
            ws.Cell(r, 8).Value = "% Cuota";
            StyleTableHeader(ws.Range(r, 1, r, 8));
            r++;

            var methodBreakdown = transactions
                .Where(t => t.TransactionType == "Ingreso")
                .GroupBy(t => string.IsNullOrWhiteSpace(t.PaymentMethod) ? "Efectivo" : t.PaymentMethod)
                .Select(g => new { Method = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Total)
                .ToList();

            if (methodBreakdown.Any() && totalIncomeAll > 0)
            {
                foreach (var m in methodBreakdown)
                {
                    ws.Range(r, 1, r, 3).Merge();
                    ws.Range(r, 4, r, 5).Merge();
                    ws.Range(r, 6, r, 7).Merge();

                    ws.Cell(r, 1).Value = m.Method;
                    ws.Cell(r, 4).Value = m.Count;
                    ws.Cell(r, 6).Value = m.Total;
                    ws.Cell(r, 8).Value = m.Total / totalIncomeAll;

                    ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    ws.Cell(r, 8).Style.NumberFormat.Format = "0.0%";

                    var rowRange = ws.Range(r, 1, r, 8);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                ws.Range(r, 1, r, 3).Merge();
                ws.Range(r, 4, r, 5).Merge();
                ws.Range(r, 6, r, 7).Merge();
                ws.Cell(r, 1).Value = "TOTAL RECAUDACIÓN MENSUAL";
                ws.Cell(r, 4).Value = methodBreakdown.Sum(x => x.Count);
                ws.Cell(r, 6).Value = totalIncomeAll;
                ws.Cell(r, 8).Value = 1.0m;

                ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 8).Style.NumberFormat.Format = "0.0%";

                var totRange = ws.Range(r, 1, r, 8);
                totRange.Style.Font.Bold = true;
                totRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                r++;
            }

            r += 2;

            // TABLA 3: GASTOS DE CAJA CHICA POR CATEGORÍA
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "3. GASTOS DE CAJA CHICA POR CATEGORÍA EN EL MES";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            ws.Range(r, 1, r, 3).Merge();
            ws.Range(r, 4, r, 5).Merge();
            ws.Range(r, 6, r, 7).Merge();
            ws.Cell(r, 1).Value = "Categoría de Gasto";
            ws.Cell(r, 4).Value = "N° Comprobantes";
            ws.Cell(r, 6).Value = "Total Gastado (S/)";
            ws.Cell(r, 8).Value = "% del Gasto";
            StyleTableHeader(ws.Range(r, 1, r, 8));
            r++;

            var pettyCategoryBreakdown = pettyMovements
                .Where(m => m.MovementType == "Egreso")
                .GroupBy(m => string.IsNullOrWhiteSpace(m.ExpenseCategory) ? "Otros Gastos Menores" : m.ExpenseCategory)
                .Select(g => new { Category = g.Key, Count = g.Count(), Total = g.Sum(x => x.Amount) })
                .OrderByDescending(x => x.Total)
                .ToList();

            if (pettyCategoryBreakdown.Any() && totalPettySpent > 0)
            {
                foreach (var cat in pettyCategoryBreakdown)
                {
                    ws.Range(r, 1, r, 3).Merge();
                    ws.Range(r, 4, r, 5).Merge();
                    ws.Range(r, 6, r, 7).Merge();

                    ws.Cell(r, 1).Value = cat.Category;
                    ws.Cell(r, 4).Value = cat.Count;
                    ws.Cell(r, 6).Value = cat.Total;
                    ws.Cell(r, 8).Value = cat.Total / totalPettySpent;

                    ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    ws.Cell(r, 8).Style.NumberFormat.Format = "0.0%";

                    var rowRange = ws.Range(r, 1, r, 8);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                ws.Range(r, 1, r, 3).Merge();
                ws.Range(r, 4, r, 5).Merge();
                ws.Range(r, 6, r, 7).Merge();
                ws.Cell(r, 1).Value = "TOTAL GASTOS CAJA CHICA";
                ws.Cell(r, 4).Value = pettyCategoryBreakdown.Sum(x => x.Count);
                ws.Cell(r, 6).Value = totalPettySpent;
                ws.Cell(r, 8).Value = 1.0m;

                ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 8).Style.NumberFormat.Format = "0.0%";

                var totRange = ws.Range(r, 1, r, 8);
                totRange.Style.Font.Bold = true;
                totRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                r++;
            }

            r += 3;
            BuildSignatureBlock(ws, r, "Contabilidad / Finanzas", "Gerencia General");

            ws.Columns().AdjustToContents(14, 45);
        }

        private void BuildMonthlyDailyEvolutionSheet(
            XLWorkbook wb,
            int year,
            int month,
            int daysInMonth,
            List<CashRegisterShift> shifts,
            List<CashTransaction> transactions,
            List<PettyCashMovement> pettyMovements)
        {
            var ws = wb.Worksheets.Add("Evolución Diaria");
            ws.ShowGridLines = true;

            string monthName = new DateTime(year, month, 1).ToString("MMMM yyyy", new CultureInfo("es-ES"));
            BuildHeaderBanner(ws, "EVOLUCIÓN DÍA A DÍA DEL FLUJO DE CAJA Y FINANZAS", $"Mes: {monthName.ToUpper()}", 10);

            int r = 6;
            string[] headers = { "Fecha", "Día", "N° Turnos", "N° Transac.", "Ingresos Efectivo (S/)", "Ingresos Digital (S/)", "Total Ingresos (S/)", "Egresos Caja (S/)", "Caja Chica (S/)", "Flujo Neto (S/)" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(r, i + 1).Value = headers[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 10));
            r++;

            int startRow = r;
            for (int day = 1; day <= daysInMonth; day++)
            {
                var curDate = new DateTime(year, month, day);
                var curDateUtc = DateTime.SpecifyKind(curDate, DateTimeKind.Utc);
                var nextDateUtc = curDateUtc.AddDays(1);

                var dayShifts = shifts.Where(s => s.OpenedAt >= curDateUtc && s.OpenedAt < nextDateUtc).ToList();
                var dayTrans = transactions.Where(t => t.CreatedAt >= curDateUtc && t.CreatedAt < nextDateUtc).ToList();
                var dayPetty = pettyMovements.Where(m => m.RegisteredAt >= curDateUtc && m.RegisteredAt < nextDateUtc && m.MovementType == "Egreso").ToList();

                decimal dayCashIn = dayTrans.Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod == "Efectivo").Sum(t => t.Amount);
                decimal dayDigitalIn = dayTrans.Where(t => t.TransactionType == "Ingreso" && t.PaymentMethod != "Efectivo").Sum(t => t.Amount);
                decimal dayTotalIn = dayCashIn + dayDigitalIn;
                decimal dayCashOut = dayTrans.Where(t => t.TransactionType == "Egreso").Sum(t => t.Amount);
                decimal dayPettyOut = dayPetty.Sum(m => m.Amount);
                decimal dayNet = dayTotalIn - dayCashOut - dayPettyOut;

                ws.Cell(r, 1).Value = curDate.ToString("dd/MM/yyyy");
                ws.Cell(r, 2).Value = curDate.ToString("ddd", new CultureInfo("es-ES")).ToUpper();
                ws.Cell(r, 3).Value = dayShifts.Count;
                ws.Cell(r, 4).Value = dayTrans.Count;
                ws.Cell(r, 5).Value = dayCashIn;
                ws.Cell(r, 6).Value = dayDigitalIn;
                ws.Cell(r, 7).Value = dayTotalIn;
                ws.Cell(r, 8).Value = dayCashOut;
                ws.Cell(r, 9).Value = dayPettyOut;
                ws.Cell(r, 10).Value = dayNet;

                ws.Cell(r, 5).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 7).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 8).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 9).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var rowRange = ws.Range(r, 1, r, 10);
                rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.OutsideBorderColor = BorderGray;
                rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.InsideBorderColor = BorderGray;

                if (curDate.DayOfWeek == DayOfWeek.Sunday || curDate.DayOfWeek == DayOfWeek.Saturday)
                {
                    rowRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#E0F7FA");
                }
                else if (r % 2 == 0)
                {
                    rowRange.Style.Fill.BackgroundColor = BrandZebra;
                }

                r++;
            }

            // FILA TOTAL MES
            ws.Range(r, 1, r, 2).Merge();
            ws.Cell(r, 1).Value = "TOTAL ACUMULADO DEL MES:";
            ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

            ws.Cell(r, 3).FormulaA1 = $"=SUM(C{startRow}:C{r - 1})";
            ws.Cell(r, 4).FormulaA1 = $"=SUM(D{startRow}:D{r - 1})";
            ws.Cell(r, 5).FormulaA1 = $"=SUM(E{startRow}:E{r - 1})";
            ws.Cell(r, 6).FormulaA1 = $"=SUM(F{startRow}:F{r - 1})";
            ws.Cell(r, 7).FormulaA1 = $"=SUM(G{startRow}:G{r - 1})";
            ws.Cell(r, 8).FormulaA1 = $"=SUM(H{startRow}:H{r - 1})";
            ws.Cell(r, 9).FormulaA1 = $"=SUM(I{startRow}:I{r - 1})";
            ws.Cell(r, 10).FormulaA1 = $"=SUM(J{startRow}:J{r - 1})";

            ws.Cell(r, 5).Style.NumberFormat.Format = "\"S/\" #,##0.00";
            ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";
            ws.Cell(r, 7).Style.NumberFormat.Format = "\"S/\" #,##0.00";
            ws.Cell(r, 8).Style.NumberFormat.Format = "\"S/\" #,##0.00";
            ws.Cell(r, 9).Style.NumberFormat.Format = "\"S/\" #,##0.00";
            ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

            var totRow = ws.Range(r, 1, r, 10);
            totRow.Style.Font.Bold = true;
            totRow.Style.Fill.BackgroundColor = BrandSurfaceMint;
            totRow.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            totRow.Style.Border.OutsideBorderColor = BrandDarkTeal;

            ws.Columns().AdjustToContents(11, 30);
        }

        private void BuildMonthlyShiftsSheet(XLWorkbook wb, string monthName, int year, List<CashRegisterShift> shifts)
        {
            var ws = wb.Worksheets.Add("Turnos y Arqueos");
            ws.ShowGridLines = true;

            BuildHeaderBanner(ws, "REGISTRO MENSUAL DE TURNOS DE CAJA Y ARQUEOS FÍSICOS", $"Período: {monthName.ToUpper()} {year} • Total Turnos: {shifts.Count}", 12);

            int r = 6;
            string[] headers = { "N° Turno", "Fecha Apertura", "Hora Ap.", "Hora Cierre", "Cajero Responsable", "Fondo Base (S/)", "Ingresos Efec. (S/)", "Ingresos Digit. (S/)", "Total Recaud. (S/)", "Efectivo Teórico (S/)", "Arqueo Real (S/)", "Diferencia (S/)" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(r, i + 1).Value = headers[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 12));
            r++;

            if (shifts.Any())
            {
                foreach (var s in shifts)
                {
                    ws.Cell(r, 1).Value = s.ShiftNumber;
                    ws.Cell(r, 2).Value = s.OpenedAt.ToLocalTime().ToString("dd/MM/yyyy");
                    ws.Cell(r, 3).Value = s.OpenedAt.ToLocalTime().ToString("HH:mm");
                    ws.Cell(r, 4).Value = s.ClosedAt.HasValue ? s.ClosedAt.Value.ToLocalTime().ToString("HH:mm") : "EN CURSO";
                    ws.Cell(r, 5).Value = s.OpenedByUserName;
                    ws.Cell(r, 6).Value = s.InitialAmount;
                    ws.Cell(r, 7).Value = s.TotalCashIncome;
                    ws.Cell(r, 8).Value = s.TotalDigitalIncome;
                    ws.Cell(r, 9).Value = s.TotalIncome;
                    ws.Cell(r, 10).Value = s.ExpectedCashBalance;
                    ws.Cell(r, 11).Value = s.ActualCash.HasValue ? s.ActualCash.Value : 0m;
                    ws.Cell(r, 12).Value = s.CashDifference;

                    for (int c = 6; c <= 12; c++)
                    {
                        ws.Cell(r, c).Style.NumberFormat.Format = "\"S/\" #,##0.00";
                    }

                    var rowRange = ws.Range(r, 1, r, 12);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (s.CashDifference < 0)
                    {
                        ws.Cell(r, 12).Style.Font.FontColor = AlertRed;
                        ws.Cell(r, 12).Style.Font.Bold = true;
                    }
                    else if (s.CashDifference > 0)
                    {
                        ws.Cell(r, 12).Style.Font.FontColor = AlertGreen;
                        ws.Cell(r, 12).Style.Font.Bold = true;
                    }

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }
            }
            else
            {
                ws.Range(r, 1, r, 12).Merge();
                ws.Cell(r, 1).Value = "No hay turnos registrados en este mes.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
            }

            ws.Columns().AdjustToContents(11, 35);
        }

        private void BuildMonthlyPettyCashSheet(XLWorkbook wb, string monthName, int year, List<PettyCashMovement> pettyMovements)
        {
            var ws = wb.Worksheets.Add("Caja Chica del Mes");
            ws.ShowGridLines = true;

            BuildHeaderBanner(ws, "REGISTRO MENSUAL DE COMPROBANTES Y RENDICIONES DE CAJA CHICA", $"Período: {monthName.ToUpper()} {year} • Total Registros: {pettyMovements.Count}", 10);

            int r = 6;
            string[] headers = { "N°", "Fecha", "Tipo", "Categoría", "Concepto / Descripción", "Proveedor", "Comprobante", "N° Doc.", "Responsable", "Importe (S/)" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(r, i + 1).Value = headers[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 10));
            r++;

            int startRow = r;
            if (pettyMovements.Any())
            {
                int index = 1;
                foreach (var m in pettyMovements)
                {
                    ws.Cell(r, 1).Value = index++;
                    ws.Cell(r, 2).Value = m.RegisteredAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                    ws.Cell(r, 3).Value = m.MovementType;
                    ws.Cell(r, 4).Value = m.ExpenseCategory;
                    ws.Cell(r, 5).Value = m.Concept;
                    ws.Cell(r, 6).Value = !string.IsNullOrWhiteSpace(m.Supplier) ? m.Supplier : "Sin proveedor";
                    ws.Cell(r, 7).Value = m.VoucherType;
                    ws.Cell(r, 8).Value = !string.IsNullOrWhiteSpace(m.VoucherNumber) ? m.VoucherNumber : "--";
                    ws.Cell(r, 9).Value = m.ResponsiblePerson;
                    ws.Cell(r, 10).Value = m.Amount;
                    ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                    var rowRange = ws.Range(r, 1, r, 10);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;
                    rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.InsideBorderColor = BorderGray;

                    if (m.MovementType == "Reposicion")
                    {
                        ws.Cell(r, 3).Style.Font.FontColor = AlertGreen;
                        ws.Cell(r, 3).Style.Font.Bold = true;
                    }

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                // Fila Total
                ws.Range(r, 1, r, 9).Merge();
                ws.Cell(r, 1).Value = "TOTAL GASTOS RENDIDOS EN EL MES:";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(r, 10).FormulaA1 = $"=SUM(J{startRow}:J{r - 1})";
                ws.Cell(r, 10).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var totRange = ws.Range(r, 1, r, 10);
                totRange.Style.Font.Bold = true;
                totRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                totRange.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
                totRange.Style.Border.OutsideBorderColor = BrandDarkTeal;
            }
            else
            {
                ws.Range(r, 1, r, 10).Merge();
                ws.Cell(r, 1).Value = "No se registraron movimientos de caja chica en este mes.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
            }

            ws.Columns().AdjustToContents(11, 45);
        }
        #endregion

        #region 3. REPORTE / ACTA DE TURNO INDIVIDUAL
        public async Task<byte[]> GenerateShiftReportExcelAsync(Guid shiftId)
        {
            var shift = await _context.CashRegisterShifts
                .Include(s => s.Transactions)
                    .ThenInclude(t => t.Booking)
                .FirstOrDefaultAsync(s => s.Id == shiftId);

            if (shift == null) throw new ArgumentException("Turno no encontrado.");

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add($"Turno {shift.ShiftNumber}");
            ws.ShowGridLines = true;

            BuildHeaderBanner(ws, $"ACTA Y LIQUIDACIÓN DE TURNO DE CAJA — {shift.ShiftNumber}", $"Aperturado: {shift.OpenedAt.ToLocalTime():dd/MM/yyyy HH:mm} | Cajero: {shift.OpenedByUserName}", 8);

            int r = 6;
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "1. RESUMEN DE ARQUEO FÍSICO DEL TURNO";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            var summary = new List<(string Label, decimal Amount)>
            {
                ("Saldo Base Inicial en Gaveta", shift.InitialAmount),
                ("(+) Total Ingresos Efectivo", shift.TotalCashIncome),
                ("(-) Total Egresos Efectivo", shift.TotalCashExpense),
                ("(=) Saldo Teórico en Efectivo", shift.ExpectedCashBalance),
                ("(~) Arqueo Físico Real Contado", shift.ActualCash ?? 0m),
                ("(Δ) Diferencia de Caja", shift.CashDifference),
                ("(+) Total Recaudación Digital (Yape/Plin/Bancos)", shift.TotalDigitalIncome),
                ("(=) RECAUDACIÓN TOTAL DEL TURNO", shift.TotalIncome)
            };

            foreach (var item in summary)
            {
                ws.Range(r, 1, r, 5).Merge();
                ws.Range(r, 6, r, 8).Merge();
                ws.Cell(r, 1).Value = item.Label;
                ws.Cell(r, 6).Value = item.Amount;
                ws.Cell(r, 6).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var rowRange = ws.Range(r, 1, r, 8);
                rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.OutsideBorderColor = BorderGray;

                if (item.Label.Contains("RECAUDACIÓN TOTAL") || item.Label.Contains("Saldo Teórico"))
                {
                    rowRange.Style.Font.Bold = true;
                    rowRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
                }
                else if (item.Label.Contains("Diferencia"))
                {
                    rowRange.Style.Font.Bold = true;
                    if (item.Amount < 0) ws.Cell(r, 6).Style.Font.FontColor = AlertRed;
                    else if (item.Amount > 0) ws.Cell(r, 6).Style.Font.FontColor = AlertGreen;
                }
                else if (r % 2 == 0)
                {
                    rowRange.Style.Fill.BackgroundColor = BrandZebra;
                }
                r++;
            }

            r += 2;
            ws.Range(r, 1, r, 8).Merge();
            ws.Cell(r, 1).Value = "2. DETALLE DE MOVIMIENTOS REGISTRADOS EN EL TURNO";
            StyleSectionHeader(ws.Range(r, 1, r, 8));
            r++;

            string[] headers = { "N°", "Hora", "Tipo", "Categoría", "Descripción / Concepto", "Referencia", "Método", "Monto (S/)" };
            for (int i = 0; i < headers.Length; i++)
            {
                ws.Cell(r, i + 1).Value = headers[i];
            }
            StyleTableHeader(ws.Range(r, 1, r, 8));
            r++;

            int startRow = r;
            if (shift.Transactions.Any())
            {
                int index = 1;
                foreach (var t in shift.Transactions.OrderBy(t => t.CreatedAt))
                {
                    ws.Cell(r, 1).Value = index++;
                    ws.Cell(r, 2).Value = t.CreatedAt.ToLocalTime().ToString("HH:mm:ss");
                    ws.Cell(r, 3).Value = t.TransactionType;
                    ws.Cell(r, 4).Value = t.Category;
                    ws.Cell(r, 5).Value = t.Description;
                    ws.Cell(r, 6).Value = !string.IsNullOrWhiteSpace(t.ReferenceCode) ? t.ReferenceCode : (t.Booking?.ReferenceCode ?? "--");
                    ws.Cell(r, 7).Value = t.PaymentMethod;
                    ws.Cell(r, 8).Value = t.Amount;
                    ws.Cell(r, 8).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                    var rowRange = ws.Range(r, 1, r, 8);
                    rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    rowRange.Style.Border.OutsideBorderColor = BorderGray;

                    if (t.TransactionType == "Egreso")
                    {
                        ws.Cell(r, 3).Style.Font.FontColor = AlertRed;
                        ws.Cell(r, 8).Style.Font.FontColor = AlertRed;
                    }
                    else
                    {
                        ws.Cell(r, 3).Style.Font.FontColor = AlertGreen;
                    }

                    if (r % 2 == 0) rowRange.Style.Fill.BackgroundColor = BrandZebra;
                    r++;
                }

                ws.Range(r, 1, r, 7).Merge();
                ws.Cell(r, 1).Value = "TOTAL MOVIMIENTOS:";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                ws.Cell(r, 8).FormulaA1 = $"=SUM(H{startRow}:H{r - 1})";
                ws.Cell(r, 8).Style.NumberFormat.Format = "\"S/\" #,##0.00";

                var totRange = ws.Range(r, 1, r, 8);
                totRange.Style.Font.Bold = true;
                totRange.Style.Fill.BackgroundColor = BrandSurfaceMint;
            }
            else
            {
                ws.Range(r, 1, r, 8).Merge();
                ws.Cell(r, 1).Value = "No se registraron transacciones en este turno.";
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Cell(r, 1).Style.Font.Italic = true;
                ws.Cell(r, 1).Style.Font.FontColor = TextMuted;
            }

            r += 3;
            BuildSignatureBlock(ws, r, $"Cajero: {shift.OpenedByUserName}", "Supervisor / Administrador");

            ws.Columns().AdjustToContents(12, 40);

            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return ms.ToArray();
        }
        #endregion

        #region Helpers de Estilos
        private static void BuildHeaderBanner(IXLWorksheet ws, string title, string subtitle, int endCol)
        {
            ws.Row(1).Height = 6;
            ws.Row(2).Height = 28;
            ws.Row(3).Height = 22;
            ws.Row(4).Height = 18;
            ws.Row(5).Height = 8;

            // Fila 2: Título Empresa
            ws.Range(2, 1, 2, endCol).Merge();
            ws.Cell(2, 1).Value = "TURISMO CHILÓN TRANSPORTE & TURISMO S.A.C.";
            ws.Cell(2, 1).Style.Font.Bold = true;
            ws.Cell(2, 1).Style.Font.FontSize = 14;
            ws.Cell(2, 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(2, 1).Style.Fill.BackgroundColor = BrandDarkTeal;
            ws.Cell(2, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(2, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            // Fila 3: Título Reporte
            ws.Range(3, 1, 3, endCol).Merge();
            ws.Cell(3, 1).Value = title;
            ws.Cell(3, 1).Style.Font.Bold = true;
            ws.Cell(3, 1).Style.Font.FontSize = 11;
            ws.Cell(3, 1).Style.Font.FontColor = XLColor.White;
            ws.Cell(3, 1).Style.Fill.BackgroundColor = BrandPrimary;
            ws.Cell(3, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(3, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            // Fila 4: Subtítulo y Metadatos
            ws.Range(4, 1, 4, endCol).Merge();
            ws.Cell(4, 1).Value = $"{subtitle} | RUC: 20601234567 | Jr. 2 de Mayo 569, Cajamarca | Generado el: {DateTime.Now:dd/MM/yyyy HH:mm}";
            ws.Cell(4, 1).Style.Font.Italic = true;
            ws.Cell(4, 1).Style.Font.FontSize = 9;
            ws.Cell(4, 1).Style.Font.FontColor = TextDark;
            ws.Cell(4, 1).Style.Fill.BackgroundColor = BrandSurface;
            ws.Cell(4, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(4, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Cell(4, 1).Style.Border.BottomBorder = XLBorderStyleValues.Medium;
            ws.Cell(4, 1).Style.Border.BottomBorderColor = BrandPrimary;
        }

        private static void StyleSectionHeader(IXLRange range)
        {
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 10;
            range.Style.Font.FontColor = BrandDarkTeal;
            range.Style.Fill.BackgroundColor = BrandSurfaceMint;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            range.Style.Border.OutsideBorderColor = BrandPrimary;
        }

        private static void StyleTableHeader(IXLRange range)
        {
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 9;
            range.Style.Font.FontColor = XLColor.White;
            range.Style.Fill.BackgroundColor = BrandPrimary;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.OutsideBorderColor = BorderGray;
            range.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            range.Style.Border.InsideBorderColor = BorderGray;
        }

        private static void BuildSignatureBlock(IXLWorksheet ws, int startRow, string sign1, string sign2)
        {
            int r = startRow;
            ws.Cell(r, 2).Value = "____________________________________";
            ws.Cell(r, 6).Value = "____________________________________";
            ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(r, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(r, 2).Style.Font.FontColor = TextMuted;
            ws.Cell(r, 6).Style.Font.FontColor = TextMuted;

            r++;
            ws.Cell(r, 2).Value = sign1;
            ws.Cell(r, 6).Value = sign2;
            ws.Cell(r, 2).Style.Font.Bold = true;
            ws.Cell(r, 6).Style.Font.Bold = true;
            ws.Cell(r, 2).Style.Font.FontSize = 9;
            ws.Cell(r, 6).Style.Font.FontSize = 9;
            ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(r, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

            r++;
            ws.Cell(r, 2).Value = "Firma y Sello de Conformidad";
            ws.Cell(r, 6).Value = "V° B° Auditoría / Gerencia";
            ws.Cell(r, 2).Style.Font.Italic = true;
            ws.Cell(r, 6).Style.Font.Italic = true;
            ws.Cell(r, 2).Style.Font.FontSize = 8;
            ws.Cell(r, 6).Style.Font.FontSize = 8;
            ws.Cell(r, 2).Style.Font.FontColor = TextMuted;
            ws.Cell(r, 6).Style.Font.FontColor = TextMuted;
            ws.Cell(r, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Cell(r, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }
        #endregion
    }
}
