namespace PGMS.PostgreSql;

public sealed record QueryNotice(string Severity,string SqlState,string Message);

/// <summary>Bounded, thread-safe capture. Use one instance per execution; not persisted.</summary>
public sealed class QueryNotices
{
    private readonly object sync=new();
    private readonly List<QueryNotice> items=[];
    private int characters;
    private long omitted;
    public IReadOnlyList<QueryNotice> Items { get { lock(sync)return items.ToArray(); } }
    public long Omitted { get { lock(sync)return omitted; } }
    internal void Add(string severity,string sqlState,string message)
    {
        lock(sync)
        {
            long length=(long)severity.Length+sqlState.Length+message.Length;
            if(items.Count>=100 || length>4096 || characters+length>65536)
            { if(omitted<long.MaxValue)omitted++;return; }
            items.Add(new(severity,sqlState,message));characters+=(int)length;
        }
    }
}
