using System.Net;
using NetBootDhcpTool.Core;

namespace NetBootDhcpTool.Network;

/// <summary>Network mutation runs under the caller's workflow lease. The journal is committed before each write.</summary>
public sealed class AdapterAddressManager
{
    private readonly IAdapterAddressBackend _backend;
    private readonly Action<AdapterAddressJournal> _save;
    public AdapterAddressJournal Journal { get; }
    public AdapterAddressManager(IAdapterAddressBackend backend, AdapterAddressJournal journal, Action<AdapterAddressJournal> save)
    {
        _backend = backend; _save = save; Journal = journal;
        if (journal.SchemaVersion != 1 || journal.Addresses is null || journal.Coexistence is null)
            throw new InvalidDataException("Invalid address journal / 地址恢复日志无效");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in journal.Addresses)
        {
            if (entry is null || entry.PolicyStore != "ActiveStore" || entry.SkipAsSource || entry.PrefixLength is < 1 or > 32
                || entry.State is not ("Intent" or "Applied" or "Removing") || !keys.Add(entry.AdapterId + "|" + entry.IpAddress))
                throw new InvalidDataException("Invalid address ownership / 地址归属记录无效");
            AdapterAddressPlan.Validate([new() { AdapterId = entry.AdapterId, IpAddress = entry.IpAddress,
                SubnetMask = IpNetwork.FromUInt32(uint.MaxValue << (32 - entry.PrefixLength)).ToString() }]);
        }
        if (journal.Coexistence.Any(x => x is null || string.IsNullOrWhiteSpace(x.AdapterId) || x.State is not ("Intent" or "Applied"))
            || journal.Coexistence.Select(x => x.AdapterId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Coexistence.Count)
            throw new InvalidDataException("Invalid coexistence journal / 共存恢复日志无效");
    }

