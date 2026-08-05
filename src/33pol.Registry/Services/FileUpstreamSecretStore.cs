using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pol33.Core.Abstractions;
using Pol33.Core.Configuration;
using Pol33.Core.Security;

namespace Pol33.Registry.Services;

public sealed class FileUpstreamSecretStore : IUpstreamSecretStore
{
    /// <summary>Published development default; rejected outside Development (see <c>WellKnownWeakSecrets</c>).</summary>
    private const string DevelopmentPepper = "dev-pepper-change-me";

    private readonly GatewayOptions _gatewayOptions;
    private readonly byte[] _key;
    private readonly ILogger<FileUpstreamSecretStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public FileUpstreamSecretStore(
        IOptions<GatewayOptions> gatewayOptions,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<FileUpstreamSecretStore> logger)
    {
        _gatewayOptions = gatewayOptions.Value;
        _logger = logger;
        _key = UpstreamSecretFileCipher.DeriveKey(ResolvePepper(configuration, environment, logger));
        LoadFromDisk();
    }

    /// <summary>
    /// The pepper is the encryption key for every upstream provider credential this gateway holds.
    /// Falling back to a published constant meant the secrets file was decryptable by anyone who
    /// obtained it, so outside Development a missing or well-known pepper fails closed instead.
    /// </summary>
    private static string ResolvePepper(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger)
    {
        var pepper = configuration["Gateway:Security:KeyPepper"]
            ?? configuration["Gateway:Bootstrap:KeyPepper"];

        var isUsable = !string.IsNullOrWhiteSpace(pepper) && !WellKnownWeakSecrets.IsWeakPepper(pepper);
        if (isUsable)
        {
            return pepper!;
        }

        if (!environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "Gateway:Security:KeyPepper must be set to a strong, non-default value: it encrypts the "
                + "stored upstream provider credentials, and a default one leaves them readable by anyone "
                + "who can read the secrets file. Set the GATEWAY_KEY_PEPPER environment variable.");
        }

        logger.LogWarning(
            "No key pepper configured; upstream credentials are encrypted with the development default "
            + "and are NOT protected. Set GATEWAY_KEY_PEPPER before storing real provider keys.");
        return DevelopmentPepper;
    }

    public bool TryGet(string modelId, out string? secret)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(modelId, out var cipher) &&
                UpstreamSecretFileCipher.TryDecrypt(cipher, _key, out var plain))
            {
                secret = plain;
                return true;
            }
        }

        secret = null;
        return false;
    }

    public async Task PutAsync(string modelId, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        var cipher = UpstreamSecretFileCipher.Encrypt(secret.Trim(), _key);
        lock (_lock)
        {
            _cache[modelId.Trim()] = cipher;
        }

        await PersistAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Stored upstream secret for model {ModelId}.", modelId);
    }

    public async Task DeleteAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var removed = false;
        lock (_lock)
        {
            removed = _cache.Remove(modelId.Trim());
        }

        if (removed)
        {
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Removed upstream secret for model {ModelId}.", modelId);
        }
    }

    public Task<bool> ExistsAsync(string modelId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_cache.ContainsKey(modelId.Trim()));
        }
    }

    public Task<IReadOnlySet<string>> ListExistingAsync(
        IEnumerable<string> modelIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelIds);

        // Materialise outside the lock so an arbitrary caller-supplied sequence is not enumerated
        // while holding it.
        var requested = modelIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToList();

        cancellationToken.ThrowIfCancellationRequested();

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            foreach (var id in requested)
            {
                if (_cache.ContainsKey(id))
                {
                    present.Add(id);
                }
            }
        }

        return Task.FromResult<IReadOnlySet<string>>(present);
    }

    private void LoadFromDisk()
    {
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            var json = File.ReadAllText(path);
            var payload = JsonSerializer.Deserialize<SecretFilePayload>(json);
            _cache = payload?.Secrets ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load upstream secrets from {Path}; starting empty.", path);
            _cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        var path = ResolvePath();
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException($"Cannot resolve directory for '{path}'.");
        Directory.CreateDirectory(directory);

        Dictionary<string, string> snapshot;
        lock (_lock)
        {
            snapshot = new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
        }

        var payload = new SecretFilePayload { Version = 1, Secrets = snapshot };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        var tempPath = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);

        // Owner-only, set on the temp file so the secrets are never world-readable even briefly.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        try
        {
            if (File.Exists(path))
            {
                File.Replace(tempPath, path, null);
            }
            else
            {
                File.Move(tempPath, path);
            }
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }

            throw;
        }
    }

    private string ResolvePath()
    {
        var path = _gatewayOptions.UpstreamSecretsPath;
        return Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
    }

    private sealed class SecretFilePayload
    {
        public int Version { get; set; }

        public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
