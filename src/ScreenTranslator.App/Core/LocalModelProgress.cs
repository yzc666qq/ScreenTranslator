namespace ScreenTranslator.App.Core;

public sealed record LocalModelProgress(
    string Title,
    string Detail,
    long BytesReceived = 0,
    long TotalBytes = 0)
{
    public double Percentage => TotalBytes > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1)
        : 0;
}
