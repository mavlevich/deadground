using DeadGround.Engine;
using DeadGround.Engine.Net;
using Xunit;

namespace DeadGround.Engine.Tests;

public sealed class ClientLifecycleControllerTests
{
    private static MatchSetup CreateDummySetup() =>
        new(MatchSetup.CurrentProtocolVersion, 1234UL, 5678UL, "Battle");

    [Fact]
    public void InitialState_IsInactive()
    {
        using ClientLifecycleController controller = new();
        Assert.Equal(ClientLifecycleState.Inactive, controller.State);
        Assert.False(controller.IsConnecting);
        Assert.Null(controller.NetworkSession);
        Assert.Null(controller.LastError);
    }

    [Fact]
    public void InvalidAddress_FailsImmediately_WithoutLaunchingSession()
    {
        bool factoryCalled = false;
        using ClientLifecycleController controller = new((host, port, setup) =>
        {
            factoryCalled = true;
            throw new InvalidOperationException("Factory should not be invoked on invalid address");
        });

        bool result = controller.TryConnect("127.0.0.1:99999", CreateDummySetup(), out string? error);
        Assert.False(result);
        Assert.False(factoryCalled);
        Assert.Equal(ClientLifecycleState.Failed, controller.State);
        Assert.NotNull(error);
        Assert.Contains("between 1 and 65535", error);
    }

    [Fact]
    public void ReentrantConnect_WhileConnecting_IsRejected()
    {
        (ITransport clientTransport, _) = InMemoryTransport.CreatePair();
        using ClientLifecycleController controller = new((host, port, setup) =>
            new ClientSession(clientTransport, setup));

        bool first = controller.TryConnect("127.0.0.1:7777", CreateDummySetup(), out string? firstError);
        Assert.True(first);
        Assert.Null(firstError);
        Assert.True(controller.IsConnecting);

        bool second = controller.TryConnect("127.0.0.1:7777", CreateDummySetup(), out string? secondError);
        Assert.False(second);
        Assert.NotNull(secondError);
        Assert.Contains("already in progress", secondError);
    }

    [Fact]
    public void Cancel_AbortsPendingConnection_AndIncrementsAttemptId()
    {
        (ITransport clientTransport, _) = InMemoryTransport.CreatePair();
        using ClientLifecycleController controller = new((host, port, setup) =>
            new ClientSession(clientTransport, setup));

        int initialAttempt = controller.ConnectionAttemptId;
        controller.TryConnect("127.0.0.1:7777", CreateDummySetup(), out _);
        int connectingAttempt = controller.ConnectionAttemptId;
        Assert.True(connectingAttempt > initialAttempt);

        controller.Cancel();
        Assert.Equal(ClientLifecycleState.Inactive, controller.State);
        Assert.Null(controller.NetworkSession);
        Assert.True(controller.ConnectionAttemptId > connectingAttempt);
        Assert.Contains("cancelled", controller.StatusDescription);
    }

    [Theory]
    [InlineData("Connection rejected: ServerFull", "Server is full (maximum 2 players).")]
    [InlineData("Connection rejected: ProtocolVersionMismatch", "Protocol version mismatch with server. Server requires protocol v23.")]
    [InlineData("Connection rejected: ContentMismatch", "Game content mismatch with server.")]
    [InlineData("Connection rejected: MapMismatch", "Map mismatch with server.")]
    [InlineData("Connection rejected: RuleSetMismatch", "Rule set mismatch with server.")]
    [InlineData("Connection timed out", "Connection timed out.")]
    [InlineData("Connection closed by server", "Connection closed by server.")]
    [InlineData("Custom unmapped error", "Custom unmapped error")]
    public void TranslateFailureReason_TranslatesKnownErrors(string input, string expected)
    {
        string translated = ClientLifecycleController.TranslateFailureReason(input);
        Assert.Equal(expected, translated);
    }

    [Theory]
    [InlineData("--scenario", true)]
    [InlineData("--connect", true)]
    [InlineData("--smoke-test", true)]
    [InlineData("--map-preview", true)]
    [InlineData("--los-sandbox", true)]
    [InlineData("--vehicle-preview", true)]
    [InlineData("--perf-microbench", true)]
    [InlineData("--combat-feedback-probe", true)]
    [InlineData("--combat-feedback-capture", true)]
    [InlineData("--vehicle-effects-capture", true)]
    [InlineData("--order-preview-capture", true)]
    [InlineData("--minimap-demo", true)]
    public void HasExplicitCliMode_DetectsAllExplicitFlags(string flag, bool expected)
    {
        Assert.Equal(expected, ClientLifecycleController.HasExplicitCliMode([flag], []));
        Assert.Equal(expected, ClientLifecycleController.HasExplicitCliMode([], [flag]));
    }

    [Fact]
    public void HasExplicitCliMode_ReturnsFalseForNormalLaunch()
    {
        Assert.False(ClientLifecycleController.HasExplicitCliMode([], []));
        Assert.False(ClientLifecycleController.HasExplicitCliMode(null, null));
        Assert.False(ClientLifecycleController.HasExplicitCliMode([], ["/path/to/Godot", "--path", "src/Game.Godot"]));
        Assert.False(ClientLifecycleController.HasExplicitCliMode(["--screenshot", "test.png"], []));
        Assert.False(ClientLifecycleController.HasExplicitCliMode(["--screenshot-on-finish", "test.png"], []));
    }
}
