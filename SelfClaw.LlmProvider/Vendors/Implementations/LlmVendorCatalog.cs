using System.Text.Json;

using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Vendors.Dtos;

namespace SelfClaw.LlmProvider.Vendors.Implementations;

/// <summary>
/// 一份不可变的厂商目录。库本身不带任何厂商数据：内置/用户厂商都是调用方的产品配置，
/// 由调用方从流、文本或目录加进来。本库不拼任何路径，也不读环境变量。
/// </summary>
public sealed class LlmVendorCatalog
{
    private readonly Dictionary<string, LlmVendorDescriptor> _vendors;

    public LlmVendorCatalog()
        : this(new Dictionary<string, LlmVendorDescriptor>(StringComparer.OrdinalIgnoreCase))
    {
    }

    private LlmVendorCatalog(Dictionary<string, LlmVendorDescriptor> vendors)
    {
        _vendors = vendors;
    }

    public IReadOnlyCollection<LlmVendorDescriptor> Vendors => _vendors.Values;

    public int Count => _vendors.Count;

    public LlmVendorDescriptor? Find(string vendorId) =>
        _vendors.GetValueOrDefault(vendorId);

    public LlmVendorDescriptor GetRequired(string vendorId)
    {
        if (!string.IsNullOrWhiteSpace(vendorId) && _vendors.TryGetValue(vendorId, out var vendor))
        {
            return vendor;
        }

        if (_vendors.Count == 0)
        {
            throw new LlmVendorCatalogException(
                $"No vendor is registered, so vendor id '{vendorId}' cannot be resolved. " +
                "Add one with LlmVendorCatalog.AddFromJson or AddFromDirectory.");
        }

        throw new LlmVendorCatalogException(
            $"No vendor descriptor is registered for vendor id '{vendorId}'. " +
            $"Known vendors: {string.Join(", ", _vendors.Keys.OrderBy(key => key, StringComparer.Ordinal))}.");
    }

    /// <summary>加一份描述符；同 id 覆盖先前的定义。</summary>
    public LlmVendorCatalog AddFromJson(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var document = JsonDocument.Parse(stream);
        return Add(LlmVendorJson.Parse(document));
    }

    /// <summary>加一份描述符文本。</summary>
    public LlmVendorCatalog AddFromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        return Add(LlmVendorJson.Parse(document));
    }

    /// <summary>直接加一个已构造好的描述符；同 id 覆盖。</summary>
    public LlmVendorCatalog Add(LlmVendorDescriptor vendor)
    {
        ArgumentNullException.ThrowIfNull(vendor);
        var copy = new Dictionary<string, LlmVendorDescriptor>(_vendors, StringComparer.OrdinalIgnoreCase)
        {
            [vendor.VendorId] = vendor
        };
        return new LlmVendorCatalog(copy);
    }

    /// <summary>加目录下所有 <c>*.json</c>。按文件路径排序加载，同名 id 以后出现的为准。</summary>
    public LlmVendorCatalog AddFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Directory.Exists(directory))
        {
            return this;
        }

        var result = this;
        foreach (var file in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.Ordinal))
        {
            using var stream = File.OpenRead(file);
            try
            {
                result = result.AddFromJson(stream);
            }
            catch (Exception exception) when (exception is JsonException or LlmVendorCatalogException)
            {
                throw new LlmVendorCatalogException(
                    $"Vendor descriptor '{file}' could not be loaded: {exception.Message}", exception);
            }
        }

        return result;
    }
}
