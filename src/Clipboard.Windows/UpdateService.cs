using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace Clipboard.Windows;

internal static class UpdateService
{
    // Supplied by the Windows release build, never inferred from the Mac repository.
    internal static string Repository => Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(value => value.Key == "UpdateRepository")?.Value ?? "";
    internal static bool Configured => System.Text.RegularExpressions.Regex.IsMatch(Repository, @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };

    internal static async Task<(string Message, Uri? Release)> Check()
    {
        if (!Configured) return ("This build has no Windows release repository configured. Update checks become available when a Windows release repository is supplied at build time.", null);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("Clipboard-Windows/1.0");
        using var response = await Client.SendAsync(request); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string tag = json.RootElement.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v'), out var available)) return ("The latest release uses an unsupported version format.", null);
        Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        return available > new Version(current.Major, current.Minor, current.Build)
            ? ($"Clipboard {available} is available. Open the Windows release page to download it?", new Uri($"https://github.com/{Repository}/releases/latest"))
            : ("You have the latest released version.", null);
    }
}
