using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClaudeCodeVs.Edits;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ClaudeCodeVs.Tests;

[TestClass]
public class EditTreeTests
{
    private static EditEntry Row(string path, int added = 1) =>
        new() { FilePath = path, FileName = Path.GetFileName(path), Added = added };

    [TestMethod]
    public void WorkspaceFiles_HangWithoutAnExtraTopNode()
    {
        var tree = EditTree.Build(new List<EditEntry> { Row(@"C:\ws\CLAUDE.md"), Row(@"C:\ws\src\A.cs") }, @"C:\ws");

        Assert.AreEqual(1, tree.Files.Count);
        Assert.AreEqual("CLAUDE.md", tree.Files[0][0].FileName);
        Assert.AreEqual("src", tree.Folders.Single().Name);
    }

    [TestMethod]
    public void SingleChildFolderChains_AreCompacted_ButBranchesAreKept()
    {
        var tree = EditTree.Build(new List<EditEntry>
        {
            Row(@"C:\ws\src\Proj\Ui\A.cs"),
            Row(@"C:\ws\src\Proj\Ui\B.cs"),
            Row(@"C:\ws\src\Proj\Res\S.resx"),
            Row(@"C:\ws\deep\one\two\three\X.cs"),
        }, @"C:\ws");

        var names = tree.Folders.Select(f => f.Name).ToList();
        CollectionAssert.AreEqual(new[] { "deep/one/two/three", "src/Proj" }, names);
        var proj = tree.Folders.Single(f => f.Name == "src/Proj");
        CollectionAssert.AreEqual(new[] { "Res", "Ui" }, proj.Folders.Select(f => f.Name).ToList());
        Assert.AreEqual(@"C:\ws\src\Proj", proj.FullPath);
    }

    [TestMethod]
    public void RowsOfOneFile_StayTogether_InRecordedOrder()
    {
        var first = Row(@"C:\ws\A.cs", 1);
        var second = Row(@"C:\ws\A.cs", 2);
        var tree = EditTree.Build(new List<EditEntry> { first, second }, @"C:\ws");

        var file = tree.Files.Single();
        Assert.AreSame(first, file[0]);
        Assert.AreSame(second, file[1]);
        Assert.AreEqual(3, tree.AllRows().Sum(r => r.Added));
    }

    [TestMethod]
    public void FileOutsideWorkspace_HangsUnderItsRepositoryName()
    {
        using var temp = new TempDir();
        var repo = temp.Sub("OtherRepo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var file = Path.Combine(temp.Sub("OtherRepo", "src", "lib"), "X.cs");

        var tree = EditTree.Build(new List<EditEntry> { Row(file) }, @"C:\definitely\not\here");

        Assert.AreEqual("OtherRepo/src/lib", tree.Folders.Single().Name);
    }

    [TestMethod]
    public void FileOutsideAnyRepository_HangsUnderItsFullDirectory()
    {
        using var temp = new TempDir();
        var dir = temp.Sub("loose");
        var tree = EditTree.Build(new List<EditEntry> { Row(Path.Combine(dir, "n.txt")) }, @"C:\definitely\not\here");

        Assert.AreEqual(dir, tree.Folders.Single().Name);
    }

    [DataTestMethod]
    [DataRow("src/Ui", 40, "src/Ui")]
    [DataRow("src/ClaudeCodeVS.Protocol/VeryLongFolderName/Deep", 30, "…/VeryLongFolderName/Deep")]
    [DataRow("a/b/AnExtremelyLongLastSegmentThatAloneIsTooLong", 10, "…/AnExtremelyLongLastSegmentThatAloneIsTooLong")]
    [DataRow(@"C:\Temp\scratch\very\long\path\here", 20, @"…\long\path\here")]
    public void Shorten_KeepsTheDeepestSegments(string input, int max, string expected)
    {
        Assert.AreEqual(expected, EditTree.Shorten(input, max));
    }
}
