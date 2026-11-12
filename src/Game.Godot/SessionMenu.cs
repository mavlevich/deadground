using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal sealed partial class SessionMenu : CanvasLayer
{
    private readonly Control _backdrop;
    private readonly PanelContainer _mainCard;
    private readonly PanelContainer _multiplayerCard;
    private readonly PanelContainer _disconnectCard;

    private readonly LineEdit _addressInput;
    private readonly Label _statusLabel;
    private readonly Button _connectBtn;
    private readonly Button _cancelBtn;
    private readonly Button _backBtn;

    private readonly Label _disconnectReason;

    public event Action? LocalBattleRequested;
    public event Action<string>? ConnectRequested;
    public event Action? CancelRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action? DisconnectModalDismissed;

    public bool IsOpen => Visible && (_mainCard.Visible || _multiplayerCard.Visible || _disconnectCard.Visible);
    public bool IsMainMenuView => Visible && _mainCard.Visible;
    public bool IsMultiplayerView => Visible && _multiplayerCard.Visible;
    public bool IsDisconnectModal => Visible && _disconnectCard.Visible;
    public string AddressText => _addressInput.Text;
    public string StatusText => _statusLabel.Text;

    public bool HandleEscape()
    {
        if (_disconnectCard.Visible)
        {
            _disconnectCard.Visible = false;
            ShowMultiplayerView();
            DisconnectModalDismissed?.Invoke();
            return true;
        }

        if (_multiplayerCard.Visible)
        {
            ShowMainMenuView();
            return true;
        }

        return false;
    }

    public SessionMenu()
    {
        Layer = 8;

        _backdrop = new Control
        {
            Visible = true,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        _backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_backdrop);

        // 1. Main menu card
        _mainCard = CreateCard(new Vector2(400, 320));
        VBoxContainer mainStack = new();
        mainStack.AddThemeConstantOverride("separation", 14);

        Label mainTitle = new()
        {
            Text = "DEAD GROUND",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        mainTitle.AddThemeFontSizeOverride("font_size", 28);
        mainTitle.AddThemeColorOverride("font_color", new Color("d5ded0"));
        mainStack.AddChild(mainTitle);

        Label mainSubtitle = new()
        {
            Text = "COLD WAR TACTICAL SIMULATOR",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        mainSubtitle.AddThemeFontSizeOverride("font_size", 12);
        mainSubtitle.AddThemeColorOverride("font_color", new Color("8a9a88"));
        mainStack.AddChild(mainSubtitle);

        Control spacer1 = new() { CustomMinimumSize = new Vector2(0, 10) };
        mainStack.AddChild(spacer1);

        Button localBattleBtn = CreateButton("LOCAL BATTLE");
        localBattleBtn.Pressed += () => LocalBattleRequested?.Invoke();
        mainStack.AddChild(localBattleBtn);

        Button multiplayerBtn = CreateButton("MULTIPLAYER");
        multiplayerBtn.Pressed += ShowMultiplayerView;
        mainStack.AddChild(multiplayerBtn);

        Button settingsBtn = CreateButton("SETTINGS [F10]");
        settingsBtn.Pressed += () => SettingsRequested?.Invoke();
        mainStack.AddChild(settingsBtn);

        Button exitBtn = CreateButton("EXIT");
        exitBtn.Pressed += () => ExitRequested?.Invoke();
        mainStack.AddChild(exitBtn);

        WrapInMargin(_mainCard, mainStack);
        AddChild(_mainCard);

        // 2. Multiplayer connection card
        _multiplayerCard = CreateCard(new Vector2(440, 340));
        _multiplayerCard.Visible = false;

        VBoxContainer mpStack = new();
        mpStack.AddThemeConstantOverride("separation", 12);

        Label mpTitle = new()
        {
            Text = "MULTIPLAYER",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        mpTitle.AddThemeFontSizeOverride("font_size", 24);
        mpTitle.AddThemeColorOverride("font_color", new Color("edc76b"));
        mpStack.AddChild(mpTitle);

        Label mpSubtitle = new()
        {
            Text = "Connect to Dedicated Server",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        mpSubtitle.AddThemeFontSizeOverride("font_size", 12);
        mpSubtitle.AddThemeColorOverride("font_color", new Color("8a9a88"));
        mpStack.AddChild(mpSubtitle);

        VBoxContainer fieldBox = new();
        fieldBox.AddThemeConstantOverride("separation", 4);

        Label addrLabel = new() { Text = "SERVER ADDRESS" };
        addrLabel.AddThemeFontSizeOverride("font_size", 11);
        addrLabel.AddThemeColorOverride("font_color", new Color("a8b8a0"));
        fieldBox.AddChild(addrLabel);

        _addressInput = new LineEdit
        {
            Text = "127.0.0.1:7777",
            PlaceholderText = "127.0.0.1:7777",
            CustomMinimumSize = new Vector2(0, 36)
        };
        _addressInput.AddThemeStyleboxOverride("normal", LineEditStyle());
        _addressInput.AddThemeStyleboxOverride("focus", LineEditStyle(focused: true));
        _addressInput.AddThemeFontSizeOverride("font_size", 14);
        _addressInput.AddThemeColorOverride("font_color", new Color("ffffff"));
        _addressInput.TextSubmitted += _ => OnConnectPressed();
        fieldBox.AddChild(_addressInput);
        mpStack.AddChild(fieldBox);

        _statusLabel = new Label
        {
            Text = "Ready to connect.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 32)
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", new Color("8a9a88"));
        mpStack.AddChild(_statusLabel);

        HBoxContainer buttonRow = new();
        buttonRow.AddThemeConstantOverride("separation", 8);

        _connectBtn = CreateButton("CONNECT");
        _connectBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _connectBtn.Pressed += OnConnectPressed;
        buttonRow.AddChild(_connectBtn);

        _cancelBtn = CreateButton("CANCEL");
        _cancelBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _cancelBtn.Disabled = true;
        _cancelBtn.Pressed += () => CancelRequested?.Invoke();
        buttonRow.AddChild(_cancelBtn);

        _backBtn = CreateButton("BACK");
        _backBtn.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _backBtn.Pressed += ShowMainMenuView;
        buttonRow.AddChild(_backBtn);

        mpStack.AddChild(buttonRow);

        WrapInMargin(_multiplayerCard, mpStack);
        AddChild(_multiplayerCard);

        // 3. Disconnect / Connection lost modal card
        _disconnectCard = CreateCard(new Vector2(400, 220));
        _disconnectCard.Visible = false;

        VBoxContainer dcStack = new();
        dcStack.AddThemeConstantOverride("separation", 14);

        Label dcTitle = new()
        {
            Text = "CONNECTION LOST",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        dcTitle.AddThemeFontSizeOverride("font_size", 22);
        dcTitle.AddThemeColorOverride("font_color", new Color("ee6666"));
        dcStack.AddChild(dcTitle);

        _disconnectReason = new Label
        {
            Text = "Disconnected from server.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _disconnectReason.AddThemeFontSizeOverride("font_size", 14);
        _disconnectReason.AddThemeColorOverride("font_color", new Color("d5ded0"));
        dcStack.AddChild(_disconnectReason);

        Button dcReturnBtn = CreateButton("RETURN TO MENU");
        dcReturnBtn.Pressed += () =>
        {
            _disconnectCard.Visible = false;
            ShowMultiplayerView();
            DisconnectModalDismissed?.Invoke();
        };
        dcStack.AddChild(dcReturnBtn);

        WrapInMargin(_disconnectCard, dcStack);
        AddChild(_disconnectCard);
    }

    private void OnConnectPressed()
    {
        if (_connectBtn.Disabled) return;
        ConnectRequested?.Invoke(_addressInput.Text);
    }

    public void ShowMainMenuView()
    {
        Visible = true;
        _backdrop.Visible = true;
        _mainCard.Visible = true;
        _multiplayerCard.Visible = false;
        _disconnectCard.Visible = false;
        SetConnecting(false);
    }

    public void ShowMultiplayerView()
    {
        Visible = true;
        _backdrop.Visible = true;
        _mainCard.Visible = false;
        _multiplayerCard.Visible = true;
        _disconnectCard.Visible = false;
        SetConnecting(false);
    }

    public void ShowConnectionLostModal(string reason)
    {
        Visible = true;
        _backdrop.Visible = true;
        _mainCard.Visible = false;
        _multiplayerCard.Visible = false;
        _disconnectCard.Visible = true;
        _disconnectReason.Text = string.IsNullOrWhiteSpace(reason)
            ? "Disconnected from server."
            : reason;
    }

    public void SetConnecting(bool isConnecting)
    {
        _connectBtn.Disabled = isConnecting;
        _cancelBtn.Disabled = !isConnecting;
        _backBtn.Disabled = isConnecting;
        _addressInput.Editable = !isConnecting;
    }

    public void SetStatus(string message, ClientLifecycleState state)
    {
        _statusLabel.Text = message;
        Color color = state switch
        {
            ClientLifecycleState.Failed => new Color("ee6666"),
            ClientLifecycleState.Connecting or
            ClientLifecycleState.Handshaking or
            ClientLifecycleState.WaitingForFirstSnapshot => new Color("edc76b"),
            ClientLifecycleState.Active => new Color("77dd77"),
            _ => new Color("8a9a88")
        };
        _statusLabel.AddThemeColorOverride("font_color", color);

        bool connecting = state is ClientLifecycleState.Connecting
            or ClientLifecycleState.Handshaking
            or ClientLifecycleState.WaitingForFirstSnapshot;
        SetConnecting(connecting);
    }

    public void SetAddress(string address)
    {
        _addressInput.Text = address;
    }

    public void HideMenu()
    {
        Visible = false;
        _backdrop.Visible = false;
        _mainCard.Visible = false;
        _multiplayerCard.Visible = false;
        _disconnectCard.Visible = false;
    }

    public Rect2 GetActiveCardRect()
    {
        if (_multiplayerCard.Visible) return _multiplayerCard.GetGlobalRect();
        if (_mainCard.Visible) return _mainCard.GetGlobalRect();
        if (_disconnectCard.Visible) return _disconnectCard.GetGlobalRect();
        return default;
    }

    private static PanelContainer CreateCard(Vector2 minSize)
    {
        PanelContainer card = new()
        {
            CustomMinimumSize = minSize,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        card.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        card.AddThemeStyleboxOverride("panel", CardStyle());
        return card;
    }

    private static void WrapInMargin(PanelContainer parent, Control child)
    {
        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        margin.AddChild(child);
        parent.AddChild(margin);
    }

    private static Button CreateButton(string text)
    {
        Button btn = new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(0, 36),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        btn.AddThemeStyleboxOverride("normal", ButtonNormalStyle());
        btn.AddThemeStyleboxOverride("hover", ButtonHoverStyle());
        btn.AddThemeStyleboxOverride("pressed", ButtonPressedStyle());
        btn.AddThemeStyleboxOverride("disabled", ButtonDisabledStyle());
        btn.AddThemeFontSizeOverride("font_size", 14);
        btn.AddThemeColorOverride("font_color", new Color("d5ded0"));
        btn.AddThemeColorOverride("font_hover_color", new Color("ffffff"));
        btn.AddThemeColorOverride("font_pressed_color", new Color("edc76b"));
        btn.AddThemeColorOverride("font_disabled_color", new Color("556655"));
        return btn;
    }

    private static StyleBoxFlat CardStyle() => new()
    {
        BgColor = new Color(0.04f, 0.06f, 0.05f, 0.96f),
        BorderColor = new Color("46533e"),
        BorderWidthBottom = 2,
        BorderWidthTop = 2,
        BorderWidthLeft = 2,
        BorderWidthRight = 2,
        CornerRadiusBottomLeft = 8,
        CornerRadiusBottomRight = 8,
        CornerRadiusTopLeft = 8,
        CornerRadiusTopRight = 8
    };

    private static StyleBoxFlat LineEditStyle(bool focused = false) => new()
    {
        BgColor = new Color(0.025f, 0.04f, 0.03f, 0.95f),
        BorderColor = focused ? new Color("edc76b") : new Color("46533e"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };

    private static StyleBoxFlat ButtonNormalStyle() => new()
    {
        BgColor = new Color(0.10f, 0.14f, 0.11f, 0.92f),
        BorderColor = new Color("46533e"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };

    private static StyleBoxFlat ButtonHoverStyle() => new()
    {
        BgColor = new Color(0.16f, 0.22f, 0.17f, 0.95f),
        BorderColor = new Color("edc76b"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };

    private static StyleBoxFlat ButtonPressedStyle() => new()
    {
        BgColor = new Color(0.08f, 0.11f, 0.09f, 0.98f),
        BorderColor = new Color("77dd77"),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };

    private static StyleBoxFlat ButtonDisabledStyle() => new()
    {
        BgColor = new Color(0.05f, 0.07f, 0.06f, 0.5f),
        BorderColor = new Color(0.18f, 0.22f, 0.18f, 0.4f),
        BorderWidthBottom = 1,
        BorderWidthTop = 1,
        BorderWidthLeft = 1,
        BorderWidthRight = 1,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4
    };
}
