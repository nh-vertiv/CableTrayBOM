using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using CableTrayBOM.Models;

namespace CableTrayBOM.Services
{
    /// <summary>
    /// Two detection paths depending on the suspension family type:
    ///
    /// PATH A — Full suspension assembly (has sub-components like rods, brackets):
    ///   1. SAT slab test (10mm OBB on top/bottom of tray vs suspension AABB)
    ///   2. Insertion point perpendicular check (location must be within tray width + 50mm)
    ///   Both must pass. This prevents large suspension AABBs from matching adjacent trays.
    ///
    /// PATH B — Standalone channel (no sub-components, parentId == selfId):
    ///   1. SAT slab test only — sufficient because the channel AABB is small and precise.
    ///   No perpendicular check (channel insertion point may be at one end, not over the tray).
    ///
    /// Parent-based dedup: one physical suspension = one count.
    /// </summary>
    public class SupportDetectionEngine
    {
        private readonly Document _doc;
        private const double SlabThicknessMm = 10.0;
        private const double PerpToleranceMm = 50.0;
        private const double EndExtensionMm = 50.0;
        // How far each detection slab reaches AWAY from the tray face. Cradle-style
        // brackets often sit a few mm below the tray bottom (or proud above the top),
        // so the thin 10mm band flush with the face could miss them. This reach lets
        // the band span from the face outward by this distance, catching near-contact
        // supports without growing so large it grabs unrelated geometry.
        private const double SlabReachMm = 60.0;

        private List<SuspensionData> _suspensions = new();
        // Spatial grid for fast lookup - cells of ~2m (6.5ft)
        private Dictionary<(int, int), List<SuspensionData>>? _grid;
        private const double GridCellSize = 6.5; // feet (~2m)

        public SupportDetectionEngine(Document doc) { _doc = doc; }
        public int SupportCount => _suspensions.Count;

        public void CollectSupports()
        {
            _suspensions.Clear();
            _grid = new Dictionary<(int, int), List<SuspensionData>>();

            foreach (var elem in new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_SpecialityEquipment)
                .WhereElementIsNotElementType())
            {
                string familyName = "", typeName = "";
                if (elem is FamilyInstance fi)
                {
                    familyName = fi.Symbol?.Family?.Name ?? "";
                    typeName = fi.Symbol?.Name ?? "";
                }

                string combined = (familyName + " " + typeName).ToLowerInvariant();
                if (!combined.Contains("support") && !combined.Contains("suspension"))
                    continue;

                var bb = elem.get_BoundingBox(null);
                if (bb == null) continue;

                XYZ? location = null;
                if (elem is FamilyInstance fi2)
                {
                    if (fi2.Location is LocationPoint lp) location = lp.Point;
                    else if (fi2.Location is LocationCurve lc) location = lc.Curve.Evaluate(0.5, true);
                }
                if (location == null) location = (bb.Min + bb.Max) / 2.0;

                ElementId parentId = elem.Id;
                bool isSubComponent = false;
                if (elem is FamilyInstance fi3)
                {
                    var current = fi3;
                    while (current.SuperComponent is FamilyInstance super)
                    { current = super; parentId = current.Id; isSubComponent = true; }
                }

                bool isFullAssembly = false;
                if (!isSubComponent && elem is FamilyInstance fi4)
                {
                    var subIds = fi4.GetSubComponentIds();
                    isFullAssembly = subIds != null && subIds.Count > 0;
                }

                var data = new SuspensionData
                {
                    ElementId = elem.Id, ParentId = parentId, FamilyName = familyName,
                    Location = location, BbMin = bb.Min, BbMax = bb.Max,
                    IsSubComponent = isSubComponent, IsFullAssembly = isFullAssembly,
                };
                _suspensions.Add(data);

                // Insert into spatial grid
                int minGx = (int)Math.Floor(bb.Min.X / GridCellSize);
                int maxGx = (int)Math.Floor(bb.Max.X / GridCellSize);
                int minGy = (int)Math.Floor(bb.Min.Y / GridCellSize);
                int maxGy = (int)Math.Floor(bb.Max.Y / GridCellSize);
                for (int gx = minGx; gx <= maxGx; gx++)
                    for (int gy = minGy; gy <= maxGy; gy++)
                    {
                        var key = (gx, gy);
                        if (!_grid.ContainsKey(key)) _grid[key] = new List<SuspensionData>();
                        _grid[key].Add(data);
                    }
            }

