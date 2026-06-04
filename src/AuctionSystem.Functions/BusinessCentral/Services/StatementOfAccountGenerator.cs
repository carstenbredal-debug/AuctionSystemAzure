using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace AuctionSystem.Functions.BusinessCentral.Services;

public class StatementEntry
{
    public string PostingDate { get; set; } = "";
    public string DocumentType { get; set; } = "";
    public string DocumentNo { get; set; } = "";
    public string Description { get; set; } = "";
    public decimal OriginalAmount { get; set; }
    public decimal RemainingAmount { get; set; }
    public bool Open { get; set; }
}

public class StatementOfAccountGenerator
{
    public byte[] Generate(string customerNo, string customerName, string address, List<StatementEntry> entries, DateTime statementDate)
    {
        QuestPDF.Settings.License = LicenseType.Community;

        var sortedEntries = entries.OrderBy(e => e.PostingDate).ToList();
        decimal runningBalance = 0;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginTop(1.5f, Unit.Centimetre);
                page.MarginBottom(1.5f, Unit.Centimetre);
                page.MarginLeft(2, Unit.Centimetre);
                page.MarginRight(2, Unit.Centimetre);

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text("KOPENHAGEN FUR").FontSize(16).Bold();
                            c.Item().Text("Statement of Account").FontSize(12).SemiBold();
                        });
                        row.RelativeItem().AlignRight().Column(c =>
                        {
                            c.Item().Text($"Date: {statementDate:dd/MM/yyyy}").FontSize(9);
                            c.Item().Text($"Customer No: {customerNo}").FontSize(9);
                        });
                    });

                    col.Item().PaddingTop(10).Column(c =>
                    {
                        c.Item().Text(customerName).FontSize(10).Bold();
                        if (!string.IsNullOrEmpty(address))
                            c.Item().Text(address).FontSize(9);
                    });

                    col.Item().PaddingTop(15).LineHorizontal(1);
                });

                page.Content().PaddingTop(10).Table(table =>
                {
                    table.ColumnsDefinition(columns =>
                    {
                        columns.ConstantColumn(70);   // Date
                        columns.ConstantColumn(75);   // Type
                        columns.ConstantColumn(85);   // Document No
                        columns.RelativeColumn();     // Description
                        columns.ConstantColumn(75);   // Amount
                        columns.ConstantColumn(75);   // Balance
                    });

                    // Header
                    table.Header(header =>
                    {
                        header.Cell().Background("#95958F").Padding(4).Text("Date").FontSize(8).Bold().FontColor(Colors.White);
                        header.Cell().Background("#95958F").Padding(4).Text("Type").FontSize(8).Bold().FontColor(Colors.White);
                        header.Cell().Background("#95958F").Padding(4).Text("Document No.").FontSize(8).Bold().FontColor(Colors.White);
                        header.Cell().Background("#95958F").Padding(4).Text("Description").FontSize(8).Bold().FontColor(Colors.White);
                        header.Cell().Background("#95958F").Padding(4).AlignRight().Text("Amount").FontSize(8).Bold().FontColor(Colors.White);
                        header.Cell().Background("#95958F").Padding(4).AlignRight().Text("Balance").FontSize(8).Bold().FontColor(Colors.White);
                    });

                    // Rows
                    foreach (var entry in sortedEntries)
                    {
                        runningBalance += entry.OriginalAmount;
                        var docType = entry.DocumentType.Replace("_x0020_", " ");
                        var isCreditNote = docType.Contains("Credit", StringComparison.OrdinalIgnoreCase);
                        var bgColor = isCreditNote ? "#9CC6C8" : "#E8E3DD";

                        table.Cell().Background(bgColor).Padding(4).Text(entry.PostingDate).FontSize(8);
                        table.Cell().Background(bgColor).Padding(4).Text(docType).FontSize(8);
                        table.Cell().Background(bgColor).Padding(4).Text(entry.DocumentNo).FontSize(8);
                        table.Cell().Background(bgColor).Padding(4).Text(entry.Description).FontSize(8);
                        table.Cell().Background(bgColor).Padding(4).AlignRight().Text($"€{entry.OriginalAmount:N2}").FontSize(8);
                        table.Cell().Background(bgColor).Padding(4).AlignRight().Text($"€{runningBalance:N2}").FontSize(8).Bold();
                    }
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.Span("Page ").FontSize(8);
                    text.CurrentPageNumber().FontSize(8);
                    text.Span(" of ").FontSize(8);
                    text.TotalPages().FontSize(8);
                });
            });
        });

        return document.GeneratePdf();
    }
}
