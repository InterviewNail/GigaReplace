using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Tokens;

namespace GigaReplace.Core.Pdf;

/// <summary>Resolves indirect objects and decodes streams of an open PDF.</summary>
internal sealed class PdfObjects(PdfDocument document)
{
    public PdfDocument Document { get; } = document;

    public IToken? Get(IndirectReference reference)
    {
        try
        {
            return Document.Structure.GetObject(reference)?.Data;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    public T? Resolve<T>(IToken? token) where T : class, IToken
    {
        for (int hops = 0; token is IndirectReferenceToken reference && hops < 32; hops++)
        {
            token = Get(reference.Data);
        }

        return token as T;
    }

    public T? Resolve<T>(DictionaryToken? dictionary, NameToken key) where T : class, IToken =>
        dictionary is not null && dictionary.Data.TryGetValue(key.Data, out var token) ? Resolve<T>(token) : null;

    public byte[] Decode(StreamToken stream) =>
        stream.Decode(Document.Structure.FilterProvider, Document.Structure.TokenScanner).ToArray();
}

/// <summary>A 2-D affine matrix in PDF's row-vector convention.</summary>
internal readonly record struct PdfMatrix(double A, double B, double C, double D, double E, double F)
{
    public static PdfMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);

    public static PdfMatrix Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    /// <summary>This matrix applied first, then <paramref name="m"/>.</summary>
    public PdfMatrix Then(PdfMatrix m) => new(
        A * m.A + B * m.C,
        A * m.B + B * m.D,
        C * m.A + D * m.C,
        C * m.B + D * m.D,
        E * m.A + F * m.C + m.E,
        E * m.B + F * m.D + m.F);

    public double VerticalScale => Math.Sqrt(C * C + D * D);
}
