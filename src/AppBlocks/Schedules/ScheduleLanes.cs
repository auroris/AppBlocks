namespace UmbracoAppHost.Schedules;

/// <summary>
/// Packs the bars of one row into sub-rows, called lanes, so bars that overlap in time stay individually visible:
/// each bar takes the first lane in which none of its columns is used yet. It is the rule the activity schedule's
/// <c>renderHorizontalCategories</c> applies through <c>isCellUsed</c> and <c>mergeCells</c>, without the table.
/// Columns are inclusive indexes, so a bar that ends in the column where the next one begins shares that column and
/// the next one takes another lane. Bars are packed in the order given; the caller sorts them by start.
/// </summary>
public static class ScheduleLanes
{
    /// <summary>The lane of each span, in the order given, and how many lanes the row needs, at least one.</summary>
    public static (int[] Lanes, int Count) Pack(IReadOnlyList<(int Start, int End)> spans)
    {
        var lanes = new int[spans.Count];
        var used = new List<List<(int Start, int End)>>();
        for (var i = 0; i < spans.Count; i++)
        {
            var (start, end) = spans[i];
            if (end < start) end = start;
            var lane = 0;
            while (lane < used.Count && used[lane].Any(taken => taken.Start <= end && start <= taken.End)) lane++;
            if (lane == used.Count) used.Add([]);
            used[lane].Add((start, end));
            lanes[i] = lane;
        }
        return (lanes, Math.Max(1, used.Count));
    }
}
