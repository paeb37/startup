namespace Dexter.WebApi.Decks.Models;

using System;
using System.Collections.Generic;

public sealed record InitialDeckArtifacts(
    string BaseName,
    string PptxStoragePath,
    string PdfFileName,
    byte[] PdfBytes,
    Dictionary<int, List<string>> ImageCaptions);

public sealed record RedactedArtifacts(
    string PptxPath,
    string PdfPath,
    string JsonPath,
    Dictionary<int, List<string>> ImageCaptions);
