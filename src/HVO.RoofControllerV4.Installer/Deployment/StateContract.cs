using System.Text.Json;

namespace HVO.RoofControllerV4.Installer.Deployment;

/// <summary>
/// What a release does with one of the stores the controller keeps: the newest schema it may write (<see cref="Writes"/>)
/// and the schemas it reads, every one from <see cref="ReadsMin"/> to <see cref="ReadsMax"/>. A release reads what it
/// writes. A new schema is read by one release before another writes it ("add first, remove later"), so a rollback to
/// the release before stays a swap; a release that writes a schema the release before it does not read makes a breaking
/// change.
/// </summary>
/// <param name="Writes">The newest schema this release may write: the one it migrates the store to.</param>
/// <param name="ReadsMin">The oldest schema it reads.</param>
/// <param name="ReadsMax">The newest schema it reads.</param>
public sealed record StoreContract(int Writes, int ReadsMin, int ReadsMax)
{
    /// <summary>True when this release reads a store of <paramref name="schema"/>.</summary>
    public bool Reads(int schema) => schema >= ReadsMin && schema <= ReadsMax;

    /// <summary>"schema 1" or "schemas 1 to 3": what it reads, for a message.</summary>
    public string DescribeReads() => ReadsMin == ReadsMax ? $"schema {ReadsMin}" : $"schemas {ReadsMin} to {ReadsMax}";
}

/// <summary>
/// The controller's state contract: for each store it keeps, the schemas a release writes and reads (release.json's
/// <c>stateContracts.controller</c>, and the controller image's <c>io.hvo.roof.state-contracts</c> label, which
/// build/release-assets.py and the Dockerfile keep the same). The installer compares it with the schemas of the stores
/// here before an upgrade and a rollback, and with the contract of the release installed to tell a breaking change.
/// </summary>
public sealed record StateContract(IReadOnlyDictionary<string, StoreContract> Stores)
{
    /// <summary>The identity store: people, sessions and managed API keys (<c>identity.json</c>'s <c>schemaVersion</c>).</summary>
    public const string Identity = "identity";

    /// <summary>The settings file and the managed secrets file (<c>appsettings.Local.json</c> and <c>secrets.json</c>).</summary>
    public const string Settings = "settings";

    /// <summary>The stores this installer knows, in the order it describes them.</summary>
    public static IReadOnlyList<string> Known { get; } = [Identity, Settings];

    /// <summary>
    /// The contract of a release whose release.json has no <c>stateContracts</c> (4.1.1 and before): it reads identity
    /// schemas 1 to its <c>identityStoreSchema</c> and writes up to it, and reads and writes settings schema 1, the only
    /// one there has been.
    /// </summary>
    public static StateContract Legacy(int identityStoreSchema) => new(new Dictionary<string, StoreContract>(StringComparer.Ordinal)
    {
        [Identity] = new(identityStoreSchema, 1, identityStoreSchema),
        [Settings] = new(1, 1, 1)
    });

    /// <summary>
    /// The contract a controller image declares in its labels: its <c>io.hvo.roof.state-contracts</c> label, or, on an image
    /// from before it, the legacy one its <c>io.hvo.roof.identity-schema</c> label gives (1 without either). Null when a
    /// label is there but cannot be read.
    /// </summary>
    public static StateContract? FromLabels(string? contracts, string? identitySchema)
    {
        if (contracts is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(contracts);
                return Parse(document.RootElement, reason => new InstallerException(reason));
            }
            catch (Exception error) when (error is JsonException or InstallerException)
            {
                return null;
            }
        }

        if (identitySchema is null)
        {
            return Legacy(1);
        }

        return int.TryParse(identitySchema, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var schema) && schema >= 1
            ? Legacy(schema)
            : null;
    }

    /// <summary>The contract for <paramref name="store"/>, or null when this one declares none.</summary>
    public StoreContract? For(string store) => Stores.GetValueOrDefault(store);

    /// <summary>
    /// The stores whose schema this release may write that <paramref name="installed"/> does not read: what makes an
    /// upgrade from it a breaking change. Empty when a rollback to it stays a swap whatever this release writes.
    /// </summary>
    public IReadOnlyList<(string Store, StoreContract Target, StoreContract Installed)> BreakingFrom(StateContract installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        return [.. Known.Where(store => For(store) is not null && installed.For(store) is not null && For(store)!.Writes > installed.For(store)!.ReadsMax)
            .Select(store => (store, For(store)!, installed.For(store)!))];
    }

    /// <summary>A store's name in a message: "the identity store", "the settings files".</summary>
    public static string Describe(string store) => store switch
    {
        Identity => "the identity store",
        Settings => "the settings files",
        _ => $"the {store} store"
    };

    /// <summary>
    /// Reads one component's contract, as release.json and the image label give it: an object of stores, each with
    /// <c>writes</c> and <c>reads.min</c>/<c>reads.max</c>, whole numbers from 1 with min ≤ writes ≤ max. Throws
    /// <see cref="InstallerException"/> saying what is wrong, through <paramref name="invalid"/>.
    /// </summary>
    internal static StateContract Parse(JsonElement contract, Func<string, InstallerException> invalid)
    {
        if (contract.ValueKind != JsonValueKind.Object)
        {
            throw invalid("the controller's state contract is not a JSON object");
        }

        var stores = new Dictionary<string, StoreContract>(StringComparer.Ordinal);
        foreach (var store in contract.EnumerateObject())
        {
            if (stores.ContainsKey(store.Name))
            {
                throw invalid($"the controller's state contract names the {store.Name} store twice");
            }

            if (store.Value.ValueKind != JsonValueKind.Object
                || Number(store.Value, "writes") is not { } writes
                || !store.Value.TryGetProperty("reads", out var reads) || reads.ValueKind != JsonValueKind.Object
                || Number(reads, "min") is not { } min || Number(reads, "max") is not { } max
                || min > writes || writes > max)
            {
                throw invalid($"the {store.Name} store's state contract is not writes and reads (min and max) with min ≤ writes ≤ max, each a whole number from 1");
            }

            stores[store.Name] = new StoreContract(writes, min, max);
        }

        return new StateContract(stores);
    }

    private static int? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 1 ? number : null;
}
