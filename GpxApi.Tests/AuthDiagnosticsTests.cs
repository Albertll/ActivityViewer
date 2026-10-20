using System.Net.Sockets;
using System.Security.Cryptography;

namespace GpxApi.Tests;

public class AuthDiagnosticsTests
{
    [Fact]
    public void DescribeException_PokazujeTypyIKody_BezTresciKomunikatow()
    {
        var ex = new HttpRequestException("No such host is known (tajny-host.example:443)",
            new SocketException((int)SocketError.HostNotFound));

        var text = StravaController.DescribeException(ex);

        Assert.Equal("HttpRequestException <- SocketException HostNotFound", text);
        Assert.DoesNotContain("tajny-host", text);
    }

    [Fact]
    public void DescribeException_CryptographicException_PokazujeHResult()
    {
        var ex = new InvalidOperationException("zewnętrzny", new CryptographicException("wewnętrzny"));

        var text = StravaController.DescribeException(ex);

        Assert.StartsWith("InvalidOperationException <- CryptographicException 0x", text);
    }
}
