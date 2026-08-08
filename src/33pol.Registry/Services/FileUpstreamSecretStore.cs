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

    private const string WeakPepperMessage =
        "Gateway:Security:KeyPepper must be set to a strong, non-default value before the gateway will "
        + "store or read upstream provider credentials: it is the encryption key for the secrets file, "
        + "and a published default leaves every stored provider key readable by anyone who obtains that "
        + "file. Set the GATEWAY_KEY_PEPPER environment variable.";

    private readonly GatewayOptions _gatewayOptions;

    /// <summary>Null when no usable pepper is configured; the store then refuses to read or write secrets.</summary>
    private readonly byte[]? _key;

    private readonly ILogger<FileUpstreamSecretStore> _logger;
    private readonly object _lock = new();
    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Decrypted credentials, so the hot path does not repeat AES-GCM work per request.</summary>
    private readonly Dictionary<string, string> _plaintextCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<string> _undecryptableWarned = new(StringComparer.OrdinalIgnoreCase);

    public FileUpstreamSecretStore(
        IOptions<GatewayOptions> gatewayOptions,
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger<FileUpstreamSecretStore> logger)
    {
        _gatewayOptions = gatewayOptions.Value;
        _logger = logger;

        var pepper = ResolvePepper(configuration, environment, logger);
        _key = pepper is null ? null : UpstreamSecretFileCipher.DeriveKey(pepper);
        LoadFromDisk();
    }

    /// <summary>
    /// Resolves the pepper that encrypts every upstream provider credential this gateway holds, or
    /// null when there is no usable one.
    /// </summary>
    /// <remarks>
    /// Falling back to a published constant meant the secrets file was decryptable by anyone who
    /// obtained it. Outside Development a missing or well-known pepper therefore disables the store
    /// rather than silently encrypting with a known key. It disables rather than throwing so a
    /// deployment that stores no upstream credentials at all still starts — the refusal lands on the
    /// operation that would have used the key, where it can be reported to whoever triggered it.
    /// </remarks>
    private static string? ResolvePepper(
        IConfiguration configuration,
        IHostEnvironment environment,
        ILogger logger)
    {
        var pepper = configuration["Gateway:Security:KeyPepper"]
            ?? configuration["Gateway:Bootstrap:KeyPepper"];

        if (!string.IsNullOrWhiteSpace(pepper) && !WellKnownWeakSecrets.IsWeakPepper(pepper))
        {
            return pepper;
        }

        if (!environment.IsDevelopment())
        {
            logger.LogError(
                "No strong key pepper is configured, so upstream provider credentials cannot be stored "
                + "or read. {Message}",
                WeakPepperMessage);
            return null;
        }

        logger.LogWarning(
            "No key pepper configured; upstream credentials are encrypted with the development default "
            + "and are NOT protected. Set GATEWAY_KEY_PEPPER before storing real provider keys.");
        return DevelopmentPepper;
    }

    /// <summary>
    /// Returns the decrypted upstream credential for a model.
    /// </summary>
    /// <remarks>
    /// The plaintext is memoised. This runs on the inference hot path — once per forwarded request —
    /// and decrypting from scratch each time allocated an <see cref="System.Security.Cryptography.AesGcm"/>
    /// instance plus the plaintext string under a global lock for every single request. The
    /// plaintext is in this process's memory either way the instant it is used as a bearer token, so
    /// holding it buys no additional exposure.
    /// </remarks>
    public bool TryGet(string modelId, out string? secret)
    {
        secret = null;
        if (_key is null)
        {
            return false;
        }

        var id = modelId.Trim();

        lock (_lock)
        {
            if (_plaintextCache.TryGetValue(id, out var cached))
            {
                secret = cached;
                return true;
            }

            if (!_cache.TryGetValue(id, out var cipher))
            {
                return false;
            }

            if (!UpstreamSecretFileCipher.TryDecrypt(cipher, _key, out var plain))
            {
                // Almost always a rotated pepper: the stored ciphertext was sealed with a different
                // key and is now unrecoverable. Said once per model, loudly, because the symptom
                // otherwise is every request to that model failing with an opaque
                // "upstream auth token not configured".
                if (_undecryptableWarned.Add(id))
                {
                    _logger.LogError(
                        "Stored upstream credential for model '{ModelId}' cannot be decrypted with the "
                        + "configured Gateway:Security:KeyPepper. This is what a rotated pepper looks like: "
                        + "existing secrets were sealed with the previous value and cannot be recovered. "
                        + "Re-enter this model's upstream API key in the admin UI.",
                        id);
                }

                return false;
            }

            _plaintextCache[id] = plain;
            secret = plain;
            return true;
        }
    }

    /// <summary>
    /// Reports how many stored credentials cannot be decrypted with the configured pepper.
    /// </summary>
    /// <remarks>
    /// Called at startup so a rotated pepper is discovered when the gateway boots, rather than one
    /// failing request at a time after it is already serving traffic.
    /// </remarks>
    public (int Total, int Undecryptable) VerifyStoredSecrets()
    {
        if (_key is null)
        {
            return (0, 0);
        }

        lock (_lock)
        {
            var undecryptable = _cache.Count(entry =>
                !UpstreamSecretFileCipher.TryDecrypt(entry.Value, _key, out _));
            return (_cache.Count, undecryptable);
        }
    }

    public async Task PutAsync(string modelId, string secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        if (_key is null)
        {
            throw new InvalidOperationException(WeakPepperMessage);
        }

        var trimmedSecret = secret.Trim();
        var cipher = UpstreamSecretFileCipher.Encrypt(trimmedSecret, _key);
        lock (_lock)
        {
            var id = modelId.Trim();
            _cache[id] = cipher;
            _plaintextCache[id] = trimmedSecret;
            _undecryptableWarned.Remove(id);
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
            var id = modelId.Trim();
            removed = _cache.Remove(id);
            _plaintextCache.Remove(id);
            _undecryptableWarned.Remove(id);
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

        var createdDirectory = !Directory.Exists(directory);
        Directory.CreateDirectory(directory);
        if (createdDirectory && !OperatingSystem.IsWindows())
        {
            // Owner-only: this directory holds every upstream provider credential the gateway has.
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

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
