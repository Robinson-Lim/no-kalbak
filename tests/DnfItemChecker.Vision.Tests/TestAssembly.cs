using Xunit;

// Several test classes create synthetic images through the process-wide GDI+ codec table.
// Keep classes sequential; OcrConcurrencyTests still exercises concurrent OCR calls explicitly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
