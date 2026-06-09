using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders.Abstractions;

namespace SelfClaw.Desktop.ViewModels;

public sealed class SystemSettingsViewModel : ObservableObject
{
    private const string ApiKeySecretName = "api_key";
    private const string DefaultApiFormatOptionName = "default_api_format";

    private readonly IAiProviderRepository _aiProviderRepository;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<SystemSettingsViewModel> _logger;
    private readonly List<AiProviderSettingsItem> _allEnabledProviders = [];
    private readonly List<AiProviderSettingsItem> _allDisabledProviders = [];
    private readonly List<AiModelSettingsItem> _allModelItems = [];
    private AiProviderSettingsItem? _selectedProvider;
    private AiModelSettingsItem? _selectedConnectivityModel;
    private string _providerSearchText = string.Empty;
    private string _modelSearchText = string.Empty;
    private string _connectionStatusText = "未检查";
    private string _autoSaveStatusText = "修改会在失焦或切换服务商时自动保存";
    private bool _initialized;
    private bool _isSwitchingProvider;
    private bool _isAddProviderDialogOpen;
    private string _newProviderName = string.Empty;
    private string _newProviderBaseUrl = string.Empty;
    private AiProviderProtocolOption? _selectedAddProtocol;

    public SystemSettingsViewModel(
        IAiProviderRepository aiProviderRepository,
        ISecretProtector secretProtector,
        ILogger<SystemSettingsViewModel> logger)
    {
        _aiProviderRepository = aiProviderRepository;
        _secretProtector = secretProtector;
        _logger = logger;

        SelectProviderCommand = new AsyncRelayCommand<AiProviderSettingsItem>(SelectProviderAsync);
        ToggleSelectedProviderEnabledCommand = new AsyncRelayCommand(ToggleSelectedProviderEnabledAsync);
        OpenAddProviderDialogCommand = new RelayCommand(OpenAddProviderDialog);
        CancelAddProviderCommand = new RelayCommand(CancelAddProviderDialog);
        AddProviderCommand = new AsyncRelayCommand(AddProviderAsync, () => CanAddProvider);
        RefreshModelsCommand = new RelayCommand(RefreshModels);
        AddModelCommand = new AsyncRelayCommand(AddModelAsync);
        EnableAllModelsCommand = new AsyncRelayCommand(() => SetAllModelsEnabledAsync(true));
        DisableAllModelsCommand = new AsyncRelayCommand(() => SetAllModelsEnabledAsync(false));
        ToggleModelCommand = new AsyncRelayCommand<AiModelSettingsItem>(ToggleModelEnabledAsync);
        CheckConnectivityCommand = new AsyncRelayCommand(CheckConnectivityAsync);

        AddProtocolOptions =
        [
            new(
                "openai-chat",
                "OpenAI Chat Completions 标准",
                AiProviderKind.OpenAICompatible,
                AiProviderApiFormat.OpenAIChatCompletions,
                "https://api.example.com/v1"),
            new(
                "openai-responses",
                "OpenAI Responses API",
                AiProviderKind.OpenAI,
                AiProviderApiFormat.OpenAIResponses,
                "https://api.openai.com/v1"),
            new(
                "anthropic",
                "Anthropic Messages",
                AiProviderKind.Anthropic,
                AiProviderApiFormat.AnthropicMessages,
                "https://api.anthropic.com")
        ];
        SelectedAddProtocol = AddProtocolOptions[0];
    }

    public ObservableCollection<AiProviderSettingsItem> EnabledProviders { get; } = [];

    public ObservableCollection<AiProviderSettingsItem> DisabledProviders { get; } = [];

    public ObservableCollection<AiModelSettingsItem> VisibleModels { get; } = [];

    public IReadOnlyList<AiProviderProtocolOption> AddProtocolOptions { get; }

    public IAsyncRelayCommand<AiProviderSettingsItem> SelectProviderCommand { get; }

    public IAsyncRelayCommand ToggleSelectedProviderEnabledCommand { get; }

    public IRelayCommand OpenAddProviderDialogCommand { get; }

    public IRelayCommand CancelAddProviderCommand { get; }

    public IAsyncRelayCommand AddProviderCommand { get; }

    public IRelayCommand RefreshModelsCommand { get; }

    public IAsyncRelayCommand AddModelCommand { get; }

    public IAsyncRelayCommand EnableAllModelsCommand { get; }

    public IAsyncRelayCommand DisableAllModelsCommand { get; }

    public IAsyncRelayCommand<AiModelSettingsItem> ToggleModelCommand { get; }