            var fullAssemblyParents = new HashSet<ElementId>(
                _suspensions.Where(s => s.IsFullAssembly).Select(s => s.ElementId));
            foreach (var s in _suspensions)
                if (fullAssemblyParents.Contains(s.ParentId)) s.ParentIsFullAssembly = true;
        }

        /// <summary>
        /// Get suspensions near a bounding box using spatial grid - much faster than iterating all.
        /// </summary>
        private IEnumerable<SuspensionData> GetNearbySuspensions(XYZ bbMin, XYZ bbMax)
        {
            if (_grid == null) yield break;
            int minGx = (int)Math.Floor(bbMin.X / GridCellSize);
            int maxGx = (int)Math.Floor(bbMax.X / GridCellSize);
            int minGy = (int)Math.Floor(bbMin.Y / GridCellSize);
            int maxGy = (int)Math.Floor(bbMax.Y / GridCellSize);

            var seen = new HashSet<ElementId>();
            for (int gx = minGx; gx <= maxGx; gx++)
                for (int gy = minGy; gy <= maxGy; gy++)
                {
                    if (_grid.TryGetValue((gx, gy), out var list))
                        foreach (var s in list)
                            if (seen.Add(s.ElementId)) yield return s;
                }
        }

        public int CountSupportsForElement(Element trayElement)
        {
            XYZ? trayStart = null, trayEnd = null;
            double halfWidth = 0, halfHeight = 0;

            if (trayElement is CableTray ct)
            {
                var curve = (ct.Location as LocationCurve)?.Curve;
                if (curve == null) return 0;
                trayStart = curve.GetEndPoint(0);
                trayEnd = curve.GetEndPoint(1);
                halfWidth = ct.Width / 2.0;
                halfHeight = ct.Height / 2.0;
            }
            else if (trayElement is FamilyInstance fi)
            {
                var conns = fi.MEPModel?.ConnectorManager?.Connectors;
                if (conns != null)
                {
                    var cl = conns.Cast<Connector>().ToList();
                    if (cl.Count >= 2)
                    { trayStart = cl[0].Origin; trayEnd = cl[1].Origin; halfWidth = Math.Max(cl[0].Width, cl[1].Width) / 2.0; halfHeight = Math.Max(cl[0].Height, cl[1].Height) / 2.0; }
                    else
                    {
                        var bb = fi.get_BoundingBox(null);
                        if (bb == null) return 0;
                        trayStart = bb.Min; trayEnd = bb.Max;
                        if (cl.Count == 1) { halfWidth = cl[0].Width / 2.0; halfHeight = cl[0].Height / 2.0; }
                    }
                }
                else return 0;
            }
            else return 0;

            if (trayStart == null || trayEnd == null) return 0;
            XYZ dir = trayEnd - trayStart;
            double trayLen = dir.GetLength();
            if (trayLen < 0.001) return 0;
            XYZ xAxis = dir.Normalize();

            var slabs = BuildSlabs(trayStart, trayEnd, xAxis, halfWidth, halfHeight, trayLen);
            if (slabs.Count == 0) return 0;

            double perpTol = MmToFt(PerpToleranceMm);
            double endExt = MmToFt(EndExtensionMm);
            double maxPerp = halfWidth + perpTol;
            XYZ startXY = new XYZ(trayStart.X, trayStart.Y, 0);
            XYZ endXY = new XYZ(trayEnd.X, trayEnd.Y, 0);
            XYZ lineDirXY = endXY - startXY;
            double lineLenXY = lineDirXY.GetLength();
            XYZ lineDirN = lineLenXY > 0.001 ? lineDirXY.Normalize() : XYZ.BasisX;

            // Use spatial grid instead of iterating ALL suspensions
            var trayBb = trayElement.get_BoundingBox(null);
            XYZ searchMin, searchMax;
            if (trayBb != null)
            {
                double ext = MmToFt(500); // 500mm search extension
                searchMin = new XYZ(trayBb.Min.X - ext, trayBb.Min.Y - ext, trayBb.Min.Z - ext);
                searchMax = new XYZ(trayBb.Max.X + ext, trayBb.Max.Y + ext, trayBb.Max.Z + ext);
            }
            else
            {
                searchMin = new XYZ(Math.Min(trayStart.X, trayEnd.X) - 3, Math.Min(trayStart.Y, trayEnd.Y) - 3, Math.Min(trayStart.Z, trayEnd.Z) - 3);
                searchMax = new XYZ(Math.Max(trayStart.X, trayEnd.X) + 3, Math.Max(trayStart.Y, trayEnd.Y) + 3, Math.Max(trayStart.Z, trayEnd.Z) + 3);
            }

            var matchedParents = new HashSet<ElementId>();

            foreach (var susp in GetNearbySuspensions(searchMin, searchMax))
            {
                if (matchedParents.Contains(susp.ParentId)) continue;

                bool slabHit = false;
                foreach (var slab in slabs)
                {
                    if (ObbVsAabb(slab, susp.BbMin, susp.BbMax))
                    { slabHit = true; break; }
                }
                if (!slabHit) continue;

                if (susp.ParentIsFullAssembly)
                {
                    XYZ suspXY = new XYZ(susp.Location.X, susp.Location.Y, 0);
                    double t = (suspXY - startXY).DotProduct(lineDirN);
                    if (t < -endExt || t > lineLenXY + endExt) continue;
                    XYZ proj = startXY + lineDirN * Math.Max(0, Math.Min(t, lineLenXY));
                    double perpDist = suspXY.DistanceTo(proj);
                    // Accept if the insertion point is near OR the centerline crosses the
                    // suspension footprint (handles offset-origin rod assemblies).
                    bool pointNear = perpDist <= maxPerp;
                    bool centerlineThroughFootprint = CenterlineCrossesFootprint(
                        startXY, lineDirN, lineLenXY, susp.BbMin, susp.BbMax, perpTol);
                    if (!pointNear && !centerlineThroughFootprint) continue;
                }

                matchedParents.Add(susp.ParentId);
            }

            return matchedParents.Count;
        }

