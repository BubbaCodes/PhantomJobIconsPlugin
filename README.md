# Phantom Job Icons Mod

A Dalamud plugin that adds each recognized player's active Phantom Job beside their character name while you are in Occult Crescent. It reads the job IDs from the game's Occult Crescent content-member list and appends the localized job name to matching player nameplates. Nameplates without a matching member/job entry are left unchanged.

The starter is based on [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin). See the [Dalamud developer documentation](https://dalamud.dev) and [AI usage policy](https://dalamud.dev/plugin-publishing/ai-policy) before developing or publishing a plugin.

## Requirements

- FINAL FANTASY XIV, XIVLauncher, and Dalamud installed and launched at least once
- .NET 10 SDK
- Dalamud's development files in the default XIVLauncher location, or set `DALAMUD_HOME`

## Build

Open `PhantomJobIconsMod.slnx` in Visual Studio or JetBrains Rider, or run:

```powershell
dotnet build .\PhantomJobIconsMod.slnx
```

The plugin DLL is `PhantomJobIconsMod\bin\x64\Debug\PhantomJobIconsMod\PhantomJobIconsMod.dll`.

## Load in-game

1. In-game, open `/xlsettings` and add the built `PhantomJobIconsMod.dll` to **Experimental > Dev Plugin Locations**.
2. Open `/xlplugins`, then enable the plugin in **Dev Tools > Installed Dev Plugins**.
3. Use `/pji` or the plugin installer buttons to open the plugin windows.
4. Enter Occult Crescent; toggle labels in the plugin settings if desired.

This project uses game UI/nameplate data exposed to the client and does not infer a job when the game has no matching content-member entry.