    public IAsyncRelayCommand CheckConnectivityCommand { get; }

    public AiProviderSettingsItem? SelectedProvider
    {
        get => _selectedProvider;
        private set => SetProperty(ref _selectedProvider, value);
    }

    public AiModelSettingsItem? SelectedConnectivityModel
    {
        get => _selectedConnectivityModel;
        set => SetProperty(ref _selectedConnectivityModel, value);
    }

    public string ProviderSearchText
    {
        get => _providerSearchText;
        set
        {
            if (SetProperty(ref _providerSearchText, value))
            {
                RefreshProviderLists();
            }
        }
    }

    public string ModelSearchText
    {
        get => _modelSearchText;
        set
        {
            if (SetProperty(ref _modelSearchText, value))
            {
                RefreshVisibleModels();
            }
        }
    }

    public string ConnectionStatusText
    {
        get => _connectionStatusText;
        private set => SetProperty(ref _connectionStatusText, value);
    }

    public string AutoSaveStatusText
    {
        get => _autoSaveStatusText;
        private set => SetProperty(ref _autoSaveStatusText, value);
    }

    public bool IsAddProviderDialogOpen
    {
        get => _isAddProviderDialogOpen;
        private set => SetProperty(ref _isAddProviderDialogOpen, value);
    }

    public string NewProviderName
    {
        get => _newProviderName;
        set
        {
            if (SetProperty(ref _newProviderName, value))
            {
                RefreshAddProviderState();
            }
        }
    }

    public string NewProviderBaseUrl
    {
        get => _newProviderBaseUrl;
        set
        {
            if (SetProperty(ref _newProviderBaseUrl, value))
            {
                RefreshAddProviderState();
            }
        }
    }

    public AiProviderProtocolOption? SelectedAddProtocol
    {
        get => _selectedAddProtocol;
        set
        {
            if (!SetProperty(ref _selectedAddProtocol, value))
            {
                return;
            }

            if (value is not null && string.IsNullOrWhiteSpace(NewProviderBaseUrl))
            {
                NewProviderBaseUrl = value.DefaultEndpoint;
            }

            RefreshAddProviderState();
        }
    }

    public bool CanAddProvider
        => SelectedAddProtocol is not null &&
           !string.IsNullOrWhiteSpace(NewProviderName) &&
           Uri.TryCreate(NewProviderBaseUrl.Trim(), UriKind.Absolute, out _);

    public int TotalModelCount => _allModelItems.Count;

    public int EnabledModelCount => _allModelItems.Count(item => item.IsModelEnabled);

    public string ModelCountBadge => $"{EnabledModelCount} / {TotalModelCount}";

    public string ModelSummaryText => $"共 {TotalModelCount} 个模型，已启用 {EnabledModelCount}";

