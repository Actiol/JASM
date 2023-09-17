﻿#nullable enable
using GIMI_ModManager.Core.Contracts.Entities;
using Serilog;

namespace GIMI_ModManager.Core.Entities;

public sealed class CharacterModList : ICharacterModList, IDisposable
{
    private readonly ILogger? _logger;
    public IReadOnlyCollection<CharacterSkinEntry> Mods => new List<CharacterSkinEntry>(_mods).AsReadOnly();
    public string AbsModsFolderPath { get; }
    private readonly List<CharacterSkinEntry> _mods = new();
    public const string DISABLED_PREFIX = "DISABLED_";
    public const string ALT_DISABLED_PREFIX = "DISABLED";
    public string DisabledPrefix => DISABLED_PREFIX;
    private readonly FileSystemWatcher _watcher;
    public GenshinCharacter Character { get; }

    internal CharacterModList(GenshinCharacter character, string absPath, ILogger? logger = null)
    {
        _logger = logger?.ForContext<CharacterModList>();
        Character = character;
        AbsModsFolderPath = absPath;
        _watcher = new(AbsModsFolderPath);
        _watcher.NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.CreationTime | NotifyFilters.FileName;
        _watcher.Renamed += OnModRenamed;
        _watcher.Created += OnModCreated;
        _watcher.Deleted += OnModDeleted;
        _watcher.Error += OnWatcherError;

        _watcher.IncludeSubdirectories = false;
        _watcher.EnableRaisingEvents = true;
    }

    public void SetCustomModName(Guid modId, string newName = "")
    {
        if (_mods.FirstOrDefault(mod => mod.Id == modId) is { } modEntry)
        {
        }
        else
            _logger?.Warning("Renamed mod {ModId} was not tracked in mod list", modId);
    }

