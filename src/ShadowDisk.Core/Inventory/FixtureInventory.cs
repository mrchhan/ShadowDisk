using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShadowDisk.Core.Model;

namespace ShadowDisk.Core.Inventory;

/// <summary>
/// Simulated disks loaded from JSON fixtures. Used for demo mode and for every test.
/// Nothing here touches real hardware.
/// </summary>
public sealed class FixtureInventory : IStorageInventory
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IReadOnlyList<PhysicalDisk> _disks;

    public FixtureInventory(IReadOnlyList<PhysicalDisk> disks, string description = "Demo disks (simulated)")
    {
        _disks = disks;
        SourceDescription = description;
    }

    public bool IsSimulated => true;
    public string SourceDescription { get; }

    public Task<InventorySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new InventorySnapshot(DateTimeOffset.Now, true, SourceDescription, _disks, []));
    }

    public static IReadOnlyList<string> BuiltInFixtureNames() =>
        typeof(FixtureInventory).Assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Split('.')[^2])
            .Order()
            .ToList();

    public static IReadOnlyList<PhysicalDisk> LoadBuiltIn(string name)
    {
        var asm = typeof(FixtureInventory).Assembly;
        var resource = asm.GetManifestResourceNames()
            .SingleOrDefault(n => n.EndsWith("." + name + ".json", StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"No built-in fixture named '{name}'.", nameof(name));
        using var stream = asm.GetManifestResourceStream(resource)!;
        return Parse(stream);
    }

    /// <summary>All built-in fixtures combined, renumbered so each simulated disk has a unique number.</summary>
    public static FixtureInventory DemoAll()
    {
        string[] preferred = ["laptop-nvme-gpt", "desktop-hdd-mbr", "usb-stick-fat32", "new-disk-uninitialized", "failing-hdd", "dynamic-disk"];
        var names = BuiltInFixtureNames();
        var ordered = preferred.Where(names.Contains).Concat(names.Except(preferred));
        var disks = new List<PhysicalDisk>();
        foreach (var name in ordered)
        {
            foreach (var d in LoadBuiltIn(name))
            {
                var number = disks.Count;
                disks.Add(d with
                {
                    Number = number,
                    Partitions = d.Partitions.Select(p => p with { DiskNumber = number }).ToList(),
                });
            }
        }
        return new FixtureInventory(disks);
    }

    public static IReadOnlyList<PhysicalDisk> Parse(Stream json) =>
        JsonSerializer.Deserialize<List<PhysicalDisk>>(json, JsonOptions)
        ?? throw new InvalidDataException("Fixture file is empty.");
}
