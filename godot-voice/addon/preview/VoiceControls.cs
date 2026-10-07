using Godot;

namespace IdleRpg;

public partial class VoiceControls : Control
{
    private Label _status = null!;
    private int _clicks;

    public override void _Ready()
    {
        _status = GetNode<Label>("%Status");
        GetNode<Button>("%CountButton").Pressed += () => Report($"Button clicked {++_clicks} times.");
        GetNode<Button>("%ResetButton").Pressed += () => { _clicks = 0; Report("Counter reset."); };
        GetNode<Button>("%BootstrapButton").Pressed += () => GetTree().ChangeSceneToFile("res://src/Bootstrap.tscn");
        GetNode<Button>("%DialogButton").Pressed += () => GetNode<AcceptDialog>("%ExampleDialog").PopupCentered();
        GetNode<AcceptDialog>("%ExampleDialog").Confirmed += () => Report("Dialog confirmed.");
        foreach (string name in new[] { "EnabledCheck", "MusicToggle", "RadioLeft", "RadioRight" })
        {
            var button = GetNode<BaseButton>("%" + name);
            button.Toggled += pressed => Report($"{button.Name}: {(pressed ? "on" : "off")}");
        }
        GetNode<LineEdit>("%NameInput").TextChanged += text => Report("Name: " + text);
        GetNode<LineEdit>("%NameInput").TextSubmitted += text => Report("Submitted: " + text);
        GetNode<TextEdit>("%NotesInput").TextChanged += () => Report("Notes updated.");
        GetNode<HSlider>("%VolumeSlider").ValueChanged += value =>
        {
            GetNode<ProgressBar>("%VolumeProgress").Value = value;
            Report($"Volume: {value:0}");
        };
        GetNode<SpinBox>("%QuantityInput").ValueChanged += value => Report($"Quantity: {value:0}");
        var difficulty = GetNode<OptionButton>("%DifficultyOptions");
        difficulty.ItemSelected += index => Report("Difficulty: " + difficulty.GetItemText((int)index));
        GetNode<ColorPickerButton>("%ColorInput").ColorChanged += color => Report("Color: " + color.ToHtml());
        GetNode<MenuButton>("%ActionMenu").GetPopup().IdPressed += id => Report($"Menu action {id} selected.");
        var items = GetNode<ItemList>("%InventoryList");
        items.ItemSelected += index => Report("Selected: " + items.GetItemText((int)index));
        items.ItemActivated += index => Report("Opened: " + items.GetItemText((int)index));
        var tree = GetNode<Tree>("%InventoryTree");
        // TreeItem rows are transient data, unlike the saved Control layout.
        var root = tree.CreateItem();
        root.SetText(0, "Inventory");
        foreach (string category in new[] { "Equipment", "Supplies", "Quest items" })
        {
            var branch = tree.CreateItem(root);
            branch.SetText(0, category);
            for (int i = 1; i <= 3; i++) tree.CreateItem(branch).SetText(0, $"{category} {i}");
        }
        tree.ItemSelected += () => Report("Tree selection: " + tree.GetSelected().GetText(0));
        GetNode<TabContainer>("%Pages").TabChanged += index => Report($"Page {index + 1} selected.");
        foreach (Node node in GetNode<VBoxContainer>("%ScrollRows").GetChildren())
            if (node is Button button) button.Pressed += () => Report(button.Text + " clicked.");
        Report("Ready. Use the green letters: click, focus, set, increase, decrease, upper, or downer.");
    }

    private void Report(string message) => _status.Text = message;
}
