using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;
using OpenCvSharp;

namespace Ocr.Core.Services;

public sealed class OpenCvRegionDetector : IRegionDetector
{
    private const int MinControlSizePx = 10;
    private const int MaxControlSizePx = 28;
    private const double MinAspectRatio = 0.75;
    private const double MaxAspectRatio = 1.33;
    private const int RightSideLabelDistancePx = 110;
    private const double CheckedFillThreshold = 0.15;

    public RegionDetectionResult Detect(PageInfo page, Mat pageImage)
    {
        if (pageImage.Empty())
        {
            return new RegionDetectionResult();
        }

        using var gray = new Mat();
        if (pageImage.Channels() == 1)
        {
            pageImage.CopyTo(gray);
        }
        else
        {
            Cv2.CvtColor(pageImage, gray, ColorConversionCodes.BGR2GRAY);
        }

        using var binary = new Mat();
        Cv2.AdaptiveThreshold(gray, binary, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 31, 8);
        using var cleaned = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(binary, cleaned, MorphTypes.Close, kernel);

        var rawCandidates = BuildRawCandidates(page, gray, cleaned);
        var geometryFiltered = FilterByGeometry(page, rawCandidates);
        var labelFiltered = FilterByLabelAssociation(page, geometryFiltered);
        var deduped = Deduplicate(labelFiltered)
            .OrderBy(r => r.Bbox.Y)
            .ThenBy(r => r.Bbox.X)
            .ThenBy(r => r.Type, StringComparer.Ordinal)
            .ToList();

        var regions = new List<RegionInfo>(deduped.Count);
        var overlays = new List<RegionOverlayInfo>(deduped.Count);
        var checkboxIndex = 1;
        var radioIndex = 1;

        foreach (var candidate in deduped)
        {
            var prefix = candidate.Type == "radio" ? "rad" : "chk";
            var index = candidate.Type == "radio" ? radioIndex++ : checkboxIndex++;
            regions.Add(new RegionInfo
            {
                RegionId = $"{prefix}-{page.PageIndex:000}-{index:0000}",
                Type = candidate.Type,
                Bbox = candidate.Bbox,
                Confidence = Math.Clamp(candidate.Confidence, 0, 1),
                Value = candidate.Value,
                LabelTokenIds = candidate.LabelTokenIds,
                Notes = candidate.Notes
            });

            overlays.Add(new RegionOverlayInfo
            {
                Type = candidate.Type,
                Value = candidate.Value,
                Bbox = candidate.Bbox
            });
        }

        return new RegionDetectionResult
        {
            Regions = regions,
            Overlays = overlays,
            Diagnostics = new RegionDetectionDiagnostics
            {
                RawCandidateCount = rawCandidates.Count,
                GeometryFilteredCount = geometryFiltered.Count,
                LabelFilteredCount = labelFiltered.Count,
                FinalCheckboxCount = regions.Count(r => string.Equals(r.Type, "checkbox", StringComparison.OrdinalIgnoreCase)),
                FinalRadioCount = regions.Count(r => string.Equals(r.Type, "radio", StringComparison.OrdinalIgnoreCase))
            }
        };
    }

