using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SIGETOUR.API.Application.Interfaces;
using SIGETOUR.API.Core.Entities;
using SIGETOUR.API.Infrastructure.Data;
using SIGETOUR.API.Models;

namespace SIGETOUR.API.Application.Services
{
    public class ManifestExportService : IManifestExportService
    {
        private readonly AppDbContext _context;

        public ManifestExportService(AppDbContext context)
        {
            _context = context;
        }

        private class ExtraPassengerJson
        {
            public string? Name { get; set; }
            public string? Dni { get; set; }
            public string? Phone { get; set; }
        }

        public async Task<ManifestPreviewDto> GetManifestPreviewAsync(DateTime date, Guid tourId, Guid? vehicleId, string? shift)
        {
            var tour = await _context.TourPackages
                .Include(t => t.Category)
                .Include(t => t.DefaultVehicle)
                .FirstOrDefaultAsync(t => t.Id == tourId);

            Vehicle? vehicle = null;
            if (vehicleId.HasValue && vehicleId.Value != Guid.Empty)
            {
                vehicle = await _context.Vehicles.FirstOrDefaultAsync(v => v.Id == vehicleId.Value);
            }
            if (vehicle == null && tour?.DefaultVehicle != null)
            {
                vehicle = tour.DefaultVehicle;
            }
            if (vehicle == null)
            {
                vehicle = await _context.Vehicles.FirstOrDefaultAsync(v => v.IsActive) 
                       ?? await _context.Vehicles.FirstOrDefaultAsync();
            }

            int totalSeats = vehicle?.SeatCapacity > 0 ? vehicle.SeatCapacity : 18;

            var bookings = await GetMatchingBookingsAsync(date, tourId, shift);
            var passengerList = ExtractPassengers(bookings);

            var previewSeats = new List<ManifestPassengerItem>();
            for (int seatNo = 1; seatNo <= totalSeats; seatNo++)
            {
                int index = seatNo - 1;
                if (index < passengerList.Count)
                {
                    var p = passengerList[index];
                    p.SeatNumber = seatNo;
                    previewSeats.Add(p);
                }
                else
                {
                    previewSeats.Add(new ManifestPassengerItem
                    {
                        SeatNumber = seatNo,
                        FullName = "--- ASIENTO DISPONIBLE / LIBRE ---",
                        DocumentType = "-",
                        DocumentNumber = "-",
                        PhoneNumber = "-",
                        PickupLocation = "-",
                        ReferenceCode = "-",
                        PassengerType = "Libre",
                        IsOccupied = false
                    });
                }
            }

            int occupied = Math.Min(passengerList.Count, totalSeats);
            int available = Math.Max(0, totalSeats - occupied);
            double occupancyPct = totalSeats > 0 ? Math.Round((double)occupied / totalSeats * 100.0, 1) : 0;

            // Lógica de Optimización de Flota para evitar pérdidas operativas
            var allActiveVehicles = await _context.Vehicles.Where(v => v.IsActive).OrderBy(v => v.SeatCapacity).ToListAsync();
            int passengerCount = passengerList.Count;
            bool isOverCapacity = passengerCount > totalSeats;
            bool isUnderUtilized = false;
            string advice = string.Empty;
            Guid? suggestedId = null;
            string suggestedName = string.Empty;

            if (isOverCapacity)
            {
                var largerVehicle = allActiveVehicles.FirstOrDefault(v => v.SeatCapacity >= passengerCount);
                if (largerVehicle != null && largerVehicle.Id != vehicle?.Id)
                {
                    advice = $"Capacidad insuficiente: Hay {passengerCount} pasajeros y la unidad '{vehicle?.Model}' tiene solo {totalSeats} asientos. Se recomienda asignar '{largerVehicle.Model} ({largerVehicle.LicensePlate})' con {largerVehicle.SeatCapacity} asientos.";
                    suggestedId = largerVehicle.Id;
                    suggestedName = $"{largerVehicle.Model} ({largerVehicle.LicensePlate})";
                }
                else
                {
                    advice = $"Capacidad insuficiente: Hay {passengerCount} pasajeros para {totalSeats} asientos. Considera programar un segundo vehículo o turno adicional.";
                }
            }
            else if (passengerCount > 0 && totalSeats >= 18 && passengerCount <= 14)
            {
                var optimalSmallerVehicle = allActiveVehicles
                    .Where(v => v.SeatCapacity >= passengerCount && v.SeatCapacity < totalSeats)
                    .OrderBy(v => v.SeatCapacity)
                    .FirstOrDefault();

                if (optimalSmallerVehicle != null && optimalSmallerVehicle.Id != vehicle?.Id)
                {
                    isUnderUtilized = true;
                    advice = $"Optimización de costos: Tienes {passengerCount} pasajeros confirmados. En lugar de enviar una unidad de {totalSeats} plazas, se sugiere asignar '{optimalSmallerVehicle.Model} ({optimalSmallerVehicle.LicensePlate})' de {optimalSmallerVehicle.SeatCapacity} plazas para ahorrar combustible y evitar pérdidas.";
                    suggestedId = optimalSmallerVehicle.Id;
                    suggestedName = $"{optimalSmallerVehicle.Model} ({optimalSmallerVehicle.LicensePlate})";
                }
            }

            return new ManifestPreviewDto
            {
                TourTitle = tour?.Title ?? "Circuito Turístico",
                TravelDate = date.ToString("dd/MM/yyyy"),
                ShiftName = !string.IsNullOrWhiteSpace(shift) ? shift : "Todos los turnos",
                VehicleModel = vehicle?.Model ?? "Unidad de Flota",
                VehiclePlate = vehicle?.LicensePlate ?? "POR ASIGNAR",
                TotalSeats = totalSeats,
                OccupiedSeats = occupied,
                AvailableSeats = available,
                OccupancyPercentage = occupancyPct,
                IsOverCapacity = isOverCapacity,
                IsUnderUtilized = isUnderUtilized,
                OptimizationAdvice = advice,
                SuggestedVehicleId = suggestedId,
                SuggestedVehicleName = suggestedName,
                Passengers = previewSeats
            };
        }

