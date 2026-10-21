using System.Net;

namespace Leistd.ServiceClient.Tests.TestDoubles;

internal sealed class ThrowingHttpContent(Exception error) : HttpContent
{
    public bool IsDisposed { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.FromException(error);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
