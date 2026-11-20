using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace PGMS.PostgreSql;

/// <summary>
/// PostGIS values as readable text (PGMS-047, M1124). The server writes a geometry or geography as hexadecimal EWKB; PGMS shows it as EWKT, the
/// text of ST_AsEWKT ("SRID=4326;POINT(1 2)"), which the geometry and geography input functions accept back. Coordinates use the shortest text
/// that gives the same double, so the value typed back is the same geometry. Anything that is not complete, valid EWKB stays as the server wrote it.
/// </summary>
public static class SpatialText
{
    /// <summary>Whether a column type, as Npgsql names it (possibly schema-qualified or quoted), is the PostGIS geometry or geography type.</summary>
    public static bool IsSpatialType(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        string name = typeName;
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        name = name.Trim('"');
        int paren = name.IndexOf('(');
        if (paren >= 0) name = name[..paren];
        return name.Equals("geometry", StringComparison.OrdinalIgnoreCase) || name.Equals("geography", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The EWKT of a hexadecimal EWKB value, or the value itself when it is not complete, valid EWKB.</summary>
    public static string Display(string value) => TryToEwkt(value, out var text) ? text : value;

    public static bool TryToEwkt(string hex, out string text)
    {
        text = "";
        if (hex.Length < 10 || hex.Length % 2 != 0 || hex.Length > 512 * 1024 * 1024) return false;
        byte[] bytes;
        try { bytes = Convert.FromHexString(hex); }
        catch (FormatException) { return false; }
        var reader = new Reader(bytes);
        var builder = new StringBuilder(hex.Length);
        try
        {
            if (!Geometry(ref reader, builder, top: true, omitName: false)) return false;
        }
        catch (ArgumentOutOfRangeException) { return false; }
        if (reader.Position != bytes.Length) return false;
        text = builder.ToString();
        return true;
    }

    private const int MaxDepth = 32;

    private ref struct Reader(byte[] bytes)
    {
        private readonly byte[] data = bytes;
        public int Position;
        public bool Little;
        public int Depth;

        public byte Byte() => Position < data.Length ? data[Position++] : throw new ArgumentOutOfRangeException(nameof(data));

        public uint UInt32()
        {
            if (Position + 4 > data.Length) throw new ArgumentOutOfRangeException(nameof(data));
            var span = data.AsSpan(Position, 4); Position += 4;
            return Little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
        }

        public double Double()
        {
            if (Position + 8 > data.Length) throw new ArgumentOutOfRangeException(nameof(data));
            var span = data.AsSpan(Position, 8); Position += 8;
            return Little ? BinaryPrimitives.ReadDoubleLittleEndian(span) : BinaryPrimitives.ReadDoubleBigEndian(span);
        }

        public int Remaining => data.Length - Position;
    }

    private static readonly string[] Names =
        ["", "POINT", "LINESTRING", "POLYGON", "MULTIPOINT", "MULTILINESTRING", "MULTIPOLYGON", "GEOMETRYCOLLECTION", "CIRCULARSTRING", "COMPOUNDCURVE",
         "CURVEPOLYGON", "MULTICURVE", "MULTISURFACE", "", "", "POLYHEDRALSURFACE", "TIN", "TRIANGLE"];

    /// <summary>The member type whose name is left out inside a collection, as ST_AsEWKT does (0: every member is named).</summary>
    private static int DefaultMember(int type) => type switch
    {
        4 => 1, 5 => 2, 6 => 3, 9 => 2, 10 => 2, 11 => 2, 12 => 3, 15 => 3, 16 => 17, _ => 0,
    };

    private static bool Geometry(ref Reader reader, StringBuilder builder, bool top, bool omitName, int parentDefault = 0)
    {
        if (++reader.Depth > MaxDepth) return false;
        byte order = reader.Byte();
        if (order > 1) return false;
        reader.Little = order == 1;
        uint raw = reader.UInt32();
        bool hasZ = (raw & 0x80000000) != 0, hasM = (raw & 0x40000000) != 0, hasSrid = (raw & 0x20000000) != 0;
        uint code = raw & 0x0FFFFFFF;
        if (code >= 1000)
        {
            // ISO WKB: 1000 Z, 2000 M, 3000 ZM.
            uint thousands = code / 1000;
            if (thousands > 3) return false;
            hasZ |= thousands is 1 or 3; hasM |= thousands is 2 or 3;
            code %= 1000;
        }
        // OGC codes: 13 (Curve) and 14 (Surface) are abstract; 15 PolyhedralSurface, 16 TIN, 17 Triangle.
        if (code is 0 or 13 or 14 or > 17) return false;
        int type = (int)code;
        if (hasSrid)
        {
            uint srid = reader.UInt32();
            if (!top) return false;
            if (srid != 0) builder.Append("SRID=").Append(srid.ToString(CultureInfo.InvariantCulture)).Append(';');
        }
        int dimensions = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        if (!(omitName && parentDefault == type))
        {
            builder.Append(Names[type]);
            // ST_AsEWKT marks M alone with a suffix; a TIN with Z is the one type it writes as "TIN Z (…)" (observed on PostGIS 3.7).
            if (hasM && !hasZ) builder.Append('M');
            else if (type == 16 && hasZ && !hasM) builder.Append(" Z ");
        }
        bool ok = type switch
        {
            1 => Point(ref reader, builder, dimensions, bare: omitName && parentDefault == 1),
            2 or 8 => Points(ref reader, builder, dimensions),
            3 or 17 => Rings(ref reader, builder, dimensions),
            _ => Members(ref reader, builder, type),
        };
        reader.Depth--;
        return ok;
    }

    /// <summary>A point; inside a MULTIPOINT, bare coordinates ("MULTIPOINT(0 0,1 1)"), as ST_AsEWKT writes them.</summary>
    private static bool Point(ref Reader reader, StringBuilder builder, int dimensions, bool bare)
    {
        var values = new double[dimensions];
        for (int i = 0; i < dimensions; i++) values[i] = reader.Double();
        if (values.All(double.IsNaN)) { builder.Append(bare ? "EMPTY" : " EMPTY"); return true; }
        if (!bare) builder.Append('(');
        Coordinates(builder, values);
        if (!bare) builder.Append(')');
        return true;
    }

    private static void Coordinates(StringBuilder builder, double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) builder.Append(' ');
            builder.Append(Number(values[i]));
        }
    }