        /// <summary>
        /// Like CountSupportsForElement, but returns each matched physical suspension
        /// (keyed by ParentId) together with the perpendicular distance from the
        /// suspension's insertion point to THIS element's centerline. Used by
        /// DetectAllSupports to assign each suspension to exactly one tray piece
        /// (the closest one), so counts never double up across cut pieces.
        /// </summary>
        public Dictionary<ElementId, double> MatchSupportsForElement(Element trayElement)
        {
            var matches = new Dictionary<ElementId, double>();

            XYZ? trayStart = null, trayEnd = null;
            double halfWidth = 0, halfHeight = 0;

            if (trayElement is CableTray ct)
            {
                var curve = (ct.Location as LocationCurve)?.Curve;
                if (curve == null) return matches;
                trayStart = curve.GetEndPoint(0);
                trayEnd = curve.GetEndPoint(1);
                halfWidth = ct.Width / 2.0;
                halfHeight = ct.Height / 2.0;
            }
            else if (trayElement is FamilyInstance fi)
            {
                var conns = fi.MEPModel?.ConnectorManager?.Connectors;
                if (conns != null)
                {
                    var cl = conns.Cast<Connector>().ToList();
                    if (cl.Count >= 2)
                    { trayStart = cl[0].Origin; trayEnd = cl[1].Origin; halfWidth = Math.Max(cl[0].Width, cl[1].Width) / 2.0; halfHeight = Math.Max(cl[0].Height, cl[1].Height) / 2.0; }
                    else
                    {
                        var bb = fi.get_BoundingBox(null);
                        if (bb == null) return matches;
                        trayStart = bb.Min; trayEnd = bb.Max;
                        if (cl.Count == 1) { halfWidth = cl[0].Width / 2.0; halfHeight = cl[0].Height / 2.0; }
                    }
                }
                else return matches;
            }
            else return matches;

            if (trayStart == null || trayEnd == null) return matches;
            XYZ dir = trayEnd - trayStart;
            double trayLen = dir.GetLength();
            if (trayLen < 0.001) return matches;
            XYZ xAxis = dir.Normalize();

            var slabs = BuildSlabs(trayStart, trayEnd, xAxis, halfWidth, halfHeight, trayLen);
            if (slabs.Count == 0) return matches;

            double perpTol = MmToFt(PerpToleranceMm);
            double endExt = MmToFt(EndExtensionMm);
            double maxPerp = halfWidth + perpTol;
            XYZ startXY = new XYZ(trayStart.X, trayStart.Y, 0);
            XYZ endXY = new XYZ(trayEnd.X, trayEnd.Y, 0);
            XYZ lineDirXY = endXY - startXY;
            double lineLenXY = lineDirXY.GetLength();
            XYZ lineDirN = lineLenXY > 0.001 ? lineDirXY.Normalize() : XYZ.BasisX;

            var trayBb = trayElement.get_BoundingBox(null);
            XYZ searchMin, searchMax;
            if (trayBb != null)
            {
                double ext = MmToFt(500);
                searchMin = new XYZ(trayBb.Min.X - ext, trayBb.Min.Y - ext, trayBb.Min.Z - ext);
                searchMax = new XYZ(trayBb.Max.X + ext, trayBb.Max.Y + ext, trayBb.Max.Z + ext);
            }
            else
            {
                searchMin = new XYZ(Math.Min(trayStart.X, trayEnd.X) - 3, Math.Min(trayStart.Y, trayEnd.Y) - 3, Math.Min(trayStart.Z, trayEnd.Z) - 3);
                searchMax = new XYZ(Math.Max(trayStart.X, trayEnd.X) + 3, Math.Max(trayStart.Y, trayEnd.Y) + 3, Math.Max(trayStart.Z, trayEnd.Z) + 3);
            }

            foreach (var susp in GetNearbySuspensions(searchMin, searchMax))
            {
                bool slabHit = false;
                foreach (var slab in slabs)
                {
                    if (ObbVsAabb(slab, susp.BbMin, susp.BbMax))
                    { slabHit = true; break; }
                }
                if (!slabHit) continue;

                // Perpendicular distance from the suspension insertion point to this
                // piece's centerline (also used as the tie-breaker for assignment).
                XYZ suspXY = new XYZ(susp.Location.X, susp.Location.Y, 0);
                double t = (suspXY - startXY).DotProduct(lineDirN);
                XYZ proj = startXY + lineDirN * Math.Max(0, Math.Min(t, lineLenXY));
                double perpDist = suspXY.DistanceTo(proj);

                if (susp.ParentIsFullAssembly)
                {
                    // Along-axis: the suspension must sit within the run (plus a small end margin).
                    if (t < -endExt || t > lineLenXY + endExt) continue;

                    // Perpendicular: accept when EITHER
                    //   (a) the insertion point is within maxPerp of the centerline, OR
                    //   (b) the tray centerline passes through the suspension's plan footprint
                    //       (AABB in XY, with a small margin).
                    // A rod-hung assembly's origin is often offset to one side (rod drop /
                    // bracket corner), so the point-only test (a) wrongly rejected real
                    // supports. Test (b) still rejects a PARALLEL adjacent tray, whose
                    // centerline does not pass through this suspension's footprint, so the
                    // original adjacent-tray guard is preserved.
                    bool pointNear = perpDist <= maxPerp;
                    bool centerlineThroughFootprint = CenterlineCrossesFootprint(
                        startXY, lineDirN, lineLenXY, susp.BbMin, susp.BbMax, perpTol);
                    if (!pointNear && !centerlineThroughFootprint) continue;
                }

                // Keep the smallest perp distance per physical suspension on THIS piece.
                if (!matches.TryGetValue(susp.ParentId, out var existing) || perpDist < existing)
                    matches[susp.ParentId] = perpDist;
            }

            return matches;
        }

