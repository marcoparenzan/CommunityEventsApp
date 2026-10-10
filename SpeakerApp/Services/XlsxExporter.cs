using ClosedXML.Excel;

namespace SpeakerApp.Services;

public static class XlsxExporter
{
    public static byte[] Create(
        string sheetName,
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var safeName = new string(sheetName.Where(c => !"[]:*?/\\".Contains(c)).ToArray());
        if (safeName.Length == 0)
        {
            safeName = "Report";
        }

        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(safeName.Length > 31 ? safeName[..31] : safeName);
        for (var column = 0; column < headers.Count; column++)
        {
            var cell = sheet.Cell(1, column + 1);
            cell.Value = headers[column];
            cell.Style.Font.Bold = true;
        }

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < headers.Count && column < rows[row].Count; column++)
            {
                // Sempre testo: nessuna cella viene interpretata come formula.
                sheet.Cell(row + 2, column + 1).SetValue(rows[row][column]);
            }
        }

        sheet.SheetView.FreezeRows(1);
        if (rows.Count > 0)
        {
            sheet.Range(1, 1, rows.Count + 1, headers.Count).SetAutoFilter();
        }

        sheet.Columns().AdjustToContents(1, Math.Min(rows.Count + 1, 200));
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
