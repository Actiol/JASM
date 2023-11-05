﻿using System.Text.Json;
using GIMI_ModManager.Core.GamesService.Models;
using GIMI_ModManager.WinUI.Contracts.Services;
using Serilog;

namespace GIMI_ModManager.WinUI.Services.Notifications;

public class ModNotificationManager
{
    private readonly ILogger _logger;
    private readonly ILocalSettingsService _localSettingsService;

    private readonly List<ModNotification> _inMemoryModNotifications = new();
    private readonly List<ModNotification> _modNotifications = new();

    private bool _isInitialized;
    private FileInfo _modNotificationsFile = null!;

    public const string ModNotificationsFileName = "ModNotifications.json";

    public event EventHandler<ModNotificationEvent>? OnModNotification;

    private readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        AllowTrailingCommas = true,
        WriteIndented = true
    };


    public ModNotificationManager(ILogger logger, ILocalSettingsService localSettingsService)
    {
        _logger = logger;
        _localSettingsService = localSettingsService;
    }

    private async Task InitializeAsync()
    {
        if (_isInitialized)
            return;

        var appDataFolder = new DirectoryInfo(_localSettingsService.ApplicationDataFolder);
        if (!appDataFolder.Exists)
            appDataFolder.Create();


        _modNotificationsFile = new FileInfo(Path.Combine(appDataFolder.FullName, ModNotificationsFileName));

        if (!_modNotificationsFile.Exists)
        {
            await using var fileStream = _modNotificationsFile.Create();

            await JsonSerializer.SerializeAsync(fileStream, new ModNotificationsRoot(), _jsonSerializerOptions);
            return;
        }

        await using var stream = _modNotificationsFile.OpenRead();
        var modNotificationRoot =
            await JsonSerializer.DeserializeAsync<ModNotificationsRoot>(stream, _jsonSerializerOptions) ??
            new ModNotificationsRoot();

        _modNotifications.AddRange(modNotificationRoot.ModNotifications);
        _modNotifications.ForEach(x => x.IsPersistent = true);

        _isInitialized = true;
    }

    private async Task SaveModNotificationsAsync()
    {
        await InitializeAsync();

        var modNotificationsRoot = new ModNotificationsRoot
        {
            ModNotifications = _modNotifications.ToArray()
        };

        await using var fileStream =
            new FileStream(_modNotificationsFile.FullName, FileMode.Truncate, FileAccess.Write);
        await JsonSerializer.SerializeAsync(fileStream, modNotificationsRoot, _jsonSerializerOptions);
        _modNotifications.ForEach(x => x.IsPersistent = true);
    }

    public async Task AddModNotification(ModNotification modNotification, bool persistent = false)
    {
        await InitializeAsync();

        if (!persistent)
        {
            modNotification.IsPersistent = false;
            _inMemoryModNotifications.Add(modNotification);
            OnModNotification?.Invoke(this, new ModNotificationEvent(modNotification, Operation.Added));
            return;
        }

        if (_modNotifications.Any(x => x.Id == modNotification.Id))
            throw new InvalidOperationException("ModNotification with the same Id already exists");

        modNotification.IsPersistent = true;
        _modNotifications.Add(modNotification);

        await SaveModNotificationsAsync();


        OnModNotification?.Invoke(this, new ModNotificationEvent(modNotification, Operation.Added));
    }

    /// <summary>
    ///    Clears all mod notifications of <c>NotificationType</c> for a specific character or all characters
    /// </summary>
    /// <param name="character"></param>
    /// <param name="clearType"></param>
    /// <returns></returns>
    public async Task ClearModNotificationsAsync(InternalName? character = null,
        NotificationType clearType = NotificationType.All)
    {
        await InitializeAsync();

        var notifications = (await GetNotificationsAsync(clearType)).Where(x =>
                character is null || character.Equals(x.CharacterInternalName))
            .ToArray();

        if (notifications.Length == 0)
            return;

        foreach (var notification in notifications)
            _removeNotification(notification);

        if (notifications.Any(x => x.IsPersistent))
            await SaveModNotificationsAsync();


        foreach (var notification in notifications)
            OnModNotification?.Invoke(this, new ModNotificationEvent(notification, Operation.Removed));
    }

    public enum NotificationType
    {
        Persistent,
        InMemory,
        All
    }

    private void _removeNotification(ModNotification modNotification)
    {
        if (!_modNotifications.Remove(modNotification))
            _inMemoryModNotifications.Remove(modNotification);
    }

    public async Task<bool> RemoveModNotificationAsync(Guid notificationId)
    {
        await InitializeAsync();

        var notification = await GetNotificationById(notificationId, IdType.NotificationId);

        if (notification is null)
            return false;

        _removeNotification(notification);

        if (notification.IsPersistent)
            await SaveModNotificationsAsync();


        OnModNotification?.Invoke(this, new ModNotificationEvent(notification, Operation.Removed));
        return true;
    }


    public async Task<ModNotification?> GetNotificationById(Guid id, IdType type = IdType.ModId)
    {
        await InitializeAsync();

        var notification = _inMemoryModNotifications.FirstOrDefault(x =>
            type == IdType.NotificationId && x.Id == id || type == IdType.ModId && x.ModId == id);
        if (notification is not null)
        {
            notification.IsPersistent = false;
            return notification;
        }

        notification = _modNotifications.FirstOrDefault(x =>
            type == IdType.NotificationId && x.Id == id || type == IdType.ModId && x.ModId == id);

        if (notification is not null)
            notification.IsPersistent = true;

        return notification;
    }

    public async Task<ICollection<ModNotification>> GetNotificationsForInternalNameAsync(InternalName internalName,
        NotificationType type = NotificationType.All)
    {
        await InitializeAsync();

        return (await GetNotificationsAsync(type)).Where(x => internalName.Equals(x.CharacterInternalName)).ToArray();
    }

    public async Task<IReadOnlyList<ModNotification>> GetNotificationsAsync(
        NotificationType type = NotificationType.All)
    {
        await InitializeAsync();
        return type switch
        {
            NotificationType.Persistent => _modNotifications.AsReadOnly(),
            NotificationType.InMemory => _inMemoryModNotifications.AsReadOnly(),
            NotificationType.All => _modNotifications.Concat(_inMemoryModNotifications).ToArray().AsReadOnly(),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };
    }

    public enum IdType
    {
        NotificationId,
        ModId
    }


    public class ModNotificationEvent : EventArgs
    {
        public ModNotification ModNotification { get; }
        public bool IsPersistent => ModNotification.IsPersistent;
        public Operation Operation { get; }


        public ModNotificationEvent(ModNotification modNotification, Operation operation)
        {
            ModNotification = modNotification;
            Operation = operation;
        }
    }

    public enum Operation
    {
        Added,
        Removed
    }
}

public class ModNotificationsRoot
{
    public ModNotification[] ModNotifications { get; set; } = Array.Empty<ModNotification>();
}