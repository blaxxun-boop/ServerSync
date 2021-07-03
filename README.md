ServerSync
==========

Bundling the dll
----------------

You need to ensure the dll is available to your mod.

Including the dll is best done via ILRepack (https://github.com/ravibpatel/ILRepack.Lib.MSBuild.Task). You can load this package (ILRepack.Lib.MSBuild.Task) from NuGet.

Then create a file ILRepack.targets in your project folder. File content:
```
<?xml version="1.0" encoding="utf-8"?>
<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
    <Target Name="ILRepacker" AfterTargets="Build">
        <ItemGroup>
            <InputAssemblies Include="$(TargetPath)" />
            <InputAssemblies Include="$(OutputPath)\ServerSync.dll" />
        </ItemGroup>
        <ILRepack Parallel="true" DebugInfo="true" Internalize="true" InputAssemblies="@(InputAssemblies)" OutputFile="$(TargetPath)" TargetKind="SameAsPrimaryAssembly" LibraryPath="$(OutputPath)" />
    </Target>
</Project>
```

Using the ServerSync
--------------------

Declare a variable:

`ServerSync.ConfigSync configSync = new ServerSync.ConfigSync("my.mod.guid") { DisplayName = "My Mod Name", CurrentVersion = "1.2.3", MinimumRequiredVersion = "1.2.0" };`

All of DisplayName, CurrentVersion and MinimumRequiredVersion are optional.
If CurrentVersion is specified, then the user will see a warning in their BepInEx log if the server version does not match the client version.
If also MinimumRequiredVersion is specified and the client has an older version than the servers MinimumRequiredVersion, the client will be immediately disconnected and see an error message, explaining why.
To display a friendly name for your mod in the error messages, specify DisplayName, otherwise the primary identifier will be used.
Also note that the primary identifier (I propose using the GUID, "my.mod.guid") should never be changed (changing it will break backwards compatibility completely).

There are two public methods on the ServerSync.ConfigSync class:

- `AddConfigEntry<T>(ConfigEntry<T> configEntry)`

  Registers a BepInEx ConfigEntry to be synchronized.

- `AddLockingConfigEntry<T>(ConfigEntry<T> lockingConfig) where T : IConvertible`
  
  Registers a BepInEx ConfigEntry to be synchronized, whose value determines whether the config is locked. If the value is zero when converted to integer, the config is not locked. Otherwise it is locked.
  This method must be called at most once. If not called at all, the config will never be locked.

Useful properties:

- `static bool ProcessingServerUpdate`

  The mod is receiving and applying configs from the server. Used internally to avoid config writing loops.

- `bool IsSourceOfTruth`

  Whether the local config is currently being used. False if a remote config is currently applied.

Additionally, there is a class `ServerSync.CustomSyncedValue<T>(ConfigSync, string Identifier, T value = default)` to synchronize arbitrary data (more precisely: all data which Valheims native serialization supports).
This class registers itself to the passed ConfigSync instance upon instantiation.
It provides a Value property and a ValueChanged event handler.
The Identifier must be unique for the given ConfigSync instance.


Handy config function
---------------------

To avoid manually adding each config entry to the ConfigSync instance, I propose to add a simple wrapper `config()` (with the same signature as `Config.Bind()`) to your UnityBasePlugin class:

```
ConfigEntry<T> config<T>(string group, string name, T value, ConfigDescription description, bool synchronizedSetting = true)
{
    ConfigEntry<T> configEntry = Config.Bind(group, name, value, description);

    SyncedConfigEntry<T> syncedConfigEntry = configSync.AddConfigEntry(configEntry);
    syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

    return configEntry;
}

ConfigEntry<T> config<T>(string group, string name, T value, string description, bool synchronizedSetting = true) => config(group, name, value, new ConfigDescription(description), synchronizedSetting);
```
