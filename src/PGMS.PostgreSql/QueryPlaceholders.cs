namespace PGMS.PostgreSql;

/// <summary>First and highest $n placeholder found in statements that the server would parse with parameters.</summary>
public sealed record UnboundParameters(int Offset,int Number,int Highest,int Count)
{
    public string Description => Count==1?$"${Number}":$"${Number} (highest ${Highest}, {Count} occurrences)";
}

/// <summary>Raised before anything is sent when SQL contains $n placeholders that have no values.</summary>
public sealed class UnboundParametersException(UnboundParameters parameters)
    : ArgumentException($"Not sent: the SQL contains parameter placeholder {parameters.Description}, which has no value. Replace placeholders with literal values before executing. Nothing was sent to the server and the session stays open.")
{
    public UnboundParameters Parameters { get; } = parameters;
}

/// <summary>
/// Finds positional parameters ($1, $2…) outside literals, quoted identifiers, comments and dollar-quoted bodies.
/// Npgsql sends every statement through Parse/Bind, so a bare $n makes the server expect a value the editor never supplies:
/// the server answers 08P01 and Npgsql closes the connection. Statements split at top-level semicolons, as Npgsql does.
/// PREPARE and CREATE FUNCTION/PROCEDURE keep their own parameters and are not reported.
/// Assumes standard_conforming_strings = on (default since PostgreSQL 9.1): backslash escapes only in E'…' strings.
/// </summary>
public static class QueryPlaceholders
{
    public static UnboundParameters? Find(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        int firstOffset=-1,firstNumber=0,highest=0,count=0;
        var words=new List<string>(4);int statementFirst=-1,statementNumber=0,statementHighest=0,statementCount=0;
        void EndStatement()
        {
            if(statementCount>0 && !Exempt(words))
            {
                if(firstOffset<0){firstOffset=statementFirst;firstNumber=statementNumber;}
                highest=Math.Max(highest,statementHighest);count+=statementCount;
            }
            words.Clear();statementFirst=-1;statementNumber=0;statementHighest=0;statementCount=0;
        }
        int i=0;
        while(i<sql.Length)
        {
            char c=sql[i];
            if(c=='-' && i+1<sql.Length && sql[i+1]=='-'){while(i<sql.Length && sql[i]!='\n')i++;continue;}
            if(c=='/' && i+1<sql.Length && sql[i+1]=='*')
            {
                int depth=1;i+=2;
                while(i<sql.Length && depth>0)
                {
                    if(sql[i]=='/' && i+1<sql.Length && sql[i+1]=='*'){depth++;i+=2;}
                    else if(sql[i]=='*' && i+1<sql.Length && sql[i+1]=='/'){depth--;i+=2;}
                    else i++;
                }
                continue;
            }
            if(c=='\''){i=SkipQuoted(sql,i,'\'',false);continue;}
            if(c=='"'){i=SkipQuoted(sql,i,'"',false);continue;}
            if(c==';'){EndStatement();i++;continue;}
            if(c=='$')
            {
                if(i+1<sql.Length && char.IsAsciiDigit(sql[i+1]))
                {
                    int start=i;i++;long number=0;
                    while(i<sql.Length && char.IsAsciiDigit(sql[i])){if(number<int.MaxValue)number=number*10+(sql[i]-'0');i++;}
                    int value=(int)Math.Min(number,int.MaxValue);
                    if(statementCount==0){statementFirst=start;statementNumber=value;}
                    statementHighest=Math.Max(statementHighest,value);statementCount++;
                    continue;
                }
                int tagEnd=i+1;
                if(tagEnd<sql.Length && (sql[tagEnd]=='_' || char.IsAsciiLetter(sql[tagEnd]) || sql[tagEnd]>=0x80))
                    while(tagEnd<sql.Length && (sql[tagEnd]=='_' || char.IsAsciiLetterOrDigit(sql[tagEnd]) || sql[tagEnd]>=0x80))tagEnd++;
                if(tagEnd<sql.Length && sql[tagEnd]=='$')
                {
                    string tag=sql[i..(tagEnd+1)];
                    int close=sql.IndexOf(tag,tagEnd+1,StringComparison.Ordinal);
                    i=close<0?sql.Length:close+tag.Length;
                    continue;
                }
                i++;continue;
            }
            if(c=='_' || char.IsAsciiLetter(c) || c>=0x80)
            {
                int start=i;
                while(i<sql.Length && (sql[i]=='_' || sql[i]=='$' || char.IsAsciiLetterOrDigit(sql[i]) || sql[i]>=0x80))i++;
                if((c=='E' || c=='e') && i==start+1 && i<sql.Length && sql[i]=='\''){i=SkipQuoted(sql,i,'\'',true);continue;}
                if(words.Count<4)words.Add(sql[start..i].ToUpperInvariant());
                continue;
            }
            i++;
        }
        EndStatement();
        return firstOffset<0?null:new(firstOffset,firstNumber,highest,count);
    }

    public static void ThrowIfUnbound(string sql)
    {
        if(Find(sql) is { } parameters)throw new UnboundParametersException(parameters);
    }

    private static int SkipQuoted(string sql,int open,char quote,bool backslashEscapes)
    {
        int i=open+1;
        while(i<sql.Length)
        {
            if(backslashEscapes && sql[i]=='\\'){i+=2;continue;}
            if(sql[i]==quote)
            {
                if(i+1<sql.Length && sql[i+1]==quote){i+=2;continue;}
                return i+1;
            }
            i++;
        }
        return sql.Length;
    }

    private static bool Exempt(List<string> words)
    {
        if(words.Count==0)return false;
        if(words[0]=="PREPARE")return true;
        if(words[0]!="CREATE")return false;
        int kind=words.Count>3 && words[1]=="OR" && words[2]=="REPLACE"?3:1;
        return words.Count>kind && words[kind] is "FUNCTION" or "PROCEDURE";
    }
}
