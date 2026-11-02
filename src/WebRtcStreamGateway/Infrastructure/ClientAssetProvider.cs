using System.Reflection;

namespace WebRtcStreamGateway.Infrastructure;

public static class ClientAssetProvider
{
    private static readonly Assembly CurrentAssembly = typeof(ClientAssetProvider).Assembly;

    public static byte[]? GetAssetBytes(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return null;

        string localPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", fileName);
        if (File.Exists(localPath))
            return File.ReadAllBytes(localPath);

        string devPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", fileName);
        if (File.Exists(devPath))
            return File.ReadAllBytes(devPath);

        string srcPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "WebRtcStreamGateway", "wwwroot", fileName);
        if (File.Exists(srcPath))
            return File.ReadAllBytes(srcPath);

        string resourceName = $"WebRtcStreamGateway.wwwroot.{fileName}";
        using Stream? stream = CurrentAssembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            return null;

        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        return memoryStream.ToArray();
    }

    public static string GetContentType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();
        return extension switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
    }
}
