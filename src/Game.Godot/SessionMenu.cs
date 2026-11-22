using DeadGround.Engine.Net;
using Godot;

namespace DeadGround.Client;

internal sealed partial class SessionMenu : CanvasLayer
{
    private const int CardHorizontalMargin = 24;
    private const int CardPanelPadding = 2;
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

        _backdrop = HudTheme.CreateModalBackdrop(visible: true);
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
        mainTitle.AddThemeColorOverride("font_color", HudTheme.Text);
        mainStack.AddChild(mainTitle);

        Label mainSubtitle = new()
        {
            Text = "COLD WAR TACTICAL SIMULATOR",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        mainSubtitle.AddThemeFontSizeOverride("font_size", 12);
        mainSubtitle.AddThemeColorOverride("font_color", HudTheme.Muted);
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
        mpTitle.AddThemeColorOverride("font_color", HudTheme.Accent);
        mpStack.AddChild(mpTitle);

        Label mpSubtitle = new()
        {
            Text = "Connect to Dedicated Server",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        mpSubtitle.AddThemeFontSizeOverride("font_size", 12);
        mpSubtitle.AddThemeColorOverride("font_color", HudTheme.Muted);
        mpStack.AddChild(mpSubtitle);

        VBoxContainer fieldBox = new();
        fieldBox.AddThemeConstantOverride("separation", 4);

        Label addrLabel = new() { Text = "SERVER ADDRESS" };
        addrLabel.AddThemeFontSizeOverride("font_size", 11);
        addrLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
        fieldBox.AddChild(addrLabel);

        _addressInput = new LineEdit
        {
            Text = "127.0.0.1:7777",
            PlaceholderText = "127.0.0.1:7777",
            CustomMinimumSize = new Vector2(0, 36)
        };
        _addressInput.AddThemeFontSizeOverride("font_size", 14);
        _addressInput.AddThemeColorOverride("font_color", HudTheme.Text);
        _addressInput.TextSubmitted += _ => OnConnectPressed();
        fieldBox.AddChild(_addressInput);
        mpStack.AddChild(fieldBox);

        _statusLabel = new Label
        {
            Size = new Vector2(_multiplayerCard.CustomMinimumSize.X - 2 * (CardHorizontalMargin + CardPanelPadding), 32),
            Text = "Ready to connect.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 32)
        };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", HudTheme.Muted);
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
        dcTitle.AddThemeColorOverride("font_color", HudTheme.Danger);
        dcStack.AddChild(dcTitle);

        _disconnectReason = new Label
        {
            // Seed the wrapping width before the first container layout measures height.
            Size = new Vector2(_disconnectCard.CustomMinimumSize.X - 2 * (CardHorizontalMargin + CardPanelPadding), 0),
            Text = "Disconnected from server.",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        _disconnectReason.AddThemeFontSizeOverride("font_size", 14);
        _disconnectReason.AddThemeColorOverride("font_color", HudTheme.Text);
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
        HudTheme.CenterModal(_mainCard);
    }

    public void ShowMultiplayerView()
    {
        Visible = true;
        _backdrop.Visible = true;
        _mainCard.Visible = false;
        _multiplayerCard.Visible = true;
        _disconnectCard.Visible = false;
        SetConnecting(false);
        HudTheme.CenterModal(_multiplayerCard);
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
        HudTheme.CenterModal(_disconnectCard);
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
            ClientLifecycleState.Failed => HudTheme.Danger,
            ClientLifecycleState.Connecting or
            ClientLifecycleState.Handshaking or
            ClientLifecycleState.WaitingForFirstSnapshot => HudTheme.Accent,
            ClientLifecycleState.Active => HudTheme.Accent,
            _ => HudTheme.Muted
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
            Theme = HudTheme.Shared,
            CustomMinimumSize = minSize,
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        HudTheme.CenterModal(card);
        card.AddThemeStyleboxOverride("panel", HudTheme.CreatePanel(accented: true, padding: CardPanelPadding, topRail: true, modal: true));
        return card;
    }

    private static void WrapInMargin(PanelContainer parent, Control child)
    {
        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", CardHorizontalMargin);
        margin.AddThemeConstantOverride("margin_right", CardHorizontalMargin);
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
        btn.AddThemeFontSizeOverride("font_size", 14);
        return btn;
    }
}