        /// <summary>
        /// True if the tray centerline (in plan) passes through the suspension's XY
        /// bounding-box footprint, expanded by <paramref name="marginFt"/>. Sampled
        /// along the segment — cheap and robust for the axis-aligned footprints here.
        /// </summary>
        private static bool CenterlineCrossesFootprint(
            XYZ startXY, XYZ dirN, double lineLenXY, XYZ bbMin, XYZ bbMax, double marginFt)
        {
            double minX = bbMin.X - marginFt, maxX = bbMax.X + marginFt;
            double minY = bbMin.Y - marginFt, maxY = bbMax.Y + marginFt;

            // Sample the centerline; step ~ the smaller footprint dimension (min 50mm),
            // so we cannot step over a small footprint.
            double footMin = Math.Min(maxX - minX, maxY - minY);
            double step = Math.Max(MmToFt(50), footMin * 0.5);
            if (step < 1e-6) step = MmToFt(50);

            for (double s = 0; s <= lineLenXY + 1e-9; s += step)
            {
                XYZ p = startXY + dirN * s;
                if (p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY)
                    return true;
            }
            // Also test the exact endpoint (loop may stop just short).
            XYZ pe = startXY + dirN * lineLenXY;
            return pe.X >= minX && pe.X <= maxX && pe.Y >= minY && pe.Y <= maxY;
        }

