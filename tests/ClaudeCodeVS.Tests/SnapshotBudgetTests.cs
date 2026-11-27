using System.Linq;
using ClaudeCodeVs.Edits;
using ClaudeCodeVs.Ui;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeVs.Tests;

/// <summary>The "Show diff" snapshots stay within their memory budget, newest kept, oldest dropped.</summary>
[TestClass]
public class SnapshotBudgetTests
{
    private long _savedBudget;

    [TestInitialize]
    public void Setup()
    {
        _savedBudget = BridgeStatus.SnapshotBudgetBytes;
        BridgeStatus.ClearEdits();
    }

    [TestCleanup]
    public void Cleanup()
    {
        BridgeStatus.SnapshotBudgetBytes = _savedBudget;
        BridgeStatus.ClearEdits();
    }

    private static EditEntry Edit(string file, int chars) => new()
    {
        FilePath = file,
        FileName = file,
        Added = 1,
        Snapshot = new EditSnapshot(new string('a', chars), new string('b', chars)),
    };

    [TestMethod]
    public void OldestSnapshotsAreDropped_NewestKept()
    {
        // Each edit costs 2 * (1000 + 1000) = 4000 bytes; room for two.
        BridgeStatus.SnapshotBudgetBytes = 9000;
        var first = Edit("a.cs", 1000);
        var second = Edit("b.cs", 1000);
        var third = Edit("c.cs", 1000);

        BridgeStatus.RecordEdits(new[] { first });
        BridgeStatus.RecordEdits(new[] { second });
        BridgeStatus.RecordEdits(new[] { third });

        Assert.IsNull(first.Snapshot, "oldest should be evicted");
        Assert.IsNotNull(second.Snapshot);
        Assert.IsNotNull(third.Snapshot);
    }

    [TestMethod]
    public void RowsSharingOneSnapshot_AreCountedOnce()
    {
        BridgeStatus.SnapshotBudgetBytes = 5000;
        var snapshot = new EditSnapshot(new string('a', 1000), new string('b', 1000)); // 4000 bytes
        var rows = Enumerable.Range(0, 3).Select(i => new EditEntry { FilePath = "x.cs", FileName = "x.cs", StartLine = i, Snapshot = snapshot }).ToArray();

        BridgeStatus.RecordEdits(rows);

        Assert.IsTrue(rows.All(r => ReferenceEquals(r.Snapshot, snapshot)));
    }
}