    /// <summary>The shortest text that reads back as the same double (".NET round-trip"), without exponent for ordinary coordinates.</summary>
    internal static string Number(double value)
    {
        if (value == 0) return "0";
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        if (text.Contains('E', StringComparison.Ordinal) && Math.Abs(value) is >= 1e-6 and < 1e21)
        {
            // Write ordinary numbers in plain decimal notation, as PostGIS does; R gives an exponent from 1e15 on.
            string plain = value.ToString("0.#################################", CultureInfo.InvariantCulture);
            if (double.Parse(plain, CultureInfo.InvariantCulture) == value) return plain;
        }
        return text;
    }

    private static bool Points(ref Reader reader, StringBuilder builder, int dimensions)
    {
        uint count = reader.UInt32();
        if (count == 0) { builder.Append(" EMPTY"); return true; }
        if ((long)count * dimensions * 8 > reader.Remaining) return false;
        builder.Append('(');
        var values = new double[dimensions];
        for (uint p = 0; p < count; p++)
        {
            if (p > 0) builder.Append(',');
            for (int i = 0; i < dimensions; i++) values[i] = reader.Double();
            Coordinates(builder, values);
        }
        builder.Append(')');
        return true;
    }

    private static bool Rings(ref Reader reader, StringBuilder builder, int dimensions)
    {
        uint rings = reader.UInt32();
        if (rings == 0) { builder.Append(" EMPTY"); return true; }
        if ((long)rings * 4 > reader.Remaining) return false;
        builder.Append('(');
        for (uint r = 0; r < rings; r++)
        {
            if (r > 0) builder.Append(',');
            uint count = reader.UInt32();
            if ((long)count * dimensions * 8 > reader.Remaining) return false;
            builder.Append('(');
            var values = new double[dimensions];
            for (uint p = 0; p < count; p++)
            {
                if (p > 0) builder.Append(',');
                for (int i = 0; i < dimensions; i++) values[i] = reader.Double();
                Coordinates(builder, values);
            }
            builder.Append(')');
        }
        builder.Append(')');
        return true;
    }

    private static bool Members(ref Reader reader, StringBuilder builder, int type)
    {
        uint count = reader.UInt32();
        if (count == 0) { builder.Append(" EMPTY"); return true; }
        if ((long)count * 5 > reader.Remaining) return false;
        int member = DefaultMember(type);
        builder.Append('(');
        for (uint m = 0; m < count; m++)
        {
            if (m > 0) builder.Append(',');
            if (!Geometry(ref reader, builder, top: false, omitName: member != 0, parentDefault: member)) return false;
        }
        builder.Append(')');
        return true;
    }
}