        private List<OrientedBox> BuildSlabs(XYZ start, XYZ end, XYZ xAxis,
            double halfWidth, double halfHeight, double length)
        {
            var result = new List<OrientedBox>();

            // Each slab straddles a tray face: it reaches a small distance INTO the tray
            // and SlabReach OUTWARD past the face. This catches supports that touch or
            // nearly touch the face (cradle brackets sitting a few mm clear) which a thin
            // band flush with the face would miss.
            double inwardBite = MmToFt(SlabThicknessMm);     // reach a little inside the face
            double outwardReach = MmToFt(SlabReachMm);        // reach this far past the face
            double bandHalf = (inwardBite + outwardReach) / 2.0;
            double bandMid = (outwardReach - inwardBite) / 2.0; // center offset from the face, outward

            double dotVert = Math.Abs(xAxis.DotProduct(XYZ.BasisZ));
            XYZ yAxis, zAxis;
            if (dotVert > 0.95)
            {
                yAxis = XYZ.BasisX.CrossProduct(xAxis).Normalize();
                if (yAxis.GetLength() < 0.5)
                    yAxis = XYZ.BasisY.CrossProduct(xAxis).Normalize();
                zAxis = xAxis.CrossProduct(yAxis).Normalize();
            }
            else
            {
                yAxis = XYZ.BasisZ.CrossProduct(xAxis).Normalize();
                zAxis = xAxis.CrossProduct(yAxis).Normalize();
            }

            XYZ center = (start + end) / 2.0;
            // Extend the slab a little past each end of the piece so a support channel
            // sitting right at a cut seam (or at the run's extreme end) is still captured.
            // Without this the slab stopped exactly at the piece end and a channel at the
            // boundary fell just outside it. Global one-to-one assignment in
            // DetectAllSupports still credits such a channel to exactly one piece, so the
            // extended overlap does not reintroduce double counting.
            double halfLen = length / 2.0 + MmToFt(EndExtensionMm);

            // Top slab: face at +halfHeight, band centered just outside it.
            result.Add(new OrientedBox
            {
                Center = center + zAxis * (halfHeight + bandMid),
                XAxis = xAxis, YAxis = yAxis, ZAxis = zAxis,
                HalfX = halfLen, HalfY = halfWidth, HalfZ = bandHalf
            });
            // Bottom slab: face at -halfHeight, band centered just outside it.
            result.Add(new OrientedBox
            {
                Center = center - zAxis * (halfHeight + bandMid),
                XAxis = xAxis, YAxis = yAxis, ZAxis = zAxis,
                HalfX = halfLen, HalfY = halfWidth, HalfZ = bandHalf
            });
            return result;
        }