    private static List<RegionCandidate> BuildRawCandidates(PageInfo page, Mat gray, Mat binary)
    {
        var raw = new List<RegionCandidate>();

        // Raw checkbox and circular contour candidates.
        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (!IsSizeInRange(rect))
            {
                continue;
            }

            var bbox = ToBbox(rect);
            var area = Cv2.ContourArea(contour);
            var perimeter = Cv2.ArcLength(contour, true);
            if (area <= 0 || perimeter <= 0)
            {
                continue;
            }

            var aspect = rect.Height == 0 ? 0 : rect.Width / (double)rect.Height;
            var approx = Cv2.ApproxPolyDP(contour, perimeter * 0.05, true);
            var corners = approx.Length;
            var contourFillRatio = area / Math.Max(1.0, rect.Width * rect.Height);
            var borderRatio = CalculateBorderRatio(binary, rect);
            var interiorDark = CalculateInteriorDarkRatio(gray, rect);
            var interiorInk = CalculateInteriorInkRatio(binary, rect);
            var centerDark = CalculateCenterDarkRatio(gray, rect);
            var circularity = Math.Clamp((4.0 * Math.PI * area) / (perimeter * perimeter), 0, 1);

            // Checkbox raw candidate.
            raw.Add(new RegionCandidate
            {
                Type = "checkbox",
                Bbox = bbox,
                Aspect = aspect,
                Corners = corners,
                Rectangularity = contourFillRatio,
                Circularity = circularity,
                BorderRatio = borderRatio,
                InteriorDarkRatio = interiorDark,
                InteriorInkRatio = interiorInk,
                CenterDarkRatio = centerDark,
                Value = Math.Max(interiorDark, interiorInk) > CheckedFillThreshold
            });

            // Radio contour candidate (separate scoring path).
            raw.Add(new RegionCandidate
            {
                Type = "radio",
                Bbox = bbox,
                Aspect = aspect,
                Corners = corners,
                Rectangularity = contourFillRatio,
                Circularity = circularity,
                BorderRatio = borderRatio,
                InteriorDarkRatio = interiorDark,
                InteriorInkRatio = interiorInk,
                CenterDarkRatio = centerDark,
                Value = Math.Max(interiorDark, centerDark) > CheckedFillThreshold
            });
        }