    public bool HasDisabledProviders => DisabledProviders.Count > 0;

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ReloadProvidersAsync();
    }

    public async Task SaveSelectedProviderAsync()
    {
        if (SelectedProvider is not null)
        {
            await SaveProviderAsync(SelectedProvider);
        }
    }

    private async Task ReloadProvidersAsync()
    {
        IReadOnlyList<AiProviderConnection> connections;
        try
        {
            connections = await _aiProviderRepository.ListAllProviderConnectionsAsync();
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load AI provider connections.");
            AutoSaveStatusText = "读取服务商配置失败";
            connections = [];
        }

        _allEnabledProviders.Clear();
        _allDisabledProviders.Clear();
        foreach (var item in connections.Select(CreateEnabledProviderItem))
        {
            if (item.IsProviderEnabled)
            {
                _allEnabledProviders.Add(item);
            }
            else
            {
                _allDisabledProviders.Add(item);
            }
        }

        RebuildDisabledProviderCatalog();
        RefreshProviderLists();

        var nextSelection = _allEnabledProviders.FirstOrDefault() ?? _allDisabledProviders.FirstOrDefault();
        if (nextSelection is not null)
        {
            await SelectProviderAsync(nextSelection);
        }
    }

    private AiProviderSettingsItem CreateEnabledProviderItem(AiProviderConnection connection)
    {
        var defaultApiFormat = ResolveDefaultApiFormat(connection);
        return new AiProviderSettingsItem(
            ResolveCatalogId(connection.Name, connection.ProviderKind, defaultApiFormat),
            connection.Id,
            connection.Name,
            GetIconText(connection.Name, connection.ProviderKind),
            connection.ProviderKind,
            defaultApiFormat,
            connection.Endpoint.AbsoluteUri.TrimEnd('/'),
            isProviderEnabled: connection.IsEnabled,
            isCustom: !IsKnownCatalogName(connection.Name),
            connection);
    }

    private void RebuildDisabledProviderCatalog()
    {
    }

    private void RefreshProviderLists()
    {
        var searchText = ProviderSearchText.Trim();
        ReplaceItems(EnabledProviders, _allEnabledProviders.Where(item => MatchesProviderSearch(item, searchText)));
        ReplaceItems(DisabledProviders, _allDisabledProviders.Where(item => MatchesProviderSearch(item, searchText)));
        OnPropertyChanged(nameof(HasDisabledProviders));
    }

    private async Task SelectProviderAsync(AiProviderSettingsItem? provider)
    {
        if (provider is null || ReferenceEquals(provider, SelectedProvider) || _isSwitchingProvider)
        {
            return;
        }

        _isSwitchingProvider = true;
        try
        {
            var previous = SelectedProvider;
            if (previous is not null)
            {
                await SaveProviderAsync(previous);
                previous.IsSelected = false;
            }

            provider.IsSelected = true;
            SelectedProvider = provider;
            ConnectionStatusText = "未检查";
            ModelSearchText = string.Empty;
            await LoadApiKeyPlaceholderAsync(provider);
            await LoadModelsAsync(provider);
        }
        finally
        {
            _isSwitchingProvider = false;
        }
    }

    private async Task LoadApiKeyPlaceholderAsync(AiProviderSettingsItem provider)
    {
        if (!provider.CredentialRefs.TryGetValue(ApiKeySecretName, out var secretRef) ||
            string.IsNullOrWhiteSpace(secretRef))
        {
            provider.SetApiKeyFromStoredSecret(null);
            return;
        }

        try
        {
            var secret = await _secretProtector.RetrieveSecretAsync(secretRef);
            provider.SetApiKeyFromStoredSecret(secret);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load AI provider API key placeholder. ProviderId={ProviderId}", provider.ConnectionId);
            provider.SetApiKeyFromStoredSecret(null);
        }
    }

    private async Task LoadModelsAsync(AiProviderSettingsItem provider)
    {
        IReadOnlyList<AiModelProfile> storedProfiles = [];
        if (provider.ConnectionId.HasValue)
        {
            try
            {
                storedProfiles = await _aiProviderRepository.ListModelProfilesAsync(provider.ConnectionId.Value);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to load AI model profiles. ProviderId={ProviderId}", provider.ConnectionId);
                AutoSaveStatusText = "读取模型列表失败";
            }
        }

        var storedByModel = storedProfiles.ToDictionary(profile => profile.Model, StringComparer.OrdinalIgnoreCase);
        var modelItems = CreateModelCatalog(provider)
            .Select(model =>
            {
                storedByModel.TryGetValue(model.ModelId, out var storedProfile);
                return new AiModelSettingsItem(
                    storedProfile?.Id,
                    model.Name,
                    model.ModelId,
                    model.Badge,
                    model.ContextText,
                    model.CostText,
                    model.CacheText,
                    storedProfile?.ApiFormat ?? provider.DefaultApiFormat,
                    storedProfile is not null);
            })
            .ToList();

        foreach (var storedProfile in storedProfiles)
        {
            if (modelItems.Any(item => string.Equals(item.ModelId, storedProfile.Model, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            modelItems.Add(new AiModelSettingsItem(
                storedProfile.Id,
                storedProfile.Name,
                storedProfile.Model,
                storedProfile.Model,
                "自定义模型",
                "手动添加",
                "价格按服务商计费",
                storedProfile.ApiFormat,
                true));
        }

        _allModelItems.Clear();
        _allModelItems.AddRange(modelItems);
        RefreshVisibleModels();
        NotifyModelSummaryChanged();
        SelectedConnectivityModel = _allModelItems.FirstOrDefault(item => item.IsModelEnabled) ?? _allModelItems.FirstOrDefault();
    }

    private void RefreshVisibleModels()
    {
        var searchText = ModelSearchText.Trim();
        ReplaceItems(VisibleModels, _allModelItems.Where(item => MatchesModelSearch(item, searchText)));
    }

    private async Task ToggleSelectedProviderEnabledAsync()
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }

        if (provider.IsProviderEnabled)
        {
            await EnableProviderAsync(provider);
            return;
        }

        await DisableProviderAsync(provider);
    }

    private async Task EnableProviderAsync(AiProviderSettingsItem provider)
    {
        if (provider.ConnectionId.HasValue)
        {
            await _aiProviderRepository.SetProviderConnectionEnabledAsync(provider.ConnectionId.Value, true);
            provider.Connection = provider.Connection is null
                ? provider.Connection
                : provider.Connection with
                {
                    IsEnabled = true,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            provider.IsProviderEnabled = true;
            if (!_allEnabledProviders.Contains(provider))
            {
                _allEnabledProviders.Add(provider);
            }

            _allDisabledProviders.Remove(provider);
            RefreshProviderLists();
            await SaveProviderAsync(provider);
            AutoSaveStatusText = "服务商已启用";
            return;
        }

        if (!Uri.TryCreate(provider.EndpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            provider.IsProviderEnabled = false;
            AutoSaveStatusText = "Base URL 格式无效，无法启用";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(),
            provider.Name,
            provider.ProviderKind,
            endpoint,
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BuildConnectionOptions(provider.DefaultApiFormat),
            now,
            now);

        try
        {
            await _aiProviderRepository.UpsertProviderConnectionAsync(connection);
            provider.ConnectionId = connection.Id;
            provider.Connection = connection;
            provider.IsProviderEnabled = true;
            provider.CredentialRefs.Clear();
            _allEnabledProviders.Add(provider);
            _allDisabledProviders.Remove(provider);
            RebuildDisabledProviderCatalog();
            RefreshProviderLists();
            await LoadModelsAsync(provider);
            AutoSaveStatusText = "服务商已启用";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to enable AI provider. Name={ProviderName}", provider.Name);
            provider.IsProviderEnabled = false;
            AutoSaveStatusText = "启用服务商失败";
        }
    }

    private async Task DisableProviderAsync(AiProviderSettingsItem provider)
    {
        if (!provider.ConnectionId.HasValue)
        {
            return;
        }

        try
        {
            await _aiProviderRepository.SetProviderConnectionEnabledAsync(provider.ConnectionId.Value, false);
            provider.Connection = provider.Connection is null
                ? provider.Connection
                : provider.Connection with
                {
                    IsEnabled = false,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            provider.IsProviderEnabled = false;
            _allEnabledProviders.Remove(provider);
            if (!_allDisabledProviders.Contains(provider))
            {
                _allDisabledProviders.Insert(0, provider);
            }

            RebuildDisabledProviderCatalog();
            RefreshProviderLists();
            AutoSaveStatusText = "服务商已禁用";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to disable AI provider. ProviderId={ProviderId}", provider.ConnectionId);
            provider.IsProviderEnabled = true;
            AutoSaveStatusText = "禁用服务商失败";
        }
    }

    private async Task SaveProviderAsync(AiProviderSettingsItem provider)
    {
        if (!provider.IsProviderEnabled || !provider.ConnectionId.HasValue)
        {
            return;
        }

        if (!Uri.TryCreate(provider.EndpointText.Trim(), UriKind.Absolute, out var endpoint))
        {
            AutoSaveStatusText = "Base URL 格式无效，未保存";
            return;
        }

        var credentialRefs = new Dictionary<string, string>(provider.CredentialRefs, StringComparer.OrdinalIgnoreCase);
        if (!provider.IsApiKeyMasked)
        {
            if (string.IsNullOrWhiteSpace(provider.ApiKeyInput))
            {
                if (credentialRefs.TryGetValue(ApiKeySecretName, out var secretRef))
                {
                    await _secretProtector.DeleteSecretAsync(secretRef);
                    credentialRefs.Remove(ApiKeySecretName);
                }
            }
            else
            {
                credentialRefs.TryGetValue(ApiKeySecretName, out var existingSecretRef);
                var secretRef = await _secretProtector.StoreSecretAsync(provider.ApiKeyInput.Trim(), existingSecretRef);
                credentialRefs[ApiKeySecretName] = secretRef;
                provider.SetApiKeyFromStoredSecret(provider.ApiKeyInput.Trim());
            }
        }

        var createdAtUtc = provider.Connection?.CreatedAtUtc ?? DateTimeOffset.UtcNow;
        var updatedConnection = new AiProviderConnection(
            provider.ConnectionId.Value,
            provider.Name,
            provider.ProviderKind,
            endpoint,
            AiProviderAuthKind.ApiKey,
            credentialRefs,
            BuildConnectionOptions(provider.DefaultApiFormat),
            createdAtUtc,
            DateTimeOffset.UtcNow,
            provider.IsProviderEnabled);

        try
        {
            await _aiProviderRepository.UpsertProviderConnectionAsync(updatedConnection);
            provider.Connection = updatedConnection;
            provider.CredentialRefs.ReplaceWith(credentialRefs);
            AutoSaveStatusText = "已自动保存";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to save AI provider settings. ProviderId={ProviderId}", provider.ConnectionId);
            AutoSaveStatusText = "自动保存失败";
        }
    }

    private void OpenAddProviderDialog()
    {
        NewProviderName = string.Empty;
        SelectedAddProtocol = AddProtocolOptions[0];
        NewProviderBaseUrl = SelectedAddProtocol.DefaultEndpoint;
        IsAddProviderDialogOpen = true;
    }

    private void CancelAddProviderDialog()
    {
        IsAddProviderDialogOpen = false;
    }

    private async Task AddProviderAsync()
    {
        if (!CanAddProvider || SelectedAddProtocol is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var providerKind = ResolveProviderKindForEndpoint(SelectedAddProtocol, NewProviderBaseUrl.Trim());
        var connection = new AiProviderConnection(
            Guid.NewGuid(),
            NewProviderName.Trim(),
            providerKind,
            new Uri(NewProviderBaseUrl.Trim(), UriKind.Absolute),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            BuildConnectionOptions(SelectedAddProtocol.ApiFormat),
            now,
            now);

        try
        {
            await _aiProviderRepository.UpsertProviderConnectionAsync(connection);
            var item = CreateEnabledProviderItem(connection);
            _allEnabledProviders.Insert(0, item);
            RebuildDisabledProviderCatalog();
            RefreshProviderLists();
            IsAddProviderDialogOpen = false;
            await SelectProviderAsync(item);
            AutoSaveStatusText = "服务商已添加";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to add AI provider. Name={ProviderName}", NewProviderName);
            AutoSaveStatusText = "添加服务商失败";
        }
    }

    private void RefreshModels()
    {
        ConnectionStatusText = "模型列表已刷新";
        RefreshVisibleModels();
    }

    private async Task AddModelAsync()
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }

        if (!provider.IsProviderEnabled)
        {
            provider.IsProviderEnabled = true;
            await EnableProviderAsync(provider);
        }

        if (!provider.ConnectionId.HasValue)
        {
            return;
        }

        var index = _allModelItems.Count(item => item.Name.StartsWith("Custom model", StringComparison.OrdinalIgnoreCase)) + 1;
        var modelId = $"custom-model-{index}";
        var model = new AiModelSettingsItem(
            null,
            $"Custom model {index}",
            modelId,
            modelId,
            "自定义模型",
            "手动添加",
            "价格按服务商计费",
            provider.DefaultApiFormat,
            true);

        await UpsertModelAsync(provider, model);
        _allModelItems.Insert(0, model);
        RefreshVisibleModels();
        NotifyModelSummaryChanged();
        AutoSaveStatusText = "模型已添加";
    }

    private async Task ToggleModelEnabledAsync(AiModelSettingsItem? model)
    {
        var provider = SelectedProvider;
        if (provider is null || model is null)
        {
            return;
        }

        if (!provider.IsProviderEnabled)
        {
            provider.IsProviderEnabled = true;
            await EnableProviderAsync(provider);
        }

        if (model.IsModelEnabled)
        {
            await UpsertModelAsync(provider, model);
        }
        else
        {
            await DeleteModelAsync(model);
        }

        NotifyModelSummaryChanged();
    }

    private async Task SetAllModelsEnabledAsync(bool isEnabled)
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            return;
        }

        if (isEnabled && !provider.IsProviderEnabled)
        {
            provider.IsProviderEnabled = true;
            await EnableProviderAsync(provider);
        }

        foreach (var model in _allModelItems)
        {
            model.IsModelEnabled = isEnabled;
            if (isEnabled)
            {
                await UpsertModelAsync(provider, model);
            }
            else
            {
                await DeleteModelAsync(model);
            }
        }

        NotifyModelSummaryChanged();
        AutoSaveStatusText = isEnabled ? "全部模型已启用" : "全部模型已禁用";
    }

    private async Task UpsertModelAsync(AiProviderSettingsItem provider, AiModelSettingsItem model)
    {
        if (!provider.ConnectionId.HasValue)
        {
            model.IsModelEnabled = false;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var profile = new AiModelProfile(
            model.ModelProfileId ?? Guid.NewGuid(),
            provider.ConnectionId.Value,
            model.Name,
            model.ApiFormat,
            model.ModelId,
            new AiSamplingOptions(false, 0.7, false, 0.7),
            new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            now,
            now);

        try
        {
            await _aiProviderRepository.UpsertModelProfileAsync(profile);
            model.ModelProfileId = profile.Id;
            model.IsModelEnabled = true;
            AutoSaveStatusText = "模型配置已自动保存";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to save AI model profile. Model={Model}", model.ModelId);
            model.IsModelEnabled = false;
            AutoSaveStatusText = "模型保存失败";
        }
    }

    private async Task DeleteModelAsync(AiModelSettingsItem model)
    {
        if (!model.ModelProfileId.HasValue)
        {
            model.IsModelEnabled = false;
            return;
        }

        try
        {
            await _aiProviderRepository.DeleteModelProfileAsync(model.ModelProfileId.Value);
            model.ModelProfileId = null;
            model.IsModelEnabled = false;
            AutoSaveStatusText = "模型已禁用";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to delete AI model profile. ModelProfileId={ModelProfileId}", model.ModelProfileId);
            model.IsModelEnabled = true;
            AutoSaveStatusText = "模型禁用失败";
        }
    }

    private async Task CheckConnectivityAsync()
    {
        var provider = SelectedProvider;
        if (provider is null)
        {
            ConnectionStatusText = "请选择服务商";
            return;
        }

        if (SelectedConnectivityModel is null)
        {
            ConnectionStatusText = "请选择模型";
            return;
        }

        if (!provider.IsProviderEnabled)
        {
            ConnectionStatusText = "启用服务商后可检查";
            return;
        }

        await SaveProviderAsync(provider);

        var apiKey = await ResolveApiKeyAsync(provider);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            ConnectionStatusText = "请先填写 API Key";
            return;
        }

        try
        {
            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(12)
            };
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildConnectivityUri(provider));

            if (provider.DefaultApiFormat == AiProviderApiFormat.AnthropicMessages)
            {
                request.Headers.Add("x-api-key", apiKey);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await httpClient.SendAsync(request);
            ConnectionStatusText = response.IsSuccessStatusCode
                ? $"检查通过：{SelectedConnectivityModel.ModelId}"
                : $"检查失败：HTTP {(int)response.StatusCode}";
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "AI provider connectivity check failed. ProviderId={ProviderId}", provider.ConnectionId);
            ConnectionStatusText = "检查失败：无法访问服务";
        }
    }

    private async Task<string?> ResolveApiKeyAsync(AiProviderSettingsItem provider)
    {
        if (!provider.IsApiKeyMasked && !string.IsNullOrWhiteSpace(provider.ApiKeyInput))
        {
            return provider.ApiKeyInput.Trim();
        }

        return provider.CredentialRefs.TryGetValue(ApiKeySecretName, out var secretRef) && !string.IsNullOrWhiteSpace(secretRef)
            ? await _secretProtector.RetrieveSecretAsync(secretRef)
            : null;
    }

    private static Uri BuildConnectivityUri(AiProviderSettingsItem provider)
    {
        var endpoint = provider.EndpointText.Trim().TrimEnd('/');
        if (provider.DefaultApiFormat == AiProviderApiFormat.AnthropicMessages &&
            !endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            endpoint += "/v1";
        }

        return new Uri($"{endpoint}/models", UriKind.Absolute);
    }

    private void RefreshAddProviderState()
    {
        OnPropertyChanged(nameof(CanAddProvider));
        AddProviderCommand.NotifyCanExecuteChanged();
    }

    private void NotifyModelSummaryChanged()
    {
        OnPropertyChanged(nameof(TotalModelCount));
        OnPropertyChanged(nameof(EnabledModelCount));
        OnPropertyChanged(nameof(ModelCountBadge));
        OnPropertyChanged(nameof(ModelSummaryText));
    }

    private static bool MatchesProviderSearch(AiProviderSettingsItem item, string searchText)
        => string.IsNullOrWhiteSpace(searchText) ||
           item.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
           item.ProtocolLabel.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private static bool MatchesModelSearch(AiModelSettingsItem item, string searchText)
        => string.IsNullOrWhiteSpace(searchText) ||
           item.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
           item.ModelId.Contains(searchText, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<AiProviderSettingsItem> CreateProviderCatalog()
        => [];

    private static AiProviderSettingsItem CreateCatalogItem(
        string catalogId,
        string name,
        string iconText,
        AiProviderKind providerKind,
        AiProviderApiFormat apiFormat,
        string endpoint)
        => new(catalogId, null, name, iconText, providerKind, apiFormat, endpoint, false, false, null);

    private static IReadOnlyList<AiModelSettingsItem> CreateModelCatalog(AiProviderSettingsItem provider)
    {
        if (provider.DefaultApiFormat == AiProviderApiFormat.AnthropicMessages)
        {
            return
            [
                new(null, "Claude Sonnet", "claude-sonnet-latest", "sonnet", "200K context", "高性能通用模型", "价格按 Anthropic 账户计费", provider.DefaultApiFormat, false),
                new(null, "Claude Opus", "claude-opus-latest", "opus", "200K context", "复杂推理与代码", "价格按 Anthropic 账户计费", provider.DefaultApiFormat, false),
                new(null, "Claude Haiku", "claude-haiku-latest", "haiku", "200K context", "低延迟轻量模型", "价格按 Anthropic 账户计费", provider.DefaultApiFormat, false)
            ];
        }

        return
        [
            new(null, "GPT 5.2", "gpt-5.2", "gpt-5.2", "391K context", "通用与代码任务", "cache: 写入 / 读取按服务商计费", provider.DefaultApiFormat, false),
            new(null, "GPT 5.1", "gpt-5.1", "gpt-5.1", "256K context", "日常编程与对话", "价格按服务商账户计费", provider.DefaultApiFormat, false),
            new(null, "GPT 4.1", "gpt-4.1", "gpt-4.1", "128K context", "稳定通用模型", "价格按服务商账户计费", provider.DefaultApiFormat, false),
            new(null, "Reasoning", "o3", "o3", "200K context", "推理模型", "价格按服务商账户计费", provider.DefaultApiFormat, false),
            new(null, "Mini", "gpt-4.1-mini", "mini", "128K context", "轻量快速模型", "价格按服务商账户计费", provider.DefaultApiFormat, false)
        ];
    }

    private static string ResolveCatalogId(string name, AiProviderKind providerKind, AiProviderApiFormat apiFormat)
    {
        var known = CreateProviderCatalog().FirstOrDefault(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase) &&
            item.ProviderKind == providerKind);
        return known?.CatalogId ?? $"custom:{providerKind}:{apiFormat}:{name}".ToLowerInvariant();
    }

    private static bool IsKnownCatalogName(string name)
        => CreateProviderCatalog().Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));

    private static AiProviderKind ResolveProviderKindForEndpoint(AiProviderProtocolOption option, string endpointText)
    {
        if (option.ApiFormat == AiProviderApiFormat.AnthropicMessages)
        {
            return AiProviderKind.Anthropic;
        }

        if (Uri.TryCreate(endpointText, UriKind.Absolute, out var endpoint) &&
            string.Equals(endpoint.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            return AiProviderKind.OpenAI;
        }

        return AiProviderKind.OpenAICompatible;
    }

    private static string GetIconText(string name, AiProviderKind kind)
        => kind switch
        {
            AiProviderKind.OpenAI => "O",
            AiProviderKind.Anthropic => "A",
            AiProviderKind.DeepSeek => "D",
            _ => string.IsNullOrWhiteSpace(name) ? "AI" : name[..Math.Min(2, name.Length)]
        };

    private static AiProviderApiFormat ResolveDefaultApiFormat(AiProviderConnection connection)
    {
        if (connection.ConnectionOptions.TryGetValue(DefaultApiFormatOptionName, out var value))
        {
            try
            {
                if (value.ValueKind == JsonValueKind.Number)
                {
                    return (AiProviderApiFormat)value.GetInt32();
                }

                if (value.ValueKind == JsonValueKind.String &&
                    Enum.TryParse(value.GetString(), ignoreCase: true, out AiProviderApiFormat parsed))
                {
                    return parsed;
                }
            }
            catch (Exception)
            {
                return DefaultApiFormatForKind(connection.ProviderKind);
            }
        }

        return DefaultApiFormatForKind(connection.ProviderKind);
    }

    private static AiProviderApiFormat DefaultApiFormatForKind(AiProviderKind kind)
        => kind == AiProviderKind.Anthropic
            ? AiProviderApiFormat.AnthropicMessages
            : AiProviderApiFormat.OpenAIChatCompletions;

    private static IReadOnlyDictionary<string, JsonElement> BuildConnectionOptions(AiProviderApiFormat apiFormat)
        => new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase)
        {
            [DefaultApiFormatOptionName] = JsonSerializer.SerializeToElement(apiFormat.ToString())
        };

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }
}