    public event EventHandler<ModFolderChangedArgs>? ModsChanged;

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _logger?.Error(e.GetException(), "Error in FileSystemWatcher");
    }

    private void OnModDeleted(object sender, FileSystemEventArgs e)
    {
        _logger?.Information("Mod {ModName} in {characterFolder} folder was deleted", e.Name, Character.DisplayName);
        if (_mods.Any(mod => mod.Mod.FullPath == e.FullPath))
        {
            var mod = _mods.First(mod => mod.Mod.FullPath == e.FullPath);
            _mods.Remove(mod);
        }
        else
            _logger?.Warning("Deleted folder {Folder} was not tracked in mod list", e.FullPath);

        ModsChanged?.Invoke(this, new ModFolderChangedArgs(e.FullPath, ModFolderChangeType.Deleted));
    }

    private void OnModCreated(object sender, FileSystemEventArgs e)
    {
        _logger?.Information("Mod {ModName} was created in {characterFolder} created", e.Name, Character.DisplayName);
        var mod = new SkinMod(new DirectoryInfo(e.FullPath));
        if (ModAlreadyAdded(mod))
            _logger?.Warning("Created folder {Folder} was already tracked in {characterFolder} mod list", e.Name,
                Character.DisplayName);
        else
            TrackMod(mod);
        ModsChanged?.Invoke(this, new ModFolderChangedArgs(e.FullPath, ModFolderChangeType.Created));
    }

    private void OnModRenamed(object sender, RenamedEventArgs e)
    {
        _logger?.Information("Mod {ModName} renamed to {NewName}", e.OldFullPath, e.FullPath);
        if (_mods.FirstOrDefault(mod => mod.Mod.FullPath == e.OldFullPath) is var oldModEntry &&
            oldModEntry is not null)
        {
            var newMod = new SkinMod(new DirectoryInfo(e.FullPath), oldModEntry.Mod.CustomName);
            var modEntry = new CharacterSkinEntry(newMod, this, !newMod.Name.StartsWith(DISABLED_PREFIX));
            _mods.Remove(oldModEntry);
            _mods.Add(modEntry);
        }
        else
            _logger?.Warning("Renamed folder {Folder} was not tracked in mod list", e.OldFullPath);

        ModsChanged?.Invoke(this, new ModFolderChangedArgs(e.FullPath, ModFolderChangeType.Renamed, e.OldFullPath));
    }


    public void TrackMod(ISkinMod mod)
    {
        if (ModAlreadyAdded(mod))
            throw new InvalidOperationException("Mod already added");

        _mods.Add(mod.Name.StartsWith(DISABLED_PREFIX)
            ? new CharacterSkinEntry(mod, this, false)
            : new CharacterSkinEntry(mod, this, true));
        _logger?.Debug("Tracking {ModName} in {CharacterName} modList", mod.Name, Character.DisplayName);
    }

    // Untrack
    public void UnTrackMod(IMod mod)
    {
        if (!ModAlreadyAdded(mod))
        {
            _logger?.Warning("Mod {ModName} was not tracked in {CharacterName} modList", mod.Name,
                Character.DisplayName);
            return;
        }

        _mods.Remove(_mods.First(m => m.Mod == mod));
        _logger?.Debug("Stopped tracking {ModName} in {CharacterName} modList", mod.Name, Character.DisplayName);
    }

    public void EnableMod(Guid modId)
    {
        try
        {
            _watcher.EnableRaisingEvents = false;

            var mod = _mods.First(m => m.Id == modId).Mod;

            if (!ModAlreadyAdded(mod))
                throw new InvalidOperationException("Mod not added");

            if (!mod.Name.StartsWith(DISABLED_PREFIX))
                throw new InvalidOperationException("Cannot enable a enabled mod");

            var newName = GetFolderNameWithoutDisabledPrefix(mod.Name);

            if (Directory.Exists(Path.Combine(AbsModsFolderPath, newName)))
                throw new InvalidOperationException("Cannot disable a mod with the same name as a disabled mod");

            mod.Rename(newName);


            _mods.First(m => m.Mod == mod).IsEnabled = true;
        }
        finally
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    public void DisableMod(Guid modId)
    {
        try
        {
            _watcher.EnableRaisingEvents = false;
            var mod = _mods.First(m => m.Id == modId).Mod;

            if (!ModAlreadyAdded(mod))
                throw new InvalidOperationException("Mod not added");

            if (mod.Name.StartsWith(DISABLED_PREFIX))
                throw new InvalidOperationException("Cannot disable a disabled mod");

            var newName = GetFolderNameWithDisabledPrefix(mod.Name);

            if (Directory.Exists(Path.Combine(AbsModsFolderPath, newName)))
                throw new InvalidOperationException("Cannot disable a mod with the same name as a disabled mod");

            mod.Rename(newName);
            _mods.First(m => m.Mod == mod).IsEnabled = false;
        }
        finally
        {
            _watcher.EnableRaisingEvents = true;
        }
    }

    public bool IsModEnabled(IMod mod)
    {
        if (!ModAlreadyAdded(mod))
            throw new InvalidOperationException("Mod not added");

        return _mods.First(m => m.Mod == mod).IsEnabled;
    }

    private bool ModAlreadyAdded(IMod mod)
    {
        return _mods.Any(m => m.Mod == mod);
    }

    public void Dispose()
        => _watcher.Dispose();

    public override string ToString()
    {
        return $"{Character} ({Mods.Count} mods)";
    }

    public DisableWatcher DisableWatcher() => new(_watcher);

    public bool FolderAlreadyExists(string folderName)
    {
        if (Path.IsPathFullyQualified(folderName))
            folderName = Path.GetDirectoryName(folderName) ?? folderName;

        var enabledFolderNamePath = Path.Combine(AbsModsFolderPath, GetFolderNameWithoutDisabledPrefix(folderName));
        var disabledFolderNamePath = Path.Combine(AbsModsFolderPath, GetFolderNameWithDisabledPrefix(folderName));

        return Directory.Exists(enabledFolderNamePath) || Directory.Exists(disabledFolderNamePath);
    }

    public string GetFolderNameWithoutDisabledPrefix(string folderName)
    {
        if (folderName.StartsWith(DISABLED_PREFIX, StringComparison.CurrentCultureIgnoreCase))
            return folderName.Replace(DISABLED_PREFIX, string.Empty);

        if (folderName.StartsWith(ALT_DISABLED_PREFIX, StringComparison.CurrentCultureIgnoreCase))
            return folderName.Replace(ALT_DISABLED_PREFIX, string.Empty);

        return folderName;
    }

    public string GetFolderNameWithDisabledPrefix(string folderName)
    {
        if (folderName.StartsWith(DISABLED_PREFIX, StringComparison.CurrentCultureIgnoreCase))
            return folderName;

        if (folderName.StartsWith(ALT_DISABLED_PREFIX, StringComparison.CurrentCultureIgnoreCase))
            return folderName.Replace(ALT_DISABLED_PREFIX, DISABLED_PREFIX);

        return DISABLED_PREFIX + folderName;
    }

    public void DeleteMod(Guid modId, bool moveToRecycleBin = true) => throw new NotImplementedException();


    public void DeleteModBySkinEntryId(Guid skinEntryId, bool moveToRecycleBin = true)
    {
        var skinEntry = _mods.FirstOrDefault(modEntry => modEntry.Id == skinEntryId);
        if (skinEntry is null)
            throw new InvalidOperationException("Skin entry not found");
        using var disableWatcher = DisableWatcher();
        var mod = skinEntry.Mod;
        _mods.Remove(skinEntry);
        mod.Delete(moveToRecycleBin);
        _logger?.Information("{Operation} mod {ModName} from {CharacterName} modList",
            moveToRecycleBin ? "Recycled" : "Deleted", mod.Name, Character.DisplayName);
    }

    public bool IsMultipleModsActive(bool perSkin = false)
    {
        return _mods.Count(mod => mod.IsEnabled) > 1;
    }

    private static bool IsModFolderEnabled(string folderName)
        => !IsModFolderDisabled(folderName);

    private static bool IsModFolderDisabled(string folderName) =>
        folderName.StartsWith(DISABLED_PREFIX, StringComparison.CurrentCultureIgnoreCase) ||
        folderName.StartsWith(ALT_DISABLED_PREFIX, StringComparison.CurrentCultureIgnoreCase);
}

public class ModFolderChangedArgs : EventArgs
{
    public ModFolderChangedArgs(string newName, ModFolderChangeType changeType, string? oldName = null)
    {
        if (changeType == ModFolderChangeType.Renamed && oldName is null)
            throw new ArgumentException("Old name must be provided when change type is renamed", nameof(oldName));

        ArgumentNullException.ThrowIfNull(newName);

        NewName = newName;
        ChangeType = changeType;
        OldName = oldName;
    }

    string NewName { get; }
    string? OldName { get; }
    ModFolderChangeType ChangeType { get; }
}

public enum ModFolderChangeType
{
    Created,
    Deleted,
    Renamed
}

public class DisableWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;

    public DisableWatcher(FileSystemWatcher watcher)
    {
        _watcher = watcher;
        _watcher.EnableRaisingEvents = false;
    }

    public void Dispose()
        => _watcher.EnableRaisingEvents = true;
}