using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using NUnit.Framework;
using WAR2D.EditorTools;

/// <summary>
/// The approvals record (asset-generation spec §3.6, <c>docs/art/asset-approvals.json</c>, kept with
/// <c>tools/asset-approvals.py</c>): an asset the owner has approved must still be built to the hash they approved.
/// A rebuild that changes an approved asset fails here until the owner approves the new one and it is recorded.
/// Assets with no record are listed by <c>tools/asset-approvals.py --pending</c>, not failed.
/// </summary>
public class AssetApprovalTests
{
    private const string FilePath = "docs/art/asset-approvals.json";

    private sealed class Approval
    {
        public string hash, date, page;
    }

    [Test]
    public void EveryApprovedAssetIsBuiltAsApproved()
    {
        var approvals = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, Approval>>>(File.ReadAllText(FilePath));
        AssetHashes built = AssetHashes.Load();
        var tables = new Dictionary<string, SortedDictionary<string, string>> { ["sprites"] = built.sprites, ["sounds"] = built.sounds, ["tracks"] = built.tracks };
        var problems = new List<string>();
        foreach (var kind in approvals)
        {
            Assert.IsTrue(tables.ContainsKey(kind.Key), $"unknown kind '{kind.Key}' in {FilePath}");
            foreach (var row in kind.Value)
            {
                if (!tables[kind.Key].TryGetValue(row.Key, out string hash)) problems.Add($"{kind.Key} {row.Key}: approved but no longer built");
                else if (hash != row.Value.hash) problems.Add($"{kind.Key} {row.Key}: built differently from what was approved on {row.Value.date}");
                Assert.IsNotEmpty(row.Value.date, row.Key);
                Assert.IsNotEmpty(row.Value.page, row.Key);
            }
        }
        Assert.IsEmpty(problems, string.Join("\n", problems));
    }
}
