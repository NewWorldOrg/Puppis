namespace Puppis.Architecture.Tests;

public sealed record SourceComment(int Line, string Text);

public sealed class CommentScanner
{
    private readonly string source;
    private readonly List<SourceComment> found = [];
    private int position;

    private CommentScanner(string source)
    {
        this.source = source;
    }

    public static IReadOnlyList<SourceComment> Find(string source)
    {
        CommentScanner scanner = new(source);
        scanner.Code(inHole: false);

        return scanner.found;
    }

    private bool Starts(string text) => string.CompareOrdinal(source, position, text, 0, text.Length) == 0;

    private int Run(char mark)
    {
        int length = 0;

        while (position + length < source.Length && source[position + length] == mark)
        {
            length++;
        }

        return length;
    }

    private void Code(bool inHole)
    {
        int depth = 0;

        while (position < source.Length)
        {
            char current = source[position];

            if (inHole && current == '}' && depth == 0)
            {
                return;
            }

            depth += inHole ? Nesting(current) : 0;
            Step(current);
        }
    }

    private static int Nesting(char current)
        => current switch
        {
            '{' => 1,
            '}' => -1,
            _ => 0,
        };

    private void Step(char current)
    {
        if (Starts("//"))
        {
            Comment(until: "\n");
        }
        else if (Starts("/*"))
        {
            Comment(until: "*/");
        }
        else if (current is '$' or '@' or '"')
        {
            Literal();
        }
        else if (current == '\'')
        {
            Character();
        }
        else
        {
            position++;
        }
    }

    private void Comment(string until)
    {
        int start = position;
        int end = source.IndexOf(until, start + 2, StringComparison.Ordinal);
        int stop = end < 0 ? source.Length : end;

        found.Add(new SourceComment(1 + source.AsSpan(0, start).Count('\n'), source[start..stop]));
        position = end < 0 ? source.Length : stop + (until == "\n" ? 0 : until.Length);
    }

    private void Character()
    {
        position++;

        while (position < source.Length && source[position] != '\'')
        {
            position += source[position] == '\\' ? 2 : 1;
        }

        position++;
    }

    private void Literal()
    {
        int dollars = 0;
        bool verbatim = false;

        while (position < source.Length && source[position] is '$' or '@')
        {
            dollars += source[position] == '$' ? 1 : 0;
            verbatim |= source[position] == '@';
            position++;
        }

        if (position >= source.Length || source[position] != '"')
        {
            return;
        }

        int quotes = Run('"');

        if (quotes >= 3 && !verbatim)
        {
            Raw(quotes, dollars);
        }
        else
        {
            Quoted(dollars, verbatim);
        }
    }

    private void Raw(int quotes, int dollars)
    {
        position += quotes;

        while (position < source.Length && Run('"') < quotes)
        {
            if (source[position] == '{' && dollars > 0)
            {
                Hole(dollars);
            }
            else
            {
                position++;
            }
        }

        position += quotes;
    }

    private void Hole(int dollars)
    {
        int braces = Run('{');

        if (braces < dollars)
        {
            position += braces;

            return;
        }

        position += braces;
        Code(inHole: true);
        position += dollars;
    }

    private void Quoted(int dollars, bool verbatim)
    {
        position++;

        while (position < source.Length && !Closes(verbatim))
        {
            int advance = Inside(dollars, verbatim);
            position += advance;
        }

        position++;
    }

    private bool Closes(bool verbatim)
        => source[position] == '"' && !(verbatim && Starts("\"\""));

    private int Inside(int dollars, bool verbatim)
    {
        char current = source[position];

        if (current == '"' || (current == '\\' && !verbatim))
        {
            return 2;
        }

        if (dollars == 0 || (current != '{' && current != '}'))
        {
            return 1;
        }

        if (Starts("{{") || Starts("}}"))
        {
            return 2;
        }

        if (current == '{')
        {
            position++;
            Code(inHole: true);
        }

        return 1;
    }
}
