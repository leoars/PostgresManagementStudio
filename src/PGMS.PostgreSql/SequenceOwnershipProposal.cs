namespace PGMS.PostgreSql;

public sealed record SequenceOwnershipProposal(SequenceSnapshot Sequence, SequenceOwnerTarget Target)
{
    public string ToSql()
    {
        if (Sequence is null || Target is null) throw new ArgumentException("Inspect both the sequence and destination column first.");
        if (Sequence.Schema != Target.Schema || Sequence.Owner != Target.Owner)
            throw new ArgumentException("Sequence and destination table must have the same schema and owner.");
        if (Sequence.Dependency?.Kind == "i") throw new ArgumentException("Identity sequences cannot be reassociated here.");
        if (Target.Kind != "r" || Target.Persistence != "p" || Target.Identity != "" || Target.Generated != "")
            throw new ArgumentException("Choose a common column of a permanent ordinary table; identity and generated columns are not supported.");
        if (Sequence.Dependency == new SequenceDependency(Target.TableOid, Target.ColumnNumber, "a"))
            throw new ArgumentException("This sequence is already associated with that column.");
        return $"ALTER SEQUENCE {ObjectSql.Relation(Sequence.Schema, Sequence.Name)} OWNED BY {ObjectSql.Relation(Target.Schema, Target.Table)}.{SqlIdentifier.Quote(Target.Column)};";
    }
}