public sealed class AiProviderSettingsItem : ObservableObject
{
    private bool _isProviderEnabled;
    private bool _isSelected;
    private string _endpointText;
    private string _apiKeyInput = string.Empty;
    private bool _isApiKeyMasked;
    private bool _isSettingApiKeyFromStore;

    public AiProviderSettingsItem(
        string catalogId,
        Guid? connectionId,
        string name,
        string iconText,
        AiProviderKind providerKind,
        AiProviderApiFormat defaultApiFormat,
        string endpointText,
        bool isProviderEnabled,
        bool isCustom,
        AiProviderConnection? connection)
    {
        CatalogId = catalogId;
        ConnectionId = connectionId;
        Name = name;
        IconText = iconText;
        ProviderKind = providerKind;
        DefaultApiFormat = defaultApiFormat;
        _endpointText = endpointText;
        _isProviderEnabled = isProviderEnabled;
        IsCustom = isCustom;
        Connection = connection;
        CredentialRefs = new Dictionary<string, string>(
            connection?.CredentialRefs ?? new Dictionary<string, string>(),
            StringComparer.OrdinalIgnoreCase);
    }

    public string CatalogId { get; }

    public Guid? ConnectionId { get; set; }

    public string Name { get; }

    public string IconText { get; }

    public AiProviderKind ProviderKind { get; }

