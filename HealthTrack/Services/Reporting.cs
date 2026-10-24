using System.Globalization;
using System.Text;
using HealthTrack.Models;

namespace HealthTrack.Services.Reporting;

// Minimal, dependency-free PDF writer for text reports (headings, paragraphs,
// key/value pairs and tables) on A4 pages using the standard Helvetica fonts.
public sealed class SimplePdfDocument
{
    private const float PageWidth = 595f;   // A4 in points
    private const float PageHeight = 842f;
    private const float Margin = 48f;
    private const float ContentWidth = PageWidth - 2 * Margin;

    private readonly string _title;
    private readonly List<StringBuilder> _pages = new();
    private StringBuilder _current = null!;
    private float _y;

    public SimplePdfDocument(string title, string? subtitle = null)
    {
        _title = title;
        NewPage();
        Text(Margin, _y, 20, bold: true, title, 0.09f, 0.13f, 0.20f);
        _y -= 22;
        Text(Margin, _y, 9, bold: false,
            (subtitle != null ? subtitle + "  |  " : string.Empty) + "Generated " + DateTime.Now.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture),
            0.40f, 0.44f, 0.52f);
        _y -= 10;
        Line(Margin, _y, PageWidth - Margin, _y, 0.85f);
        _y -= 18;
    }

    // Adds a section heading.
    public SimplePdfDocument Heading(string text)
    {
        EnsureSpace(34);
        _y -= 6;
        Text(Margin, _y, 13, bold: true, text, 0.15f, 0.39f, 0.92f);
        _y -= 18;
        return this;
    }

    // Adds word-wrapped body text.
    public SimplePdfDocument Paragraph(string text)
    {
        foreach (var line in Wrap(text, 10, ContentWidth))
        {
            EnsureSpace(14);
            Text(Margin, _y, 10, bold: false, line, 0.2f, 0.2f, 0.2f);
            _y -= 14;
        }
        _y -= 4;
        return this;
    }

    // Adds a two-column list of labels and values.
    public SimplePdfDocument KeyValues(IEnumerable<(string Key, string Value)> pairs)
    {
        foreach (var (key, value) in pairs)
        {
            EnsureSpace(15);
            Text(Margin, _y, 10, bold: true, key, 0.3f, 0.3f, 0.3f);
            Text(Margin + 170, _y, 10, bold: false, Fit(value, 10, ContentWidth - 170), 0.1f, 0.1f, 0.1f);
            _y -= 15;
        }
        _y -= 4;
        return this;
    }

    // Adds a table with a shaded header row; rows flow onto new pages automatically.
    public SimplePdfDocument Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var rowList = rows.ToList();
        var widths = ColumnWidths(headers, rowList);

        void DrawHeader()
        {
            EnsureSpace(20);
            Rect(Margin, _y - 5, ContentWidth, 17, 0.93f);
            var x = Margin + 3;
            for (var i = 0; i < headers.Count; i++)
            {
                Text(x, _y, 8.5f, bold: true, Fit(headers[i], 8.5f, widths[i] - 6), 0.2f, 0.2f, 0.2f);
                x += widths[i];
            }
            _y -= 17;
        }

        DrawHeader();
        if (rowList.Count == 0)
        {
            Text(Margin + 3, _y, 9, bold: false, "No data for this period.", 0.5f, 0.5f, 0.5f);
            _y -= 16;
        }

        foreach (var row in rowList)
        {
            if (_y < Margin + 20)
            {
                NewPage();
                DrawHeader();
            }
            var x = Margin + 3;
            for (var i = 0; i < headers.Count; i++)
            {
                var cell = i < row.Count ? row[i] : string.Empty;
                Text(x, _y, 8.5f, bold: false, Fit(cell, 8.5f, widths[i] - 6), 0.15f, 0.15f, 0.15f);
                x += widths[i];
            }
            _y -= 4;
            Line(Margin, _y, PageWidth - Margin, _y, 0.92f);
            _y -= 10;
        }
        _y -= 8;
        return this;
    }

    // Serialises the document to PDF 1.4 bytes.
    public byte[] Build()
    {
        var pdf = new StringBuilder();
        var offsets = new List<int>();
        pdf.Append("%PDF-1.4\n");

        void BeginObject(int number)
        {
            while (offsets.Count < number) offsets.Add(0);
            offsets[number - 1] = pdf.Length;
            pdf.Append(number).Append(" 0 obj\n");
        }

        // Object layout: 1 catalog, 2 pages, 3 Helvetica, 4 Helvetica-Bold, then (page, content) pairs.
        var pageCount = _pages.Count;
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{5 + i * 2} 0 R"));

        BeginObject(1);
        pdf.Append("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject(2);
        pdf.Append($"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");
        BeginObject(3);
        pdf.Append("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj\n");
        BeginObject(4);
        pdf.Append("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            var pageObj = 5 + i * 2;
            var contentObj = pageObj + 1;

            var content = new StringBuilder(_pages[i].ToString());
            // Footer with page numbers.
            AppendText(content, Margin, 26, 8, false, $"{_title}  -  page {i + 1} of {pageCount}", 0.55f, 0.55f, 0.55f);
            var stream = content.ToString();

            BeginObject(pageObj);
            pdf.Append($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(PageWidth)} {F(PageHeight)}] ")
               .Append("/Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> ")
               .Append($"/Contents {contentObj} 0 R >>\nendobj\n");

            BeginObject(contentObj);
            pdf.Append($"<< /Length {stream.Length} >>\nstream\n").Append(stream).Append("\nendstream\nendobj\n");
        }

        var xrefOffset = pdf.Length;
        pdf.Append("xref\n").Append($"0 {offsets.Count + 1}\n").Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");

        // Every character written is ASCII, so string length equals byte length (offsets stay valid).
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    // ------------------------------------------------------------------ drawing primitives

    private void NewPage()
    {
        _current = new StringBuilder();
        _pages.Add(_current);
        _y = PageHeight - Margin - 10;
    }

    private void EnsureSpace(float needed)
    {
        if (_y - needed < Margin + 10)
            NewPage();
    }

    private void Text(float x, float y, float size, bool bold, string text, float r, float g, float b) =>
        AppendText(_current, x, y, size, bold, text, r, g, b);

    private static void AppendText(StringBuilder sb, float x, float y, float size, bool bold, string text, float r, float g, float b)
    {
        sb.Append($"{F(r)} {F(g)} {F(b)} rg BT /{(bold ? "F2" : "F1")} {F(size)} Tf {F(x)} {F(y)} Td ({Escape(text)}) Tj ET\n");
    }

    private void Line(float x1, float y1, float x2, float y2, float gray) =>
        _current.Append($"{F(gray)} G 0.6 w {F(x1)} {F(y1)} m {F(x2)} {F(y2)} l S\n");

    private void Rect(float x, float y, float w, float h, float gray) =>
        _current.Append($"{F(gray)} g {F(x)} {F(y)} {F(w)} {F(h)} re f\n");

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    // Escapes PDF string delimiters and replaces non-ASCII characters.
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '(': sb.Append("\\("); break;
                case ')': sb.Append("\\)"); break;
                case '–': case '—': sb.Append('-'); break;
                case '’': case '‘': sb.Append('\''); break;
                case '“': case '”': sb.Append('"'); break;
                case '…': sb.Append("..."); break;
                default:
                    sb.Append(c >= 32 && c < 127 ? c : '?');
                    break;
            }
        }
        return sb.ToString();
    }

    // Approximate Helvetica width (average glyph ≈ 0.52 em).
    private static float TextWidth(string text, float size) => text.Length * size * 0.52f;

    private static string Fit(string text, float size, float maxWidth)
    {
        if (TextWidth(text, size) <= maxWidth) return text;
        var maxChars = Math.Max(1, (int)(maxWidth / (size * 0.52f)) - 3);
        return text.Substring(0, Math.Min(text.Length, maxChars)) + "...";
    }

    private static IEnumerable<string> Wrap(string text, float size, float maxWidth)
    {
        var maxChars = Math.Max(10, (int)(maxWidth / (size * 0.52f)));
        foreach (var paragraph in text.Replace("\r", string.Empty).Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length + word.Length + 1 > maxChars && line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            yield return line.ToString();
        }
    }

    private static float[] ColumnWidths(IReadOnlyList<string> headers, List<IReadOnlyList<string>> rows)
    {
        // Weight each column by its longest content (capped) so wide text gets more room.
        var weights = new float[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            var longest = headers[i].Length;
            foreach (var row in rows.Take(200))
                if (i < row.Count) longest = Math.Max(longest, row[i]?.Length ?? 0);
            weights[i] = Math.Clamp(longest, 4, 40);
        }
        var total = weights.Sum();
        return weights.Select(w => ContentWidth * w / total).ToArray();
    }
}