        private static bool ObbVsAabb(OrientedBox obb, XYZ aMin, XYZ aMax)
        {
            XYZ aC = (aMin + aMax) / 2.0;
            XYZ aH = (aMax - aMin) / 2.0;
            XYZ[] aA = { XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ };
            double[] ah = { aH.X, aH.Y, aH.Z };
            XYZ[] bA = { obb.XAxis, obb.YAxis, obb.ZAxis };
            double[] bh = { obb.HalfX, obb.HalfY, obb.HalfZ };
            XYZ T = obb.Center - aC;
            double[,] R = new double[3,3], AR = new double[3,3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                { R[i,j] = aA[i].DotProduct(bA[j]); AR[i,j] = Math.Abs(R[i,j]) + 1e-10; }
            double[] t = { T.DotProduct(aA[0]), T.DotProduct(aA[1]), T.DotProduct(aA[2]) };
            for (int i = 0; i < 3; i++)
            { double ra=ah[i],rb=bh[0]*AR[i,0]+bh[1]*AR[i,1]+bh[2]*AR[i,2]; if(Math.Abs(t[i])>ra+rb) return false; }
            for (int j = 0; j < 3; j++)
            { double ra=ah[0]*AR[0,j]+ah[1]*AR[1,j]+ah[2]*AR[2,j],rb=bh[j]; double tp=t[0]*R[0,j]+t[1]*R[1,j]+t[2]*R[2,j]; if(Math.Abs(tp)>ra+rb) return false; }
            { double ra=ah[1]*AR[2,0]+ah[2]*AR[1,0],rb=bh[1]*AR[0,2]+bh[2]*AR[0,1]; if(Math.Abs(t[2]*R[1,0]-t[1]*R[2,0])>ra+rb) return false; }
            { double ra=ah[1]*AR[2,1]+ah[2]*AR[1,1],rb=bh[0]*AR[0,2]+bh[2]*AR[0,0]; if(Math.Abs(t[2]*R[1,1]-t[1]*R[2,1])>ra+rb) return false; }
            { double ra=ah[1]*AR[2,2]+ah[2]*AR[1,2],rb=bh[0]*AR[0,1]+bh[1]*AR[0,0]; if(Math.Abs(t[2]*R[1,2]-t[1]*R[2,2])>ra+rb) return false; }
            { double ra=ah[0]*AR[2,0]+ah[2]*AR[0,0],rb=bh[1]*AR[1,2]+bh[2]*AR[1,1]; if(Math.Abs(t[0]*R[2,0]-t[2]*R[0,0])>ra+rb) return false; }
            { double ra=ah[0]*AR[2,1]+ah[2]*AR[0,1],rb=bh[0]*AR[1,2]+bh[2]*AR[1,0]; if(Math.Abs(t[0]*R[2,1]-t[2]*R[0,1])>ra+rb) return false; }
            { double ra=ah[0]*AR[2,2]+ah[2]*AR[0,2],rb=bh[0]*AR[1,1]+bh[1]*AR[1,0]; if(Math.Abs(t[0]*R[2,2]-t[2]*R[0,2])>ra+rb) return false; }
            { double ra=ah[0]*AR[1,0]+ah[1]*AR[0,0],rb=bh[1]*AR[2,2]+bh[2]*AR[2,1]; if(Math.Abs(t[1]*R[0,0]-t[0]*R[1,0])>ra+rb) return false; }
            { double ra=ah[0]*AR[1,1]+ah[1]*AR[0,1],rb=bh[0]*AR[2,2]+bh[2]*AR[2,0]; if(Math.Abs(t[1]*R[0,1]-t[0]*R[1,1])>ra+rb) return false; }
            { double ra=ah[0]*AR[1,2]+ah[1]*AR[0,2],rb=bh[0]*AR[2,1]+bh[1]*AR[2,0]; if(Math.Abs(t[1]*R[0,2]-t[0]*R[1,2])>ra+rb) return false; }
            return true;
        }

        public void DetectAllSupports(List<CableTraySegment> segments)
        {
            if (_suspensions.Count == 0) CollectSupports();

            // Pass 1: gather, for every segment, the physical suspensions that match it
            // and how far each sits from that segment's centerline.
            var segMatches = new Dictionary<string, Dictionary<ElementId, double>>();
            foreach (var seg in segments)
            {
                seg.SupportCount = 0; // reset; assigned in pass 3
                var elem = _doc.GetElement(RevitCompat.ToElementId(seg.ElementId));
                if (elem == null) continue;
                segMatches[seg.ElementId] = MatchSupportsForElement(elem);
            }

            // Pass 2: assign each physical suspension (by ParentId) to the SINGLE segment
            // whose centerline it is closest to. A suspension near a cut point — or whose
            // bounding box straddles two adjoining pieces — would otherwise be counted by
            // every piece it touches (the V_Count = 6-instead-of-3 bug). One suspension =
            // one piece = one count, so per-piece counts sum to the true physical total.
            var bestSegForSusp = new Dictionary<ElementId, (string segId, double perp)>();
            foreach (var kvp in segMatches)
            {
                string segId = kvp.Key;
                foreach (var m in kvp.Value)
                {
                    ElementId suspParent = m.Key;
                    double perp = m.Value;
                    if (!bestSegForSusp.TryGetValue(suspParent, out var cur) || perp < cur.perp)
                        bestSegForSusp[suspParent] = (segId, perp);
                }
            }

            // Pass 3: tally per segment from the unique assignments.
            var countBySeg = new Dictionary<string, int>();
            foreach (var assign in bestSegForSusp.Values)
                countBySeg[assign.segId] = countBySeg.TryGetValue(assign.segId, out var c) ? c + 1 : 1;

            foreach (var seg in segments)
                seg.SupportCount = countBySeg.TryGetValue(seg.ElementId, out var c) ? c : 0;
        }

        public string GetDiagnosticInfo(List<CableTraySegment> segments)
        {
            var sb = new System.Text.StringBuilder();
            var parents = _suspensions.GroupBy(s => s.ParentId).ToList();
            var fullAssemblies = parents.Count(g => g.Any(s => s.ParentIsFullAssembly));
            var standalones = parents.Count - fullAssemblies;
            sb.AppendLine($"Entries: {_suspensions.Count}, Physical: {parents.Count}");
            sb.AppendLine($"Full assemblies (perp check): {fullAssemblies}");
            sb.AppendLine($"Standalone channels (slab only): {standalones}");
            sb.AppendLine();
            foreach (var g in parents.Take(30))
            {
                var f = g.First();
                string path = f.ParentIsFullAssembly ? "PATH-A(perp)" : "PATH-B(slab)";
                sb.AppendLine($"  Parent {g.Key}: {g.Count()} parts, \"{f.FamilyName}\" {path}");
            }
            sb.AppendLine();
            foreach (var seg in segments.Take(60))
                sb.AppendLine($"  {seg.ElementId}: Sup={seg.SupportCount} Conn={seg.ConnectionCount} {seg.Size}");
            return sb.ToString();
        }

        private static double MmToFt(double mm) =>
            UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
    }

    internal class OrientedBox
    {
        public XYZ Center { get; set; } = XYZ.Zero;
        public XYZ XAxis { get; set; } = XYZ.BasisX;
        public XYZ YAxis { get; set; } = XYZ.BasisY;
        public XYZ ZAxis { get; set; } = XYZ.BasisZ;
        public double HalfX, HalfY, HalfZ;
    }

    internal class SuspensionData
    {
        public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
        public ElementId ParentId { get; set; } = ElementId.InvalidElementId;
        public string FamilyName { get; set; } = "";
        public XYZ Location { get; set; } = XYZ.Zero;
        public XYZ BbMin { get; set; } = XYZ.Zero;
        public XYZ BbMax { get; set; } = XYZ.Zero;
        public bool IsSubComponent { get; set; }
        public bool IsFullAssembly { get; set; }
        public bool ParentIsFullAssembly { get; set; }
    }
}