        // Hough radio candidates.
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 1.2);
        var circles = Cv2.HoughCircles(blurred, HoughModes.Gradient, 1.2, 10, 100, 14, 5, 20);
        foreach (var circle in circles)
        {
            var x = (int)Math.Round(circle.Center.X - circle.Radius);
            var y = (int)Math.Round(circle.Center.Y - circle.Radius);
            var d = (int)Math.Round(circle.Radius * 2);
            var rect = new Rect(x, y, d, d);
            if (!IsSizeInRange(rect))
            {
                continue;
            }

            var bbox = ToBbox(rect);
            var aspect = rect.Height == 0 ? 0 : rect.Width / (double)rect.Height;
            var borderRatio = CalculateBorderRatio(binary, rect);
            var interiorDark = CalculateInteriorDarkRatio(gray, rect);
            var centerDark = CalculateCenterDarkRatio(gray, rect);
            var circularity = EstimateCircularity(gray, rect);

            raw.Add(new RegionCandidate
            {
                Type = "radio",
                Bbox = bbox,
                Aspect = aspect,
                Corners = 0,
                Rectangularity = 0,
                Circularity = circularity,
                BorderRatio = borderRatio,
                InteriorDarkRatio = interiorDark,
                InteriorInkRatio = 0,
                CenterDarkRatio = centerDark,
                Value = Math.Max(interiorDark, centerDark) > CheckedFillThreshold,
                Notes = ["hough_candidate"]
            });
        }

        return raw;
    }

    private static List<RegionCandidate> FilterByGeometry(PageInfo page, List<RegionCandidate> raw)
    {
        var filtered = new List<RegionCandidate>();
        foreach (var candidate in raw)
        {
            if (!IsAspectInRange(candidate.Aspect))
            {
                continue;
            }

            if (IsInsideTextToken(page.Tokens, candidate.Bbox) || OverlapsTextToken(page.Tokens, candidate.Bbox, 0.35))
            {
                continue;
            }

            var size = (candidate.Bbox.W + candidate.Bbox.H) / 2.0;
            var sizeScore = Math.Clamp(1.0 - (Math.Abs(size - 16.0) / 16.0), 0, 1);
            var borderScore = Math.Clamp(candidate.BorderRatio / 0.22, 0, 1);
            var interiorScore = candidate.Type == "radio"
                ? Math.Clamp(1.0 - Math.Abs(candidate.CenterDarkRatio - (candidate.Value == true ? 0.3 : 0.05)), 0, 1)
                : Math.Clamp(1.0 - Math.Abs(candidate.InteriorDarkRatio - (candidate.Value == true ? 0.24 : 0.05)), 0, 1);

            if (candidate.Type == "checkbox")
            {
                if (candidate.Corners is < 4 or > 8)
                {
                    continue;
                }

                // Require a reasonably rectangular ring-like shape.
                if (candidate.Rectangularity is < 0.12 or > 0.85)
                {
                    continue;
                }

                if (candidate.BorderRatio is < 0.06 or > 0.55)
                {
                    continue;
                }

                if (candidate.InteriorDarkRatio > 0.78 && candidate.Rectangularity < 0.4)
                {
                    continue;
                }

                var shapeScore = Math.Clamp(1.0 - Math.Abs(candidate.Aspect - 1.0), 0, 1);
                candidate.Confidence = Math.Clamp((shapeScore * 0.34) + (sizeScore * 0.2) + (borderScore * 0.24) + (interiorScore * 0.22), 0, 1);
                if (candidate.Confidence < 0.58)
                {
                    continue;
                }
            }
            else
            {
                // Higher circularity for radios.
                if (candidate.Circularity < 0.78)
                {
                    continue;
                }

                if (candidate.BorderRatio is < 0.05 or > 0.5)
                {
                    continue;
                }

                if (candidate.InteriorDarkRatio > 0.88 && candidate.Circularity < 0.9)
                {
                    continue;
                }

                candidate.Confidence = Math.Clamp((candidate.Circularity * 0.44) + (sizeScore * 0.2) + (borderScore * 0.2) + (interiorScore * 0.16), 0, 1);
                if (candidate.Confidence < 0.62)
                {
                    continue;
                }

                candidate.Notes.Add("circularity_pass");
            }

            if (candidate.Value == true && candidate.Type == "radio")
            {
                candidate.Notes.Add("checked_by_fill_ratio");
            }

            filtered.Add(candidate);
        }

        return filtered;
    }

    private static List<RegionCandidate> FilterByLabelAssociation(PageInfo page, List<RegionCandidate> candidates)
    {
        var filtered = new List<RegionCandidate>();
        foreach (var candidate in candidates)
        {
            var labelTokenIds = FindLabelTokenIds(page, candidate.Bbox);
            if (labelTokenIds.Count == 0)
            {
                continue;
            }

            candidate.LabelTokenIds = labelTokenIds;
            candidate.Confidence = Math.Clamp(candidate.Confidence + 0.08, 0, 1);
            candidate.Notes.Add("strong_label_match");
            filtered.Add(candidate);
        }

        return filtered;
    }

    private static List<RegionCandidate> Deduplicate(List<RegionCandidate> candidates)
    {
        var result = new List<RegionCandidate>();
        foreach (var candidate in candidates
                     .OrderByDescending(c => c.Confidence)
                     .ThenBy(c => c.Bbox.Y)
                     .ThenBy(c => c.Bbox.X))
        {
            var duplicate = result.Any(existing =>
                (string.Equals(existing.Type, candidate.Type, StringComparison.OrdinalIgnoreCase) && IntersectionOverUnion(existing.Bbox, candidate.Bbox) > 0.45) ||
                (!string.Equals(existing.Type, candidate.Type, StringComparison.OrdinalIgnoreCase) && IntersectionOverUnion(existing.Bbox, candidate.Bbox) > 0.6));
            if (!duplicate)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    private static bool IsSizeInRange(Rect rect)
    {
        return rect.Width >= MinControlSizePx &&
               rect.Height >= MinControlSizePx &&
               rect.Width <= MaxControlSizePx &&
               rect.Height <= MaxControlSizePx;
    }

    private static bool IsAspectInRange(double aspect)
    {
        return aspect >= MinAspectRatio && aspect <= MaxAspectRatio;
    }

    private static double CalculateBorderRatio(Mat binary, Rect rect)
    {
        var x = Math.Max(0, rect.X);
        var y = Math.Max(0, rect.Y);
        var w = Math.Min(binary.Width - x, rect.Width);
        var h = Math.Min(binary.Height - y, rect.Height);
        if (w <= 4 || h <= 4)
        {
            return 0;
        }

        using var roi = new Mat(binary, new Rect(x, y, w, h));
        var outerDark = Cv2.CountNonZero(roi);
        using var inner = new Mat(roi, new Rect(2, 2, Math.Max(1, w - 4), Math.Max(1, h - 4)));
        var innerDark = Cv2.CountNonZero(inner);
        var borderDark = Math.Max(0, outerDark - innerDark);
        return borderDark / (double)(w * h);
    }

    private static double CalculateInteriorDarkRatio(Mat gray, Rect rect)
    {
        var x = Math.Max(0, rect.X + 2);
        var y = Math.Max(0, rect.Y + 2);
        var w = Math.Min(gray.Width - x, Math.Max(1, rect.Width - 4));
        var h = Math.Min(gray.Height - y, Math.Max(1, rect.Height - 4));
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        using var interior = new Mat(gray, new Rect(x, y, w, h));
        using var darkMask = new Mat();
        Cv2.Threshold(interior, darkMask, 150, 255, ThresholdTypes.BinaryInv);
        return Cv2.CountNonZero(darkMask) / (double)(w * h);
    }

    private static double CalculateInteriorInkRatio(Mat binary, Rect rect)
    {
        var x = Math.Max(0, rect.X + 2);
        var y = Math.Max(0, rect.Y + 2);
        var w = Math.Min(binary.Width - x, Math.Max(1, rect.Width - 4));
        var h = Math.Min(binary.Height - y, Math.Max(1, rect.Height - 4));
        if (w <= 0 || h <= 0)
        {
            return 0;
        }

        using var interior = new Mat(binary, new Rect(x, y, w, h));
        return Cv2.CountNonZero(interior) / (double)(w * h);
    }

    private static double CalculateCenterDarkRatio(Mat gray, Rect rect)
    {
        var centerW = Math.Max(1, (int)Math.Round(rect.Width * 0.45));
        var centerH = Math.Max(1, (int)Math.Round(rect.Height * 0.45));
        var centerX = rect.X + ((rect.Width - centerW) / 2);
        var centerY = rect.Y + ((rect.Height - centerH) / 2);
        centerX = Math.Max(0, centerX);
        centerY = Math.Max(0, centerY);
        centerW = Math.Min(gray.Width - centerX, centerW);
        centerH = Math.Min(gray.Height - centerY, centerH);
        if (centerW <= 0 || centerH <= 0)
        {
            return 0;
        }

        using var center = new Mat(gray, new Rect(centerX, centerY, centerW, centerH));
        using var darkMask = new Mat();
        Cv2.Threshold(center, darkMask, 150, 255, ThresholdTypes.BinaryInv);
        return Cv2.CountNonZero(darkMask) / (double)(centerW * centerH);
    }

    private static double EstimateCircularity(Mat gray, Rect rect)
    {
        var x = Math.Max(0, rect.X);
        var y = Math.Max(0, rect.Y);
        var w = Math.Min(gray.Width - x, rect.Width);
        var h = Math.Min(gray.Height - y, rect.Height);
        if (w <= 4 || h <= 4)
        {
            return 0;
        }

        using var roi = new Mat(gray, new Rect(x, y, w, h));
        using var roiBinary = new Mat();
        Cv2.Threshold(roi, roiBinary, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);
        Cv2.FindContours(roiBinary, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        if (contours.Length == 0)
        {
            return 0;
        }

        var contour = contours.OrderByDescending(c => Cv2.ContourArea(c)).First();
        var area = Cv2.ContourArea(contour);
        var perimeter = Cv2.ArcLength(contour, true);
        if (area <= 0 || perimeter <= 0)
        {
            return 0;
        }

        return Math.Clamp((4.0 * Math.PI * area) / (perimeter * perimeter), 0, 1);
    }

    private static bool OverlapsTextToken(IEnumerable<TokenInfo> tokens, BboxInfo bbox, double threshold)
    {
        return tokens.Any(t =>
            !string.IsNullOrWhiteSpace(t.Text) &&
            OverlapOverCandidate(t.Bbox, bbox) >= threshold);
    }

    private static bool IsInsideTextToken(IEnumerable<TokenInfo> tokens, BboxInfo bbox)
    {
        var centerX = bbox.X + (bbox.W / 2.0);
        var centerY = bbox.Y + (bbox.H / 2.0);
        return tokens.Any(t =>
            !string.IsNullOrWhiteSpace(t.Text) &&
            centerX >= t.Bbox.X &&
            centerX <= t.Bbox.X + t.Bbox.W &&
            centerY >= t.Bbox.Y &&
            centerY <= t.Bbox.Y + t.Bbox.H);
    }

    private static List<string> FindLabelTokenIds(PageInfo page, BboxInfo regionBbox)
    {
        var rightX = regionBbox.X + regionBbox.W;
        var centerY = regionBbox.Y + (regionBbox.H / 2.0);
        var tightYTolerance = Math.Max(10, regionBbox.H / 2.0 + 4);
        var expandedYTolerance = Math.Max(14, regionBbox.H + 4);

        var sameLineRight = page.Tokens
            .Where(t =>
                t.Bbox.X >= rightX - 2 &&
                t.Bbox.X <= rightX + RightSideLabelDistancePx &&
                Math.Abs((t.Bbox.Y + (t.Bbox.H / 2.0)) - centerY) <= tightYTolerance)
            .OrderBy(t => t.Bbox.X)
            .ThenBy(t => Math.Abs((t.Bbox.Y + (t.Bbox.H / 2.0)) - centerY))
            .ToList();

        if (sameLineRight.Count > 0)
        {
            var anchor = sameLineRight[0];
            var lineTokens = page.Tokens
                .Where(t =>
                    t.LineId == anchor.LineId &&
                    t.Bbox.X >= rightX - 2 &&
                    t.Bbox.X <= rightX + (RightSideLabelDistancePx + 20))
                .OrderBy(t => t.Bbox.X)
                .ThenBy(t => t.Bbox.Y)
                .Select(t => t.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (lineTokens.Count > 0)
            {
                return lineTokens;
            }
        }

        var fallback = page.Tokens
            .Where(t =>
                t.Bbox.X >= rightX - 2 &&
                t.Bbox.X <= rightX + RightSideLabelDistancePx &&
                Math.Abs((t.Bbox.Y + (t.Bbox.H / 2.0)) - centerY) <= expandedYTolerance)
            .OrderBy(t => t.Bbox.X - rightX)
            .ThenBy(t => Math.Abs((t.Bbox.Y + (t.Bbox.H / 2.0)) - centerY))
            .ToList();

        return fallback.Count == 0 ? [] : [fallback[0].Id];
    }

    private static double IntersectionOverUnion(BboxInfo a, BboxInfo b)
    {
        var interX1 = Math.Max(a.X, b.X);
        var interY1 = Math.Max(a.Y, b.Y);
        var interX2 = Math.Min(a.X + a.W, b.X + b.W);
        var interY2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var interW = Math.Max(0, interX2 - interX1);
        var interH = Math.Max(0, interY2 - interY1);
        var interArea = interW * interH;
        if (interArea == 0)
        {
            return 0;
        }

        var union = (a.W * a.H) + (b.W * b.H) - interArea;
        return union <= 0 ? 0 : interArea / (double)union;
    }

    private static double OverlapOverCandidate(BboxInfo token, BboxInfo candidate)
    {
        var interX1 = Math.Max(token.X, candidate.X);
        var interY1 = Math.Max(token.Y, candidate.Y);
        var interX2 = Math.Min(token.X + token.W, candidate.X + candidate.W);
        var interY2 = Math.Min(token.Y + token.H, candidate.Y + candidate.H);
        var interW = Math.Max(0, interX2 - interX1);
        var interH = Math.Max(0, interY2 - interY1);
        var interArea = interW * interH;
        var candidateArea = Math.Max(1, candidate.W * candidate.H);
        return interArea / (double)candidateArea;
    }

    private static BboxInfo ToBbox(Rect rect)
    {
        return new BboxInfo
        {
            X = Math.Max(0, rect.X),
            Y = Math.Max(0, rect.Y),
            W = Math.Max(0, rect.Width),
            H = Math.Max(0, rect.Height)
        };
    }

    private sealed class RegionCandidate
    {
        public required string Type { get; init; }
        public required BboxInfo Bbox { get; init; }
        public double Aspect { get; init; }
        public int Corners { get; init; }
        public double Rectangularity { get; init; }
        public double Circularity { get; init; }
        public double BorderRatio { get; init; }
        public double InteriorDarkRatio { get; init; }
        public double InteriorInkRatio { get; init; }
        public double CenterDarkRatio { get; init; }
        public bool? Value { get; init; }
        public double Confidence { get; set; }
        public List<string> LabelTokenIds { get; set; } = [];
        public List<string> Notes { get; set; } = [];
    }
}
