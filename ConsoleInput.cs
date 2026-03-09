using System.Text;

class ConsoleInput
{
    readonly string[] _commands;
    readonly List<string> _history = new();

    int _historyIndex = -1;

    List<string>? _matches;
    int _matchIndex;

    public ConsoleInput(string[] commands)
    {
        _commands = commands;
    }

    public string ReadLine()
    {
        var buffer = new StringBuilder();
        int cursor = 0;

        while (true)
        {
            var key = Console.ReadKey(true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    var line = buffer.ToString();
                    if (!string.IsNullOrWhiteSpace(line))
                        _history.Add(line);
                    _historyIndex = _history.Count;
                    _matches = null;
                    return line;

                case ConsoleKey.Backspace:
                    if (cursor > 0)
                    {
                        buffer.Remove(cursor - 1, 1);
                        cursor--;
                        ResetAutocomplete();
                        Redraw(buffer, cursor);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (cursor > 0)
                    {
                        cursor--;
                        Console.CursorLeft--;
                    }
                    break;

                case ConsoleKey.RightArrow:
                    if (cursor < buffer.Length)
                    {
                        cursor++;
                        Console.CursorLeft++;
                    }
                    break;

                case ConsoleKey.UpArrow:
                    if (_history.Count > 0 && _historyIndex > 0)
                    {
                        _historyIndex--;
                        buffer.Clear().Append(_history[_historyIndex]);
                        cursor = buffer.Length;
                        ResetAutocomplete();
                        Redraw(buffer, cursor);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (_historyIndex < _history.Count - 1)
                    {
                        _historyIndex++;
                        buffer.Clear().Append(_history[_historyIndex]);
                        cursor = buffer.Length;
                        ResetAutocomplete();
                        Redraw(buffer, cursor);
                    }
                    break;

                case ConsoleKey.Tab:
                    AutoComplete(buffer, ref cursor);
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(cursor, key.KeyChar);
                        cursor++;
                        ResetAutocomplete();
                        Redraw(buffer, cursor);
                    }
                    break;
            }
        }
    }

    void AutoComplete(StringBuilder buffer, ref int cursor)
    {
        string text = buffer.ToString();

        if (_matches == null)
        {
            _matches = _commands
                .Where(c => c.StartsWith(text, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _matchIndex = 0;

            if (_matches.Count == 0)
            {
                _matches = null;
                return;
            }

            //if (_matches.Count > 1)
            //{
            //    Console.WriteLine();
            //    Console.WriteLine(string.Join("   ", _matches));
            //    Console.Write("> " + buffer);
            //    Console.CursorLeft = 2 + cursor;
            //}
        }

        var match = _matches[_matchIndex];
        _matchIndex = (_matchIndex + 1) % _matches.Count;

        buffer.Clear().Append(match);
        cursor = buffer.Length;

        Redraw(buffer, cursor);
    }

    void ResetAutocomplete()
    {
        _matches = null;
    }

    void Redraw(StringBuilder buffer, int cursor)
    {
        lock (ConsoleSync.Lock)
        {
            Console.CursorVisible = false;
            Console.Write("\r\x1b[2K> " + buffer + " ");
            Console.CursorLeft = 2 + cursor;
            Console.CursorVisible = true;
        }

    }
}