// RFC 4180 CSV writer with protection against spreadsheet formula injection.
public static class CsvBuilder
{
    // Writes a valid PDF file (CSV/ICS variants return UTF-8 bytes).
    public static byte[] Build(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", row.Select(Escape)));

        // UTF-8 with BOM so Excel opens accented characters correctly.
        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        return preamble.Concat(body).ToArray();
    }

    private static string Escape(string? value)
    {
        value ??= string.Empty;

        // Cells that start with = + - @ are executed as formulas by spreadsheet apps.
        var isNumber = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _);
        if (!isNumber && value.Length > 0 && "=+-@".Contains(value[0]))
            value = "'" + value;

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            value = "\"" + value.Replace("\"", "\"\"") + "\"";
        return value;
    }
}

// Creates an iCalendar (.ics) file so an appointment can be added to any calendar app.
public static class IcsBuilder
{
    // Writes a valid PDF file (CSV/ICS variants return UTF-8 bytes).
    public static byte[] Build(Appointment appointment, string summary, string description, string organizerName)
    {
        static string Utc(DateTime local) => local.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        static string Esc(string value) => value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");

        var lines = new[]
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//HealthTrack//Appointments//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:healthtrack-appointment-{appointment.Id}@healthtrack.local",
            $"DTSTAMP:{Utc(DateTime.Now)}",
            $"DTSTART:{Utc(appointment.StartTime)}",
            $"DTEND:{Utc(appointment.EndTime)}",
            $"SUMMARY:{Esc(summary)}",
            $"DESCRIPTION:{Esc(description)}",
            $"ORGANIZER;CN={Esc(organizerName)}:mailto:no-reply@healthtrack.local",
            "STATUS:" + (appointment.Status == AppointmentStatus.Approved ? "CONFIRMED" : "TENTATIVE"),
            "BEGIN:VALARM",
            "TRIGGER:-PT1H",
            "ACTION:DISPLAY",
            "DESCRIPTION:Appointment reminder",
            "END:VALARM",
            "END:VEVENT",
            "END:VCALENDAR"
        };
        return Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n");
    }
}
