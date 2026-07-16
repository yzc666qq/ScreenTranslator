namespace ScreenTranslator.App.Core;

public sealed record ScreenCaptureFrame(
    int Width,
    int Height,
    int Stride,
    ReadOnlyMemory<byte> Pixels);