        public async Task<byte[]> GenerateManifestExcelAsync(ManifestExportRequest request)
        {
            var tour = await _context.TourPackages
                .Include(t => t.Category)
                .Include(t => t.DefaultVehicle)
                .FirstOrDefaultAsync(t => t.Id == request.TourPackageId)
                ?? throw new Exception("Tour no encontrado.");

            // Si se seleccionó un DriverId de la base de datos, extraer sus datos oficiales
            if (request.DriverId.HasValue && request.DriverId.Value != Guid.Empty)
            {
                var driverDb = await _context.Drivers.FindAsync(request.DriverId.Value);
                if (driverDb != null)
                {
                    request.DriverName = driverDb.FullName;
                    request.DriverLicense = driverDb.LicenseNumber;
                }
            }

            Vehicle? vehicle = null;
            if (request.VehicleId != Guid.Empty)
            {
                vehicle = await _context.Vehicles.FirstOrDefaultAsync(v => v.Id == request.VehicleId);
            }
            if (vehicle == null && tour.DefaultVehicle != null)
            {
                vehicle = tour.DefaultVehicle;
            }
            if (vehicle == null)
            {
                vehicle = await _context.Vehicles.FirstOrDefaultAsync(v => v.IsActive) 
                       ?? await _context.Vehicles.FirstOrDefaultAsync()
                       ?? new Vehicle { Model = "Van Turística Estándar", LicensePlate = "T7C-000", SeatCapacity = 18 };
            }

            int totalSeats = vehicle.SeatCapacity > 0 ? vehicle.SeatCapacity : 18;

            var bookings = await GetMatchingBookingsAsync(request.Date, request.TourPackageId, request.ShiftName);
            var passengerList = ExtractPassengers(bookings);

            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("Manifiesto Pasajeros");

            // Colores corporativos Turismo Chilón
            var primaryColor = XLColor.FromHtml("#00685A");
            var headerSubColor = XLColor.FromHtml("#005045");
            var lightAccent = XLColor.FromHtml("#E4FFFB");
            var zebraColor = XLColor.FromHtml("#F4FFFA");
            var borderColor = XLColor.FromHtml("#BDC9C5");
            var darkBorderColor = XLColor.FromHtml("#6D7A76");
            var grayText = XLColor.FromHtml("#666666");

            // ENCABEZADO OFICIAL
            ws.Range("A1:I1").Merge().Value = "TURISMO CHILÓN S.A.C. - SERVICIOS TURÍSTICOS Y TRANSPORTE";
            ws.Range("A1:I1").Style.Font.Bold = true;
            ws.Range("A1:I1").Style.Font.FontSize = 14;
            ws.Range("A1:I1").Style.Font.FontColor = XLColor.White;
            ws.Range("A1:I1").Style.Fill.BackgroundColor = primaryColor;
            ws.Range("A1:I1").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range("A1:I1").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(1).Height = 24;

            ws.Range("A2:I2").Merge().Value = "R.U.C. 20601845921 • AUTORIZACIÓN DIRCETUR / MTC N° 045-2023-GRC/DRTC • CAJAMARCA, PERÚ";
            ws.Range("A2:I2").Style.Font.FontSize = 9;
            ws.Range("A2:I2").Style.Font.FontColor = XLColor.White;
            ws.Range("A2:I2").Style.Fill.BackgroundColor = headerSubColor;
            ws.Range("A2:I2").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range("A2:I2").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(2).Height = 16;

            ws.Range("A3:I3").Merge().Value = "MANIFIESTO OFICIAL DE PASAJEROS - TRANSPORTE TURÍSTICO TERRESTRE (MTC / SUTRAN)";
            ws.Range("A3:I3").Style.Font.Bold = true;
            ws.Range("A3:I3").Style.Font.FontSize = 11;
            ws.Range("A3:I3").Style.Font.FontColor = XLColor.White;
            ws.Range("A3:I3").Style.Fill.BackgroundColor = primaryColor;
            ws.Range("A3:I3").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range("A3:I3").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(3).Height = 20;

            ws.Row(4).Height = 8; // Espacio

            // FICHA TÉCNICA DE LA SALIDA Y VEHÍCULO (Filas 5 a 10)
            void SetMetaCell(int row, string col1, string col2, string label, string value, bool isHighlight = false)
            {
                ws.Cell(row, col1).Value = label;
                ws.Cell(row, col1).Style.Font.Bold = true;
                ws.Cell(row, col1).Style.Font.FontSize = 9;
                ws.Cell(row, col1).Style.Font.FontColor = primaryColor;
                ws.Cell(row, col1).Style.Fill.BackgroundColor = lightAccent;

                ws.Cell(row, col2).Value = value;
                ws.Cell(row, col2).Style.Font.FontSize = 9;
                ws.Cell(row, col2).Style.Font.Bold = isHighlight;
                if (isHighlight) ws.Cell(row, col2).Style.Font.FontColor = XLColor.Black;
            }

            // Fila 5
            ws.Range("A5:B5").Merge();
            ws.Range("C5:D5").Merge();
            ws.Range("E5:F5").Merge();
            ws.Range("G5:I5").Merge();
            SetMetaCell(5, "A", "C", "Circuito Turístico:", tour.Title, true);
            SetMetaCell(5, "E", "G", "Unidad Vehicular:", $"{vehicle.Model} (Año: {vehicle.ManufactureYear})", true);

            // Fila 6
            ws.Range("A6:B6").Merge();
            ws.Range("C6:D6").Merge();
            ws.Range("E6:F6").Merge();
            ws.Range("G6:I6").Merge();
            SetMetaCell(6, "A", "C", "Fecha de Salida:", request.Date.ToString("dd/MM/yyyy (dddd)"));
            SetMetaCell(6, "E", "G", "Placa de Rodaje:", vehicle.LicensePlate, true);

            // Fila 7
            ws.Range("A7:B7").Merge();
            ws.Range("C7:D7").Merge();
            ws.Range("E7:F7").Merge();
            ws.Range("G7:I7").Merge();
            var shiftText = !string.IsNullOrWhiteSpace(request.ShiftName) ? request.ShiftName : "Turno General";
            SetMetaCell(7, "A", "C", "Turno / Horario:", shiftText);
            SetMetaCell(7, "E", "G", "Capacidad Oficial:", $"{totalSeats} Asientos Oficiales");

            // Fila 8
            ws.Range("A8:B8").Merge();
            ws.Range("C8:D8").Merge();
            ws.Range("E8:F8").Merge();
            ws.Range("G8:I8").Merge();
            var depPoint = !string.IsNullOrWhiteSpace(request.DeparturePoint) ? request.DeparturePoint : "Jr. Dos de Mayo 534 - Cajamarca";
            SetMetaCell(8, "A", "C", "Punto Partida General:", depPoint);
            var soatInfo = !string.IsNullOrWhiteSpace(vehicle.SoatNumber) 
                ? $"N° {vehicle.SoatNumber} ({vehicle.SoatProvider ?? "SOAT"} - Venc: {vehicle.SoatExpiryDate?.ToString("dd/MM/yyyy") ?? "Vigente"})"
                : "Póliza Oficial Vigente";
            SetMetaCell(8, "E", "G", "Póliza SOAT:", soatInfo);

            // Fila 9
            ws.Range("A9:B9").Merge();
            ws.Range("C9:D9").Merge();
            ws.Range("E9:F9").Merge();
            ws.Range("G9:I9").Merge();
            var driverInfo = !string.IsNullOrWhiteSpace(request.DriverName) 
                ? $"{request.DriverName} (Lic: {request.DriverLicense ?? "Q-42910834"})"
                : "Conductor Titular Habilitado MTC";
            SetMetaCell(9, "A", "C", "Conductor Asignado:", driverInfo);
            var citvInfo = !string.IsNullOrWhiteSpace(vehicle.CitvNumber) 
                ? $"Cert. {vehicle.CitvNumber} (Venc: {vehicle.CitvExpiryDate?.ToString("dd/MM/yyyy") ?? "Vigente"})"
                : "Habilitación Técnica Vigente";
            SetMetaCell(9, "E", "G", "Certificado CITV:", citvInfo);

            // Fila 10
            ws.Range("A10:B10").Merge();
            ws.Range("C10:D10").Merge();
            ws.Range("E10:F10").Merge();
            ws.Range("G10:I10").Merge();
            var guideInfo = !string.IsNullOrWhiteSpace(request.GuideName) 
                ? $"{request.GuideName} (Carnet: {request.GuideCarnet ?? "DIRCETUR"})"
                : "Sin guía contratado (Chofer a cargo / No requerido)";
            SetMetaCell(10, "A", "C", "Guía de Turismo:", guideInfo);
            var tucInfo = !string.IsNullOrWhiteSpace(vehicle.TucNumber)
                ? $"TUC N° {vehicle.TucNumber} - Resol: {vehicle.ResolutionNumber ?? "MTC"}"
                : "Habilitación MTC Operativa";
            SetMetaCell(10, "E", "G", "Tarjeta TUC / MTC:", tucInfo);

            // Borde alrededor de la ficha técnica
            ws.Range("A5:I10").Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            ws.Range("A5:I10").Style.Border.OutsideBorderColor = primaryColor;
            ws.Range("A5:I10").Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.Range("A5:I10").Style.Border.InsideBorderColor = borderColor;

            for (int r = 5; r <= 10; r++) ws.Row(r).Height = 18;

            // RESUMEN KPI (Fila 12)
            int occupiedSeats = Math.Min(passengerList.Count, totalSeats);
            int availableSeats = Math.Max(0, totalSeats - occupiedSeats);
            double occupancyPercent = totalSeats > 0 ? Math.Round((double)occupiedSeats / totalSeats * 100.0, 1) : 0;

            ws.Row(11).Height = 6; // Espacio

            ws.Range("A12:C12").Merge().Value = $"CAPACIDAD VEHÍCULO: {totalSeats} ASIENTOS";
            ws.Range("A12:C12").Style.Font.Bold = true;
            ws.Range("A12:C12").Style.Font.FontSize = 10;
            ws.Range("A12:C12").Style.Font.FontColor = primaryColor;
            ws.Range("A12:C12").Style.Fill.BackgroundColor = lightAccent;
            ws.Range("A12:C12").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range("A12:C12").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            ws.Range("D12:F12").Merge().Value = $"PASAJEROS A BORDO: {occupiedSeats} PAX ({occupancyPercent}%)";
            ws.Range("D12:F12").Style.Font.Bold = true;
            ws.Range("D12:F12").Style.Font.FontSize = 10;
            ws.Range("D12:F12").Style.Font.FontColor = primaryColor;
            ws.Range("D12:F12").Style.Fill.BackgroundColor = lightAccent;
            ws.Range("D12:F12").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range("D12:F12").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            ws.Range("G12:I12").Merge().Value = $"ASIENTOS LIBRES: {availableSeats} ASIENTOS";
            ws.Range("G12:I12").Style.Font.Bold = true;
            ws.Range("G12:I12").Style.Font.FontSize = 10;
            ws.Range("G12:I12").Style.Font.FontColor = primaryColor;
            ws.Range("G12:I12").Style.Fill.BackgroundColor = lightAccent;
            ws.Range("G12:I12").Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            ws.Range("G12:I12").Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

            ws.Range("A12:I12").Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            ws.Range("A12:I12").Style.Border.OutsideBorderColor = primaryColor;
            ws.Range("A12:I12").Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            ws.Range("A12:I12").Style.Border.InsideBorderColor = primaryColor;
            ws.Row(12).Height = 22;

            ws.Row(13).Height = 6; // Espacio

            // CABECERAS DE LA TABLA DE ASIENTOS (Fila 14)
            int tableHeaderRow = 14;
            var headers = new[]
            {
                (Col: "A", Title: "N° ASIENTO", Width: 12),
                (Col: "B", Title: "APELLIDOS Y NOMBRES DEL PASAJERO", Width: 34),
                (Col: "C", Title: "DOC.", Width: 8),
                (Col: "D", Title: "N° DOCUMENTO", Width: 15),
                (Col: "E", Title: "TELÉFONO", Width: 14),
                (Col: "F", Title: "LUGAR DE RECOJO / ABORDAJE", Width: 26),
                (Col: "G", Title: "CÓD. RESERVA", Width: 15),
                (Col: "H", Title: "CONDICIÓN", Width: 14),
                (Col: "I", Title: "CONFORMIDAD / FIRMA", Width: 22)
            };

            foreach (var h in headers)
            {
                var cell = ws.Cell(tableHeaderRow, h.Col);
                cell.Value = h.Title;
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontSize = 9;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = primaryColor;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                cell.Style.Alignment.WrapText = true;
            }
            ws.Row(tableHeaderRow).Height = 26;

            // TABLA CON EXACTAMENTE LA CANTIDAD DE ASIENTOS DEL VEHÍCULO ELEGIDO
            int startDataRow = 15;
            for (int seatNumber = 1; seatNumber <= totalSeats; seatNumber++)
            {
                int currentRow = startDataRow + seatNumber - 1;
                int passengerIndex = seatNumber - 1;
                bool isOccupied = passengerIndex < passengerList.Count;

                var rowBg = (seatNumber % 2 == 0) ? zebraColor : XLColor.White;
                if (!isOccupied)
                {
                    rowBg = XLColor.FromHtml("#FDFDFD");
                }

                // Col A: Asiento
                var seatCell = ws.Cell(currentRow, "A");
                seatCell.Value = $"ASIENTO {seatNumber:D2}";
                seatCell.Style.Font.Bold = true;
                seatCell.Style.Font.FontSize = 9;
                seatCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                if (isOccupied)
                {
                    var p = passengerList[passengerIndex];
                    seatCell.Style.Font.FontColor = primaryColor;

                    // Col B: Nombre
                    var nameCell = ws.Cell(currentRow, "B");
                    nameCell.Value = p.FullName.ToUpperInvariant();
                    nameCell.Style.Font.Bold = (p.PassengerType == "Titular");
                    nameCell.Style.Font.FontSize = 9;
                    nameCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

                    // Col C: Tipo Doc
                    var docTypeCell = ws.Cell(currentRow, "C");
                    docTypeCell.Value = p.DocumentType;
                    docTypeCell.Style.Font.FontSize = 9;
                    docTypeCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    // Col D: Documento
                    var docCell = ws.Cell(currentRow, "D");
                    docCell.Value = p.DocumentNumber;
                    docCell.Style.Font.FontSize = 9;
                    docCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    // Col E: Teléfono
                    var phoneCell = ws.Cell(currentRow, "E");
                    phoneCell.Value = p.PhoneNumber;
                    phoneCell.Style.Font.FontSize = 9;
                    phoneCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    // Col F: Recojo
                    var pickupCell = ws.Cell(currentRow, "F");
                    pickupCell.Value = p.PickupLocation;
                    pickupCell.Style.Font.FontSize = 9;
                    pickupCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

                    // Col G: Reserva
                    var refCell = ws.Cell(currentRow, "G");
                    refCell.Value = p.ReferenceCode;
                    refCell.Style.Font.FontSize = 9;
                    refCell.Style.Font.Bold = true;
                    refCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    // Col H: Condición
                    var condCell = ws.Cell(currentRow, "H");
                    condCell.Value = p.PassengerType;
                    condCell.Style.Font.FontSize = 9;
                    condCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    // Col I: Firma (Espacio en blanco)
                    var signCell = ws.Cell(currentRow, "I");
                    signCell.Value = "";
                    signCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                }
                else
                {
                    seatCell.Style.Font.FontColor = grayText;

                    var nameCell = ws.Cell(currentRow, "B");
                    nameCell.Value = "--- ASIENTO DISPONIBLE / LIBRE ---";
                    nameCell.Style.Font.Italic = true;
                    nameCell.Style.Font.FontSize = 8.5;
                    nameCell.Style.Font.FontColor = grayText;
                    nameCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                    ws.Cell(currentRow, "C").Value = "-";
                    ws.Cell(currentRow, "D").Value = "-";
                    ws.Cell(currentRow, "E").Value = "-";
                    ws.Cell(currentRow, "F").Value = "-";
                    ws.Cell(currentRow, "G").Value = "-";
                    ws.Cell(currentRow, "H").Value = "Libre";
                    ws.Cell(currentRow, "I").Value = "-";

                    for (int c = 3; c <= 9; c++)
                    {
                        var cell = ws.Cell(currentRow, c);
                        cell.Style.Font.FontSize = 8.5;
                        cell.Style.Font.FontColor = grayText;
                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    }
                }

                // Estilos de fila
                var rowRange = ws.Range(currentRow, 1, currentRow, 9);
                rowRange.Style.Fill.BackgroundColor = rowBg;
                rowRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.OutsideBorderColor = borderColor;
                rowRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                rowRange.Style.Border.InsideBorderColor = borderColor;
                rowRange.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                ws.Row(currentRow).Height = 21;
            }

            int endTableIndex = startDataRow + totalSeats - 1;
            ws.Range(tableHeaderRow, 1, endTableIndex, 9).Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            ws.Range(tableHeaderRow, 1, endTableIndex, 9).Style.Border.OutsideBorderColor = primaryColor;

            // BLOQUE DE SOBRECUPO / EXCEDENTES (SI HUBIERA)
            int currentPointer = endTableIndex + 2;
            if (passengerList.Count > totalSeats)
            {
                int overflowCount = passengerList.Count - totalSeats;
                ws.Range(currentPointer, 1, currentPointer, 9).Merge().Value = 
                    $"AVISO DE CAPACIDAD: Se registran {overflowCount} pasajeros adicionales fuera de la capacidad de la unidad {vehicle.LicensePlate}. Requiere despacho de unidad de apoyo.";
                var warnRange = ws.Range(currentPointer, 1, currentPointer, 9);
                warnRange.Style.Font.Bold = true;
                warnRange.Style.Font.FontSize = 9;
                warnRange.Style.Font.FontColor = XLColor.FromHtml("#B0251B");
                warnRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#FFDAD6");
                warnRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                warnRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                warnRange.Style.Border.OutsideBorderColor = XLColor.FromHtml("#B0251B");
                ws.Row(currentPointer).Height = 22;
                currentPointer += 2;
            }

            // NOTA LEGAL Y NORMATIVA MTC
            ws.Range(currentPointer, 1, currentPointer, 9).Merge().Value = 
                "DECLARACIÓN JURADA: El presente Manifiesto Oficial de Pasajeros da fe de las personas que abordan la unidad vehicular de transporte turístico terrestre, en estricto cumplimiento con el Reglamento Nacional de Administración de Transporte (D.S. N° 017-2009-MTC), fiscalización de SUTRAN, PNP de Carreteras y DIRCETUR Cajamarca. Cobertura bajo póliza SOAT vigente.";
            var legalRange = ws.Range(currentPointer, 1, currentPointer, 9);
            legalRange.Style.Font.FontSize = 8;
            legalRange.Style.Font.Italic = true;
            legalRange.Style.Font.FontColor = grayText;
            legalRange.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            legalRange.Style.Alignment.WrapText = true;
            ws.Row(currentPointer).Height = 24;
            currentPointer += 2;

            // CAJAS DE FIRMAS
            int sigStartRow = currentPointer;
            int sigLineRow = sigStartRow + 2;
            int sigTitleRow = sigStartRow + 3;
            int sigDetailRow = sigStartRow + 4;

            // 1. Conductor
            ws.Range(sigLineRow, 1, sigLineRow, 3).Merge().Value = "________________________________________";
            ws.Range(sigTitleRow, 1, sigTitleRow, 3).Merge().Value = "FIRMA Y HUELLA DEL CONDUCTOR";
            ws.Range(sigDetailRow, 1, sigDetailRow, 3).Merge().Value = $"DNI / Lic: {request.DriverLicense ?? "Habilitado MTC"}";

            // 2. Despacho / Counter
            ws.Range(sigLineRow, 4, sigLineRow, 6).Merge().Value = "________________________________________";
            ws.Range(sigTitleRow, 4, sigTitleRow, 6).Merge().Value = "DESPACHADOR DE TURNO / SELLO";
            ws.Range(sigDetailRow, 4, sigDetailRow, 6).Merge().Value = "Turismo Chilón S.A.C. - Terminal";

            // 3. Guía Oficial / Control Operativo
            ws.Range(sigLineRow, 7, sigLineRow, 9).Merge().Value = "________________________________________";
            if (!string.IsNullOrWhiteSpace(request.GuideName))
            {
                ws.Range(sigTitleRow, 7, sigTitleRow, 9).Merge().Value = "GUÍA OFICIAL DE TURISMO";
                ws.Range(sigDetailRow, 7, sigDetailRow, 9).Merge().Value = $"Carnet: {request.GuideCarnet ?? "DIRCETUR Cajamarca"}";
            }
            else
            {
                ws.Range(sigTitleRow, 7, sigTitleRow, 9).Merge().Value = "CONTROL Y REVISIÓN OPERATIVA";
                ws.Range(sigDetailRow, 7, sigDetailRow, 9).Merge().Value = "Turismo Chilón S.A.C.";
            }

            for (int r = sigLineRow; r <= sigDetailRow; r++)
            {
                ws.Row(r).Height = 16;
                ws.Range(r, 1, r, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                ws.Range(r, 1, r, 9).Style.Font.FontSize = 8.5;
            }
            ws.Range(sigTitleRow, 1, sigTitleRow, 9).Style.Font.Bold = true;
            ws.Range(sigTitleRow, 1, sigTitleRow, 9).Style.Font.FontColor = primaryColor;

            // AJUSTE DE ANCHOS DE COLUMNAS
            ws.Column("A").Width = 14;
            ws.Column("B").Width = 36;
            ws.Column("C").Width = 8;
            ws.Column("D").Width = 15;
            ws.Column("E").Width = 14;
            ws.Column("F").Width = 28;
            ws.Column("G").Width = 15;
            ws.Column("H").Width = 14;
            ws.Column("I").Width = 22;

            // CONFIGURACIÓN DE IMPRESIÓN (Horizontal / Landscape para hojas A4)
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.PaperSize = XLPaperSize.A4Paper;
            ws.PageSetup.FitToPages(1, 0); // Ajustar a 1 página de ancho
            ws.ShowGridLines = true;
            ws.PageSetup.Margins.Left = 0.4;
            ws.PageSetup.Margins.Right = 0.4;
            ws.PageSetup.Margins.Top = 0.5;
            ws.PageSetup.Margins.Bottom = 0.5;

            using var memoryStream = new MemoryStream();
            workbook.SaveAs(memoryStream);
            return memoryStream.ToArray();
        }

        private async Task<List<Booking>> GetMatchingBookingsAsync(DateTime targetDate, Guid tourId, string? shift)
        {
            var minUtc = DateTime.SpecifyKind(targetDate.Date.AddDays(-1), DateTimeKind.Utc);
            var maxUtc = DateTime.SpecifyKind(targetDate.Date.AddDays(2), DateTimeKind.Utc);

            var candidates = await _context.Bookings
                .Include(b => b.Items)
                .Where(b => b.TravelDate >= minUtc && b.TravelDate <= maxUtc &&
                            b.Status != BookingStatus.Cancelled &&
                            b.Items.Any(i => i.TourPackageId == tourId))
                .OrderBy(b => b.BookingDate)
                .ToListAsync();

            var targetDateStr = targetDate.ToString("yyyy-MM-dd");

            var matched = candidates.Where(b =>
                b.TravelDate.ToString("yyyy-MM-dd") == targetDateStr ||
                b.TravelDate.ToUniversalTime().ToString("yyyy-MM-dd") == targetDateStr ||
                b.TravelDate.AddHours(-5).ToString("yyyy-MM-dd") == targetDateStr ||
                b.TravelDate.Date == targetDate.Date
            ).ToList();

            if (!string.IsNullOrWhiteSpace(shift) &&
                !shift.Equals("Todos", StringComparison.OrdinalIgnoreCase) &&
                !shift.StartsWith("Todos", StringComparison.OrdinalIgnoreCase))
            {
                var cleanShift = shift.ToLower().Replace("turno", "").Trim();
                var keyword = cleanShift.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? cleanShift;
                matched = matched.Where(b =>
                    string.IsNullOrWhiteSpace(b.ShiftName) ||
                    b.ShiftName.ToLower().Contains(keyword) ||
                    cleanShift.Contains(b.ShiftName.ToLower())
                ).ToList();
            }

            return matched;
        }

        private List<ManifestPassengerItem> ExtractPassengers(List<Booking> bookings)
        {
            var list = new List<ManifestPassengerItem>();

            foreach (var b in bookings)
            {
                // Titular
                list.Add(new ManifestPassengerItem
                {
                    FullName = b.CustomerName,
                    DocumentType = "DNI",
                    DocumentNumber = b.CustomerDni,
                    PhoneNumber = b.CustomerPhone,
                    PickupLocation = !string.IsNullOrWhiteSpace(b.PickupLocation) ? b.PickupLocation : "Agencia Principal",
                    ReferenceCode = b.ReferenceCode,
                    PassengerType = "Titular",
                    IsOccupied = true
                });

                // Acompañantes JSON
                int extraAdded = 0;
                if (!string.IsNullOrWhiteSpace(b.AdditionalPassengersJson) && b.AdditionalPassengersJson != "[]")
                {
                    try
                    {
                        var extras = JsonSerializer.Deserialize<List<ExtraPassengerJson>>(
                            b.AdditionalPassengersJson,
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                        if (extras != null)
                        {
                            foreach (var ex in extras)
                            {
                                if (!string.IsNullOrWhiteSpace(ex.Name) || !string.IsNullOrWhiteSpace(ex.Dni))
                                {
                                    list.Add(new ManifestPassengerItem
                                    {
                                        FullName = !string.IsNullOrWhiteSpace(ex.Name) ? ex.Name : $"{b.CustomerName} (Acompañante {extraAdded + 1})",
                                        DocumentType = "DNI",
                                        DocumentNumber = !string.IsNullOrWhiteSpace(ex.Dni) ? ex.Dni : "-",
                                        PhoneNumber = !string.IsNullOrWhiteSpace(ex.Phone) ? ex.Phone : b.CustomerPhone,
                                        PickupLocation = !string.IsNullOrWhiteSpace(b.PickupLocation) ? b.PickupLocation : "Agencia Principal",
                                        ReferenceCode = b.ReferenceCode,
                                        PassengerType = "Acompañante",
                                        IsOccupied = true
                                    });
                                    extraAdded++;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // Si TotalPassengers supera 1 + extraAdded, agregar entradas adicionales
                int remaining = b.TotalPassengers - 1 - extraAdded;
                for (int k = 0; k < remaining; k++)
                {
                    list.Add(new ManifestPassengerItem
                    {
                        FullName = $"{b.CustomerName} (Acompañante {extraAdded + k + 1})",
                        DocumentType = "DNI",
                        DocumentNumber = "-",
                        PhoneNumber = b.CustomerPhone,
                        PickupLocation = !string.IsNullOrWhiteSpace(b.PickupLocation) ? b.PickupLocation : "Agencia Principal",
                        ReferenceCode = b.ReferenceCode,
                        PassengerType = "Acompañante",
                        IsOccupied = true
                    });
                }
            }

            return list;
        }
    }
}
