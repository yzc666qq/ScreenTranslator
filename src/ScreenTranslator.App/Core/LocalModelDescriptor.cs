namespace ScreenTranslator.App.Core;

public sealed record LocalModelDescriptor(
    string FileName,
    Uri DownloadUri,
    long FileSize,
    string Sha256)
{
    public static LocalModelDescriptor Qwen25HalfBillionQ4Km { get; } = new(
        "qwen2.5-0.5b-instruct-q4_k_m.gguf",
        new Uri(
            "https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/" +
            "9217f5db79a29953eb74d5343926648285ec7e67/" +
            "qwen2.5-0.5b-instruct-q4_k_m.gguf?download=true"),
        491_400_032,
        "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db");
}
