using System;
using System.Collections.Generic;
using System.Linq;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Service that handles slicing cable trays/ladders into standard lengths.
    /// Maintains routing integrity by not cutting fittings (bends, tees, risers).
    /// Adds 1mm coupling gap at each connection point.
    /// </summary>
    public class SlicingService
    {
        private readonly BOMSettings _settings;

        public SlicingService(BOMSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Slice a cable tray segment into standard-length pieces.
        /// Fittings are never sliced. Straight segments are divided into
        /// pieces of the specified standard length plus remainder.
        /// </summary>
        public void SliceSegment(CableTraySegment segment, double? customSliceLength = null)
        {
            double sliceLength = customSliceLength ?? _settings.DefaultSliceLength;
            segment.SlicedPieces.Clear();

            // Fittings (bends, tees, risers, etc.) are NEVER sliced
            if (segment.IsFitting)
            {
                segment.SlicedPieces.Add(new SlicedPiece
                {
                    Length = segment.OriginalLength,
                    LengthWithCoupling = segment.OriginalLength,
                    IsFullLength = false,
                    OrderPieces = 1
                });
                // Do NOT set ConnectionCount here — it comes from actual connector analysis
                return;
            }

            double totalLength = segment.OriginalLength;

            if (totalLength <= 0)
            {
                // Do NOT reset ConnectionCount — it comes from actual connector analysis
                return;
            }

            // Simple calculation: how many full pieces fit, plus remainder
            // Each piece is sliceLength (e.g. 3000mm). Coupling gaps (1mm each) are
            // for ordering/installation but don't change how we divide the length.
            int fullPieces = (int)Math.Floor(totalLength / sliceLength);
            double remainder = totalLength - (fullPieces * sliceLength);

            // Add full-length pieces
            for (int i = 0; i < fullPieces; i++)
            {
                segment.SlicedPieces.Add(new SlicedPiece
                {
                    Length = sliceLength,
                    LengthWithCoupling = sliceLength + _settings.CouplingGap,
                    IsFullLength = true,
                    OrderPieces = 1
                });
            }

            // Add remainder piece if any
            if (remainder > _settings.CouplingGap) // Only if there's meaningful length left
            {
                segment.SlicedPieces.Add(new SlicedPiece
                {
                    Length = remainder,
                    LengthWithCoupling = remainder + _settings.CouplingGap,
                    IsFullLength = false,
                    // Round up: a partial piece still requires ordering 1 full piece
                    OrderPieces = _settings.RoundUpOrderQuantity ? 1 : 0
                });
            }

            // Do NOT set ConnectionCount from slice count.
            // ConnectionCount comes only from actual Revit connector analysis
            // in RevitElementCollector.CalculateConnections().
        }

        /// <summary>
        /// Calculate total number of standard-length pieces to order for a set of segments.
        /// Groups by tray type and size, rounds up partial pieces.
        /// </summary>
        public Dictionary<string, OrderSummary> CalculateOrderQuantities(
            IEnumerable<CableTraySegment> segments, double? customSliceLength = null)
        {
            double sliceLength = customSliceLength ?? _settings.DefaultSliceLength;
            var orderMap = new Dictionary<string, OrderSummary>();

            foreach (var segment in segments)
            {
                // Ensure segment is sliced (needed for connection counting)
                if (segment.SlicedPieces.Count == 0)
                    SliceSegment(segment, customSliceLength);

                string key = $"{segment.TrayType}|{segment.Size}|{segment.PartNumber}|{segment.ServiceType}";

                if (!orderMap.ContainsKey(key))
                {
                    orderMap[key] = new OrderSummary
                    {
                        TrayType = segment.TrayType,
                        Size = segment.Size,
                        PartNumber = segment.PartNumber,
                        Manufacturer = segment.Manufacturer,
                        Description = segment.Description,
                        ServiceType = segment.ServiceType,
                        StandardLengthMm = sliceLength
                    };
                }

                var summary = orderMap[key];
                summary.TotalOriginalLengthMm += segment.OriginalLength;
                summary.TotalConnections += segment.ConnectionCount;
                summary.TotalSupports += segment.SupportCount;
            }

            // Calculate order pieces from AGGREGATE total length per type/size
            // This avoids rounding up each small segment individually
            foreach (var summary in orderMap.Values)
            {
                double totalLen = summary.TotalOriginalLengthMm;
                int fullPieces = (int)Math.Floor(totalLen / sliceLength);
                double remainder = totalLen - (fullPieces * sliceLength);

                summary.FullPieces = fullPieces;
                summary.PartialPieces = remainder > 0 ? 1 : 0;
                summary.TotalOrderPieces = fullPieces + summary.PartialPieces;
            }

            return orderMap;
        }

        /// <summary>
        /// Calculate order quantities grouped by room.
        /// </summary>
        public Dictionary<string, Dictionary<string, OrderSummary>> CalculateOrderByRoom(
            IEnumerable<CableTraySegment> segments, double? customSliceLength = null)
        {
            var roomMap = new Dictionary<string, Dictionary<string, OrderSummary>>();

            var groupedByRoom = segments.GroupBy(s =>
                string.IsNullOrEmpty(s.RoomNumber) ? s.RoomName : $"{s.RoomNumber} - {s.RoomName}");

            foreach (var roomGroup in groupedByRoom)
            {
                string roomKey = string.IsNullOrEmpty(roomGroup.Key) ? "Unassigned" : roomGroup.Key;
                roomMap[roomKey] = CalculateOrderQuantities(roomGroup, customSliceLength);
            }

            return roomMap;
        }
    }

    public class OrderSummary
    {
        public TrayCategory TrayType { get; set; }
        public string Size { get; set; } = "";
        public string PartNumber { get; set; } = "";
        public string Manufacturer { get; set; } = "";
        public string Description { get; set; } = "";
        public string ServiceType { get; set; } = "";
        public double StandardLengthMm { get; set; }
        public double TotalOriginalLengthMm { get; set; }
        public int FullPieces { get; set; }
        public int PartialPieces { get; set; }
        public int TotalOrderPieces { get; set; }
        public int TotalConnections { get; set; }
        public int TotalSupports { get; set; }
        public double TotalLengthMeters => TotalOriginalLengthMm / 1000.0;
    }
}
