using System.Globalization;

namespace PGMS.PostgreSql;

public enum PartitionValueKind { Literal, Null, Minimum, Maximum }
public sealed record PartitionValue(PartitionValueKind Kind, string Text = "")
{
    public string ToSql()
    {
        if (!Enum.IsDefined(Kind)) throw new ArgumentException("Unsupported partition value.");
        if (Kind != PartitionValueKind.Literal && Text.Length != 0) throw new ArgumentException("Special partition values cannot carry literal text.");
        if (Kind == PartitionValueKind.Literal && (Text.Contains('\0') || Text.Length > 10000)) throw new ArgumentException("Partition literals allow at most 10000 characters and no NUL.");
        return Kind switch
        {
            PartitionValueKind.Null => "NULL", PartitionValueKind.Minimum => "MINVALUE", PartitionValueKind.Maximum => "MAXVALUE",
            _ => "E'" + Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'"
        };
    }
}

/// <summary>Captured literal bounds, never SQL expressions. The server validates ordering and overlap.</summary>
public sealed class PartitionBounds
{
    public PartitionStrategy Strategy { get; }
    public bool IsDefault { get; }
    public IReadOnlyList<PartitionValue> Lower { get; }
    public IReadOnlyList<PartitionValue> Upper { get; }
    public int Modulus { get; }
    public int Remainder { get; }
    private PartitionBounds(PartitionStrategy strategy, bool isDefault, IEnumerable<PartitionValue> lower, IEnumerable<PartitionValue> upper, int modulus=0, int remainder=0)
    {
        Strategy=strategy;IsDefault=isDefault;Lower=Capture(lower);Upper=Capture(upper);Modulus=modulus;Remainder=remainder;
    }
    private static IReadOnlyList<PartitionValue> Capture(IEnumerable<PartitionValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);var result=values.Take(1001).ToArray();
        if(result.Length>1000 || result.Any(v=>v is null))throw new ArgumentException("Use at most 1000 non-null value descriptors.");
        foreach(var value in result)_=value.ToSql();return Array.AsReadOnly(result);
    }
    public static PartitionBounds Range(IEnumerable<PartitionValue> lower,IEnumerable<PartitionValue> upper)=>new(PartitionStrategy.Range,false,lower,upper);
    public static PartitionBounds List(IEnumerable<PartitionValue> values)=>new(PartitionStrategy.List,false,values,[]);
    public static PartitionBounds Hash(int modulus,int remainder)=>new(PartitionStrategy.Hash,false,[],[],modulus,remainder);
    public static PartitionBounds Default(PartitionStrategy strategy)
    {
        if(strategy is not(PartitionStrategy.Range or PartitionStrategy.List))throw new ArgumentException("DEFAULT is available for RANGE and LIST only.");
        return new(strategy,true,[],[]);
    }
    public string ToSql(PartitionStrategy strategy,int keyCount)
    {
        if(strategy!=Strategy || keyCount is <1 or >32)throw new ArgumentException("Bounds must match the inspected strategy and key count.");
        if(IsDefault)return "DEFAULT";
        if(Strategy==PartitionStrategy.Hash)
        {
            if(Modulus<=0 || Remainder<0 || Remainder>=Modulus)throw new ArgumentException("HASH requires positive modulus and remainder from zero to modulus minus one.");
            return $"FOR VALUES WITH (MODULUS {Modulus.ToString(CultureInfo.InvariantCulture)}, REMAINDER {Remainder.ToString(CultureInfo.InvariantCulture)})";
        }
        if(Strategy==PartitionStrategy.List)
        {
            if(keyCount!=1 || Lower.Count==0 || Lower.Any(v=>v.Kind is PartitionValueKind.Minimum or PartitionValueKind.Maximum))throw new ArgumentException("LIST requires literal or NULL values for one key.");
            return "FOR VALUES IN ("+string.Join(", ",Lower.Select(v=>v.ToSql()))+")";
        }
        foreach(var tuple in new[]{Lower,Upper})
        {
            if(tuple.Count!=keyCount || tuple.Any(v=>v.Kind==PartitionValueKind.Null))throw new ArgumentException("RANGE requires one non-NULL bound per key.");
            PartitionValueKind? special=null;
            foreach(var value in tuple)
            {
                if(special is { } kind && value.Kind!=kind)throw new ArgumentException("All values after MINVALUE or MAXVALUE must use the same marker.");
                if(value.Kind is PartitionValueKind.Minimum or PartitionValueKind.Maximum)special=value.Kind;
            }
        }
        return "FOR VALUES FROM ("+string.Join(", ",Lower.Select(v=>v.ToSql()))+") TO ("+string.Join(", ",Upper.Select(v=>v.ToSql()))+")";
    }
}
