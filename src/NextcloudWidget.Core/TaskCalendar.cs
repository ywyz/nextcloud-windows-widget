using System.Globalization;
using System.Text;

namespace NextcloudWidget.Core;

// Edit direct properties of the master VTODO. Preserve alarms, other components,
// recurrence overrides, time zones and extensions when rewriting the resource.
internal static class TaskCalendar
{
    internal static TaskItem Parse(string id, TaskList list, string ics)
    {
        var todo = new Todo(ics);
        return new TaskItem(id, Unescape(todo.Value("SUMMARY")), list.Name, Unescape(todo.Value("DESCRIPTION")),
            todo.Due(), todo.IsComplete, list.CanWrite, "synced", list.Id);
    }

    internal static string Create(string title, string description, DateTimeOffset? due)
    {
        string seed = string.Join("\r\n", "BEGIN:VCALENDAR", "VERSION:2.0",
            "PRODID:-//Nextcloud Windows Widget//Tasks//EN", "BEGIN:VTODO",
            "UID:" + Guid.NewGuid().ToString("D"), "DTSTAMP:" + Utc(DateTimeOffset.UtcNow),
            "STATUS:NEEDS-ACTION", "END:VTODO", "END:VCALENDAR");
        return Edit(seed, title, description, due);
    }

    internal static string Edit(string ics, string title, string description, DateTimeOffset? due)
    {
        var todo = new Todo(ics);
        todo.Set("SUMMARY", Escape(title));
        todo.Set("DESCRIPTION", description.Length == 0 ? null : Escape(description));
        if (due != todo.Due())
        {
            if (due != null) todo.Set("DURATION", null);
            todo.Set("DUE", due == null ? null : Utc(due.Value), replaceParameters: true);
        }
        return todo.Save();
    }

    internal static string SetCompleted(string ics, bool completed)
    {
        var todo = new Todo(ics);
        if (todo.IsComplete == completed) return ics;
        todo.Set("STATUS", completed ? "COMPLETED" : "NEEDS-ACTION");
        todo.Set("PERCENT-COMPLETE", completed ? "100" : "0");
        todo.Set("COMPLETED", completed ? Utc(DateTimeOffset.UtcNow) : null, replaceParameters: true);
        return todo.Save();
    }

    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\r\n", "\n")
        .Replace('\r', '\n').Replace("\n", "\\n").Replace(";", "\\;").Replace(",", "\\,");
    private static string Unescape(string text)
    {
        var output = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length) output.Append(text[i]);
            else
            {
                char next = text[++i];
                output.Append(next is 'n' or 'N' ? '\n' : next);
            }
        }
        return output.ToString();
    }

    private sealed class Todo
    {
        private readonly List<string> lines = [];
        private readonly int start;
        private int end;

        internal Todo(string ics)
        {
            using var reader = new StringReader(ics);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length > 0 && line[0] is ' ' or '\t' && lines.Count > 0)
                    lines[^1] += line[1..];
                else if (line.Length != 0) lines.Add(line);
            }
            int first = -1, firstEnd = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!lines[i].Equals("BEGIN:VTODO", StringComparison.OrdinalIgnoreCase)) continue;
                int last = lines.FindIndex(i + 1, s => s.Equals("END:VTODO", StringComparison.OrdinalIgnoreCase));
                if (last < 0) throw new InvalidDataException("任务数据缺少 VTODO 结束标记。");
                if (first < 0) (first, firstEnd) = (i, last);
                (start, end) = (i, last);
                if (Find("RECURRENCE-ID") < 0) return;
                i = last;
            }
            if (first < 0) throw new InvalidDataException("没有找到 VTODO 任务。");
            (start, end) = (first, firstEnd);
        }

        // Colons inside quoted parameter values (e.g. ALTREP) are not separators.
        private static int Separator(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') quoted = !quoted;
                if (!quoted && line[i] == ':') return i;
            }
            return -1;
        }

        private int Find(string name)
        {
            int depth = 0;
            for (int i = start + 1; i < end; i++)
            {
                var line = lines[i];
                if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
                if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase)) { depth--; continue; }
                if (depth == 0 && line.Length > name.Length && line[name.Length] is ':' or ';' &&
                    line.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        internal string Value(string name)
        {
            int index = Find(name);
            if (index < 0) return "";
            int colon = Separator(lines[index]);
            return colon < 0 ? "" : lines[index][(colon + 1)..];
        }

        internal bool IsComplete => Value("STATUS").Equals("COMPLETED", StringComparison.OrdinalIgnoreCase) ||
            Value("COMPLETED").Length > 0 || Value("PERCENT-COMPLETE") == "100";

        internal DateTimeOffset? Due()
        {
            int index = Find("DUE");
            if (index < 0) return null;
            string value = Value("DUE");
            bool utc = value.EndsWith('Z');
            if (!DateTime.TryParseExact(utc ? value[..^1] : value, ["yyyyMMdd", "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return null;
            if (utc) return new DateTimeOffset(local, TimeSpan.Zero);
            string header = lines[index][..Separator(lines[index])];
            var zone = TimeZoneInfo.Local;
            foreach (var parameter in header.Split(';').Skip(1))
            {
                if (!parameter.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase)) continue;
                if (TimeZoneInfo.TryFindSystemTimeZoneById(parameter[5..].Trim('"'), out var found)) zone = found;
                break;
            }
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }

        internal void Set(string name, string? value, bool replaceParameters = false)
        {
            int i = Find(name);
            if (value == null)
            {
                if (i >= 0) { lines.RemoveAt(i); end--; }
                return;
            }
            if (i < 0) { lines.Insert(end++, name + ":" + value); return; }
            int colon = Separator(lines[i]);
            string header = replaceParameters || colon < 0 ? name : lines[i][..colon];
            lines[i] = header + ":" + value;
        }

        internal string Save()
        {
            var stamp = Utc(DateTimeOffset.UtcNow);
            Set("DTSTAMP", stamp, replaceParameters: true);
            Set("LAST-MODIFIED", stamp, replaceParameters: true);
            var output = new StringBuilder();
            foreach (string line in lines)
            {
                int bytes = 0;
                foreach (var rune in line.EnumerateRunes())
                {
                    if (bytes + rune.Utf8SequenceLength > 75) { output.Append("\r\n "); bytes = 1; }
                    output.Append(rune.ToString());
                    bytes += rune.Utf8SequenceLength;
                }
                output.Append("\r\n");
            }
            return output.ToString();
        }
    }
}
