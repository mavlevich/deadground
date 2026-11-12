using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class NetworkAddressValidatorTests
{
    [Theory]
    [InlineData("127.0.0.1:7777", "127.0.0.1", 7777)]
    [InlineData("127.0.0.1", "127.0.0.1", 7777)]
    [InlineData("localhost:7777", "localhost", 7777)]
    [InlineData("localhost", "localhost", 7777)]
    [InlineData("192.168.1.100:8000", "192.168.1.100", 8000)]
    [InlineData("server.local:9999", "server.local", 9999)]
    [InlineData("game.lan:1", "game.lan", 1)]
    [InlineData("game.lan:65535", "game.lan", 65535)]
    [InlineData("  127.0.0.1:7777  ", "127.0.0.1", 7777)]
    [InlineData("dedicated-host:7777", "dedicated-host", 7777)]
    public void ValidAddress_ParsesSuccessfully(string input, string expectedHost, int expectedPort)
    {
        bool success = NetworkAddressValidator.TryParse(input, out string host, out int port, out string? error);
        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespaceAddress_ReturnsError(string? input)
    {
        bool success = NetworkAddressValidator.TryParse(input, out _, out _, out string? error);
        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains("empty", error);
    }

    [Theory]
    [InlineData("127.0.0.1:0", "between 1 and 65535")]
    [InlineData("127.0.0.1:65536", "between 1 and 65535")]
    [InlineData("127.0.0.1:-1", "between 1 and 65535")]
    [InlineData("127.0.0.1:abc", "between 1 and 65535")]
    [InlineData("127.0.0.1:", "Port cannot be empty")]
    [InlineData(":7777", "Host name cannot be empty")]
    [InlineData("127.0.0.1:7777:8888", "Multiple colons")]
    [InlineData("[::1]:7777", "IPv6 addresses are not supported")]
    [InlineData("::1", "IPv6 addresses are not supported")]
    [InlineData("invalid host with spaces:7777", "invalid character")]
    [InlineData("-badhost:7777", "cannot start or end with a hyphen")]
    [InlineData("badhost-:7777", "cannot start or end with a hyphen")]
    [InlineData("bad..host:7777", "cannot have empty labels")]
    public void InvalidAddress_FailsWithDescriptiveError(string input, string expectedSubstring)
    {
        bool success = NetworkAddressValidator.TryParse(input, out _, out _, out string? error);
        Assert.False(success);
        Assert.NotNull(error);
        Assert.Contains(expectedSubstring, error);
    }
}
