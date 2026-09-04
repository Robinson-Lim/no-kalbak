namespace DnfItemChecker.Core.Ocr;

/// <summary>One OCR'd text line with its bounding box, in the input image's pixel space.</summary>
public sealed record OcrLine(string Text, double Left, double Top, double Width, double Height);