    public AiProviderApiFormat DefaultApiFormat { get; }

    public bool IsCustom { get; }

    public AiProviderConnection? Connection { get; set; }

    public Dictionary<string, string> CredentialRefs { get; }

    public string ProtocolLabel
        => DefaultApiFormat switch
        {
            AiProviderApiFormat.OpenAIResponses => "OpenAI Responses API",
            AiProviderApiFormat.AnthropicMessages => "Anthropic Messages",
            _ => "OpenAI Chat Completions 兼容"
        };

    public string EndpointText
    {
        get => _endpointText;
        set => SetProperty(ref _endpointText, value);
    }

    public string ApiKeyInput
    {
        get => _apiKeyInput;
        set
        {
            if (!SetProperty(ref _apiKeyInput, value))
            {
                return;
            }

            if (!_isSettingApiKeyFromStore)
            {
                IsApiKeyMasked = false;
            }
        }
    }

    public bool IsApiKeyMasked
    {
        get => _isApiKeyMasked;
        private set => SetProperty(ref _isApiKeyMasked, value);
    }

    public bool IsProviderEnabled
    {
        get => _isProviderEnabled;
        set => SetProperty(ref _isProviderEnabled, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public void SetApiKeyFromStoredSecret(string? secret)
    {
        _isSettingApiKeyFromStore = true;
        try
        {
            ApiKeyInput = string.IsNullOrWhiteSpace(secret)
                ? string.Empty
                : MaskSecret(secret);
            IsApiKeyMasked = !string.IsNullOrWhiteSpace(secret);
        }
        finally
        {
            _isSettingApiKeyFromStore = false;
        }
    }

    private static string MaskSecret(string secret)
    {
        if (secret.Length <= 4)
        {
            return "已保存";
        }

        var prefixLength = Math.Min(3, secret.Length);
        return $"{secret[..prefixLength]}...";
    }
}

public sealed record AiProviderProtocolOption(
    string Id,
    string DisplayName,
    AiProviderKind ProviderKind,
    AiProviderApiFormat ApiFormat,
    string DefaultEndpoint);

public sealed class AiModelSettingsItem : ObservableObject
{
    private Guid? _modelProfileId;
    private bool _isModelEnabled;

    public AiModelSettingsItem(
        Guid? modelProfileId,
        string name,
        string modelId,
        string badge,
        string contextText,
        string costText,
        string cacheText,
        AiProviderApiFormat apiFormat,
        bool isModelEnabled)
    {
        _modelProfileId = modelProfileId;
        Name = name;
        ModelId = modelId;
        Badge = badge;
        ContextText = contextText;
        CostText = costText;
        CacheText = cacheText;
        ApiFormat = apiFormat;
        _isModelEnabled = isModelEnabled;
    }

    public Guid? ModelProfileId
    {
        get => _modelProfileId;
        set => SetProperty(ref _modelProfileId, value);
    }

    public string Name { get; }

    public string ModelId { get; }

    public string Badge { get; }

    public string ContextText { get; }

    public string CostText { get; }

    public string CacheText { get; }

    public AiProviderApiFormat ApiFormat { get; init; }

    public bool IsModelEnabled
    {
        get => _isModelEnabled;
        set => SetProperty(ref _isModelEnabled, value);
    }
}

internal static class DictionaryExtensions
{
    public static void ReplaceWith<TKey, TValue>(this Dictionary<TKey, TValue> target, IReadOnlyDictionary<TKey, TValue> source)
        where TKey : notnull
    {
        target.Clear();
        foreach (var pair in source)
        {
            target[pair.Key] = pair.Value;
        }
    }
}
