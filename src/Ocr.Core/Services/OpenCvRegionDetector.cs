using Ocr.Core.Abstractions;
using Ocr.Core.Contracts;
using OpenCvSharp;

namespace Ocr.Core.Services;

public sealed class OpenCvRegionDetector : IRegionDetector
{
    private const int MinBoxSize = 10;
    private const int MaxBoxSize = 36;
    private const int LabelSearchDistancePx = 120;
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

        var detected = new List<DetectedRegion>();
        detected.AddRange(DetectCheckboxes(page, gray, binary));
        detected.AddRange(DetectRadios(page, gray, detected));

        var deduped = Deduplicate(detected)
            .OrderBy(r => r.Bbox.Y)
            .ThenBy(r => r.Bbox.X)
            .ThenBy(r => r.Type, StringComparer.Ordinal)
            .ToList();

        var regions = new List<RegionInfo>(deduped.Count);
        var overlays = new List<RegionOverlayInfo>(deduped.Count);
        var checkboxIndex = 1;
        var radioIndex = 1;

        foreach (var region in deduped)
        {
            var prefix = region.Type == "radio" ? "rad" : "chk";
            var index = region.Type == "radio" ? radioIndex++ : checkboxIndex++;
            regions.Add(new RegionInfo
            {
                RegionId = $"{prefix}-{page.PageIndex:000}-{index:0000}",
                Type = region.Type,
                Bbox = region.Bbox,
                Confidence = Math.Clamp(region.Confidence, 0, 1),
                Value = region.Value,
                LabelTokenIds = region.LabelTokenIds
            });

            overlays.Add(new RegionOverlayInfo
            {
                Type = region.Type,
                Value = region.Value,
                Bbox = region.Bbox
            });
        }