    public async Task ApplyAsync(IEnumerable<AdapterAddressDraft> drafts, CancellationToken ct)
    {
        var plan = AdapterAddressPlan.Validate(drafts);
        var created = new List<OwnedAdapterAddress>();
        try
        {
            foreach (var group in plan.GroupBy(x => x.AdapterId, StringComparer.OrdinalIgnoreCase))
            {
                if (Journal.Addresses.Any(x => x.AdapterId.Equals(group.Key, StringComparison.OrdinalIgnoreCase) && x.State != "Applied")
                    || Journal.Coexistence.Any(x => x.AdapterId.Equals(group.Key, StringComparison.OrdinalIgnoreCase) && x.State != "Applied"))
                    throw new InvalidOperationException("Resolve pending address recovery first / 请先处理待恢复地址");
                var adapter = _backend.Resolve(group.Key);
                var before = await _backend.CaptureAsync(adapter, ct);
                if (before.DnsMode == AdapterDnsMode.Unknown) throw new InvalidOperationException("DNS source unknown; no write allowed / DNS 来源未知，禁止写入");
                var observed = await _backend.ReadAsync(adapter, ct);
                foreach (var draft in group)
                {
                    var prefix = IpNetwork.PrefixLength(IPAddress.Parse(draft.SubnetMask));
                    var existing = observed.Where(x => x.IpAddress == draft.IpAddress).ToArray();
                    if (existing.Length > 0)
                    {
                        if (existing.Length != 1 || existing[0].PrefixLength != prefix || existing[0].AddressState != "Preferred")
                            throw new InvalidOperationException("Existing address has conflicting properties / 已有地址属性冲突或不可用");
                        continue; // Never acquire ownership of a pre-existing address.
                    }
                    var entry = new OwnedAdapterAddress { AdapterId = adapter.Id, AdapterName = adapter.Name, IpAddress = draft.IpAddress, PrefixLength = prefix };
                    await _backend.ValidateAppendAsync(_backend.Resolve(adapter.Id), entry, ct);
                    if (before.DhcpEnabled) await EnsureCoexistenceAsync(adapter, ct);
                    Journal.Addresses.Add(entry); _save(Journal);
                    await _backend.AddAsync(_backend.Resolve(adapter.Id), entry, ct);
                    entry.State = "Applied"; _save(Journal); created.Add(entry);
                    await WaitForAddressAsync(adapter, entry, ct);
                }
                var after = await _backend.CaptureAsync(_backend.Resolve(adapter.Id), ct);
                if (!PreservesConfiguration(before, after, created.Where(x => x.AdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase))))
                    throw new InvalidOperationException("Business configuration changed; recovery retained / 业务配置发生变化，保留恢复记录");
            }
        }
        catch
        {
            using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            foreach (var entry in created.AsEnumerable().Reverse())
            {
                try { await RemoveAsync(entry, recovery.Token); }
                catch (Exception ex) { entry.Error = ex.Message; _save(Journal); }
            }
            throw;
        }
    }

    private async Task EnsureCoexistenceAsync(NetworkAdapterInfo adapter, CancellationToken ct)
    {
        var lease = Journal.Coexistence.SingleOrDefault(x => x.AdapterId.Equals(adapter.Id, StringComparison.OrdinalIgnoreCase));
        if (lease is not null)
        {
            if (!await _backend.ReadCoexistenceAsync(adapter, ct)) throw new InvalidOperationException("Coexistence changed externally / 共存设置被外部修改");
            return;
        }
        var enabled = await _backend.ReadCoexistenceAsync(adapter, ct);
        if (enabled) return;
        lease = new AdapterCoexistenceLease { AdapterId = adapter.Id, OriginalEnabled = false };
        Journal.Coexistence.Add(lease); _save(Journal);
        await _backend.SetCoexistenceAsync(_backend.Resolve(adapter.Id), true, ct);
        if (!await _backend.ReadCoexistenceAsync(adapter, ct)) throw new InvalidOperationException("Coexistence verification failed / 共存状态验证失败");
        lease.State = "Applied"; _save(Journal);
    }

    private async Task WaitForAddressAsync(NetworkAdapterInfo adapter, OwnedAdapterAddress entry, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var item = (await _backend.ReadAsync(_backend.Resolve(adapter.Id), ct)).SingleOrDefault(x => x.IpAddress == entry.IpAddress);
            if (item is not null && Matches(entry, item) && item.AddressState == "Preferred") return;
            if (item?.AddressState == "Duplicate" || item is not null && !Matches(entry, item))
                throw new InvalidOperationException("Duplicate address or changed properties / 地址冲突或属性发生变化");
            await Task.Delay(250, ct);
        }
        throw new TimeoutException("Address not usable after DAD / 地址检测后仍不可用");
    }

    public async Task RemoveAsync(OwnedAdapterAddress entry, CancellationToken ct, bool confirmUncertain = false)
    {
        if (!Journal.Addresses.Contains(entry)) return;
        if (entry.State == "Intent" && !confirmUncertain)
            throw new InvalidOperationException("Uncertain write: inspect and confirm recovery / 写入结果不确定，请检查后确认恢复");
        var adapter = _backend.Resolve(entry.AdapterId);
        var before = await _backend.CaptureAsync(adapter, ct);
        var items = (await _backend.ReadAsync(adapter, ct)).Where(x => x.IpAddress == entry.IpAddress).ToArray();
        if (items.Length > 1 || items.Length == 1 && !Matches(entry, items[0]))
            throw new InvalidOperationException("Address ownership changed; not removed / 地址归属变化，未删除");
        entry.State = "Removing"; _save(Journal);
        await _backend.RemoveAsync(_backend.Resolve(entry.AdapterId), entry, ct);
        var after = await _backend.CaptureAsync(_backend.Resolve(entry.AdapterId), ct);
        var expected = before.Addresses.Where(x => x.IpAddress != entry.IpAddress).ToList();
        var remaining = after.Addresses;
        before.Addresses = expected;
        before.IpAddress = expected.FirstOrDefault()?.IpAddress ?? "";
        if (!AdapterIpv4SnapshotComparer.Equivalent(before, after) || remaining.Any(x => x.IpAddress == entry.IpAddress))
            throw new InvalidOperationException("Address cleanup verification failed / 地址清理验证失败");
        Journal.Addresses.Remove(entry); _save(Journal);
        await RestoreCoexistenceAsync(adapter.Id, ct);
    }

    public async Task RestoreCoexistenceAsync(string adapterId, CancellationToken ct, bool confirmUncertain = false)
    {
        if (Journal.Addresses.Any(x => x.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase))) return;
        var lease = Journal.Coexistence.SingleOrDefault(x => x.AdapterId.Equals(adapterId, StringComparison.OrdinalIgnoreCase));
        if (lease is null) return;
        if (lease.State == "Intent" && !confirmUncertain) throw new InvalidOperationException("Confirm uncertain coexistence recovery / 请确认结果不确定的共存恢复");
        var adapter = _backend.Resolve(adapterId);
        // Turning coexistence off with externally added static addresses could disrupt them. Leave it pending.
        if ((await _backend.ReadAsync(adapter, ct)).Any(x => x.PrefixOrigin == "Manual"))
            throw new InvalidOperationException("Other static addresses exist; coexistence retained / 存在其他固定地址，保留共存状态");
        var before = await _backend.CaptureAsync(adapter, ct);
        if (await _backend.ReadCoexistenceAsync(adapter, ct) != lease.OriginalEnabled)
            await _backend.SetCoexistenceAsync(adapter, lease.OriginalEnabled, ct);
        if (await _backend.ReadCoexistenceAsync(adapter, ct) != lease.OriginalEnabled
            || !AdapterIpv4SnapshotComparer.Equivalent(before, await _backend.CaptureAsync(adapter, ct)))
            throw new InvalidOperationException("Coexistence restore verification failed / 共存恢复验证失败");
        Journal.Coexistence.Remove(lease); _save(Journal);
    }

    public static bool Matches(OwnedAdapterAddress entry, AddressObservation item) => entry.IpAddress == item.IpAddress
        && entry.PrefixLength == item.PrefixLength && entry.SkipAsSource == item.SkipAsSource && !item.Persistent && item.PrefixOrigin == "Manual";

    public static bool PreservesConfiguration(AdapterIpv4Snapshot before, AdapterIpv4Snapshot after, IEnumerable<OwnedAdapterAddress> added)
    {
        var keys = added.Select(x => x.IpAddress).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copy = System.Text.Json.JsonSerializer.Deserialize<AdapterIpv4Snapshot>(System.Text.Json.JsonSerializer.Serialize(after))!;
        copy.Addresses.RemoveAll(x => keys.Contains(x.IpAddress));
        copy.IpAddress = copy.Addresses.FirstOrDefault()?.IpAddress ?? "";
        return AdapterIpv4SnapshotComparer.Equivalent(before, copy)
            && before.Dns.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(copy.Dns.Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    }
}
