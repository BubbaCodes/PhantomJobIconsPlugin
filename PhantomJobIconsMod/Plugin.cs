using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.NamePlate;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Game.Text.SeStringHandling;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using PhantomJobIconsMod.Windows;

namespace PhantomJobIconsMod;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static INamePlateGui NamePlateGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/pji";

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("PhantomJobIconsMod");
    private ConfigWindow ConfigWindow { get; init; }
    private MainWindow MainWindow { get; init; }
    private readonly IReadOnlyDictionary<byte, string> phantomJobNames;
    private readonly Dictionary<uint, string> jobsByEntityId = new();

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        phantomJobNames = DataManager.GetExcelSheet<MKDSupportJob>()
            .ToDictionary(job => (byte)job.RowId, job => job.Name.ExtractText());

        ConfigWindow = new ConfigWindow(this);
        MainWindow = new MainWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle the Phantom Job Icons window."
        });

        // Tell the UI system that we want our windows to be drawn through the window system
        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;

        // This adds a button to the plugin installer entry of this plugin which allows
        // toggling the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;

        // Adds another button doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        NamePlateGui.OnPostDataUpdate += OnPostNamePlateDataUpdate;

        // Add a simple message to the log with level set to information
        // Use /xllog to open the log window in-game
        // Example Output: 00:57:54.959 | INF | [PhantomJobIconsMod] ===A cool log message from Phantom Job Icons Mod===
        Log.Information($"===A cool log message from {PluginInterface.Manifest.Name}===");
    }

    public void Dispose()
    {
        // Unregister all actions to not leak anything during disposal of plugin
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        NamePlateGui.OnPostDataUpdate -= OnPostNamePlateDataUpdate;
        
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();

        CommandManager.RemoveHandler(CommandName);
    }

    private void OnCommand(string command, string args)
    {
        // In response to the slash command, toggle the display status of our main ui
        MainWindow.Toggle();
    }

    private unsafe void OnPostNamePlateDataUpdate(
        INamePlateUpdateContext _,
        IReadOnlyList<INamePlateUpdateHandler> handlers)
    {
        if (!Configuration.ShowPhantomJobs || PublicContentOccultCrescent.GetState() == null)
            return;

        var contentMembers = InfoProxyContentMember.Instance();
        if (contentMembers == null)
            return;

        jobsByEntityId.Clear();
        // The Occult Crescent member list maps each actor entity ID to its selected Phantom Job.
        for (var i = 0; i < contentMembers->EntryCount && i < 200; i++)
        {
            var member = contentMembers->MemberData[i];
            if (member.EntityId != 0 && phantomJobNames.TryGetValue(member.MKDSupportJob, out var jobName))
                jobsByEntityId[member.EntityId] = jobName;
        }

        foreach (var handler in handlers)
        {
            var player = handler.PlayerCharacter;
            if (player == null || !jobsByEntityId.TryGetValue(player.EntityId, out var jobName))
                continue;

            handler.Name = new SeStringBuilder()
                .Append(handler.Name)
                .Append($"  [{jobName}]")
                .Build();
        }
    }
    
    public void ToggleConfigUi() => ConfigWindow.Toggle();
    public void ToggleMainUi() => MainWindow.Toggle();
}