        return new RegionDetectionResult
        {
            Regions = regions,
            Overlays = overlays
        };
    }

    private static List<DetectedRegion> DetectCheckboxes(PageInfo page, Mat gray, Mat binary)
    {
        Cv2.FindContours(binary, out var contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var result = new List<DetectedRegion>();

        foreach (var contour in contours)
        {
            var rect = Cv2.BoundingRect(contour);
            if (!IsSmallSquare(rect))
            {
                continue;
            }

            var perimeter = Cv2.ArcLength(contour, true);
            if (perimeter <= 0)
            {
                continue;
            }

            var approx = Cv2.ApproxPolyDP(contour, perimeter * 0.04, true);
            if (approx.Length is < 4 or > 8 || !Cv2.IsContourConvex(approx))
            {
                continue;
            }

            var bbox = ToBbox(rect);
            if (IsInsideTextToken(page.Tokens, bbox) || OverlapsTextToken(page.Tokens, bbox, 0.25))
            {
                continue;
            }

            var contourArea = Cv2.ContourArea(contour);
            var contourFillRatio = contourArea / Math.Max(1.0, rect.Width * rect.Height);
            if (contourFillRatio is < 0.2 or > 0.9)
            {
                continue;
            }

            var borderRatio = CalculateBorderRatio(binary, rect);
            var fillRatio = CalculateInteriorDarkRatio(gray, rect);
            var checkedValue = fillRatio > CheckedFillThreshold;

            if (borderRatio < 0.14 || fillRatio >= 0.8)
            {
                continue;
            }

            var aspect = rect.Height == 0 ? 0 : rect.Width / (double)rect.Height;
            var shapeScore = Math.Clamp(1.0 - Math.Abs(aspect - 1.0), 0, 1);
            var sizeScore = Math.Clamp(1.0 - (Math.Abs(((rect.Width + rect.Height) / 2.0) - 18.0) / 18.0), 0, 1);
            var borderScore = Math.Clamp(borderRatio / 0.25, 0, 1);
            var confidence = Math.Clamp((shapeScore * 0.35) + (sizeScore * 0.25) + (borderScore * 0.4), 0, 1);
            if (confidence < 0.58)
            {
                continue;
            }

            result.Add(new DetectedRegion
            {
                Type = "checkbox",
                Bbox = bbox,
                Value = checkedValue,
                Confidence = confidence,
                LabelTokenIds = FindLabelTokenIds(page, bbox)
            });
        }

        return result;
    }

    private static List<DetectedRegion> DetectRadios(PageInfo page, Mat gray, List<DetectedRegion> existing)
    {
        using var blurred = new Mat();
        Cv2.GaussianBlur(gray, blurred, new Size(5, 5), 1.2);
        var circles = Cv2.HoughCircles(
            blurred,
            HoughModes.Gradient,
            dp: 1.2,
            minDist: 12,
            param1: 100,
            param2: 16,
            minRadius: 6,
            maxRadius: 14);

        var result = new List<DetectedRegion>();
        foreach (var circle in circles)
        {
            var x = (int)Math.Round(circle.Center.X - circle.Radius);
            var y = (int)Math.Round(circle.Center.Y - circle.Radius);
            var d = (int)Math.Round(circle.Radius * 2);
            var rect = new Rect(x, y, d, d);
            if (!IsSmallSquare(rect))
            {
                continue;
            }

            var bbox = ToBbox(rect);
            if (existing.Any(r => IntersectionOverUnion(r.Bbox, bbox) > 0.35))
            {
                continue;
            }

            if (IsInsideTextToken(page.Tokens, bbox) || OverlapsTextToken(page.Tokens, bbox, 0.25))
            {
                continue;
            }

            var circularity = EstimateCircularity(gray, rect);
            if (circularity < 0.65)
            {
                continue;
            }

            var fillRatio = CalculateInteriorDarkRatio(gray, rect);
            var checkedValue = fillRatio > CheckedFillThreshold;
            if (fillRatio > 0.75)
            {
                continue;
            }
            var sizeScore = Math.Clamp(1.0 - (Math.Abs(circle.Radius - 9.0) / 9.0), 0, 1);
            var confidence = Math.Clamp((circularity * 0.7) + (sizeScore * 0.3), 0, 1);
            if (confidence < 0.68)
            {
                continue;
            }

            var labelTokenIds = FindLabelTokenIds(page, bbox);
            if (labelTokenIds.Count == 0)
            {
                continue;
            }

            result.Add(new DetectedRegion
            {
                Type = "radio",
                Bbox = bbox,
                Value = checkedValue,
                Confidence = confidence,
                LabelTokenIds = labelTokenIds
            });
        }

        return result;
    }

    private static List<DetectedRegion> Deduplicate(List<DetectedRegion> regions)
    {
        var deduped = new List<DetectedRegion>();
        foreach (var candidate in regions
                     .OrderByDescending(r => r.Confidence)
                     .ThenBy(r => r.Bbox.Y)
                     .ThenBy(r => r.Bbox.X))
        {
            var duplicate = deduped.Any(existing =>
                string.Equals(existing.Type, candidate.Type, StringComparison.Ordinal) &&
                IntersectionOverUnion(existing.Bbox, candidate.Bbox) > 0.5);

            if (!duplicate)
            {
                deduped.Add(candidate);
            }
        }

        return deduped;
    }

    private static bool IsSmallSquare(Rect rect)
    {
        if (rect.Width < MinBoxSize || rect.Height < MinBoxSize || rect.Width > MaxBoxSize || rect.Height > MaxBoxSize)
        {
            return false;
        }

        var aspect = rect.Height == 0 ? 0 : rect.Width / (double)rect.Height;
        return aspect is >= 0.75 and <= 1.25;
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
        var darkPixels = Cv2.CountNonZero(darkMask);
        return darkPixels / (double)(w * h);
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
        var yTolerance = Math.Max(12, regionBbox.H);

        var candidates = page.Tokens
            .Where(t =>
                t.Bbox.X >= rightX - 2 &&
                t.Bbox.X <= rightX + LabelSearchDistancePx &&
                (VerticalOverlap(t.Bbox, regionBbox) >= 0.2 ||
                 Math.Abs((t.Bbox.Y + (t.Bbox.H / 2.0)) - centerY) <= yTolerance))
            .OrderBy(t => Math.Abs((t.Bbox.Y + (t.Bbox.H / 2.0)) - centerY))
            .ThenBy(t => t.Bbox.X)
            .ToList();

        if (candidates.Count == 0)
        {
            return [];
        }

        var anchor = candidates[0];
        var lineTokens = page.Tokens
            .Where(t => t.LineId == anchor.LineId && t.Bbox.X >= rightX - 2 && t.Bbox.X <= rightX + 200)
            .OrderBy(t => t.Bbox.X)
            .ThenBy(t => t.Bbox.Y)
            .Select(t => t.Id)
            .ToList();

        return lineTokens.Count == 0 ? [anchor.Id] : lineTokens;
    }

    private static double VerticalOverlap(BboxInfo a, BboxInfo b)
    {
        var top = Math.Max(a.Y, b.Y);
        var bottom = Math.Min(a.Y + a.H, b.Y + b.H);
        var overlap = Math.Max(0, bottom - top);
        var minHeight = Math.Max(1, Math.Min(a.H, b.H));
        return overlap / (double)minHeight;
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

    private sealed class DetectedRegion
    {
        public required string Type { get; init; }
        public required BboxInfo Bbox { get; init; }
        public bool? Value { get; init; }
        public double Confidence { get; init; }
        public List<string> LabelTokenIds { get; init; } = [];
    }
}
