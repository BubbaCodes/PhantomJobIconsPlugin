using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PhantomJobIconsMod.Windows;

public class MainWindow : Window
{
    private readonly Plugin plugin;

    public MainWindow(Plugin plugin)
        : base("Phantom Job Icons##Main Window", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(340, 140),
            MaximumSize = new Vector2(600, 240)
        };

        this.plugin = plugin;
    }

    public override void Draw()
    {
        ImGui.TextWrapped("Shows each recognized player's active Phantom Job beside their nameplate while you are in Occult Crescent.");
        ImGui.Spacing();
        ImGui.Text($"Nameplate labels: {(plugin.Configuration.ShowPhantomJobs ? "On" : "Off")}");

        if (ImGui.Button("Show Settings"))
        {
            plugin.ToggleConfigUi();
        }
    }
}
