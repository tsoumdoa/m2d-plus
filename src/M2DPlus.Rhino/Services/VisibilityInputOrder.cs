using M2DPlus.Rhino.Sections;

namespace M2DPlus.Rhino.Services;

/// <summary>M1b source order, extended by stable geometry identity for generated registrations.</summary>
internal static class VisibilityInputOrder
{
    internal const string Policy = "M1b path/identity; generated geometry SHA-256; far flag";
    internal static int CompareSource(PreparedLeaf a, PreparedLeaf b)
    {
        for (int i=0; i<Math.Min(a.Path.Length,b.Path.Length); i++)
        { int c=a.Path[i].CompareTo(b.Path[i]); if(c!=0)return c; }
        int order=a.Path.Length.CompareTo(b.Path.Length);
        return order!=0 ? order : StringComparer.Ordinal.Compare(a.Identity,b.Identity);
    }
    internal static T[] Canonical<T>(IEnumerable<T> values, Func<T,VisibilityInput> input)
    {
        var entries=values.Select(value => (Value:value,Input:input(value),Key:(string?)null)).ToArray();
        // Compute generated-piece keys once; never fingerprint inside the sort comparer.
        foreach(var group in entries.Select((e,i)=>(e.Input.Source.Identity,Index:i)).GroupBy(e=>e.Identity))
            if(group.Skip(1).Any())
                foreach(var entry in group) entries[entry.Index].Key=GeometryFingerprint.Compute(entries[entry.Index].Input.Geometry);
        Array.Sort(entries,(a,b)=>
        {
            int order=CompareSource(a.Input.Source,b.Input.Source);
            if(order==0)order=StringComparer.Ordinal.Compare(a.Key,b.Key);
            if(order==0)order=a.Input.Far.CompareTo(b.Input.Far);
            return order;
        });
        return entries.Select(e=>e.Value).ToArray();
    }
    internal static T[] Native<T>(IEnumerable<T> values, Func<T,VisibilityInput> input, NativeSettings settings)
    {
        var ordered=Canonical(values,input);
        if(settings.NativeOnly && settings.ReverseNativeForAudit) Array.Reverse(ordered);
        settings.ObserveNativeInputs?.Invoke(ordered.Select(input).ToArray());
        return ordered;
    }
}
