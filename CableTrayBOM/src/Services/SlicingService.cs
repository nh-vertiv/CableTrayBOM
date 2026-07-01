using System;
using System.Collections.Generic;
using System.Linq;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Slices cable tray segments into standard-length pieces for BOM ordering.
    ///
    /// LADDER / PERFORATED / NON-PERFORATED / FIBER trays (existing logic, unchanged):
    ///   - Fittings (IsFitting = true, IsMeshChannel = false) are never sliced.
    ///   - Straight segments are divided into fullPieces × sliceLength + remainder.
    ///   - Remainder is kept as-is (no rounding).
    ///
    /// MESH CHANNEL trays (_Channel suffix, IsMeshChannel = true):
    ///   - The tray is factory-bent, not cut at direction changes.
    ///   - Both straight CableTray segments AND CableTrayFitting bends/offsets
    ///     with _Channel are treated as orderable stock pieces.
    ///   - Fittings (IsFitting = true AND IsMeshChannel = true) ARE sliced
    ///     using the same logic as straights — they contribute their bounding-box
    ///     length as stock pieces.
    ///   - Remainder piece length is rounded UP to the nearest
    ///     MeshChannelLengthIncrementMm (default 100 mm).
    ///     e.g. remainder 1450 mm → ordered as 1500 mm (cut from a 3000 mm stock).
    ///   - Full 3000 mm pieces are already multiples of 100 mm — no rounding needed.
    ///   - Couplings (ConnectionCount-driven hardware) exist only at piece-to-piece
    ///     joints. Non-_Channel fittings at run ends are NOT counted.
    /// </summary>
    public class SlicingService
    {
        private readonly BOMSettings _settings;

        public SlicingService(BOMSettings settings)
        {
            _settings = settings;
        }

        /// <summary>
        /// Slice a single cable tray segment into standard-length pieces.
        /// </summary>
        public void SliceSegment(CableTraySegment segment, double? customSliceLength = null)
        {
            double sliceLength = customSliceLength ?? _settings.DefaultSliceLength;
            segment.SlicedPieces.Clear();

            // Non-mesh fittings (ladder bends, tees, etc.) are NEVER sliced.
            if (segment.IsFitting && !segment.IsMeshChannel)
            {
                segment.SlicedPieces.Add(new SlicedPiece
                {
                    Length = segment.OriginalLength,
                    LengthWithCoupling = segment.OriginalLength,
                    IsFullLength = false,
                    OrderPieces = 1
                });
                return;
            }

            double totalLength = segment.OriginalLength;
            if (totalLength <= 0)
                return;

            int fullPieces = (int)Math.Floor(totalLength / sliceLength);
            double remainder = totalLength - (fullPieces * sliceLength);

            // For mesh channel segments, round remainder UP to nearest 100 mm increment.
            // Full pieces are already exact multiples of the increment — no change needed.
            if (segment.IsMeshChannel && remainder > _settings.CouplingGap)
            {
                double inc = _settings.MeshChannelLengthIncrementMm;
                if (inc > 1)
                    remainder = Math.Ceiling(remainder / inc) * inc;
            }

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

            // Add remainder piece if any meaningful length remains
            if (remainder > _settings.CouplingGap)
            {
                segment.SlicedPieces.Add(new SlicedPiece
                {
                    Length = remainder,
                    LengthWithCoupling = remainder + _settings.CouplingGap,
                    IsFullLength = false,
                    // A partial piece always requires ordering 1 full standard-length piece
                    OrderPieces = _settings.RoundUpOrderQuantity ? 1 : 0
                });
            }
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

            // Calculate order pieces from AGGREGATE total length per type/size.
            // For mesh channel, also apply the 100 mm increment rounding to the
            // aggregate remainder — consistent with per-segment rounding.
            foreach (var kvp in orderMap)
            {
                var summary = kvp.Value;
                double totalLen = summary.TotalOriginalLengthMm;
                int fullPieces = (int)Math.Floor(totalLen / sliceLength);
                double remainder = totalLen - (fullPieces * sliceLength);

                // Determine if all segments in this key are mesh channel.
                // We re-inspect the segments to check IsMeshChannel on the key group.
                bool isMeshChannelGroup = segments
                    .Where(s => $"{s.TrayType}|{s.Size}|{s.PartNumber}|{s.ServiceType}" == kvp.Key)
                    .All(s => s.IsMeshChannel);

                if (isMeshChannelGroup && remainder > 0)
                {
                    double inc = _settings.MeshChannelLengthIncrementMm;
                    if (inc > 1)
                        remainder = Math.Ceiling(remainder / inc) * inc;
                }

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
