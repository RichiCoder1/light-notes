using System.Diagnostics;

namespace LightNotes;

/// <summary>Opens a validated web link through the platform's registered handler.</summary>
public interface IExternalLinkOpener
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>Uses the operating system's registered HTTP or HTTPS handler.</summary>
public sealed class SystemExternalLinkOpener : IExternalLinkOpener
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        Validate(uri);
        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        return Task.CompletedTask;
    }

    internal static void Validate(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException(
                "Only complete HTTP or HTTPS links can be opened.",
                nameof(uri)
            );
    }
}
