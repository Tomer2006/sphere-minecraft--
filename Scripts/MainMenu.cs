using System.Collections.Generic;
using Godot;

namespace SphereMinecraft;

public partial class MainMenu : Control
{
    private const string GameScenePath = "res://Scenes/main.tscn";

    private Button? continueButton;
    private Label? subtitleLabel;
    private Label? saveCountLabel;
    private VBoxContainer? saveList;

    public override void _Ready()
    {
        BuildMenu();
        RefreshState();
    }

    private void BuildMenu()
    {
        AnchorRight = 1f;
        AnchorBottom = 1f;
        MouseFilter = MouseFilterEnum.Stop;

        ColorRect background = new()
        {
            Color = new Color(0.05f, 0.07f, 0.10f, 1f),
            AnchorRight = 1f,
            AnchorBottom = 1f
        };
        AddChild(background);

        ColorRect glow = new()
        {
            Color = new Color(0.30f, 0.55f, 0.32f, 0.16f),
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -420f,
            OffsetTop = -280f,
            OffsetRight = 420f,
            OffsetBottom = 280f
        };
        AddChild(glow);

        ColorRect accent = new()
        {
            Color = new Color(0.76f, 0.90f, 0.58f, 0.05f),
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -250f,
            OffsetTop = -210f,
            OffsetRight = 250f,
            OffsetBottom = 210f
        };
        AddChild(accent);

        PanelContainer panel = new()
        {
            AnchorLeft = 0.5f,
            AnchorTop = 0.5f,
            AnchorRight = 0.5f,
            AnchorBottom = 0.5f,
            OffsetLeft = -360f,
            OffsetTop = -250f,
            OffsetRight = 360f,
            OffsetBottom = 250f
        };
        panel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
            new Color(0.08f, 0.10f, 0.14f, 0.96f),
            new Color(0.43f, 0.58f, 0.35f, 0.35f),
            24,
            1));
        AddChild(panel);

        MarginContainer margin = new();
        margin.AddThemeConstantOverride("margin_left", 32);
        margin.AddThemeConstantOverride("margin_top", 30);
        margin.AddThemeConstantOverride("margin_right", 32);
        margin.AddThemeConstantOverride("margin_bottom", 30);
        panel.AddChild(margin);

        VBoxContainer layout = new();
        layout.AddThemeConstantOverride("separation", 18);
        margin.AddChild(layout);

        Label title = new()
        {
            Text = "Sphere Minecraft",
            HorizontalAlignment = HorizontalAlignment.Center
        };
        title.AddThemeFontSizeOverride("font_size", 30);
        layout.AddChild(title);

        subtitleLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        subtitleLabel.AddThemeColorOverride("font_color", new Color(0.82f, 0.88f, 0.92f, 0.92f));
        layout.AddChild(subtitleLabel);

        HBoxContainer actionsRow = new();
        actionsRow.AddThemeConstantOverride("separation", 12);
        layout.AddChild(actionsRow);

        continueButton = new Button
        {
            Text = "Continue Latest Save",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 46f)
        };
        StylePrimaryButton(continueButton);
        continueButton.Pressed += ContinueLatestSave;
        actionsRow.AddChild(continueButton);

        Button newGameButton = new()
        {
            Text = "New Game",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0f, 46f)
        };
        StyleSecondaryButton(newGameButton);
        newGameButton.Pressed += StartNewGame;
        actionsRow.AddChild(newGameButton);

        HBoxContainer savesHeader = new();
        savesHeader.AddThemeConstantOverride("separation", 8);
        layout.AddChild(savesHeader);

        Label savesLabel = new()
        {
            Text = "Saves",
            HorizontalAlignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        savesLabel.AddThemeFontSizeOverride("font_size", 20);
        savesHeader.AddChild(savesLabel);

        saveCountLabel = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        saveCountLabel.AddThemeColorOverride("font_color", new Color(0.71f, 0.78f, 0.82f, 0.82f));
        savesHeader.AddChild(saveCountLabel);

        PanelContainer savesPanel = new();
        savesPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
            new Color(0.10f, 0.13f, 0.18f, 0.86f),
            new Color(0.34f, 0.44f, 0.52f, 0.28f),
            18,
            1));
        layout.AddChild(savesPanel);

        MarginContainer savesMargin = new();
        savesMargin.AddThemeConstantOverride("margin_left", 14);
        savesMargin.AddThemeConstantOverride("margin_top", 14);
        savesMargin.AddThemeConstantOverride("margin_right", 14);
        savesMargin.AddThemeConstantOverride("margin_bottom", 14);
        savesPanel.AddChild(savesMargin);

        VBoxContainer savesPanelLayout = new();
        savesPanelLayout.AddThemeConstantOverride("separation", 10);
        savesMargin.AddChild(savesPanelLayout);

        Label savesHint = new()
        {
            Text = "Load any slot, or delete saves you no longer need.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        savesHint.AddThemeColorOverride("font_color", new Color(0.74f, 0.80f, 0.86f, 0.78f));
        savesPanelLayout.AddChild(savesHint);

        ScrollContainer savesScroll = new()
        {
            CustomMinimumSize = new Vector2(0f, 230f),
            SizeFlagsVertical = SizeFlags.ExpandFill
        };
        savesPanelLayout.AddChild(savesScroll);

        saveList = new VBoxContainer();
        saveList.AddThemeConstantOverride("separation", 10);
        savesScroll.AddChild(saveList);

        HBoxContainer footerRow = new();
        layout.AddChild(footerRow);

        Button quitButton = new()
        {
            Text = "Quit",
            CustomMinimumSize = new Vector2(0f, 42f)
        };
        StyleSecondaryButton(quitButton);
        quitButton.Pressed += () => GetTree().Quit();
        footerRow.AddChild(quitButton);
    }

    private void RefreshState()
    {
        List<SaveSlotInfo> saves = SaveGameManager.ListSaveSlots();

        if (continueButton != null)
        {
            continueButton.Disabled = saves.Count == 0;
        }

        if (subtitleLabel != null)
        {
            subtitleLabel.Text = saves.Count > 0
                ? "Continue loads the newest save.\nYou can also pick any save from the list below."
                : "No saves found yet.\nStart a new game and save from the pause menu or with F5.";
        }

        if (saveCountLabel != null)
        {
            saveCountLabel.Text = saves.Count == 1 ? "1 slot" : $"{saves.Count} slots";
        }

        RebuildSaveList(saves);
    }

    private void RebuildSaveList(List<SaveSlotInfo> saves)
    {
        if (saveList is null)
        {
            return;
        }

        foreach (Node child in saveList.GetChildren())
        {
            child.QueueFree();
        }

        if (saves.Count == 0)
        {
            PanelContainer emptyCard = new();
            emptyCard.AddThemeStyleboxOverride("panel", CreatePanelStyle(
                new Color(0.12f, 0.14f, 0.18f, 0.92f),
                new Color(0.30f, 0.38f, 0.46f, 0.25f),
                16,
                1));
            saveList.AddChild(emptyCard);

            MarginContainer emptyMargin = new();
            emptyMargin.AddThemeConstantOverride("margin_left", 16);
            emptyMargin.AddThemeConstantOverride("margin_top", 16);
            emptyMargin.AddThemeConstantOverride("margin_right", 16);
            emptyMargin.AddThemeConstantOverride("margin_bottom", 16);
            emptyCard.AddChild(emptyMargin);

            Label emptyLabel = new()
            {
                Text = "No saves yet.\nCreate a new game, then save from the pause menu or with F5.",
                HorizontalAlignment = HorizontalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            emptyLabel.AddThemeColorOverride("font_color", new Color(0.78f, 0.82f, 0.88f, 0.9f));
            emptyMargin.AddChild(emptyLabel);
            return;
        }

        for (int index = 0; index < saves.Count; index++)
        {
            SaveSlotInfo save = saves[index];
            bool isLatest = index == 0;

            PanelContainer rowPanel = new();
            rowPanel.AddThemeStyleboxOverride("panel", CreatePanelStyle(
                isLatest
                    ? new Color(0.13f, 0.18f, 0.14f, 0.96f)
                    : new Color(0.12f, 0.14f, 0.18f, 0.92f),
                isLatest
                    ? new Color(0.58f, 0.78f, 0.46f, 0.42f)
                    : new Color(0.30f, 0.38f, 0.46f, 0.25f),
                16,
                1));
            saveList.AddChild(rowPanel);

            MarginContainer rowMargin = new();
            rowMargin.AddThemeConstantOverride("margin_left", 14);
            rowMargin.AddThemeConstantOverride("margin_top", 12);
            rowMargin.AddThemeConstantOverride("margin_right", 14);
            rowMargin.AddThemeConstantOverride("margin_bottom", 12);
            rowPanel.AddChild(rowMargin);

            VBoxContainer card = new();
            card.AddThemeConstantOverride("separation", 10);
            rowMargin.AddChild(card);

            HBoxContainer topRow = new();
            topRow.AddThemeConstantOverride("separation", 10);
            card.AddChild(topRow);

            Label info = new()
            {
                Text = save.GetDisplayText(),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                AutowrapMode = TextServer.AutowrapMode.WordSmart
            };
            info.AddThemeColorOverride("font_color", new Color(0.94f, 0.97f, 0.98f, 0.96f));
            topRow.AddChild(info);

            if (isLatest)
            {
                Label latestLabel = new()
                {
                    Text = "LATEST",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    CustomMinimumSize = new Vector2(72f, 28f)
                };
                latestLabel.AddThemeFontSizeOverride("font_size", 12);
                latestLabel.AddThemeColorOverride("font_color", new Color(0.17f, 0.24f, 0.12f, 1f));

                PanelContainer badge = new();
                badge.AddThemeStyleboxOverride("panel", CreatePanelStyle(
                    new Color(0.72f, 0.89f, 0.56f, 0.95f),
                    new Color(0.90f, 0.98f, 0.82f, 0.0f),
                    12,
                    0));
                badge.AddChild(latestLabel);
                topRow.AddChild(badge);
            }

            HBoxContainer actionRow = new();
            actionRow.Alignment = BoxContainer.AlignmentMode.End;
            actionRow.AddThemeConstantOverride("separation", 8);
            card.AddChild(actionRow);

            Button loadButton = new()
            {
                Text = "Load",
                CustomMinimumSize = new Vector2(96f, 36f)
            };
            StylePrimaryButton(loadButton);
            loadButton.Pressed += () => LoadSave(save.SlotId);
            actionRow.AddChild(loadButton);

            Button deleteButton = new()
            {
                Text = "Delete",
                CustomMinimumSize = new Vector2(96f, 36f)
            };
            StyleDangerButton(deleteButton);
            deleteButton.Pressed += () => DeleteSave(save.SlotId);
            actionRow.AddChild(deleteButton);
        }
    }

    private void ContinueLatestSave()
    {
        List<SaveSlotInfo> saves = SaveGameManager.ListSaveSlots();
        if (saves.Count == 0)
        {
            RefreshState();
            return;
        }

        LoadSave(saves[0].SlotId);
    }

    private void LoadSave(string slotId)
    {
        SaveGameManager.BeginLoadSlot(slotId);
        GetTree().ChangeSceneToFile(GameScenePath);
    }

    private void DeleteSave(string slotId)
    {
        SaveGameManager.DeleteSave(slotId);
        RefreshState();
    }

    private void StartNewGame()
    {
        SaveGameManager.BeginNewGame();
        GetTree().ChangeSceneToFile(GameScenePath);
    }

    private static StyleBoxFlat CreatePanelStyle(Color bgColor, Color borderColor, int cornerRadius, int borderWidth)
    {
        return new StyleBoxFlat
        {
            BgColor = bgColor,
            BorderColor = borderColor,
            CornerRadiusTopLeft = cornerRadius,
            CornerRadiusTopRight = cornerRadius,
            CornerRadiusBottomRight = cornerRadius,
            CornerRadiusBottomLeft = cornerRadius,
            BorderWidthLeft = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthBottom = borderWidth
        };
    }

    private static void StylePrimaryButton(Button button)
    {
        button.AddThemeStyleboxOverride("normal", CreatePanelStyle(
            new Color(0.44f, 0.64f, 0.38f, 0.95f),
            new Color(0.62f, 0.82f, 0.53f, 0.55f),
            14,
            1));
        button.AddThemeStyleboxOverride("hover", CreatePanelStyle(
            new Color(0.49f, 0.70f, 0.42f, 0.98f),
            new Color(0.70f, 0.88f, 0.60f, 0.65f),
            14,
            1));
        button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(
            new Color(0.36f, 0.54f, 0.31f, 1f),
            new Color(0.70f, 0.88f, 0.60f, 0.45f),
            14,
            1));
        button.AddThemeColorOverride("font_color", new Color(0.05f, 0.09f, 0.04f, 1f));
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.AddThemeStyleboxOverride("normal", CreatePanelStyle(
            new Color(0.15f, 0.18f, 0.23f, 0.96f),
            new Color(0.36f, 0.44f, 0.52f, 0.40f),
            14,
            1));
        button.AddThemeStyleboxOverride("hover", CreatePanelStyle(
            new Color(0.19f, 0.23f, 0.29f, 0.98f),
            new Color(0.46f, 0.56f, 0.65f, 0.50f),
            14,
            1));
        button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(
            new Color(0.12f, 0.15f, 0.19f, 1f),
            new Color(0.34f, 0.42f, 0.50f, 0.35f),
            14,
            1));
    }

    private static void StyleDangerButton(Button button)
    {
        button.AddThemeStyleboxOverride("normal", CreatePanelStyle(
            new Color(0.28f, 0.14f, 0.15f, 0.94f),
            new Color(0.66f, 0.30f, 0.32f, 0.40f),
            14,
            1));
        button.AddThemeStyleboxOverride("hover", CreatePanelStyle(
            new Color(0.34f, 0.17f, 0.18f, 0.98f),
            new Color(0.80f, 0.36f, 0.39f, 0.52f),
            14,
            1));
        button.AddThemeStyleboxOverride("pressed", CreatePanelStyle(
            new Color(0.22f, 0.10f, 0.11f, 1f),
            new Color(0.70f, 0.32f, 0.34f, 0.35f),
            14,
            1));
    }
}
